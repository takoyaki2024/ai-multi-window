using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AiMultiWindow;

public enum AgentRole { Manager = 0, Planner = 1, Coder = 2, Reviewer = 3 }
public enum WorkflowState { Idle, Running, Success, Stopped }

public sealed class OrchestrationEngine
{
    public string TaskText { get; set; } = string.Empty;
    public WorkflowState State { get; set; } = WorkflowState.Idle;
    public AgentRole CurrentRole { get; set; } = AgentRole.Manager;
    public int AiCalls { get; set; }
    public int SendAttempts { get; set; }
    public int FixAttempts { get; set; }
    public int MaxAiCalls { get; set; } = 10;
    public int MaxSendAttempts { get; set; } = 16;
    public int MaxFixAttempts { get; set; } = 3;
    public int MaxCoderSteps { get; set; } = 5;
    public int DuplicateLimit { get; set; } = 2;
    public string StopReason { get; set; } = string.Empty;
    public Dictionary<AgentRole, string> Answers { get; set; } = new();
    public string LastAnswerHash { get; set; } = string.Empty;
    public int DuplicateCount { get; set; }
    public string ExecutionResult { get; set; } = string.Empty;
    public bool LastExecutionSucceeded { get; set; }
    public string WorkspaceContext { get; set; } = string.Empty;
    public string CoderWorkspaceContext { get; set; } = string.Empty;
    public List<string> ImplementationSteps { get; set; } = new();
    public int CoderStepIndex { get; set; }
    public List<string> CoderStepAnswers { get; set; } = new();
    public List<string> SuccessfulExecutionResults { get; set; } = new();
    public bool AwaitingResponse { get; set; }
    public AgentRole AwaitingRole { get; set; } = AgentRole.Manager;
    public int AwaitingCoderStepIndex { get; set; }
    public string PreviousFailedCoderAttempt { get; set; } = string.Empty;
    public string PreviousPatchSearchHash { get; set; } = string.Empty;
    public string RetryStrategy { get; set; } = string.Empty;
    public int CoderRepairAttempt { get; set; }

    [JsonIgnore]
    public int CoderStepNumber => ImplementationSteps.Count == 0 ? 1 : Math.Min(CoderStepIndex + 1, ImplementationSteps.Count);

    [JsonIgnore]
    public int CoderStepCount => Math.Max(1, ImplementationSteps.Count);

    [JsonIgnore]
    public string CurrentImplementationStep => ImplementationSteps.Count == 0
        ? GetAnswer(AgentRole.Planner)
        : ImplementationSteps[Math.Clamp(CoderStepIndex, 0, ImplementationSteps.Count - 1)];

    private static string CheckpointDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AiMultiWindow");
    private static string CheckpointPath => Path.Combine(CheckpointDirectory, "orchestrator-checkpoint.json");

    public static OrchestrationEngine Load()
    {
        try
        {
            if (!File.Exists(CheckpointPath)) return new OrchestrationEngine();
            return JsonSerializer.Deserialize<OrchestrationEngine>(File.ReadAllText(CheckpointPath)) ?? new OrchestrationEngine();
        }
        catch { return new OrchestrationEngine(); }
    }

    public void Start(string task, string workspaceContext = "")
    {
        TaskText = task.Trim(); State = WorkflowState.Running; CurrentRole = AgentRole.Manager;
        AiCalls = 0; SendAttempts = 0; FixAttempts = 0; StopReason = string.Empty; Answers.Clear();
        LastAnswerHash = string.Empty; DuplicateCount = 0; ExecutionResult = string.Empty; LastExecutionSucceeded = false;
        WorkspaceContext = workspaceContext; CoderWorkspaceContext = string.Empty;
        ImplementationSteps.Clear(); CoderStepIndex = 0; CoderStepAnswers.Clear(); SuccessfulExecutionResults.Clear();
        AwaitingResponse = false; AwaitingRole = AgentRole.Manager; AwaitingCoderStepIndex = 0;
        PreviousFailedCoderAttempt = string.Empty; PreviousPatchSearchHash = string.Empty; RetryStrategy = string.Empty; CoderRepairAttempt = 0;
        Save();
        WorkflowDiagnostics.Event("1 Manager", "workflow", "WORKFLOW_STARTED", $"taskLength={TaskText.Length}; workspaceContextLength={WorkspaceContext.Length}");
        WorkflowDiagnostics.Snapshot(this, "WORKFLOW_STARTED");
    }

    public void Reset()
    {
        TaskText = string.Empty; State = WorkflowState.Idle; CurrentRole = AgentRole.Manager;
        AiCalls = 0; SendAttempts = 0; FixAttempts = 0; StopReason = string.Empty; Answers.Clear();
        LastAnswerHash = string.Empty; DuplicateCount = 0; ExecutionResult = string.Empty; LastExecutionSucceeded = false;
        WorkspaceContext = string.Empty; CoderWorkspaceContext = string.Empty;
        ImplementationSteps.Clear(); CoderStepIndex = 0; CoderStepAnswers.Clear(); SuccessfulExecutionResults.Clear();
        AwaitingResponse = false; AwaitingRole = AgentRole.Manager; AwaitingCoderStepIndex = 0;
        PreviousFailedCoderAttempt = string.Empty; PreviousPatchSearchHash = string.Empty; RetryStrategy = string.Empty; CoderRepairAttempt = 0;
        Save();
        WorkflowDiagnostics.Event("System", "workflow", "WORKFLOW_RESET");
        WorkflowDiagnostics.Snapshot(this, "WORKFLOW_RESET");
    }

    public void Stop(string reason)
    {
        State = WorkflowState.Stopped; StopReason = reason; Save();
        WorkflowDiagnostics.Event(RoleLabel(CurrentRole), "workflow", "WORKFLOW_STOPPED", reason);
        WorkflowDiagnostics.Snapshot(this, "WORKFLOW_STOPPED", reason);
    }

    public void SetExecutionResult(string value) { ExecutionResult = value; Save(); }

    public void SetExecutionResult(string value, bool success)
    {
        ExecutionResult = value;
        LastExecutionSucceeded = success;
        if (success)
        {
            while (SuccessfulExecutionResults.Count <= CoderStepIndex) SuccessfulExecutionResults.Add(string.Empty);
            SuccessfulExecutionResults[CoderStepIndex] = value;
        }
        Save();
        WorkflowDiagnostics.Event(RoleLabel(CurrentRole), "local-verification", success ? "LOCAL_VERIFICATION_PASS" : "LOCAL_VERIFICATION_FAIL", Tail(value, 1200));
        if (!success) WorkflowDiagnostics.Snapshot(this, "LOCAL_VERIFICATION_FAIL", Tail(value, 3500));
    }

    public void SetCoderWorkspaceContext(string value) { CoderWorkspaceContext = value; Save(); }

    public bool TryBeginPromptAttempt()
    {
        if (State != WorkflowState.Running) return false;
        if (AwaitingResponse) return false;
        if (AiCalls >= MaxAiCalls) { Stop("AI呼び出し上限に達しました"); return false; }
        if (SendAttempts >= MaxSendAttempts) { Stop("送信試行回数の上限に達しました"); return false; }
        SendAttempts++;
        WorkflowDiagnostics.Event(RoleLabel(CurrentRole), "send", "PROMPT_ATTEMPT", $"sendAttempt={SendAttempts}; aiCalls={AiCalls}; fixAttempts={FixAttempts}; coderStep={CoderStepNumber}/{CoderStepCount}");
        Save(); return true;
    }

    public void RecordPromptAccepted()
    {
        AiCalls++;
        AwaitingResponse = true;
        AwaitingRole = CurrentRole;
        AwaitingCoderStepIndex = CoderStepIndex;
        WorkflowDiagnostics.Event(RoleLabel(CurrentRole), "send", "PROMPT_ACCEPTED", $"aiCalls={AiCalls}; sendAttempts={SendAttempts}; coderStep={CoderStepNumber}/{CoderStepCount}");
        Save();
    }

    public bool RecordAnswer(string answer)
    {
        if (State != WorkflowState.Running || string.IsNullOrWhiteSpace(answer))
        {
            WorkflowDiagnostics.Event(RoleLabel(CurrentRole), "answer", "ANSWER_REJECTED_EMPTY_OR_NOT_RUNNING", $"state={State}; answerLength={answer?.Length ?? 0}");
            WorkflowDiagnostics.Snapshot(this, "ANSWER_REJECTED_EMPTY_OR_NOT_RUNNING");
            return false;
        }

        var roleBefore = CurrentRole;
        var normalized = answer.Trim();
        if (AwaitingResponse && (AwaitingRole != CurrentRole || AwaitingCoderStepIndex != CoderStepIndex))
        {
            Stop("送信待ち状態と現在工程が一致しません");
            return false;
        }
        WorkflowDiagnostics.Event(RoleLabel(roleBefore), "answer", "ANSWER_RECEIVED", $"length={normalized.Length}; lastExecutionSucceeded={LastExecutionSucceeded}; coderStep={CoderStepNumber}/{CoderStepCount}");

        var markerValid = AnswerMatchesCurrentRole(normalized);
        if (!markerValid)
        {
            WorkflowDiagnostics.Event(RoleLabel(roleBefore), "answer", "ANSWER_FORMAT_MISMATCH", $"answerLength={normalized.Length}");
            WorkflowDiagnostics.Snapshot(this, "ANSWER_FORMAT_MISMATCH", $"role={roleBefore}; answerLength={normalized.Length}");
            Stop($"{CurrentRole} の回答形式を確認できませんでした");
            return false;
        }

        var answerIdentity = $"{roleBefore}:{CoderStepIndex}:{normalized}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(answerIdentity)));
        var duplicateFailedCoderRepair = roleBefore == AgentRole.Coder
            && !LastExecutionSucceeded
            && hash == LastAnswerHash;
        DuplicateCount = hash == LastAnswerHash ? DuplicateCount + 1 : 0;
        LastAnswerHash = hash;
        if (duplicateFailedCoderRepair)
        {
            RetryStrategy = "ESCALATE_TO_MODIFY_IF_COMPLETE_FILE";
            WorkflowDiagnostics.Event("3 Coder", "repair", "CODER_DUPLICATE_REPAIR_ESCALATION",
                $"duplicateCount={DuplicateCount}; nextRepairAttempt={FixAttempts + 1}/{MaxFixAttempts}; previousPatchSearchHash={PreviousPatchSearchHash}; retryStrategy={RetryStrategy}");
            WorkflowDiagnostics.Snapshot(this, "CODER_DUPLICATE_REPAIR_ESCALATION", "同じ失敗回答を停止理由にせず、次のrepair戦略へ昇格します。");
        }
        else if (DuplicateCount >= DuplicateLimit)
        {
            WorkflowDiagnostics.Event(RoleLabel(roleBefore), "answer", "DUPLICATE_ANSWER_BLOCKED", $"duplicateCount={DuplicateCount}");
            WorkflowDiagnostics.Snapshot(this, "DUPLICATE_ANSWER_BLOCKED");
            Stop("同一回答が繰り返されたため停止しました");
            return false;
        }
        Answers[CurrentRole] = normalized;
        AwaitingResponse = false;

        switch (CurrentRole)
        {
            case AgentRole.Manager:
                CurrentRole = AgentRole.Planner;
                WorkflowDiagnostics.Event("1 Manager", "transition", "MANAGER_TO_PLANNER");
                break;

            case AgentRole.Planner:
                ParseImplementationSteps(normalized);
                CurrentRole = AgentRole.Coder;
                WorkflowDiagnostics.Event("2 Planner", "transition", "PLANNER_TO_CODER", $"steps={CoderStepCount}");
                break;

            case AgentRole.Coder:
                if (!LastExecutionSucceeded)
                {
                    FixAttempts++;
                    CoderRepairAttempt = FixAttempts;
                    PreviousFailedCoderAttempt = normalized;
                    PreviousPatchSearchHash = BuildPatchSearchHash(normalized);
                    if (IsNoSafeMatchFailure(ExecutionResult) && string.IsNullOrWhiteSpace(RetryStrategy))
                        RetryStrategy = "RELOAD_COMPLETE_FILE_THEN_SHORT_UNIQUE_SEARCH";
                    WorkflowDiagnostics.Event("3 Coder", "transition", "CODER_RETRY_LOCAL_VERIFICATION_FAILED", $"fixAttempt={FixAttempts}/{MaxFixAttempts}; coderStep={CoderStepNumber}/{CoderStepCount}");
                    if (IsNoSafeMatchFailure(ExecutionResult))
                        WorkflowDiagnostics.Event("3 Coder", "repair", "NO_SAFE_MATCH_REPAIR",
                            $"PREVIOUS_PATCH_SEARCH_HASH={PreviousPatchSearchHash}; RETRY_STRATEGY={RetryStrategy}; CODER_REPAIR_ATTEMPT={CoderRepairAttempt}");
                    WorkflowDiagnostics.Snapshot(this, "CODER_RETRY_LOCAL_VERIFICATION_FAILED", Tail(ExecutionResult, 3500));
                    if (FixAttempts >= MaxFixAttempts)
                    {
                        Stop("ローカルbuild/test失敗の修正回数上限に達しました");
                        return false;
                    }
                    CurrentRole = AgentRole.Coder;
                }
                else
                {
                    PreviousFailedCoderAttempt = string.Empty;
                    PreviousPatchSearchHash = string.Empty;
                    RetryStrategy = string.Empty;
                    CoderRepairAttempt = 0;
                    while (CoderStepAnswers.Count <= CoderStepIndex) CoderStepAnswers.Add(string.Empty);
                    CoderStepAnswers[CoderStepIndex] = normalized;
                    if (CoderStepIndex + 1 < CoderStepCount)
                    {
                        CoderStepIndex++;
                        CurrentRole = AgentRole.Coder;
                        WorkflowDiagnostics.Event("3 Coder", "transition", "CODER_STEP_TO_NEXT", $"nextStep={CoderStepNumber}/{CoderStepCount}; completedAnswers={CoderStepAnswers.Count}");
                        WorkflowDiagnostics.Snapshot(this, "CODER_STEP_TO_NEXT", $"Verified previous step was committed locally. Next Coder step {CoderStepNumber}/{CoderStepCount}.");
                    }
                    else
                    {
                        CurrentRole = AgentRole.Reviewer;
                        WorkflowDiagnostics.Event("3 Coder", "transition", "CODER_TO_REVIEWER", $"steps={CoderStepAnswers.Count}; localVerification=PASS");
                        WorkflowDiagnostics.Snapshot(this, "CODER_TO_REVIEWER", "All Coder implementation steps passed local verification. Reviewer should be the next role.");
                    }
                }
                break;

            case AgentRole.Reviewer:
                if (ReviewerVerdict(normalized) == ReviewVerdict.Pass && !LastExecutionSucceeded)
                {
                    WorkflowDiagnostics.Snapshot(this, "REVIEWER_PASS_BLOCKED_BY_LOCAL_FAILURE", Tail(ExecutionResult, 3500));
                    Stop("ローカル検証失敗のためReviewer PASSを受理できません");
                    return false;
                }
                if (ReviewerVerdict(normalized) == ReviewVerdict.Fail)
                {
                    FixAttempts++;
                    CoderStepIndex = Math.Max(0, CoderStepCount - 1);
                    WorkflowDiagnostics.Event("4 Reviewer", "transition", "REVIEWER_FAIL_TO_CODER", $"fixAttempt={FixAttempts}/{MaxFixAttempts}; repairStep={CoderStepNumber}/{CoderStepCount}");
                    if (FixAttempts >= MaxFixAttempts) { Stop("修正回数の上限に達しました"); return false; }
                    CurrentRole = AgentRole.Coder;
                }
                else
                {
                    State = WorkflowState.Success;
                    WorkflowDiagnostics.Event("4 Reviewer", "transition", "REVIEWER_PASS_WORKFLOW_SUCCESS");
                }
                break;
        }

        Save();
        WorkflowDiagnostics.Snapshot(this, CurrentRole == AgentRole.Reviewer && State == WorkflowState.Running ? "READY_FOR_REVIEWER" : "ANSWER_PROCESSED",
            $"previousRole={roleBefore}; currentRole={CurrentRole}; state={State}; fixAttempts={FixAttempts}; coderStep={CoderStepNumber}/{CoderStepCount}");
        return true;
    }

    public string BuildCurrentPrompt() => CurrentRole switch
    {
        AgentRole.Manager => $"""
            あなたは開発司令塔です。次の依頼を安全に実行可能な作業へ分解してください。
            仕様を勝手に増やさず、完了条件・作業順・注意点を明確にしてください。
            最後に MANAGER_DONE と書いてください。

            USER_REQUEST:
            {TaskText}
            """,

        AgentRole.Planner => $"""
            あなたはPlannerです。司令塔の結果と実際のWORKSPACE_CONTEXTを読み、実装計画を作ってください。
            大きな変更をCoderへ一度に渡してはいけません。実装を1〜5個の小さなステップへ分割してください。
            原則として1ステップは1ファイル、最大でも2ファイル・2〜4個程度の小さなPATCHで完了する規模にしてください。
            各ステップ終了時点でdotnet build/testが通る順番にしてください。
            必ず実在するファイル名を使い、コード全文は書かないでください。

            次の形式を厳守してください。
            STEP: 1
            FILES: relative/path.ext
            TASK: このステップだけで行う具体的変更
            STEP_END
            STEP: 2
            FILES: relative/path.ext
            TASK: 次の具体的変更
            STEP_END

            ステップは最大5個です。最後に PLAN_DONE と書いてください。

            USER_REQUEST:
            {TaskText}

            MANAGER_RESULT:
            {GetAnswer(AgentRole.Manager)}

            WORKSPACE_CONTEXT:
            {WorkspaceContext}
            """,

        AgentRole.Coder => $"""
            あなたはCoderです。依頼全体を一度に実装せず、CURRENT_IMPLEMENTATION_STEPだけを実装してください。
            現在は Coder Step {CoderStepNumber}/{CoderStepCount} です。未来のステップを先取りしてはいけません。
            使用可能ACTIONは CREATE / PATCH / MODIFY です。.git/bin/objやworkspace外は変更禁止です。
            既存ファイルの小規模変更ではPATCHを第一選択にしてください。原則1ファイル、最大2ファイルまでです。

            PATCH形式:
            FILE: relative/path.ext
            ACTION: PATCH
            <<<SEARCH
            WORKSPACE_CONTEXTに実在する一意な元文字列
            SEARCH
            <<<REPLACE
            置換後の文字列
            REPLACE

            CREATE/MODIFY形式:
            FILE: relative/path.ext
            ACTION: CREATE または MODIFY
            <<<CONTENT
            ファイル全文
            CONTENT

            SEARCHは推測せず実際のコンテキストからそのまま使ってください。Markdownコードフェンスや説明文をブロック内へ混ぜないでください。
            XAML/XMLの終了ルートタグより後ろへ内容を混ぜないでください。
            LOCAL_EXECUTION_FEEDBACKに失敗がある場合は最優先で修正してください。
            PREVIOUS_FAILED_CODER_ATTEMPTがある場合は、前回失敗した出力です。同じ失敗内容をそのまま再生成してはいけません。
            LOCAL_EXECUTION_FEEDBACKに PATCH_MATCH_MODE: NO_SAFE_MATCH または matches=0 がある場合、次を厳守してください。
            A. 最新のCOMPLETE FILEを再読込する。
            B. その最新内容から1〜3行程度の短い一意なSEARCHを整形せず完全コピーする。
            C. PREVIOUS_FAILED_CODER_ATTEMPT内と同じSEARCH文字列は絶対に再利用しない。
            D. PATCHで安全に表現できない場合、対象のCOMPLETE FILEが本当に先頭から末尾まで提供されている場合のみMODIFYへ切り替える。
            RETRY_STRATEGYが ESCALATE_TO_MODIFY_IF_COMPLETE_FILE の場合は、短い別SEARCHへ変更するか、完全なファイル全体が提供済みならMODIFYへ昇格してください。
            このステップだけを完了させ、最後に独立行で CODER_DONE と書いてください。

            USER_REQUEST:
            {TaskText}

            CURRENT_IMPLEMENTATION_STEP:
            {CurrentImplementationStep}

            REVIEW_FEEDBACK:
            {GetAnswer(AgentRole.Reviewer)}

            LOCAL_EXECUTION_FEEDBACK:
            {ExecutionResult}

            PREVIOUS_FAILED_CODER_ATTEMPT:
            {(string.IsNullOrWhiteSpace(PreviousFailedCoderAttempt) ? "(なし)" : PreviousFailedCoderAttempt)}

            PREVIOUS_PATCH_SEARCH_HASH:
            {(string.IsNullOrWhiteSpace(PreviousPatchSearchHash) ? "(なし)" : PreviousPatchSearchHash)}

            RETRY_STRATEGY:
            {(string.IsNullOrWhiteSpace(RetryStrategy) ? "INITIAL_PATCH" : RetryStrategy)}

            CODER_REPAIR_ATTEMPT:
            {CoderRepairAttempt}/{MaxFixAttempts}

            WORKSPACE_CONTEXT:
            {CoderWorkspaceContext}
            """,

        AgentRole.Reviewer => $"""
            あなたはReviewerです。全Coderステップの変更内容と全ローカル検証結果をまとめて厳しくレビューしてください。
            ビルド失敗、要求未達、危険な変更があれば先頭行を FAIL にしてください。
            問題なければ先頭行を PASS にしてください。FAILの場合は修正点を具体的に列挙してください。

            USER_REQUEST:
            {TaskText}

            PLAN:
            {GetAnswer(AgentRole.Planner)}

            CODER_STEP_RESULTS:
            {BuildCoderStepSummary()}

            LOCAL_EXECUTION_RESULTS:
            {BuildExecutionSummary()}
            """,
        _ => TaskText
    };

    public string GetAnswer(AgentRole role) => Answers.TryGetValue(role, out var value) ? value : "(なし)";

    public bool AnswerMatchesCurrentRole(string answer) => CurrentRole switch
    {
        AgentRole.Manager => ContainsMarker(answer, "MANAGER_DONE"),
        AgentRole.Planner => ContainsMarker(answer, "PLAN_DONE"),
        AgentRole.Coder => ContainsMarker(answer, "CODER_DONE"),
        AgentRole.Reviewer => ReviewerVerdict(answer) != ReviewVerdict.Unknown,
        _ => false
    };

    public void AbandonAwaitingResponse(string reason)
    {
        if (!AwaitingResponse) return;
        WorkflowDiagnostics.Event(RoleLabel(AwaitingRole), "answer", "PENDING_RESPONSE_ABANDONED", reason);
        AwaitingResponse = false;
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(CheckpointDirectory);
            File.WriteAllText(CheckpointPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void ParseImplementationSteps(string plan)
    {
        ImplementationSteps.Clear();
        CoderStepIndex = 0;
        var regex = new Regex(@"(?ms)^STEP:\s*(?<number>\d+)\s*\r?\n(?<body>.*?)^STEP_END\s*$", RegexOptions.CultureInvariant);
        foreach (Match match in regex.Matches(plan))
        {
            if (ImplementationSteps.Count >= MaxCoderSteps) break;
            var number = match.Groups["number"].Value.Trim();
            var body = match.Groups["body"].Value.Trim();
            ImplementationSteps.Add($"STEP: {number}{Environment.NewLine}{body}");
        }

        if (ImplementationSteps.Count == 0)
        {
            var fallback = plan.Replace("PLAN_DONE", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            ImplementationSteps.Add($"STEP: 1{Environment.NewLine}TASK: {fallback}");
            WorkflowDiagnostics.Event("2 Planner", "plan", "PLANNER_STEP_PARSE_FALLBACK", "Structured STEP blocks were not found; using one bounded Coder step.");
        }
        else
        {
            WorkflowDiagnostics.Event("2 Planner", "plan", "PLANNER_STEPS_PARSED", $"steps={ImplementationSteps.Count}");
        }
    }

    private string BuildCoderStepSummary()
    {
        if (CoderStepAnswers.Count == 0) return "(なし)";
        var builder = new StringBuilder();
        for (var i = 0; i < CoderStepAnswers.Count; i++)
            builder.AppendLine($"===== CODER STEP {i + 1} =====").AppendLine(CoderStepAnswers[i]);
        return builder.ToString().Trim();
    }

    private string BuildExecutionSummary()
    {
        if (SuccessfulExecutionResults.Count == 0) return ExecutionResult;
        var builder = new StringBuilder();
        for (var i = 0; i < SuccessfulExecutionResults.Count; i++)
            builder.AppendLine($"===== LOCAL STEP {i + 1} =====").AppendLine(SuccessfulExecutionResults[i]);
        return builder.ToString().Trim();
    }

    private static bool ContainsMarker(string answer, string marker) =>
        answer.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line.Trim(), marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsNoSafeMatchFailure(string value) =>
        value.Contains("PATCH_MATCH_MODE: NO_SAFE_MATCH", StringComparison.OrdinalIgnoreCase)
        || value.Contains("matches=0", StringComparison.OrdinalIgnoreCase)
        || value.Contains("matches = 0", StringComparison.OrdinalIgnoreCase);

    private static string BuildPatchSearchHash(string answer)
    {
        var matches = Regex.Matches(answer, @"(?ms)^<<<SEARCH\s*\r?\n(?<search>.*?)\r?\nSEARCH\s*$", RegexOptions.CultureInvariant);
        if (matches.Count == 0) return string.Empty;
        var searches = string.Join("\n---\n", matches.Select(match => match.Groups["search"].Value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(searches)));
    }

    private static ReviewVerdict ReviewerVerdict(string answer)
    {
        var firstLine = answer.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        if (string.Equals(firstLine, "PASS", StringComparison.OrdinalIgnoreCase)) return ReviewVerdict.Pass;
        if (string.Equals(firstLine, "FAIL", StringComparison.OrdinalIgnoreCase)) return ReviewVerdict.Fail;
        return ReviewVerdict.Unknown;
    }

    private static string RoleLabel(AgentRole role) => $"{(int)role + 1} {role}";

    private static string Tail(string? value, int max)
    {
        var text = value ?? string.Empty;
        return text.Length <= max ? text : text[^max..];
    }

    private enum ReviewVerdict { Unknown, Pass, Fail }
}

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    public int DuplicateLimit { get; set; } = 2;
    public string StopReason { get; set; } = string.Empty;
    public Dictionary<AgentRole, string> Answers { get; set; } = new();
    public string LastAnswerHash { get; set; } = string.Empty;
    public int DuplicateCount { get; set; }
    public string ExecutionResult { get; set; } = string.Empty;
    public bool LastExecutionSucceeded { get; set; }
    public string WorkspaceContext { get; set; } = string.Empty;
    public string CoderWorkspaceContext { get; set; } = string.Empty;

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
        WorkspaceContext = workspaceContext; CoderWorkspaceContext = string.Empty; Save();
        WorkflowDiagnostics.Event("1 Manager", "workflow", "WORKFLOW_STARTED", $"taskLength={TaskText.Length}; workspaceContextLength={WorkspaceContext.Length}");
        WorkflowDiagnostics.Snapshot(this, "WORKFLOW_STARTED");
    }

    public void Reset()
    {
        TaskText = string.Empty; State = WorkflowState.Idle; CurrentRole = AgentRole.Manager;
        AiCalls = 0; SendAttempts = 0; FixAttempts = 0; StopReason = string.Empty; Answers.Clear();
        LastAnswerHash = string.Empty; DuplicateCount = 0; ExecutionResult = string.Empty; LastExecutionSucceeded = false;
        WorkspaceContext = string.Empty; CoderWorkspaceContext = string.Empty; Save();
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
        Save();
        WorkflowDiagnostics.Event(RoleLabel(CurrentRole), "local-verification", success ? "LOCAL_VERIFICATION_PASS" : "LOCAL_VERIFICATION_FAIL", Tail(value, 1200));
        if (!success) WorkflowDiagnostics.Snapshot(this, "LOCAL_VERIFICATION_FAIL", Tail(value, 3500));
    }

    public void SetCoderWorkspaceContext(string value) { CoderWorkspaceContext = value; Save(); }

    public bool TryBeginPromptAttempt()
    {
        if (State != WorkflowState.Running) return false;
        if (AiCalls >= MaxAiCalls) { Stop("AI呼び出し上限に達しました"); return false; }
        if (SendAttempts >= MaxSendAttempts) { Stop("送信試行回数の上限に達しました"); return false; }
        SendAttempts++;
        WorkflowDiagnostics.Event(RoleLabel(CurrentRole), "send", "PROMPT_ATTEMPT", $"sendAttempt={SendAttempts}; aiCalls={AiCalls}; fixAttempts={FixAttempts}");
        Save(); return true;
    }

    public void RecordPromptAccepted()
    {
        AiCalls++;
        WorkflowDiagnostics.Event(RoleLabel(CurrentRole), "send", "PROMPT_ACCEPTED", $"aiCalls={AiCalls}; sendAttempts={SendAttempts}");
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
        WorkflowDiagnostics.Event(RoleLabel(roleBefore), "answer", "ANSWER_RECEIVED", $"length={normalized.Length}; lastExecutionSucceeded={LastExecutionSucceeded}");

        var markerValid = CurrentRole switch
        {
            AgentRole.Manager => ContainsMarker(normalized, "MANAGER_DONE"),
            AgentRole.Planner => ContainsMarker(normalized, "PLAN_DONE"),
            AgentRole.Coder => ContainsMarker(normalized, "CODER_DONE"),
            AgentRole.Reviewer => ReviewerVerdict(normalized) != ReviewVerdict.Unknown,
            _ => false
        };
        if (!markerValid)
        {
            WorkflowDiagnostics.Event(RoleLabel(roleBefore), "answer", "ANSWER_FORMAT_MISMATCH", $"answerLength={normalized.Length}");
            WorkflowDiagnostics.Snapshot(this, "ANSWER_FORMAT_MISMATCH", $"role={roleBefore}; answerLength={normalized.Length}");
            Stop($"{CurrentRole} の回答形式を確認できませんでした");
            return false;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        DuplicateCount = hash == LastAnswerHash ? DuplicateCount + 1 : 0;
        LastAnswerHash = hash;
        if (DuplicateCount >= DuplicateLimit)
        {
            WorkflowDiagnostics.Event(RoleLabel(roleBefore), "answer", "DUPLICATE_ANSWER_BLOCKED", $"duplicateCount={DuplicateCount}");
            WorkflowDiagnostics.Snapshot(this, "DUPLICATE_ANSWER_BLOCKED");
            Stop("同一回答が繰り返されたため停止しました");
            return false;
        }
        Answers[CurrentRole] = normalized;

        switch (CurrentRole)
        {
            case AgentRole.Manager:
                CurrentRole = AgentRole.Planner;
                WorkflowDiagnostics.Event("1 Manager", "transition", "MANAGER_TO_PLANNER");
                break;

            case AgentRole.Planner:
                CurrentRole = AgentRole.Coder;
                WorkflowDiagnostics.Event("2 Planner", "transition", "PLANNER_TO_CODER");
                break;

            case AgentRole.Coder:
                if (!LastExecutionSucceeded)
                {
                    FixAttempts++;
                    WorkflowDiagnostics.Event("3 Coder", "transition", "CODER_RETRY_LOCAL_VERIFICATION_FAILED", $"fixAttempt={FixAttempts}/{MaxFixAttempts}; this explains another Coder question instead of Reviewer");
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
                    CurrentRole = AgentRole.Reviewer;
                    WorkflowDiagnostics.Event("3 Coder", "transition", "CODER_TO_REVIEWER", $"coderAnswerLength={normalized.Length}; localVerification=PASS");
                    WorkflowDiagnostics.Snapshot(this, "CODER_TO_REVIEWER", "Coder answer accepted and local verification passed. Reviewer should be the next role.");
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
                    WorkflowDiagnostics.Event("4 Reviewer", "transition", "REVIEWER_FAIL_TO_CODER", $"fixAttempt={FixAttempts}/{MaxFixAttempts}");
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
            $"previousRole={roleBefore}; currentRole={CurrentRole}; state={State}; fixAttempts={FixAttempts}");
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
            あなたはPlannerです。司令塔の結果と実際のWORKSPACE_CONTEXTを読み、実装計画だけを作ってください。
            必ず実在するファイル名・クラス・UI要素を根拠に変更対象を選んでください。
            既存アプリの機能変更依頼なのに、無関係なtxtやダミーファイルを追加して代用してはいけません。
            変更対象、追加ファイル、テスト方法、リスクを具体化してください。コード全文は書かないでください。
            最後に PLAN_DONE と書いてください。

            USER_REQUEST:
            {TaskText}

            MANAGER_RESULT:
            {GetAnswer(AgentRole.Manager)}

            WORKSPACE_CONTEXT:
            {WorkspaceContext}
            """,

        AgentRole.Coder => $"""
            あなたはCoderです。計画と実際のWORKSPACE_CONTEXTに従い、workspace内へ適用できる本物の変更を出力してください。
            使用可能ACTIONは CREATE / PATCH / MODIFY です。.git/bin/objやworkspace外は変更禁止です。
            既存ファイルの小規模変更では必ず PATCH を第一選択にしてください。MODIFYによるファイル全文再生成は、PATCHでは安全に表現できない場合だけ使用してください。
            無関係なtxt、説明用ファイル、ダミーファイルを作って実装の代わりにしてはいけません。

            既存ファイルを部分変更する場合は次の厳密なPATCH形式を使ってください。

            FILE: relative/path.ext
            ACTION: PATCH
            <<<SEARCH
            既存ファイル内に完全一致で1回だけ存在する、十分に具体的な元の文字列
            SEARCH
            <<<REPLACE
            置換後の文字列
            REPLACE

            SEARCHはWORKSPACE_CONTEXTに実際に存在する文字列をそのまま使い、完全一致1件になるだけの周辺行を含めてください。
            SEARCHを推測・省略・整形してはいけません。PATCHは一致数が0件または複数件なら安全のため自動拒否されます。

            新規ファイル、またはPATCHで安全に表現できない場合だけ次を使えます。

            FILE: relative/path.ext
            ACTION: CREATE または MODIFY
            <<<CONTENT
            ファイル全文
            CONTENT

            重要: 各ブロック内にはファイル内容だけを書き、Markdownコードフェンスや説明文を混ぜないでください。
            Plannerが複数の変更対象ファイルを指定し、それらがWORKSPACE_CONTEXTに完全な内容で存在する場合は、計画どおり各対象ファイルを最小差分で変更してください。別ファイルの動的生成で計画を迂回しないでください。
            XAML/XMLでは終了ルートタグの後ろにCODER_DONEや説明文を絶対に入れないでください。CODER_DONEはすべてのFILEブロックの外側、回答の最後の独立行にだけ置いてください。
            ローカルbuild/testが失敗した場合は LOCAL_EXECUTION_FEEDBACK を最優先で読み、同じ失敗を繰り返さず、可能なら全文MODIFYからPATCHへ切り替えて修正してください。
            Reviewerから修正指摘がある場合は REVIEW_FEEDBACK を最優先で反映し、指摘された無関係な差分を元に戻してください。
            説明だけで終わらず、実装が必要なら必ず上記ブロックを出してください。
            最後に CODER_DONE と書いてください。

            USER_REQUEST:
            {TaskText}

            PLAN:
            {GetAnswer(AgentRole.Planner)}

            REVIEW_FEEDBACK:
            {GetAnswer(AgentRole.Reviewer)}

            LOCAL_EXECUTION_FEEDBACK:
            {ExecutionResult}

            WORKSPACE_CONTEXT:
            {CoderWorkspaceContext}
            """,

        AgentRole.Reviewer => $"""
            あなたはReviewerです。Coderの変更内容とローカル実行結果を厳しくレビューしてください。
            ビルド失敗、要求未達、危険な変更があれば先頭行を FAIL にしてください。
            問題なければ先頭行を PASS にしてください。FAILの場合は修正点を具体的に列挙してください。

            重要: LOCAL_EXECUTION_RESULT に PASS_ALREADY_SATISFIED とあり、対象ファイルが既に存在していて内容が要求と完全一致し、WRITE_PERFORMED: false で安全に上書きを避けている場合は、最終状態として要求を満たしているものとして扱ってください。ユーザーが明示的に「必ず今回新規作成すること」自体を要求していない限り、それだけを理由にFAILにしないでください。

            USER_REQUEST:
            {TaskText}

            PLAN:
            {GetAnswer(AgentRole.Planner)}

            CODER_RESULT:
            {GetAnswer(AgentRole.Coder)}

            LOCAL_EXECUTION_RESULT:
            {ExecutionResult}
            """,
        _ => TaskText
    };

    public string GetAnswer(AgentRole role) => Answers.TryGetValue(role, out var value) ? value : "(なし)";

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(CheckpointDirectory);
            File.WriteAllText(CheckpointPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static bool ContainsMarker(string answer, string marker) =>
        answer.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line.Trim(), marker, StringComparison.OrdinalIgnoreCase));

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

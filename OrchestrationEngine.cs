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
    }

    public void Reset()
    {
        TaskText = string.Empty; State = WorkflowState.Idle; CurrentRole = AgentRole.Manager;
        AiCalls = 0; SendAttempts = 0; FixAttempts = 0; StopReason = string.Empty; Answers.Clear();
        LastAnswerHash = string.Empty; DuplicateCount = 0; ExecutionResult = string.Empty; LastExecutionSucceeded = false;
        WorkspaceContext = string.Empty; CoderWorkspaceContext = string.Empty; Save();
    }

    public void Stop(string reason) { State = WorkflowState.Stopped; StopReason = reason; Save(); }
    public void SetExecutionResult(string value) { ExecutionResult = value; Save(); }
    public void SetExecutionResult(string value, bool success) { ExecutionResult = value; LastExecutionSucceeded = success; Save(); }
    public void SetCoderWorkspaceContext(string value) { CoderWorkspaceContext = value; Save(); }

    public bool TryBeginPromptAttempt()
    {
        if (State != WorkflowState.Running) return false;
        if (AiCalls >= MaxAiCalls) { Stop("AI呼び出し上限に達しました"); return false; }
        if (SendAttempts >= MaxSendAttempts) { Stop("送信試行回数の上限に達しました"); return false; }
        SendAttempts++; Save(); return true;
    }

    public void RecordPromptAccepted() { AiCalls++; Save(); }

    public bool RecordAnswer(string answer)
    {
        if (State != WorkflowState.Running || string.IsNullOrWhiteSpace(answer)) return false;
        var normalized = answer.Trim();
        var markerValid = CurrentRole switch
        {
            AgentRole.Manager => ContainsMarker(normalized, "MANAGER_DONE"),
            AgentRole.Planner => ContainsMarker(normalized, "PLAN_DONE"),
            AgentRole.Coder => ContainsMarker(normalized, "CODER_DONE"),
            AgentRole.Reviewer => ReviewerVerdict(normalized) != ReviewVerdict.Unknown,
            _ => false
        };
        if (!markerValid) { Stop($"{CurrentRole} の回答形式を確認できませんでした"); return false; }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        DuplicateCount = hash == LastAnswerHash ? DuplicateCount + 1 : 0;
        LastAnswerHash = hash;
        if (DuplicateCount >= DuplicateLimit) { Stop("同一回答が繰り返されたため停止しました"); return false; }
        Answers[CurrentRole] = normalized;

        switch (CurrentRole)
        {
            case AgentRole.Manager:
                CurrentRole = AgentRole.Planner;
                break;
            case AgentRole.Planner:
                CurrentRole = AgentRole.Coder;
                break;
            case AgentRole.Coder:
                if (!LastExecutionSucceeded)
                {
                    FixAttempts++;
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
                }
                break;
            case AgentRole.Reviewer:
                if (ReviewerVerdict(normalized) == ReviewVerdict.Pass && !LastExecutionSucceeded)
                {
                    Stop("ローカル検証失敗のためReviewer PASSを受理できません");
                    return false;
                }
                if (ReviewerVerdict(normalized) == ReviewVerdict.Fail)
                {
                    FixAttempts++;
                    if (FixAttempts >= MaxFixAttempts) { Stop("修正回数の上限に達しました"); return false; }
                    CurrentRole = AgentRole.Coder;
                }
                else State = WorkflowState.Success;
                break;
        }
        Save(); return true;
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
            CREATE/MODIFYのみ使用可能です。.git/bin/objやworkspace外は変更禁止です。
            既存アプリのUIや動作を変更する依頼では、WORKSPACE_CONTEXTから該当する既存ソースを特定してMODIFYしてください。
            無関係なtxt、説明用ファイル、ダミーファイルを作って実装の代わりにしてはいけません。
            変更する各ファイルを必ず次の厳密な形式で、ファイル全文として出力してください。

            FILE: relative/path.ext
            ACTION: CREATE または MODIFY
            <<<CONTENT
            ファイル全文
            CONTENT

            重要: <<<CONTENT と CONTENT の間にはファイルそのものだけを書いてください。
            ```xml や ```csharp などのMarkdownコードフェンス、説明文、ファイル名見出しを混ぜてはいけません。
            XAML/XMLは単一の正しいルート要素で閉じ、終了タグより後ろに余分な文字を置いてはいけません。
            ローカルbuild/testが失敗した場合は LOCAL_EXECUTION_FEEDBACK を最優先で読み、同じ失敗を繰り返さず修正してください。
            説明だけで終わらず、実装が必要なら必ず上記ブロックを出してください。
            Reviewerから修正指摘がある場合は必ず反映してください。
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

    private enum ReviewVerdict { Unknown, Pass, Fail }
}

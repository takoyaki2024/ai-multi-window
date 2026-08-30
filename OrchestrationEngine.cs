using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiMultiWindow;

public enum AgentRole
{
    Manager = 0,
    Planner = 1,
    Coder = 2,
    Reviewer = 3
}

public enum WorkflowState
{
    Idle,
    Running,
    Success,
    Stopped
}

public sealed class OrchestrationEngine
{
    public string TaskText { get; set; } = string.Empty;
    public WorkflowState State { get; set; } = WorkflowState.Idle;
    public AgentRole CurrentRole { get; set; } = AgentRole.Manager;
    public int AiCalls { get; set; }
    public int FixAttempts { get; set; }
    public int MaxAiCalls { get; set; } = 10;
    public int MaxFixAttempts { get; set; } = 3;
    public int DuplicateLimit { get; set; } = 2;
    public string StopReason { get; set; } = string.Empty;
    public Dictionary<AgentRole, string> Answers { get; set; } = new();
    public string LastAnswerHash { get; set; } = string.Empty;
    public int DuplicateCount { get; set; }

    private static string CheckpointDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AiMultiWindow");

    private static string CheckpointPath => Path.Combine(CheckpointDirectory, "orchestrator-checkpoint.json");

    public static OrchestrationEngine Load()
    {
        try
        {
            if (!File.Exists(CheckpointPath))
                return new OrchestrationEngine();

            var json = File.ReadAllText(CheckpointPath);
            return JsonSerializer.Deserialize<OrchestrationEngine>(json) ?? new OrchestrationEngine();
        }
        catch
        {
            return new OrchestrationEngine();
        }
    }

    public void Start(string task)
    {
        TaskText = task.Trim();
        State = WorkflowState.Running;
        CurrentRole = AgentRole.Manager;
        AiCalls = 0;
        FixAttempts = 0;
        StopReason = string.Empty;
        Answers.Clear();
        LastAnswerHash = string.Empty;
        DuplicateCount = 0;
        Save();
    }

    public void Reset()
    {
        TaskText = string.Empty;
        State = WorkflowState.Idle;
        CurrentRole = AgentRole.Manager;
        AiCalls = 0;
        FixAttempts = 0;
        StopReason = string.Empty;
        Answers.Clear();
        LastAnswerHash = string.Empty;
        DuplicateCount = 0;
        Save();
    }

    public void Stop(string reason)
    {
        State = WorkflowState.Stopped;
        StopReason = reason;
        Save();
    }

    public bool MarkPromptSent()
    {
        if (State != WorkflowState.Running)
            return false;

        if (AiCalls >= MaxAiCalls)
        {
            Stop("AI呼び出し上限に達しました");
            return false;
        }

        AiCalls++;
        Save();
        return true;
    }

    public bool RecordAnswer(string answer)
    {
        if (State != WorkflowState.Running || string.IsNullOrWhiteSpace(answer))
            return false;

        var normalized = answer.Trim();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        if (hash == LastAnswerHash)
            DuplicateCount++;
        else
            DuplicateCount = 0;

        LastAnswerHash = hash;
        if (DuplicateCount >= DuplicateLimit)
        {
            Stop("同一回答が繰り返されたため停止しました");
            return false;
        }

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
                CurrentRole = AgentRole.Reviewer;
                break;
            case AgentRole.Reviewer:
                if (ReviewerRequestsFix(normalized))
                {
                    FixAttempts++;
                    if (FixAttempts >= MaxFixAttempts)
                    {
                        Stop("修正回数の上限に達しました");
                        return false;
                    }

                    CurrentRole = AgentRole.Coder;
                }
                else
                {
                    State = WorkflowState.Success;
                }
                break;
        }

        Save();
        return true;
    }

    public string BuildCurrentPrompt()
    {
        return CurrentRole switch
        {
            AgentRole.Manager => $"""
                あなたは開発司令塔です。次の依頼を安全に実行可能な作業へ分解してください。
                仕様を勝手に増やさず、完了条件・作業順・注意点を明確にしてください。
                最後に MANAGER_DONE と書いてください。

                USER_REQUEST:
                {TaskText}
                """,

            AgentRole.Planner => $"""
                あなたはPlannerです。司令塔の結果から実装計画だけを作ってください。
                変更対象、追加ファイル、テスト方法、リスクを具体化してください。コード全文は書かないでください。
                最後に PLAN_DONE と書いてください。

                USER_REQUEST:
                {TaskText}

                MANAGER_RESULT:
                {GetAnswer(AgentRole.Manager)}
                """,

            AgentRole.Coder => $"""
                あなたはCoderです。以下の依頼と計画に従って実装案を作ってください。
                仕様を勝手に増やさず、変更ファイルと必要なコードを明示してください。
                Reviewerから修正指摘がある場合は必ず反映してください。
                最後に CODER_DONE と書いてください。

                USER_REQUEST:
                {TaskText}

                PLAN:
                {GetAnswer(AgentRole.Planner)}

                REVIEW_FEEDBACK:
                {GetAnswer(AgentRole.Reviewer)}
                """,

            AgentRole.Reviewer => $"""
                あなたはReviewerです。Coderの結果を厳しくレビューしてください。
                致命的または実装上必要な修正がある場合は先頭行を FAIL にしてください。
                問題なければ先頭行を PASS にしてください。
                FAILの場合は修正点を具体的に列挙してください。

                USER_REQUEST:
                {TaskText}

                PLAN:
                {GetAnswer(AgentRole.Planner)}

                CODER_RESULT:
                {GetAnswer(AgentRole.Coder)}
                """,

            _ => TaskText
        };
    }

    public string GetAnswer(AgentRole role) => Answers.TryGetValue(role, out var value) ? value : "(なし)";

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(CheckpointDirectory);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CheckpointPath, json);
        }
        catch
        {
            // Checkpoint failure must not crash the UI.
        }
    }

    private static bool ReviewerRequestsFix(string answer)
    {
        var firstLine = answer.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? string.Empty;
        return firstLine.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase);
    }
}

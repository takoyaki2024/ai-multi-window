using System.Text;

namespace AiMultiWindow;

public static class WorkflowDiagnostics
{
    private static readonly object Sync = new();
    private static readonly Queue<string> RecentEvents = new();
    private const int MaxRecentEvents = 40;

    public static string LatestReportPath => Path.Combine(
        Environment.CurrentDirectory,
        ".ai-multi-window",
        "logs",
        "workflow-diagnostic-latest.txt");

    public static void Event(string role, string stage, string code, string detail = "")
    {
        var line = $"{DateTime.Now:O} | role={Clean(role)} | stage={Clean(stage)} | code={Clean(code)} | {Clean(detail)}";
        lock (Sync)
        {
            RecentEvents.Enqueue(line);
            while (RecentEvents.Count > MaxRecentEvents) RecentEvents.Dequeue();
            WriteLatestUnsafe(null, code, detail);
        }
    }

    public static void Snapshot(OrchestrationEngine engine, string code, string detail = "")
    {
        lock (Sync)
        {
            WriteLatestUnsafe(engine, code, detail);
            if (IsErrorCode(code))
            {
                try
                {
                    var root = Path.GetDirectoryName(LatestReportPath)!;
                    Directory.CreateDirectory(root);
                    var path = Path.Combine(root, $"workflow-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{SafeFilePart(code)}.txt");
                    File.Copy(LatestReportPath, path, overwrite: true);
                }
                catch { }
            }
        }
    }

    private static void WriteLatestUnsafe(OrchestrationEngine? engine, string code, string detail)
    {
        try
        {
            var root = Path.GetDirectoryName(LatestReportPath)!;
            Directory.CreateDirectory(root);
            var text = new StringBuilder()
                .AppendLine("AI_MULTI_WINDOW_DIAGNOSTIC_V1")
                .AppendLine($"TIME: {DateTime.Now:O}")
                .AppendLine($"CODE: {Clean(code)}")
                .AppendLine($"DETAIL: {Clean(detail)}");

            if (engine is not null)
            {
                text.AppendLine($"STATE: {engine.State}")
                    .AppendLine($"CURRENT_ROLE: {(int)engine.CurrentRole + 1} {engine.CurrentRole}")
                    .AppendLine($"AI_CALLS: {engine.AiCalls}/{engine.MaxAiCalls}")
                    .AppendLine($"SEND_ATTEMPTS: {engine.SendAttempts}/{engine.MaxSendAttempts}")
                    .AppendLine($"FIX_ATTEMPTS: {engine.FixAttempts}/{engine.MaxFixAttempts}")
                    .AppendLine($"CODER_STEP: {engine.CoderStepNumber}/{engine.CoderStepCount}")
                    .AppendLine($"AWAITING_RESPONSE: {engine.AwaitingResponse}")
                    .AppendLine($"AWAITING_ROLE: {(int)engine.AwaitingRole + 1} {engine.AwaitingRole}")
                    .AppendLine($"AWAITING_CODER_STEP: {engine.AwaitingCoderStepIndex + 1}")
                    .AppendLine($"LAST_EXECUTION_SUCCEEDED: {engine.LastExecutionSucceeded}")
                    .AppendLine($"STOP_REASON: {Clean(engine.StopReason)}")
                    .AppendLine($"MANAGER_ANSWER_LENGTH: {engine.GetAnswer(AgentRole.Manager).Length}")
                    .AppendLine($"PLANNER_ANSWER_LENGTH: {engine.GetAnswer(AgentRole.Planner).Length}")
                    .AppendLine($"CODER_ANSWER_LENGTH: {engine.GetAnswer(AgentRole.Coder).Length}")
                    .AppendLine($"REVIEWER_ANSWER_LENGTH: {engine.GetAnswer(AgentRole.Reviewer).Length}")
                    .AppendLine("EXECUTION_RESULT_TAIL:")
                    .AppendLine(Tail(engine.ExecutionResult, 5000));
            }

            text.AppendLine("RECENT_EVENTS:");
            foreach (var item in RecentEvents) text.AppendLine(item);
            File.WriteAllText(LatestReportPath, text.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    private static bool IsErrorCode(string code) =>
        code.Contains("FAIL", StringComparison.OrdinalIgnoreCase)
        || code.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
        || code.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase)
        || code.Contains("BLOCKED", StringComparison.OrdinalIgnoreCase)
        || code.Contains("STOP", StringComparison.OrdinalIgnoreCase)
        || code.Contains("MISMATCH", StringComparison.OrdinalIgnoreCase);

    private static string Tail(string? value, int max)
    {
        var text = value ?? string.Empty;
        return text.Length <= max ? text : text[^max..];
    }

    private static string Clean(string? value) =>
        (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string SafeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var result = new string(chars);
        return result.Length <= 60 ? result : result[..60];
    }
}

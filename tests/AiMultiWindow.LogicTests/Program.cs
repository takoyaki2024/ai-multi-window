using AiMultiWindow;

var tests = new (string Name, Func<Task> Run)[]
{
    ("workflow-happy-path", WorkflowHappyPath),
    ("reviewer-unknown-stops", ReviewerUnknownStops),
    ("failed-local-verification-rejects-pass", FailedVerificationRejectsPass),
    ("planner-and-coder-contexts-are-separated", ContextsAreSeparated)
};

var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"RESULT {tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static Task WorkflowHappyPath()
{
    var e = new OrchestrationEngine();
    e.Start("task", "tree");
    Assert(e.TryBeginPromptAttempt(), "manager attempt"); e.RecordPromptAccepted();
    Assert(e.RecordAnswer("work\nMANAGER_DONE"), "manager answer");
    Assert(e.CurrentRole == AgentRole.Planner, "planner transition");
    Assert(e.RecordAnswer("plan\nPLAN_DONE"), "planner answer");
    Assert(e.RecordAnswer("files\nCODER_DONE"), "coder answer");
    e.SetExecutionResult("ok", true);
    Assert(e.RecordAnswer("PASS\nLooks good"), "review answer");
    Assert(e.State == WorkflowState.Success, "success state");
    Assert(e.AiCalls == 1 && e.SendAttempts == 1, "accepted and attempted counts");
    return Task.CompletedTask;
}

static Task ReviewerUnknownStops()
{
    var e = AtReviewer(true);
    Assert(!e.RecordAnswer("Looks good"), "unknown verdict rejected");
    Assert(e.State == WorkflowState.Stopped, "unknown verdict stops");
    return Task.CompletedTask;
}

static Task FailedVerificationRejectsPass()
{
    var e = AtReviewer(false);
    Assert(!e.RecordAnswer("PASS\nLooks good"), "PASS rejected after failed local verification");
    Assert(e.State == WorkflowState.Stopped, "failed verification stops");
    return Task.CompletedTask;
}

static async Task ContextsAreSeparated()
{
    var root = Path.Combine(Path.GetTempPath(), "AiMultiWindowTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        await File.WriteAllTextAsync(Path.Combine(root, "App.csproj"), "<Project />");
        await File.WriteAllTextAsync(Path.Combine(root, "Small.cs"), "class Small { }");
        await File.WriteAllTextAsync(Path.Combine(root, "Large.cs"), new string('x', 19_000));
        var planner = await WorkspaceContextBuilder.BuildPlannerAsync(root);
        var coder = await WorkspaceContextBuilder.BuildCoderAsync(root, "Modify Small.cs and Large.cs");
        Assert(planner.Contains("OMITTED_LARGE_FILE: Large.cs"), "large planner file omitted, not truncated");
        Assert(coder.Contains("COMPLETE FILE: Small.cs"), "selected coder file included");
        Assert(coder.Contains("OMITTED_TOO_LARGE: Large.cs"), "large coder file refused");
        Assert(!coder.Contains(new string('x', 100)), "partial large content not supplied");
    }
    finally { Directory.Delete(root, true); }
}

static OrchestrationEngine AtReviewer(bool verificationSucceeded)
{
    var e = new OrchestrationEngine();
    e.Start("task");
    e.RecordAnswer("MANAGER_DONE");
    e.RecordAnswer("PLAN_DONE");
    e.RecordAnswer("CODER_DONE");
    e.SetExecutionResult("result", verificationSucceeded);
    return e;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

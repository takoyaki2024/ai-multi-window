using AiMultiWindow;

var tests = new (string Name, Func<Task> Run)[]
{
    ("workflow-multi-step-3-to-4", WorkflowMultiStepHappyPath),
    ("local-failure-retries-current-step", LocalFailureRetriesCurrentStep),
    ("reviewer-fail-repairs-last-step", ReviewerFailRepairsLastStep),
    ("reviewer-unknown-stops", ReviewerUnknownStops),
    ("failed-local-verification-rejects-pass", FailedVerificationRejectsPass),
    ("coder-context-is-current-step-scoped", CoderContextIsCurrentStepScoped),
    ("step-rollback-preserves-committed-step", StepRollbackPreservesCommittedStep)
};

var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"RESULT {tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static Task WorkflowMultiStepHappyPath()
{
    var e = new OrchestrationEngine();
    e.Start("task", "tree");
    Accept(e, "work\nMANAGER_DONE");
    Assert(e.CurrentRole == AgentRole.Planner, "manager -> planner");
    Accept(e, "STEP: 1\nFILES: A.cs\nTASK: first\nSTEP_END\nSTEP: 2\nFILES: B.cs\nTASK: second\nSTEP_END\nPLAN_DONE");
    Assert(e.CurrentRole == AgentRole.Coder && e.CoderStepNumber == 1, "planner -> coder step 1");
    e.SetExecutionResult("step 1 pass", true);
    Accept(e, "same answer\nCODER_DONE");
    Assert(e.CurrentRole == AgentRole.Coder && e.CoderStepNumber == 2, "step 1 -> step 2, not reviewer");
    e.SetExecutionResult("step 2 pass", true);
    Accept(e, "same answer\nCODER_DONE");
    Assert(e.CurrentRole == AgentRole.Reviewer, "last successful coder step -> reviewer");
    Assert(e.CoderStepAnswers.Count == 2 && e.SuccessfulExecutionResults.Count == 2, "one result per step");
    Accept(e, "PASS\nLooks good");
    Assert(e.State == WorkflowState.Success, "reviewer PASS -> success");
    Assert(e.AiCalls == 5 && e.SendAttempts == 5 && !e.AwaitingResponse, "one accepted send per role/step");
    return Task.CompletedTask;
}

static Task LocalFailureRetriesCurrentStep()
{
    var e = AtCoderWithTwoSteps();
    e.SetExecutionResult("compile failed", false);
    Assert(e.RecordAnswer("attempt 1\nCODER_DONE"), "failed answer is processed as retry feedback");
    Assert(e.CurrentRole == AgentRole.Coder && e.CoderStepNumber == 1, "failure stays on current step");
    Assert(e.FixAttempts == 1 && e.CoderStepAnswers.Count == 0, "failure counted once and not recorded as success");
    e.SetExecutionResult("fixed", true);
    Assert(e.RecordAnswer("attempt 2\nCODER_DONE"), "fixed answer accepted");
    Assert(e.CoderStepNumber == 2 && e.FixAttempts == 1, "success advances without erasing total fix count");
    Assert(e.SuccessfulExecutionResults.Count == 1, "retry result replaces failed result");
    return Task.CompletedTask;
}

static Task ReviewerFailRepairsLastStep()
{
    var e = AtCoderWithTwoSteps();
    e.SetExecutionResult("one", true); Assert(e.RecordAnswer("one\nCODER_DONE"), "step one");
    e.SetExecutionResult("two", true); Assert(e.RecordAnswer("two\nCODER_DONE"), "step two");
    Assert(e.CurrentRole == AgentRole.Reviewer, "at reviewer");
    Assert(e.RecordAnswer("FAIL\nrepair last step"), "review fail accepted");
    Assert(e.CurrentRole == AgentRole.Coder && e.CoderStepNumber == 2 && e.FixAttempts == 1, "reviewer returns only to last step");
    e.SetExecutionResult("two repaired", true); Assert(e.RecordAnswer("two repaired\nCODER_DONE"), "repair accepted");
    Assert(e.CurrentRole == AgentRole.Reviewer && e.FixAttempts == 1, "repair returns to reviewer without resetting limit");
    Assert(e.CoderStepAnswers.Count == 2 && e.CoderStepAnswers[1].Contains("repaired"), "repair replaces last step answer");
    Assert(e.SuccessfulExecutionResults.Count == 2 && e.SuccessfulExecutionResults[1] == "two repaired", "repair replaces last verification");
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

static async Task CoderContextIsCurrentStepScoped()
{
    var root = TempRoot();
    try
    {
        await File.WriteAllTextAsync(Path.Combine(root, "A.cs"), "class A { }");
        await File.WriteAllTextAsync(Path.Combine(root, "B.cs"), "class B { }");
        await File.WriteAllTextAsync(Path.Combine(root, "C.cs"), "class C { }");
        var coder = await WorkspaceContextBuilder.BuildCoderAsync(root, "FILES: B.cs, C.cs");
        Assert(!coder.Contains("COMPLETE FILE: A.cs"), "unmentioned file excluded");
        Assert(coder.Contains("COMPLETE FILE: B.cs") && coder.Contains("COMPLETE FILE: C.cs"), "current step files included");
    }
    finally { Directory.Delete(root, true); }
}

static async Task StepRollbackPreservesCommittedStep()
{
    var root = TempRoot();
    try
    {
        await File.WriteAllTextAsync(Path.Combine(root, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(root, "Value.cs"), "public static class Value { public const int Number = 1; }");
        var first = await WorkspaceExecutor.ApplyCoderResponseAsync(root, Patch("Value.cs", "Number = 1", "Number = 2"));
        Assert(first.Success, "first step verifies");
        Assert(!WorkspaceExecutor.CommitPending().Contains("FAILED", StringComparison.Ordinal), "first step commits");
        var second = await WorkspaceExecutor.ApplyCoderResponseAsync(root, Patch("Value.cs", "Number = 2", "Number = ;"));
        Assert(!second.Success, "broken second step fails verification");
        var content = await File.ReadAllTextAsync(Path.Combine(root, "Value.cs"));
        Assert(content.Contains("Number = 2"), "failed step rolled back without reverting committed step");
        Assert(!WorkspaceExecutor.HasPendingChanges, "rollback boundary cleared");
    }
    finally
    {
        if (WorkspaceExecutor.HasPendingChanges) await WorkspaceExecutor.RollbackPendingAsync();
        Directory.Delete(root, true);
    }
}

static OrchestrationEngine AtCoderWithTwoSteps()
{
    var e = new OrchestrationEngine();
    e.Start("task");
    Assert(e.RecordAnswer("MANAGER_DONE"), "manager");
    Assert(e.RecordAnswer("STEP: 1\nFILES: A.cs\nTASK: first\nSTEP_END\nSTEP: 2\nFILES: B.cs\nTASK: second\nSTEP_END\nPLAN_DONE"), "planner");
    return e;
}

static OrchestrationEngine AtReviewer(bool verificationSucceeded)
{
    var e = new OrchestrationEngine();
    e.Start("task");
    e.RecordAnswer("MANAGER_DONE");
    e.RecordAnswer("PLAN_DONE");
    e.SetExecutionResult("result", verificationSucceeded);
    e.RecordAnswer("CODER_DONE");
    return e;
}

static void Accept(OrchestrationEngine e, string answer)
{
    Assert(e.TryBeginPromptAttempt(), "prompt attempt");
    e.RecordPromptAccepted();
    Assert(e.AwaitingResponse, "accepted send awaits exactly one response");
    Assert(e.RecordAnswer(answer), "answer accepted");
}

static string Patch(string path, string search, string replace) => $"FILE: {path}\nACTION: PATCH\n<<<SEARCH\n{search}\nSEARCH\n<<<REPLACE\n{replace}\nREPLACE\nCODER_DONE";
static string TempRoot() { var path = Path.Combine(Path.GetTempPath(), "AiMultiWindowTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

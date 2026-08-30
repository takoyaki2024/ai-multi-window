using AiMultiWindow;

var tests = new (string Name, Func<Task> Run)[]
{
    ("workflow-multi-step-3-to-4", WorkflowMultiStepHappyPath),
    ("local-failure-retries-current-step", LocalFailureRetriesCurrentStep),
    ("reviewer-fail-repairs-last-step", ReviewerFailRepairsLastStep),
    ("reviewer-unknown-stops", ReviewerUnknownStops),
    ("failed-local-verification-rejects-pass", FailedVerificationRejectsPass),
    ("coder-context-is-current-step-scoped", CoderContextIsCurrentStepScoped),
    ("step-rollback-preserves-committed-step", StepRollbackPreservesCommittedStep),
    ("no-safe-match-duplicate-escalates-repair", NoSafeMatchDuplicateEscalatesRepair),
    ("failed-patch-repairs-through-reviewer", FailedPatchRepairsThroughReviewer),
    ("safe-patch-hint-produces-valid-patch", SafePatchHintProducesValidPatch),
    ("invalid-output-format-repairs-to-valid-patch", InvalidOutputFormatRepairsToValidPatch)
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
        Assert(first.Success, $"first step verifies: {first.Summary}\n{first.TestOutput}");
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

static Task NoSafeMatchDuplicateEscalatesRepair()
{
    var e = AtCoderWithTwoSteps();
    const string failedAnswer = "FILE: A.cs\nACTION: PATCH\n<<<SEARCH\nold text that is not present\nSEARCH\n<<<REPLACE\nnew text\nREPLACE\nCODER_DONE";
    const string noSafeMatch = "PATCH_MATCH_MODE: NO_SAFE_MATCH\nPATCHのSEARCHを安全に一意特定できません: A.cs (matches=0, mode=NO_SAFE_MATCH)";

    e.SetExecutionResult(noSafeMatch, false);
    Assert(e.RecordAnswer(failedAnswer), "first failed patch enters repair");
    var firstRepairPrompt = e.BuildCurrentPrompt();
    Assert(firstRepairPrompt.Contains("PREVIOUS_FAILED_CODER_ATTEMPT:") && firstRepairPrompt.Contains(failedAnswer), "repair prompt includes previous failed coder answer");
    Assert(firstRepairPrompt.Contains("同じSEARCH文字列は絶対に再利用しない"), "repair prompt forbids reusing the failed SEARCH");
    Assert(firstRepairPrompt.Contains("1〜3行程度の短い一意なSEARCH"), "repair prompt requires short exact SEARCH from latest file");
    Assert(!string.IsNullOrWhiteSpace(e.PreviousPatchSearchHash), "failed SEARCH hash recorded");

    e.SetExecutionResult(noSafeMatch, false);
    Assert(e.RecordAnswer(failedAnswer), "duplicate failed answer escalates instead of duplicate stop");
    Assert(e.State == WorkflowState.Running && e.CurrentRole == AgentRole.Coder, "duplicate repair remains running");
    Assert(e.RetryStrategy == "FULL_FILE_MODIFY_IF_COMPLETE", "duplicate selects the final bounded repair strategy");
    Assert(e.CoderRepairAttempt == 2 && e.FixAttempts == 2, "repair remains bounded by existing fix counter");
    Assert(e.BuildCurrentPrompt().Contains("FULL_FILE_MODIFY_IF_COMPLETE"), "escalated strategy is explicit in next prompt");
    e.SetExecutionResult(noSafeMatch, false);
    Assert(!e.RecordAnswer(failedAnswer), "third failed repair reaches existing fix limit");
    Assert(e.State == WorkflowState.Stopped && e.StopReason.Contains("修正回数上限"), "loop stops by MaxFixAttempts, not generic duplicate blocking");
    Assert(!e.StopReason.Contains("同一回答"), "duplicate stop reason is not used for repair loop");
    return Task.CompletedTask;
}

static async Task FailedPatchRepairsThroughReviewer()
{
    var root = TempRoot();
    try
    {
        await File.WriteAllTextAsync(Path.Combine(root, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(root, "Value.cs"), "public static class Value { public const int Number = 1; }");
        await File.WriteAllTextAsync(Path.Combine(root, "Other.cs"), "public static class Other { public const int Number = 10; }");

        var e = new OrchestrationEngine();
        e.Start("task");
        e.RecordAnswer("MANAGER_DONE");
        e.RecordAnswer("STEP: 1\nFILES: Value.cs\nTASK: update Value\nSTEP_END\nSTEP: 2\nFILES: Other.cs\nTASK: update Other\nSTEP_END\nPLAN_DONE");

        var failedAnswer = Patch("Value.cs", "Number = 999", "Number = 2");
        var failed = await WorkspaceExecutor.ApplyCoderResponseAsync(root, failedAnswer);
        Assert(!failed.Success && failed.TestOutput.Contains("PATCH_MATCH_MODE: NO_SAFE_MATCH"), "first PATCH fails with NO_SAFE_MATCH");
        Assert(failed.TestOutput.Contains("SAFE_PATCH_HINT:") && failed.TestOutput.Contains("SOURCE: CURRENT_WORKSPACE_FILE"), "executor emits a current-file exact anchor");
        e.SetExecutionResult(failed.Summary + "\n" + failed.TestOutput, false);
        Assert(e.RecordAnswer(failedAnswer), "failed patch returns to same coder step");
        e.SetCoderWorkspaceContext(await WorkspaceContextBuilder.BuildCoderAsync(root, e.CurrentImplementationStep));
        Assert(e.CoderWorkspaceContext.Contains("Number = 1"), "repair context is regenerated from current disk");
        Assert(e.RetryStrategy == "SAFE_ANCHOR_PATCH" && e.BuildCurrentPrompt().Contains("<<<EXACT_ANCHOR"), "safe anchor strategy and exact text reach repair prompt");

        const string invalidRepair = "修正方針の説明のみです。\nCODER_DONE";
        var invalid = await WorkspaceExecutor.ApplyCoderResponseAsync(root, invalidRepair);
        Assert(!invalid.Success && invalid.Summary.Contains("適用可能な"), "second attempt is classified as output format repair");
        e.SetExecutionResult(invalid.Summary, false);
        Assert(e.RecordAnswer(invalidRepair), "format failure remains in bounded repair loop");
        e.SetCoderWorkspaceContext(await WorkspaceContextBuilder.BuildCoderAsync(root, e.CurrentImplementationStep));
        Assert(e.RetryStrategy == "FULL_FILE_MODIFY_IF_COMPLETE" && e.CoderOutputFormatRepair, "second failure advances to final repair strategy");

        var repairedAnswer = Patch("Value.cs", "Number = 1", "Number = 2");
        var repaired = await WorkspaceExecutor.ApplyCoderResponseAsync(root, repairedAnswer);
        Assert(repaired.Success, $"new valid PATCH passes build/test: {repaired.Summary}\n{repaired.TestOutput}");
        var firstCommit = WorkspaceExecutor.CommitPending();
        Assert(!firstCommit.Contains("FAILED", StringComparison.Ordinal), "repaired step commits");
        e.SetExecutionResult(repaired.Summary + "\n" + repaired.TestOutput + "\n" + firstCommit, true);
        Assert(e.RecordAnswer(repairedAnswer), "repaired step advances");
        Assert(e.CurrentRole == AgentRole.Coder && e.CoderStepNumber == 2, "next coder step selected");

        var finalAnswer = Patch("Other.cs", "Number = 10", "Number = 11");
        var final = await WorkspaceExecutor.ApplyCoderResponseAsync(root, finalAnswer);
        Assert(final.Success, "final step passes build/test");
        var finalCommit = WorkspaceExecutor.CommitPending();
        Assert(!finalCommit.Contains("FAILED", StringComparison.Ordinal), "final step commits");
        e.SetExecutionResult(final.Summary + "\n" + final.TestOutput + "\n" + finalCommit, true);
        Assert(e.RecordAnswer(finalAnswer), "final coder answer accepted");
        Assert(e.CurrentRole == AgentRole.Reviewer && e.State == WorkflowState.Running, "final successful step transitions 3 -> 4");
    }
    finally
    {
        if (WorkspaceExecutor.HasPendingChanges) await WorkspaceExecutor.RollbackPendingAsync();
        Directory.Delete(root, true);
    }
}

static async Task SafePatchHintProducesValidPatch()
{
    var root = TempRoot();
    try
    {
        const string currentLine = "public static class Value { public const int Number = 1; }";
        await File.WriteAllTextAsync(Path.Combine(root, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(root, "Value.cs"), currentLine);
        var failedAnswer = Patch("Value.cs", "public static class Value { public const int Number = 999; }", "public static class Value { public const int Number = 2; }");
        var failed = await WorkspaceExecutor.ApplyCoderResponseAsync(root, failedAnswer);
        Assert(!failed.Success, "hallucinated SEARCH fails without fuzzy application");
        Assert((await File.ReadAllTextAsync(Path.Combine(root, "Value.cs"))) == currentLine, "fuzzy candidate never changes the file");
        var normalizedFeedback = failed.TestOutput.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert(normalizedFeedback.Contains($"<<<EXACT_ANCHOR\n{currentLine}\nEXACT_ANCHOR"), "hint copies exact current-file text");

        var e = AtCoderWithTwoSteps();
        e.SetExecutionResult(failed.Summary + "\n" + failed.TestOutput, false);
        Assert(e.RecordAnswer(failedAnswer), "NO_SAFE_MATCH enters repair");
        var prompt = e.BuildCurrentPrompt();
        Assert(prompt.Contains(currentLine) && prompt.Contains("文字単位でそのままSEARCH"), "exact anchor and immutable-use instruction reach Coder");
        Assert(e.SafePatchHintFile == "Value.cs" && !string.IsNullOrWhiteSpace(e.SafePatchHintHash), $"hint diagnostics state is populated: file={e.SafePatchHintFile}; hash={e.SafePatchHintHash}; hint={e.SafePatchHint}");

        var valid = await WorkspaceExecutor.ApplyCoderResponseAsync(root, Patch("Value.cs", currentLine, "public static class Value { public const int Number = 2; }"));
        Assert(valid.Success, "PATCH using exact anchor passes build/test");
        WorkspaceExecutor.CommitPending();
    }
    finally
    {
        if (WorkspaceExecutor.HasPendingChanges) await WorkspaceExecutor.RollbackPendingAsync();
        Directory.Delete(root, true);
    }
}

static async Task InvalidOutputFormatRepairsToValidPatch()
{
    var root = TempRoot();
    try
    {
        await File.WriteAllTextAsync(Path.Combine(root, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(root, "Value.cs"), "public static class Value { public const int Number = 1; }");
        var e = AtCoderWithTwoSteps();
        const string invalid = "変更方法の説明だけです。\nCODER_DONE";
        var parsed = await WorkspaceExecutor.ApplyCoderResponseAsync(root, invalid);
        Assert(!parsed.Success && parsed.Summary.Contains("適用可能な"), "invalid output has no executable block");
        e.SetExecutionResult(parsed.Summary, false);
        Assert(e.RecordAnswer(invalid), "invalid output becomes repair instead of terminal format stop");
        var prompt = e.BuildCurrentPrompt();
        Assert(e.CoderOutputFormatRepair && prompt.Contains("CODER_OUTPUT_FORMAT_REPAIR:") && prompt.Contains("true"), "format repair state reaches prompt");
        Assert(prompt.Contains("説明のみは禁止") && prompt.Contains("必ずFILE/ACTION"), "prompt requires executable syntax");

        var validAnswer = Patch("Value.cs", "Number = 1", "Number = 2");
        var valid = await WorkspaceExecutor.ApplyCoderResponseAsync(root, validAnswer);
        Assert(valid.Success, "valid FILE/ACTION/PATCH passes after format repair");
        WorkspaceExecutor.CommitPending();
        e.SetExecutionResult(valid.Summary + "\n" + valid.TestOutput, true);
        Assert(e.RecordAnswer(validAnswer) && e.CoderStepNumber == 2, "valid repair advances to next step");
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
    e.SetExecutionResult("result", true);
    e.RecordAnswer("CODER_DONE");
    e.LastExecutionSucceeded = verificationSucceeded;
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
static string TempRoot()
{
    var path = Path.Combine(Path.GetTempPath(), "AiMultiWindowTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    File.WriteAllText(Path.Combine(path, "NuGet.Config"), "<configuration><packageSources><clear /></packageSources></configuration>");
    return path;
}
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

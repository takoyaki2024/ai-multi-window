# AI Multi Window engineering guide

## Project goal

Deliver a guarded Windows WPF workflow that moves one user request through Manager, Planner, one or more verified Coder steps, and Reviewer with minimal human intervention and no paid API dependency.

## Architecture

- `MainWindow.xaml.cs`: UI workflow driver, heartbeat, per-step context refresh, workspace apply/build/test orchestration.
- `OrchestrationEngine.cs`: persisted role/step state machine, limits, repair strategies, prompts, response correlation state.
- `BrowserPane.cs` and `ChatGptWebAdapter.cs`: isolated WebView2 sessions, single physical send, accepted-turn response correlation.
- `WorkspaceContextBuilder.cs`: bounded Planner context and complete, current-step-only Coder files.
- `WorkspaceExecutor.cs`: guarded CREATE/MODIFY/PATCH transactions, exact matching, rollback and step confirmation.
- `WorkspaceVerification.cs`: isolated Release-compatible local build/test verification.
- `WorkflowDiagnostics.cs`: non-secret workflow and repair diagnostics.
- `tests/AiMultiWindow.LogicTests`: executable regression, state-machine and workspace integration tests.

## Safety and workspace rules

- Never modify outside the selected workspace or protected `.git`, `bin`, `obj`, or `.ai-multi-window` paths.
- Do not read, store, or log credentials, cookies, session data, or secrets.
- Do not add paid APIs or services.
- Preserve pending-response correlation: never send twice while a response is pending and never accept an old visible answer as the new turn.
- Coder may change only the current Planner step, normally one file and at most two files.
- Refresh Coder context from disk at every step start and repair.
- Never give partial file content to a Coder that may use MODIFY. MODIFY is allowed only when the Executor receives proof that the generated context contained that complete file.

## Coder output format

Existing files should use PATCH whenever a small safe change is possible:

```text
FILE: relative/path.ext
ACTION: PATCH
<<<SEARCH
exact current text
SEARCH
<<<REPLACE
replacement text
REPLACE
```

CREATE or authorized full-file MODIFY uses `<<<CONTENT` / `CONTENT`. End the response with `CODER_DONE`. Do not put Markdown fences inside blocks or append content after an XML/XAML root closing tag.

## PATCH safety and repair

- Apply only an exact unique match or the existing unique newline/trailing-space-normalized match.
- Fuzzy similarity may locate a hint candidate only; it must never write a file.
- A `SAFE_PATCH_HINT` anchor must be copied directly from the current file and occur exactly once.
- Repair progresses through `INITIAL_PATCH`, `SAFE_ANCHOR_PATCH`, `FULL_FILE_MODIFY_IF_COMPLETE`, then stop at existing limits.
- Malformed output uses `CODER_OUTPUT_FORMAT_REPAIR`; explanation-only responses are not executable.

## Transactions and rollback

- Each Coder step is its own transaction: apply, build/test, confirm, then advance.
- A failed step rolls back only that step. Confirmed earlier steps remain.
- Reviewer is entered only after the final step passes local verification and is confirmed.
- Reviewer PASS cannot override a failed local verification. Reviewer FAIL returns to repair without destroying confirmed steps.

## Build and verification

- Release build: `dotnet build -c Release --nologo`
- Tests: `dotnet run -c Release --project tests/AiMultiWindow.LogicTests/AiMultiWindow.LogicTests.csproj`
- Complete local V1 verification: `powershell -ExecutionPolicy Bypass -File scripts/verify-v1.ps1`

Before committing, run the complete verification command and `git diff --check`. Do not delete, skip, or weaken failing tests.

## V1 completion criteria

V1 requires: correct Manager→Planner→multi-step Coder→Reviewer→Success transitions; per-step build/test and rollback isolation; safe CREATE/PATCH/MODIFY; automatic NO_SAFE_MATCH and malformed-output repair; bounded AI/fix attempts; guarded send/response correlation; heartbeat and diagnostics; zero new build warnings; all automated tests and CI passing. Logged-in WebView behavior still requires a final user-local E2E run.

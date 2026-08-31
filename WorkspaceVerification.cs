using System.Diagnostics;
using System.Text;

namespace AiMultiWindow;

public sealed record VerificationResult(bool Success, string Output);

public interface IWorkspaceVerificationProvider
{
    Task<VerificationResult> VerifyAsync(string root, IReadOnlyList<string> changedPaths, CancellationToken cancellationToken);
}

public static class WorkspaceVerificationProviderFactory
{
    public static IWorkspaceVerificationProvider Create(string root) => new DotNetVerificationProvider();
}

public sealed class DotNetVerificationProvider : IWorkspaceVerificationProvider
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    public async Task<VerificationResult> VerifyAsync(string root, IReadOnlyList<string> changedPaths, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var artifactsPath = Path.Combine(Path.GetTempPath(), "AiMultiWindowVerify", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactsPath);

        try
        {
            // Self-modification can target the currently running AiMultiWindow binaries.
            // Build into an isolated artifacts directory so verification never replaces/locks
            // the executable or assemblies that are hosting this workflow.
            var build = await RunDotNetAsync(root, ["build", "--nologo", "--artifacts-path", artifactsPath], cancellationToken);
            output.AppendLine("BUILD:").AppendLine($"EXIT: {build.ExitCode}").AppendLine(build.Output);
            if (build.ExitCode != 0) return new(false, output.ToString().Trim());

            var testProjects = Directory.EnumerateFiles(root, "*.*proj", SearchOption.AllDirectories)
                .Where(p => !IsGenerated(p) && IsTestProject(p)).ToList();
            if (testProjects.Count == 0)
            {
                output.AppendLine("TEST:").AppendLine("SKIPPED: no test project detected");
            }
            else
            {
                foreach (var project in testProjects)
                {
                    var testArtifacts = Path.Combine(artifactsPath, "tests", Path.GetFileNameWithoutExtension(project));
                    var test = await RunDotNetAsync(root, ["test", project, "--nologo", "--artifacts-path", testArtifacts], cancellationToken);
                    output.AppendLine("TEST:").AppendLine($"PROJECT: {Path.GetRelativePath(root, project)}")
                        .AppendLine($"EXIT: {test.ExitCode}").AppendLine(test.Output);
                    if (test.ExitCode != 0) return new(false, output.ToString().Trim());
                }
            }

            var diff = await RunProcessAsync(root, "git", ["diff", "--no-ext-diff", "--", .. changedPaths], cancellationToken, TimeSpan.FromSeconds(30));
            output.AppendLine("GIT_DIFF:").AppendLine(diff.ExitCode == 0 ? diff.Output : $"UNAVAILABLE: {diff.Output}");
            return new(true, output.ToString().Trim());
        }
        finally
        {
            try { if (Directory.Exists(artifactsPath)) Directory.Delete(artifactsPath, true); } catch { }
        }
    }

    private static bool IsGenerated(string path) => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}.ai-multi-window{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static bool IsTestProject(string path)
    {
        try
        {
            var project = File.ReadAllText(path);
            return project.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
                || project.Contains("<IsTestProject>true</IsTestProject>", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static Task<(int ExitCode, string Output)> RunDotNetAsync(string root, IReadOnlyList<string> args, CancellationToken ct) =>
        RunProcessAsync(root, "dotnet", args, ct, CommandTimeout);

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string root, string fileName, IReadOnlyList<string> args, CancellationToken ct, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo { FileName = fileName, WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi);
        if (process is null) return (-1, $"Could not start {fileName}.");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        try { await process.WaitForExitAsync(timeoutCts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(true); } catch { }
            return (-2, $"{fileName} timed out after {timeout}.");
        }
        var output = (await stdout) + Environment.NewLine + (await stderr);
        if (output.Length > 30_000) output = output[^30_000..];
        return (process.ExitCode, output.Trim());
    }
}

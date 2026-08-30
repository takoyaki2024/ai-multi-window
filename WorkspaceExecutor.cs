using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AiMultiWindow;

public sealed record WorkspaceExecutionResult(bool Success, string Summary, string TestOutput);

public static class WorkspaceExecutor
{
    private const int MaxFiles = 12;
    private const int MaxContentChars = 250_000;

    public static async Task<WorkspaceExecutionResult> ApplyCoderResponseAsync(
        string workspaceRoot,
        string coderResponse,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            return new(false, "Workspaceが存在しません。", string.Empty);

        var root = Path.GetFullPath(workspaceRoot);
        var changes = ParseChanges(coderResponse).Take(MaxFiles + 1).ToList();
        if (changes.Count == 0)
            return new(false, "Coder回答に適用可能な FILE/ACTION/CONTENT がありません。", string.Empty);
        if (changes.Count > MaxFiles)
            return new(false, $"変更ファイル数が上限 {MaxFiles} を超えています。", string.Empty);

        foreach (var change in changes)
        {
            if (change.Content.Length > MaxContentChars)
                return new(false, $"{change.Path}: 内容が大きすぎます。", string.Empty);

            var target = SafePath(root, change.Path);
            if (target is null)
                return new(false, $"Workspace外への変更を拒否しました: {change.Path}", string.Empty);

            if (IsProtectedPath(root, target))
                return new(false, $"保護対象への変更を拒否しました: {change.Path}", string.Empty);

            switch (change.Action)
            {
                case "CREATE":
                    if (File.Exists(target))
                        return new(false, $"CREATE対象が既に存在します: {change.Path}", string.Empty);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await File.WriteAllTextAsync(target, change.Content, Encoding.UTF8, cancellationToken);
                    break;
                case "MODIFY":
                    if (!File.Exists(target))
                        return new(false, $"MODIFY対象が存在しません: {change.Path}", string.Empty);
                    await File.WriteAllTextAsync(target, change.Content, Encoding.UTF8, cancellationToken);
                    break;
                default:
                    return new(false, $"未対応ACTIONです: {change.Action}", string.Empty);
            }
        }

        var test = await RunBuildAsync(root, cancellationToken);
        return new(test.ExitCode == 0,
            $"{changes.Count}ファイルを適用。dotnet build exit={test.ExitCode}",
            test.Output);
    }

    private static IEnumerable<FileChange> ParseChanges(string text)
    {
        var pattern = new Regex(
            @"(?ms)^FILE:\s*(?<path>[^\r\n]+)\s*\r?\nACTION:\s*(?<action>CREATE|MODIFY)\s*\r?\n<<<CONTENT\s*\r?\n(?<content>.*?)\r?\nCONTENT(?:\r?\n|$)",
            RegexOptions.CultureInvariant);

        foreach (Match match in pattern.Matches(text ?? string.Empty))
        {
            yield return new FileChange(
                match.Groups["path"].Value.Trim(),
                match.Groups["action"].Value.Trim().ToUpperInvariant(),
                match.Groups["content"].Value);
        }
    }

    private static string? SafePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            return null;

        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static bool IsProtectedPath(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        return relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)
            || relative.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int ExitCode, string Output)> RunBuildAsync(string root, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "build --nologo",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process is null)
            return (-1, "dotnet build を開始できませんでした。");

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await stdout) + Environment.NewLine + (await stderr);
        if (output.Length > 20_000)
            output = output[^20_000..];
        return (process.ExitCode, output.Trim());
    }

    private sealed record FileChange(string Path, string Action, string Content);
}

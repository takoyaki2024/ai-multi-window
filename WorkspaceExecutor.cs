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

        var verification = new StringBuilder();

        foreach (var change in changes)
        {
            if (change.Content.Length > MaxContentChars)
                return new(false, $"{change.Path}: 内容が大きすぎます。", verification.ToString());

            var target = SafePath(root, change.Path);
            if (target is null)
                return new(false, $"Workspace外への変更を拒否しました: {change.Path}", verification.ToString());

            if (IsProtectedPath(root, target))
                return new(false, $"保護対象への変更を拒否しました: {change.Path}", verification.ToString());

            var existedBefore = File.Exists(target);
            verification.AppendLine($"FILE: {change.Path}");
            verification.AppendLine($"ACTION: {change.Action}");
            verification.AppendLine($"PRECHECK_EXISTS: {existedBefore.ToString().ToLowerInvariant()}");

            switch (change.Action)
            {
                case "CREATE":
                    if (existedBefore)
                    {
                        verification.AppendLine("PRECHECK_RESULT: FAIL (CREATE対象が既に存在)");
                        return new(false, $"CREATE対象が既に存在します: {change.Path}", verification.ToString());
                    }
                    verification.AppendLine("PRECHECK_RESULT: PASS (未存在を確認)");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await File.WriteAllTextAsync(target, change.Content, Encoding.UTF8, cancellationToken);
                    break;

                case "MODIFY":
                    if (!existedBefore)
                    {
                        verification.AppendLine("PRECHECK_RESULT: FAIL (MODIFY対象が存在しない)");
                        return new(false, $"MODIFY対象が存在しません: {change.Path}", verification.ToString());
                    }
                    verification.AppendLine("PRECHECK_RESULT: PASS (存在を確認)");
                    await File.WriteAllTextAsync(target, change.Content, Encoding.UTF8, cancellationToken);
                    break;

                default:
                    return new(false, $"未対応ACTIONです: {change.Action}", verification.ToString());
            }

            var existsAfter = File.Exists(target);
            verification.AppendLine($"POSTCHECK_EXISTS: {existsAfter.ToString().ToLowerInvariant()}");
            if (!existsAfter)
            {
                verification.AppendLine("POSTCHECK_RESULT: FAIL (書き込み後にファイルが存在しない)");
                return new(false, $"書き込み後の存在確認に失敗しました: {change.Path}", verification.ToString());
            }

            var readBack = await File.ReadAllTextAsync(target, Encoding.UTF8, cancellationToken);
            var exactMatch = string.Equals(readBack, change.Content, StringComparison.Ordinal);
            verification.AppendLine($"CONTENT_EXACT_MATCH: {exactMatch.ToString().ToLowerInvariant()}");
            verification.AppendLine($"EXPECTED_LENGTH: {change.Content.Length}");
            verification.AppendLine($"ACTUAL_LENGTH: {readBack.Length}");

            if (!exactMatch)
            {
                verification.AppendLine("POSTCHECK_RESULT: FAIL (内容不一致)");
                return new(false, $"書き込み後の内容検証に失敗しました: {change.Path}", verification.ToString());
            }

            verification.AppendLine("POSTCHECK_RESULT: PASS (存在・内容完全一致)");
            verification.AppendLine();
        }

        var test = await RunBuildAsync(root, cancellationToken);
        verification.AppendLine("BUILD_TEST:");
        verification.AppendLine($"DOTNET_BUILD_EXIT: {test.ExitCode}");
        verification.AppendLine(test.Output);

        return new(test.ExitCode == 0,
            $"{changes.Count}ファイルを適用・読み返し検証済み。dotnet build exit={test.ExitCode}",
            verification.ToString().Trim());
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

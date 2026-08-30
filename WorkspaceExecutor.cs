using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AiMultiWindow;

public sealed record WorkspaceExecutionResult(bool Success, string Summary, string TestOutput);

public static class WorkspaceExecutor
{
    private const int MaxFiles = 12;
    private const int MaxContentChars = 250_000;
    private static readonly List<FileSnapshot> PendingSnapshots = new();
    private static string? PendingRoot;

    public static bool HasPendingChanges => PendingSnapshots.Count > 0;

    public static async Task<WorkspaceExecutionResult> ApplyCoderResponseAsync(
        string workspaceRoot,
        string coderResponse,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            return new(false, "Workspaceが存在しません。", string.Empty);

        if (HasPendingChanges)
            return new(false, "前回の変更が未確定です。Reviewer判定後に再実行してください。", string.Empty);

        var root = Path.GetFullPath(workspaceRoot);
        var changes = ParseChanges(coderResponse).Take(MaxFiles + 1).ToList();
        if (changes.Count == 0)
            return new(false, "Coder回答に適用可能な FILE/ACTION/CONTENT がありません。", string.Empty);
        if (changes.Count > MaxFiles)
            return new(false, $"変更ファイル数が上限 {MaxFiles} を超えています。", string.Empty);

        var verification = new StringBuilder();
        PendingRoot = root;
        var satisfiedWithoutWrite = 0;

        try
        {
            foreach (var change in changes)
            {
                if (change.Content.Length > MaxContentChars)
                    return await FailAndRollbackAsync($"{change.Path}: 内容が大きすぎます。", verification.ToString());

                var target = SafePath(root, change.Path);
                if (target is null)
                    return await FailAndRollbackAsync($"Workspace外への変更を拒否しました: {change.Path}", verification.ToString());

                if (IsProtectedPath(root, target))
                    return await FailAndRollbackAsync($"保護対象への変更を拒否しました: {change.Path}", verification.ToString());

                var existedBefore = File.Exists(target);
                verification.AppendLine($"FILE: {change.Path}");
                verification.AppendLine($"ACTION: {change.Action}");
                verification.AppendLine($"PRECHECK_EXISTS: {existedBefore.ToString().ToLowerInvariant()}");

                switch (change.Action)
                {
                    case "CREATE":
                        if (existedBefore)
                        {
                            var existingContent = await File.ReadAllTextAsync(target, Encoding.UTF8, cancellationToken);
                            var alreadyMatches = string.Equals(existingContent, change.Content, StringComparison.Ordinal);
                            verification.AppendLine($"PRECHECK_EXISTING_CONTENT_MATCH: {alreadyMatches.ToString().ToLowerInvariant()}");

                            if (!alreadyMatches)
                            {
                                verification.AppendLine("PRECHECK_RESULT: FAIL (CREATE対象が既に存在し内容が異なる。上書き禁止)");
                                return await FailAndRollbackAsync($"CREATE対象が既に存在し内容が異なります: {change.Path}", verification.ToString());
                            }

                            verification.AppendLine("PRECHECK_RESULT: PASS_ALREADY_SATISFIED (既存ファイルが要求内容と完全一致。上書きなし)");
                            verification.AppendLine("WRITE_PERFORMED: false");
                            verification.AppendLine("POSTCHECK_EXISTS: true");
                            verification.AppendLine("CONTENT_EXACT_MATCH: true");
                            verification.AppendLine($"EXPECTED_LENGTH: {change.Content.Length}");
                            verification.AppendLine($"ACTUAL_LENGTH: {existingContent.Length}");
                            verification.AppendLine("POSTCHECK_RESULT: PASS_ALREADY_SATISFIED");
                            verification.AppendLine();
                            satisfiedWithoutWrite++;
                            continue;
                        }

                        verification.AppendLine("PRECHECK_RESULT: PASS (未存在を確認)");
                        PendingSnapshots.Add(new FileSnapshot(target, false, null));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        await File.WriteAllTextAsync(target, change.Content, Encoding.UTF8, cancellationToken);
                        verification.AppendLine("WRITE_PERFORMED: true");
                        break;

                    case "MODIFY":
                        if (!existedBefore)
                        {
                            verification.AppendLine("PRECHECK_RESULT: FAIL (MODIFY対象が存在しない)");
                            return await FailAndRollbackAsync($"MODIFY対象が存在しません: {change.Path}", verification.ToString());
                        }
                        verification.AppendLine("PRECHECK_RESULT: PASS (存在を確認)");
                        var original = await File.ReadAllTextAsync(target, Encoding.UTF8, cancellationToken);
                        if (string.Equals(original, change.Content, StringComparison.Ordinal))
                        {
                            verification.AppendLine("WRITE_PERFORMED: false");
                            verification.AppendLine("POSTCHECK_EXISTS: true");
                            verification.AppendLine("CONTENT_EXACT_MATCH: true");
                            verification.AppendLine($"EXPECTED_LENGTH: {change.Content.Length}");
                            verification.AppendLine($"ACTUAL_LENGTH: {original.Length}");
                            verification.AppendLine("POSTCHECK_RESULT: PASS_ALREADY_SATISFIED");
                            verification.AppendLine();
                            satisfiedWithoutWrite++;
                            continue;
                        }

                        PendingSnapshots.Add(new FileSnapshot(target, true, original));
                        await File.WriteAllTextAsync(target, change.Content, Encoding.UTF8, cancellationToken);
                        verification.AppendLine("WRITE_PERFORMED: true");
                        break;

                    default:
                        return await FailAndRollbackAsync($"未対応ACTIONです: {change.Action}", verification.ToString());
                }

                var existsAfter = File.Exists(target);
                verification.AppendLine($"POSTCHECK_EXISTS: {existsAfter.ToString().ToLowerInvariant()}");
                if (!existsAfter)
                {
                    verification.AppendLine("POSTCHECK_RESULT: FAIL (書き込み後にファイルが存在しない)");
                    return await FailAndRollbackAsync($"書き込み後の存在確認に失敗しました: {change.Path}", verification.ToString());
                }

                var readBack = await File.ReadAllTextAsync(target, Encoding.UTF8, cancellationToken);
                var exactMatch = string.Equals(readBack, change.Content, StringComparison.Ordinal);
                verification.AppendLine($"CONTENT_EXACT_MATCH: {exactMatch.ToString().ToLowerInvariant()}");
                verification.AppendLine($"EXPECTED_LENGTH: {change.Content.Length}");
                verification.AppendLine($"ACTUAL_LENGTH: {readBack.Length}");

                if (!exactMatch)
                {
                    verification.AppendLine("POSTCHECK_RESULT: FAIL (内容不一致)");
                    return await FailAndRollbackAsync($"書き込み後の内容検証に失敗しました: {change.Path}", verification.ToString());
                }

                verification.AppendLine("POSTCHECK_RESULT: PASS (存在・内容完全一致)");
                verification.AppendLine();
            }

            var test = await RunBuildAsync(root, cancellationToken);
            verification.AppendLine("BUILD_TEST:");
            verification.AppendLine($"DOTNET_BUILD_EXIT: {test.ExitCode}");
            verification.AppendLine(test.Output);

            if (test.ExitCode != 0)
                return await FailAndRollbackAsync($"dotnet build に失敗しました。exit={test.ExitCode}", verification.ToString());

            var changedCount = PendingSnapshots.Count;
            return new(true,
                $"要求状態を検証済み。実変更 {changedCount}ファイル、既に一致 {satisfiedWithoutWrite}ファイル。Reviewer判定待ち。dotnet build exit=0",
                verification.ToString().Trim());
        }
        catch (Exception ex)
        {
            return await FailAndRollbackAsync($"Workspace適用中に例外: {ex.GetType().Name}: {ex.Message}", verification.ToString());
        }
    }

    public static async Task<string> RollbackPendingAsync()
    {
        if (!HasPendingChanges)
            return "ROLLBACK: 対象なし";

        var log = new StringBuilder();
        for (var i = PendingSnapshots.Count - 1; i >= 0; i--)
        {
            var snapshot = PendingSnapshots[i];
            try
            {
                if (snapshot.ExistedBefore)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(snapshot.Path)!);
                    await File.WriteAllTextAsync(snapshot.Path, snapshot.OriginalContent ?? string.Empty, Encoding.UTF8);
                    log.AppendLine($"ROLLBACK_RESTORED: {RelativePendingPath(snapshot.Path)}");
                }
                else if (File.Exists(snapshot.Path))
                {
                    File.Delete(snapshot.Path);
                    log.AppendLine($"ROLLBACK_DELETED: {RelativePendingPath(snapshot.Path)}");
                }
            }
            catch (Exception ex)
            {
                log.AppendLine($"ROLLBACK_FAIL: {RelativePendingPath(snapshot.Path)}: {ex.Message}");
            }
        }

        ClearPending();
        return log.Length == 0 ? "ROLLBACK: 完了" : log.ToString().Trim();
    }

    public static string CommitPending()
    {
        var count = PendingSnapshots.Count;
        ClearPending();
        return $"COMMIT_LOCAL_CHANGES: {count}ファイルを確定";
    }

    private static async Task<WorkspaceExecutionResult> FailAndRollbackAsync(string summary, string output)
    {
        var rollback = await RollbackPendingAsync();
        var combined = string.IsNullOrWhiteSpace(output) ? rollback : output.TrimEnd() + Environment.NewLine + rollback;
        return new(false, summary, combined);
    }

    private static void ClearPending()
    {
        PendingSnapshots.Clear();
        PendingRoot = null;
    }

    private static string RelativePendingPath(string path)
    {
        if (string.IsNullOrWhiteSpace(PendingRoot)) return path;
        return Path.GetRelativePath(PendingRoot, path).Replace('\\', '/');
    }

    private static IEnumerable<FileChange> ParseChanges(string text)
    {
        var pattern = new Regex(
            @"(?ms)^FILE:\s*(?<path>[^\r\n]+)\s*\r?\nACTION:\s*(?<action>CREATE|MODIFY)\s*\r?\n<<<CONTENT\s*\r?\n(?<content>.*?)\r?\nCONTENT(?:\r?\n|$)",
            RegexOptions.CultureInvariant);

        foreach (Match match in pattern.Matches(text ?? string.Empty))
            yield return new FileChange(match.Groups["path"].Value.Trim(), match.Groups["action"].Value.Trim().ToUpperInvariant(), match.Groups["content"].Value);
    }

    private static string? SafePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return null;
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
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
        if (process is null) return (-1, "dotnet build を開始できませんでした。");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await stdout) + Environment.NewLine + (await stderr);
        if (output.Length > 20_000) output = output[^20_000..];
        return (process.ExitCode, output.Trim());
    }

    private sealed record FileChange(string Path, string Action, string Content);
    private sealed record FileSnapshot(string Path, bool ExistedBefore, string? OriginalContent);
}

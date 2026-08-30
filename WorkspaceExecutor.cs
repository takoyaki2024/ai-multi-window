using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiMultiWindow;

public sealed record WorkspaceExecutionResult(bool Success, string Summary, string TestOutput);

public static class WorkspaceExecutor
{
    private const int MaxFiles = 12;
    private const int MaxContentChars = 250_000;
    private static readonly List<FileSnapshot> PendingSnapshots = new();
    private static string? PendingRoot;
    private const string TransactionDirectoryName = ".ai-multi-window";
    private const string TransactionFileName = "rollback.json";

    public static bool HasPendingChanges => PendingSnapshots.Count > 0;

    public static async Task<bool> RecoverPendingAsync(string workspaceRoot)
    {
        if (HasPendingChanges) return true;
        var root = Path.GetFullPath(workspaceRoot);
        var path = TransactionPath(root);
        if (!File.Exists(path)) return false;
        var stored = JsonSerializer.Deserialize<List<PersistedSnapshot>>(await File.ReadAllTextAsync(path, Encoding.UTF8)) ?? [];
        PendingRoot = root;
        foreach (var item in stored)
        {
            var full = SafePath(root, item.RelativePath);
            if (full is null || IsProtectedPath(root, full)) throw new InvalidDataException("Invalid rollback transaction path.");
            PendingSnapshots.Add(new FileSnapshot(full, item.ExistedBefore, item.OriginalContent));
        }
        return HasPendingChanges;
    }

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
            return new(false, "Coder回答に適用可能な FILE/ACTION/CONTENT または PATCH がありません。", string.Empty);
        if (changes.Count > MaxFiles)
            return new(false, $"変更ファイル数が上限 {MaxFiles} を超えています。", string.Empty);

        var validationError = ValidateChangeSet(root, changes);
        if (validationError is not null) return new(false, validationError, string.Empty);

        var verification = new StringBuilder();
        PendingRoot = root;
        var satisfiedWithoutWrite = 0;

        try
        {
            foreach (var change in changes)
            {
                var payloadLength = (change.Content?.Length ?? 0) + (change.Search?.Length ?? 0) + (change.Replace?.Length ?? 0);
                if (payloadLength > MaxContentChars)
                    return await FailAndRollbackAsync($"{change.Path}: 変更内容が大きすぎます。", verification.ToString());

                var target = SafePath(root, change.Path);
                if (target is null)
                    return await FailAndRollbackAsync($"Workspace外への変更を拒否しました: {change.Path}", verification.ToString());

                if (IsProtectedPath(root, target))
                    return await FailAndRollbackAsync($"保護対象への変更を拒否しました: {change.Path}", verification.ToString());

                var existedBefore = File.Exists(target);
                verification.AppendLine($"FILE: {change.Path}");
                verification.AppendLine($"ACTION: {change.Action}");
                verification.AppendLine($"PRECHECK_EXISTS: {existedBefore.ToString().ToLowerInvariant()}");

                string expectedContent;

                switch (change.Action)
                {
                    case "CREATE":
                    {
                        expectedContent = change.Content ?? string.Empty;
                        if (existedBefore)
                        {
                            var existingContent = await File.ReadAllTextAsync(target, Encoding.UTF8, cancellationToken);
                            var alreadyMatches = string.Equals(existingContent, expectedContent, StringComparison.Ordinal);
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
                            verification.AppendLine($"EXPECTED_LENGTH: {expectedContent.Length}");
                            verification.AppendLine($"ACTUAL_LENGTH: {existingContent.Length}");
                            verification.AppendLine("POSTCHECK_RESULT: PASS_ALREADY_SATISFIED");
                            verification.AppendLine();
                            satisfiedWithoutWrite++;
                            continue;
                        }

                        verification.AppendLine("PRECHECK_RESULT: PASS (未存在を確認)");
                        PendingSnapshots.Add(new FileSnapshot(target, false, null));
                        await PersistPendingAsync();
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        await File.WriteAllTextAsync(target, expectedContent, Encoding.UTF8, cancellationToken);
                        verification.AppendLine("WRITE_PERFORMED: true");
                        break;
                    }

                    case "MODIFY":
                    {
                        expectedContent = change.Content ?? string.Empty;
                        if (!existedBefore)
                        {
                            verification.AppendLine("PRECHECK_RESULT: FAIL (MODIFY対象が存在しない)");
                            return await FailAndRollbackAsync($"MODIFY対象が存在しません: {change.Path}", verification.ToString());
                        }

                        verification.AppendLine("PRECHECK_RESULT: PASS (存在を確認)");
                        var original = await File.ReadAllTextAsync(target, Encoding.UTF8, cancellationToken);
                        if (string.Equals(original, expectedContent, StringComparison.Ordinal))
                        {
                            verification.AppendLine("WRITE_PERFORMED: false");
                            verification.AppendLine("POSTCHECK_EXISTS: true");
                            verification.AppendLine("CONTENT_EXACT_MATCH: true");
                            verification.AppendLine($"EXPECTED_LENGTH: {expectedContent.Length}");
                            verification.AppendLine($"ACTUAL_LENGTH: {original.Length}");
                            verification.AppendLine("POSTCHECK_RESULT: PASS_ALREADY_SATISFIED");
                            verification.AppendLine();
                            satisfiedWithoutWrite++;
                            continue;
                        }

                        PendingSnapshots.Add(new FileSnapshot(target, true, original));
                        await PersistPendingAsync();
                        await File.WriteAllTextAsync(target, expectedContent, Encoding.UTF8, cancellationToken);
                        verification.AppendLine("WRITE_PERFORMED: true");
                        break;
                    }

                    case "PATCH":
                    {
                        if (!existedBefore)
                        {
                            verification.AppendLine("PRECHECK_RESULT: FAIL (PATCH対象が存在しない)");
                            return await FailAndRollbackAsync($"PATCH対象が存在しません: {change.Path}", verification.ToString());
                        }

                        var search = change.Search ?? string.Empty;
                        var replace = change.Replace ?? string.Empty;
                        if (search.Length == 0)
                        {
                            verification.AppendLine("PRECHECK_RESULT: FAIL (SEARCHが空)");
                            return await FailAndRollbackAsync($"PATCHのSEARCHが空です: {change.Path}", verification.ToString());
                        }

                        var original = await File.ReadAllTextAsync(target, Encoding.UTF8, cancellationToken);
                        var matches = CountExactOccurrences(original, search);
                        verification.AppendLine($"PATCH_SEARCH_MATCHES: {matches}");
                        verification.AppendLine($"PATCH_SEARCH_LENGTH: {search.Length}");
                        verification.AppendLine($"PATCH_REPLACE_LENGTH: {replace.Length}");

                        if (matches != 1)
                        {
                            verification.AppendLine("PRECHECK_RESULT: FAIL (SEARCHは既存ファイル内で完全一致1件である必要があります)");
                            return await FailAndRollbackAsync($"PATCHのSEARCH一致数が1ではありません: {change.Path} (matches={matches})", verification.ToString());
                        }

                        expectedContent = original.Replace(search, replace, StringComparison.Ordinal);
                        if (string.Equals(original, expectedContent, StringComparison.Ordinal))
                        {
                            verification.AppendLine("PRECHECK_RESULT: PASS_ALREADY_SATISFIED");
                            verification.AppendLine("WRITE_PERFORMED: false");
                            verification.AppendLine();
                            satisfiedWithoutWrite++;
                            continue;
                        }

                        verification.AppendLine("PRECHECK_RESULT: PASS (SEARCH完全一致1件)");
                        PendingSnapshots.Add(new FileSnapshot(target, true, original));
                        await PersistPendingAsync();
                        await File.WriteAllTextAsync(target, expectedContent, Encoding.UTF8, cancellationToken);
                        verification.AppendLine("WRITE_PERFORMED: true");
                        break;
                    }

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
                var exactMatch = string.Equals(readBack, expectedContent, StringComparison.Ordinal);
                verification.AppendLine($"CONTENT_EXACT_MATCH: {exactMatch.ToString().ToLowerInvariant()}");
                verification.AppendLine($"EXPECTED_LENGTH: {expectedContent.Length}");
                verification.AppendLine($"ACTUAL_LENGTH: {readBack.Length}");

                if (!exactMatch)
                {
                    verification.AppendLine("POSTCHECK_RESULT: FAIL (内容不一致)");
                    return await FailAndRollbackAsync($"書き込み後の内容検証に失敗しました: {change.Path}", verification.ToString());
                }

                verification.AppendLine("POSTCHECK_RESULT: PASS (存在・内容完全一致)");
                verification.AppendLine();
            }

            var provider = WorkspaceVerificationProviderFactory.Create(root);
            var changedPaths = changes.Select(c => c.Path.Replace('\\', '/')).ToList();
            var test = await provider.VerifyAsync(root, changedPaths, cancellationToken);
            verification.AppendLine("LOCAL_VERIFICATION:").AppendLine(test.Output);
            if (!test.Success) return await FailAndRollbackAsync("ローカルbuild/testに失敗しました。", verification.ToString());

            var changedCount = PendingSnapshots.Count;
            return new(true,
                $"要求状態を検証済み。実変更 {changedCount}ファイル、既に一致 {satisfiedWithoutWrite}ファイル。Reviewer判定待ち。build/test成功",
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
        var failed = new List<FileSnapshot>();
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
                failed.Add(snapshot);
            }
        }

        if (failed.Count == 0) ClearPending();
        else
        {
            PendingSnapshots.Clear();
            PendingSnapshots.AddRange(failed);
            await PersistPendingAsync();
        }
        return log.Length == 0 ? "ROLLBACK: 完了" : log.ToString().Trim();
    }

    public static string CommitPending()
    {
        var count = PendingSnapshots.Count;
        if (!TryDeleteTransaction()) return "COMMIT_LOCAL_CHANGES_FAILED: rollback記録を削除できないため確定状態にできません";
        PendingSnapshots.Clear();
        PendingRoot = null;
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
        TryDeleteTransaction();
        PendingSnapshots.Clear();
        PendingRoot = null;
    }

    private static bool TryDeleteTransaction()
    {
        if (string.IsNullOrWhiteSpace(PendingRoot)) return true;
        try
        {
            var directory = Path.Combine(PendingRoot, TransactionDirectoryName);
            var transaction = Path.Combine(directory, TransactionFileName);
            if (File.Exists(transaction)) File.Delete(transaction);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            return !File.Exists(transaction);
        }
        catch { return false; }
    }

    private static string RelativePendingPath(string path)
    {
        if (string.IsNullOrWhiteSpace(PendingRoot)) return path;
        return Path.GetRelativePath(PendingRoot, path).Replace('\\', '/');
    }

    private static IEnumerable<FileChange> ParseChanges(string text)
    {
        text ??= string.Empty;
        var parsed = new List<(int Index, FileChange Change)>();

        var contentPattern = new Regex(
            @"(?ms)^FILE:\s*(?<path>[^\r\n]+)\s*\r?\nACTION:\s*(?<action>CREATE|MODIFY)\s*\r?\n<<<CONTENT\s*\r?\n(?<content>.*?)\r?\nCONTENT(?:\r?\n|$)",
            RegexOptions.CultureInvariant);

        foreach (Match match in contentPattern.Matches(text))
        {
            parsed.Add((match.Index, new FileChange(
                match.Groups["path"].Value.Trim(),
                match.Groups["action"].Value.Trim().ToUpperInvariant(),
                match.Groups["content"].Value,
                null,
                null)));
        }

        var patchPattern = new Regex(
            @"(?ms)^FILE:\s*(?<path>[^\r\n]+)\s*\r?\nACTION:\s*PATCH\s*\r?\n<<<SEARCH\s*\r?\n(?<search>.*?)\r?\nSEARCH\s*\r?\n<<<REPLACE\s*\r?\n(?<replace>.*?)\r?\nREPLACE(?:\r?\n|$)",
            RegexOptions.CultureInvariant);

        foreach (Match match in patchPattern.Matches(text))
        {
            parsed.Add((match.Index, new FileChange(
                match.Groups["path"].Value.Trim(),
                "PATCH",
                null,
                match.Groups["search"].Value,
                match.Groups["replace"].Value)));
        }

        foreach (var item in parsed.OrderBy(x => x.Index))
            yield return item.Change;
    }

    private static int CountExactOccurrences(string source, string search)
    {
        var count = 0;
        var index = 0;
        while (index <= source.Length - search.Length)
        {
            var found = source.IndexOf(search, index, StringComparison.Ordinal);
            if (found < 0) break;
            count++;
            index = found + search.Length;
        }
        return count;
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
            || relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(TransactionDirectoryName + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ValidateChangeSet(string root, IReadOnlyList<FileChange> changes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            var target = SafePath(root, change.Path);
            if (target is null || IsProtectedPath(root, target)) return $"不正または保護対象のパスです: {change.Path}";
            if (!seen.Add(target)) return $"同一ファイルが複数回指定されています: {change.Path}";
            if (HasReparsePointBetween(root, target)) return $"reparse point経由の変更を拒否しました: {change.Path}";
            if (change.Action == "CREATE" && File.Exists(target) && !string.Equals(File.ReadAllText(target), change.Content, StringComparison.Ordinal)) return $"CREATE対象が既に存在します: {change.Path}";
            if ((change.Action == "MODIFY" || change.Action == "PATCH") && !File.Exists(target)) return $"{change.Action}対象が存在しません: {change.Path}";
            if (change.Action == "PATCH" && string.IsNullOrEmpty(change.Search)) return $"PATCHのSEARCHが空です: {change.Path}";
        }
        return null;
    }

    private static bool HasReparsePointBetween(string root, string target)
    {
        var current = File.Exists(target) ? target : Path.GetDirectoryName(target);
        while (!string.IsNullOrWhiteSpace(current) && !string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(current) || Directory.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            current = Path.GetDirectoryName(current);
        }
        return false;
    }

    private static string TransactionPath(string root) => Path.Combine(root, TransactionDirectoryName, TransactionFileName);

    private static async Task PersistPendingAsync()
    {
        if (string.IsNullOrWhiteSpace(PendingRoot)) return;
        var path = TransactionPath(PendingRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stored = PendingSnapshots.Select(s => new PersistedSnapshot(Path.GetRelativePath(PendingRoot, s.Path), s.ExistedBefore, s.OriginalContent)).ToList();
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(stored), Encoding.UTF8);
    }

    private sealed record FileChange(string Path, string Action, string? Content, string? Search, string? Replace);
    private sealed record FileSnapshot(string Path, bool ExistedBefore, string? OriginalContent);
    private sealed record PersistedSnapshot(string RelativePath, bool ExistedBefore, string? OriginalContent);
}

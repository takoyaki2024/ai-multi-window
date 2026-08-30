using System.Text;

namespace AiMultiWindow;

public static class WorkspaceContextBuilder
{
    private const int MaxContextChars = 14_000;
    private const int MaxSingleFileChars = 18_000;
    private const int MaxFiles = 40;
    private const int MaxCoderFiles = 4;
    private const int MaxCoderContextChars = 24_000;
    private const int MaxCoderSingleFileChars = 80_000;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".csproj", ".props", ".targets", ".json", ".xml",
        ".md", ".ps1", ".bat", ".cmd", ".yml", ".yaml"
    };

    public static Task<string> BuildAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
        BuildPlannerAsync(workspaceRoot, cancellationToken);

    public static async Task<string> BuildPlannerAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            return "WORKSPACE_CONTEXT_ERROR: Workspaceが存在しません。";

        var root = Path.GetFullPath(workspaceRoot);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !IsIgnored(root, path))
            .Select(path => new FileInfo(path))
            .Where(info => TextExtensions.Contains(info.Extension))
            .OrderBy(info => Priority(root, info))
            .ThenBy(info => Path.GetRelativePath(root, info.FullName), StringComparer.OrdinalIgnoreCase)
            .Take(MaxFiles)
            .ToList();

        var output = new StringBuilder();
        output.AppendLine("WORKSPACE_FILE_LIST:");
        foreach (var file in files)
            output.AppendLine(Path.GetRelativePath(root, file.FullName).Replace('\\', '/'));

        output.AppendLine();
        output.AppendLine("WORKSPACE_OVERVIEW:");

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output.Length >= MaxContextChars) break;

            string content;
            try
            {
                content = await File.ReadAllTextAsync(file.FullName, Encoding.UTF8, cancellationToken);
            }
            catch
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
            if (content.Length > MaxSingleFileChars)
            {
                output.AppendLine($"OMITTED_LARGE_FILE: {relative} ({content.Length} chars)");
                continue;
            }

            var block = $"\n===== FILE: {relative} =====\n{content}\n===== END FILE =====\n";
            var remaining = MaxContextChars - output.Length;
            if (block.Length > remaining)
            {
                output.AppendLine($"OMITTED_CONTEXT_BUDGET: {relative} ({content.Length} chars). Content is not partial.");
                continue;
            }

            output.Append(block);
        }

        output.AppendLine();
        output.AppendLine($"WORKSPACE_CONTEXT_CHARS: {output.Length}");
        return output.ToString();
    }

    public static async Task<string> BuildCoderAsync(string workspaceRoot, string plannerAnswer, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            return "WORKSPACE_CONTEXT_ERROR: Workspaceが存在しません。";

        var root = Path.GetFullPath(workspaceRoot);
        var all = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !IsIgnored(root, path))
            .Select(path => new FileInfo(path))
            .Where(info => TextExtensions.Contains(info.Extension))
            .ToList();

        // CoderにはPlannerが実際に言及したファイルだけを優先して渡す。
        // 以前はcsprojを常時追加していたため、UI変更でも30KB超のpromptになり
        // WebView2のInput.insertTextが不安定になっていた。
        var mentioned = all
            .Where(f => PlannerMentionsFile(root, f, plannerAnswer))
            .OrderBy(f => CoderPriority(root, f))
            .ThenBy(f => Path.GetRelativePath(root, f.FullName), StringComparer.OrdinalIgnoreCase)
            .Take(MaxCoderFiles)
            .ToList();

        // Plannerが具体的なファイルを1つも挙げなかった場合だけ、安全な最小フォールバックを使う。
        if (mentioned.Count == 0)
        {
            mentioned = all
                .OrderBy(f => CoderPriority(root, f))
                .ThenBy(f => Path.GetRelativePath(root, f.FullName), StringComparer.OrdinalIgnoreCase)
                .Take(1)
                .ToList();
        }

        var output = new StringBuilder("CODER_WORKSPACE_FILES (complete selected files only):\n");
        foreach (var file in mentioned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await File.ReadAllTextAsync(file.FullName, Encoding.UTF8, cancellationToken);
            var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');

            if (content.Length > MaxCoderSingleFileChars)
            {
                output.AppendLine($"OMITTED_TOO_LARGE: {relative} ({content.Length} chars). File exceeds coder safety limit; do not modify without full content.");
                continue;
            }

            var block = $"\n===== COMPLETE FILE: {relative} =====\n{content}\n===== END COMPLETE FILE =====\n";
            if (output.Length + block.Length > MaxCoderContextChars)
            {
                output.AppendLine($"OMITTED_CODER_CONTEXT_BUDGET: {relative} ({content.Length} chars). Content is not partial. Do not modify this omitted file.");
                continue;
            }

            output.Append(block);
        }

        output.AppendLine($"CODER_CONTEXT_CHARS: {output.Length}");
        return output.ToString();
    }

    private static bool PlannerMentionsFile(string root, FileInfo file, string plannerAnswer)
    {
        var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
        return plannerAnswer.Contains(relative, StringComparison.OrdinalIgnoreCase)
            || plannerAnswer.Contains(file.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static int CoderPriority(string root, FileInfo info)
    {
        var relative = Path.GetRelativePath(root, info.FullName).Replace('\\', '/');
        if (relative.Equals("MainWindow.xaml.cs", StringComparison.OrdinalIgnoreCase)) return 0;
        if (info.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)) return 1;
        if (relative.Equals("MainWindow.xaml", StringComparison.OrdinalIgnoreCase)) return 2;
        if (info.Extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase)) return 3;
        if (info.Extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)) return 4;
        return 5;
    }

    private static bool IsIgnored(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("workspace/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(".ai-multi-window/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/.git/", StringComparison.OrdinalIgnoreCase);
    }

    private static int Priority(string root, FileInfo info)
    {
        var relative = Path.GetRelativePath(root, info.FullName).Replace('\\', '/');

        if (relative.Equals("MainWindow.xaml", StringComparison.OrdinalIgnoreCase)) return 0;
        if (relative.Equals("MainWindow.xaml.cs", StringComparison.OrdinalIgnoreCase)) return 1;
        if (info.Extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)) return 2;
        if (info.Extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase)) return 3;
        if (info.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)) return 4;
        if (info.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) return 5;
        if (info.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase)) return 8;
        return 7;
    }
}

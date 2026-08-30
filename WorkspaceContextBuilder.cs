using System.Text;

namespace AiMultiWindow;

public static class WorkspaceContextBuilder
{
    private const int MaxContextChars = 32_000;
    private const int MaxSingleFileChars = 18_000;
    private const int MaxFiles = 40;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".csproj", ".props", ".targets", ".json", ".xml",
        ".md", ".ps1", ".bat", ".cmd", ".yml", ".yaml"
    };

    public static async Task<string> BuildAsync(string workspaceRoot, CancellationToken cancellationToken = default)
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
        output.AppendLine("WORKSPACE_FILE_CONTENTS:");

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
                content = content[..MaxSingleFileChars] + "\n[TRUNCATED]";

            var block = $"\n===== FILE: {relative} =====\n{content}\n===== END FILE =====\n";
            var remaining = MaxContextChars - output.Length;
            if (block.Length > remaining)
            {
                if (remaining > 300)
                    output.Append(block[..remaining]);
                break;
            }

            output.Append(block);
        }

        output.AppendLine();
        output.AppendLine($"WORKSPACE_CONTEXT_CHARS: {output.Length}");
        return output.ToString();
    }

    private static bool IsIgnored(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("workspace/", StringComparison.OrdinalIgnoreCase)
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

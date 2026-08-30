using System.Text;

namespace AiMultiWindow;

public static class WorkspaceContextBuilder
{
    private const int MaxContextChars = 120_000;
    private const int MaxSingleFileChars = 30_000;
    private const int MaxFiles = 80;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".csproj", ".props", ".targets", ".json", ".xml",
        ".md", ".txt", ".ps1", ".bat", ".cmd", ".yml", ".yaml"
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
            .OrderBy(info => Priority(info.Extension))
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
                if (remaining > 100)
                    output.Append(block[..remaining]);
                break;
            }

            output.Append(block);
        }

        return output.ToString();
    }

    private static bool IsIgnored(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/.git/", StringComparison.OrdinalIgnoreCase);
    }

    private static int Priority(string extension) => extension.ToLowerInvariant() switch
    {
        ".csproj" => 0,
        ".xaml" => 1,
        ".cs" => 2,
        ".json" => 3,
        ".md" => 4,
        _ => 5
    };
}

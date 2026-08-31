using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace AiMultiWindow;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string? logPath = null;
        try
        {
            var root = Path.Combine(Environment.CurrentDirectory, ".ai-multi-window", "logs");
            Directory.CreateDirectory(root);
            logPath = Path.Combine(root, $"app-crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            var text = new StringBuilder()
                .AppendLine(DateTime.Now.ToString("O"))
                .AppendLine(e.Exception.ToString())
                .ToString();
            File.WriteAllText(logPath, text, Encoding.UTF8);
        }
        catch { }

        try
        {
            MessageBox.Show(
                logPath is null
                    ? $"処理中にエラーを捕捉しました。\n{e.Exception.GetType().Name}: {e.Exception.Message}"
                    : $"処理中にエラーを捕捉しました。アプリは継続します。\nログ: {logPath}",
                "AI Multi Window",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch { }

        // Browser/workflow automation exceptions must not terminate the whole desktop app.
        e.Handled = true;
    }
}

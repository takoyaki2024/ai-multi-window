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
        try
        {
            var root = Path.Combine(Environment.CurrentDirectory, ".ai-multi-window", "logs");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, $"app-crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            var text = new StringBuilder()
                .AppendLine(DateTime.Now.ToString("O"))
                .AppendLine(e.Exception.ToString())
                .ToString();
            File.WriteAllText(path, text, Encoding.UTF8);

            if (Current?.MainWindow is MainWindow window)
                window.ShowWorkflowException($"未処理エラーを捕捉しました。ログ: {path}");
        }
        catch { }

        // Workflow/browser automation errors should not terminate the whole desktop app.
        e.Handled = true;
    }
}

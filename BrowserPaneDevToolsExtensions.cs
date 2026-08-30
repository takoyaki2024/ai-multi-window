using Microsoft.Web.WebView2.Wpf;
using System.Reflection;
using System.Text.Json;

namespace AiMultiWindow;

public static class BrowserPaneDevToolsExtensions
{
    public static async Task<bool> TrySendPhysicalEnterAsync(this BrowserPane pane)
    {
        try
        {
            var field = typeof(BrowserPane).GetField("_webView", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(pane) is not WebView2 webView || webView.CoreWebView2 is null)
                return false;

            var core = webView.CoreWebView2;

            const string focusScript = """
                (() => {
                    const input = document.querySelector('#prompt-textarea')
                        || document.querySelector('[contenteditable="true"][role="textbox"]')
                        || document.querySelector('textarea');
                    if (!input) return 'ERROR:NO_INPUT';
                    input.focus();
                    return 'OK:FOCUSED';
                })();
                """;

            var focusRaw = await core.ExecuteScriptAsync(focusScript);
            var focusResult = JsonSerializer.Deserialize<string>(focusRaw) ?? string.Empty;
            if (focusResult != "OK:FOCUSED")
                return false;

            await Task.Delay(150);

            await DispatchKeyAsync(core, "rawKeyDown", includeText: false);
            await DispatchKeyAsync(core, "char", includeText: true);
            await DispatchKeyAsync(core, "keyUp", includeText: false);

            const string verifyScript = """
                (() => {
                    const input = document.querySelector('#prompt-textarea')
                        || document.querySelector('[contenteditable="true"][role="textbox"]')
                        || document.querySelector('textarea');
                    if (!input) return 'OK:INPUT_REMOVED';
                    const text = input instanceof HTMLTextAreaElement || input instanceof HTMLInputElement
                        ? input.value
                        : (input.innerText || input.textContent || '');
                    return text.trim().length === 0 ? 'OK:CLEARED' : 'WAIT:STILL_FILLED';
                })();
                """;

            for (var attempt = 0; attempt < 12; attempt++)
            {
                await Task.Delay(attempt == 0 ? 300 : 200);
                var raw = await core.ExecuteScriptAsync(verifyScript);
                var result = JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
                if (result.StartsWith("OK:", StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static Task<string> DispatchKeyAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core, string type, bool includeText)
    {
        var payload = includeText
            ? JsonSerializer.Serialize(new
            {
                type,
                key = "Enter",
                code = "Enter",
                text = "\r",
                unmodifiedText = "\r",
                windowsVirtualKeyCode = 13,
                nativeVirtualKeyCode = 13
            })
            : JsonSerializer.Serialize(new
            {
                type,
                key = "Enter",
                code = "Enter",
                windowsVirtualKeyCode = 13,
                nativeVirtualKeyCode = 13
            });

        return core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", payload);
    }
}

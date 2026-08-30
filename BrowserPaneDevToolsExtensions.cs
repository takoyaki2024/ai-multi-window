using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.Reflection;
using System.Text.Json;

namespace AiMultiWindow;

public static class BrowserPaneDevToolsExtensions
{
    public static async Task<bool> TrySendMessageWithDevToolsAsync(this BrowserPane pane, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        try
        {
            var field = typeof(BrowserPane).GetField("_webView", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(pane) is not WebView2 webView || webView.CoreWebView2 is null)
                return false;

            var core = webView.CoreWebView2;

            const string focusScript = """
                (() => {
                    const selectors = [
                        '#prompt-textarea',
                        '[contenteditable="true"][role="textbox"]',
                        'textarea'
                    ];
                    let input = null;
                    for (const selector of selectors) {
                        input = Array.from(document.querySelectorAll(selector)).find(el => {
                            const r = el.getBoundingClientRect();
                            return r.width > 20 && r.height > 10 && !el.disabled;
                        });
                        if (input) break;
                    }
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

            // Clear any stale composer text using genuine keyboard events so the page's
            // editor state and the visible DOM remain in sync.
            await DispatchKeyAsync(core, "rawKeyDown", "Control", "ControlLeft", 17, modifiers: 2);
            await DispatchKeyAsync(core, "rawKeyDown", "a", "KeyA", 65, modifiers: 2);
            await DispatchKeyAsync(core, "keyUp", "a", "KeyA", 65, modifiers: 2);
            await DispatchKeyAsync(core, "keyUp", "Control", "ControlLeft", 17);
            await DispatchKeyAsync(core, "rawKeyDown", "Backspace", "Backspace", 8);
            await DispatchKeyAsync(core, "keyUp", "Backspace", "Backspace", 8);

            await Task.Delay(100);

            // Input.insertText goes through Chromium's editing pipeline instead of
            // directly mutating DOM text. This is important for React/ProseMirror editors.
            var insertPayload = JsonSerializer.Serialize(new { text = message });
            await core.CallDevToolsProtocolMethodAsync("Input.insertText", insertPayload);

            await Task.Delay(300);

            const string hasTextScript = """
                (() => {
                    const input = document.querySelector('#prompt-textarea')
                        || document.querySelector('[contenteditable="true"][role="textbox"]')
                        || document.querySelector('textarea');
                    if (!input) return 'ERROR:NO_INPUT';
                    const text = input instanceof HTMLTextAreaElement || input instanceof HTMLInputElement
                        ? input.value
                        : (input.innerText || input.textContent || '');
                    return text.trim().length > 0 ? 'OK:HAS_TEXT' : 'ERROR:EMPTY';
                })();
                """;

            var textRaw = await core.ExecuteScriptAsync(hasTextScript);
            var textResult = JsonSerializer.Deserialize<string>(textRaw) ?? string.Empty;
            if (textResult != "OK:HAS_TEXT")
                return false;

            await DispatchKeyAsync(core, "rawKeyDown", "Enter", "Enter", 13);
            await DispatchKeyAsync(core, "char", "Enter", "Enter", 13, text: "\r");
            await DispatchKeyAsync(core, "keyUp", "Enter", "Enter", 13);

            return await WaitForSendConfirmationAsync(core);
        }
        catch
        {
            return false;
        }
    }

    // Kept for compatibility with older callers.
    public static async Task<bool> TrySendPhysicalEnterAsync(this BrowserPane pane)
    {
        try
        {
            var field = typeof(BrowserPane).GetField("_webView", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(pane) is not WebView2 webView || webView.CoreWebView2 is null)
                return false;

            var core = webView.CoreWebView2;
            await DispatchKeyAsync(core, "rawKeyDown", "Enter", "Enter", 13);
            await DispatchKeyAsync(core, "char", "Enter", "Enter", 13, text: "\r");
            await DispatchKeyAsync(core, "keyUp", "Enter", "Enter", 13);
            return await WaitForSendConfirmationAsync(core);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitForSendConfirmationAsync(CoreWebView2 core)
    {
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

        for (var attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(attempt == 0 ? 300 : 200);
            var raw = await core.ExecuteScriptAsync(verifyScript);
            var result = JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
            if (result.StartsWith("OK:", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static Task<string> DispatchKeyAsync(
        CoreWebView2 core,
        string type,
        string key,
        string code,
        int virtualKeyCode,
        int modifiers = 0,
        string? text = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type,
            key,
            code,
            text,
            unmodifiedText = text,
            windowsVirtualKeyCode = virtualKeyCode,
            nativeVirtualKeyCode = virtualKeyCode,
            modifiers
        });

        return core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", payload);
    }
}

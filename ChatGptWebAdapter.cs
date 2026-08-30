using Microsoft.Web.WebView2.Core;
using System.Text.Json;

namespace AiMultiWindow;

public sealed record ChatSendResult(bool Success, string Code, string Detail);

public sealed class ChatGptWebAdapter
{
    private readonly CoreWebView2 _core;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private ResponseBaseline? _pendingResponse;

    public ChatGptWebAdapter(CoreWebView2 core) => _core = core;

    public async Task<ChatSendResult> SendAsync(string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return new(false, "EMPTY_MESSAGE", "Message is empty.");
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var initial = await ReadStateAsync(cancellationToken);
            if (!initial.IsChatGpt) return new(false, "UNSUPPORTED_PAGE", "The active page is not chatgpt.com.");
            if (!initial.ComposerFound) return new(false, "COMPOSER_NOT_FOUND", "ChatGPT composer is not ready. Login or page reload may be required.");
            if (initial.Generating) return new(false, "GENERATION_IN_PROGRESS", "Wait for the current response to finish.");

            if (!await FocusComposerAsync(cancellationToken))
                return new(false, "FOCUS_FAILED", "Could not focus the ChatGPT composer.");

            await ClearComposerAsync(cancellationToken);
            await CallCdpAsync("Input.insertText", JsonSerializer.Serialize(new { text = message }), cancellationToken);

            ComposerState? ready = null;
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(100, cancellationToken);
                ready = await ReadStateAsync(cancellationToken);
                if (TextEquals(ready.ComposerText, message) && ready.SendButtonEnabled) break;
            }

            if (ready is null || !TextEquals(ready.ComposerText, message))
                return new(false, "INPUT_MISMATCH", "The DOM composer value does not match the requested message.");
            if (!ready.SendButtonEnabled || ready.SendX is null || ready.SendY is null)
                return new(false, "SEND_NOT_READY", "React did not enable the composer send button.");

            // A real CDP mouse event is dispatched once. No synthetic Enter/click fallback is stacked.
            await DispatchMouseAsync("mousePressed", ready.SendX.Value, ready.SendY.Value, cancellationToken);
            await DispatchMouseAsync("mouseReleased", ready.SendX.Value, ready.SendY.Value, cancellationToken);

            var accepted = await WaitForAcceptanceAsync(initial, message, cancellationToken);
            if (!accepted.Success) return accepted;

            _pendingResponse = new ResponseBaseline(initial.AssistantCount, initial.UserCount, message);
            return new(true, "ACCEPTED", "A matching new user turn and assistant generation were observed.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new(false, "WEBVIEW_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally { _operationLock.Release(); }
    }

    public async Task<string?> WaitForLatestResponseAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var baseline = _pendingResponse;
            if (baseline is null) return null;

            string? stableText = null;
            var stableReads = 0;
            for (var i = 0; i < 240; i++)
            {
                var state = await ReadStateAsync(cancellationToken);
                if (state.AssistantCount > baseline.AssistantCount && !string.IsNullOrWhiteSpace(state.LatestAssistantText))
                {
                    if (!state.Generating && string.Equals(stableText, state.LatestAssistantText, StringComparison.Ordinal)) stableReads++;
                    else stableReads = 0;
                    stableText = state.LatestAssistantText;
                    if (!state.Generating && stableReads >= 2)
                    {
                        _pendingResponse = null;
                        return stableText;
                    }
                }
                await Task.Delay(500, cancellationToken);
            }
            throw new TimeoutException("The matching ChatGPT response did not finish within two minutes.");
        }
        finally { _operationLock.Release(); }
    }

    private async Task<ChatSendResult> WaitForAcceptanceAsync(ComposerState baseline, string message, CancellationToken cancellationToken)
    {
        var userObserved = false;
        for (var i = 0; i < 80; i++)
        {
            await Task.Delay(150, cancellationToken);
            var state = await ReadStateAsync(cancellationToken);
            userObserved |= state.UserCount > baseline.UserCount && TextEquals(state.LatestUserText, message);
            if (userObserved && (state.Generating || state.AssistantCount > baseline.AssistantCount))
                return new(true, "ACCEPTED", string.Empty);
        }
        return userObserved
            ? new(false, "GENERATION_NOT_STARTED", "The user turn appeared, but assistant generation was not observed.")
            : new(false, "USER_TURN_NOT_OBSERVED", "No matching new user conversation turn appeared after the single send trigger.");
    }

    private async Task<bool> FocusComposerAsync(CancellationToken cancellationToken)
    {
        const string script = """
            (() => {
              const visible = e => { const r=e?.getBoundingClientRect(); return !!r && r.width>20 && r.height>10; };
              const e = ['#prompt-textarea','[contenteditable="true"][role="textbox"]','textarea']
                .flatMap(s => Array.from(document.querySelectorAll(s))).find(visible);
              if (!e) return false; e.focus(); return document.activeElement === e || e.contains(document.activeElement);
            })()
            """;
        return await ExecuteJsonAsync<bool>(script, cancellationToken);
    }

    private async Task ClearComposerAsync(CancellationToken cancellationToken)
    {
        await DispatchKeyAsync("rawKeyDown", "a", "KeyA", 65, modifiers: 2, cancellationToken);
        await DispatchKeyAsync("keyUp", "a", "KeyA", 65, modifiers: 2, cancellationToken);
        await DispatchKeyAsync("rawKeyDown", "Backspace", "Backspace", 8, modifiers: 0, cancellationToken);
        await DispatchKeyAsync("keyUp", "Backspace", "Backspace", 8, modifiers: 0, cancellationToken);
    }

    private async Task<ComposerState> ReadStateAsync(CancellationToken cancellationToken)
    {
        const string script = """
            (() => {
              const visible = e => { const r=e?.getBoundingClientRect(); return !!r && r.width>0 && r.height>0; };
              const unique = xs => [...new Set(xs)];
              const composer = ['#prompt-textarea','[contenteditable="true"][role="textbox"]','textarea']
                .flatMap(s => Array.from(document.querySelectorAll(s))).find(visible) || null;
              const text = e => !e ? '' : ((e instanceof HTMLTextAreaElement || e instanceof HTMLInputElement) ? e.value : (e.innerText || e.textContent || ''));
              let scope = composer?.closest('form') || composer?.parentElement || null;
              let send = null;
              const selectors = ['button[data-testid="send-button"]','button[aria-label="Send prompt"]','button[aria-label="Send message"]','button[aria-label="送信"]'];
              for (let depth=0; scope && depth<5 && !send; depth++, scope=scope.parentElement)
                for (const s of selectors) { send=Array.from(scope.querySelectorAll(s)).find(visible)||null; if(send) break; }
              const users = unique(Array.from(document.querySelectorAll('[data-message-author-role="user"],[data-content-source="user"],[data-turn="user"]'))).filter(visible);
              const assistants = unique(Array.from(document.querySelectorAll('[data-message-author-role="assistant"],[data-content-source="assistant"],[data-turn="assistant"]'))).filter(visible);
              const stop = Array.from(document.querySelectorAll('button[data-testid="stop-button"],button[aria-label*="Stop"],button[aria-label*="停止"]')).some(visible);
              const r = send?.getBoundingClientRect();
              return { isChatGpt: location.hostname === 'chatgpt.com' || location.hostname.endsWith('.chatgpt.com'), composerFound: !!composer,
                composerText: text(composer), sendButtonEnabled: !!send && !send.disabled && send.getAttribute('aria-disabled') !== 'true',
                sendX: r ? r.left+r.width/2 : null, sendY: r ? r.top+r.height/2 : null, userCount: users.length,
                assistantCount: assistants.length, latestUserText: text(users.at(-1)), latestAssistantText: text(assistants.at(-1)), generating: stop };
            })()
            """;
        return await ExecuteJsonAsync<ComposerState>(script, cancellationToken) ?? new ComposerState();
    }

    private Task DispatchMouseAsync(string type, double x, double y, CancellationToken ct) =>
        CallCdpAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x, y, button = "left", clickCount = 1 }), ct);

    private Task DispatchKeyAsync(string type, string key, string code, int virtualKey, int modifiers, CancellationToken cancellationToken) =>
        CallCdpAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type, key, code, windowsVirtualKeyCode = virtualKey, nativeVirtualKeyCode = virtualKey, modifiers }), cancellationToken);

    private async Task CallCdpAsync(string method, string payload, CancellationToken cancellationToken) =>
        await _core.CallDevToolsProtocolMethodAsync(method, payload).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

    private async Task<T?> ExecuteJsonAsync<T>(string script, CancellationToken cancellationToken)
    {
        var raw = await _core.ExecuteScriptAsync(script).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        return JsonSerializer.Deserialize<T>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private static bool TextEquals(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    private static string Normalize(string? value) => (value ?? string.Empty).Replace("\r\n", "\n").Trim();

    private sealed record ResponseBaseline(int AssistantCount, int UserCount, string Message);
    private sealed class ComposerState
    {
        public bool IsChatGpt { get; set; }
        public bool ComposerFound { get; set; }
        public string ComposerText { get; set; } = string.Empty;
        public bool SendButtonEnabled { get; set; }
        public double? SendX { get; set; }
        public double? SendY { get; set; }
        public int UserCount { get; set; }
        public int AssistantCount { get; set; }
        public string LatestUserText { get; set; } = string.Empty;
        public string LatestAssistantText { get; set; } = string.Empty;
        public bool Generating { get; set; }
    }
}

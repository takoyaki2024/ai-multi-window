using Microsoft.Web.WebView2.Core;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiMultiWindow;

public sealed record ChatSendResult(bool Success, string Code, string Detail);

public sealed class ChatGptWebAdapter
{
    private readonly CoreWebView2 _core;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private ResponseBaseline? _pendingResponse;
    private string? _lastLogPath;

    public string? LastLogPath => _lastLogPath;

    public ChatGptWebAdapter(CoreWebView2 core) => _core = core;

    public async Task<ChatSendResult> SendAsync(string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return new(false, "EMPTY_MESSAGE", "Message is empty.");
        await _operationLock.WaitAsync(cancellationToken);
        var log = new StringBuilder();
        try
        {
            Log(log, "SEND_START", $"expectedLength={message.Length}; normalizedExpectedLength={Normalize(message).Length}");
            var initial = await ReadStateAsync(cancellationToken);
            LogState(log, "INITIAL", initial);

            if (!initial.IsChatGpt) return await FailAsync(log, "UNSUPPORTED_PAGE", "The active page is not chatgpt.com.");
            if (!initial.ComposerFound) return await FailAsync(log, "COMPOSER_NOT_FOUND", "ChatGPT composer is not ready. Login or page reload may be required.");
            if (initial.Generating) return await FailAsync(log, "GENERATION_IN_PROGRESS", "Wait for the current response to finish.");

            if (!await FocusComposerAsync(cancellationToken))
                return await FailAsync(log, "FOCUS_FAILED", "Could not focus the ChatGPT composer.");

            var focused = await ReadStateAsync(cancellationToken);
            LogState(log, "AFTER_FOCUS", focused);

            await ClearComposerAsync(cancellationToken);
            await Task.Delay(100, cancellationToken);
            var cleared = await ReadStateAsync(cancellationToken);
            LogState(log, "AFTER_CLEAR", cleared);

            await CallCdpAsync("Input.insertText", JsonSerializer.Serialize(new { text = message }), cancellationToken);
            Log(log, "CDP_INSERT_TEXT", "completed");

            ComposerState? ready = null;
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(100, cancellationToken);
                ready = await ReadStateAsync(cancellationToken);
                if (TextEquals(ready.ComposerText, message) && ready.SendButtonEnabled && ready.SendX is not null && ready.SendY is not null)
                    break;
            }

            if (ready is null)
                return await FailAsync(log, "STATE_UNAVAILABLE", "Could not read ChatGPT state after input.");

            LogState(log, "READY_CHECK", ready);
            if (!TextEquals(ready.ComposerText, message))
            {
                Log(log, "INPUT_COMPARE", $"expectedNormalized={EscapeForLog(Normalize(message))}; actualNormalized={EscapeForLog(Normalize(ready.ComposerText))}");
                return await FailAsync(log, "INPUT_MISMATCH", $"Composer text does not match after DOM whitespace normalization. expectedLength={message.Length}, actualLength={ready.ComposerText.Length}, normalizedExpectedLength={Normalize(message).Length}, normalizedActualLength={Normalize(ready.ComposerText).Length}");
            }
            if (!ready.SendButtonEnabled || ready.SendX is null || ready.SendY is null)
                return await FailAsync(log, "SEND_NOT_READY", $"Send button is unavailable. selector={ready.SendSelector ?? "none"}");
            if (!ready.SendHitTestMatches)
                return await FailAsync(log, "SEND_BUTTON_OBSCURED", $"Send button center is not hittable. hit={ready.HitElement ?? "none"}");

            Log(log, "SEND_TRIGGER", $"selector={ready.SendSelector}; x={ready.SendX:0.##}; y={ready.SendY:0.##}");
            await DispatchMouseAsync("mouseMoved", ready.SendX.Value, ready.SendY.Value, cancellationToken);
            await DispatchMouseAsync("mousePressed", ready.SendX.Value, ready.SendY.Value, cancellationToken);
            await DispatchMouseAsync("mouseReleased", ready.SendX.Value, ready.SendY.Value, cancellationToken);

            var accepted = await WaitForAcceptanceAsync(initial, message, log, cancellationToken);
            if (!accepted.Success)
            {
                await FlushLogAsync(log);
                return accepted;
            }

            _pendingResponse = new ResponseBaseline(initial.AssistantCount, initial.UserCount, message);
            Log(log, "SEND_ACCEPTED", "matching user turn and assistant generation observed");
            await FlushLogAsync(log);
            return new(true, "ACCEPTED", BuildDetail("A matching new user turn and assistant generation were observed."));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log(log, "EXCEPTION", $"{ex.GetType().Name}: {ex.Message}");
            await FlushLogAsync(log);
            return new(false, "WEBVIEW_ERROR", BuildDetail($"{ex.GetType().Name}: {ex.Message}"));
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

    public async Task<string?> GetVisibleLatestAnswerAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadStateAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(state.LatestAssistantText) ? null : state.LatestAssistantText;
        }
        finally { _operationLock.Release(); }
    }

    private async Task<ChatSendResult> WaitForAcceptanceAsync(ComposerState baseline, string message, StringBuilder log, CancellationToken cancellationToken)
    {
        var userObserved = false;
        ComposerState? last = null;
        for (var i = 0; i < 80; i++)
        {
            await Task.Delay(150, cancellationToken);
            last = await ReadStateAsync(cancellationToken);
            userObserved |= last.UserCount > baseline.UserCount && TextEquals(last.LatestUserText, message);
            if (userObserved && (last.Generating || last.AssistantCount > baseline.AssistantCount))
            {
                LogState(log, "ACCEPTED_STATE", last);
                return new(true, "ACCEPTED", string.Empty);
            }
        }

        if (last is not null)
        {
            LogState(log, "ACCEPTANCE_TIMEOUT_STATE", last);
            Log(log, "ACCEPTANCE_COMPARE", $"expectedNormalizedLength={Normalize(message).Length}; latestUserNormalizedLength={Normalize(last.LatestUserText).Length}; latestUserNormalized={EscapeForLog(Normalize(last.LatestUserText))}");
        }
        return userObserved
            ? new(false, "GENERATION_NOT_STARTED", BuildDetail("The user turn appeared, but assistant generation was not observed."))
            : new(false, "USER_TURN_NOT_OBSERVED", BuildDetail("No matching new user conversation turn appeared after the single send trigger."));
    }

    private async Task<bool> FocusComposerAsync(CancellationToken cancellationToken)
    {
        const string script = """
            (() => {
              const visible = e => { const r=e?.getBoundingClientRect(); return !!r && r.width>20 && r.height>10; };
              const candidates = [
                document.querySelector('#prompt-textarea'),
                ...document.querySelectorAll('div[contenteditable="true"]'),
                ...document.querySelectorAll('[contenteditable="true"][role="textbox"]'),
                ...document.querySelectorAll('textarea')
              ].filter(Boolean);
              const e = candidates.find(x => visible(x) && !x.closest('[aria-hidden="true"]')) || null;
              if (!e) return false;
              e.focus();
              const active = document.activeElement;
              return active === e || e.contains(active);
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
              const visible = e => { const r=e?.getBoundingClientRect(); return !!r && r.width>0 && r.height>0 && getComputedStyle(e).visibility !== 'hidden'; };
              const unique = xs => [...new Set(xs)];
              const candidates = [
                document.querySelector('#prompt-textarea'),
                ...document.querySelectorAll('div[contenteditable="true"]'),
                ...document.querySelectorAll('[contenteditable="true"][role="textbox"]'),
                ...document.querySelectorAll('textarea')
              ].filter(Boolean);
              const composer = candidates.find(x => visible(x) && !x.closest('[aria-hidden="true"]')) || null;
              const text = e => !e ? '' : ((e instanceof HTMLTextAreaElement || e instanceof HTMLInputElement) ? e.value : (e.innerText || e.textContent || ''));
              const css = e => {
                if (!e) return '';
                if (e.id) return '#' + e.id;
                const dt=e.getAttribute('data-testid'); if(dt) return `[data-testid="${dt}"]`;
                const al=e.getAttribute('aria-label'); if(al) return `[aria-label="${al}"]`;
                return e.tagName.toLowerCase();
              };

              let scope = composer?.closest('form') || composer?.parentElement || null;
              let send = null;
              let sendSelector = '';
              const selectors = [
                'button[data-testid="send-button"]',
                'button[data-testid*="send"]',
                'button[aria-label="Send prompt"]',
                'button[aria-label="Send message"]',
                'button[aria-label="送信"]',
                'button[aria-label*="Send"]',
                'button[aria-label*="送信"]'
              ];
              for (let depth=0; scope && depth<7 && !send; depth++, scope=scope.parentElement) {
                for (const s of selectors) {
                  const found=Array.from(scope.querySelectorAll(s)).find(x => visible(x));
                  if(found){ send=found; sendSelector=s; break; }
                }
              }

              const turnCandidates = Array.from(document.querySelectorAll('main article, main [data-testid*="conversation-turn"], [data-message-author-role], [data-turn], [data-content-source]'));
              const users = unique(turnCandidates.filter(e => {
                const role=(e.getAttribute('data-message-author-role')||e.getAttribute('data-turn')||e.getAttribute('data-content-source')||'').toLowerCase();
                return visible(e) && role.includes('user');
              }));
              const assistants = unique(turnCandidates.filter(e => {
                const role=(e.getAttribute('data-message-author-role')||e.getAttribute('data-turn')||e.getAttribute('data-content-source')||'').toLowerCase();
                if (role.includes('assistant')) return visible(e);
                if (e.matches('article,[data-testid*="conversation-turn"]')) {
                  return visible(e) && !!e.querySelector('.markdown,[data-message-author-role="assistant"],[data-content-source="assistant"]');
                }
                return false;
              }));
              const stop = Array.from(document.querySelectorAll('button[data-testid="stop-button"],button[aria-label*="Stop"],button[aria-label*="停止"],button[data-testid*="stop"]')).some(visible);
              const r = send?.getBoundingClientRect();
              const cx = r ? r.left+r.width/2 : null;
              const cy = r ? r.top+r.height/2 : null;
              const hit = (cx !== null && cy !== null) ? document.elementFromPoint(cx, cy) : null;
              const hitMatches = !!send && !!hit && (hit === send || send.contains(hit));
              const active = document.activeElement;

              return {
                url: location.href,
                hostname: location.hostname,
                isChatGpt: location.hostname === 'chatgpt.com' || location.hostname.endsWith('.chatgpt.com'),
                composerFound: !!composer,
                composerSelector: css(composer),
                composerTag: composer?.tagName || '',
                composerContentEditable: composer?.getAttribute('contenteditable') || '',
                composerRole: composer?.getAttribute('role') || '',
                composerText: text(composer),
                activeElement: css(active),
                sendButtonEnabled: !!send && !send.disabled && send.getAttribute('aria-disabled') !== 'true',
                sendSelector,
                sendDisabled: !!send?.disabled,
                sendAriaDisabled: send?.getAttribute('aria-disabled') || '',
                sendX: cx,
                sendY: cy,
                sendHitTestMatches: hitMatches,
                hitElement: css(hit),
                userCount: users.length,
                assistantCount: assistants.length,
                latestUserText: text(users.at(-1)),
                latestAssistantText: text(assistants.at(-1)),
                generating: stop
              };
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

    private async Task<ChatSendResult> FailAsync(StringBuilder log, string code, string detail)
    {
        Log(log, "FAIL", $"{code}: {detail}");
        await FlushLogAsync(log);
        return new(false, code, BuildDetail(detail));
    }

    private string BuildDetail(string detail) => string.IsNullOrWhiteSpace(_lastLogPath) ? detail : $"{detail} Log: {_lastLogPath}";

    private async Task FlushLogAsync(StringBuilder log)
    {
        try
        {
            var root = Path.Combine(Environment.CurrentDirectory, ".ai-multi-window", "logs");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, $"chatgpt-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            await File.WriteAllTextAsync(path, log.ToString(), Encoding.UTF8);
            _lastLogPath = path;
        }
        catch
        {
            _lastLogPath = null;
        }
    }

    private static void Log(StringBuilder log, string stage, string detail) =>
        log.Append(DateTime.Now.ToString("O")).Append(' ').Append(stage).Append(' ').AppendLine(detail.Replace('\r', ' ').Replace('\n', ' '));

    private static void LogState(StringBuilder log, string stage, ComposerState s)
    {
        Log(log, stage,
            $"url={s.Url}; host={s.Hostname}; chatgpt={s.IsChatGpt}; composer={s.ComposerFound}; composerSelector={s.ComposerSelector}; tag={s.ComposerTag}; contenteditable={s.ComposerContentEditable}; role={s.ComposerRole}; composerLength={s.ComposerText.Length}; normalizedComposerLength={Normalize(s.ComposerText).Length}; active={s.ActiveElement}; sendSelector={s.SendSelector}; sendEnabled={s.SendButtonEnabled}; disabled={s.SendDisabled}; ariaDisabled={s.SendAriaDisabled}; x={s.SendX}; y={s.SendY}; hitMatches={s.SendHitTestMatches}; hit={s.HitElement}; users={s.UserCount}; assistants={s.AssistantCount}; latestUserLength={s.LatestUserText.Length}; latestAssistantLength={s.LatestAssistantText.Length}; generating={s.Generating}");
    }

    private static bool TextEquals(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    // contenteditable/innerText can represent one visual line break differently from the
    // string sent through CDP (for example an extra DOM newline between block nodes).
    // Compare transport text after normalizing only DOM/editor whitespace artifacts;
    // all non-whitespace characters still have to match exactly.
    private static string Normalize(string? value)
    {
        var text = (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\u00A0', ' ')
            .Replace("\u200B", string.Empty, StringComparison.Ordinal)
            .Replace("\uFEFF", string.Empty, StringComparison.Ordinal);

        text = Regex.Replace(text, "[ \\t]*\\n[ \\t]*", "\n");
        text = Regex.Replace(text, "\\n+", "\n");
        return text.Trim();
    }

    private static string EscapeForLog(string value)
    {
        const int max = 500;
        var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        return escaped.Length <= max ? escaped : escaped[..max] + "…";
    }

    private sealed record ResponseBaseline(int AssistantCount, int UserCount, string Message);
    private sealed class ComposerState
    {
        public string Url { get; set; } = string.Empty;
        public string Hostname { get; set; } = string.Empty;
        public bool IsChatGpt { get; set; }
        public bool ComposerFound { get; set; }
        public string ComposerSelector { get; set; } = string.Empty;
        public string ComposerTag { get; set; } = string.Empty;
        public string ComposerContentEditable { get; set; } = string.Empty;
        public string ComposerRole { get; set; } = string.Empty;
        public string ComposerText { get; set; } = string.Empty;
        public string ActiveElement { get; set; } = string.Empty;
        public bool SendButtonEnabled { get; set; }
        public string SendSelector { get; set; } = string.Empty;
        public bool SendDisabled { get; set; }
        public string SendAriaDisabled { get; set; } = string.Empty;
        public double? SendX { get; set; }
        public double? SendY { get; set; }
        public bool SendHitTestMatches { get; set; }
        public string HitElement { get; set; } = string.Empty;
        public int UserCount { get; set; }
        public int AssistantCount { get; set; }
        public string LatestUserText { get; set; } = string.Empty;
        public string LatestAssistantText { get; set; } = string.Empty;
        public bool Generating { get; set; }
    }
}

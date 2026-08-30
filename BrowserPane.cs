using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AiMultiWindow;

public sealed class BrowserPane : Grid
{
    private readonly TextBox _addressBar;
    private readonly WebView2 _webView;
    private readonly Button _backButton;
    private readonly Button _forwardButton;
    private readonly TextBox _promptBox;
    private readonly Button _sendButton;
    private readonly Button _copyAnswerButton;
    private readonly TextBlock _aiStatus;
    private readonly string _profileFolder;
    private string _homeUrl;

    public event Action<string>? UrlChanged;

    public BrowserPane(string initialUrl, int paneIndex)
    {
        _homeUrl = NormalizeUrl(initialUrl);
        _profileFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AiMultiWindow",
            "WebViewProfiles",
            $"pane-{paneIndex + 1}");

        Background = System.Windows.Media.Brushes.White;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var toolbar = new Grid { Margin = new Thickness(6) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _backButton = MakeButton("←", "戻る");
        _forwardButton = MakeButton("→", "進む");
        var reloadButton = MakeButton("↻", "再読込");
        var homeButton = MakeButton("⌂", "ホーム");
        _addressBar = new TextBox
        {
            Text = _homeUrl,
            VerticalContentAlignment = VerticalAlignment.Center,
            MinWidth = 120,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4)
        };

        AddToToolbar(toolbar, _backButton, 0);
        AddToToolbar(toolbar, _forwardButton, 1);
        AddToToolbar(toolbar, reloadButton, 2);
        AddToToolbar(toolbar, homeButton, 3);
        Grid.SetColumn(_addressBar, 4);
        toolbar.Children.Add(_addressBar);
        Children.Add(toolbar);

        var aiBar = new Grid { Margin = new Thickness(6, 0, 6, 6) };
        aiBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        aiBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        aiBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        aiBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _promptBox = new TextBox
        {
            MinWidth = 120,
            MinHeight = 34,
            MaxHeight = 96,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 5, 8, 5),
            ToolTip = "このペインのAIへ送るメッセージ。Ctrl+Enterでも送信できます。"
        };
        _sendButton = new Button
        {
            Content = "送信",
            MinWidth = 62,
            Height = 34,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(10, 0, 10, 0),
            ToolTip = "表示中のAIチャットへ送信"
        };
        _copyAnswerButton = new Button
        {
            Content = "最新回答コピー",
            MinWidth = 105,
            Height = 34,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(10, 0, 10, 0),
            ToolTip = "表示中ページから最新のAI回答を取得してクリップボードへコピー"
        };
        _aiStatus = new TextBlock
        {
            Text = $"P{paneIndex + 1} 待機",
            Foreground = System.Windows.Media.Brushes.DimGray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 2, 0),
            MinWidth = 52,
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        Grid.SetColumn(_promptBox, 0);
        Grid.SetColumn(_sendButton, 1);
        Grid.SetColumn(_copyAnswerButton, 2);
        Grid.SetColumn(_aiStatus, 3);
        aiBar.Children.Add(_promptBox);
        aiBar.Children.Add(_sendButton);
        aiBar.Children.Add(_copyAnswerButton);
        aiBar.Children.Add(_aiStatus);
        Grid.SetRow(aiBar, 1);
        Children.Add(aiBar);

        _webView = new WebView2();
        Grid.SetRow(_webView, 2);
        Children.Add(_webView);

        _backButton.Click += (_, _) => { if (_webView.CanGoBack) _webView.GoBack(); };
        _forwardButton.Click += (_, _) => { if (_webView.CanGoForward) _webView.GoForward(); };
        reloadButton.Click += (_, _) => _webView.Reload();
        homeButton.Click += (_, _) => Navigate(_homeUrl);
        _sendButton.Click += async (_, _) => await SendPromptAsync();
        _copyAnswerButton.Click += async (_, _) => await CopyLatestAnswerAsync();
        _promptBox.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                await SendPromptAsync();
            }
        };

        _addressBar.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Navigate(_addressBar.Text);
                Keyboard.ClearFocus();
            }
        };
        _addressBar.LostKeyboardFocus += (_, _) =>
        {
            var normalized = NormalizeUrl(_addressBar.Text);
            _addressBar.Text = normalized;
            _homeUrl = normalized;
            UrlChanged?.Invoke(normalized);
        };

        Loaded += async (_, _) => await InitializeWebViewAsync();
    }

    public string HomeUrl
    {
        get => _homeUrl;
        set
        {
            _homeUrl = NormalizeUrl(value);
            _addressBar.Text = _homeUrl;
            UrlChanged?.Invoke(_homeUrl);
        }
    }

    public void NavigateHome() => Navigate(_homeUrl);
    public void FocusAddressBar() { _addressBar.Focus(); _addressBar.SelectAll(); }
    public void FocusPromptBox() => _promptBox.Focus();

    private async Task InitializeWebViewAsync()
    {
        if (_webView.CoreWebView2 is not null)
            return;

        try
        {
            Directory.CreateDirectory(_profileFolder);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _profileFolder);
            await _webView.EnsureCoreWebView2Async(environment);
            var core = _webView.CoreWebView2;
            if (core is null)
            {
                _aiStatus.Text = "WebView失敗";
                return;
            }

            core.Settings.AreDevToolsEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsZoomControlEnabled = true;
            core.HistoryChanged += (_, _) => UpdateNavigationButtons();
            core.NavigationCompleted += (_, _) =>
            {
                if (_webView.Source is not null)
                    _addressBar.Text = _webView.Source.ToString();
                UpdateNavigationButtons();
                _aiStatus.Text = "待機";
            };
            Navigate(_homeUrl);
        }
        catch (Exception ex)
        {
            _aiStatus.Text = $"WebView失敗: {ex.GetType().Name}";
            _aiStatus.ToolTip = ex.Message;
        }
    }

    public async Task<bool> SendMessageAsync(string message)
    {
        if (_webView.CoreWebView2 is null || string.IsNullOrWhiteSpace(message))
            return false;

        var messageJson = JsonSerializer.Serialize(message);
        var script = $$"""
            (() => {
                const message = {{messageJson}};
                const selectors = [
                    '#prompt-textarea',
                    'textarea[placeholder]',
                    'textarea',
                    '[contenteditable="true"][role="textbox"]',
                    '[contenteditable="true"]'
                ];
                let input = null;
                for (const selector of selectors) {
                    const candidates = Array.from(document.querySelectorAll(selector));
                    input = candidates.find(el => {
                        const r = el.getBoundingClientRect();
                        return r.width > 20 && r.height > 10 && !el.disabled;
                    });
                    if (input) break;
                }
                if (!input) return 'ERROR:NO_INPUT';

                input.focus();
                if (input instanceof HTMLTextAreaElement || input instanceof HTMLInputElement) {
                    const proto = input instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
                    const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
                    if (setter) setter.call(input, message); else input.value = message;
                    input.dispatchEvent(new Event('input', { bubbles: true }));
                    input.dispatchEvent(new Event('change', { bubbles: true }));
                } else {
                    input.textContent = '';
                    document.execCommand('insertText', false, message);
                    if (!input.textContent) input.textContent = message;
                    input.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: message }));
                }

                const sendSelectors = [
                    'button[data-testid="send-button"]',
                    'button[aria-label*="Send"]',
                    'button[aria-label*="送信"]',
                    'button[title*="Send"]',
                    'button[title*="送信"]'
                ];
                for (const selector of sendSelectors) {
                    const button = Array.from(document.querySelectorAll(selector))
                        .find(btn => !btn.disabled && btn.getBoundingClientRect().width > 0);
                    if (button) { button.click(); return 'OK:BUTTON'; }
                }

                input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }));
                input.dispatchEvent(new KeyboardEvent('keyup', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }));
                return 'OK:ENTER';
            })();
            """;

        try
        {
            var raw = await ExecuteScriptWithRetryAsync(script, 8);
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            var result = JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
            return result.StartsWith("OK:", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            _aiStatus.Text = $"送信失敗: {ex.GetType().Name}";
            _aiStatus.ToolTip = ex.Message;
            return false;
        }
    }

    public async Task<string?> GetLatestAnswerAsync()
    {
        if (_webView.CoreWebView2 is null)
        {
            _aiStatus.Text = "取得失敗: WebView未準備";
            return null;
        }

        const string script = """
            (() => {
                const visible = el => {
                    if (!el) return false;
                    const r = el.getBoundingClientRect();
                    return r.width > 0 && r.height > 0;
                };

                const candidates = [];
                const selectors = [
                    '[data-message-author-role="assistant"]',
                    '[data-content-source="assistant"]',
                    '[data-turn="assistant"]',
                    'main article[data-testid*="conversation-turn"]'
                ];

                for (const selector of selectors) {
                    for (const el of document.querySelectorAll(selector)) {
                        if (!visible(el)) continue;
                        const role = el.getAttribute('data-message-author-role');
                        if (role && role !== 'assistant') continue;
                        const text = (el.innerText || '').trim();
                        if (text.length > 0) candidates.push({ el, text });
                    }
                }

                if (candidates.length === 0) {
                    const markdown = Array.from(document.querySelectorAll('main .markdown, main [class*="markdown"]'))
                        .filter(visible)
                        .map(el => ({ el, text: (el.innerText || '').trim() }))
                        .filter(x => x.text.length > 0);
                    if (markdown.length > 0) return markdown[markdown.length - 1].text;
                    return '';
                }

                const unique = [];
                const seen = new Set();
                for (const item of candidates) {
                    if (seen.has(item.el)) continue;
                    seen.add(item.el);
                    unique.push(item);
                }
                return unique[unique.length - 1].text;
            })();
            """;

        try
        {
            _aiStatus.Text = "取得中";
            var raw = await ExecuteScriptWithRetryAsync(script, 5);
            if (string.IsNullOrWhiteSpace(raw) || raw is "null" or "undefined")
            {
                _aiStatus.Text = "回答なし";
                return null;
            }

            string? text;
            try
            {
                text = JsonSerializer.Deserialize<string>(raw);
            }
            catch (JsonException)
            {
                text = raw.Trim('"');
            }

            _aiStatus.Text = string.IsNullOrWhiteSpace(text) ? "回答なし" : "取得済";
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (TimeoutException)
        {
            _aiStatus.Text = "取得タイムアウト";
            return null;
        }
        catch (Exception ex)
        {
            var message = ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (message.Length > 120)
                message = message[..120] + "…";
            _aiStatus.Text = $"取得失敗: {ex.GetType().Name}";
            _aiStatus.ToolTip = message;
            return null;
        }
    }

    private async Task<string> ExecuteScriptWithRetryAsync(string script, int timeoutSeconds)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (_webView.CoreWebView2 is null)
                    throw new InvalidOperationException("WebView2 is not initialized.");

                return await _webView.CoreWebView2.ExecuteScriptAsync(script)
                    .WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
            }
            catch (Exception ex) when (attempt < 2)
            {
                last = ex;
                await Task.Delay(250 * (attempt + 1));
            }
        }

        throw last ?? new InvalidOperationException("Script execution failed.");
    }

    private async Task SendPromptAsync()
    {
        var message = _promptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(message)) { _aiStatus.Text = "未入力"; return; }
        SetAiControlsEnabled(false);
        _aiStatus.Text = "送信中";
        var success = await SendMessageAsync(message);
        if (success) { _promptBox.Clear(); _aiStatus.Text = "送信済"; }
        else if (!_aiStatus.Text.StartsWith("送信失敗", StringComparison.Ordinal)) _aiStatus.Text = "送信失敗";
        SetAiControlsEnabled(true);
    }

    private async Task CopyLatestAnswerAsync()
    {
        SetAiControlsEnabled(false);
        var answer = await GetLatestAnswerAsync();
        if (!string.IsNullOrWhiteSpace(answer))
        {
            try
            {
                Clipboard.SetText(answer);
                _aiStatus.Text = "コピー済";
            }
            catch (Exception ex)
            {
                _aiStatus.Text = $"コピー失敗: {ex.GetType().Name}";
                _aiStatus.ToolTip = ex.Message;
            }
        }
        SetAiControlsEnabled(true);
    }

    private void SetAiControlsEnabled(bool enabled)
    {
        _sendButton.IsEnabled = enabled;
        _copyAnswerButton.IsEnabled = enabled;
    }

    private void Navigate(string value)
    {
        var normalized = NormalizeUrl(value);
        _addressBar.Text = normalized;
        _homeUrl = normalized;
        UrlChanged?.Invoke(normalized);
        _webView.CoreWebView2?.Navigate(normalized);
    }

    private void UpdateNavigationButtons()
    {
        _backButton.IsEnabled = _webView.CanGoBack;
        _forwardButton.IsEnabled = _webView.CanGoForward;
    }

    private static Button MakeButton(string text, string tooltip) => new()
    {
        Content = text,
        ToolTip = tooltip,
        MinWidth = 34,
        Height = 30,
        Margin = new Thickness(0, 0, 4, 0),
        Padding = new Thickness(7, 2, 7, 2)
    };

    private static void AddToToolbar(Grid toolbar, UIElement control, int column)
    {
        Grid.SetColumn(control, column);
        toolbar.Children.Add(control);
    }

    public static string NormalizeUrl(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return "https://www.google.com/";
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString()
            : "https://www.google.com/";
    }
}

using Microsoft.Web.WebView2.Wpf;
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
    private string _homeUrl;

    public event Action<string>? UrlChanged;

    public BrowserPane(string initialUrl)
    {
        _homeUrl = NormalizeUrl(initialUrl);
        Background = System.Windows.Media.Brushes.White;
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

        _webView = new WebView2();
        Grid.SetRow(_webView, 1);
        Children.Add(_webView);

        _backButton.Click += (_, _) => { if (_webView.CanGoBack) _webView.GoBack(); };
        _forwardButton.Click += (_, _) => { if (_webView.CanGoForward) _webView.GoForward(); };
        reloadButton.Click += (_, _) => _webView.Reload();
        homeButton.Click += (_, _) => Navigate(_homeUrl);
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

        Loaded += async (_, _) =>
        {
            if (_webView.CoreWebView2 is not null)
                return;

            await _webView.EnsureCoreWebView2Async();
            var core = _webView.CoreWebView2;
            if (core is null)
                return;

            core.Settings.AreDevToolsEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsZoomControlEnabled = true;
            core.HistoryChanged += (_, _) => UpdateNavigationButtons();
            core.NavigationCompleted += (_, _) =>
            {
                if (_webView.Source is not null)
                    _addressBar.Text = _webView.Source.ToString();
                UpdateNavigationButtons();
            };
            Navigate(_homeUrl);
        };
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

    public void FocusAddressBar()
    {
        _addressBar.Focus();
        _addressBar.SelectAll();
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
        if (string.IsNullOrWhiteSpace(text))
            return "https://www.google.com/";

        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString()
            : "https://www.google.com/";
    }
}

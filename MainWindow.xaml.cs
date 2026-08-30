using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AiMultiWindow;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly BrowserPane[] _panes;
    private bool _isFullscreen;
    private WindowStyle _previousWindowStyle;
    private WindowState _previousWindowState;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        _panes = new BrowserPane[3];

        for (var i = 0; i < _panes.Length; i++)
        {
            var index = i;
            _panes[i] = new BrowserPane(_settings.Urls[i], i);
            _panes[i].UrlChanged += url =>
            {
                _settings.Urls[index] = url;
                _settings.Save();
            };
        }

        Loaded += (_, _) => BuildThreeColumnLayout();
        Closing += MainWindow_Closing;
    }

    private void BuildThreeColumnLayout()
    {
        foreach (var pane in _panes)
            DetachFromParent(pane);

        LayoutHost.Children.Clear();
        LayoutHost.RowDefinitions.Clear();
        LayoutHost.ColumnDefinitions.Clear();

        LayoutHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        LayoutHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        LayoutHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        LayoutHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        LayoutHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddPaneCard(0, 0);
        AddSplitter(1);
        AddPaneCard(1, 2);
        AddSplitter(3);
        AddPaneCard(2, 4);

        StatusText.Text = "3画面 — 境界線をドラッグして幅を変更できます";
    }

    private void AddPaneCard(int paneIndex, int column)
    {
        var card = new Grid { Background = Brushes.White };
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(249, 250, 251)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(209, 213, 219)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 6, 8, 6)
        };

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = $"Chat {paneIndex + 1}",
            FontWeight = FontWeights.Bold,
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center
        };

        var pasteButton = new Button
        {
            Content = "共通入力を貼付",
            Tag = paneIndex,
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            ToolTip = $"共通入力をChat {paneIndex + 1}へ入れます。送信はしません。"
        };
        pasteButton.Click += PasteToPane_Click;

        Grid.SetColumn(title, 0);
        Grid.SetColumn(pasteButton, 1);
        headerGrid.Children.Add(title);
        headerGrid.Children.Add(pasteButton);
        header.Child = headerGrid;

        Grid.SetRow(header, 0);
        Grid.SetRow(_panes[paneIndex], 1);
        card.Children.Add(header);
        card.Children.Add(_panes[paneIndex]);

        Grid.SetColumn(card, column);
        LayoutHost.Children.Add(card);
    }

    private void AddSplitter(int column)
    {
        var splitter = new GridSplitter
        {
            Width = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ShowsPreview = true
        };
        Grid.SetColumn(splitter, column);
        LayoutHost.Children.Add(splitter);
    }

    private void PasteAll_Click(object sender, RoutedEventArgs e)
    {
        var text = SharedPromptTextBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = "共通入力が空です";
            SharedPromptTextBox.Focus();
            return;
        }

        foreach (var pane in _panes)
            pane.SetPromptText(text);

        StatusText.Text = "共通入力をChat 1・2・3へ貼り付けました（未送信）";
    }

    private void PasteToPane_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int paneIndex } || paneIndex < 0 || paneIndex >= _panes.Length)
            return;

        var text = SharedPromptTextBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = "共通入力が空です";
            SharedPromptTextBox.Focus();
            return;
        }

        _panes[paneIndex].SetPromptText(text, focus: true);
        StatusText.Text = $"共通入力をChat {paneIndex + 1}へ貼り付けました（未送信）";
    }

    private void ResetLayout_Click(object sender, RoutedEventArgs e) => BuildThreeColumnLayout();

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
    }

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _previousWindowStyle = WindowStyle;
            _previousWindowState = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            _isFullscreen = true;
            StatusText.Text = "全画面表示 — F11で戻ります";
        }
        else
        {
            WindowStyle = _previousWindowStyle;
            WindowState = _previousWindowState;
            _isFullscreen = false;
            StatusText.Text = "3画面表示";
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        for (var i = 0; i < _panes.Length; i++)
            _settings.Urls[i] = _panes[i].HomeUrl;

        _settings.LayoutCount = 3;
        _settings.Save();
    }

    private static void DetachFromParent(UIElement element)
    {
        if (element.Parent is Panel panel)
            panel.Children.Remove(element);
        else if (element.Parent is Decorator decorator)
            decorator.Child = null;
        else if (element.Parent is ContentControl contentControl)
            contentControl.Content = null;
    }
}

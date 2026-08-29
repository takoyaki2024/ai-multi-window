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
    private int _layoutCount;
    private bool _isFullscreen;
    private WindowStyle _previousWindowStyle;
    private WindowState _previousWindowState;

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        _layoutCount = _settings.LayoutCount;
        _panes = new BrowserPane[4];

        for (var i = 0; i < 4; i++)
        {
            var index = i;
            _panes[i] = new BrowserPane(_settings.Urls[i]);
            _panes[i].UrlChanged += url =>
            {
                _settings.Urls[index] = url;
                _settings.Save();
            };
        }

        Loaded += (_, _) => ApplyLayout(_layoutCount);
        Closing += MainWindow_Closing;
    }

    private void LayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var count))
            ApplyLayout(count);
    }

    private void ApplyLayout(int count)
    {
        count = Math.Clamp(count, 1, 4);
        _layoutCount = count;
        _settings.LayoutCount = count;

        foreach (var pane in _panes)
            DetachFromParent(pane);

        LayoutHost.Children.Clear();
        LayoutHost.RowDefinitions.Clear();
        LayoutHost.ColumnDefinitions.Clear();

        FrameworkElement content = count switch
        {
            1 => BuildSingle(),
            2 => BuildTwoColumns(),
            3 => BuildThreePane(),
            _ => BuildFourPane()
        };

        LayoutHost.Children.Add(content);
        UpdateLayoutButtons();
        StatusText.Text = $"{count}分割 — 境界線をドラッグしてサイズ変更できます";
        _settings.Save();
    }

    private FrameworkElement BuildSingle()
    {
        var grid = CreateContainer();
        grid.Children.Add(_panes[0]);
        return grid;
    }

    private FrameworkElement BuildTwoColumns()
    {
        var grid = CreateContainer();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddAt(grid, _panes[0], 0, 0);
        var splitter = VerticalSplitter();
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);
        AddAt(grid, _panes[1], 0, 2);
        return grid;
    }

    private FrameworkElement BuildThreePane()
    {
        var grid = CreateContainer();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddAt(grid, _panes[0], 0, 0);

        var vertical = VerticalSplitter();
        Grid.SetColumn(vertical, 1);
        grid.Children.Add(vertical);

        var right = CreateContainer();
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        AddAt(right, _panes[1], 0, 0);
        var horizontal = HorizontalSplitter();
        Grid.SetRow(horizontal, 1);
        right.Children.Add(horizontal);
        AddAt(right, _panes[2], 2, 0);

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    private FrameworkElement BuildFourPane()
    {
        var grid = CreateContainer();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        AddAt(grid, _panes[0], 0, 0);
        AddAt(grid, _panes[1], 0, 2);
        AddAt(grid, _panes[2], 2, 0);
        AddAt(grid, _panes[3], 2, 2);

        var vertical = VerticalSplitter();
        Grid.SetColumn(vertical, 1);
        Grid.SetRowSpan(vertical, 3);
        grid.Children.Add(vertical);

        var horizontal = HorizontalSplitter();
        Grid.SetRow(horizontal, 1);
        Grid.SetColumnSpan(horizontal, 3);
        grid.Children.Add(horizontal);

        return grid;
    }

    private static Grid CreateContainer() => new()
    {
        Background = Brushes.Black,
        ClipToBounds = true
    };

    private static void AddAt(Grid grid, UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static GridSplitter VerticalSplitter() => new()
    {
        Width = 6,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        Background = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
        ResizeDirection = GridResizeDirection.Columns,
        ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        Cursor = Cursors.SizeWE,
        ShowsPreview = false
    };

    private static GridSplitter HorizontalSplitter() => new()
    {
        Height = 6,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        Background = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
        ResizeDirection = GridResizeDirection.Rows,
        ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        Cursor = Cursors.SizeNS,
        ShowsPreview = false
    };

    private static void DetachFromParent(UIElement element)
    {
        if (element is FrameworkElement { Parent: Panel panel })
            panel.Children.Remove(element);
    }

    private void UpdateLayoutButtons()
    {
        var buttons = new[] { Layout1Button, Layout2Button, Layout3Button, Layout4Button };
        for (var i = 0; i < buttons.Length; i++)
            buttons[i].FontWeight = i + 1 == _layoutCount ? FontWeights.Bold : FontWeights.Normal;
    }

    private void ResetLayout_Click(object sender, RoutedEventArgs e) => ApplyLayout(_layoutCount);

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _previousWindowStyle = WindowStyle;
            _previousWindowState = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            _isFullscreen = true;
            StatusText.Text = "全画面表示 — F11で戻る";
        }
        else
        {
            WindowStyle = _previousWindowStyle;
            WindowState = _previousWindowState;
            _isFullscreen = false;
            StatusText.Text = $"{_layoutCount}分割";
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            var count = e.Key switch
            {
                Key.D1 or Key.NumPad1 => 1,
                Key.D2 or Key.NumPad2 => 2,
                Key.D3 or Key.NumPad3 => 3,
                Key.D4 or Key.NumPad4 => 4,
                _ => 0
            };

            if (count > 0)
            {
                ApplyLayout(count);
                e.Handled = true;
            }
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _settings.LayoutCount = _layoutCount;
        for (var i = 0; i < _panes.Length; i++)
            _settings.Urls[i] = _panes[i].HomeUrl;
        _settings.Save();
    }
}

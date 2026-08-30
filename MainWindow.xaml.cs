using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AiMultiWindow;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly BrowserPane[] _panes;
    private readonly OrchestrationEngine _orchestrator;
    private int _layoutCount;
    private bool _isFullscreen;
    private WindowStyle _previousWindowStyle;
    private WindowState _previousWindowState;
    private readonly SemaphoreSlim _workflowLock = new(1, 1);
    private CancellationTokenSource? _workflowCancellation;
    private readonly DispatcherTimer _activityTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _activityStartedAt;
    private string _activityRole = "-";
    private string _activityStage = "待機中";
    private int _activityTimeoutSeconds;
    private bool _activityActive;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        _orchestrator = OrchestrationEngine.Load();
        _layoutCount = _settings.LayoutCount;
        _panes = new BrowserPane[4];

        for (var i = 0; i < 4; i++)
        {
            var index = i;
            _panes[i] = new BrowserPane(_settings.Urls[i], i);
            _panes[i].UrlChanged += url => { _settings.Urls[index] = url; _settings.Save(); };
        }

        _activityTimer.Tick += (_, _) => UpdateActivityUi();
        _activityTimer.Start();

        Loaded += (_, _) =>
        {
            ApplyLayout(_layoutCount);
            TaskTextBox.Text = _orchestrator.TaskText;
            WorkspaceTextBox.Text = Environment.CurrentDirectory;
            UpdateOrchestratorUi();
            UpdateActivityUi();
        };
        Closing += MainWindow_Closing;
    }

    private void LayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var count)) ApplyLayout(count);
    }

    private void ApplyLayout(int count)
    {
        count = Math.Clamp(count, 1, 4); _layoutCount = count; _settings.LayoutCount = count;
        foreach (var pane in _panes) DetachFromParent(pane);
        LayoutHost.Children.Clear(); LayoutHost.RowDefinitions.Clear(); LayoutHost.ColumnDefinitions.Clear();
        FrameworkElement content = count switch { 1 => BuildSingle(), 2 => BuildTwoColumns(), 3 => BuildThreePane(), _ => BuildFourPane() };
        LayoutHost.Children.Add(content); UpdateLayoutButtons();
        StatusText.Text = $"{count}分割 — 境界線をドラッグしてサイズ変更できます"; _settings.Save();
    }

    private FrameworkElement BuildSingle() { var grid = CreateContainer(); grid.Children.Add(_panes[0]); return grid; }

    private FrameworkElement BuildTwoColumns()
    {
        var grid = CreateContainer();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddAt(grid, _panes[0], 0, 0); var splitter = VerticalSplitter(); Grid.SetColumn(splitter, 1); grid.Children.Add(splitter); AddAt(grid, _panes[1], 0, 2); return grid;
    }

    private FrameworkElement BuildThreePane()
    {
        var grid = CreateContainer();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddAt(grid, _panes[0], 0, 0); var vertical = VerticalSplitter(); Grid.SetColumn(vertical, 1); grid.Children.Add(vertical);
        var right = CreateContainer(); right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) }); right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        AddAt(right, _panes[1], 0, 0); var horizontal = HorizontalSplitter(); Grid.SetRow(horizontal, 1); right.Children.Add(horizontal); AddAt(right, _panes[2], 2, 0); Grid.SetColumn(right, 2); grid.Children.Add(right); return grid;
    }

    private FrameworkElement BuildFourPane()
    {
        var grid = CreateContainer();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) }); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        AddAt(grid, _panes[0], 0, 0); AddAt(grid, _panes[1], 0, 2); AddAt(grid, _panes[2], 2, 0); AddAt(grid, _panes[3], 2, 2);
        var vertical = VerticalSplitter(); Grid.SetColumn(vertical, 1); Grid.SetRowSpan(vertical, 3); grid.Children.Add(vertical);
        var horizontal = HorizontalSplitter(); Grid.SetRow(horizontal, 1); Grid.SetColumnSpan(horizontal, 3); grid.Children.Add(horizontal); return grid;
    }

    private async void StartOrchestration_Click(object sender, RoutedEventArgs e)
    {
        if (!await _workflowLock.WaitAsync(0)) { StatusText.Text = "別の処理を実行中です"; return; }
        try
        {
        _workflowCancellation?.Cancel();
        _workflowCancellation?.Dispose();
        _workflowCancellation = new CancellationTokenSource();
        var task = TaskTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(task)) { StatusText.Text = "依頼を入力してください"; return; }

        var workspace = WorkspaceTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            StatusText.Text = "有効なWorkspaceを指定してください";
            return;
        }

        BeginActivity(null, "Workspace読み込み・復旧確認", 30);
        await WorkspaceExecutor.RecoverPendingAsync(workspace);
        if (WorkspaceExecutor.HasPendingChanges) await WorkspaceExecutor.RollbackPendingAsync();

        StatusText.Text = "Workspaceの実コードを読み込み中...";
        var workspaceContext = await WorkspaceContextBuilder.BuildAsync(workspace);
        _orchestrator.Start(task, workspaceContext);
        ApplyLayout(4);
        UpdateOrchestratorUi();
        await RunAutomaticWorkflowAsync(_workflowCancellation.Token);
        }
        catch (OperationCanceledException) { StatusText.Text = "処理をキャンセルしました"; EndActivity("キャンセル", false); }
        finally { _workflowLock.Release(); }
    }

    private async void StopOrchestration_Click(object sender, RoutedEventArgs e)
    {
        _workflowCancellation?.Cancel();
        await _workflowLock.WaitAsync();
        try
        {
        if (WorkspaceExecutor.HasPendingChanges) await WorkspaceExecutor.RollbackPendingAsync();
        if (_orchestrator.State == WorkflowState.Running) _orchestrator.Stop("ユーザーが停止しました");
        UpdateOrchestratorUi(); StatusText.Text = "司令塔を停止しました。未確定変更はロールバックしました。";
        EndActivity("停止", false);
        }
        finally { _workflowLock.Release(); }
    }

    private async void ResetOrchestration_Click(object sender, RoutedEventArgs e)
    {
        _workflowCancellation?.Cancel();
        await _workflowLock.WaitAsync();
        try
        {
        if (WorkspaceExecutor.HasPendingChanges) await WorkspaceExecutor.RollbackPendingAsync();
        _orchestrator.Reset(); TaskTextBox.Clear(); UpdateOrchestratorUi(); StatusText.Text = "司令塔をリセットしました。未確定変更はロールバックしました。";
        EndActivity("待機中", true);
        }
        finally { _workflowLock.Release(); }
    }

    private async void CaptureAndAdvance_Click(object sender, RoutedEventArgs e)
    {
        if (!await _workflowLock.WaitAsync(0)) { StatusText.Text = "別の処理を実行中です"; return; }
        var cancellationToken = _workflowCancellation?.Token ?? CancellationToken.None;
        if (_orchestrator.State != WorkflowState.Running) { StatusText.Text = "実行中のタスクがありません"; _workflowLock.Release(); return; }
        var role = _orchestrator.CurrentRole; var pane = _panes[(int)role];
        CaptureNextButton.IsEnabled = false; CaptureNextButton.Content = "回答取得中..."; StatusText.Text = $"{role} の回答を取得中...";
        BeginActivity(role, "回答取得中", 120);

        try
        {
            var answer = await pane.GetLatestAnswerAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(answer)) { StatusText.Text = $"{role} の回答を取得できませんでした"; EndActivity("回答取得失敗", false); return; }

            if (role == AgentRole.Coder)
            {
                var workspace = WorkspaceTextBox.Text.Trim();
                StatusText.Text = "Coder変更をWorkspaceへ適用してビルド中...";
                BeginActivity(role, "Workspace適用 / build / test", 120);
                var execution = await WorkspaceExecutor.ApplyCoderResponseAsync(workspace, answer);
                var executionText = execution.Summary + Environment.NewLine + execution.TestOutput;
                _orchestrator.SetExecutionResult(executionText, execution.Success);
                if (!execution.Success)
                    StatusText.Text = "ローカル適用/ビルドに失敗。変更をロールバックし、Reviewerへ結果を渡します。";
            }

            if (role == AgentRole.Planner)
            {
                BeginActivity(role, "Coder用コンテキスト作成", 30);
                var coderContext = await WorkspaceContextBuilder.BuildCoderAsync(WorkspaceTextBox.Text.Trim(), answer, cancellationToken);
                _orchestrator.SetCoderWorkspaceContext(coderContext);
            }

            var advanced = _orchestrator.RecordAnswer(answer);

            if (role == AgentRole.Reviewer)
            {
                if (_orchestrator.State == WorkflowState.Success)
                {
                    var commitResult = WorkspaceExecutor.CommitPending();
                    _orchestrator.SetExecutionResult(_orchestrator.ExecutionResult + Environment.NewLine + commitResult);
                    if (commitResult.Contains("FAILED", StringComparison.Ordinal)) _orchestrator.Stop(commitResult);
                }
                else if (_orchestrator.State == WorkflowState.Running && _orchestrator.CurrentRole == AgentRole.Coder)
                {
                    var rollbackResult = await WorkspaceExecutor.RollbackPendingAsync();
                    _orchestrator.SetExecutionResult(_orchestrator.ExecutionResult + Environment.NewLine + rollbackResult);
                    StatusText.Text = "Reviewer FAIL — 今回の変更をロールバックしてCoderへ戻します。";
                    BeginActivity(AgentRole.Coder, "Reviewer指摘後の再準備", 30);
                    var refreshed = await WorkspaceContextBuilder.BuildCoderAsync(WorkspaceTextBox.Text.Trim(), _orchestrator.GetAnswer(AgentRole.Planner), cancellationToken);
                    _orchestrator.SetCoderWorkspaceContext(refreshed);
                }
                else if (_orchestrator.State == WorkflowState.Stopped && WorkspaceExecutor.HasPendingChanges)
                {
                    await WorkspaceExecutor.RollbackPendingAsync();
                }
            }

            UpdateOrchestratorUi();
            if (_orchestrator.State == WorkflowState.Success) { StatusText.Text = "レビューPASS — ローカル変更を確定してワークフロー完了"; EndActivity("完了", true); return; }
            if (_orchestrator.State == WorkflowState.Stopped) { StatusText.Text = _orchestrator.StopReason; EndActivity("停止", false); return; }
            if (!advanced) { StatusText.Text = "回答を処理できなかったため停止しました"; EndActivity("回答処理失敗", false); return; }
            await SendCurrentStepAsync(cancellationToken);
        }
        catch (OperationCanceledException) { StatusText.Text = "処理をキャンセルしました"; EndActivity("キャンセル", false); }
        finally
        {
            CaptureNextButton.Content = "回答取得 → 次工程";
            CaptureNextButton.IsEnabled = _orchestrator.State == WorkflowState.Running;
            _workflowLock.Release();
        }
    }

    private async void ResendCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (!await _workflowLock.WaitAsync(0)) { StatusText.Text = "別の処理を実行中です"; return; }
        try
        {
        if (_orchestrator.State != WorkflowState.Running) { StatusText.Text = "再送できる実行中タスクがありません"; return; }
        await RunAutomaticWorkflowAsync(_workflowCancellation?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { StatusText.Text = "処理をキャンセルしました"; EndActivity("キャンセル", false); }
        finally { _workflowLock.Release(); }
    }

    private void ChatGptMode_Click(object sender, RoutedEventArgs e)
    {
        const string chatGptUrl = "https://chatgpt.com/";
        for (var i = 0; i < _panes.Length; i++) { _panes[i].HomeUrl = chatGptUrl; _panes[i].NavigateHome(); _settings.Urls[i] = chatGptUrl; }
        _settings.Save(); ApplyLayout(4); StatusText.Text = "4ペインを独立ChatGPTプロファイルに設定しました";
    }

    private async Task<bool> SendCurrentStepAsync(CancellationToken cancellationToken)
    {
        if (_orchestrator.State != WorkflowState.Running) return false;
        if (!_orchestrator.TryBeginPromptAttempt()) { UpdateOrchestratorUi(); StatusText.Text = _orchestrator.StopReason; EndActivity("送信上限で停止", false); return false; }
        var role = _orchestrator.CurrentRole; var pane = _panes[(int)role]; var prompt = _orchestrator.BuildCurrentPrompt();
        StatusText.Text = $"{role} へ送信中...";
        var sendTimeout = prompt.Length >= 20_000 ? 120 : 60;
        BeginActivity(role, $"プロンプト入力・送信中 ({prompt.Length:N0}文字)", sendTimeout);

        var sent = await pane.SendMessageAsync(prompt, cancellationToken);
        if (sent) _orchestrator.RecordPromptAccepted();

        StatusText.Text = sent
            ? $"{role} へ送信済み。回答完成後に「回答取得 → 次工程」を押してください。"
            : $"{role} への自動送信に失敗しました。状態を確認して再送してください。";
        if (sent) BeginActivity(role, "ChatGPT回答待ち", 120);
        else EndActivity("自動送信失敗", false);
        UpdateOrchestratorUi();
        return sent;
    }

    private async Task RunAutomaticWorkflowAsync(CancellationToken cancellationToken)
    {
        while (_orchestrator.State == WorkflowState.Running)
        {
            var sent = await SendCurrentStepAsync(cancellationToken);
            if (_orchestrator.State != WorkflowState.Running) return;
            var role = _orchestrator.CurrentRole;
            if (!sent) return; // Leave the workflow resumable for an explicit retry.

            StatusText.Text = $"{role} の回答完了を待機中...";
            BeginActivity(role, "ChatGPT回答生成・完了待ち", 120);
            var answer = await _panes[(int)role].GetLatestAnswerAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(answer)) { StatusText.Text = $"{role} の回答を取得できませんでした"; EndActivity("回答取得失敗", false); return; }

            if (role == AgentRole.Planner)
            {
                BeginActivity(role, "Coder用コンテキスト作成", 30);
                var context = await WorkspaceContextBuilder.BuildCoderAsync(WorkspaceTextBox.Text.Trim(), answer, cancellationToken);
                _orchestrator.SetCoderWorkspaceContext(context);
            }
            if (role == AgentRole.Coder)
            {
                StatusText.Text = "Coder変更を適用し、build/testを実行中...";
                BeginActivity(role, "Workspace適用 / build / test", 120);
                var execution = await WorkspaceExecutor.ApplyCoderResponseAsync(WorkspaceTextBox.Text.Trim(), answer, cancellationToken);
                _orchestrator.SetExecutionResult(execution.Summary + Environment.NewLine + execution.TestOutput, execution.Success);
            }

            var advanced = _orchestrator.RecordAnswer(answer);
            if (role == AgentRole.Reviewer)
            {
                if (_orchestrator.State == WorkflowState.Success)
                {
                    var commit = WorkspaceExecutor.CommitPending();
                    _orchestrator.SetExecutionResult(_orchestrator.ExecutionResult + Environment.NewLine + commit);
                    if (commit.Contains("FAILED", StringComparison.Ordinal)) _orchestrator.Stop(commit);
                }
                else if (_orchestrator.State == WorkflowState.Running && _orchestrator.CurrentRole == AgentRole.Coder)
                {
                    var rollback = await WorkspaceExecutor.RollbackPendingAsync();
                    _orchestrator.SetExecutionResult(_orchestrator.ExecutionResult + Environment.NewLine + rollback);
                    BeginActivity(AgentRole.Coder, "Reviewer指摘後の再準備", 30);
                    var refreshed = await WorkspaceContextBuilder.BuildCoderAsync(WorkspaceTextBox.Text.Trim(), _orchestrator.GetAnswer(AgentRole.Planner), cancellationToken);
                    _orchestrator.SetCoderWorkspaceContext(refreshed);
                }
                else if (_orchestrator.State == WorkflowState.Stopped && WorkspaceExecutor.HasPendingChanges)
                    await WorkspaceExecutor.RollbackPendingAsync();
            }
            UpdateOrchestratorUi();
            if (!advanced || _orchestrator.State != WorkflowState.Running)
            {
                StatusText.Text = _orchestrator.State == WorkflowState.Success ? "レビューPASS — ローカル変更を確定して完了" : _orchestrator.StopReason;
                EndActivity(_orchestrator.State == WorkflowState.Success ? "完了" : "停止", _orchestrator.State == WorkflowState.Success);
                return;
            }
        }
    }

    private void BeginActivity(AgentRole? role, string stage, int timeoutSeconds)
    {
        _activityRole = role is null ? "System" : $"{(int)role.Value + 1} {role.Value}";
        _activityStage = stage;
        _activityStartedAt = DateTime.Now;
        _activityTimeoutSeconds = Math.Max(1, timeoutSeconds);
        _activityActive = true;
        UpdateActivityUi();
    }

    private void EndActivity(string stage, bool success)
    {
        _activityStage = stage;
        _activityActive = false;
        ActivityHealthText.Text = success ? "⚪ 待機中 / 完了" : "🔴 停止 / 要確認";
        ActivityStageText.Text = $"処理: {stage}";
        ActivityHeartbeatText.Text = $"UI heartbeat: {DateTime.Now:HH:mm:ss}";
        ActivityProgressBar.Value = 0;
        ActivityTimeoutText.Text = "上限目安: -";
    }

    private void UpdateActivityUi()
    {
        if (!_activityActive)
        {
            ActivityRoleText.Text = $"工程: {_activityRole}";
            ActivityElapsedText.Text = "経過: 00:00";
            ActivityHeartbeatText.Text = $"UI heartbeat: {DateTime.Now:HH:mm:ss}";
            return;
        }

        var elapsed = DateTime.Now - _activityStartedAt;
        var elapsedSeconds = Math.Max(0, elapsed.TotalSeconds);
        var ratio = elapsedSeconds / _activityTimeoutSeconds;
        ActivityHealthText.Text = ratio >= 1.0
            ? "🔴 上限目安を超過 — 処理待機中"
            : ratio >= 0.70
                ? "🟡 時間がかかっています — UIは応答中"
                : "🟢 動作中 — UI heartbeat正常";
        ActivityRoleText.Text = $"工程: {_activityRole}";
        ActivityStageText.Text = $"処理: {_activityStage}";
        ActivityElapsedText.Text = $"経過: {elapsed:mm\\:ss}";
        ActivityHeartbeatText.Text = $"UI heartbeat: {DateTime.Now:HH:mm:ss} (1秒更新)";
        ActivityProgressBar.Maximum = _activityTimeoutSeconds;
        ActivityProgressBar.Value = Math.Min(elapsedSeconds, _activityTimeoutSeconds);
        ActivityTimeoutText.Text = $"上限目安: {_activityTimeoutSeconds}秒";
    }

    private void UpdateOrchestratorUi()
    {
        WorkflowStateText.Text = $"State: {_orchestrator.State}"; CurrentRoleText.Text = $"Role: {_orchestrator.CurrentRole}";
        AiCallsText.Text = $"AI Calls: {_orchestrator.AiCalls} / {_orchestrator.MaxAiCalls} (試行 {_orchestrator.SendAttempts})"; FixAttemptsText.Text = $"Fix: {_orchestrator.FixAttempts} / {_orchestrator.MaxFixAttempts}"; StopReasonText.Text = _orchestrator.StopReason;
        var running = _orchestrator.State == WorkflowState.Running; CaptureNextButton.IsEnabled = running; ResendButton.IsEnabled = running;
    }

    private static Grid CreateContainer() => new() { Background = Brushes.Black, ClipToBounds = true };
    private static void AddAt(Grid grid, UIElement element, int row, int column) { Grid.SetRow(element, row); Grid.SetColumn(element, column); grid.Children.Add(element); }
    private static GridSplitter VerticalSplitter() => new() { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = new SolidColorBrush(Color.FromRgb(31, 41, 55)), ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Cursor = Cursors.SizeWE, ShowsPreview = false };
    private static GridSplitter HorizontalSplitter() => new() { Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = new SolidColorBrush(Color.FromRgb(31, 41, 55)), ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Cursor = Cursors.SizeNS, ShowsPreview = false };
    private static void DetachFromParent(UIElement element) { if (element is FrameworkElement { Parent: Panel panel }) panel.Children.Remove(element); }

    private void UpdateLayoutButtons()
    {
        var buttons = new[] { Layout1Button, Layout2Button, Layout3Button, Layout4Button };
        for (var i = 0; i < buttons.Length; i++) buttons[i].FontWeight = i + 1 == _layoutCount ? FontWeights.Bold : FontWeights.Normal;
    }

    private void ResetLayout_Click(object sender, RoutedEventArgs e) => ApplyLayout(_layoutCount);
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (!_isFullscreen) { _previousWindowStyle = WindowStyle; _previousWindowState = WindowState; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; _isFullscreen = true; StatusText.Text = "全画面表示 — F11で戻る"; }
        else { WindowStyle = _previousWindowStyle; WindowState = _previousWindowState; _isFullscreen = false; StatusText.Text = $"{_layoutCount}分割"; }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; return; }
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            var count = e.Key switch { Key.D1 or Key.NumPad1 => 1, Key.D2 or Key.NumPad2 => 2, Key.D3 or Key.NumPad3 => 3, Key.D4 or Key.NumPad4 => 4, _ => 0 };
            if (count > 0) { ApplyLayout(count); e.Handled = true; }
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _activityTimer.Stop();
        _settings.LayoutCount = _layoutCount; for (var i = 0; i < _panes.Length; i++) _settings.Urls[i] = _panes[i].HomeUrl;
        _settings.Save(); _orchestrator.Save();
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AadcCommandCenter;

public partial class MainWindow : Window
{
    private readonly DashboardData _data = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _scan = new() { Interval = TimeSpan.FromMilliseconds(35) };
    private readonly DispatcherTimer _commandStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly PipeCommandClient _pipeClient = new();
    private readonly Dictionary<uint, uint> _targetFcsSelections = new();
    private readonly HashSet<uint> _assignedTargets = new();
    private readonly Dictionary<uint, AssignmentView> _assignmentsByTarget = new();
    private readonly HashSet<uint> _pendingFcsAssignments = new();
    private SharedMemoryFrameReader? _frameReader;
    private double _scanAngle = -135;
    private int _scanDirection = 1;
    private bool _isSharedMemoryLive;
    private bool _isCommandPipeLive;
    private bool _isRefreshingCommandStatus;
    private bool? _previousConnectionState;
    private AssignmentState? _previousAssignmentState;
    private uint? _selectedTargetId;
    private double _workspaceZoom = 1.0;
    private bool _workspaceLayoutRefreshQueued;
    private readonly string _eventLogPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AadcCommandCenter", "event-log.txt");

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _data;
        RdrSelector.ItemsSource = _data.RdrAssets;
        FcsSelector.ItemsSource = _data.FcsAssets;
        TargetGrid.ItemsSource = _data.Targets;
        AssignmentGrid.ItemsSource = _data.Assignments;
        LoadPersistentEventLog();
        AddLog($"프로그램 시작 · 로그파일 {_eventLogPath}");
        _frameReader = new SharedMemoryFrameReader();
        _frameReader.StateChanged += state => Dispatcher.BeginInvoke(() =>
        {
            var isLive = state.StartsWith("LIVE", StringComparison.Ordinal);
            if (_previousConnectionState != isLive)
            {
                if (isLive)
                    AddLog(_previousConnectionState == false ? "공유 메모리 연결 시작 · 재연결" : "공유 메모리 연결 시작");
                else if (_previousConnectionState == true)
                    AddLog($"공유 메모리 연결 중단 · {state}");
                _previousConnectionState = isLive;
            }
            _isSharedMemoryLive = isLive;
            SharedMemoryStatus.Text = state;
            SharedMemoryStatus.Foreground = isLive
                ? new SolidColorBrush(Color.FromRgb(0, 230, 138))
                : new SolidColorBrush(Color.FromRgb(255, 176, 0));
            if (isLive) _scan.Start(); else _scan.Stop();
            RefreshRdrInfo(RdrSelector.SelectedItem as RdrAsset);
            RefreshFcsInfo(FcsSelector.SelectedItem as FcsAsset);
            RefreshAssignments();
        });
        _frameReader.FrameReceived += frame => Dispatcher.BeginInvoke(() => ApplyFrame(frame));
        Loaded += async (_, _) => { UpdateClock(); DrawRadar(); _clock.Start(); _frameReader.Start(); _commandStatusTimer.Start(); QueueWorkspaceLayoutRefresh(); await RefreshCommandStatusAsync(); };
        Closed += (_, _) => { _clock.Stop(); _scan.Stop(); _commandStatusTimer.Stop(); _frameReader.Stop(); _frameReader.Dispose(); _pipeClient.Dispose(); };
        _clock.Tick += (_, _) => UpdateClock();
        _commandStatusTimer.Tick += async (_, _) => await RefreshCommandStatusAsync();
        _scan.Tick += (_, _) =>
        {
            _scanAngle += _scanDirection * 1.2;
            if (_scanAngle >= 135 || _scanAngle <= -135) { _scanAngle = Math.Clamp(_scanAngle, -135, 135); _scanDirection *= -1; }
            var radians = _scanAngle * Math.PI / 180;
            RadarSweep.X2 = 130 + 120 * Math.Sin(radians);
            RadarSweep.Y2 = 145 - 120 * Math.Cos(radians);
        };
    }

    private void ApplyFrame(SharedFrame frame)
    {
        var payload = frame.payload;
        // The sender's selectedFcsId is a global asset selection, not a per-target
        // assignment. Preserve the operator's choice by target ID across frames,
        // including targets that temporarily disappear from the incoming frame.
        foreach (var existingTarget in _data.Targets)
            _targetFcsSelections[existingTarget.Id] = existingTarget.SelectedFcsId;

        var rdrs = payload.rdrAssets.Take((int)payload.rdrCount)
            .Select(x => new RdrAsset(x.rdrId, x.isEnabled != 0, x.isOperating != 0, x.isConnected != 0, x.azimuthDeg, x.elevationDeg)).ToArray();
        var fcs = payload.fcsAssets.Take((int)payload.fcsCount)
            .Select(x => new FcsAsset(x.fcsId, x.isEnabled != 0, x.isOperating != 0, x.isConnected != 0, x.laserOn != 0, x.azimuthDeg, x.elevationDeg)).ToArray();
        var targets = payload.targets.Take((int)payload.targetCount).Where(x => x.valid == 1)
            .Select(x => new TargetRecord(x.targetId, x.azimuthDeg, x.elevationDeg, x.distanceM, x.lidarX, x.lidarY, x.lidarZ, x.rdrId, x.status, x.valid == 1,
                _targetFcsSelections.TryGetValue(x.targetId, out var selectedFcsId) ? selectedFcsId : payload.selectedFcsId,
                _assignedTargets.Contains(x.targetId))).ToArray();

        SyncById(_data.RdrAssets, rdrs, x => x.Id, (current, next) => current.UpdateFrom(next));
        SyncById(_data.FcsAssets, fcs, x => x.Id, (current, next) => current.UpdateFrom(next));
        SyncById(_data.Targets, targets, x => x.Id, (current, next) => current.UpdateFrom(next));
        RdrSelector.SelectedItem = _data.RdrAssets.FirstOrDefault(x => x.Id == payload.selectedRdrId) ?? _data.RdrAssets.FirstOrDefault();
        FcsSelector.SelectedItem = _data.FcsAssets.FirstOrDefault(x => x.Id == payload.selectedFcsId) ?? _data.FcsAssets.FirstOrDefault();
        RefreshRdrInfo(RdrSelector.SelectedItem as RdrAsset);
        RefreshFcsInfo(FcsSelector.SelectedItem as FcsAsset);
        RefreshAssignments();
        TargetCountText.Text = $"{_data.Targets.Count} / {SharedMemoryProtocol.TargetCapacity}";
        DrawRadar();
        QueueWorkspaceLayoutRefresh();
    }

    private static void SyncById<T>(ObservableCollection<T> current, IReadOnlyCollection<T> incoming, Func<T, uint> getId, Action<T, T> update)
    {
        var ids = incoming.Select(getId).ToHashSet();
        for (var i = current.Count - 1; i >= 0; i--)
            if (!ids.Contains(getId(current[i]))) current.RemoveAt(i);

        foreach (var next in incoming)
        {
            var existing = current.FirstOrDefault(item => getId(item) == getId(next));
            if (existing is null) current.Add(next);
            else update(existing, next);
        }
    }

    private void UpdateClock() => ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");

    private void ZoomInClick(object sender, RoutedEventArgs e) => SetWorkspaceZoom(_workspaceZoom + 0.1);
    private void ZoomOutClick(object sender, RoutedEventArgs e) => SetWorkspaceZoom(_workspaceZoom - 0.1);
    private void ZoomResetClick(object sender, RoutedEventArgs e) => SetWorkspaceZoom(1.0);

    private void SetWorkspaceZoom(double zoom)
    {
        _workspaceZoom = Math.Clamp(Math.Round(zoom, 2), 0.5, 1.5);
        WorkspaceScaleTransform.ScaleX = _workspaceZoom;
        WorkspaceScaleTransform.ScaleY = _workspaceZoom;
        ZoomResetButton.Content = $"{_workspaceZoom:P0}";
        FitWorkspaceWidth();
        QueueWorkspaceLayoutRefresh();
        AddLog($"작업 영역 배율 변경 · {_workspaceZoom:P0}");
    }

    private void WorkspaceScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        FitWorkspaceWidth();
        QueueWorkspaceLayoutRefresh();
    }

    private void FitWorkspaceWidth()
    {
        if (WorkspaceScrollViewer.ViewportWidth <= 0) return;
        const double minimumWorkspaceWidth = 962; // left + center minimum + right panels and splitters
        var width = Math.Max(minimumWorkspaceWidth, WorkspaceScrollViewer.ViewportWidth / _workspaceZoom);
        if (double.IsNaN(WorkspaceLayout.Width) || Math.Abs(WorkspaceLayout.Width - width) > 1)
            WorkspaceLayout.Width = width;
    }

    private void QueueWorkspaceLayoutRefresh()
    {
        if (_workspaceLayoutRefreshQueued) return;
        _workspaceLayoutRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _workspaceLayoutRefreshQueued = false;
            FitWorkspaceWidth();
            WorkspaceLayout.InvalidateMeasure();
            TargetGrid.InvalidateMeasure();
            AssignmentGrid.InvalidateMeasure();
            WorkspaceLayout.UpdateLayout();
            RecalculateStarColumns(TargetGrid);
            RecalculateStarColumns(AssignmentGrid);
        }));
    }

    private static void RecalculateStarColumns(DataGrid grid)
    {
        foreach (var column in grid.Columns.Where(column => column.Width.IsStar))
        {
            var starWidth = column.Width;
            column.Width = DataGridLength.Auto;
            column.Width = starWidth;
        }
        grid.UpdateLayout();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        switch (e.Key)
        {
            case Key.OemPlus:
            case Key.Add:
                SetWorkspaceZoom(_workspaceZoom + 0.1);
                e.Handled = true;
                break;
            case Key.OemMinus:
            case Key.Subtract:
                SetWorkspaceZoom(_workspaceZoom - 0.1);
                e.Handled = true;
                break;
            case Key.D0:
            case Key.NumPad0:
                SetWorkspaceZoom(1.0);
                e.Handled = true;
                break;
        }
    }

    private void Workspace_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        SetWorkspaceZoom(_workspaceZoom + (e.Delta > 0 ? 0.05 : -0.05));
        e.Handled = true;
    }

    private void RdrSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshRdrInfo(RdrSelector.SelectedItem as RdrAsset);
    }

    private void RefreshRdrInfo(RdrAsset? rdr)
    {
        if (RdrIdText is null) return;
        if (!_isSharedMemoryLive || rdr is null)
        {
            RdrIdText.Text = rdr?.DisplayName ?? "NULL";
            RdrEnabledText.Text = RdrOperatingText.Text = RdrConnectedText.Text = RdrAzText.Text = RdrElText.Text = "NULL";
            SetRdrValueForeground(Brushes.Orange);
            RdrStatusLine.Text = "공유 메모리 대기 · NULL";
            RdrStatusLine.Foreground = Brushes.Orange;
            return;
        }
        RdrIdText.Text = rdr.DisplayName;
        RdrEnabledText.Text = YesNo(rdr.IsEnabled); RdrOperatingText.Text = YesNo(rdr.IsOperating); RdrConnectedText.Text = YesNo(rdr.IsConnected);
        RdrAzText.Text = $"{rdr.AzimuthDeg:0}°"; RdrElText.Text = $"{rdr.ElevationDeg:0}°";
        SetRdrValueForeground(new SolidColorBrush(Color.FromRgb(0, 230, 138)));
        RdrStatusLine.Text = $"{rdr.DisplayName} · {(rdr.IsConnected ? "CONNECTED" : "DISCONNECTED")} · {(rdr.IsOperating ? "OPERATING" : "STOPPED")}";
        RdrStatusLine.Foreground = rdr.IsConnected && rdr.IsOperating ? new SolidColorBrush(Color.FromRgb(0, 230, 138)) : Brushes.Orange;
        DrawRadar();
    }

    private void SetRdrValueForeground(Brush brush)
    {
        RdrIdText.Foreground = RdrEnabledText.Foreground = RdrOperatingText.Foreground = RdrConnectedText.Foreground = RdrAzText.Foreground = RdrElText.Foreground = brush;
    }

    private void FcsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshFcsInfo(FcsSelector.SelectedItem as FcsAsset);
    }

    private void RefreshFcsInfo(FcsAsset? fcs)
    {
        if (FcsIdText is null) return;
        if (!_isSharedMemoryLive || fcs is null)
        {
            FcsIdText.Text = fcs?.DisplayName ?? "NULL";
            FcsEnabledText.Text = FcsOperatingText.Text = FcsConnectedText.Text = FcsLaserText.Text = FcsAzText.Text = FcsElText.Text = "NULL";
            SetFcsValueForeground(Brushes.Orange);
            return;
        }
        FcsIdText.Text = fcs.DisplayName;
        FcsEnabledText.Text = YesNo(fcs.IsEnabled); FcsOperatingText.Text = YesNo(fcs.IsOperating); FcsConnectedText.Text = YesNo(fcs.IsConnected);
        FcsLaserText.Text = fcs.LaserOn ? "ON" : "OFF";
        FcsAzText.Text = $"{fcs.AzimuthDeg:0}°"; FcsElText.Text = $"{fcs.ElevationDeg:0}°";
        SetFcsValueForeground(new SolidColorBrush(Color.FromRgb(0, 230, 138)));
    }

    private void SetFcsValueForeground(Brush brush)
    {
        FcsIdText.Foreground = FcsEnabledText.Foreground = FcsOperatingText.Foreground = FcsConnectedText.Foreground = FcsLaserText.Foreground = FcsAzText.Foreground = FcsElText.Foreground = brush;
    }

    private static string YesNo(bool value) => value ? "YES" : "NO";

    private void DrawRadar()
    {
        if (RadarCanvas is null) return;
        foreach (var dot in RadarCanvas.Children.OfType<Ellipse>().Where(x => x.Tag is uint).ToArray()) RadarCanvas.Children.Remove(dot);
        var rdr = RdrSelector.SelectedItem as RdrAsset;
        if (rdr is null) return;
        foreach (var target in _data.Targets.Where(x => x.Valid != 0 && x.RdrId == rdr.Id))
        {
            var signedAz = target.AzimuthDeg - 135d;
            var angle = signedAz * Math.PI / 180;
            var radius = Math.Clamp(target.DistanceM / 4000d * 118d, 8, 118);
            var x = 130 + radius * Math.Sin(angle); var y = 145 - radius * Math.Cos(angle);
            var isSelected = _selectedTargetId == target.Id;
            var dot = new Ellipse
            {
                Width = isSelected ? 12 : 8,
                Height = isSelected ? 12 : 8,
                Fill = isSelected ? new SolidColorBrush(Color.FromRgb(0, 162, 255)) : target.Status == 1 ? Brushes.Gray : target.Status == 2 ? Brushes.Orange : new SolidColorBrush(Color.FromRgb(0, 230, 138)),
                Stroke = isSelected ? Brushes.LightCyan : Brushes.Transparent,
                StrokeThickness = isSelected ? 1.5 : 0,
                Cursor = Cursors.Hand,
                Tag = target.Id,
                ToolTip = target.DisplayName
            };
            dot.MouseLeftButtonDown += RadarTargetMouseLeftButtonDown;
            Canvas.SetLeft(dot, x - dot.Width / 2); Canvas.SetTop(dot, y - dot.Height / 2); RadarCanvas.Children.Add(dot);
        }
    }

    private void RadarTargetMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Ellipse { Tag: uint targetId }) return;
        var target = _data.Targets.FirstOrDefault(x => x.Id == targetId);
        if (target is null) return;
        _selectedTargetId = targetId;
        TargetGrid.SelectedItem = target;
        TargetGrid.ScrollIntoView(target);
        DrawRadar();
        AddLog($"레이더 표적 선택 · {target.DisplayName}");
        e.Handled = true;
    }

    private void TargetGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedId = (TargetGrid.SelectedItem as TargetRecord)?.Id;
        if (_selectedTargetId == selectedId) return;
        _selectedTargetId = selectedId;
        DrawRadar();
    }

    private async void TargetAssignClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureSharedMemoryLiveForCommand("표적 할당")) return;
        if ((sender as FrameworkElement)?.DataContext is not TargetRecord target) { AddLog("표적 할당 거부 · 표적 행을 확인할 수 없음"); return; }
        AddLog($"표적 할당 버튼 클릭 · {target.DisplayName}");
        var fcs = _data.FcsAssets.FirstOrDefault(x => x.Id == target.SelectedFcsId);
        if (fcs is null)
        {
            AddLog($"표적 할당 거부 · {target.DisplayName} 타격자산 미선택");
            ShowAlert("타격자산을 먼저 선택하세요.", "표적 할당");
            return;
        }
        if (target.Status != 0)
        {
            AddLog($"표적 할당 거부 · {target.DisplayName} 상태 {target.StatusText}");
            ShowAlert("생존 상태인 표적만 할당할 수 있습니다.", "표적 할당");
            return;
        }
        var occupiedAssignment = _data.Assignments.FirstOrDefault(x => x.FcsId == fcs.Id && x.TargetId != target.Id && x.IsActiveCommandState);
        if (occupiedAssignment is not null)
        {
            AddLog($"표적 할당 거부 · {fcs.DisplayName} 이미 {occupiedAssignment.TargetName}에 할당됨");
            ShowAlert($"{fcs.DisplayName}은(는) 이미 {occupiedAssignment.TargetName}에 할당된 타격자산입니다.\n기존 할당을 취소한 뒤 다시 시도하세요.", "이미 할당된 타격자산");
            return;
        }
        if (_pendingFcsAssignments.Contains(fcs.Id))
        {
            AddLog($"표적 할당 중복 클릭 무시 · {fcs.DisplayName} 연결 시도 중");
            return;
        }
        if (_assignmentsByTarget.TryGetValue(target.Id, out var currentAssignment) && currentAssignment.FcsId == fcs.Id && !currentAssignment.CanRetry)
        {
            AddLog($"표적 할당 거부 · {target.DisplayName}에 {fcs.DisplayName} 이미 할당됨");
            ShowAlert($"{fcs.DisplayName}은(는) 이미 {target.DisplayName}에 할당되어 있습니다.", "중복 할당");
            return;
        }

        var rdr = _data.RdrAssets.FirstOrDefault(x => x.Id == target.RdrId);
        if (rdr is null || !rdr.IsConnected || !rdr.IsOperating)
        {
            AddLog($"표적 할당 거부 · {target.DisplayName} 탐지 레이더 연결/구동 불가");
            ShowAlert("탐지 레이더의 연결 및 구동 상태를 확인하세요.", "표적 할당");
            return;
        }

        if (!_assignmentsByTarget.TryGetValue(target.Id, out var assignment))
        {
            assignment = new AssignmentView(target.Id, fcs.Id);
            _assignmentsByTarget[target.Id] = assignment;
            _data.Assignments.Add(assignment);
        }
        else assignment.FcsId = fcs.Id;
        assignment.SetConnecting();
        target.IsAssignmentPending = true;
        _pendingFcsAssignments.Add(fcs.Id);
        RefreshAssignments();
        AddLog($"{target.DisplayName} → {fcs.DisplayName} 명령 파이프 연결 시도 중");

        PipeResponse? response;
        try { response = await SendPipeCommandAsync(CommandType.AssignTarget, target.RdrId, fcs.Id, target.Id, "표적 할당", showConnectionAlert: false); }
        finally
        {
            _pendingFcsAssignments.Remove(fcs.Id);
            target.IsAssignmentPending = false;
        }
        if (response is null)
        {
            _assignmentsByTarget.Remove(target.Id);
            _assignedTargets.Remove(target.Id);
            target.IsAssigned = false;
            target.IsAssignmentPending = false;
            if (assignment is not null) _data.Assignments.Remove(assignment);
            RefreshAssignments();
            AddLog($"{target.DisplayName} 표적 할당 실패 · 연결 불가, 할당 정보 제거");
            return;
        }
        ApplyCommandResponse(response.Value);
        if ((ResultCode)response.Value.ResultCode != ResultCode.Success)
        {
            if (response.Value.AssignedTargetId != target.Id && _assignmentsByTarget.Remove(target.Id, out var rejectedRow))
            {
                _data.Assignments.Remove(rejectedRow);
                target.IsAssigned = false;
                RefreshAssignmentEmptyState();
            }
            var message = CommandResultText((ResultCode)response.Value.ResultCode);
            AddLog($"표적 할당 거부 · {target.DisplayName} · {message}");
            ShowAlert(message, "표적 할당 거부");
            return;
        }
        AddLog($"표적 할당 명령 접수 · {target.DisplayName} → {fcs.DisplayName} · {AssignmentStateText((AssignmentState)response.Value.AssignmentState)}");
    }

    private async void CancelAssignmentClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureSharedMemoryLiveForCommand("표적 할당 취소")) return;
        if ((sender as FrameworkElement)?.DataContext is not AssignmentView assignment) { AddLog("표적 할당 취소 거부 · 할당 행을 확인할 수 없음"); return; }
        AddLog($"표적 할당 취소 버튼 클릭 · {assignment.TargetName}");
        var target = _data.Targets.FirstOrDefault(x => x.Id == assignment.TargetId);
        uint rdrId = target?.RdrId ?? 1;
        var response = await SendPipeCommandAsync(CommandType.CancelAssignment, rdrId, assignment.FcsId, assignment.TargetId, "표적 할당 취소");
        if (response is null) return;
        ApplyCommandResponse(response.Value);
        if ((ResultCode)response.Value.ResultCode != ResultCode.Success)
        {
            var message = CommandResultText((ResultCode)response.Value.ResultCode);
            AddLog($"표적 할당 취소 거부 · {assignment.TargetName} · {message}");
            ShowAlert(message, "표적 할당 취소 거부");
            return;
        }
        AddLog($"표적 할당 취소 명령 접수 · {assignment.TargetName}");
    }

    private async void FireAssignmentClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureSharedMemoryLiveForCommand("발사")) return;
        if ((sender as FrameworkElement)?.DataContext is not AssignmentView assignment) { AddLog("발사 거부 · 할당 행을 확인할 수 없음"); return; }
        AddLog($"발사 버튼 클릭 · {assignment.TargetName}");
        var targetName = assignment.TargetName;
        var fcs = _data.FcsAssets.FirstOrDefault(x => x.Id == assignment.FcsId);
        var unmetConditions = new List<string>();
        if (!assignment.CanFire) unmetConditions.Add($"현재 할당 상태: {assignment.StateText} (할당 완료 필요)");
        if (fcs is null) unmetConditions.Add("타격자산 데이터를 확인할 수 없습니다");
        if (unmetConditions.Count > 0)
        {
            AddLog($"발사 거부 · {targetName} · {string.Join(", ", unmetConditions)}");
            ShowAlert(string.Join(Environment.NewLine, unmetConditions), "발사 조건 미충족");
            return;
        }
        if (fcs is null) return;
        var target = _data.Targets.FirstOrDefault(x => x.Id == assignment.TargetId);
        var response = await SendPipeCommandAsync(CommandType.Fire, target?.RdrId ?? 1, assignment.FcsId, assignment.TargetId, "발사");
        if (response is null) return;
        ApplyCommandResponse(response.Value);
        if ((ResultCode)response.Value.ResultCode != ResultCode.Success)
        {
            var message = CommandResultText((ResultCode)response.Value.ResultCode);
            AddLog($"발사 거부 · {targetName} · {message}");
            ShowAlert(message, "발사 거부");
            return;
        }
        AddLog($"발사 명령 응답 · {targetName} → {fcs.DisplayName} · {(response.Value.FireState == 1 ? "발사 완료" : "발사 접수")}");
    }

    private void ShowAlert(string message, string title) =>
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    private async Task RefreshCommandStatusAsync()
    {
        if (_isRefreshingCommandStatus || _pendingFcsAssignments.Count > 0) return;
        _isRefreshingCommandStatus = true;
        try
        {
            var response = await _pipeClient.SendAsync(CommandType.GetStatus, rdrId: 1, fcsId: 1);
            SetCommandPipeOnline(true);
            ApplyCommandResponse(response);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            SetCommandPipeOnline(false);
        }
        finally { _isRefreshingCommandStatus = false; }
    }

    private async Task<PipeResponse?> SendPipeCommandAsync(CommandType command, uint rdrId, uint fcsId, uint targetId, string action, bool showConnectionAlert = true)
    {
        try
        {
            var response = await _pipeClient.SendAsync(command, targetId, rdrId, fcsId);
            SetCommandPipeOnline(true);
            return response;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            SetCommandPipeOnline(false);
            AddLog($"{action} 명령 전송 실패 · Named Pipe 서버 연결 불가");
            if (showConnectionAlert)
                ShowAlert("명령 파이프 서버에 연결할 수 없습니다.\n모의 통제소 프로그램이 실행 중인지 확인하세요.", "명령 전송 실패");
            return null;
        }
    }

    private void SetCommandPipeOnline(bool online)
    {
        if (_isCommandPipeLive == online)
        {
            return;
        }
        _isCommandPipeLive = online;
        AddLog(online ? "명령 파이프 연결됨" : "명령 파이프 연결 끊김 · 명령 응답 대기");
        if (!online)
        {
            foreach (var assignment in _data.Assignments) assignment.SetCommandUnavailable();
            RefreshAssignments();
        }
    }

    private void ApplyCommandResponse(PipeResponse response)
    {
        var state = (AssignmentState)response.AssignmentState;
        if (response.AssignedTargetId == 0 && state == AssignmentState.Idle &&
            !(response.CommandType == (uint)CommandType.GetStatus && _pendingFcsAssignments.Contains(response.FcsId)))
        {
            foreach (var current in _data.Assignments.ToArray())
            {
                _assignmentsByTarget.Remove(current.TargetId);
                _assignedTargets.Remove(current.TargetId);
                var oldTarget = _data.Targets.FirstOrDefault(x => x.Id == current.TargetId);
                if (oldTarget is not null) { oldTarget.IsAssigned = false; oldTarget.IsAssignmentPending = false; }
                _data.Assignments.Remove(current);
            }
            RefreshAssignmentEmptyState();
            _previousAssignmentState = state;
            return;
        }

        if (response.AssignedTargetId != 0)
        {
            if (!_assignmentsByTarget.TryGetValue(response.AssignedTargetId, out var assignment))
            {
                assignment = new AssignmentView(response.AssignedTargetId, response.FcsId);
                _assignmentsByTarget[response.AssignedTargetId] = assignment;
                _data.Assignments.Add(assignment);
            }
            assignment.FcsId = response.FcsId;
            assignment.SetCommandStatus(state, response.FireState);
            _assignedTargets.Add(response.AssignedTargetId);
            var target = _data.Targets.FirstOrDefault(x => x.Id == response.AssignedTargetId);
            if (target is not null)
            {
                target.IsAssigned = true;
                target.SelectedFcsId = response.FcsId;
            }
        }

        if (_previousAssignmentState != state)
        {
            AddLog($"명령 서버 표적할당 상태 · {AssignmentStateText(state)} · {TargetName(response.AssignedTargetId)}");
            _previousAssignmentState = state;
        }
        RefreshAssignments();
    }

    private static string TargetName(uint targetId) => targetId == 0 ? "없음" : $"TGT-{targetId}";

    private static string AssignmentStateText(AssignmentState state) => state switch
    {
        AssignmentState.Idle => "대기",
        AssignmentState.Adjusting => "타격자산 조준 중",
        AssignmentState.Assigned => "할당 완료",
        AssignmentState.Failed => "할당 실패",
        _ => "알 수 없음"
    };

    private static string CommandResultText(ResultCode result) => result switch
    {
        ResultCode.Success => "명령 접수",
        ResultCode.InvalidRequest => "요청 값이 올바르지 않습니다",
        ResultCode.AssetUnavailable => "타격자산을 사용할 수 없습니다",
        ResultCode.NotAssigned => "타격자산에 할당된 표적이 없습니다",
        ResultCode.AlreadyAssigned => "타격자산에 이미 다른 표적이 할당되어 있습니다",
        ResultCode.NotSupported => "지원하지 않는 명령입니다",
        ResultCode.DeviceError => "장치 처리 오류입니다",
        _ => "알 수 없는 명령 응답입니다"
    };

    private bool EnsureSharedMemoryLiveForCommand(string action)
    {
        if (_isSharedMemoryLive) return true;
        AddLog($"{action} 버튼 클릭 · 공유 메모리 대기 · 명령 거부");
        ShowAlert("공유 메모리 대기 상태입니다.\n명령을 수행할 수 없습니다.", "공유 메모리 대기");
        return false;
    }

    private void OpenEventLogClick(object sender, RoutedEventArgs e)
    {
        AddLog("로그파일 열기 버튼 클릭");
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                UseShellExecute = false,
                ArgumentList = { _eventLogPath }
            });
            AddLog("로그파일을 메모장으로 열었습니다");
        }
        catch (Exception ex)
        {
            AddLog($"로그파일 열기 실패 · {ex.Message}");
            ShowAlert($"로그파일을 열 수 없습니다.\n{ex.Message}", "로그파일 열기 실패");
        }
    }

    private void LoadPersistentEventLog()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_eventLogPath)!);
            if (!File.Exists(_eventLogPath)) return;
            foreach (var line in File.ReadLines(_eventLogPath).Reverse()) EventLogList.Items.Add(line);
        }
        catch (Exception ex)
        {
            ShowAlert($"기존 이벤트 로그를 불러오지 못했습니다.\n{ex.Message}", "로그파일 읽기 실패");
        }
    }

    private void RefreshAssignments()
    {
        foreach (var assignment in _data.Assignments)
        {
            var target = _data.Targets.FirstOrDefault(x => x.Id == assignment.TargetId);
            var fcs = _data.FcsAssets.FirstOrDefault(x => x.Id == assignment.FcsId);
            assignment.Refresh(target, fcs);
        }
        RefreshAssignmentEmptyState();
    }

    private void RefreshAssignmentEmptyState() => AssignmentEmptyText.Visibility = _data.Assignments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void AddLog(string text)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}";
        EventLogList.Items.Insert(0, line);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_eventLogPath)!);
            File.AppendAllText(_eventLogPath, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            SharedMemoryStatus.Text = $"로그파일 기록 실패 · {ex.Message}";
            SharedMemoryStatus.Foreground = Brushes.Orange;
        }
    }
}

public sealed class DashboardData
{
    public ObservableCollection<RdrAsset> RdrAssets { get; } = new();
    public ObservableCollection<FcsAsset> FcsAssets { get; } = new();
    public ObservableCollection<TargetRecord> Targets { get; } = new();
    public ObservableCollection<AssignmentView> Assignments { get; } = new();
}

public sealed class AssignmentView : INotifyPropertyChanged
{
    private uint _fcsId;
    private string _azimuthText = "—", _elevationText = "—", _distanceText = "—", _lidarXText = "—", _lidarYText = "—", _lidarZText = "—", _rdrIdText = "—", _targetStatusText = "WAITING", _stateText = "명령 파이프 대기";
    private bool _launchRequested;
    private AssignmentState? _commandState;
    private uint _fireState;
    private bool _connectionPending, _connectionFailed;
    public uint TargetId { get; }
    public string TargetName => $"TGT-{TargetId}";
    public uint FcsId { get => _fcsId; set { if (_fcsId == value) return; _fcsId = value; OnPropertyChanged(); OnPropertyChanged(nameof(FcsName)); } }
    public string FcsName => $"FCS-{FcsId:000}";
    public string AzimuthText { get => _azimuthText; private set => Set(ref _azimuthText, value); }
    public string ElevationText { get => _elevationText; private set => Set(ref _elevationText, value); }
    public string DistanceText { get => _distanceText; private set => Set(ref _distanceText, value); }
    public string LidarXText { get => _lidarXText; private set => Set(ref _lidarXText, value); }
    public string LidarYText { get => _lidarYText; private set => Set(ref _lidarYText, value); }
    public string LidarZText { get => _lidarZText; private set => Set(ref _lidarZText, value); }
    public string RdrIdText { get => _rdrIdText; private set => Set(ref _rdrIdText, value); }
    public string TargetStatusText { get => _targetStatusText; private set => Set(ref _targetStatusText, value); }
    public string StateText
    {
        get => _stateText;
        private set
        {
            if (_stateText == value) return;
            _stateText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanFire));
        }
    }
    public bool CanRetry => _connectionFailed || _commandState == AssignmentState.Failed;
    public bool IsActiveCommandState => _connectionPending || _commandState is AssignmentState.Adjusting or AssignmentState.Assigned || _fireState != 0;
    public bool CanFire => StateText == "할당 완료";
    public bool LaunchRequested { get => _launchRequested; set { if (_launchRequested == value) return; _launchRequested = value; OnPropertyChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    public AssignmentView(uint targetId, uint fcsId) { TargetId = targetId; _fcsId = fcsId; }

    public void SetConnecting()
    {
        _connectionPending = true;
        _connectionFailed = false;
        _commandState = null;
        StateText = "연결 시도 중";
    }

    public void SetConnectionFailed()
    {
        _connectionPending = false;
        _connectionFailed = true;
        _commandState = null;
        StateText = "연결 실패 · 재시도 가능";
    }

    internal void SetCommandStatus(AssignmentState state, uint fireState)
    {
        _connectionPending = false;
        _connectionFailed = false;
        _commandState = state;
        _fireState = fireState;
        RefreshStateText(null, null);
    }

    public void SetCommandUnavailable()
    {
        if (_connectionPending) return;
        _connectionFailed = false;
        _commandState = null;
        _fireState = 0;
        StateText = "명령 파이프 대기";
    }

    public void Refresh(TargetRecord? target, FcsAsset? fcs)
    {
        if (target is null)
        {
            TargetStatusText = "NO DATA";
            RefreshStateText(null, fcs);
            return;
        }
        AzimuthText = $"{target.AzimuthDeg:0}°";
        ElevationText = $"{target.ElevationDeg:0}°";
        DistanceText = $"{target.DistanceM:0.00}";
        LidarXText = $"{target.LidarX:0.00}";
        LidarYText = $"{target.LidarY:0.00}";
        LidarZText = $"{target.LidarZ:0.00}";
        RdrIdText = target.RdrId.ToString();
        TargetStatusText = target.Status switch { 0 => "ALIVE", 1 => "HIT", 2 => "MISSING", _ => "UNKNOWN" };
        RefreshStateText(target, fcs);
    }

    private void RefreshStateText(TargetRecord? target, FcsAsset? fcs)
    {
        if (_connectionPending) { StateText = "연결 시도 중"; return; }
        if (_connectionFailed) { StateText = "연결 실패 · 재시도 가능"; return; }
        if (_fireState != 0) { StateText = "발사 완료"; return; }
        if (target?.Status == 1) { StateText = "발사 완료"; return; }
        if (fcs is null) { StateText = "타격자산 데이터 대기"; return; }
        if (!fcs.IsEnabled) { StateText = "타격자산 사용 불가"; return; }
        if (!fcs.IsOperating) { StateText = "타격자산 정지"; return; }
        if (!fcs.IsConnected) { StateText = "타격자산 연결 대기"; return; }
        if (_commandState is null) { StateText = "명령 파이프 대기"; return; }
        StateText = _commandState.Value switch
        {
            AssignmentState.Idle => "명령 대기",
            AssignmentState.Adjusting => "타격자산 조준 중",
            AssignmentState.Assigned => "할당 완료",
            AssignmentState.Failed => "할당 실패",
            _ => "상태 확인 불가"
        };
    }

    private void Set(ref string field, string value, [CallerMemberName] string? name = null)
    { if (field == value) return; field = value; OnPropertyChanged(name); }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RdrAsset : INotifyPropertyChanged
{
    public uint Id { get; }
    private bool _isEnabled, _isOperating, _isConnected;
    private int _azimuthDeg, _elevationDeg;
    public bool IsEnabled { get => _isEnabled; private set => Set(ref _isEnabled, value); }
    public bool IsOperating { get => _isOperating; private set => Set(ref _isOperating, value); }
    public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }
    public int AzimuthDeg { get => _azimuthDeg; private set => Set(ref _azimuthDeg, value); }
    public int ElevationDeg { get => _elevationDeg; private set => Set(ref _elevationDeg, value); }
    public string DisplayName => $"RDR-{Id:000}";
    public event PropertyChangedEventHandler? PropertyChanged;
    public RdrAsset(uint id, bool isEnabled, bool isOperating, bool isConnected, int azimuthDeg, int elevationDeg)
    { Id = id; UpdateFrom(isEnabled, isOperating, isConnected, azimuthDeg, elevationDeg); }
    public void UpdateFrom(RdrAsset other) => UpdateFrom(other.IsEnabled, other.IsOperating, other.IsConnected, other.AzimuthDeg, other.ElevationDeg);
    private void UpdateFrom(bool enabled, bool operating, bool connected, int az, int el)
    { IsEnabled = enabled; IsOperating = operating; IsConnected = connected; AzimuthDeg = az; ElevationDeg = el; }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
}

public sealed class FcsAsset : INotifyPropertyChanged
{
    public uint Id { get; }
    private bool _isEnabled, _isOperating, _isConnected, _laserOn;
    private int _azimuthDeg, _elevationDeg;
    public bool IsEnabled { get => _isEnabled; private set => Set(ref _isEnabled, value); }
    public bool IsOperating { get => _isOperating; private set => Set(ref _isOperating, value); }
    public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }
    public bool LaserOn { get => _laserOn; private set => Set(ref _laserOn, value); }
    public int AzimuthDeg { get => _azimuthDeg; private set => Set(ref _azimuthDeg, value); }
    public int ElevationDeg { get => _elevationDeg; private set => Set(ref _elevationDeg, value); }
    public string DisplayName => $"FCS-{Id:000}";
    public event PropertyChangedEventHandler? PropertyChanged;
    public FcsAsset(uint id, bool isEnabled, bool isOperating, bool isConnected, bool laserOn, int azimuthDeg, int elevationDeg)
    { Id = id; UpdateFrom(isEnabled, isOperating, isConnected, laserOn, azimuthDeg, elevationDeg); }
    public void UpdateFrom(FcsAsset other) => UpdateFrom(other.IsEnabled, other.IsOperating, other.IsConnected, other.LaserOn, other.AzimuthDeg, other.ElevationDeg);
    private void UpdateFrom(bool enabled, bool operating, bool connected, bool laser, int az, int el)
    { IsEnabled = enabled; IsOperating = operating; IsConnected = connected; LaserOn = laser; AzimuthDeg = az; ElevationDeg = el; }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
}

public sealed class TargetRecord : INotifyPropertyChanged
{
    private uint _selectedFcsId;
    public uint Id { get; }
    private int _azimuthDeg, _elevationDeg;
    private float _distanceM, _lidarX, _lidarY, _lidarZ;
    private uint _rdrId;
    private byte _status, _valid;
    private bool _isAssigned, _isAssignmentPending;
    public int AzimuthDeg { get => _azimuthDeg; private set => Set(ref _azimuthDeg, value); }
    public int ElevationDeg { get => _elevationDeg; private set => Set(ref _elevationDeg, value); }
    public float DistanceM { get => _distanceM; private set => Set(ref _distanceM, value); }
    public float LidarX { get => _lidarX; private set => Set(ref _lidarX, value); }
    public float LidarY { get => _lidarY; private set => Set(ref _lidarY, value); }
    public float LidarZ { get => _lidarZ; private set => Set(ref _lidarZ, value); }
    public uint RdrId { get => _rdrId; private set => Set(ref _rdrId, value); }
    public byte Status { get => _status; private set => Set(ref _status, value); }
    public byte Valid { get => _valid; private set => Set(ref _valid, value); }
    public bool IsAssigned { get => _isAssigned; set => Set(ref _isAssigned, value); }
    public bool IsAssignmentPending { get => _isAssignmentPending; set => Set(ref _isAssignmentPending, value); }
    public uint SelectedFcsId { get => _selectedFcsId; set { if (_selectedFcsId == value) return; _selectedFcsId = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedFcsId))); } }
    public string DisplayName => $"TGT-{Id}";
    public string AzimuthText => $"{AzimuthDeg:0}°";
    public string ElevationText => $"{ElevationDeg:0}°";
    public string DistanceText => $"{DistanceM:0.00}";
    public string LidarXText => LidarX.ToString("0.00");
    public string LidarYText => LidarY.ToString("0.00");
    public string LidarZText => LidarZ.ToString("0.00");
    public string StatusText => Status switch { 0 => "ALIVE", 1 => "HIT", 2 => "MISSING", _ => "UNKNOWN" };
    public string ValidText => Valid == 1 ? "YES" : "NO";
    public event PropertyChangedEventHandler? PropertyChanged;

    public TargetRecord(uint id, int azimuthDeg, int elevationDeg, float distanceM, float lidarX, float lidarY, float lidarZ, uint rdrId, byte status, bool valid, uint selectedFcsId, bool isAssigned)
    {
        Id = id; _selectedFcsId = selectedFcsId; _isAssigned = isAssigned;
        UpdateFrom(azimuthDeg, elevationDeg, distanceM, lidarX, lidarY, lidarZ, rdrId, status, valid ? (byte)1 : (byte)0);
    }
    public void UpdateFrom(TargetRecord other) => UpdateFrom(other.AzimuthDeg, other.ElevationDeg, other.DistanceM, other.LidarX, other.LidarY, other.LidarZ, other.RdrId, other.Status, other.Valid);
    private void UpdateFrom(int az, int el, float distance, float x, float y, float z, uint rdrId, byte status, byte valid)
    { AzimuthDeg = az; ElevationDeg = el; DistanceM = distance; LidarX = x; LidarY = y; LidarZ = z; RdrId = rdrId; Status = status; Valid = valid; }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        var computed = name switch
        {
            nameof(AzimuthDeg) => nameof(AzimuthText), nameof(ElevationDeg) => nameof(ElevationText),
            nameof(DistanceM) => nameof(DistanceText), nameof(LidarX) => nameof(LidarXText),
            nameof(LidarY) => nameof(LidarYText), nameof(LidarZ) => nameof(LidarZText),
            nameof(Status) => nameof(StatusText), nameof(Valid) => nameof(ValidText), _ => null
        };
        if (computed is not null) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(computed));
    }
}

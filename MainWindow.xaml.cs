using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AadcCommandCenter;

public partial class MainWindow : Window
{
    private readonly DashboardData _data = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _commandStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _rdrElevationSendTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly PipeCommandClient _pipeClient = new();
    private readonly Dictionary<uint, uint> _targetFcsSelections = new();
    private readonly Dictionary<uint, int> _targetListOrder = new();
    private readonly HashSet<uint> _assignedTargets = new();
    private readonly Dictionary<uint, AssignmentView> _assignmentsByTarget = new();
    private readonly HashSet<uint> _pendingFcsAssignments = new();
    private SharedMemoryFrameReader? _frameReader;
    private bool _isSharedMemoryLive;
    private bool _isCommandPipeLive;
    private bool _isRefreshingCommandStatus;
    private bool _supportsBatchAssignments;
    private bool? _previousConnectionState;
    private AssignmentState? _previousAssignmentState;
    private string? _previousAssignmentStatusText;
    private uint? _selectedTargetId;
    private double _workspaceZoom = 1.0;
    private bool _workspaceLayoutRefreshQueued;
    private bool _syncingPositionInputs;
    private bool _rdrOperatingCommandPending;
    private bool? _requestedRdrOperating;
    private bool _updatingRdrElevationSlider;
    private bool _updatingRdrElevationInput;
    private bool _rdrElevationInputDirty;
    private int? _requestedRdrElevationTenths;
    private readonly string _eventLogPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AadcCommandCenter", "event-log.txt");

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _data;
        RdrSelector.ItemsSource = _data.RdrAssets;
        FcsSelector.ItemsSource = _data.FcsAssets;
        TargetGrid.ItemsSource = _data.Targets;
        RefreshAssignmentEmptyState();
        LoadPersistentEventLog();
        AddLog($"프로그램 시작 · 로그파일 {_eventLogPath}");
        _frameReader = new SharedMemoryFrameReader();
        _frameReader.StateChanged += state => Dispatcher.BeginInvoke(() => ApplyConnectionState(state));
        _frameReader.FrameReceived += frame => Dispatcher.BeginInvoke(() => ApplyFrame(frame));
        Loaded += async (_, _) => { UpdateClock(); DrawRadar(); _clock.Start(); _frameReader.Start(); _commandStatusTimer.Start(); QueueWorkspaceLayoutRefresh(); await RefreshCommandStatusAsync(); await RefreshCameraSettingsAsync(); await RefreshRdrStartupAsync(); };
        Closed += (_, _) => { _clock.Stop(); _commandStatusTimer.Stop(); _rdrElevationSendTimer.Stop(); _frameReader.Stop(); _frameReader.Dispose(); _pipeClient.Dispose(); };
        _clock.Tick += (_, _) => UpdateClock();
        _commandStatusTimer.Tick += async (_, _) => { await RefreshCommandStatusAsync(); await RefreshCameraSettingsAsync(); await RefreshRdrEmissionAsync(); await RefreshRdrStartupAsync(); };
        _rdrElevationSendTimer.Tick += async (_, _) =>
        {
            _rdrElevationSendTimer.Stop();
            await SendRdrElevationCommandAsync();
        };
    }

    private void ApplyConnectionState(string state)
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
        if (!isLive)
        {
            _data.Targets.Clear();
            _selectedTargetId = null;
            TargetCountText.Text = $"0 / {SharedMemoryProtocol.TargetCapacity}";
            DrawRadar();
        }
        UpdateRdrElevationControl(RdrSelector.SelectedItem as RdrAsset);
        UpdateRdrOperatingControl(RdrSelector.SelectedItem as RdrAsset);
        SharedMemoryStatus.Text = state;
        SharedMemoryStatus.Foreground = isLive
            ? new SolidColorBrush(Color.FromRgb(0, 230, 138))
            : new SolidColorBrush(Color.FromRgb(255, 176, 0));
        RefreshRdrInfo(RdrSelector.SelectedItem as RdrAsset);
        RefreshFcsInfo(FcsSelector.SelectedItem as FcsAsset);
        RefreshAssignments();
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
            .Select(x => new RdrAsset(x.rdrId, x.isEnabled != 0, x.isOperating != 0, x.isConnected != 0, x.azimuthDeg, x.elevationDeg, x.positionX, x.positionY, x.positionZ, x.fieldOfViewDeg)).ToArray();
        var fcs = payload.fcsAssets.Take((int)payload.fcsCount)
            .Select(x => new FcsAsset(x.fcsId, x.isEnabled != 0, x.isOperating != 0, x.isConnected != 0, x.laserOn != 0, x.azimuthDeg, x.elevationDeg, x.positionX, x.positionY, x.positionZ)).ToArray();
        var targets = payload.targets.Take((int)payload.targetCount).Where(x => x.valid == 1 || (x.valid == 0 && x.status is 4 or 5))
            .Select(x => new TargetRecord(x.targetId, x.azimuthDeg, x.elevationDeg, x.distanceM, x.lidarX, x.lidarY, x.lidarZ, x.rdrId, x.status, x.valid == 1,
                _targetFcsSelections.TryGetValue(x.targetId, out var selectedFcsId) ? selectedFcsId : payload.selectedFcsId,
                _assignedTargets.Contains(x.targetId))).ToArray();

        SyncById(_data.RdrAssets, rdrs, x => x.Id, (current, next) => current.UpdateFrom(next));
        SyncById(_data.FcsAssets, fcs, x => x.Id, (current, next) => current.UpdateFrom(next));
        foreach (var target in targets)
        {
            _targetListOrder.TryAdd(target.Id, _targetListOrder.Count);
            if (_assignmentsByTarget.TryGetValue(target.Id, out var assignment) && assignment.IsConfirmedHit)
                target.MarkHit();
        }
        SyncById(_data.Targets, targets, x => x.Id, (current, next) => current.UpdateFrom(next),
            target => _targetListOrder[target.Id]);
        var defaultFcsId = _data.FcsAssets.FirstOrDefault(asset => asset.Id == payload.selectedFcsId)?.Id
            ?? _data.FcsAssets.FirstOrDefault()?.Id;
        if (defaultFcsId.HasValue)
        {
            foreach (var target in _data.Targets)
                if (!_data.FcsAssets.Any(asset => asset.Id == target.SelectedFcsId))
                    target.SelectedFcsId = defaultFcsId.Value;
        }
        RdrSelector.SelectedItem = _data.RdrAssets.FirstOrDefault(x => x.Id == payload.selectedRdrId) ?? _data.RdrAssets.FirstOrDefault();
        FcsSelector.SelectedItem = _data.FcsAssets.FirstOrDefault(x => x.Id == payload.selectedFcsId) ?? _data.FcsAssets.FirstOrDefault();
        RefreshRdrInfo(RdrSelector.SelectedItem as RdrAsset);
        SyncRdrElevationFromTelemetry(RdrSelector.SelectedItem as RdrAsset);
        RefreshFcsInfo(FcsSelector.SelectedItem as FcsAsset);
        RefreshAssignments();
        TargetCountText.Text = $"{_data.Targets.Count} / {SharedMemoryProtocol.TargetCapacity}";
        DrawRadar();
        QueueWorkspaceLayoutRefresh();
    }

    private static void SyncById<T>(ObservableCollection<T> current, IReadOnlyCollection<T> incoming, Func<T, uint> getId,
        Action<T, T> update, Func<T, int>? getOrder = null)
    {
        var ids = incoming.Select(getId).ToHashSet();
        for (var i = current.Count - 1; i >= 0; i--)
            if (!ids.Contains(getId(current[i]))) current.RemoveAt(i);

        foreach (var next in incoming)
        {
            var existing = current.FirstOrDefault(item => getId(item) == getId(next));
            if (existing is not null)
            {
                update(existing, next);
                continue;
            }
            var index = getOrder is null ? current.Count : current.TakeWhile(item => getOrder(item) < getOrder(next)).Count();
            current.Insert(index, next);
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
            WorkspaceLayout.UpdateLayout();
            RecalculateStarColumns(TargetGrid);
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
        _requestedRdrElevationTenths = null;
        _rdrElevationInputDirty = false;
        _rdrElevationSendTimer.Stop();
        var rdr = RdrSelector.SelectedItem as RdrAsset;
        UpdateRdrElevationControl(rdr);
        RefreshRdrInfo(rdr);
        SyncRdrElevationFromTelemetry(rdr);
    }

    private async void RdrOperatingToggleClick(object sender, RoutedEventArgs e)
    {
        if (RdrSelector.SelectedItem is not RdrAsset rdr || !_isSharedMemoryLive || _rdrOperatingCommandPending || _requestedRdrOperating is not null)
            return;

        var requestedOperating = !rdr.IsOperating;
        _rdrOperatingCommandPending = true;
        UpdateRdrOperatingControl(rdr);
        try
        {
            var response = await _pipeClient.SendAsync(CommandType.SetRdrOperating, requestedOperating ? 1u : 0u, rdr.Id);
            if ((ResultCode)response.ResultCode != ResultCode.Success)
                throw new IOException(CommandResultText((ResultCode)response.ResultCode));

            _requestedRdrOperating = requestedOperating;
            AddLog($"RDR {rdr.DisplayName} · {(requestedOperating ? "라이다 구동" : "라이다 정지")} 요청");
        }
        catch (Exception ex)
        {
            ShowAlert($"라이다 상태 변경에 실패했습니다: {ex.Message}", "RDR 제어 실패");
        }
        finally
        {
            _rdrOperatingCommandPending = false;
            UpdateRdrOperatingControl(RdrSelector.SelectedItem as RdrAsset);
        }
    }

    private bool _rdrStartupPolling;
    private bool _rdrEmissionPolling;
    private bool? _rdrEmissionOn;
    private long _rdrEmissionAt;
    private uint? _rdrThermalState;
    private bool _rdrThermalStop;
    private long _rdrThermalAt;
    private string _lastEmissionText = "";
    private void UpdateRdrEmissionLine()
    {
        if (RdrEmissionLine is null) return;
        var rdr = RdrSelector.SelectedItem as RdrAsset;
        bool connected = _isSharedMemoryLive && rdr?.IsConnected == true;
        bool? emission = connected && Environment.TickCount64 - _rdrEmissionAt < 3200 ? _rdrEmissionOn : null;
        var text = connected ? LidarEmissionStatus.Text(rdr!.IsOperating, emission) : "RDR 연결 대기 / 방출 상태 확인 불가";
        bool thermalFresh = connected && Environment.TickCount64 - _rdrThermalAt < 2500;
        var thermal = thermalFresh ? _rdrThermalState : null;
        bool protection = thermalFresh && _rdrThermalStop;
        RdrThermalLine.Text = connected ? LidarEmissionStatus.ThermalText(thermal, protection) : "온도 상태 확인 불가 · RDR 연결 대기";
        RdrThermalLine.Foreground = protection || thermal == 9 ? Brushes.OrangeRed :
            thermal is 7 or 8 || thermal is null ? Brushes.Orange : Brushes.LightGreen;
        text += " / " + RdrThermalLine.Text;
        RdrEmissionLine.Text = connected ? LidarEmissionStatus.Text(rdr!.IsOperating, emission) : "RDR 연결 대기 / 방출 상태 확인 불가";
        RdrEmissionLine.Foreground = emission.HasValue ? Brushes.LightGreen : Brushes.Orange;
        if (_lastEmissionText != text) { AddLog($"RDR · {text}"); _lastEmissionText = text; }
    }
    private async Task RefreshRdrEmissionAsync()
    {
        if (_rdrEmissionPolling || !_isSharedMemoryLive) return;
        _rdrEmissionPolling = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var response = await _pipeClient.SendAsync(CommandType.GetRdrLidarState, cancellationToken: timeout.Token);
            _rdrEmissionOn = (ResultCode)response.ResultCode == ResultCode.Success && response.IsConnected == 1 && response.AssignmentState == 1
                ? response.FireState == 1 : null;
            _rdrEmissionAt = Environment.TickCount64;
            var health = await _pipeClient.SendAsync(CommandType.GetRdrSensorHealth, cancellationToken: timeout.Token);
            bool validReply = (ResultCode)health.ResultCode == ResultCode.Success && health.IsConnected == 1
                && health.AssignmentState <= 1 && health.FireState <= 1 && health.AssignedTargetId <= 255;
            _rdrThermalState = validReply && health.AssignmentState == 1 && health.AssignedTargetId != 255
                ? health.AssignedTargetId : null;
            _rdrThermalStop = validReply && health.FireState == 1;
            _rdrThermalAt = Environment.TickCount64;
        }
        catch { _rdrEmissionOn = null; _rdrThermalState = null; _rdrThermalStop = false; _rdrThermalAt = 0; }
        finally { _rdrEmissionPolling = false; UpdateRdrEmissionLine(); }
    }
    private async Task RefreshRdrStartupAsync()
    {
        if (_requestedRdrOperating != true || _rdrStartupPolling) return;
        _rdrStartupPolling = true;
        try
        {
            var response = await _pipeClient.SendAsync(CommandType.GetRdrStartupStatus);
            var result = (ResultCode)response.ResultCode;
            if (result == ResultCode.StatusPending) return;
            _requestedRdrOperating = null;
            if (result == ResultCode.Success) AddLog("RDR 구동 완료 · SDK 재개 명령 성공");
            else
            {
                var message = CommandResultText(result);
                AddLog($"RDR 구동 실패 · {message}");
                ShowAlert(message, "RDR 구동 실패");
            }
        }
        catch (Exception ex)
        {
            _requestedRdrOperating = null;
            AddLog($"RDR 구동 상태 확인 실패 · {ex.Message}");
            ShowAlert("ECS 연결을 확인해 주세요. 구동 완료 여부를 확인하지 못했습니다.", "RDR 상태 확인 실패");
        }
        finally { _rdrStartupPolling = false; UpdateRdrOperatingControl(RdrSelector.SelectedItem as RdrAsset); }
    }

    private void UpdateRdrOperatingControl(RdrAsset? rdr)
    {
        if (RdrOperatingToggleButton is null) return;
        if (rdr is not null && _requestedRdrOperating == false && !rdr.IsOperating)
            _requestedRdrOperating = null;
        var sharedMemoryAvailable = _isSharedMemoryLive;
        var rdrOperating = sharedMemoryAvailable && rdr?.IsOperating == true;
        RdrOperatingToggleButton.Content = _requestedRdrOperating == true ? "라이다 구동 확인 중" : rdrOperating ? "라이다 정지" : "라이다 구동";
        RdrOperatingToggleButton.Background = sharedMemoryAvailable ? new SolidColorBrush(Color.FromRgb(13, 91, 61)) : new SolidColorBrush(Color.FromRgb(169, 21, 40));
        RdrOperatingToggleButton.Foreground = Brushes.White;
        RdrOperatingToggleButton.BorderBrush = sharedMemoryAvailable ? new SolidColorBrush(Color.FromRgb(0, 230, 138)) : new SolidColorBrush(Color.FromRgb(255, 82, 99));
        RdrOperatingToggleButton.IsEnabled = _isSharedMemoryLive && rdr is not null && rdr.IsConnected &&
            !_rdrOperatingCommandPending && _requestedRdrOperating is null;
    }

    private void RadarCanvasSizeChanged(object sender, SizeChangedEventArgs e) => DrawRadar();

    private void RdrElevationSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingRdrElevationSlider || RdrElevationSliderValue is null) return;
        var degrees = Math.Clamp((float)Math.Round(e.NewValue, 1), 0f, 90f);
        RdrElevationSliderValue.Text = $"{degrees:0.0}°";
        if (!_isSharedMemoryLive || RdrSelector.SelectedItem is not RdrAsset) return;
        _rdrElevationInputDirty = false;
        SetRdrElevationInputText(degrees);
        QueueRdrElevationCommand((int)Math.Round(degrees * 10));
    }

    private void RdrElevationInputPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var separator = System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        e.Handled = !e.Text.All(c => char.IsDigit(c) || c.ToString() == separator || c == '.' || c == ',');
    }

    private void RdrElevationInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updatingRdrElevationInput)
            _rdrElevationInputDirty = true;
    }

    private void RdrElevationInputApplyClick(object sender, RoutedEventArgs e)
    {
        AddLog("라이다 고각 입력 적용 버튼 클릭");
        if (!float.TryParse(RdrElevationInput.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var degrees) || !float.IsFinite(degrees) || degrees is < 0 or > 90)
        {
            AddLog($"라이다 고각 입력 거부 · 입력값 '{RdrElevationInput.Text}' · 허용 범위 0~90°");
            ShowAlert("고각은 0.0부터 90.0 사이의 값으로 입력하세요.", "고각 입력 확인");
            RdrElevationInput.Text = RdrElevationSlider.Value.ToString("0.0");
            return;
        }

        degrees = MathF.Round(degrees, 1);
        _rdrElevationInputDirty = false;
        SetRdrElevationSlider(degrees);
        QueueRdrElevationCommand((int)Math.Round(degrees * 10));
    }

    private void QueueRdrElevationCommand(int degreesTenths)
    {
        if (!_isSharedMemoryLive || RdrSelector.SelectedItem is not RdrAsset) return;
        _requestedRdrElevationTenths = degreesTenths;
        _rdrElevationSendTimer.Stop();
        _rdrElevationSendTimer.Start();
    }

    private void SyncRdrElevationFromTelemetry(RdrAsset? rdr)
    {
        if (rdr is null) return;
        if (_requestedRdrElevationTenths == (int)Math.Round(rdr.ElevationDeg * 10))
        {
            _requestedRdrElevationTenths = null;
        }
        if (_requestedRdrElevationTenths is int requested)
        {
            SetRdrElevationSlider(requested / 10f);
            return;
        }
        SetRdrElevationSlider(Math.Clamp(rdr.ElevationDeg, 0, 90));
    }

    private void SetRdrElevationSlider(float degrees)
    {
        if (RdrElevationSlider is null) return;
        _updatingRdrElevationSlider = true;
        RdrElevationSlider.Value = Math.Clamp(degrees, 0, 90);
        var clampedDegrees = Math.Clamp(degrees, 0f, 90f);
        RdrElevationSliderValue.Text = $"{clampedDegrees:0.0}°";
        if (!_rdrElevationInputDirty) SetRdrElevationInputText(clampedDegrees);
        _updatingRdrElevationSlider = false;
    }

    private void SetRdrElevationInputText(float degrees)
    {
        _updatingRdrElevationInput = true;
        RdrElevationInput.Text = Math.Clamp(degrees, 0f, 90f).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);
        _updatingRdrElevationInput = false;
    }

    private void UpdateRdrElevationControl(RdrAsset? rdr)
    {
        if (RdrElevationSlider is null) return;
        var isEnabled = _isSharedMemoryLive && rdr is not null && rdr.IsConnected && rdr.IsOperating;
        RdrElevationSlider.IsEnabled = RdrElevationInput.IsEnabled = RdrElevationApplyButton.IsEnabled = isEnabled;
    }

    private async Task SendRdrElevationCommandAsync()
    {
        if (_requestedRdrElevationTenths is not int degreesTenths || RdrSelector.SelectedItem is not RdrAsset rdr || !_isSharedMemoryLive)
            return;
        try
        {
            var response = await _pipeClient.SendAsync(CommandType.SetRdrElevation, (uint)degreesTenths, rdr.Id);
            if ((ResultCode)response.ResultCode != ResultCode.Success)
                throw new IOException(CommandResultText((ResultCode)response.ResultCode));

            AddLog($"라이다 고각 설정 요청 · {rdr.DisplayName} {degreesTenths / 10f:0.0}°");
            if ((int)Math.Round(RdrElevationSlider.Value * 10) != degreesTenths)
                _rdrElevationSendTimer.Start();
        }
        catch (Exception ex)
        {
            if ((int)Math.Round(RdrElevationSlider.Value * 10) == degreesTenths)
            {
                _requestedRdrElevationTenths = null;
                SyncRdrElevationFromTelemetry(rdr);
            }
            else
                _rdrElevationSendTimer.Start();
            AddLog($"라이다 고각 설정 실패 · {ex.Message}");
        }
    }

    private static bool TryCoordinate(string value, out float coordinate) =>
        float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out coordinate) && float.IsFinite(coordinate);

    private void RefreshRdrInfo(RdrAsset? rdr)
    {
        if (RdrIdText is null) return;
        if (!_isSharedMemoryLive || rdr?.IsConnected != true) { _rdrEmissionOn = null; _rdrThermalState = null; _rdrThermalStop = false; _rdrThermalAt = 0; }
        UpdateRdrEmissionLine();
        if (!_isSharedMemoryLive || rdr is null)
        {
            UpdateRdrElevationControl(rdr);
            UpdateRdrOperatingControl(rdr);
            RdrIdText.Text = rdr?.DisplayName ?? "NULL";
            RdrEnabledText.Text = RdrOperatingText.Text = RdrConnectedText.Text = RdrAzText.Text = RdrElText.Text = "NULL";
            SetRdrValueForeground(Brushes.Orange);
            RdrStatusLine.Text = "공유 메모리 대기 · NULL";
            RdrStatusLine.Foreground = Brushes.Orange;
            return;
        }
        RdrIdText.Text = rdr.DisplayName;
        RdrEnabledText.Text = YesNo(rdr.IsEnabled); RdrOperatingText.Text = YesNo(rdr.IsOperating); RdrConnectedText.Text = YesNo(rdr.IsConnected);
        RdrAzText.Text = $"{rdr.AzimuthDeg:0.0}°"; RdrElText.Text = $"{rdr.ElevationDeg:0.0}°";
        UpdateRdrElevationControl(rdr);
        SetRdrValueForeground(new SolidColorBrush(Color.FromRgb(0, 230, 138)));
        if (!rdr.IsEnabled) RdrEnabledText.Foreground = Brushes.Orange;
        RdrOperatingText.Foreground = rdr.IsOperating
            ? new SolidColorBrush(Color.FromRgb(0, 230, 138))
            : new SolidColorBrush(Color.FromRgb(255, 176, 0));
        UpdateRdrOperatingControl(rdr);
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
        FcsAzText.Text = $"{fcs.AzimuthDeg:0.0}°"; FcsElText.Text = $"{fcs.ElevationDeg:0.0}°";
        var normalColor = new SolidColorBrush(Color.FromRgb(0, 230, 138));
        SetFcsValueForeground(normalColor);
        FcsLaserText.Foreground = fcs.LaserOn
            ? new SolidColorBrush(Color.FromRgb(255, 65, 82))
            : normalColor;
    }

    private void SetFcsValueForeground(Brush brush)
    {
        FcsIdText.Foreground = FcsEnabledText.Foreground = FcsOperatingText.Foreground = FcsConnectedText.Foreground = FcsLaserText.Foreground = FcsAzText.Foreground = FcsElText.Foreground = brush;
    }

    private static string YesNo(bool value) => value ? "YES" : "NO";

    private void DrawRadar()
    {
        if (RadarCanvas is null) return;
        RadarCanvas.Children.Clear();
        var rdr = RdrSelector.SelectedItem as RdrAsset;
        if (rdr is null) return;

        var fcs = FcsSelector.SelectedItem as FcsAsset;
        var targets = _data.Targets.Where(x => (x.Valid != 0 || x.IsObservationWaiting) && x.RdrId == rdr.Id).ToArray();
        var rdrX = (double)rdr.PositionX;
        var rdrY = (double)rdr.PositionY;
        var fcsX = fcs == null ? rdrX : (double)fcs.PositionX;
        var fcsY = fcs == null ? rdrY : (double)fcs.PositionY;
        double minX = new[] { rdrX, fcsX }.Concat(targets.Select(x => (double)x.LidarX)).Min();
        double maxX = new[] { rdrX, fcsX }.Concat(targets.Select(x => (double)x.LidarX)).Max();
        double minY = new[] { rdrY, fcsY }.Concat(targets.Select(x => (double)x.LidarY)).Min();
        double maxY = new[] { rdrY, fcsY }.Concat(targets.Select(x => (double)x.LidarY)).Max();
        var ranges = _cameraSettings?[0];
        var fov = _cameraSettings is { Length: > 1 } ? _cameraSettings[1] : null;
        if (ranges != null)
        {
            minX = Math.Min(minX, rdrX - ranges[1]); maxX = Math.Max(maxX, rdrX + ranges[1]);
            minY = Math.Min(minY, rdrY - ranges[1]); maxY = Math.Max(maxY, rdrY + ranges[1]);
            if (fcs != null)
            {
                minX = Math.Min(minX, fcsX - ranges[2]); maxX = Math.Max(maxX, fcsX + ranges[2]);
                minY = Math.Min(minY, fcsY - ranges[2]); maxY = Math.Max(maxY, fcsY + ranges[2]);
            }
        }
        var span = Math.Max(Math.Max(maxX - minX, maxY - minY), 1.0);
        var padding = Math.Max(span * 0.10, 0.45);
        minX -= padding; maxX += padding; minY -= padding; maxY += padding;
        var canvasWidth = RadarCanvas.ActualWidth > 20 ? RadarCanvas.ActualWidth : double.IsFinite(RadarCanvas.Width) ? RadarCanvas.Width : 260;
        var canvasHeight = RadarCanvas.ActualHeight > 20 ? RadarCanvas.ActualHeight : double.IsFinite(RadarCanvas.Height) ? RadarCanvas.Height : 280;
        const double inset = 18;
        var scale = Math.Min((canvasWidth - inset * 2) / (maxX - minX), (canvasHeight - inset * 2) / (maxY - minY));
        var originX = (canvasWidth - (maxX - minX) * scale) / 2;
        var originY = (canvasHeight - (maxY - minY) * scale) / 2;
        Point Map(double x, double y) => new(originX + (x - minX) * scale, canvasHeight - originY - (y - minY) * scale);

        var gridBrush = new SolidColorBrush(Color.FromRgb(22, 55, 46));
        var rawStep = span / 8;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        var gridStep = (rawStep / magnitude <= 2 ? 2 : rawStep / magnitude <= 5 ? 5 : 10) * magnitude;
        for (var x = Math.Ceiling(minX / gridStep) * gridStep; x <= maxX; x += gridStep)
        {
            var a = Map(x, minY); var b = Map(x, maxY);
            RadarCanvas.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = gridBrush, StrokeThickness = 1 });
        }
        for (var y = Math.Ceiling(minY / gridStep) * gridStep; y <= maxY; y += gridStep)
        {
            var a = Map(minX, y); var b = Map(maxX, y);
            RadarCanvas.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = gridBrush, StrokeThickness = 1 });
        }

        // Read-only overlay: distances from ECS; optical FOV from the XT-M60 specification.
        var rdrPoint = Map(rdrX, rdrY);
        var rdrDisplayAzimuth = Math.Clamp(rdr.AzimuthDeg, 0, 270) - 135d;
        var viewRadius = ranges != null ? ranges[1] * scale : Math.Min(canvasWidth, canvasHeight) * .3;
        if (ranges != null)
        {
            var overlay = new CameraMapOverlay
            {
                Width = canvasWidth, Height = canvasHeight, RdrCenter = rdrPoint,
                FcsCenter = fcs == null ? null : Map(fcsX, fcsY),
                MinimumRadius = ranges[0] * scale, MaximumRadius = viewRadius,
                CaptureRadius = ranges[2] * scale, Heading = rdrDisplayAzimuth,
                IsHitTestVisible = false, HorizontalFieldOfView = fov?[0] ?? 120
            };
            RadarCanvas.Children.Add(overlay);
        }

        var fcsPoint = fcs is null ? (Point?)null : Map(fcsX, fcsY);
        var assignedTargetId = fcs is null ? (uint?)null : _data.Assignments.FirstOrDefault(
            row => row.FcsId == fcs.Id && (!row.IsBatch || row.IsFiring))?.TargetId;
        var laserTarget = targets.FirstOrDefault(x => x.Id == assignedTargetId);
        if (fcs?.LaserOn == true && fcsPoint is Point fp && laserTarget is not null)
        {
            var tp = Map(laserTarget.LidarX, laserTarget.LidarY);
            RadarCanvas.Children.Add(new Line { X1 = fp.X, Y1 = fp.Y, X2 = tp.X, Y2 = tp.Y, Stroke = new SolidColorBrush(Color.FromRgb(255, 65, 82)), StrokeThickness = 1.5 });
        }

        AddMapLabel("Y ↑   ·   거리 m", 5, canvasHeight - 17, Color.FromRgb(82, 121, 109));
        if (fcs is not null) AddAssetMarker(fcsPoint!.Value, "FCS", Color.FromRgb(30, 125, 157), fcsX, fcsY);
        AddAssetMarker(rdrPoint, "RDR", Color.FromRgb(0, 190, 225), rdrX, rdrY);

        if (ranges != null)
        {
            AddMapLabel($"{ranges[1]:G4} m", rdrPoint.X + 6, rdrPoint.Y - viewRadius - 13, Color.FromRgb(90, 211, 214), 9);
            var textAngle = rdrDisplayAzimuth * Math.PI / 180;
            AddMapLabel($"{fov?[0] ?? 120:G4}°", rdrPoint.X + viewRadius * .68 * Math.Sin(textAngle) - 12,
                rdrPoint.Y - viewRadius * .68 * Math.Cos(textAngle), Color.FromRgb(0, 230, 138), 10);
        }

        if (fcs is not null && fcsPoint is Point orientationPoint)
        {
            var fcsDisplayAzimuth = fcs.AzimuthDeg - 135d;
            var fcsAngle = fcsDisplayAzimuth * Math.PI / 180d;
            const double directionLength = 24;
            RadarCanvas.Children.Add(new Line
            {
                X1 = orientationPoint.X,
                Y1 = orientationPoint.Y,
                X2 = orientationPoint.X + directionLength * Math.Sin(fcsAngle),
                Y2 = orientationPoint.Y - directionLength * Math.Cos(fcsAngle),
                Stroke = new SolidColorBrush(Color.FromRgb(0, 162, 255)),
                StrokeThickness = 2.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false,
                ToolTip = $"FCS 방향 {fcs.AzimuthDeg}°"
            });
        }

        foreach (var target in targets)
        {
            var point = Map(target.LidarX, target.LidarY);
            var isSelected = _selectedTargetId == target.Id;
            var color = target.IsObservationWaiting ? Color.FromRgb(150, 160, 165) : isSelected ? Color.FromRgb(0, 162, 255) : target.Status == 1 ? Color.FromRgb(110, 120, 120) : target.Status == 2 ? Color.FromRgb(255, 176, 0) : Color.FromRgb(255, 65, 82);
            var marker = new Polygon
            {
                Fill = new SolidColorBrush(color),
                Stroke = isSelected ? Brushes.LightCyan : Brushes.White,
                StrokeThickness = isSelected ? 1.5 : 0.7,
                Cursor = Cursors.Hand,
                Tag = target.Id,
                ToolTip = $"{target.DisplayName} · {target.StatusText}",
                Opacity = target.IsObservationWaiting ? 0.45 : 1
            };
            marker.Points.Add(new Point(point.X, point.Y - 6)); marker.Points.Add(new Point(point.X - 5, point.Y + 4)); marker.Points.Add(new Point(point.X + 5, point.Y + 4));
            marker.MouseLeftButtonDown += RadarTargetMouseLeftButtonDown;
            RadarCanvas.Children.Add(marker);
            AddMapLabel(target.DisplayName, point.X + 6, point.Y - 5, color, 8);
        }

        void AddAssetMarker(Point p, string label, Color color, double x, double y)
        {
            var marker = new Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(color), Stroke = Brushes.White, StrokeThickness = 1, ToolTip = $"{label}  X:{x:0.00}m  Y:{y:0.00}m" };
            Canvas.SetLeft(marker, p.X - 8); Canvas.SetTop(marker, p.Y - 8); RadarCanvas.Children.Add(marker);
            AddMapLabel(label, p.X + 9, p.Y + 3, color, 9);
        }

        void AddMapLabel(string text, double x, double y, Color color, double fontSize = 9)
        {
            var label = new TextBlock { Text = text, Foreground = new SolidColorBrush(color), FontSize = fontSize, IsHitTestVisible = false };
            Canvas.SetLeft(label, Math.Clamp(x, 2, Math.Max(2, canvasWidth - 80))); Canvas.SetTop(label, Math.Clamp(y, 2, Math.Max(2, canvasHeight - 16))); RadarCanvas.Children.Add(label);
        }
    }

    private void RadarTargetMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: uint targetId }) return;
        var target = _data.Targets.FirstOrDefault(x => x.Id == targetId);
        if (target is null) return;
        _selectedTargetId = targetId;
        TargetGrid.SelectedItem = target;
        TargetGrid.ScrollIntoView(target);
        DrawRadar();
        AddLog($"레이더 객체 선택 · {target.DisplayName}");
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
        if (!EnsureSharedMemoryLiveForCommand("객체 할당")) return;
        if ((sender as FrameworkElement)?.DataContext is not TargetRecord target) { AddLog("객체 할당 거부 · 객체 행을 확인할 수 없음"); return; }
        if (target.Status == 4 || _data.Assignments.Any(row => row.IsScanPaused))
        {
            ShowAlert("객체를 할당하려면 먼저 조사를 재개하세요.", "조사 재개 필요");
            return;
        }
        AddLog($"객체 할당 버튼 클릭 · {target.DisplayName}");
        var fcs = _data.FcsAssets.FirstOrDefault(x => x.Id == target.SelectedFcsId);
        if (fcs is null)
        {
            AddLog($"객체 할당 거부 · {target.DisplayName} FCS 미선택");
            ShowAlert("FCS을 먼저 선택하세요.", "객체 할당");
            return;
        }
        if (!target.CanAssign)
        {
            AddLog($"객체 할당 거부 · {target.DisplayName} 상태 {target.StatusText}");
            ShowAlert("생존 상태인 객체만 할당할 수 있습니다.", "객체 할당");
            return;
        }
        if (_pendingFcsAssignments.Contains(fcs.Id))
        {
            AddLog($"객체 할당 중복 클릭 무시 · {fcs.DisplayName} 연결 시도 중");
            return;
        }
        if (_assignmentsByTarget.TryGetValue(target.Id, out var currentAssignment) && currentAssignment.FcsId == fcs.Id && !currentAssignment.CanRetry)
        {
            AddLog($"객체 할당 거부 · {target.DisplayName}에 {fcs.DisplayName} 이미 할당됨");
            ShowAlert($"{fcs.DisplayName}은(는) 이미 {target.DisplayName}에 할당되어 있습니다.", "중복 할당");
            return;
        }

        var rdr = _data.RdrAssets.FirstOrDefault(x => x.Id == target.RdrId);
        if (rdr is null || !rdr.IsConnected || !rdr.IsOperating)
        {
            AddLog($"객체 할당 거부 · {target.DisplayName} 탐지 레이더 연결/구동 불가");
            ShowAlert("탐지 레이더의 연결 및 구동 상태를 확인하세요.", "객체 할당");
            return;
        }

        if (!_assignmentsByTarget.TryGetValue(target.Id, out var assignment))
        {
            assignment = new AssignmentView(target.Id, fcs.Id);
            _assignmentsByTarget[target.Id] = assignment;
            _data.Assignments.Add(assignment);
        }
        else assignment.FcsId = fcs.Id;
        if (!_supportsBatchAssignments && LegacySlotOccupied(assignment))
        {
            assignment.AwaitingEcsSlot = true;
            _assignedTargets.Add(target.Id);
            target.IsAssigned = true;
            RefreshAssignments();
            AddLog($"객체 할당 목록 등록 · {target.DisplayName} → {fcs.DisplayName} · 처리 대기");
            return;
        }
        assignment.SetConnecting();
        target.IsAssignmentPending = true;
        _pendingFcsAssignments.Add(fcs.Id);
        RefreshAssignments();
        AddLog($"{target.DisplayName} → {fcs.DisplayName} 명령 파이프 연결 시도 중");

        PipeResponse? response;
        try
        {
            if (!_supportsBatchAssignments)
            {
                var capability = await SendPipeCommandAsync(CommandType.GetBatchStatus, target.RdrId, fcs.Id, 0, "ECS 할당 기능 확인", showConnectionAlert: false);
                _supportsBatchAssignments = capability is { ResultCode: (uint)ResultCode.Success };
            }
            var register = _supportsBatchAssignments ? CommandType.RegisterBatchTarget : CommandType.RegisterManualTarget;
            response = await SendPipeCommandAsync(register, target.RdrId, fcs.Id, target.Id, "객체 할당", showConnectionAlert: false);
        }
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
            AddLog($"{target.DisplayName} 객체 할당 실패 · 연결 불가, 할당 정보 제거");
            return;
        }
        ApplyCommandResponse(response.Value);
        if ((ResultCode)response.Value.ResultCode != ResultCode.Success)
        {
            if ((response.Value.AssignedTargetId != target.Id ||
                 (_supportsBatchAssignments && response.Value.AssignmentState == (uint)AssignmentState.Idle)) &&
                _assignmentsByTarget.Remove(target.Id, out var rejectedRow))
            {
                _data.Assignments.Remove(rejectedRow);
                target.IsAssigned = false;
                RefreshAssignmentEmptyState();
            }
            var message = CommandResultText((ResultCode)response.Value.ResultCode);
            AddLog($"객체 할당 거부 · {target.DisplayName} · {message}");
            ShowAlert(message, "객체 할당 거부");
            return;
        }
        AddLog($"객체 할당 명령 접수 · {target.DisplayName} → {fcs.DisplayName} · {AssignmentStateText((AssignmentState)response.Value.AssignmentState)}");
    }

    private async void AssignmentStageClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AssignmentView assignment, CommandParameter: AssignmentStage stage } &&
            assignment.CanUsePrimaryAction && stage == assignment.PrimaryAction)
            await RunAssignmentActionAsync(assignment, stage);
    }

    private void EditAssignmentCoordinatesClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AssignmentView assignment }) return;
        var target = _data.Targets.FirstOrDefault(item => item.Id == assignment.TargetId);
        if (target is null) return;

        var editor = new TargetCoordinateEditorWindow(target.Id, target.LidarX, target.LidarY, target.LidarZ)
        {
            Owner = this
        };
        if (editor.ShowDialog() != true) return;

        target.SetLocalPositionOverride(editor.PositionX, editor.PositionY, editor.PositionZ);
        RefreshAssignments();
        DrawRadar();
        AddLog($"객체 좌표 임시 변경 · {target.DisplayName} · X/Y/Z {target.LidarX:0.00}/{target.LidarY:0.00}/{target.LidarZ:0.00} m (GUI 전용)");
    }

    private async void AssignmentListActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AssignmentView assignment } || !assignment.CanUseListAction) return;
        if (assignment.AwaitingEcsSlot)
        {
            RemoveAssignment(assignment);
            RefreshAssignments();
            return;
        }

        var action = new AssignmentStage("할당 해제", "", CanAdvance: true)
        {
            Command = assignment.IsBatch ? CommandType.CancelBatchTarget : CommandType.CancelAssignment
        };
        await RunAssignmentActionAsync(assignment, action);
    }

    private async void PauseAssignmentScanClick(object sender, RoutedEventArgs e) => await RunSharedScanActionAsync(1);

    private async void ResumeAssignmentScanClick(object sender, RoutedEventArgs e) => await RunSharedScanActionAsync(3);

    private async Task RunSharedScanActionAsync(int stageIndex)
    {
        var assignment = FindAssignmentForScanAction(stageIndex);
        if (assignment is not null)
            await RunAssignmentActionAsync(assignment, assignment.Stages[stageIndex]);
    }

    private async Task RunAssignmentActionAsync(AssignmentView assignment, AssignmentStage stage)
    {
        if (!stage.CanAdvance || stage.Command is not CommandType command || !assignment.StageCommandsEnabled ||
            _data.Assignments.Any(row => row.IsStageCommandPending)) return;
        if ((command is CommandType.FireBatchTarget or CommandType.StartManualFire) && !assignment.CanFireFromList) return;
        if (!EnsureSharedMemoryLiveForCommand(stage.Label)) return;

        var target = _data.Targets.FirstOrDefault(x => x.Id == assignment.TargetId);
        bool startingDeferred = assignment.AwaitingEcsSlot && command == CommandType.RegisterManualTarget;
        if (startingDeferred && (LegacySlotOccupied(assignment) || target?.CanAssign != true ||
            _pendingFcsAssignments.Contains(assignment.FcsId))) return;
        if ((command is CommandType.StartManualAim or CommandType.PauseManualScan) &&
            target?.CanAssign != true)
        {
            ShowAlert("최신 관측 정보가 필요합니다.", stage.Label);
            return;
        }

        assignment.SetStageCommandPending(true);
        RefreshAssignmentScanControls();
        try
        {
            var response = await SendPipeCommandAsync(command, target?.RdrId ?? 1, assignment.FcsId, assignment.TargetId, stage.Label);
            if (response is null) return;
            var result = (ResultCode)response.Value.ResultCode;
            if (!startingDeferred || result == ResultCode.Success)
            {
                if (startingDeferred) assignment.AwaitingEcsSlot = false;
                ApplyCommandResponse(response.Value);
            }
            if (result == ResultCode.Success) AddLog($"{stage.Label} · {assignment.TargetName} → {assignment.FcsName}");
            else ShowAlert(CommandResultText(result), stage.Label);
        }
        finally
        {
            assignment.SetStageCommandPending(false);
            RefreshAssignments();
        }
    }

    private void ShowAlert(string message, string title) =>
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    private async Task RefreshCommandStatusAsync()
    {
        if (_isRefreshingCommandStatus || _pendingFcsAssignments.Count > 0 ||
            _data.Assignments.Any(assignment => assignment.IsStageCommandPending)) return;
        _isRefreshingCommandStatus = true;
        try
        {
            var batch = await _pipeClient.SendAsync(CommandType.GetBatchStatus, rdrId: 1, fcsId: 1);
            _supportsBatchAssignments = (ResultCode)batch.ResultCode == ResultCode.Success;
            if (_supportsBatchAssignments)
            {
                SetCommandPipeOnline(batch.IsConnected != 0);
                if (batch.IsConnected == 0) return;
                var targetIds = _data.Targets.Select(target => target.Id)
                    .Concat(_data.Assignments.Where(row => row.IsBatch).Select(row => row.TargetId)).Distinct().ToArray();
                foreach (var targetId in targetIds)
                {
                    var status = await _pipeClient.SendAsync(CommandType.GetBatchStatus, targetId);
                    if (_pendingFcsAssignments.Count > 0 ||
                        _data.Assignments.Any(row => row.IsStageCommandPending)) return;
                    if (status.IsConnected == 0)
                    {
                        SetCommandPipeOnline(false);
                        return;
                    }
                    ApplyCommandResponse(status);
                }
                return;
            }
            var response = await _pipeClient.SendAsync(CommandType.GetManualStatus, rdrId: 1, fcsId: 1);
            if ((ResultCode)response.ResultCode == ResultCode.NotSupported)
                response = await _pipeClient.SendAsync(CommandType.GetStatus, rdrId: 1, fcsId: 1);
            if (_pendingFcsAssignments.Count > 0 ||
                _data.Assignments.Any(row => row.IsStageCommandPending)) return;
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
                ShowAlert("명령 파이프 서버에 연결할 수 없습니다.\nECS 프로그램이 실행 중인지 확인하세요.", "명령 전송 실패");
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
        UpdateRdrOperatingControl(RdrSelector.SelectedItem as RdrAsset);
        if (!online)
        {
            foreach (var assignment in _data.Assignments) assignment.SetCommandUnavailable();
            RefreshAssignments();
        }
    }

    private void ApplyCommandResponse(PipeResponse response)
    {
        var state = (AssignmentState)response.AssignmentState;
        bool batch = (CommandType)response.CommandType is >= CommandType.GetBatchStatus and <= CommandType.RetryBatchTarget;
        if (batch)
        {
            var result = (ResultCode)response.ResultCode;
            if (result != ResultCode.Success) return;
            foreach (var row in _data.Assignments.Where(row => row.IsBatch))
                row.BatchFlags = response.FireState;
            if (state == AssignmentState.Idle)
            {
                if (_assignmentsByTarget.TryGetValue(response.AssignedTargetId, out var row) && row.IsBatch &&
                    !row.IsConnecting)
                {
                    _assignmentsByTarget.Remove(row.TargetId);
                    _assignedTargets.Remove(row.TargetId);
                    var targetRow = _data.Targets.FirstOrDefault(target => target.Id == row.TargetId);
                    if (targetRow is not null) targetRow.IsAssigned = false;
                    _data.Assignments.Remove(row);
                }
                RefreshAssignments();
                return;
            }
        }
        if (response.AssignedTargetId == 0 && state == AssignmentState.Idle &&
            !((CommandType)response.CommandType is CommandType.GetStatus or CommandType.GetManualStatus &&
              _pendingFcsAssignments.Contains(response.FcsId)))
        {
            foreach (var current in _data.Assignments.Where(current => current.FcsId == response.FcsId && !current.AwaitingEcsSlot).ToArray())
                RemoveAssignment(current);
            RefreshAssignments();
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
            if (batch)
            {
                assignment.IsBatch = true;
                assignment.BatchFlags = response.FireState;
            }
            if (batch)
            {
                bool changed = !assignment.HasCommandState(state);
                assignment.SetCommandStatus(state, 0);
                if (changed) AddLog($"ECS · {assignment.TargetName} · {AssignmentStateText(state)}");
            }
            else if (response.CommandType == (uint)CommandType.GetManualStatus)
                assignment.SetManualStatus(state, (ResultCode)response.ResultCode, response.FireState);
            else assignment.SetCommandStatus(state, response.FireState);
            _assignedTargets.Add(response.AssignedTargetId);
            var target = _data.Targets.FirstOrDefault(x => x.Id == response.AssignedTargetId);
            if (target is not null)
            {
                target.IsAssigned = true;
                target.SelectedFcsId = response.FcsId;
                if (state == AssignmentState.ResultConfirmed) target.MarkHit();
            }
        }

        if (batch)
        {
            RefreshAssignments();
            return;
        }

        var stateText = state == AssignmentState.Failed && _assignmentsByTarget.TryGetValue(response.AssignedTargetId, out var failed)
            ? failed.StateText : AssignmentStateText(state);
        if (_previousAssignmentState != state || _previousAssignmentStatusText != stateText)
        {
            AddLog($"명령 서버 객체할당 상태 · {stateText} · {TargetName(response.AssignedTargetId)}");
            _previousAssignmentState = state;
            _previousAssignmentStatusText = stateText;
        }
        RefreshAssignments();
    }

    private static string TargetName(uint targetId) => targetId == 0 ? "없음" : $"TGT-{targetId}";

    internal static string AssignmentStateText(AssignmentState state) => state switch
    {
        AssignmentState.Idle => "대기",
        AssignmentState.Adjusting => "FCS 조준 중",
        AssignmentState.Assigned => "할당 완료",
        AssignmentState.Failed => "할당 실패",
        AssignmentState.Detected => "할당 대기",
        AssignmentState.Confirmed => "할당 완료 · 조준 대기",
        AssignmentState.ManualAiming => "FCS 조준 중",
        AssignmentState.Aimed => "조준 완료",
        AssignmentState.ScanPaused => "라이다 조사 중지 완료",
        AssignmentState.Firing => "실행 중",
        AssignmentState.FireConfirmed => "실행 완료",
        AssignmentState.ScanResumed => "조사 재개 완료 · 결과 판정 중",
        AssignmentState.ResultConfirmed => "결과 확인 · HIT",
        AssignmentState.TargetStillPresent => "결과 확인 · 객체 유지",
        _ => "알 수 없음"
    };

    private static string CommandResultText(ResultCode result) => result switch
    {
        ResultCode.Success => "명령 접수",
        ResultCode.InvalidRequest => "요청 값이 올바르지 않습니다",
        ResultCode.AssetUnavailable => "FCS을 사용할 수 없습니다",
        ResultCode.NotAssigned => "FCS에 할당된 객체가 없습니다",
        ResultCode.AlreadyAssigned => "ECS에 이미 할당된 객체가 있습니다. 진행 중인 할당 정보를 확인하세요",
        ResultCode.NotSupported => "지원하지 않는 명령입니다",
        ResultCode.DeviceError => "장치 처리 오류입니다",
        ResultCode.ResultUnconfirmed => "동일 ID의 재관측을 3초 안에 확인하지 못했습니다",
        ResultCode.PauseTimeout => "실행 완료 전 조사 중지 제한 시간(10초)을 초과했습니다",
        ResultCode.StatusPending => "장치 상태 갱신을 기다리는 중입니다",
        ResultCode.TargetPending => "기존 대상의 최신 관측을 확인할 수 없습니다. 할당을 해제한 뒤 현재 객체 목록에서 대상을 다시 선택하세요.",
        ResultCode.DeviceFault => "장치가 준비되지 않았습니다",
        ResultCode.LidarUnsupported => "RDR 펌웨어가 라이다 방출 제어를 지원하지 않습니다. ED07/DE07 지원 버전으로 업데이트해 주세요",
        ResultCode.LidarUnknown => "RDR의 실제 라이다 방출 상태가 확인되지 않았습니다",
        ResultCode.LidarRecoveryRequired => "라이다 연동이 진행 중이거나 복구가 필요합니다. 진행 중인 작업을 확인하고 취소 후 다시 시도해 주세요",
        ResultCode.LidarThermalFault => "라이다 센서가 고온 오류를 보고하여 보호 정지했습니다. 온도와 장치 상태를 확인한 뒤 복구해 주세요",
        ResultCode.LidarStopWarningUnconfirmed => "온도 경고가 보고된 상태에서 라이다 중지 명령의 완료를 확인하지 못했습니다. 작업이 보류되었습니다",
        ResultCode.LidarStopUnconfirmed => "라이다 정지 및 실행 준비 확인에 실패했습니다. RDR·FCS 상태와 ECS 로그를 확인해 주세요",
        ResultCode.LidarDisconnected => "RDR 연결이 끊겨 라이다 정지를 확인할 수 없습니다",
        ResultCode.LidarResumeUnconfirmed => "RDR 구동 준비는 완료됐지만 SDK 재개 명령이 실패했거나 응답이 없습니다. ED07/DE07 응답을 확인해 주세요",
        ResultCode.FcsOffUnconfirmed => "FCS 출력 OFF 및 시연 종료를 확인하지 못해 라이다 구동을 중단했습니다",
        ResultCode.RdrStartUnconfirmed => "RDR 구동 준비 완료를 확인하지 못했습니다. RDR 상태와 ECS 로그를 확인해 주세요",
        ResultCode.LidarBusy => "RDR이 이전 명령을 처리 중입니다. 완료 후 다시 시도해 주세요",
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
        var resumeRow = _data.Assignments.FirstOrDefault(row => row.CanOfferBatchResume && row.HasUnfiredAim)
            ?? _data.Assignments.FirstOrDefault(row => row.CanOfferBatchResume);
        foreach (var assignment in _data.Assignments)
        {
            assignment.EcsSlotAvailable = assignment.AwaitingEcsSlot && _isCommandPipeLive && !LegacySlotOccupied(assignment);
            assignment.ShowBatchResume = ReferenceEquals(assignment, resumeRow);
            var target = _data.Targets.FirstOrDefault(x => x.Id == assignment.TargetId);
            var fcs = _data.FcsAssets.FirstOrDefault(x => x.Id == assignment.FcsId);
            assignment.Refresh(_isSharedMemoryLive ? target : null, _isSharedMemoryLive ? fcs : null, _isSharedMemoryLive);
        }
        RefreshAssignmentEmptyState();
    }

    private bool LegacySlotOccupied(AssignmentView waiting) => _data.Assignments.Any(row =>
        row.FcsId == waiting.FcsId && row.TargetId != waiting.TargetId && !row.AwaitingEcsSlot);

    private void RemoveAssignment(AssignmentView assignment)
    {
        _assignmentsByTarget.Remove(assignment.TargetId);
        _assignedTargets.Remove(assignment.TargetId);
        var target = _data.Targets.FirstOrDefault(row => row.Id == assignment.TargetId);
        if (target is not null)
        {
            target.IsAssigned = false;
            target.IsAssignmentPending = false;
        }
        _data.Assignments.Remove(assignment);
    }

    private void RefreshAssignmentEmptyState()
    {
        AssignmentEmptyState.Visibility = _data.Assignments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshAssignmentScanControls();
    }

    private AssignmentView? FindAssignmentForScanAction(int stageIndex)
    {
        if (!_isSharedMemoryLive || _data.Assignments.Any(row => row.IsStageCommandPending)) return null;
        return _data.Assignments.FirstOrDefault(row => row.StageCommandsEnabled && row.Stages[stageIndex].CanAdvance);
    }

    private void RefreshAssignmentScanControls()
    {
        PauseAssignmentScanButton.IsEnabled = FindAssignmentForScanAction(1) is not null;
        ResumeAssignmentScanButton.IsEnabled = FindAssignmentForScanAction(3) is not null;
        var rdr = RdrSelector.SelectedItem as RdrAsset;
        AssignmentScanStatusText.Text = !_isSharedMemoryLive || rdr?.IsConnected != true ? "연결 대기" :
            _data.Assignments.Any(row => row.IsScanPaused) ? "조사 일시 중지됨" :
            rdr.IsOperating ? "조사 중" : "라이다 정지";
    }

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

public sealed record AssignmentStage(string Label, string Status, bool IsCurrent = false, string? ActionLabel = null, bool CanAdvance = false)
{
    internal CommandType? Command { get; init; }
    public bool IsComplete => Status.StartsWith("완료", StringComparison.Ordinal);
}

public sealed class AssignmentView : INotifyPropertyChanged
{
    private static readonly string[] StageLabels =
    [
        "1. 조준", "2. 조사 중지", "3. 실행", "4. 조사 재개", "5. 결과 확인"
    ];
    private uint _fcsId;
    private string _azimuthText = "—", _elevationText = "—", _distanceText = "—", _lidarXText = "—", _lidarYText = "—", _lidarZText = "—", _rdrIdText = "—", _targetStatusText = "WAITING", _stateText = "명령 파이프 대기";
    private bool _launchRequested;
    private AssignmentState? _commandState;
    private uint _fireState;
    private bool _connectionPending, _connectionFailed;
    public uint TargetId { get; }
    public string TargetName => $"TGT-{TargetId}";
    public uint FcsId { get => _fcsId; set { if (_fcsId == value) return; _fcsId = value; OnPropertyChanged(); OnPropertyChanged(nameof(FcsName)); OnPropertyChanged(nameof(ProgressTitle)); } }
    public string FcsName => $"FCS-{FcsId:000}";
    public bool HasAssignment => TargetId != 0;
    internal bool IsConfirmedHit => _commandState == AssignmentState.ResultConfirmed;
    public string ProgressTitle => HasAssignment ? $"{TargetName}  →  {FcsName}" : "미할당";
    public IReadOnlyList<AssignmentStage> Stages { get; private set; } = StageLabels
        .Select(label => new AssignmentStage(label, "")).ToArray();
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
    public bool IsActiveCommandState => _connectionPending || _commandState is AssignmentState.Adjusting or AssignmentState.Assigned ||
        ManualCompletedStep > 0 || _fireState != 0;
    public bool IsStageCommandPending { get; private set; }
    internal bool IsConnecting => _connectionPending;
    internal bool IsBatch { get; set; }
    internal bool AwaitingEcsSlot { get; set; }
    internal bool EcsSlotAvailable { get; set; }
    internal bool IsFiring => _commandState == AssignmentState.Firing;
    internal bool HasUnfiredAim => _commandState is AssignmentState.ManualAiming or AssignmentState.ScanPaused;
    internal bool HasCommandState(AssignmentState state) => _commandState == state;
    internal uint BatchFlags { get; set; }
    internal bool CanOfferBatchResume => IsBatch && (BatchFlags & 2) != 0 &&
        _commandState is AssignmentState.Confirmed or AssignmentState.ManualAiming or
            AssignmentState.ScanPaused or AssignmentState.FireConfirmed;
    internal bool ShowBatchResume { get; set; }
    public bool StageCommandsEnabled => HasAssignment && !_connectionPending && !IsStageCommandPending;
    internal bool IsScanPaused => IsBatch ? (BatchFlags & 1) != 0 :
        _commandState is AssignmentState.ScanPaused or AssignmentState.Firing or AssignmentState.FireConfirmed;
    public AssignmentStage PrimaryAction => _commandState switch
    {
        _ when AwaitingEcsSlot => Stages[0] with { ActionLabel = "처리 시작" },
        AssignmentState.TargetStillPresent when Stages[4].ActionLabel == "다시 시도" => Stages[4],
        AssignmentState.Confirmed when IsBatch && !IsScanPaused =>
            Stages[0] with { ActionLabel = "조사 일시 정지 필요", CanAdvance = false },
        _ when ManualCompletedStep >= 3 && _commandState != AssignmentState.Failed => Stages[2] with { ActionLabel = "실행" },
        _ => Stages[0] with { ActionLabel = "조준" }
    };
    public bool CanUsePrimaryAction => StageCommandsEnabled && PrimaryAction.CanAdvance &&
        (PrimaryAction.ActionLabel != "실행" || CanFireFromList);
    public bool CanFireFromList => StageCommandsEnabled && IsScanPaused && Stages[2].CanAdvance;
    public string? ListActionLabel => Stages[0].Status.Length == 0 ? null : "할당 해제";
    public bool CanUseListAction => StageCommandsEnabled && ListActionLabel is not null &&
        (!IsBatch || _commandState is not (AssignmentState.Firing or AssignmentState.ScanResumed));
    public bool CanFire => StateText == "할당 완료";
    public bool LaunchRequested { get => _launchRequested; set { if (_launchRequested == value) return; _launchRequested = value; OnPropertyChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    public AssignmentView(uint targetId, uint fcsId) { TargetId = targetId; _fcsId = fcsId; }

    public void SetStageCommandPending(bool pending)
    {
        IsStageCommandPending = pending;
        OnPropertyChanged(nameof(StageCommandsEnabled));
        NotifyListActions();
    }

    private int _lastManualStep;
    private ResultCode _failureReason;
    private int ManualCompletedStep => _commandState switch
    {
        AssignmentState.Failed => _lastManualStep,
        AssignmentState.ManualAiming => 2,
        AssignmentState.TargetStillPresent => 8,
        >= AssignmentState.Detected and <= AssignmentState.ResultConfirmed => (int)_commandState.Value - 100,
        _ => 0
    };

    public void SetConnecting()
    {
        _connectionPending = true;
        _connectionFailed = false;
        _commandState = null;
        StateText = "연결 시도 중";
        OnPropertyChanged(nameof(StageCommandsEnabled));
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
        if (state != AssignmentState.Failed)
        {
            _lastManualStep = ManualCompletedStep;
            _failureReason = ResultCode.Success;
        }
        OnPropertyChanged(nameof(StageCommandsEnabled));
        RefreshStateText(null, null);
    }

    internal void SetManualStatus(AssignmentState state, ResultCode reason, uint completedStep)
    {
        if (state == AssignmentState.Failed)
        {
            _lastManualStep = (int)Math.Min(completedStep, 8u);
            _failureReason = reason;
        }
        SetCommandStatus(state, state == AssignmentState.FireConfirmed ? 1u : 0u);
    }

    private string FailureText => _failureReason switch
    {
        ResultCode.ResultUnconfirmed => "결과 미확정 · 동일 ID 재관측 시간 초과 (3초)",
        ResultCode.PauseTimeout => "진행 중단 · 실행 완료 전 조사 중지 제한 초과 (10초)",
        ResultCode.StatusPending => "진행 중단 · FCS 연결 또는 최신 상태 확인 불가",
        ResultCode.TargetPending => "진행 중단 · RDR 관측/보고 상태 확인 불가",
        ResultCode.TargetChanged => "진행 중단 · 대상 위치 또는 관측 세션 변경",
        ResultCode.DeviceFault => "진행 중단 · FCS 장치 오류",
        ResultCode.AlignmentRequired => "진행 중단 · 조준 정렬 확인 실패",
        ResultCode.LidarDisconnected => "진행 중단 · RDR 연결 끊김 또는 세션 변경",
        ResultCode.LidarThermalFault => "진행 중단 · 센서 고온 오류로 보호 정지",
        ResultCode.LidarStopWarningUnconfirmed => "진행 중단 · 온도 경고 동반 / 중지 완료 확인 실패",
        ResultCode.LidarStopUnconfirmed => "진행 중단 · 라이다 정지 상태 확인 실패",
        ResultCode.LidarResumeUnconfirmed => "진행 중단 · 라이다 재개 확인 실패",
        ResultCode.LidarUnknown => "진행 중단 · 라이다 방출 상태 확인 불가",
        ResultCode.FcsOffUnconfirmed => "진행 중단 · FCS 출력/동작 종료 확인 실패",
        ResultCode.LidarRecoveryRequired => "진행 중단 · 라이다 연동 복구 필요",
        ResultCode.ResponseTimeout => "진행 중단 · 장치 응답 시간 초과",
        ResultCode.DeviceRejected => "진행 중단 · 장치 명령 거부",
        ResultCode.DeviceError => "진행 중단 · 장치 처리 오류",
        ResultCode.Success => "진행 중단 · 상세 사유 확인 중",
        _ => $"진행 중단 · 오류 코드 {(uint)_failureReason}"
    };

    public void SetCommandUnavailable()
    {
        if (_connectionPending) return;
        _connectionFailed = false;
        _commandState = null;
        _fireState = 0;
        StateText = "명령 파이프 대기";
    }

    public void Refresh(TargetRecord? target, FcsAsset? fcs) => Refresh(target, fcs, true);

    internal void Refresh(TargetRecord? target, FcsAsset? fcs, bool isSharedMemoryLive)
    {
        if (target is null)
        {
            AzimuthText = ElevationText = DistanceText = LidarXText = LidarYText = LidarZText = RdrIdText = "—";
            TargetStatusText = "NO DATA";
            RefreshStateText(null, fcs);
            if (isSharedMemoryLive) RefreshStages(null);
            else UpdateStages(StageLabels.Select(label => new AssignmentStage(label, "")).ToArray());
            return;
        }
        if (fcs is not null)
        {
            var dx = target.LidarX - fcs.PositionX;
            var dy = target.LidarY - fcs.PositionY;
            var dz = target.LidarZ - fcs.PositionZ;
            var azimuth = Math.Atan2(dx, dy) * 180.0 / Math.PI + 135.0;
            azimuth = (azimuth % 360.0 + 360.0) % 360.0;
            var elevation = Math.Atan2(dz, Math.Sqrt(dx * dx + dy * dy)) * 180.0 / Math.PI;
            AzimuthText = $"{azimuth:0.0}°";
            ElevationText = $"{elevation:0.0}°";
        }
        else
        {
            AzimuthText = $"{target.AzimuthDeg:0.0}°";
            ElevationText = $"{target.ElevationDeg:0.0}°";
        }
        DistanceText = $"{target.DistanceM:0.00}";
        LidarXText = $"{target.LidarX:0.00}";
        LidarYText = $"{target.LidarY:0.00}";
        LidarZText = $"{target.LidarZ:0.00}";
        RdrIdText = target.RdrId.ToString();
        TargetStatusText = target.StatusText;
        RefreshStateText(target, fcs);
        RefreshStages(target);
    }

    private void RefreshStages(TargetRecord? target)
    {
        if (!HasAssignment) return;
        if (AwaitingEcsSlot)
        {
            var waitingStages = StageLabels.Select(label => new AssignmentStage(label, "대기")).ToArray();
            waitingStages[0] = new AssignmentStage("처리 시작", "대기", ActionLabel: "처리 시작",
                CanAdvance: EcsSlotAvailable && target?.CanAssign == true) { Command = CommandType.RegisterManualTarget };
            UpdateStages(waitingStages);
            return;
        }

        int completed = ManualCompletedStep;
        if (_commandState == AssignmentState.Failed)
        {
            // The ECS supplies progress even after reconnecting to a failed run.
            int[] thresholds = [3, 4, 6, 7];
            int failedIndex = Array.FindIndex(thresholds, threshold => completed < threshold);
            var failedStages = thresholds.Select((threshold, index) =>
                new AssignmentStage(StageLabels[index], completed >= threshold ? "완료" :
                    index == failedIndex ? "중단" : "대기", index == failedIndex)).ToList();
            failedStages.Add(new AssignmentStage(StageLabels[4],
                _failureReason == ResultCode.ResultUnconfirmed ? "미확정 · 재관측 시간 초과" :
                failedIndex < 0 ? "판정 중단" : "미판정", true, "할당 해제", true)
                { Command = CommandType.CancelAssignment });
            UpdateStages(failedStages.ToArray());
            return;
        }
        if (completed > 0)
        {
            bool aiming = _commandState == AssignmentState.ManualAiming;
            bool firing = _commandState == AssignmentState.Firing;
            bool batchPaused = IsBatch && (BatchFlags & 1) != 0;
            bool canAim = completed == 2 && !aiming &&
                (target?.CanAssign == true || (batchPaused && target?.IsObservationWaiting == true)) &&
                (!IsBatch || (batchPaused && (BatchFlags & 4) != 0));
            bool canFire = completed == 4 && (!IsBatch || (BatchFlags & 8) != 0);
            bool canPause = !batchPaused && (completed == 3 ||
                (IsBatch && completed == 2 && !aiming && (BatchFlags & 4) != 0 && target?.CanAssign == true));
            bool canResume = IsBatch ? ShowBatchResume : completed == 6;
            var cancelCommand = IsBatch ? CommandType.CancelBatchTarget : CommandType.CancelAssignment;
            var result = _commandState switch
            {
                AssignmentState.ResultConfirmed => new AssignmentStage(StageLabels[4], "완료 · HIT", true, "할당 해제", true)
                    { Command = cancelCommand },
                AssignmentState.TargetStillPresent when batchPaused =>
                    new AssignmentStage(StageLabels[4], "ALIVE", false, "다시 시도"),
                AssignmentState.TargetStillPresent when target?.CanAssign != true =>
                    new AssignmentStage(StageLabels[4], "이전 ID 없음 · 현재 목록에서 다시 선택", true, "할당 해제", true)
                    { Command = cancelCommand },
                AssignmentState.TargetStillPresent => new AssignmentStage(StageLabels[4], "ALIVE", true, "다시 시도", true)
                    { Command = IsBatch ? CommandType.RetryBatchTarget : CommandType.RetryManualAssignment },
                AssignmentState.ScanResumed => new AssignmentStage(StageLabels[4], "판정 중", true, "할당 해제"),
                _ => new AssignmentStage(StageLabels[4], "대기", ActionLabel: "할당 해제")
            };
            UpdateStages(
            [
                new(StageLabels[0], completed >= 3 ? "완료" : aiming ? "조준 중" : "대기", completed == 2,
                    "조준", canAim) { Command = IsBatch ? CommandType.AimBatchTarget : CommandType.StartManualAim },
                new(StageLabels[1], completed >= 4 || batchPaused ? "완료" : "대기", canPause,
                    IsBatch && batchPaused ? null : "중지", canPause)
                    { Command = IsBatch ? CommandType.PauseBatchScan : CommandType.PauseManualScan },
                new(StageLabels[2], completed >= 6 ? "완료" : firing ? "실행 중" : "대기", completed is 4 or 5,
                    "실행", canFire) { Command = IsBatch ? CommandType.FireBatchTarget : CommandType.StartManualFire },
                new(StageLabels[3], completed >= 7 ? "완료" : completed == 6 ?
                    "재개 대기" : canResume ? "재개 가능" : "대기", canResume,
                    IsBatch ? (canResume ? "조사 재개" : null) : "재개", canResume)
                    { Command = IsBatch ? CommandType.ResumeBatchScan : CommandType.ResumeManualScan },
                result
            ]);
            return;
        }

        bool assigned = _commandState is AssignmentState.Adjusting or AssignmentState.Assigned;
        bool aimed = _commandState == AssignmentState.Assigned;
        bool fireComplete = _fireState == 1;
        bool hit = target?.Status == 1;
        AssignmentStage[] stages =
        [
            new(StageLabels[0], aimed ? "완료" : assigned ? "진행 중" : "대기", assigned && !fireComplete && !hit),
            new(StageLabels[1], "미확인"),
            new(StageLabels[2], fireComplete ? "완료" : "대기"),
            new(StageLabels[3], "미확인"),
            new(StageLabels[4], hit ? "명중" : "미판정", hit, "할당 해제")
        ];
        UpdateStages(stages);
    }

    private void UpdateStages(AssignmentStage[] stages)
    {
        if (Stages.SequenceEqual(stages)) return;
        Stages = stages;
        OnPropertyChanged(nameof(Stages));
        NotifyListActions();
    }

    private void NotifyListActions()
    {
        OnPropertyChanged(nameof(PrimaryAction));
        OnPropertyChanged(nameof(CanUsePrimaryAction));
        OnPropertyChanged(nameof(CanFireFromList));
        OnPropertyChanged(nameof(ListActionLabel));
        OnPropertyChanged(nameof(CanUseListAction));
    }

    private void RefreshStateText(TargetRecord? target, FcsAsset? fcs)
    {
        if (AwaitingEcsSlot) { StateText = EcsSlotAvailable ? "처리 시작 대기" : "FCS 사용 대기"; return; }
        if (_connectionPending) { StateText = "연결 시도 중"; return; }
        if (_connectionFailed) { StateText = "연결 실패 · 재시도 가능"; return; }
        if (_commandState == AssignmentState.Failed) { StateText = FailureText; return; }
        if (ManualCompletedStep > 0)
        {
            StateText = MainWindow.AssignmentStateText(_commandState!.Value);
            return;
        }
        if (target?.IsObservationWaiting == true) { StateText = target.StatusText; return; }
        if (_fireState != 0) { StateText = "실행 완료"; return; }
        if (target?.Status == 1) { StateText = "실행 완료"; return; }
        if (fcs is null) { StateText = "FCS 데이터 대기"; return; }
        if (!fcs.IsEnabled) { StateText = "FCS 사용 불가"; return; }
        if (!fcs.IsOperating) { StateText = "FCS 정지"; return; }
        if (!fcs.IsConnected) { StateText = "FCS 연결 대기"; return; }
        if (_commandState is null) { StateText = "명령 파이프 대기"; return; }
        StateText = _commandState.Value switch
        {
            AssignmentState.Idle => "명령 대기",
            AssignmentState.Adjusting => "FCS 조준 중",
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
    private float _azimuthDeg, _elevationDeg;
    private float _positionX, _positionY, _positionZ, _fieldOfViewDeg;
    public bool IsEnabled { get => _isEnabled; private set => Set(ref _isEnabled, value); }
    public bool IsOperating { get => _isOperating; private set => Set(ref _isOperating, value); }
    public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }
    public float AzimuthDeg { get => _azimuthDeg; private set => Set(ref _azimuthDeg, value); }
    public float ElevationDeg { get => _elevationDeg; private set => Set(ref _elevationDeg, value); }
    public float PositionX { get => _positionX; private set => Set(ref _positionX, value); }
    public float PositionY { get => _positionY; private set => Set(ref _positionY, value); }
    public float PositionZ { get => _positionZ; private set => Set(ref _positionZ, value); }
    public float FieldOfViewDeg { get => _fieldOfViewDeg; private set => Set(ref _fieldOfViewDeg, value); }
    public string DisplayName => $"RDR-{Id:000}";
    public event PropertyChangedEventHandler? PropertyChanged;
    public RdrAsset(uint id, bool isEnabled, bool isOperating, bool isConnected, float azimuthDeg, float elevationDeg, float positionX, float positionY, float positionZ, float fieldOfViewDeg)
    { Id = id; UpdateFrom(isEnabled, isOperating, isConnected, azimuthDeg, elevationDeg, positionX, positionY, positionZ, fieldOfViewDeg); }
    public void UpdateFrom(RdrAsset other) => UpdateFrom(other.IsEnabled, other.IsOperating, other.IsConnected, other.AzimuthDeg, other.ElevationDeg, other.PositionX, other.PositionY, other.PositionZ, other.FieldOfViewDeg);
    private void UpdateFrom(bool enabled, bool operating, bool connected, float az, float el, float x, float y, float z, float fov)
    { IsEnabled = enabled; IsOperating = operating; IsConnected = connected; AzimuthDeg = az; ElevationDeg = el; PositionX = x; PositionY = y; PositionZ = z; FieldOfViewDeg = fov; }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
}

public sealed class FcsAsset : INotifyPropertyChanged
{
    public uint Id { get; }
    private bool _isEnabled, _isOperating, _isConnected, _laserOn;
    private float _azimuthDeg, _elevationDeg;
    private float _positionX, _positionY, _positionZ;
    public bool IsEnabled { get => _isEnabled; private set => Set(ref _isEnabled, value); }
    public bool IsOperating { get => _isOperating; private set => Set(ref _isOperating, value); }
    public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }
    public bool LaserOn { get => _laserOn; private set => Set(ref _laserOn, value); }
    public float AzimuthDeg { get => _azimuthDeg; private set => Set(ref _azimuthDeg, value); }
    public float ElevationDeg { get => _elevationDeg; private set => Set(ref _elevationDeg, value); }
    public float PositionX { get => _positionX; private set => Set(ref _positionX, value); }
    public float PositionY { get => _positionY; private set => Set(ref _positionY, value); }
    public float PositionZ { get => _positionZ; private set => Set(ref _positionZ, value); }
    public string DisplayName => $"FCS-{Id:000}";
    public event PropertyChangedEventHandler? PropertyChanged;
    public FcsAsset(uint id, bool isEnabled, bool isOperating, bool isConnected, bool laserOn, float azimuthDeg, float elevationDeg, float positionX, float positionY, float positionZ)
    { Id = id; UpdateFrom(isEnabled, isOperating, isConnected, laserOn, azimuthDeg, elevationDeg, positionX, positionY, positionZ); }
    public void UpdateFrom(FcsAsset other) => UpdateFrom(other.IsEnabled, other.IsOperating, other.IsConnected, other.LaserOn, other.AzimuthDeg, other.ElevationDeg, other.PositionX, other.PositionY, other.PositionZ);
    private void UpdateFrom(bool enabled, bool operating, bool connected, bool laser, float az, float el, float x, float y, float z)
    { IsEnabled = enabled; IsOperating = operating; IsConnected = connected; LaserOn = laser; AzimuthDeg = az; ElevationDeg = el; PositionX = x; PositionY = y; PositionZ = z; }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
}

public sealed class TargetRecord : INotifyPropertyChanged
{
    private uint _selectedFcsId;
    public uint Id { get; }
    private float _azimuthDeg, _elevationDeg;
    private float _distanceM, _lidarX, _lidarY, _lidarZ;
    private bool _hasLocalPositionOverride;
    private uint _rdrId;
    private byte _status, _valid;
    private bool _isAssigned, _isAssignmentPending;
    public float AzimuthDeg { get => _azimuthDeg; private set => Set(ref _azimuthDeg, value); }
    public float ElevationDeg { get => _elevationDeg; private set => Set(ref _elevationDeg, value); }
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
    public string AzimuthText => $"{AzimuthDeg:0.0}°";
    public string ElevationText => $"{ElevationDeg:0.0}°";
    public string DistanceText => $"{DistanceM:0.00}";
    public string LidarXText => LidarX.ToString("0.00");
    public string LidarYText => LidarY.ToString("0.00");
    public string LidarZText => LidarZ.ToString("0.00");
    public bool IsObservationWaiting => Status is 4 or 5;
    public bool CanAssign => Valid == 1 && Status == 0;
    public bool CanClickAssign => CanAssign || Status == 4;
    public string StatusText => Status switch { 0 => "ALIVE", 1 => "HIT", 2 => "MISSING", 4 => "관측 일시중지", 5 => "재관측 대기", _ => "UNKNOWN" };
    public string ValidText => Valid == 1 ? "YES" : "NO";
    public event PropertyChangedEventHandler? PropertyChanged;

    public TargetRecord(uint id, float azimuthDeg, float elevationDeg, float distanceM, float lidarX, float lidarY, float lidarZ, uint rdrId, byte status, bool valid, uint selectedFcsId, bool isAssigned)
    {
        Id = id; _selectedFcsId = selectedFcsId; _isAssigned = isAssigned;
        UpdateFrom(azimuthDeg, elevationDeg, distanceM, lidarX, lidarY, lidarZ, rdrId, status, valid ? (byte)1 : (byte)0);
    }
    public void UpdateFrom(TargetRecord other) => UpdateFrom(other.AzimuthDeg, other.ElevationDeg, other.DistanceM, other.LidarX, other.LidarY, other.LidarZ, other.RdrId, other.Status, other.Valid);
    internal void SetLocalPositionOverride(float x, float y, float z)
    {
        _hasLocalPositionOverride = true;
        LidarX = x; LidarY = y; LidarZ = z;
    }
    internal void MarkHit() => Status = 1;
    private void UpdateFrom(float az, float el, float distance, float x, float y, float z, uint rdrId, byte status, byte valid)
    { AzimuthDeg = az; ElevationDeg = el; DistanceM = distance; if (!_hasLocalPositionOverride) { LidarX = x; LidarY = y; LidarZ = z; } RdrId = rdrId; Status = status; Valid = valid; }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name is nameof(Status) or nameof(Valid))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanAssign)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanClickAssign)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsObservationWaiting)));
        }
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

internal sealed class TargetCoordinateEditorWindow : Window
{
    private readonly TextBox _xInput;
    private readonly TextBox _yInput;
    private readonly TextBox _zInput;

    internal float PositionX { get; private set; }
    internal float PositionY { get; private set; }
    internal float PositionZ { get; private set; }

    internal TargetCoordinateEditorWindow(uint targetId, float x, float y, float z)
    {
        Title = $"TGT-{targetId} 좌표 수정";
        Width = 340;
        Height = 230;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(9, 20, 17));
        Foreground = new SolidColorBrush(Color.FromRgb(226, 239, 232));

        _xInput = CreateInput(x);
        _yInput = CreateInput(y);
        _zInput = CreateInput(z);

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = "발사 조준에 사용할 로컬 좌표 (m)",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 10)
        });
        AddCoordinateRow(root, "X", _xInput, 1);
        AddCoordinateRow(root, "Y", _yInput, 2);
        AddCoordinateRow(root, "Z", _zInput, 3);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var save = new Button { Content = "적용", Width = 72, Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
        save.Click += (_, _) => SaveCoordinates();
        var cancel = new Button { Content = "취소", Width = 72, IsCancel = true };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);
        Content = root;
    }

    private static TextBox CreateInput(float value) => new()
    {
        Text = value.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture),
        MinWidth = 190,
        Margin = new Thickness(8, 3, 0, 3),
        Padding = new Thickness(6, 3, 6, 3),
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private static void AddCoordinateRow(Grid root, string label, TextBox input, int row)
    {
        var line = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 2) };
        line.Children.Add(new TextBlock { Text = label, Width = 28, VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(input);
        Grid.SetRow(line, row);
        root.Children.Add(line);
    }

    private void SaveCoordinates()
    {
        if (!TryRead(_xInput, out var x) || !TryRead(_yInput, out var y) || !TryRead(_zInput, out var z))
        {
            MessageBox.Show(this, "X, Y, Z에 유효한 숫자를 입력하세요.", "좌표 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        PositionX = x;
        PositionY = y;
        PositionZ = z;
        DialogResult = true;
    }

    private static bool TryRead(TextBox input, out float value) =>
        (float.TryParse(input.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out value) ||
         float.TryParse(input.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)) &&
        float.IsFinite(value);
}

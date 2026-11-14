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
using System.Windows.Data;

namespace AadcCommandCenter;

public partial class MainWindow : Window
{
    private readonly DashboardData _data = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _commandStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _rdrElevationSendTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly PipeCommandClient _pipeClient = new();
    private readonly Dictionary<uint, uint> _targetFcsSelections = new();
    private readonly HashSet<uint> _assignedTargets = new();
    private readonly Dictionary<uint, AssignmentView> _assignmentsByTarget = new();
    private readonly HashSet<uint> _pendingFcsAssignments = new();
    private readonly ObservableCollection<SequenceQueueItem> _sequenceQueue = new();
    private readonly ICollectionView _sequenceQueueView;
    private bool _sequenceRunning, _stopSequenceRequested;
    private SharedMemoryFrameReader? _frameReader;
    private bool _isSharedMemoryLive;
    private bool _isCommandPipeLive;
    private bool _isRefreshingCommandStatus;
    private bool? _previousConnectionState;
    private AssignmentState? _previousAssignmentState;
    private uint? _selectedTargetId;
    private double _workspaceZoom = 1.0;
    private bool _workspaceLayoutRefreshQueued;
    private bool _rdrPositionDirty, _fcsPositionDirty;
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
        AssignmentGrid.ItemsSource = _data.Assignments;
        QueueFcsSelector.ItemsSource = _data.FcsAssets;
        _sequenceQueueView = CollectionViewSource.GetDefaultView(_sequenceQueue);
        _sequenceQueueView.Filter = item => item is SequenceQueueItem queued && QueueFcsSelector.SelectedItem is FcsAsset selectedFcs && queued.FcsId == selectedFcs.Id;
        SequenceQueueGrid.ItemsSource = _sequenceQueueView;
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
            UpdateQueueUploadControl();
            UpdateRdrElevationControl(RdrSelector.SelectedItem as RdrAsset);
            UpdateRdrOperatingControl(RdrSelector.SelectedItem as RdrAsset);
            SharedMemoryStatus.Text = state;
            SharedMemoryStatus.Foreground = isLive
                ? new SolidColorBrush(Color.FromRgb(0, 230, 138))
                : new SolidColorBrush(Color.FromRgb(255, 176, 0));
            RefreshRdrInfo(RdrSelector.SelectedItem as RdrAsset);
            RefreshFcsInfo(FcsSelector.SelectedItem as FcsAsset);
            RefreshAssignments();
        });
        _frameReader.FrameReceived += frame => Dispatcher.BeginInvoke(() => ApplyFrame(frame));
        Loaded += async (_, _) => { UpdateClock(); DrawRadar(); _clock.Start(); _frameReader.Start(); _commandStatusTimer.Start(); QueueWorkspaceLayoutRefresh(); await RefreshCommandStatusAsync(); await RefreshCameraSettingsAsync(); await RefreshRdrStartupAsync(); };
        Closed += (_, _) => { _clock.Stop(); _commandStatusTimer.Stop(); _rdrElevationSendTimer.Stop(); _frameReader.Stop(); _frameReader.Dispose(); _pipeClient.Dispose(); };
        _clock.Tick += (_, _) => UpdateClock();
        _commandStatusTimer.Tick += async (_, _) => { await RefreshCommandStatusAsync(); await RefreshCameraSettingsAsync(); await RefreshRdrStartupAsync(); };
        _rdrElevationSendTimer.Tick += async (_, _) =>
        {
            _rdrElevationSendTimer.Stop();
            await SendRdrElevationCommandAsync();
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
            .Select(x => new RdrAsset(x.rdrId, x.isEnabled != 0, x.isOperating != 0, x.isConnected != 0, x.azimuthDeg, x.elevationDeg, x.positionX, x.positionY, x.positionZ, x.fieldOfViewDeg)).ToArray();
        var fcs = payload.fcsAssets.Take((int)payload.fcsCount)
            .Select(x => new FcsAsset(x.fcsId, x.isEnabled != 0, x.isOperating != 0, x.isConnected != 0, x.laserOn != 0, x.azimuthDeg, x.elevationDeg, x.positionX, x.positionY, x.positionZ)).ToArray();
        var targets = payload.targets.Take((int)payload.targetCount).Where(x => x.valid == 1 || (x.valid == 0 && x.status is 4 or 5))
            .Select(x => new TargetRecord(x.targetId, x.azimuthDeg, x.elevationDeg, x.distanceM, x.lidarX, x.lidarY, x.lidarZ, x.rdrId, x.status, x.valid == 1,
                _targetFcsSelections.TryGetValue(x.targetId, out var selectedFcsId) ? selectedFcsId : payload.selectedFcsId,
                _assignedTargets.Contains(x.targetId))).ToArray();

        SyncById(_data.RdrAssets, rdrs, x => x.Id, (current, next) => current.UpdateFrom(next));
        SyncById(_data.FcsAssets, fcs, x => x.Id, (current, next) => current.UpdateFrom(next));
        SyncById(_data.Targets, targets, x => x.Id, (current, next) => current.UpdateFrom(next));
        var defaultFcsId = _data.FcsAssets.FirstOrDefault(asset => asset.Id == payload.selectedFcsId)?.Id
            ?? _data.FcsAssets.FirstOrDefault()?.Id;
        if (defaultFcsId.HasValue)
        {
            foreach (var target in _data.Targets)
                if (!_data.FcsAssets.Any(asset => asset.Id == target.SelectedFcsId))
                    target.SelectedFcsId = defaultFcsId.Value;
        }
        if (QueueFcsSelector.SelectedItem is null && _data.FcsAssets.Count > 0)
            QueueFcsSelector.SelectedItem = _data.FcsAssets[0];
        RdrSelector.SelectedItem = _data.RdrAssets.FirstOrDefault(x => x.Id == payload.selectedRdrId) ?? _data.RdrAssets.FirstOrDefault();
        FcsSelector.SelectedItem = _data.FcsAssets.FirstOrDefault(x => x.Id == payload.selectedFcsId) ?? _data.FcsAssets.FirstOrDefault();
        RefreshRdrInfo(RdrSelector.SelectedItem as RdrAsset);
        SyncRdrElevationFromTelemetry(RdrSelector.SelectedItem as RdrAsset);
        RefreshFcsInfo(FcsSelector.SelectedItem as FcsAsset);
        RefreshAssignments();
        TargetCountText.Text = $"{_data.Targets.Count} / {SharedMemoryProtocol.TargetCapacity}";
        UpdateQueueUploadControl();
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
            if (result == ResultCode.Success) AddLog("RDR 구동 완료 · 라이다 방출 재개 확인");
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

    private async void RdrPositionApplyClick(object sender, RoutedEventArgs e) => await ApplyAssetPositionAsync(true);
    private async void FcsPositionApplyClick(object sender, RoutedEventArgs e) => await ApplyAssetPositionAsync(false);
    private void RdrPositionToggleClick(object sender, RoutedEventArgs e) => TogglePositionEditor(RdrPositionEditor, RdrPositionToggleButton, "RDR 영점 보정 (m)", "RDR 영점 보정 접기");
    private void FcsPositionToggleClick(object sender, RoutedEventArgs e) => TogglePositionEditor(FcsPositionEditor, FcsPositionToggleButton, "FCS 영점 보정 (m)", "FCS 영점 보정 접기");

    private static void TogglePositionEditor(Border editor, Button button, string openText, string closeText)
    {
        var currentHeight = editor.ActualHeight;
        var opening = editor.Visibility != Visibility.Visible || Equals(button.Content, openText);
        editor.BeginAnimation(FrameworkElement.HeightProperty, null);
        if (opening)
        {
            editor.Visibility = Visibility.Visible;
            editor.Height = 0;
            button.Content = closeText;
            var animation = new DoubleAnimation(0, 58, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            animation.Completed += (_, _) => { editor.Height = 58; editor.BeginAnimation(FrameworkElement.HeightProperty, null); };
            editor.BeginAnimation(FrameworkElement.HeightProperty, animation);
        }
        else
        {
            var animation = new DoubleAnimation(currentHeight, 0, TimeSpan.FromMilliseconds(180));
            animation.Completed += (_, _) => { editor.Visibility = Visibility.Collapsed; editor.Height = 0; button.Content = openText; };
            editor.BeginAnimation(FrameworkElement.HeightProperty, animation);
        }
    }
    private void RdrPositionTextChanged(object sender, TextChangedEventArgs e) { if (!_syncingPositionInputs) _rdrPositionDirty = true; }
    private void FcsPositionTextChanged(object sender, TextChangedEventArgs e) { if (!_syncingPositionInputs) _fcsPositionDirty = true; }

    private async Task ApplyAssetPositionAsync(bool isRdr)
    {
        var xText = isRdr ? RdrXInput.Text : FcsXInput.Text;
        var yText = isRdr ? RdrYInput.Text : FcsYInput.Text;
        var zText = isRdr ? RdrZInput.Text : FcsZInput.Text;
        if (!TryCoordinate(xText, out var x) || !TryCoordinate(yText, out var y) || !TryCoordinate(zText, out var z))
        {
            ShowAlert("좌표 X, Y, Z를 유효한 숫자로 입력하세요.", "좌표 입력 확인");
            return;
        }
        var rdr = RdrSelector.SelectedItem as RdrAsset;
        var fcs = FcsSelector.SelectedItem as FcsAsset;
        if (!_isSharedMemoryLive || (isRdr ? rdr is null : fcs is null))
        {
            ShowAlert("공유 메모리와 연결된 자산이 필요합니다.", "좌표 적용 불가");
            return;
        }
        try
        {
            var response = await _pipeClient.SendAsync(CommandType.SetCameraSettings, targetId: isRdr ? 0u : 1u, rdrId: 0, fcsId: 0,
                positionX: x, positionY: y, positionZ: z);
            if ((ResultCode)response.ResultCode != ResultCode.Success)
                throw new IOException(CommandResultText((ResultCode)response.ResultCode));
            if (isRdr) _rdrPositionDirty = false; else _fcsPositionDirty = false;
            await RefreshCameraSettingsAsync();
            AddLog($"{(isRdr ? "RDR" : "FCS")} 영점 보정 저장 · X={x:0.###}, Y={y:0.###}, Z={z:0.###} m");
        }
        catch (Exception ex) { ShowAlert($"좌표 적용에 실패했습니다: {ex.Message}", "좌표 적용 실패"); }
    }

    private static bool TryCoordinate(string value, out float coordinate) =>
        float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out coordinate) && float.IsFinite(coordinate);

    private void RefreshRdrInfo(RdrAsset? rdr)
    {
        if (RdrIdText is null) return;
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
        double minX = new[] { rdr.PositionX, fcs?.PositionX ?? rdr.PositionX }.Concat(targets.Select(x => x.LidarX)).Min();
        double maxX = new[] { rdr.PositionX, fcs?.PositionX ?? rdr.PositionX }.Concat(targets.Select(x => x.LidarX)).Max();
        double minY = new[] { rdr.PositionY, fcs?.PositionY ?? rdr.PositionY }.Concat(targets.Select(x => x.LidarY)).Min();
        double maxY = new[] { rdr.PositionY, fcs?.PositionY ?? rdr.PositionY }.Concat(targets.Select(x => x.LidarY)).Max();
        var ranges = _cameraSettings?[2];
        var fov = _cameraSettings is { Length: > 3 } ? _cameraSettings[3] : null;
        if (ranges != null)
        {
            minX = Math.Min(minX, (double)rdr.PositionX - ranges[1]); maxX = Math.Max(maxX, (double)rdr.PositionX + ranges[1]);
            minY = Math.Min(minY, (double)rdr.PositionY - ranges[1]); maxY = Math.Max(maxY, (double)rdr.PositionY + ranges[1]);
            if (fcs != null)
            {
                minX = Math.Min(minX, (double)fcs.PositionX - ranges[2]); maxX = Math.Max(maxX, (double)fcs.PositionX + ranges[2]);
                minY = Math.Min(minY, (double)fcs.PositionY - ranges[2]); maxY = Math.Max(maxY, (double)fcs.PositionY + ranges[2]);
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
        var rdrPoint = Map(rdr.PositionX, rdr.PositionY);
        var rdrDisplayAzimuth = Math.Clamp(rdr.AzimuthDeg, 0, 270) - 135d;
        var viewRadius = ranges != null ? ranges[1] * scale : Math.Min(canvasWidth, canvasHeight) * .3;
        if (ranges != null)
        {
            var overlay = new CameraMapOverlay
            {
                Width = canvasWidth, Height = canvasHeight, RdrCenter = rdrPoint,
                FcsCenter = fcs == null ? null : Map(fcs.PositionX, fcs.PositionY),
                MinimumRadius = ranges[0] * scale, MaximumRadius = viewRadius,
                CaptureRadius = ranges[2] * scale, Heading = rdrDisplayAzimuth,
                IsHitTestVisible = false, HorizontalFieldOfView = fov?[0] ?? 120
            };
            RadarCanvas.Children.Add(overlay);
            MapRangeSummary.Text = $"RDR {ranges[0]:G4}–{ranges[1]:G4} m · FCS shoot ≤ {ranges[2]:G4} m";
        }
        else MapRangeSummary.Text = "거리 설정: ECS 연결 대기";
        MapFovSummary.Text = $"XT-M60 · 수평 {fov?[0] ?? 120:G4}° / 수직 {fov?[1] ?? 45:G4}°\n방향 {rdrDisplayAzimuth:0.#}° · 고각 {rdr.ElevationDeg:0.#}°" +
            (_isSharedMemoryLive ? "" : " · 마지막 수신값");

        var fcsPoint = fcs is null ? (Point?)null : Map(fcs.PositionX, fcs.PositionY);
        var assignedTargetId = fcs is null ? (uint?)null : _data.Assignments.FirstOrDefault(x => x.FcsId == fcs.Id)?.TargetId;
        var laserTarget = targets.FirstOrDefault(x => x.Id == assignedTargetId);
        if (fcs?.LaserOn == true && fcsPoint is Point fp && laserTarget is not null)
        {
            var tp = Map(laserTarget.LidarX, laserTarget.LidarY);
            RadarCanvas.Children.Add(new Line { X1 = fp.X, Y1 = fp.Y, X2 = tp.X, Y2 = tp.Y, Stroke = new SolidColorBrush(Color.FromRgb(255, 65, 82)), StrokeThickness = 1.5 });
        }

        AddMapLabel("Y ↑   ·   거리 m", 5, canvasHeight - 17, Color.FromRgb(82, 121, 109));
        if (fcs is not null) AddAssetMarker(fcsPoint!.Value, "FCS", Color.FromRgb(30, 125, 157), fcs.PositionX, fcs.PositionY);
        AddAssetMarker(rdrPoint, "RDR", Color.FromRgb(0, 190, 225), rdr.PositionX, rdr.PositionY);

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

        void AddAssetMarker(Point p, string label, Color color, float x, float y)
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

    private void SelectAllTargetsClick(object sender, RoutedEventArgs e)
    {
        var selected = (sender as CheckBox)?.IsChecked == true;
        foreach (var target in _data.Targets) target.IsSelected = selected;
        UpdateQueueUploadControl();
    }

    private void TargetCheckboxChanged(object sender, RoutedEventArgs e) => Dispatcher.BeginInvoke(new Action(UpdateQueueUploadControl));
    private void QueueFcsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var item in _sequenceQueue) item.IsSelected = false;
        if (_sequenceQueueView is not null) _sequenceQueueView.Refresh();
        RefreshSequenceQueue();
    }

    private void QueueSelectAllClick(object sender, RoutedEventArgs e)
    {
        if (QueueFcsSelector.SelectedItem is not FcsAsset fcs) return;
        var selectAll = QueueSelectAllCheckBox.IsChecked == true;
        foreach (var item in _sequenceQueue.Where(item => item.FcsId == fcs.Id)) item.IsSelected = selectAll;
        UpdateQueueSelectAllControl();
    }

    private void QueueItemSelectionChanged(object sender, RoutedEventArgs e) => Dispatcher.BeginInvoke(new Action(UpdateQueueSelectAllControl));

    private void UpdateQueueSelectAllControl()
    {
        if (QueueSelectAllCheckBox is null) return;
        var fcsId = (QueueFcsSelector.SelectedItem as FcsAsset)?.Id;
        var visibleItems = _sequenceQueue.Where(item => item.FcsId == fcsId).ToArray();
        QueueSelectAllCheckBox.IsChecked = visibleItems.Length > 0 && visibleItems.All(item => item.IsSelected);
    }

    private void SequenceDeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        if (_sequenceRunning) { ShowAlert("순차 할당 중에는 대기 목록을 삭제할 수 없습니다.", "대기 삭제"); return; }
        if (QueueFcsSelector.SelectedItem is not FcsAsset fcs) { ShowAlert("먼저 대기 목록의 FCS을 선택하세요.", "대기 삭제"); return; }
        var selectedItems = _sequenceQueue.Where(item => item.FcsId == fcs.Id && item.IsSelected).ToArray();
        if (selectedItems.Length == 0) { ShowAlert("삭제할 객체를 체크하세요.", "대기 삭제"); return; }
        foreach (var item in selectedItems) _sequenceQueue.Remove(item);
        RefreshSequenceQueue();
        AddLog($"{fcs.DisplayName} 대기 목록에서 {selectedItems.Length}개 객체 삭제");
    }

    private void QueueMoveSelectedUpClick(object sender, RoutedEventArgs e) => MoveSelectedQueueItems(-1);
    private void QueueMoveSelectedDownClick(object sender, RoutedEventArgs e) => MoveSelectedQueueItems(1);

    private void MoveSelectedQueueItems(int direction)
    {
        if (_sequenceRunning) return;
        if (QueueFcsSelector.SelectedItem is not FcsAsset fcs) return;
        var visible = _sequenceQueue.Where(item => item.FcsId == fcs.Id).ToList();
        if (!visible.Any(item => item.IsSelected)) { ShowAlert("순서를 바꿀 객체를 체크하세요.", "대기 목록 순서"); return; }

        if (direction < 0)
        {
            for (var i = 1; i < visible.Count; i++)
            {
                if (!visible[i].IsSelected || visible[i - 1].IsSelected) continue;
                var moving = visible[i];
                var displaced = visible[i - 1];
                _sequenceQueue.Move(_sequenceQueue.IndexOf(moving), _sequenceQueue.IndexOf(displaced));
                visible[i - 1] = moving;
                visible[i] = displaced;
            }
        }
        else
        {
            for (var i = visible.Count - 2; i >= 0; i--)
            {
                if (!visible[i].IsSelected || visible[i + 1].IsSelected) continue;
                var moving = visible[i];
                var displaced = visible[i + 1];
                _sequenceQueue.Move(_sequenceQueue.IndexOf(moving), _sequenceQueue.IndexOf(displaced));
                visible[i + 1] = moving;
                visible[i] = displaced;
            }
        }
        RefreshSequenceQueue();
    }

    private void UpdateQueueUploadControl()
    {
        if (QueueUploadButton is null) return;
        QueueUploadButton.IsEnabled = _data.Targets.Count > 0 && !_sequenceRunning;
    }

    private void TargetQueueToggleClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TargetRecord target) return;
        if (_sequenceRunning) { ShowAlert("순차 할당 실행 중에는 대기 목록을 변경할 수 없습니다.", "객체할당 대기 목록"); return; }
        var queuedItem = _sequenceQueue.FirstOrDefault(item => item.TargetId == target.Id);
        if (queuedItem is not null)
        {
            _sequenceQueue.Remove(queuedItem);
            RefreshSequenceQueue();
            AddLog($"대기 목록에서 객체 해제 · {target.DisplayName}");
            return;
        }
        if (!EnsureSharedMemoryLiveForCommand("대기 목록 추가")) return;
        var fcs = _data.FcsAssets.FirstOrDefault(asset => asset.Id == target.SelectedFcsId);
        if (fcs is null) { ShowAlert($"{target.DisplayName} 행의 FCS을 먼저 선택하세요.", "객체할당 대기 목록"); return; }
        if (!target.CanAssign) { ShowAlert("생존 상태인 객체만 대기 목록에 넣을 수 있습니다.", "객체할당 대기 목록"); return; }
        if (_data.Assignments.Any(item => item.TargetId == target.Id)) { ShowAlert($"{target.DisplayName}은(는) 객체 할당 정보에 있습니다. 먼저 할당을 취소하세요.", "중복 등록 차단"); return; }
        var rdr = _data.RdrAssets.FirstOrDefault(asset => asset.Id == target.RdrId);
        if (rdr is null || !rdr.IsConnected || !rdr.IsOperating) { ShowAlert("라이다가 연결되어 구동 중일 때만 객체를 대기 목록에 넣을 수 있습니다.", "라이다 구동 확인"); return; }
        _sequenceQueue.Add(new SequenceQueueItem(target.Id, fcs.Id, target.RdrId));
        RefreshSequenceQueue();
        AddLog($"대기 목록에 객체 추가 · {target.DisplayName} → {fcs.DisplayName}");
    }

    private void AddSelectedTargetsToQueueClick(object sender, RoutedEventArgs e)
    {
        if (_sequenceRunning) { ShowAlert("순차 할당 진행 중에는 대기 목록을 변경할 수 없습니다.", "객체할당 대기 목록"); return; }
        if (QueueFcsSelector.SelectedItem is not FcsAsset fcs) { ShowAlert("우상단 대기 목록 FCS를 선택하세요.", "객체할당 대기 목록"); return; }
        if (!EnsureSharedMemoryLiveForCommand("대기 목록 올리기")) return;
        var selectedTargets = _data.Targets.Where(x => x.IsSelected).ToArray();
        if (selectedTargets.Length == 0) { ShowAlert("객체 목록에서 먼저 객체를 체크하세요.", "객체할당 대기 목록"); return; }
        var invalidTargets = new List<string>();
        foreach (var target in selectedTargets)
        {
            var reasons = new List<string>();
            if (!target.CanAssign) reasons.Add("생존 상태 아님");
            if (_sequenceQueue.Any(item => item.TargetId == target.Id)) reasons.Add("이미 대기 목록에 등록됨");
            if (_data.Assignments.Any(item => item.TargetId == target.Id)) reasons.Add("객체 할당 정보에 등록됨");
            var rdr = _data.RdrAssets.FirstOrDefault(asset => asset.Id == target.RdrId);
            if (rdr is null || !rdr.IsConnected || !rdr.IsOperating) reasons.Add("라이다 미연결 또는 정지");
            if (reasons.Count > 0) invalidTargets.Add($"{target.DisplayName}: {string.Join(", ", reasons)}");
        }
        if (invalidTargets.Count > 0)
        {
            AddLog($"대기 목록 일괄 등록 거부 · 전체 취소 · {string.Join("; ", invalidTargets)}");
            ShowAlert($"다음 객체를 등록할 수 없어 이번 요청의 객체는 하나도 대기 목록에 추가하지 않았습니다.\n\n{string.Join(Environment.NewLine, invalidTargets)}", "대기 목록 등록 실패");
            return;
        }
        foreach (var target in selectedTargets)
        {
            _sequenceQueue.Add(new SequenceQueueItem(target.Id, fcs.Id, target.RdrId));
            target.IsSelected = false;
        }
        SelectAllTargetsCheckBox.IsChecked = false;
        RefreshSequenceQueue();
        AddLog($"객체할당 대기 목록에 {selectedTargets.Length}개 등록 · {fcs.DisplayName}");
    }

    private async void SequenceStartClick(object sender, RoutedEventArgs e)
    {
        if (_sequenceRunning) { _stopSequenceRequested = true; SequenceStartButton.IsEnabled = false; return; }
        if (!EnsureSharedMemoryLiveForCommand("객체 순차 할당")) return;
        if (QueueFcsSelector.SelectedItem is not FcsAsset selectedQueueFcs) { ShowAlert("순차 할당할 FCS을 선택하세요.", "객체 순차 할당"); return; }
        var pendingItems = _sequenceQueue.Where(item => item.FcsId == selectedQueueFcs.Id &&
            (item.ResultText == "대기" || item.ResultText.StartsWith("실패", StringComparison.Ordinal) || item.ResultText.StartsWith("중단 ·", StringComparison.Ordinal) || item.ResultText.StartsWith("중지 ·", StringComparison.Ordinal))).ToArray();
        if (pendingItems.Length == 0) { ShowAlert($"{selectedQueueFcs.DisplayName} 대기 목록에 순차 처리할 객체가 없습니다.", "객체 순차 할당"); return; }
        foreach (var item in pendingItems) item.ResultText = "대기";
        _sequenceRunning = true;
        QueueFcsSelector.IsEnabled = false;
        UpdateQueueUploadControl();
        _stopSequenceRequested = false;
        SequenceStartButton.Content = "중지 요청";
        AddLog($"{selectedQueueFcs.DisplayName} 객체 순차 할당 시작 · {pendingItems.Length}개 객체");
        var sequenceOwnsCurrentAssignment = false;
        var waitForFcs = false;
        SequenceQueueItem? currentSequenceItem = null;
        try
        {
            foreach (var item in pendingItems)
            {
                if (_stopSequenceRequested) break;
                currentSequenceItem = item;
                sequenceOwnsCurrentAssignment = false;
                if (waitForFcs)
                {
                    item.ResultText = SequenceQueueItem.FcsBusyWait;
                    RefreshSequenceQueue();
                    if (!await WaitForFcsIdleAsync(item))
                    {
                        item.ResultText = "대기";
                        break;
                    }
                }
                item.ResultText = "할당 요청";
                RefreshSequenceQueue();
                var target = _data.Targets.FirstOrDefault(x => x.Id == item.TargetId);
                var fcs = _data.FcsAssets.FirstOrDefault(x => x.Id == item.FcsId);
                var reacquireWait = Stopwatch.StartNew();
                while (target?.IsObservationWaiting == true && !_stopSequenceRequested && reacquireWait.Elapsed < TimeSpan.FromSeconds(13))
                {
                    item.ResultText = target.StatusText;
                    await Task.Delay(100);
                    target = _data.Targets.FirstOrDefault(x => x.Id == item.TargetId);
                }
                if (_stopSequenceRequested) { item.ResultText = "대기"; break; }
                var rdr = _data.RdrAssets.FirstOrDefault(x => x.Id == item.RdrId);
                if (target is null || !target.CanAssign || fcs is null || !fcs.IsConnected || !fcs.IsEnabled || !fcs.IsOperating || rdr is null || !rdr.IsConnected || !rdr.IsOperating)
                    throw new InvalidOperationException($"{TargetName(item.TargetId)} 또는 할당 자산의 연결/구동 상태를 확인하세요.");

                var status = await _pipeClient.SendAsync(CommandType.GetStatus, rdrId: item.RdrId, fcsId: item.FcsId);
                ApplyCommandResponse(status);
                if ((AssignmentState)status.AssignmentState != AssignmentState.Idle || status.AssignedTargetId != 0)
                    throw new InvalidOperationException("FCS에 진행 중인 수동 할당이 있습니다. 기존 할당을 취소한 뒤 다시 시작하세요.");
                if (_stopSequenceRequested)
                {
                    item.ResultText = "대기";
                    break;
                }

                var assigned = await SendPipeCommandAsync(CommandType.AssignTarget, item.RdrId, item.FcsId, item.TargetId, "순차 객체 할당");
                if (assigned is null || (ResultCode)assigned.Value.ResultCode != ResultCode.Success)
                    throw new InvalidOperationException(assigned is null ? "객체 할당 명령을 전송하지 못했습니다." : CommandResultText((ResultCode)assigned.Value.ResultCode));
                ApplyCommandResponse(assigned.Value);
                sequenceOwnsCurrentAssignment = true;
                item.ResultText = "조준 중";
                AddLog($"순차 할당 · {TargetName(item.TargetId)} · FCS 방향 조정 대기");

                PipeResponse current = assigned.Value;
                while ((AssignmentState)current.AssignmentState != AssignmentState.Assigned)
                {
                    if (_stopSequenceRequested) break;
                    await Task.Delay(250);
                    current = await _pipeClient.SendAsync(CommandType.GetStatus, rdrId: item.RdrId, fcsId: item.FcsId);
                    ApplyCommandResponse(current);
                    if ((AssignmentState)current.AssignmentState == AssignmentState.Failed || current.AssignedTargetId != item.TargetId)
                        throw new InvalidOperationException($"{TargetName(item.TargetId)} 할당이 중단되었습니다.");
                }
                if (_stopSequenceRequested)
                {
                    var canceled = await _pipeClient.SendAsync(CommandType.CancelAssignment, 0, item.RdrId, item.FcsId);
                    if ((ResultCode)canceled.ResultCode != ResultCode.Success) throw new InvalidOperationException($"{TargetName(item.TargetId)} 할당 취소 실패: {CommandResultText((ResultCode)canceled.ResultCode)}");
                    item.ResultText = "중지 · 할당 취소";
                    break;
                }

                var fired = await SendPipeCommandAsync(CommandType.Fire, item.RdrId, item.FcsId, item.TargetId, "순차 실행");
                if (fired is null || (ResultCode)fired.Value.ResultCode != ResultCode.Success)
                    throw new InvalidOperationException(fired is null ? "실행 명령을 전송하지 못했습니다." : CommandResultText((ResultCode)fired.Value.ResultCode));
                ApplyCommandResponse(fired.Value);
                item.ResultText = "실행 중";
                AddLog($"순차 실행 접수 · {TargetName(item.TargetId)}");

                var laserSeenOn = false;
                var laserWaitStarted = DateTime.UtcNow;
                do
                {
                    var laserOn = _data.FcsAssets.FirstOrDefault(x => x.Id == item.FcsId)?.LaserOn == true;
                    laserSeenOn |= laserOn;
                    if (laserSeenOn && !laserOn) break;
                    if (DateTime.UtcNow - laserWaitStarted > TimeSpan.FromSeconds(10)) throw new TimeoutException("레이저 종료 상태를 확인하지 못했습니다.");
                    await Task.Delay(200);
                } while (true);
                var canceledAfterFire = await _pipeClient.SendAsync(CommandType.CancelAssignment, 0, item.RdrId, item.FcsId);
                if ((ResultCode)canceledAfterFire.ResultCode != ResultCode.Success) throw new InvalidOperationException($"{TargetName(item.TargetId)} 실행 후 할당 취소 실패: {CommandResultText((ResultCode)canceledAfterFire.ResultCode)}");
                waitForFcs = true;
                item.ResultText = "완료 · 실행 후 자동 취소";
                RefreshSequenceQueue();
                if (!_stopSequenceRequested) AddLog($"순차 처리 완료 · {TargetName(item.TargetId)} · 자동 취소 완료");
            }
            if (_stopSequenceRequested) AddLog("객체 순차 할당 중지 · 미처리 객체는 대기 목록에 유지");
            else if (!_sequenceQueue.Any(item => item.FcsId == selectedQueueFcs.Id && item.ResultText == "대기")) AddLog($"{selectedQueueFcs.DisplayName} 객체 순차 할당 완료");
            else AddLog("객체 순차 할당 중지 · 미처리 객체는 대기 목록에 유지");
        }
        catch (Exception ex)
        {
            var active = currentSequenceItem;
            if (active is not null)
            {
                if (sequenceOwnsCurrentAssignment)
                {
                    try { await _pipeClient.SendAsync(CommandType.CancelAssignment, 0, active.RdrId, active.FcsId); } catch { }
                }
                active.ResultText = $"중단 · {ex.Message}";
            }
            AddLog($"순차 실행 중단 · {ex.Message}");
            ShowAlert(ex.Message, "순차 실행 중단");
        }
        finally
        {
            _sequenceRunning = false;
            _stopSequenceRequested = false;
            QueueFcsSelector.IsEnabled = true;
            SequenceStartButton.Content = "객체 순차 할당";
            SequenceStartButton.IsEnabled = true;
            RefreshSequenceQueue();
            UpdateQueueUploadControl();
        }
    }

    private async Task<bool> WaitForFcsIdleAsync(SequenceQueueItem item)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (!_stopSequenceRequested)
            {
                var response = await _pipeClient.SendAsync(CommandType.GetFcsBusy,
                    rdrId: item.RdrId, fcsId: item.FcsId, cancellationToken: timeout.Token);
                if (_stopSequenceRequested) return false;
                var result = (ResultCode)response.ResultCode;
                if (result == ResultCode.NotSupported)
                    throw new InvalidOperationException("ECS가 FCS 작업 상태 조회를 지원하지 않습니다. ECS를 먼저 업데이트하세요.");
                if (response.IsConnected == 0)
                    throw new InvalidOperationException("FCS 작업 종료 대기 중 연결이 끊겼습니다.");
                if (result == ResultCode.Success && response.FireState == 0) return true;
                if (result != ResultCode.Success && result != ResultCode.AssetUnavailable && result != ResultCode.StatusPending)
                    throw new InvalidOperationException($"FCS 작업 상태 조회 실패 · {CommandResultText(result)}");
                await Task.Delay(250, timeout.Token);
            }
            return false;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (_stopSequenceRequested) return false;
            throw new TimeoutException("FCS 작업 종료 대기 시간 초과");
        }
    }

    private async void TargetAssignClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureSharedMemoryLiveForCommand("객체 할당")) return;
        if ((sender as FrameworkElement)?.DataContext is not TargetRecord target) { AddLog("객체 할당 거부 · 객체 행을 확인할 수 없음"); return; }
        AddLog($"객체 할당 버튼 클릭 · {target.DisplayName}");
        if (_sequenceQueue.Any(item => item.TargetId == target.Id))
        {
            AddLog($"객체 할당 거부 · {target.DisplayName} 대기 목록 등록 중");
            ShowAlert($"{target.DisplayName}은(는) 객체할당 대기 목록에 등록되어 있습니다. 대기 목록에서 해제한 뒤 직접 할당하세요.", "대기 목록 등록 객체");
            return;
        }
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
        var occupiedAssignment = _data.Assignments.FirstOrDefault(x => x.FcsId == fcs.Id && x.TargetId != target.Id && x.IsActiveCommandState);
        if (occupiedAssignment is not null)
        {
            AddLog($"객체 할당 거부 · {fcs.DisplayName} 이미 {occupiedAssignment.TargetName}에 할당됨");
            ShowAlert($"{fcs.DisplayName}은(는) 이미 {occupiedAssignment.TargetName}에 할당된 FCS입니다.\n기존 할당을 취소한 뒤 다시 시도하세요.", "이미 할당된 FCS");
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
        assignment.SetConnecting();
        target.IsAssignmentPending = true;
        _pendingFcsAssignments.Add(fcs.Id);
        RefreshAssignments();
        AddLog($"{target.DisplayName} → {fcs.DisplayName} 명령 파이프 연결 시도 중");

        PipeResponse? response;
        try { response = await SendPipeCommandAsync(CommandType.AssignTarget, target.RdrId, fcs.Id, target.Id, "객체 할당", showConnectionAlert: false); }
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
            if (response.Value.AssignedTargetId != target.Id && _assignmentsByTarget.Remove(target.Id, out var rejectedRow))
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

    private async void CancelAssignmentClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureSharedMemoryLiveForCommand("객체 할당 취소")) return;
        if ((sender as FrameworkElement)?.DataContext is not AssignmentView assignment) { AddLog("객체 할당 취소 거부 · 할당 행을 확인할 수 없음"); return; }
        AddLog($"객체 할당 취소 버튼 클릭 · {assignment.TargetName}");
        var target = _data.Targets.FirstOrDefault(x => x.Id == assignment.TargetId);
        uint rdrId = target?.RdrId ?? 1;
        var response = await SendPipeCommandAsync(CommandType.CancelAssignment, rdrId, assignment.FcsId, assignment.TargetId, "객체 할당 취소");
        if (response is null) return;
        ApplyCommandResponse(response.Value);
        if ((ResultCode)response.Value.ResultCode != ResultCode.Success)
        {
            var message = CommandResultText((ResultCode)response.Value.ResultCode);
            AddLog($"객체 할당 취소 거부 · {assignment.TargetName} · {message}");
            ShowAlert(message, "객체 할당 취소 거부");
            return;
        }
        AddLog($"객체 할당 취소 명령 접수 · {assignment.TargetName}");
    }

    private async void FireAssignmentClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureSharedMemoryLiveForCommand("실행")) return;
        if ((sender as FrameworkElement)?.DataContext is not AssignmentView assignment) { AddLog("실행 거부 · 할당 행을 확인할 수 없음"); return; }
        AddLog($"실행 버튼 클릭 · {assignment.TargetName}");
        var targetName = assignment.TargetName;
        var fcs = _data.FcsAssets.FirstOrDefault(x => x.Id == assignment.FcsId);
        var unmetConditions = new List<string>();
        if (_data.Targets.FirstOrDefault(x => x.Id == assignment.TargetId)?.CanAssign != true) unmetConditions.Add("최신 재관측 확인이 필요합니다");
        if (!assignment.CanFire) unmetConditions.Add($"현재 할당 상태: {assignment.StateText} (할당 완료 필요)");
        if (fcs is null) unmetConditions.Add("FCS 데이터를 확인할 수 없습니다");
        if (unmetConditions.Count > 0)
        {
            AddLog($"실행 거부 · {targetName} · {string.Join(", ", unmetConditions)}");
            ShowAlert(string.Join(Environment.NewLine, unmetConditions), "실행 조건 미충족");
            return;
        }
        if (fcs is null) return;
        var target = _data.Targets.FirstOrDefault(x => x.Id == assignment.TargetId);
        var response = await SendPipeCommandAsync(CommandType.Fire, target?.RdrId ?? 1, assignment.FcsId, assignment.TargetId, "실행");
        if (response is null) return;
        ApplyCommandResponse(response.Value);
        if ((ResultCode)response.Value.ResultCode != ResultCode.Success)
        {
            var message = CommandResultText((ResultCode)response.Value.ResultCode);
            AddLog($"실행 거부 · {targetName} · {message}");
            ShowAlert(message, "실행 거부");
            return;
        }
        AddLog($"실행 명령 응답 · {targetName} → {fcs.DisplayName} · {(response.Value.FireState == 1 ? "실행 완료" : "실행 접수")}");
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
        if (response.AssignedTargetId == 0 && state == AssignmentState.Idle &&
            !(response.CommandType == (uint)CommandType.GetStatus && _pendingFcsAssignments.Contains(response.FcsId)))
        {
            foreach (var current in _data.Assignments.Where(current => current.FcsId == response.FcsId).ToArray())
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
            AddLog($"명령 서버 객체할당 상태 · {AssignmentStateText(state)} · {TargetName(response.AssignedTargetId)}");
            _previousAssignmentState = state;
        }
        RefreshAssignments();
    }

    private static string TargetName(uint targetId) => targetId == 0 ? "없음" : $"TGT-{targetId}";

    private static string AssignmentStateText(AssignmentState state) => state switch
    {
        AssignmentState.Idle => "대기",
        AssignmentState.Adjusting => "FCS 조준 중",
        AssignmentState.Assigned => "할당 완료",
        AssignmentState.Failed => "할당 실패",
        _ => "알 수 없음"
    };

    private static string CommandResultText(ResultCode result) => result switch
    {
        ResultCode.Success => "명령 접수",
        ResultCode.InvalidRequest => "요청 값이 올바르지 않습니다",
        ResultCode.AssetUnavailable => "FCS을 사용할 수 없습니다",
        ResultCode.NotAssigned => "FCS에 할당된 객체가 없습니다",
        ResultCode.AlreadyAssigned => "FCS에 이미 다른 객체가 할당되어 있습니다",
        ResultCode.NotSupported => "지원하지 않는 명령입니다",
        ResultCode.DeviceError => "장치 처리 오류입니다",
        ResultCode.StatusPending => "장치 상태 갱신을 기다리는 중입니다",
        ResultCode.DeviceFault => "장치가 준비되지 않았습니다",
        ResultCode.LidarUnsupported => "RDR 펌웨어가 라이다 방출 제어를 지원하지 않습니다. ED07/DE07 지원 버전으로 업데이트해 주세요",
        ResultCode.LidarUnknown => "RDR의 실제 라이다 방출 상태가 확인되지 않았습니다",
        ResultCode.LidarRecoveryRequired => "라이다 연동이 진행 중이거나 복구가 필요합니다. 진행 중인 작업을 확인하고 취소 후 다시 시도해 주세요",
        ResultCode.LidarStopUnconfirmed => "라이다 정지 및 실행 준비 확인에 실패했습니다. RDR·FCS 상태와 ECS 로그를 확인해 주세요",
        ResultCode.LidarDisconnected => "RDR 연결이 끊겨 라이다 정지를 확인할 수 없습니다",
        ResultCode.LidarResumeUnconfirmed => "RDR 구동 준비는 완료됐지만 라이다 방출 재개를 확인하지 못했습니다. ED07/DE07 응답을 확인해 주세요",
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
        foreach (var assignment in _data.Assignments)
        {
            var target = _data.Targets.FirstOrDefault(x => x.Id == assignment.TargetId);
            var fcs = _data.FcsAssets.FirstOrDefault(x => x.Id == assignment.FcsId);
            assignment.Refresh(target, fcs);
        }
        RefreshAssignmentEmptyState();
        UpdateQueueUploadControl();
    }

    private void RefreshAssignmentEmptyState() => AssignmentEmptyText.Visibility = _data.Assignments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void RefreshSequenceQueue()
    {
        foreach (var assetQueue in _sequenceQueue.GroupBy(item => item.FcsId))
        {
            var order = 1;
            foreach (var item in assetQueue) item.OrderText = (order++).ToString();
        }
        var queuedIds = _sequenceQueue.Select(item => item.TargetId).ToHashSet();
        foreach (var target in _data.Targets) target.IsQueued = queuedIds.Contains(target.Id);
        var selectedFcsId = (QueueFcsSelector.SelectedItem as FcsAsset)?.Id;
        SequenceQueueText.Text = $"{_sequenceQueue.Count(item => item.FcsId == selectedFcsId)}개";
        _sequenceQueueView.Refresh();
        UpdateQueueSelectAllControl();
        UpdateQueueUploadControl();
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

public sealed class SequenceQueueItem : INotifyPropertyChanged
{
    internal const string FcsBusyWait = "FCS 작업 종료 대기";
    private string _orderText = "", _resultText = "대기";
    private bool _isSelected;
    public uint TargetId { get; }
    public uint FcsId { get; }
    public uint RdrId { get; }
    public string TargetName => $"TGT-{TargetId}";
    public string FcsName => $"FCS-{FcsId:000}";
    public string StatusText => _resultText switch
    {
        "대기" or FcsBusyWait => "대기",
        "할당 요청" => "할당",
        "조준 중" => "조준",
        "실행 중" => "실행",
        "중지 · 할당 취소" => "중지",
        "완료 · 실행 후 자동 취소" => "완료",
        _ => "실패"
    };
    public string OrderText { get => _orderText; set { if (_orderText == value) return; _orderText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OrderText))); } }
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
    public string ResultText { get => _resultText; set { if (_resultText == value) return; _resultText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResultText))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public SequenceQueueItem(uint targetId, uint fcsId, uint rdrId) { TargetId = targetId; FcsId = fcsId; RdrId = rdrId; }
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
        AzimuthText = $"{target.AzimuthDeg:0.0}°";
        ElevationText = $"{target.ElevationDeg:0.0}°";
        DistanceText = $"{target.DistanceM:0.00}";
        LidarXText = $"{target.LidarX:0.00}";
        LidarYText = $"{target.LidarY:0.00}";
        LidarZText = $"{target.LidarZ:0.00}";
        RdrIdText = target.RdrId.ToString();
        TargetStatusText = target.StatusText;
        RefreshStateText(target, fcs);
    }

    private void RefreshStateText(TargetRecord? target, FcsAsset? fcs)
    {
        if (_connectionPending) { StateText = "연결 시도 중"; return; }
        if (_connectionFailed) { StateText = "연결 실패 · 재시도 가능"; return; }
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
    private bool _isSelected;
    private bool _isQueued;
    public uint Id { get; }
    private float _azimuthDeg, _elevationDeg;
    private float _distanceM, _lidarX, _lidarY, _lidarZ;
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
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public bool IsQueued { get => _isQueued; set => Set(ref _isQueued, value); }
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
    public string StatusText => Status switch { 0 => "ALIVE", 1 => "HIT", 2 => "MISSING", 4 => "관측 일시중지", 5 => "재관측 대기", _ => "UNKNOWN" };
    public string ValidText => Valid == 1 ? "YES" : "NO";
    public event PropertyChangedEventHandler? PropertyChanged;

    public TargetRecord(uint id, float azimuthDeg, float elevationDeg, float distanceM, float lidarX, float lidarY, float lidarZ, uint rdrId, byte status, bool valid, uint selectedFcsId, bool isAssigned)
    {
        Id = id; _selectedFcsId = selectedFcsId; _isAssigned = isAssigned;
        UpdateFrom(azimuthDeg, elevationDeg, distanceM, lidarX, lidarY, lidarZ, rdrId, status, valid ? (byte)1 : (byte)0);
    }
    public void UpdateFrom(TargetRecord other) => UpdateFrom(other.AzimuthDeg, other.ElevationDeg, other.DistanceM, other.LidarX, other.LidarY, other.LidarZ, other.RdrId, other.Status, other.Valid);
    private void UpdateFrom(float az, float el, float distance, float x, float y, float z, uint rdrId, byte status, byte valid)
    { AzimuthDeg = az; ElevationDeg = el; DistanceM = distance; LidarX = x; LidarY = y; LidarZ = z; RdrId = rdrId; Status = status; Valid = valid; }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name is nameof(Status) or nameof(Valid))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanAssign)));
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

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
    private readonly DispatcherTimer _commandStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _rdrElevationSendTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly PipeCommandClient _pipeClient = new();
    private readonly Dictionary<uint, uint> _targetFcsSelections = new();
    private readonly HashSet<uint> _assignedTargets = new();
    private readonly Dictionary<uint, AssignmentView> _assignmentsByTarget = new();
    private readonly HashSet<uint> _pendingFcsAssignments = new();
    private SharedMemoryFrameReader? _frameReader;
    private bool _isSharedMemoryLive;
    private bool _isCommandPipeLive;
    private bool _isRefreshingCommandStatus;
    private bool? _previousConnectionState;
    private AssignmentState? _previousAssignmentState;
    private uint? _selectedTargetId;
    private double _workspaceZoom = 1.0;
    private bool _workspaceLayoutRefreshQueued;
    private bool _updatingRdrElevationSlider;
    private bool _updatingRdrElevationInput;
    private bool _rdrElevationInputDirty;
    private int? _requestedRdrElevation;
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
            UpdateRdrElevationControl(RdrSelector.SelectedItem as RdrAsset);
            SharedMemoryStatus.Text = state;
            SharedMemoryStatus.Foreground = isLive
                ? new SolidColorBrush(Color.FromRgb(0, 230, 138))
                : new SolidColorBrush(Color.FromRgb(255, 176, 0));
            RefreshRdrInfo(RdrSelector.SelectedItem as RdrAsset);
            RefreshFcsInfo(FcsSelector.SelectedItem as FcsAsset);
            RefreshAssignments();
        });
        _frameReader.FrameReceived += frame => Dispatcher.BeginInvoke(() => ApplyFrame(frame));
        Loaded += async (_, _) => { UpdateClock(); DrawRadar(); _clock.Start(); _frameReader.Start(); _commandStatusTimer.Start(); QueueWorkspaceLayoutRefresh(); await RefreshCommandStatusAsync(); };
        Closed += (_, _) => { _clock.Stop(); _commandStatusTimer.Stop(); _rdrElevationSendTimer.Stop(); _frameReader.Stop(); _frameReader.Dispose(); _pipeClient.Dispose(); };
        _clock.Tick += (_, _) => UpdateClock();
        _commandStatusTimer.Tick += async (_, _) => await RefreshCommandStatusAsync();
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
            .Select(x => new RdrAsset(x.rdrId, x.isEnabled != 0, x.isOperating != 0, x.isConnected != 0, x.azimuthDeg, x.elevationDeg, x.positionX, x.positionY, x.fieldOfViewDeg)).ToArray();
        var fcs = payload.fcsAssets.Take((int)payload.fcsCount)
            .Select(x => new FcsAsset(x.fcsId, x.isEnabled != 0, x.isOperating != 0, x.isConnected != 0, x.laserOn != 0, x.azimuthDeg, x.elevationDeg, x.positionX, x.positionY)).ToArray();
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
        SyncRdrElevationFromTelemetry(RdrSelector.SelectedItem as RdrAsset);
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
        _requestedRdrElevation = null;
        _rdrElevationInputDirty = false;
        _rdrElevationSendTimer.Stop();
        var rdr = RdrSelector.SelectedItem as RdrAsset;
        UpdateRdrElevationControl(rdr);
        RefreshRdrInfo(rdr);
        SyncRdrElevationFromTelemetry(rdr);
    }

    private void RadarCanvasSizeChanged(object sender, SizeChangedEventArgs e) => DrawRadar();

    private void RdrElevationSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingRdrElevationSlider || RdrElevationSliderValue is null) return;
        var degrees = Math.Clamp((int)Math.Round(e.NewValue), 0, 90);
        RdrElevationSliderValue.Text = $"{degrees}°";
        if (!_isSharedMemoryLive || RdrSelector.SelectedItem is not RdrAsset) return;
        _rdrElevationInputDirty = false;
        SetRdrElevationInputText(degrees);
        QueueRdrElevationCommand(degrees);
    }

    private void RdrElevationInputPreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsDigit);

    private void RdrElevationInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updatingRdrElevationInput)
            _rdrElevationInputDirty = true;
    }

    private void RdrElevationInputApplyClick(object sender, RoutedEventArgs e)
    {
        AddLog("라이다 고각 입력 적용 버튼 클릭");
        if (!int.TryParse(RdrElevationInput.Text, out var degrees) || degrees is < 0 or > 90)
        {
            AddLog($"라이다 고각 입력 거부 · 입력값 '{RdrElevationInput.Text}' · 허용 범위 0~90°");
            ShowAlert("고각은 0부터 90 사이의 정수로 입력하세요.", "고각 입력 확인");
            RdrElevationInput.Text = ((int)Math.Round(RdrElevationSlider.Value)).ToString();
            return;
        }

        _rdrElevationInputDirty = false;
        SetRdrElevationSlider(degrees);
        QueueRdrElevationCommand(degrees);
    }

    private void QueueRdrElevationCommand(int degrees)
    {
        if (!_isSharedMemoryLive || RdrSelector.SelectedItem is not RdrAsset) return;
        _requestedRdrElevation = degrees;
        _rdrElevationSendTimer.Stop();
        _rdrElevationSendTimer.Start();
    }

    private void SyncRdrElevationFromTelemetry(RdrAsset? rdr)
    {
        if (rdr is null) return;
        if (_requestedRdrElevation == rdr.ElevationDeg)
        {
            _requestedRdrElevation = null;
        }
        if (_requestedRdrElevation is int requested)
        {
            SetRdrElevationSlider(requested);
            return;
        }
        SetRdrElevationSlider(Math.Clamp(rdr.ElevationDeg, 0, 90));
    }

    private void SetRdrElevationSlider(int degrees)
    {
        if (RdrElevationSlider is null) return;
        _updatingRdrElevationSlider = true;
        RdrElevationSlider.Value = Math.Clamp(degrees, 0, 90);
        var clampedDegrees = Math.Clamp(degrees, 0, 90);
        RdrElevationSliderValue.Text = $"{clampedDegrees}°";
        if (!_rdrElevationInputDirty) SetRdrElevationInputText(clampedDegrees);
        _updatingRdrElevationSlider = false;
    }

    private void SetRdrElevationInputText(int degrees)
    {
        _updatingRdrElevationInput = true;
        RdrElevationInput.Text = Math.Clamp(degrees, 0, 90).ToString();
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
        if (_requestedRdrElevation is not int degrees || RdrSelector.SelectedItem is not RdrAsset rdr || !_isSharedMemoryLive)
            return;
        try
        {
            var response = await _pipeClient.SendAsync(CommandType.SetRdrElevation, (uint)degrees, rdr.Id);
            if ((ResultCode)response.ResultCode != ResultCode.Success)
                throw new IOException(CommandResultText((ResultCode)response.ResultCode));

            AddLog($"라이다 고각 설정 요청 · {rdr.DisplayName} {degrees}°");
            if ((int)Math.Round(RdrElevationSlider.Value) != degrees)
                _rdrElevationSendTimer.Start();
        }
        catch (Exception ex)
        {
            if ((int)Math.Round(RdrElevationSlider.Value) == degrees)
            {
                _requestedRdrElevation = null;
                SyncRdrElevationFromTelemetry(rdr);
            }
            else
                _rdrElevationSendTimer.Start();
            AddLog($"라이다 고각 설정 실패 · {ex.Message}");
        }
    }

    private void RefreshRdrInfo(RdrAsset? rdr)
    {
        if (RdrIdText is null) return;
        if (!_isSharedMemoryLive || rdr is null)
        {
            UpdateRdrElevationControl(rdr);
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
        UpdateRdrElevationControl(rdr);
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
        var targets = _data.Targets.Where(x => x.Valid != 0 && x.RdrId == rdr.Id).ToArray();
        var minX = new[] { rdr.PositionX, fcs?.PositionX ?? rdr.PositionX }.Concat(targets.Select(x => x.LidarX)).Min();
        var maxX = new[] { rdr.PositionX, fcs?.PositionX ?? rdr.PositionX }.Concat(targets.Select(x => x.LidarX)).Max();
        var minY = new[] { rdr.PositionY, fcs?.PositionY ?? rdr.PositionY }.Concat(targets.Select(x => x.LidarY)).Min();
        var maxY = new[] { rdr.PositionY, fcs?.PositionY ?? rdr.PositionY }.Concat(targets.Select(x => x.LidarY)).Max();
        var span = Math.Max(Math.Max(maxX - minX, maxY - minY), 1.0);
        var padding = (float)Math.Max(span * 0.18, 0.45);
        minX -= padding; maxX += padding; minY -= padding; maxY += padding;
        var canvasWidth = RadarCanvas.ActualWidth > 20 ? RadarCanvas.ActualWidth : double.IsFinite(RadarCanvas.Width) ? RadarCanvas.Width : 260;
        var canvasHeight = RadarCanvas.ActualHeight > 20 ? RadarCanvas.ActualHeight : double.IsFinite(RadarCanvas.Height) ? RadarCanvas.Height : 280;
        const double inset = 18;
        var scale = Math.Min((canvasWidth - inset * 2) / (maxX - minX), (canvasHeight - inset * 2) / (maxY - minY));
        var originX = (canvasWidth - (maxX - minX) * scale) / 2;
        var originY = (canvasHeight - (maxY - minY) * scale) / 2;
        Point Map(double x, double y) => new(originX + (x - minX) * scale, canvasHeight - originY - (y - minY) * scale);

        var gridBrush = new SolidColorBrush(Color.FromRgb(22, 55, 46));
        var gridStep = span > 8 ? 2.0 : span > 3 ? 1.0 : 0.5;
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

        // SharedMemory 방위각 0~270도를 화면상의 -135~+135도로 변환
        var rdrPoint = Map(rdr.PositionX, rdr.PositionY);
        var rdrDisplayAzimuth = Math.Clamp(rdr.AzimuthDeg, 0, 270) - 135d;
        var fov = Math.Clamp(float.IsFinite(rdr.FieldOfViewDeg) ? rdr.FieldOfViewDeg : 0, 0, 360);
        var edgeClearance = Math.Min(Math.Min(rdrPoint.X - inset, canvasWidth - inset - rdrPoint.X),
            Math.Min(rdrPoint.Y - inset, canvasHeight - inset - rdrPoint.Y));
        var viewRadius = Math.Max(4, edgeClearance - 8);
        var rangeRing = new Ellipse
        {
            Width = viewRadius * 2,
            Height = viewRadius * 2,
            Stroke = new SolidColorBrush(Color.FromRgb(27, 88, 66)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 4 },
            IsHitTestVisible = false
        };
        Canvas.SetLeft(rangeRing, rdrPoint.X - viewRadius);
        Canvas.SetTop(rangeRing, rdrPoint.Y - viewRadius);
        RadarCanvas.Children.Add(rangeRing);
        if (fov > 0)
        {
            var sector = new Polygon { Fill = new SolidColorBrush(Color.FromArgb(45, 0, 230, 138)), Stroke = new SolidColorBrush(Color.FromArgb(120, 0, 230, 138)), StrokeThickness = 1 };
            sector.Points.Add(rdrPoint);
            var start = rdrDisplayAzimuth - fov / 2d;
            var steps = Math.Max(12, (int)(fov / 4));
            for (var i = 0; i <= steps; i++)
            {
                var angle = (start + fov * i / steps) * Math.PI / 180d;
                sector.Points.Add(new Point(rdrPoint.X + viewRadius * Math.Sin(angle), rdrPoint.Y - viewRadius * Math.Cos(angle)));
            }
            RadarCanvas.Children.Add(sector);
        }

        var azimuthRad = rdrDisplayAzimuth * Math.PI / 180d;
        RadarCanvas.Children.Add(new Line { X1 = rdrPoint.X, Y1 = rdrPoint.Y, X2 = rdrPoint.X + viewRadius * Math.Sin(azimuthRad), Y2 = rdrPoint.Y - viewRadius * Math.Cos(azimuthRad), Stroke = new SolidColorBrush(Color.FromRgb(0, 230, 138)), StrokeThickness = 2 });

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

        AddMapLabel("0°", rdrPoint.X - 8, rdrPoint.Y - viewRadius - 15, Color.FromRgb(82, 121, 109), 8);
        var leftLimit = -135 * Math.PI / 180d;
        var rightLimit = 135 * Math.PI / 180d;
        AddMapLabel("-135°", rdrPoint.X + viewRadius * Math.Sin(leftLimit) - 16, rdrPoint.Y - viewRadius * Math.Cos(leftLimit), Color.FromRgb(82, 121, 109), 8);
        AddMapLabel("+135°", rdrPoint.X + viewRadius * Math.Sin(rightLimit) - 10, rdrPoint.Y - viewRadius * Math.Cos(rightLimit), Color.FromRgb(82, 121, 109), 8);

        if (fcs is not null && fcsPoint is Point orientationPoint)
        {
            var fcsDisplayAzimuth = Math.Clamp(fcs.AzimuthDeg, 0, 270) - 135d;
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
            var color = isSelected ? Color.FromRgb(0, 162, 255) : target.Status == 1 ? Color.FromRgb(110, 120, 120) : target.Status == 2 ? Color.FromRgb(255, 176, 0) : Color.FromRgb(255, 65, 82);
            var marker = new Polygon
            {
                Fill = new SolidColorBrush(color),
                Stroke = isSelected ? Brushes.LightCyan : Brushes.White,
                StrokeThickness = isSelected ? 1.5 : 0.7,
                Cursor = Cursors.Hand,
                Tag = target.Id,
                ToolTip = target.DisplayName
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
    private float _positionX, _positionY, _fieldOfViewDeg;
    public bool IsEnabled { get => _isEnabled; private set => Set(ref _isEnabled, value); }
    public bool IsOperating { get => _isOperating; private set => Set(ref _isOperating, value); }
    public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }
    public int AzimuthDeg { get => _azimuthDeg; private set => Set(ref _azimuthDeg, value); }
    public int ElevationDeg { get => _elevationDeg; private set => Set(ref _elevationDeg, value); }
    public float PositionX { get => _positionX; private set => Set(ref _positionX, value); }
    public float PositionY { get => _positionY; private set => Set(ref _positionY, value); }
    public float FieldOfViewDeg { get => _fieldOfViewDeg; private set => Set(ref _fieldOfViewDeg, value); }
    public string DisplayName => $"RDR-{Id:000}";
    public event PropertyChangedEventHandler? PropertyChanged;
    public RdrAsset(uint id, bool isEnabled, bool isOperating, bool isConnected, int azimuthDeg, int elevationDeg, float positionX, float positionY, float fieldOfViewDeg)
    { Id = id; UpdateFrom(isEnabled, isOperating, isConnected, azimuthDeg, elevationDeg, positionX, positionY, fieldOfViewDeg); }
    public void UpdateFrom(RdrAsset other) => UpdateFrom(other.IsEnabled, other.IsOperating, other.IsConnected, other.AzimuthDeg, other.ElevationDeg, other.PositionX, other.PositionY, other.FieldOfViewDeg);
    private void UpdateFrom(bool enabled, bool operating, bool connected, int az, int el, float x, float y, float fov)
    { IsEnabled = enabled; IsOperating = operating; IsConnected = connected; AzimuthDeg = az; ElevationDeg = el; PositionX = x; PositionY = y; FieldOfViewDeg = fov; }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
}

public sealed class FcsAsset : INotifyPropertyChanged
{
    public uint Id { get; }
    private bool _isEnabled, _isOperating, _isConnected, _laserOn;
    private int _azimuthDeg, _elevationDeg;
    private float _positionX, _positionY;
    public bool IsEnabled { get => _isEnabled; private set => Set(ref _isEnabled, value); }
    public bool IsOperating { get => _isOperating; private set => Set(ref _isOperating, value); }
    public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }
    public bool LaserOn { get => _laserOn; private set => Set(ref _laserOn, value); }
    public int AzimuthDeg { get => _azimuthDeg; private set => Set(ref _azimuthDeg, value); }
    public int ElevationDeg { get => _elevationDeg; private set => Set(ref _elevationDeg, value); }
    public float PositionX { get => _positionX; private set => Set(ref _positionX, value); }
    public float PositionY { get => _positionY; private set => Set(ref _positionY, value); }
    public string DisplayName => $"FCS-{Id:000}";
    public event PropertyChangedEventHandler? PropertyChanged;
    public FcsAsset(uint id, bool isEnabled, bool isOperating, bool isConnected, bool laserOn, int azimuthDeg, int elevationDeg, float positionX, float positionY)
    { Id = id; UpdateFrom(isEnabled, isOperating, isConnected, laserOn, azimuthDeg, elevationDeg, positionX, positionY); }
    public void UpdateFrom(FcsAsset other) => UpdateFrom(other.IsEnabled, other.IsOperating, other.IsConnected, other.LaserOn, other.AzimuthDeg, other.ElevationDeg, other.PositionX, other.PositionY);
    private void UpdateFrom(bool enabled, bool operating, bool connected, bool laser, int az, int el, float x, float y)
    { IsEnabled = enabled; IsOperating = operating; IsConnected = connected; LaserOn = laser; AzimuthDeg = az; ElevationDeg = el; PositionX = x; PositionY = y; }
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

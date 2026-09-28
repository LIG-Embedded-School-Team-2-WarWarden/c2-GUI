using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AadcCommandCenter;

public partial class MainWindow : Window
{
    private bool _isAssigned;
    private double _scanAngle = -135;
    private double _scanDirection = 1;
    private double _targetPhase;
    private double? _aimAzimuth;
    private double? _aimElevation;
    private int _interceptTicks;
    private Ellipse? _activeTargetDot;
    private TextBlock? _activeTargetLabel;
    private readonly string _logPath = System.IO.Path.Combine(AppContext.BaseDirectory, "AADC-9-simulation.log");
    private readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromMilliseconds(35) };

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UpdateControlVisuals();
            AddLog("SYSTEM", "모의 관제 시스템 시작");
            _scanTimer.Tick += ScanTimerTick;
            _scanTimer.Start();
        };
        Closed += (_, _) => _scanTimer.Stop();
    }

    private void ScanTimerTick(object? sender, EventArgs e)
    {
        ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");
        _scanAngle += _scanDirection * 1.2;
        if (_scanAngle >= 135 || _scanAngle <= -135)
        {
            _scanAngle = Math.Clamp(_scanAngle, -135, 135);
            _scanDirection *= -1;
        }
        SweepTransform.Angle = _scanAngle;
        ScanAzimuthText.Text = $"SCAN {_scanAngle:+0.0;-0.0;0.0}°";
        DetectionAssetTransform.Angle = _scanAngle;
        DetectionMoveSlider.Value = _scanAngle + 135;
        DetectionMoveValue.Text = $"{_scanAngle:+0.0;-0.0;0.0}°";
        AnimateTargetAndAim();
    }

    private void AnimateTargetAndAim()
    {
        _targetPhase += 0.035;
        PlaceTarget(Target001Dot, Target001Label, 39, 1908, Math.Sin(_targetPhase) * 5, Math.Cos(_targetPhase * 1.4) * 35);
        PlaceTarget(Target002Dot, Target002Label, 119, 2943, 0, 0);
        PlaceTarget(Target003Dot, Target003Label, -49, 987, 0, 0);
        PlaceTarget(Target403Dot, Target403Label, 47, 2856, 0, 0);
        if (_aimAzimuth is double az && _aimElevation is double el)
        {
            AzSlider.Value += (az - AzSlider.Value) * 0.045;
            ElSlider.Value += (el - ElSlider.Value) * 0.045;
            if (Math.Abs(az - AzSlider.Value) < 0.05 && Math.Abs(el - ElSlider.Value) < 0.05)
            {
                AzSlider.Value = az; ElSlider.Value = el; _aimAzimuth = _aimElevation = null;
                Checks.Text = "■ 표적 할당                         OK\n■ 발사대 조준                       OK\n■ 레이저 준비                       OK";
                AddLog("READY", "표적 자동 조준 완료");
            }
        }
        if (_interceptTicks > 0 && --_interceptTicks == 0)
        {
            SimulationBeam.Opacity = 0;
            if (_activeTargetDot is not null) _activeTargetDot.Visibility = Visibility.Collapsed;
            if (_activeTargetLabel is not null) { _activeTargetLabel.Text += "  SIM KILL"; _activeTargetLabel.Foreground = new SolidColorBrush(Color.FromRgb(0, 233, 139)); }
            var status = SelectedTarget.Text switch
            {
                "TGT-002" => Target002Status,
                "TGT-003" => Target003Status,
                "TGT-403" => Target403Status,
                _ => Target001Status
            };
            status.Text = "SIM KILL";
            status.Foreground = new SolidColorBrush(Color.FromRgb(0, 233, 139));
            AddLog("SIM", $"{SelectedTarget.Text} 모의 격추 완료");
            _isAssigned = false; AssignmentStatus.Text = "— 다음 표적 대기 —"; AssignButton.Content = "표적 할당 →";
            Checks.Text = "■ 표적 할당                         READY\n■ 발사대 조준                       READY\n■ 레이저 준비                       OK";
            FireButton.Content = "●   발사 명령";
            _activeTargetDot = null; _activeTargetLabel = null;
        }
    }

    // 표의 상대 방위각(0°=상단)과 거리(최대 4 km)를 레이더 좌표로 변환한다.
    private static void PlaceTarget(Ellipse dot, TextBlock label, double azimuth, double range, double azJitter, double rangeJitter)
    {
        if (dot.Visibility != Visibility.Visible) return;
        var angle = (azimuth + azJitter) * Math.PI / 180;
        var radius = Math.Clamp((range + rangeJitter) / 4000d * 118d, 12, 118);
        var x = 154 + radius * Math.Sin(angle);
        var y = 154 - radius * Math.Cos(angle);
        Canvas.SetLeft(dot, x - dot.Width / 2); Canvas.SetTop(dot, y - dot.Height / 2);
        Canvas.SetLeft(label, x + 8); Canvas.SetTop(label, y - 12);
    }

    private (double X, double Y) GetTargetMapPosition(string id)
    {
        var (azimuth, range) = id switch
        {
            "TGT-002" => (119d, 2943d),
            "TGT-003" => (-49d, 987d),
            "TGT-403" => (47d, 2856d),
            _ => (39d + Math.Sin(_targetPhase) * 5, 1908d + Math.Cos(_targetPhase * 1.4) * 35)
        };
        var angle = azimuth * Math.PI / 180;
        var radius = Math.Clamp(range / 4000d * 118d, 12, 118);
        return (154 + radius * Math.Sin(angle), 154 - radius * Math.Cos(angle));
    }

    private void TargetClick(object sender, RoutedEventArgs e)
    {
        var id = (string)((Button)sender).Tag;
        var data = id switch
        {
            "TGT-002" => ("119°", "5°", "2943m", "67 m/s", "1.22 m²", "MED   DETECTED"),
            "TGT-003" => ("−49°", "32°", "987m", "187 m/s", "0.41 m²", "CRIT   DETECTED"),
            "TGT-403" => ("47°", "6°", "2856m", "51 m/s", "0.43 m²", "HIGH   DETECTED"),
            _ => ("39°", "15°", "1908m", "127 m/s", "0.84 m²", "HIGH   TRACKING")
        };
        SelectedTarget.Text = id; SelectedThreat.Text = "   " + data.Item6;
        AzimuthValue.Text = data.Item1; ElevationValue.Text = data.Item2; DistanceValue.Text = data.Item3; SpeedValue.Text = data.Item4; RcsValue.Text = data.Item5;
        _isAssigned = false; AssignmentStatus.Text = "— 없음 —"; AssignButton.Content = "표적 할당 →";
        Checks.Text = "■ 표적 할당                         NG\n■ 발사대 조준                       NG\n■ 레이저 준비                       OK";
    }

    private void AssignClick(object sender, RoutedEventArgs e)
    {
        _isAssigned = true; AssignmentStatus.Text = SelectedTarget.Text; AssignButton.Content = "할당 완료 ✓";
        // 감지 레이더의 표적 방위각이 아니라, 지도 하단 타격자산에서 표적까지의 실제 방향으로 조준한다.
        var targetPosition = GetTargetMapPosition(SelectedTarget.Text);
        var weaponHeading = Math.Atan2(targetPosition.X - 154, -(targetPosition.Y - 217)) * 180 / Math.PI;
        _aimAzimuth = Math.Clamp(weaponHeading, -135, 135) + 135;
        _aimElevation = SelectedTarget.Text == "TGT-003" ? 32 : SelectedTarget.Text == "TGT-002" ? 5 : SelectedTarget.Text == "TGT-403" ? 6 : 15;
        Checks.Text = "■ 표적 할당                         OK\n■ 발사대 조준                       ALIGNING\n■ 레이저 준비                       OK";
        AddLog("ASSIGN", $"{SelectedTarget.Text} 할당, 자동 조준 시작");
    }

    private void AngleChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateControlVisuals();

    private void UpdateControlVisuals()
    {
        if (AzText == null) return;
        var azimuth = AzSlider.Value - 135;
        var elevation = ElSlider.Value;
        var azimuthText = $"{azimuth:+0.00;-0.00;0.00}°";
        AzText.Text = azimuthText;
        ElText.Text = $"{elevation:0.00}°";
        AzValue.Text = AzGaugeValue.Text = azimuthText;
        ElValue.Text = ElGaugeValue.Text = $"{elevation:0.00}°";

        // 지도 자산은 방위각으로만 회전한다. 고각은 자세 값으로만 표시한다.
        StrikeAzimuthTransform.Angle = azimuth;
        StrikeAssetLabel.Text = $"타격자산  AZ {azimuthText} / EL {elevation:0.00}°";
        DrawGauge(AzGauge, AzSlider.Value, 270, Color.FromRgb(0, 233, 139), false);
        DrawGauge(ElGauge, elevation, 90, Color.FromRgb(255, 176, 0), true);
    }

    private static void DrawGauge(Canvas gauge, double value, double maximum, Color color, bool elevationGauge)
    {
        gauge.Children.Clear();
        var brush = new SolidColorBrush(color);
        var dim = new SolidColorBrush(Color.FromRgb(30, 73, 59));
        var centerX = elevationGauge ? 110d : 64d;
        const double centerY = 70;
        const double radius = 50;
        var startAngle = elevationGauge ? 180d : 210d;
        var sweepAngle = elevationGauge ? 90d : 120d;
        var angle = startAngle + (value / maximum * sweepAngle);
        var startRadians = startAngle * Math.PI / 180;
        var endRadians = (startAngle + sweepAngle) * Math.PI / 180;
        var start = new Point(centerX + radius * Math.Cos(startRadians), centerY + radius * Math.Sin(startRadians));
        var end = new Point(centerX + radius * Math.Cos(endRadians), centerY + radius * Math.Sin(endRadians));
        gauge.Children.Add(CreateArc(start, end, radius, dim, 6));
        var pointerRadians = angle * Math.PI / 180;
        var activeEnd = new Point(centerX + radius * Math.Cos(pointerRadians), centerY + radius * Math.Sin(pointerRadians));
        gauge.Children.Add(CreateArc(start, activeEnd, radius, brush, 4));
        var endX = centerX + 40 * Math.Cos(pointerRadians);
        var endY = centerY + 40 * Math.Sin(pointerRadians);
        gauge.Children.Add(new Line { X1 = centerX, Y1 = centerY, X2 = endX, Y2 = endY, Stroke = brush, StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        var hub = new Ellipse { Width = 8, Height = 8, Fill = brush };
        Canvas.SetLeft(hub, centerX - 4); Canvas.SetTop(hub, centerY - 4); gauge.Children.Add(hub);
        for (var i = 0; i <= (elevationGauge ? 3 : 6); i++)
        {
            var tick = (startAngle + i * (sweepAngle / (elevationGauge ? 3 : 6))) * Math.PI / 180;
            var outerX = centerX + 56 * Math.Cos(tick); var outerY = centerY + 56 * Math.Sin(tick);
            var innerX = centerX + 49 * Math.Cos(tick); var innerY = centerY + 49 * Math.Sin(tick);
            gauge.Children.Add(new Line { X1 = innerX, Y1 = innerY, X2 = outerX, Y2 = outerY, Stroke = dim, StrokeThickness = 1 });
        }
    }

    private static System.Windows.Shapes.Path CreateArc(Point start, Point end, double radius, Brush brush, double thickness) => new()
    {
        Stroke = brush,
        StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Data = new PathGeometry(new[] { new PathFigure(start, new[] { new ArcSegment(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true) }, false) })
    };

    private void FireClick(object sender, RoutedEventArgs e)
    {
        if (!_isAssigned) { MessageBox.Show("먼저 표적을 할당해 주세요.", "발사 통제", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (_aimAzimuth is not null) { AddLog("WAIT", "자동 조준 완료 후 모의 발사가 가능합니다."); return; }
        (_activeTargetDot, _activeTargetLabel) = SelectedTarget.Text switch
        {
            "TGT-002" => (Target002Dot, Target002Label),
            "TGT-003" => (Target003Dot, Target003Label),
            "TGT-403" => (Target403Dot, Target403Label),
            _ => (Target001Dot, Target001Label)
        };
        if (_activeTargetDot.Visibility != Visibility.Visible) { AddLog("INFO", "이미 모의 격추된 표적입니다. 다른 표적을 선택하세요."); return; }
        SimulationBeam.X2 = Canvas.GetLeft(_activeTargetDot) + _activeTargetDot.Width / 2;
        SimulationBeam.Y2 = Canvas.GetTop(_activeTargetDot) + _activeTargetDot.Height / 2;
        SimulationBeam.Opacity = 1; _interceptTicks = 50; FireButton.Content = "● 모의 교전 진행…";
        AddLog("FIRE", $"{SelectedTarget.Text} 모의 발사 명령 전송");
    }

    private void ToggleManualControl(object sender, RoutedEventArgs e)
    {
        var show = ManualControlPanel.Visibility != Visibility.Visible;
        ManualControlPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ManualControlButton.Content = show ? "수동 자세 조정 ▴" : "수동 자세 조정 ▾";
    }

    private void DetectionMoveChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DetectionAssetTransform == null) return;
        _scanAngle = e.NewValue - 135;
        DetectionAssetTransform.Angle = _scanAngle;
        DetectionMoveValue.Text = $"{_scanAngle:+0.0;-0.0;0.0}°";
    }

    private void AddLog(string level, string message)
    {
        var entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{level}]  {message}";
        EventLogList?.Items.Insert(0, entry);
        try { File.AppendAllText(_logPath, entry + Environment.NewLine); } catch { }
    }

    private void OpenLogClick(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_logPath)) File.WriteAllText(_logPath, "AADC-9 simulation log" + Environment.NewLine);
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_logPath}\"") { UseShellExecute = true });
    }
}

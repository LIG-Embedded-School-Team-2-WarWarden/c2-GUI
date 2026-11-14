using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AadcCommandCenter;

public partial class MainWindow
{
    private float[][]? _cameraSettings;
    private bool _cameraReadPending;
    private EquipmentSettingsWindow? _equipmentWindow;

    private async Task<float[]> ReadCameraGroupAsync(uint group)
    {
        var r = await _pipeClient.SendAsync(CommandType.GetCameraSettings, group, 0, 0);
        if (r.ResultCode != 0) throw new IOException("ECS 카메라 설정을 읽을 수 없습니다.");
        var values = new[] { BitConverter.UInt32BitsToSingle(r.FcsId), BitConverter.UInt32BitsToSingle(r.AssignedTargetId), BitConverter.UInt32BitsToSingle(r.AssignmentState) };
        if (values.Any(v => !float.IsFinite(v))) throw new IOException("잘못된 ECS 설정 응답입니다.");
        return values;
    }

    private async Task RefreshCameraSettingsAsync()
    {
        if (_cameraReadPending) return;
        _cameraReadPending = true;
        try
        {
            var values = new[] { await ReadCameraGroupAsync(0), await ReadCameraGroupAsync(1), await ReadCameraGroupAsync(2) };
            _cameraSettings = values;
            _syncingPositionInputs = true;
            if (!_rdrPositionDirty) SetCoordinates(new[] { RdrXInput, RdrYInput, RdrZInput }, values[0]);
            if (!_fcsPositionDirty) SetCoordinates(new[] { FcsXInput, FcsYInput, FcsZInput }, values[1]);
            var rdr = RdrSelector.SelectedItem as RdrAsset;
            var fcs = FcsSelector.SelectedItem as FcsAsset;
            if (rdr != null && fcs != null)
                InstallationPositionsText.Text = $"실제 설치 위치 (m)\nRDR: {rdr.PositionX:G6}, {rdr.PositionY:G6}, {rdr.PositionZ:G6}\nFCS: {fcs.PositionX:G6}, {fcs.PositionY:G6}, {fcs.PositionZ:G6}";
        }
        catch { /* Keep the last confirmed values on disconnect or an older ECS. */ }
        finally { _syncingPositionInputs = false; _cameraReadPending = false; }
    }

    private static void SetCoordinates(TextBox[] inputs, float[] values)
    {
        for (int i = 0; i < inputs.Length; i++) inputs[i].Text = values[i].ToString("G9", CultureInfo.CurrentCulture);
    }

    private async void EquipmentSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_equipmentWindow != null) { _equipmentWindow.Activate(); return; }
        try
        {
            var ranges = await ReadCameraGroupAsync(2);
            if (!_isSharedMemoryLive || RdrSelector.SelectedItem is not RdrAsset rdr || FcsSelector.SelectedItem is not FcsAsset fcs)
                throw new IOException("ECS 연결과 설치 위치 정보가 필요합니다.");
            _equipmentWindow = new EquipmentSettingsWindow(
                new[] { rdr.PositionX, rdr.PositionY, rdr.PositionZ },
                new[] { fcs.PositionX, fcs.PositionY, fcs.PositionZ }, ranges,
                async (group, values) =>
                {
                    var response = group < 2
                        ? await _pipeClient.SendAsync(CommandType.SetAssetPosition, rdrId: group == 0 ? rdr.Id : 0, fcsId: group == 1 ? fcs.Id : 0, positionX: values[0], positionY: values[1], positionZ: values[2])
                        : await _pipeClient.SendAsync(CommandType.SetCameraSettings, 2, 0, 0, positionX: values[0], positionY: values[1], positionZ: values[2]);
                    if (response.ResultCode != 0) throw new IOException(CommandResultText((ResultCode)response.ResultCode));
                }) { Owner = this };
            _equipmentWindow.Closed += (_, _) => _equipmentWindow = null;
            _equipmentWindow.Show();
        }
        catch (Exception error) { ShowAlert(error.Message, "장비 설정 열기 실패"); }
    }
}

// Standalone display: no targets, command client, assignment or output controls.
internal sealed class RangePreview : FrameworkElement
{
    public float[] Rdr { get; set; } = new float[3];
    public float[] Fcs { get; set; } = new float[3];
    public float[] Ranges { get; set; } = new[] { 1.5f, 10f, 3f };
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 1 || h < 1) return;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(10, 20, 19)), null, new Rect(0, 0, w, h));
        // Scale in double precision, including separated installations and zero ranges.
        double extent = Math.Max(1, Math.Max(Ranges[1], Ranges[2]));
        double minX = Math.Min(Rdr[0], Fcs[0]) - extent, maxX = Math.Max(Rdr[0], Fcs[0]) + extent;
        double minY = Math.Min(Rdr[1], Fcs[1]) - extent, maxY = Math.Max(Rdr[1], Fcs[1]) + extent;
        double scale = Math.Min((w - 70) / (maxX - minX), (h - 70) / (maxY - minY));
        Point Map(float[] p) => new(w / 2 + (p[0] - (minX + maxX) / 2) * scale, h / 2 - (p[1] - (minY + maxY) / 2) * scale);
        var rdr = Map(Rdr); var fcs = Map(Fcs);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        var outside = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, w, h)), new EllipseGeometry(fcs, Ranges[2] * scale, Ranges[2] * scale));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(65, 255, 220, 0)), null, outside);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(115, 255, 45, 45)), null, rdr, Ranges[0] * scale, Ranges[0] * scale);
        dc.DrawEllipse(null, new Pen(Brushes.Cyan, 1.5), rdr, Ranges[1] * scale, Ranges[1] * scale);
        dc.DrawEllipse(null, new Pen(Brushes.Gold, 1), fcs, Ranges[2] * scale, Ranges[2] * scale);
        void Label(string text, Point at, Brush brush) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), 12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), at);
        dc.DrawEllipse(Brushes.Cyan, null, rdr, 3, 3); dc.DrawEllipse(Brushes.White, null, fcs, 3, 3);
        Label("RDR", new Point(rdr.X + 5, rdr.Y + 4), Brushes.Cyan);
        Label("FCS", new Point(fcs.X + 5, fcs.Y - 20), Brushes.White);
        Label($"RDR 최대 {Ranges[1]:G6} m", new Point(12, 10), Brushes.Cyan);
        Label($"FCS 최대 {Ranges[2]:G6} m · XY 평면", new Point(12, h - 25), Brushes.Gold);
        dc.Pop();
    }
}

internal sealed class EquipmentSettingsWindow : Window
{
    internal EquipmentSettingsWindow(float[] rdr, float[] fcs, float[] ranges, Func<int, float[], Task> save)
    {
        Title = "장비 설정 · 독립 범위 미리보기"; Width = 720; Height = 790;
        MinWidth = 680; MinHeight = 590; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AadcCommandCenter;component/Theme.xaml", UriKind.Relative) });
        Background = new SolidColorBrush(Color.FromRgb(7, 16, 15));
        Foreground = new SolidColorBrush(Color.FromRgb(195, 214, 206));
        FontFamily = new FontFamily("Consolas");
        var root = new StackPanel { Margin = new Thickness(20), Background = Background };
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(new TextBlock { Text = "장비 설정", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = (Brush)Resources["Green"], Margin = new Thickness(0, 0, 0, 12) });
        root.Children.Add(new TextBlock { Text = "각 행은 별도로 저장됩니다. 모든 입력 단위: m", Margin = new Thickness(0, 0, 0, 10) });
        var preview = new RangePreview { Height = 300, Rdr = rdr, Fcs = fcs, Ranges = ranges, Margin = new Thickness(0, 14, 0, 0) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        var buttons = new List<Button>();
        void Row(string title, string[] labels, float[] initial, int group)
        {
            root.Children.Add(new TextBlock { Text = title, Foreground = (Brush)Resources["Green"], FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 3) });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var inputs = new TextBox[3];
            for (int i = 0; i < 3; i++)
            {
                var cell = new StackPanel { Width = 170, Margin = new Thickness(0, 0, 8, 0) };
                cell.Children.Add(new TextBlock { Text = labels[i] + " (m)", FontSize = 11 });
                inputs[i] = new TextBox { Text = initial[i].ToString("G9", CultureInfo.CurrentCulture), Padding = new Thickness(4), Foreground = Brushes.Black, Background = Brushes.White };
                cell.Children.Add(inputs[i]); row.Children.Add(cell);
            }
            var button = new Button { Content = "저장", Width = 65, Margin = new Thickness(0, 14, 0, 0) };
            buttons.Add(button); row.Children.Add(button); root.Children.Add(row);
            button.Click += async (_, _) =>
            {
                var values = new float[3];
                for (int i = 0; i < 3; i++)
                    if (!float.TryParse(inputs[i].Text, NumberStyles.Float, CultureInfo.CurrentCulture, out values[i]) || !float.IsFinite(values[i]))
                    { status.Text = "저장 실패: 유한한 숫자를 입력하세요. 기존 저장값을 유지합니다."; return; }
                if (group == 2 && (values.Any(v => v < 0) || values[1] < values[0]))
                { status.Text = "저장 실패: 거리는 0 이상, RDR 최대 거리는 최소 거리 이상이어야 합니다."; return; }
                foreach (var b in buttons) b.IsEnabled = false;
                status.Text = "ECS에 저장 중…";
                try
                {
                    await save(group, values);
                    if (group == 0) preview.Rdr = values;
                    else if (group == 1) preview.Fcs = values;
                    else preview.Ranges = values;
                    preview.InvalidateVisual(); status.Text = title + " 저장 완료";
                }
                catch (Exception error) { status.Text = "저장 실패 · 기존 저장값 유지: " + error.Message; }
                finally { foreach (var b in buttons) b.IsEnabled = true; }
            };
        }
        Row("RDR 실제 설치 위치", new[] { "X", "Y", "Z" }, rdr, 0);
        Row("FCS 실제 설치 위치", new[] { "X", "Y", "Z" }, fcs, 1);
        Row("인식·촬영 거리", new[] { "RDR 최소 인식 거리", "RDR 최대 인식 거리", "FCS 최대 촬영 거리" }, ranges, 2);
        root.Children.Add(status); root.Children.Add(preview);
        root.Children.Add(new TextBlock { Text = "노란색: 촬영 불가 범위", Foreground = Brushes.Gold, Margin = new Thickness(0, 5, 0, 0) });
        root.Children.Add(new TextBlock { Text = "빨간색: 라이다 음영지역", Foreground = Brushes.Salmon });
        root.Children.Add(new TextBlock { Text = "저장된 설정의 독립 미리보기 · 장비 명령/대상 할당/출력 제어와 연결되지 않음", FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
    }
}

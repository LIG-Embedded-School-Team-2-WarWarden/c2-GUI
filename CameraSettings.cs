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
    private GeometryCalibration _geometryCalibration = GeometryCalibration.Identity;
    private readonly string _fcsAngleOffsetPath =
        Environment.GetEnvironmentVariable("AADC_FCS_ANGLE_OFFSET_PATH") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AadcCommandCenter", "fcs-angle-offset.json");
    private float[]? _fcsAngleOffsets;
    private bool _cameraReadPending;
    private bool _fcsAngleDirty;
    private EquipmentSettingsWindow? _equipmentWindow;

    private float[] GetFcsAngleOffsets() =>
        (_fcsAngleOffsets ??= FcsAngleOffsetStorage.Load(_fcsAngleOffsetPath)).ToArray();

    private float[] CurrentFcsAngleOffsets() =>
        _cameraSettings is { Length: > 2 } ? _cameraSettings[2] : GetFcsAngleOffsets();

    private async Task<float[]> ReadCameraGroupAsync(uint group)
    {
        var r = await _pipeClient.SendAsync(CommandType.GetCameraSettings, group, 0, 0);
        if (r.ResultCode != 0) throw new IOException("ECS FCS 설정을 읽을 수 없습니다.");
        var values = new[] { BitConverter.UInt32BitsToSingle(r.FcsId), BitConverter.UInt32BitsToSingle(r.AssignedTargetId), BitConverter.UInt32BitsToSingle(r.AssignmentState) };
        if (values.Any(v => !float.IsFinite(v))) throw new IOException("잘못된 ECS 설정 응답입니다.");
        return values;
    }

    private async Task<GeometryCalibration> ReadGeometryCalibrationAsync()
    {
        var token = (uint)Random.Shared.Next(1, int.MaxValue);
        async Task<PipeResponse> Read(CommandType command)
        {
            var response = await _pipeClient.SendAsync(command, token);
            if ((ResultCode)response.ResultCode != ResultCode.Success)
                throw new IOException("ECS 3D 보정값을 읽을 수 없습니다.");
            return response;
        }
        static double[] Values(PipeResponse response)
        {
            var values = new[] {
                (double)BitConverter.UInt32BitsToSingle(response.FcsId),
                BitConverter.UInt32BitsToSingle(response.AssignedTargetId),
                BitConverter.UInt32BitsToSingle(response.AssignmentState)
            };
            if (values.Any(value => !double.IsFinite(value)))
                throw new IOException("ECS가 잘못된 3D 보정값을 반환했습니다.");
            return values;
        }

        var translation = await Read(CommandType.ReadGeometryTranslation);
        var rotation = await Read(CommandType.ReadGeometryRotation);
        var rdrAnchor = await Read(CommandType.ReadGeometryRdrAnchor);
        var fcsAnchor = await Read(CommandType.ReadGeometryFcsAnchor);
        return new GeometryCalibration(translation.FireState == 1, Values(translation), Values(rotation),
            Values(rdrAnchor), Values(fcsAnchor));
    }

    private async Task RefreshCameraSettingsAsync()
    {
        if (_cameraReadPending) return;
        _cameraReadPending = true;
        try
        {
            var fcsAngleOffsets = GetFcsAngleOffsets();
            var photoSettings = _cameraSettings is { Length: > 3 }
                ? _cameraSettings[3] : new[] { .05f, 1f, 0f };
            try { photoSettings = await ReadCameraGroupAsync(5); }
            catch (IOException) { /* Older ECS keeps the defaults and existing camera settings. */ }
            var values = new[] { await ReadCameraGroupAsync(2), await ReadCameraGroupAsync(3),
                fcsAngleOffsets, photoSettings };
            _cameraSettings = values;
            try { _geometryCalibration = await ReadGeometryCalibrationAsync(); }
            catch (IOException) { /* 3D 보정을 지원하지 않는 기존 ECS에서는 기존 계산식을 사용합니다. */ }
            DrawRadar();
            _syncingPositionInputs = true;
            if (!_fcsAngleDirty) SetCoordinates(new[] { FcsPanOffsetInput, FcsTiltOffsetInput }, values[2]);
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

    private void FcsAngleTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncingPositionInputs) {
            _fcsAngleDirty = true;
            if (FcsAngleStatus != null) FcsAngleStatus.Text = "변경값을 저장하세요.";
        }
    }

    private void FcsAngleSaveClick(object sender, RoutedEventArgs e)
    {
        if (!TryCoordinate(FcsPanOffsetInput.Text, out var pan) ||
            !TryCoordinate(FcsTiltOffsetInput.Text, out var tilt) ||
            !FcsAngleOffsetStorage.IsValid(pan, tilt)) {
            FcsAngleStatus.Text = "팬 −180~180°, 틸트 −90~90°의 숫자를 입력하세요.";
            return;
        }
        FcsAngleSaveButton.IsEnabled = FcsPanOffsetInput.IsEnabled = FcsTiltOffsetInput.IsEnabled = false;
        FcsAngleStatus.Text = "저장 중…";
        try {
            FcsAngleOffsetStorage.Save(_fcsAngleOffsetPath, pan, tilt);
            _fcsAngleOffsets = [pan, tilt, 0f];
            if (_cameraSettings is { Length: > 2 }) _cameraSettings[2] = _fcsAngleOffsets.ToArray();
            _fcsAngleDirty = false;
            DrawRadar();
            FcsAngleStatus.Text = "저장 완료 · 다음 조준부터 적용됩니다.";
            AddLog($"FCS 각도 보정 GUI 저장 · 팬={pan:0.###}°, 틸트={tilt:0.###}°");
        }
        catch (Exception error) { FcsAngleStatus.Text = "저장 실패: " + error.Message; }
        finally { FcsAngleSaveButton.IsEnabled = FcsPanOffsetInput.IsEnabled = FcsTiltOffsetInput.IsEnabled = true; }
    }

    private async void EquipmentSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_equipmentWindow != null) { _equipmentWindow.Activate(); return; }
        try
        {
            var ranges = await ReadCameraGroupAsync(2);
            var fov = await ReadCameraGroupAsync(3);
            var continuousPhotos = _cameraSettings is { Length: > 3 }
                ? _cameraSettings[3] : new[] { .05f, 1f, 0f };
            try { continuousPhotos = await ReadCameraGroupAsync(5); }
            catch (IOException) { /* The settings window remains usable with an older ECS. */ }
            if (!_isSharedMemoryLive || RdrSelector.SelectedItem is not RdrAsset rdr || FcsSelector.SelectedItem is not FcsAsset fcs)
                throw new IOException("ECS 연결과 설치 위치 정보가 필요합니다.");
            _equipmentWindow = new EquipmentSettingsWindow(this,
                new[] { rdr.PositionX, rdr.PositionY, rdr.PositionZ },
                new[] { fcs.PositionX, fcs.PositionY, fcs.PositionZ }, ranges, fov, continuousPhotos,
                async (group, values) =>
                {
                    var response = group < 2
                        ? await _pipeClient.SendAsync(CommandType.SetAssetPosition, rdrId: group == 0 ? rdr.Id : 0, fcsId: group == 1 ? fcs.Id : 0, positionX: values[0], positionY: values[1], positionZ: values[2])
                        : await _pipeClient.SendAsync(CommandType.SetCameraSettings, (uint)group, 0, 0, positionX: values[0], positionY: values[1], positionZ: values[2]);
                    if (response.ResultCode != 0) throw new IOException(CommandResultText((ResultCode)response.ResultCode));
                    await RefreshCameraSettingsAsync();
                }, _workspaceZoom) { Heading = Math.Clamp(rdr.AzimuthDeg, 0, 270) - 135d };
            _equipmentWindow.Closed += (_, _) => _equipmentWindow = null;
            _equipmentWindow.Show();
        }
        catch (Exception error) { ShowAlert(error.Message, "장비 설정 열기 실패"); }
    }
}

// Standalone display: no targets, command client, assignment or output controls.
internal sealed class RangePreview : FrameworkElement
{
    public double Heading { get; set; }
    public float[] Rdr { get; set; } = new float[3];
    public float[] Fcs { get; set; } = new float[3];
    public float[] Fov { get; set; } = new[] { 120f, 45f, 0f };
    public float[] Ranges { get; set; } = new[] { 1.5f, 10f, 3f };
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 1 || h < 1) return;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(10, 20, 19)), null, new Rect(0, 0, w, h));
        // Scale in double precision, including separated installations and zero ranges.
        var rdrPosition = Rdr.Select(p => (double)p).ToArray();
        var fcsPosition = Fcs.Select(p => (double)p).ToArray();
        double extent = Math.Max(1, Math.Max(Ranges[1], Ranges[2]));
        double minX = Math.Min(rdrPosition[0], fcsPosition[0]) - extent, maxX = Math.Max(rdrPosition[0], fcsPosition[0]) + extent;
        double minY = Math.Min(rdrPosition[1], fcsPosition[1]) - extent, maxY = Math.Max(rdrPosition[1], fcsPosition[1]) + extent;
        double scale = Math.Min((w - 70) / (maxX - minX), (h - 70) / (maxY - minY));
        Point Map(double[] p) => new(w / 2 + (p[0] - (minX + maxX) / 2) * scale, h / 2 - (p[1] - (minY + maxY) / 2) * scale);
        var rdr = Map(rdrPosition); var fcs = Map(fcsPosition);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        CameraMapOverlay.Draw(dc, new Rect(0, 0, w, h), rdr, fcs,
            Ranges[0] * scale, Ranges[1] * scale, Ranges[2] * scale, Heading, Fov[0]);
        void Label(string text, Point at, Brush brush) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), 12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), at);
        dc.DrawEllipse(Brushes.Cyan, null, rdr, 3, 3); dc.DrawEllipse(Brushes.White, null, fcs, 3, 3);
        Label("RDR", new Point(rdr.X + 5, rdr.Y + 4), Brushes.Cyan);
        Label("FCS", new Point(fcs.X + 5, fcs.Y - 20), Brushes.White);
        Label($"RDR 최대 {Ranges[1]:G6} m", new Point(12, 10), Brushes.Cyan);
        Label($"FCS shoot ≤ {Ranges[2]:G6} m · 수평 {Fov[0]:G4}° / 수직 {Fov[1]:G4}°", new Point(12, h - 25), Brushes.Gold);
        dc.Pop();
    }
}

internal sealed class EquipmentSettingsWindow : Window
{
    private RangePreview? _preview;
    public double Heading { set { if (_preview != null) { _preview.Heading = value; _preview.InvalidateVisual(); } } }

    internal EquipmentSettingsWindow(Window owner, float[] rdr, float[] fcs, float[] ranges,
        float[] fov, float[] continuousPhotos, Func<int, float[], Task> save, double displayScale)
    {
        Title = "장비 설정";
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(1, Math.Min(workArea.Width, owner.ActualWidth) - 32);
        var availableHeight = Math.Max(1, Math.Min(workArea.Height, owner.ActualHeight) - 64);
        var scale = Math.Clamp(displayScale, 0.5, 1.5);
        MaxWidth = availableWidth;
        MaxHeight = availableHeight;
        Width = Math.Min(720 * scale, availableWidth);
        Height = Math.Min(790 * scale, availableHeight);
        MinWidth = Math.Min(360, Width);
        MinHeight = Math.Min(320, Height);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AadcCommandCenter;component/Theme.xaml", UriKind.Relative) });
        Background = new SolidColorBrush(Color.FromRgb(7, 16, 15));
        Foreground = new SolidColorBrush(Color.FromRgb(195, 214, 206));
        FontFamily = new FontFamily("Consolas");
        var root = new StackPanel { Margin = new Thickness(20), Background = Background, LayoutTransform = new ScaleTransform(scale, scale) };
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(new TextBlock { Text = "장비 설정", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = (Brush)Resources["Green"], Margin = new Thickness(0, 0, 0, 12) });
        root.Children.Add(new TextBlock { Text = "각 행은 별도로 저장됩니다. 위치·거리: m / 시야각: °", Margin = new Thickness(0, 0, 0, 10) });
        var preview = new RangePreview { Height = 300, Rdr = rdr, Fcs = fcs, Ranges = ranges, Fov = fov, Margin = new Thickness(0, 14, 0, 0) };
        _preview = preview;
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        var buttons = new List<Button>();
        void Row(string title, string[] labels, float[] initial, int group)
        {
            root.Children.Add(new TextBlock { Text = title, Foreground = (Brush)Resources["Green"], FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 3) });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var inputs = new TextBox[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                var cell = new StackPanel { Width = 170, Margin = new Thickness(0, 0, 8, 0) };
                string unit = group == 5 ? i == 0 ? " (cm)" : "" : group >= 3 ? " (°)" : " (m)";
                cell.Children.Add(new TextBlock { Text = labels[i] + unit, FontSize = 11 });
                float displayed = group == 5 && i == 0 ? initial[i] * 100 : initial[i];
                inputs[i] = new TextBox { Text = displayed.ToString("G9", CultureInfo.CurrentCulture), Padding = new Thickness(4), Foreground = Brushes.Black, Background = Brushes.White };
                cell.Children.Add(inputs[i]); row.Children.Add(cell);
            }
            var button = new Button { Content = "저장", Width = 65, Margin = new Thickness(0, 14, 0, 0) };
            buttons.Add(button); row.Children.Add(button); root.Children.Add(row);
            button.Click += async (_, _) =>
            {
                var values = new float[3];
                for (int i = 0; i < labels.Length; i++)
                    if (!float.TryParse(inputs[i].Text, NumberStyles.Float, CultureInfo.CurrentCulture, out values[i]) || !float.IsFinite(values[i]))
                    { status.Text = "저장 실패: 유한한 숫자를 입력하세요. 기존 저장값을 유지합니다."; return; }
                if (group == 2 && (values.Any(v => v < 0) || values[1] < values[0]))
                { status.Text = "저장 실패: 거리는 0 이상, RDR 최대 거리는 최소 거리 이상이어야 합니다."; return; }
                if (group == 3 && (values[0] <= 0 || values[0] > 360 || values[1] <= 0 || values[1] > 180))
                { status.Text = "저장 실패: 수평은 0° 초과 360° 이하, 수직은 0° 초과 180° 이하입니다."; return; }
                if (group == 5 && (values[0] <= 0 || values[0] > 100 ||
                    values[1] < 1 || values[1] > 20 || values[1] != MathF.Truncate(values[1])))
                { status.Text = "저장 실패: 축별 최대 보정 범위는 0cm 초과 100cm 이하, 단계 수는 1~20의 정수입니다."; return; }
                float[] stored = group == 5 ? [values[0] / 100, values[1], 0] : values;
                foreach (var b in buttons) b.IsEnabled = false;
                status.Text = "ECS에 저장 중…";
                try
                {
                    await save(group, stored);
                    if (group == 0) preview.Rdr = stored;
                    else if (group == 1) preview.Fcs = stored;
                    else if (group == 2) preview.Ranges = stored;
                    else if (group == 3) preview.Fov = stored;
                    preview.InvalidateVisual(); status.Text = title + " 저장 완료";
                }
                catch (Exception error) { status.Text = "저장 실패 · 기존 저장값 유지: " + error.Message; }
                finally { foreach (var b in buttons) b.IsEnabled = true; }
            };
        }
        Row("RDR 실제 설치 위치", new[] { "X", "Y", "Z" }, rdr, 0);
        Row("FCS 실제 설치 위치", new[] { "X", "Y", "Z" }, fcs, 1);
        Row("인식·shoot 거리", new[] { "RDR 최소 인식 거리", "RDR 최대 인식 거리", "FCS 최대 shoot 거리" }, ranges, 2);
        Row("RDR 시야각", new[] { "수평", "수직" }, fov, 3);
        Row("연속 촬영 보정", new[] { "축별 최대 보정 범위", "단계 수" }, continuousPhotos, 5);
        root.Children.Add(status);
        root.Children.Add(new Expander { Header = "설치 거리·시야각 도면", Foreground = Foreground, Content = preview });
        root.Children.Add(new TextBlock { Text = "지도와 조준 계산은 실제 설치 위치를 기준으로 합니다.", FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
    }
}

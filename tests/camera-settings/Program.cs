using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;

class Program
{
    [STAThread]
    static void Main()
    {
        Directory.CreateDirectory("artifacts/camera-settings");
        var type = typeof(AadcCommandCenter.MainWindow).Assembly.GetType("AadcCommandCenter.EquipmentSettingsWindow")!;
        var resultType = type.Assembly.GetType("AadcCommandCenter.ResultCode")!;
        var resultText = typeof(AadcCommandCenter.MainWindow).GetMethod("CommandResultText", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach(var code in Enumerable.Range(15, 9)) {
            var text = (string)resultText.Invoke(null, new[] { Enum.ToObject(resultType, code) })!;
            if (!text.Contains("RDR") && !text.Contains("라이다")) throw new Exception("Missing interlock diagnostic: " + code);
            if (code == 15 && !text.Contains("ED07/DE07")) throw new Exception("Missing firmware upgrade guidance");
        }
        int calls = 0; bool fail = false;
        Func<int,float[],Task> save = (group, values) => { calls++; return fail ? Task.FromException(new IOException("simulated disk failure")) : Task.CompletedTask; };
        var window = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { new float[] { 0,0,0 }, new float[] { 2,1,0 }, new float[] { 1.5f,10,3 }, new float[] {120,45,0}, save }, null)!;
        var root = (StackPanel)((ScrollViewer)window.Content).Content;
        var rows = root.Children.OfType<StackPanel>().ToArray();
        var preview = root.Children.OfType<FrameworkElement>().Single(x => x.GetType().Name == "RangePreview");
        var inputs = rows[2].Children.OfType<StackPanel>().SelectMany(x => x.Children.OfType<TextBox>()).ToArray();
        var button = rows[2].Children.OfType<Button>().Single();
        void Click() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        foreach (var bad in new[] { "word", "NaN", "Infinity", "-1" }) {
            inputs[0].Text = bad; Click(); Check(calls == 0, "invalid GUI number accepted: " + bad);
        }
        inputs[0].Text = "11"; Click(); Check(calls == 0, "inverted range accepted");
        inputs[0].Text = "2"; fail = true; Click();
        Check(calls == 1, "failure callback missing");
        Check(((float[])preview.GetType().GetProperty("Ranges")!.GetValue(preview)!)[0] == 1.5f, "failed save changed preview");
        Check(root.Children.OfType<TextBlock>().Any(x=>x.Text.Contains("저장 실패")), "failure not displayed");
        fail = false; Click(); Check(calls == 2, "save callback missing");
        Check(((float[])preview.GetType().GetProperty("Ranges")!.GetValue(preview)!)[0] == 2, "saved preview stale");
        Check(root.Children.OfType<TextBlock>().All(x=>!x.Text.Contains("노란색:")), "yellow legend absent");
        Check(root.Children.OfType<TextBlock>().All(x=>!x.Text.Contains("빨간색:")), "red legend absent");
        var angleInputs = rows[3].Children.OfType<StackPanel>().SelectMany(x => x.Children.OfType<TextBox>()).ToArray();
        var angleSave = rows[3].Children.OfType<Button>().Single();
        angleInputs[0].Text = "361";
        angleSave.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(calls == 2, "Invalid horizontal angle accepted");
        angleInputs[0].Text = "90"; angleInputs[1].Text = "60";
        angleSave.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(calls == 3, "FOV save callback missing");
        Check(((float[])preview.GetType().GetProperty("Fov")!.GetValue(preview)!)[0] == 90, "FOV preview stale");
        root.Measure(new Size(680, double.PositiveInfinity));
        root.Arrange(new Rect(0,0,680,root.DesiredSize.Height)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(680,(int)Math.Ceiling(root.ActualHeight + 40),96,96,PixelFormats.Pbgra32);
        bitmap.Render(root);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create("artifacts/camera-settings/preview.png"); png.Save(output);
        Console.WriteLine("PASS: GUI numeric/range rejection, failed-save preview rollback, successful refresh, labels and WPF render");
        window.Close();
        CheckMap();
    }
    static void CheckMap()
    {
        var main = new AadcCommandCenter.MainWindow();
        var type = main.GetType();
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        type.GetField("_cameraSettings", flags)!.SetValue(main, new[] { new float[3], new float[3], new float[] { 1.5f, 10, 3 }, new float[] {90,60,0} });
        type.GetField("_isSharedMemoryLive", flags)!.SetValue(main, true);
        var frame = new AadcCommandCenter.SharedFrame
        {
            payload = new AadcCommandCenter.SharedPayload
            {
                rdrCount = 1, fcsCount = 1, selectedRdrId = 1, selectedFcsId = 1,
                rdrAssets = new[] { new AadcCommandCenter.SharedRdrAsset { rdrId = 1, isEnabled = 1, isConnected = 1, isOperating = 1, azimuthDeg = 135, fieldOfViewDeg = 120 } },
                fcsAssets = new[] { new AadcCommandCenter.SharedFcsAsset { fcsId = 1, isEnabled = 1, isConnected = 1, positionX = 2, positionY = 1, azimuthDeg = 135 } },
                targets = new AadcCommandCenter.SharedTarget[10]
            }
        };
        type.GetMethod("ApplyFrame", flags)!.Invoke(main, new object[] { frame });
        var content = (FrameworkElement)main.Content;
        content.Measure(new Size(1480, 820)); content.Arrange(new Rect(0, 0, 1480, 820)); content.UpdateLayout();
        type.GetMethod("DrawRadar", flags)!.Invoke(main, null);
        var canvas = (Canvas)main.FindName("RadarCanvas");
        var overlay = canvas.Children.OfType<FrameworkElement>().Single(x=>x.GetType().Name == "CameraMapOverlay");
        var overlayType = overlay.GetType();
        double radius = (double)overlayType.GetProperty("MaximumRadius")!.GetValue(overlay)!;
        var center = (Point)overlayType.GetProperty("RdrCenter")!.GetValue(overlay)!;
        if (center.X-radius < 0 || center.X+radius > canvas.ActualWidth || center.Y-radius < 0 || center.Y+radius > canvas.ActualHeight)
            throw new Exception("Range ring clipped");
        if (overlay.IsHitTestVisible) throw new Exception("Overlay intercepts object clicks");
        var sectorMethod = overlayType.GetMethod("Sector", BindingFlags.NonPublic | BindingFlags.Static)!;
        var sector = (Geometry)sectorMethod.Invoke(null, new object[] { new Point(100,100), 80d, 0d, 120d })!;
        if (!sector.FillContains(new Point(100,50)) || sector.FillContains(new Point(150,100)) || sector.FillContains(new Point(100,150)))
            throw new Exception("120 degree FOV geometry incorrect");
        var rotated = (Geometry)sectorMethod.Invoke(null, new object[] { new Point(100,100), 80d, 90d, 120d })!;
        if (!rotated.FillContains(new Point(150,100)) || rotated.FillContains(new Point(100,50)))
            throw new Exception("FOV direction does not rotate");
        content.UpdateLayout();
        SaveVisual(content, 1480, 820, "artifacts/camera-settings/main-map.png");
        var panel = (FrameworkElement)((FrameworkElement)canvas.Parent).Parent;
        SaveVisual(panel, (int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight), "artifacts/camera-settings/left-map.png");
        main.Close();
        Console.WriteLine("PASS: actual main map render, range fits, 120 degree sector, heading rotation, click-through overlay");
    }

    static void SaveVisual(FrameworkElement visual, int width, int height, string path)
    {
        var bitmap = new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

}

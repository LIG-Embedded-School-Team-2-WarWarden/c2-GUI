using AadcCommandCenter;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;

static class Checks
{
    static void Check(bool b, string reason) { if (!b) throw new Exception(reason); }
    [STAThread] static void Main()
    {
        var app = new App(); app.InitializeComponent();
        var window = new MainWindow();
        try
        {
            var apply = typeof(MainWindow).GetMethod("ApplyFrame", BindingFlags.NonPublic|BindingFlags.Instance)!;
            typeof(MainWindow).GetField("_isSharedMemoryLive",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,true);
            var grid = (DataGrid)window.FindName("TargetGrid");
            SharedFrame Frame(byte status, byte valid, uint id=7) => new() { payload = new SharedPayload {
                rdrCount=1, fcsCount=1, selectedRdrId=1, selectedFcsId=1, targetCount=1,
                rdrAssets=new[]{new SharedRdrAsset { rdrId=1,isEnabled=1,isOperating=1,isConnected=1,azimuthDeg=135,fieldOfViewDeg=90 }},
                fcsAssets=new[]{new SharedFcsAsset { fcsId=1,isEnabled=1,isOperating=1,isConnected=1 }},
                targets=new[]{new SharedTarget { targetId=id,rdrId=1,lidarX=1,lidarY=3,lidarZ=.5f,distanceM=3.2f,status=status,valid=valid }} }};
            apply.Invoke(window,new object[]{Frame(0,1)});
            var original=(TargetRecord)grid.Items[0]; original.SelectedFcsId=1;
            Check(original.CanAssign,"observed should be usable");
            apply.Invoke(window,new object[]{Frame(4,0)});
            Check(grid.Items.Count==1 && ReferenceEquals(original,grid.Items[0]),"paused row identity lost");
            Check(original.StatusText=="관측 일시중지" && !original.CanAssign && original.LidarX==1,"paused display/control mismatch");
            var assignment=new AssignmentView(7,1);
            assignment.Refresh(original,new FcsAsset(1,true,true,true,false,180,0,0,0,0));
            Check(!assignment.CanFire,"paused execution enabled");
            if (Environment.GetEnvironmentVariable("RETENTION_RENDER") is string output)
            {
                var content=(FrameworkElement)window.Content;
                content.Measure(new Size(1500,950));content.Arrange(new Rect(0,0,1500,950));content.UpdateLayout();
                var bitmap=new RenderTargetBitmap(1500,950,96,96,PixelFormats.Pbgra32);bitmap.Render(content);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream=File.Create(output);encoder.Save(stream);
            }
            apply.Invoke(window,new object[]{Frame(5,0)});
            Check(ReferenceEquals(original,grid.Items[0]) && original.StatusText=="재관측 대기" && !original.CanAssign,"reacquiring row lost");
            apply.Invoke(window,new object[]{Frame(0,1)});
            Check(ReferenceEquals(original,grid.Items[0]) && original.CanAssign,"same ID failed to recover");
            apply.Invoke(window,new object[]{Frame(0,1,8)});
            Check(((TargetRecord)grid.Items[0]).Id==8 && !ReferenceEquals(original,grid.Items[0]),"new ID aliased to old object");
            apply.Invoke(window,new object[]{Frame(2,0)});
            Check(grid.Items.Count==0,"ordinary missing target retained");
            var connection = typeof(MainWindow).GetMethod("ApplyConnectionState",BindingFlags.NonPublic|BindingFlags.Instance)!;
            var running = typeof(MainWindow).GetField("_sequenceRunning",BindingFlags.NonPublic|BindingFlags.Instance)!;
            var stopped = typeof(MainWindow).GetField("_stopSequenceRequested",BindingFlags.NonPublic|BindingFlags.Instance)!;
            var setStatus=typeof(AssignmentView).GetMethod("SetCommandStatus",BindingFlags.NonPublic|BindingFlags.Instance)!;
            setStatus.Invoke(assignment,new object[]{Enum.Parse(setStatus.GetParameters()[0].ParameterType,"Assigned"),0u});
            ((DashboardData)window.DataContext).Assignments.Add(assignment);
            running.SetValue(window,true);
            FrameFreshnessChecks.Run(s => connection.Invoke(window,new object[]{s}),
                f => apply.Invoke(window,new object[]{f}), () => grid.Items.Count, () => assignment.CanFire);
            Check((bool)stopped.GetValue(window)!,"stale frames did not stop sequence");
            assignment.Refresh(null,null);
            Check(!assignment.CanFire && assignment.LidarXText=="—","offline assignment remained usable");
            running.SetValue(window,false);
            Console.WriteLine("PASS: WPF ApplyFrame paused/reacquiring visibility, row continuity, command gates, new ID and ordinary loss");
        }
        finally { window.Close(); app.Shutdown(); }
    }
}

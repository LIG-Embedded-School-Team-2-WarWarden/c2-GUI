using AadcCommandCenter;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;

internal static class Program
{
    [STAThread] static void Main()
    {
        GeometryChecks.Run();
        void Check(bool yes,string why) {if(!yes)throw new Exception(why);}
        var train=Enumerable.Range(0,3).Select(i=>new CalibrationSample(DateTimeOffset.UtcNow,1,i,10,1,359,5,1,8,false)).ToArray();
        var validation=train[0] with { X=5,ActualPan=2,Validation=true };
        var fit=CalibrationMath.Fit(train.Append(validation));
        Check(Math.Abs(fit.Pan-2)<1e-8&&Math.Abs(fit.Tilt-3)<1e-8,"wrap-aware correction");
        Check(Math.Abs(fit.ValidationRms-1)<1e-8,"validation must not alter fit");
        try {CalibrationMath.Fit(new[]{train[0],train[0],train[0]});throw new Exception("duplicate points accepted");}catch(InvalidOperationException){}
        var data=new DashboardData();
        data.FcsAssets.Add(new FcsAsset(1,true,false,true,false,135,5,0,0,0));
        // RDR is optional in this fixture; all points and FCS still render.
        for(uint i=1;i<=4;i++)data.Targets.Add(new TargetRecord(i,0,0,10,i*2,10+i,1+i,1,0,true,1,false));
        int moves=0;bool live=true;
        float motorPan=182,motorTilt=8;
        (float Pan,float Tilt) lastCommand=default;
        TaskCompletionSource<PipeResponse>? pendingFeedback=null;
        Task<PipeResponse> Send(CommandType c,uint target,float pan,float tilt)
        {
            if(c==CommandType.CalibrationPoint){moves++;lastCommand=(pan,tilt);motorPan=pan;motorTilt=tilt;}
            if(c==CommandType.CalibrationFeedback&&pendingFeedback!=null)return pendingFeedback.Task;
            return Task.FromResult(new PipeResponse { FcsId=BitConverter.SingleToUInt32Bits(c==CommandType.CalibrationReference?180:motorPan),AssignedTargetId=BitConverter.SingleToUInt32Bits(c==CommandType.CalibrationReference?5:motorTilt),IsConnected=1 });
        }
        var path=Path.Combine(Path.GetTempPath(),"c2-calibration-check-"+Guid.NewGuid()+".json");
        var window=new CalibrationWindow(data,()=>live,Send,new float[]{7,9,0},path);
        object Field(string name)=>typeof(CalibrationWindow).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
        void Call(string name)=>((Task)typeof(CalibrationWindow).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,null)!).GetAwaiter().GetResult();
        var targets=(ComboBox)Field("targets");
        foreach(var target in data.Targets.Take(3)){targets.SelectedItem=target;Call("CaptureReference");Call("SaveSample");}
        targets.SelectedItem=data.Targets[3];Call("CaptureReference");((CheckBox)Field("validation")).IsChecked=true;Call("SaveSample");((CheckBox)Field("validation")).IsChecked=false;
        Check(File.Exists(path),"samples persisted");
        var archive=System.Text.Json.JsonSerializer.Deserialize<CalibrationArchive>(File.ReadAllText(path))!;
        Check(archive.Samples.Count==4&&archive.OriginalOffsets[0]==7,"archive retains samples and original offsets");
        Check(CalibrationMath.Fit(archive.Samples).Pan==2,"GUI collection uses raw baseline, not old offset");
        targets.SelectedItem=data.Targets[0];Call("CaptureReference");live=false;
        try {Call("SaveSample");throw new Exception("disconnected sample accepted");}catch(IOException){}
        live=true;
        data.Targets[0].UpdateFrom(new TargetRecord(1,0,0,10,20,10,1,1,0,true,1,false));
        try {Call("SaveSample");throw new Exception("moving target accepted");}catch(IOException){}
        Check(moves==0,"reading and saving must not move motors");
        void Set(string name,object value)=>typeof(CalibrationWindow).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,value);
        void Sync(string name)=>typeof(CalibrationWindow).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,null);
        // Discrete movement must preserve the other axis, including negative tilt.
        Set("review",false);Call("CaptureReference");motorPan=359.9f;motorTilt=-1.9f;
        window.HandleDirectionKey(Key.Right,false).GetAwaiter().GetResult();Check(moves==0,"unarmed arrows moved motor");
        ((ComboBox)Field("stepSize")).SelectedIndex=0;Set("keyboardEnabled",true);
        window.HandleDirectionKey(Key.Right,true).GetAwaiter().GetResult();Check(moves==0,"key repeat moved motor");
        window.HandleDirectionKey(Key.Right,false).GetAwaiter().GetResult();Check(moves==1&&lastCommand.Pan==0&&Math.Abs(lastCommand.Tilt+1.9)<.001,"wrap / signed tilt preservation");
        window.HandleDirectionKey(Key.Up,false).GetAwaiter().GetResult();Check(moves==1,"queued input while awaiting arrival");
        Call("Tick");window.HandleDirectionKey(Key.Up,false).GetAwaiter().GetResult();Check(moves==2&&Math.Abs(lastCommand.Tilt+1.8)<.001,"up is positive 0.1 degree");
        Call("Tick");((ComboBox)Field("stepSize")).SelectedIndex=1;
        Check((bool)Field("keyboardEnabled"),"changing step size disabled adjustment mode");
        window.HandleDirectionKey(Key.Left,false).GetAwaiter().GetResult();Check(moves==3&&lastCommand.Pan==359.5f,"left is negative 0.5 degree");
        Call("Tick");((ComboBox)Field("stepSize")).SelectedIndex=2;
        Check((bool)Field("keyboardEnabled"),"changing step size requires re-arming");
        window.HandleDirectionKey(Key.Down,false).GetAwaiter().GetResult();Check(moves==4&&Math.Abs(lastCommand.Tilt+2.8)<.001,"down is negative 1 degree");
        Call("Tick");motorTilt=-30;window.HandleDirectionKey(Key.Down,false).GetAwaiter().GetResult();Check(moves==4,"lower limit sent command");
        // Escape/deactivation while waiting for feedback must cancel the deferred action.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        pendingFeedback=new TaskCompletionSource<PipeResponse>();
        var pending=window.HandleDirectionKey(Key.Right,false);Sync("Disarm");
        pendingFeedback.SetResult(new PipeResponse{FcsId=BitConverter.SingleToUInt32Bits(180),AssignedTargetId=BitConverter.SingleToUInt32Bits(0)});pendingFeedback=null;
        var frame=new DispatcherFrame();var pump=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(10)};int ticks=0;
        pump.Tick+=(_,_)=>{if(pending.IsCompleted||++ticks>200){pump.Stop();frame.Continue=false;}};pump.Start();Dispatcher.PushFrame(frame);pending.GetAwaiter().GetResult();
        Check(moves==4,"cancelled input sent a delayed command");
        foreach(var (index,step) in new[]{(3,5f),(4,10f),(5,30f)}) {
            motorPan=180;motorTilt=10;Call("Tick");
            ((ComboBox)Field("stepSize")).SelectedIndex=index;Set("keyboardEnabled",true);
            int before=moves;window.HandleDirectionKey(Key.Right,false).GetAwaiter().GetResult();
            Check(moves==before+1&&lastCommand==(180+step,10f),"large horizontal step must be one command");
            Call("Tick");window.HandleDirectionKey(Key.Up,false).GetAwaiter().GetResult();
            Check(moves==before+2&&lastCommand==(180+step,10+step),"large vertical step must preserve pan");
            Call("Tick");motorTilt=80;
            if(step==30){window.HandleDirectionKey(Key.Up,false).GetAwaiter().GetResult();Check(moves==before+2,"large step exceeded tilt limit");}
        }
        Sync("Disarm");
        motorPan=182;motorTilt=8;Call("Tick");Sync("Update");
        var root=(FrameworkElement)window.Content;root.Measure(new Size(1080,780));root.Arrange(new Rect(0,0,1080,780));root.UpdateLayout();
        var bitmap=new RenderTargetBitmap(1080,780,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
        Directory.CreateDirectory("camera-calibration-check/artifacts");
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var output=File.Create("camera-calibration-check/artifacts/calibration.png"))encoder.Save(output);
        window.Close();File.Delete(path);
        Console.WriteLine("PASS: wrap, held-out error, duplicate rejection, collection, persistence, disconnect/motion rejection, no incidental motor commands, WPF render");
    }
}

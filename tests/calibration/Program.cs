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
        Check(TargetScene.MirrorDisplayX(2.5)==-2.5&&TargetScene.MirrorDisplayX(-3)==3,
            "calibration 3D scene horizontal display mirror regression");
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
        int moves=0,laserPulses=0,laserOffs=0;bool live=true,failLaserOff=false,failLaserPulse=false;
        var commands=new List<CommandType>();
        float motorPan=182,motorTilt=8;
        (float Pan,float Tilt) lastCommand=default;
        TaskCompletionSource<PipeResponse>? pendingFeedback=null;
        TaskCompletionSource<PipeResponse>? pendingPoint=null;
        Task<PipeResponse> Send(CommandType c,uint target,float pan,float tilt)
        {
            commands.Add(c);
            if(c==CommandType.CalibrationLaserPulse){laserPulses++;if(failLaserPulse)throw new IOException("pulse test failure");}
            if(c==CommandType.CalibrationLaserOff){laserOffs++;if(failLaserOff)throw new IOException("OFF test failure");}
            if(c==CommandType.CalibrationPoint){moves++;lastCommand=(pan,tilt);if(pendingPoint!=null)return pendingPoint.Task;motorPan=pan;motorTilt=tilt;}
            if(c==CommandType.CalibrationFeedback&&pendingFeedback!=null)return pendingFeedback.Task;
            return Task.FromResult(new PipeResponse { FcsId=BitConverter.SingleToUInt32Bits(c==CommandType.CalibrationReference?180:motorPan),AssignedTargetId=BitConverter.SingleToUInt32Bits(c==CommandType.CalibrationReference?5:motorTilt),IsConnected=1 });
        }
        var path=Path.Combine(Path.GetTempPath(),"c2-calibration-check-"+Guid.NewGuid()+".json");
        var window=new CalibrationWindow(data,()=>live,Send,new float[]{7,9,0},path);
        object Field(string name)=>typeof(CalibrationWindow).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
        void Call(string name)=>((Task)typeof(CalibrationWindow).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,null)!).GetAwaiter().GetResult();
        var targets=(ComboBox)Field("targets");
        targets.SelectedItem=data.Targets[0];Call("CaptureReference");
        Check(moves==1&&Math.Abs(lastCommand.Pan-180)<.001&&Math.Abs(lastCommand.Tilt-5)<.001,"training coarse aim did not move to the raw ECS reference");
        Check(((Button)Field("laserPulse")).Content?.ToString()=="FCS 레이저 자동 ON 유지 중"&&((Button)Field("laserOff")).Content?.ToString()=="FCS 레이저 즉시 끄기","calibration controls must distinguish the automatically enabled FCS laser from RDR lidar");
        Check(laserPulses==1,"entering zero adjustment must automatically enable the FCS laser");
        Check((bool)Field("laserContinuous"),"continuous laser mode was not armed");
        var laserOnStatus=((TextBlock)Field("status")).Text;
        Check(laserOnStatus.Contains("FCS 레이저 자동 ON")&&laserOnStatus.Contains("방향키로 바로"),"zero adjustment status must describe automatic FCS laser and immediate direction control");
        Check(((Button)Field("laserOff")).IsEnabled,"active laser pulse must expose immediate OFF");
        Call("ShowLaserPulse");Check(laserPulses==1,"active pulse was repeated");
        Set("laserRenewAt",DateTimeOffset.UtcNow.AddSeconds(-1));Call("Tick");
        Check(laserPulses==2&&(bool)Field("laserContinuous"),"continuous laser safety pulse was not renewed");
        int offBeforeDeactivate=laserOffs;
        typeof(Window).GetMethod("OnDeactivated",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,new object[]{EventArgs.Empty});
        Check(laserOffs==offBeforeDeactivate&&(bool)Field("laserContinuous"),"temporarily leaving the calibration window stopped the automatic FCS laser");
        failLaserOff=true;Call("BestEffortLaserOff");Sync("Update");
        Check(((Button)Field("laserOff")).IsEnabled&&((TextBlock)Field("laserState")).Text.Contains("OFF 확인 중"),"unknown OFF outcome hid the active pulse before status observation");
        Check(!(bool)Field("laserContinuous"),"failed OFF left automatic laser renewal armed");
        failLaserOff=false;
        int offBeforeImmediate=laserOffs;Call("RequestLaserOff");
        Check(laserOffs==offBeforeImmediate+1&&!((Button)Field("laserOff")).IsEnabled,"immediate laser OFF missing");
        var fcs=data.FcsAssets[0];
        fcs.UpdateFrom(new FcsAsset(1,true,false,true,true,135,5,0,0,0));failLaserOff=true;Call("BestEffortLaserOff");Sync("Update");
        Check(((Button)Field("laserOff")).IsEnabled&&!((Button)Field("laserPulse")).IsEnabled&&((TextBlock)Field("laserState")).Text.Contains("ON 감지"),"observed laser ON was hidden after an OFF failure");
        failLaserOff=false;fcs.UpdateFrom(new FcsAsset(1,true,false,true,false,135,5,0,0,0));Call("RequestLaserOff");Sync("Update");
        Call("ShowLaserPulse");Set("laserRenewAt",DateTimeOffset.UtcNow.AddSeconds(-1));failLaserPulse=true;
        int offBeforeRenewFailure=laserOffs;Call("Tick");
        Check(!(bool)Field("laserContinuous")&&laserOffs>offBeforeRenewFailure,"failed renewal did not stop continuous mode and request OFF");
        failLaserPulse=false;
        typeof(CalibrationWindow).GetField("reference",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,null);
        foreach(var target in data.Targets.Take(3)){targets.SelectedItem=target;Call("CaptureReference");motorPan=182;motorTilt=8;Call("SaveSample");}
        targets.SelectedItem=data.Targets[3];Call("CaptureReference");motorPan=182;motorTilt=8;((CheckBox)Field("validation")).IsChecked=true;Call("SaveSample");((CheckBox)Field("validation")).IsChecked=false;
        Check(File.Exists(path),"samples persisted");
        var archive=System.Text.Json.JsonSerializer.Deserialize<CalibrationArchive>(File.ReadAllText(path))!;
        Check(archive.Samples.Count==4&&archive.OriginalOffsets[0]==7,"archive retains samples and original offsets");
        Check(CalibrationMath.Fit(archive.Samples).Pan==2,"GUI collection uses raw baseline, not old offset");
        var resumed=new CalibrationWindow(data,()=>live,Send,new float[]{7,9,0},path);
        object ResumedField(string name)=>typeof(CalibrationWindow).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(resumed)!;
        ((Task)typeof(CalibrationWindow).GetMethod("InitializeSession",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(resumed,null)!).GetAwaiter().GetResult();
        Check(((System.Collections.ICollection)ResumedField("samples")).Count==4&&((CheckBox)ResumedField("validation")).IsChecked==true,"saved calibration was not resumed automatically");
        ((DispatcherTimer)ResumedField("timer")).Stop();
        targets.SelectedItem=data.Targets[0];Call("CaptureReference");int movesBeforeSaveFailures=moves;live=false;
        try {Call("SaveSample");throw new Exception("disconnected sample accepted");}catch(IOException){}
        live=true;
        data.Targets[0].UpdateFrom(new TargetRecord(1,0,0,10,20,10,1,1,0,true,1,false));
        try {Call("SaveSample");throw new Exception("moving target accepted");}catch(IOException){}
        Check(moves==movesBeforeSaveFailures,"saving must not add motor movement after the coarse aim");
        void Set(string name,object value)=>typeof(CalibrationWindow).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,value);
        void Sync(string name)=>typeof(CalibrationWindow).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,null);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        void Pump(Task task){if(task.IsCompleted){task.GetAwaiter().GetResult();return;}var frame=new DispatcherFrame();var pump=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(10)};int ticks=0;pump.Tick+=(_,_)=>{if(task.IsCompleted||++ticks>200){pump.Stop();frame.Continue=false;}};pump.Start();Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult();}
        // Discrete movement must preserve the other axis, including negative tilt.
        Set("review",false);Call("CaptureReference");moves=0;motorPan=359.9f;motorTilt=-1.9f;
        ((ComboBox)Field("stepSize")).SelectedIndex=0;
        window.HandleDirectionKey(Key.Right,true).GetAwaiter().GetResult();Check(moves==0,"key repeat moved motor");
        pendingPoint=new TaskCompletionSource<PipeResponse>();
        var firstRapid=window.HandleDirectionKey(Key.Right,false);var secondRapid=window.HandleDirectionKey(Key.Up,false);
        Check(moves==1,"overlapping input was not queued behind the in-flight command");
        Check(((List<Button>)Field("arrows")).All(button=>button.IsEnabled),"arrow buttons were disabled while a nudge was in flight");
        var pointReply=pendingPoint;pendingPoint=null;pointReply.SetResult(new PipeResponse{IsConnected=1});Pump(Task.WhenAll(firstRapid,secondRapid));
        Check(moves==2&&Math.Abs(lastCommand.Pan-359.7)<.001&&Math.Abs(lastCommand.Tilt+1.7)<.001,"rapid 0.2-degree goals did not accumulate with mirrored horizontal calibration");
        motorPan=359.5f;motorTilt=-2.5f;Call("Tick");Check((bool)Field("motionPending"),"intermediate feedback falsely completed the latest goal");
        motorPan=359.7f;motorTilt=-1.7f;Call("Tick");Check(!(bool)Field("motionPending"),"arrival feedback did not complete the latest goal");
        ((ComboBox)Field("stepSize")).SelectedIndex=1;
        window.HandleDirectionKey(Key.Left,false).GetAwaiter().GetResult();Check(moves==3&&Math.Abs(lastCommand.Pan-.2)<.001,"left is positive 0.5 degree on the mirrored camera axis");
        Call("Tick");((ComboBox)Field("stepSize")).SelectedIndex=2;
        window.HandleDirectionKey(Key.Down,false).GetAwaiter().GetResult();Check(moves==4&&Math.Abs(lastCommand.Tilt+2.7)<.001,"down is negative 1 degree");
        Call("Tick");motorTilt=-30;Set("commandGoalValid",false);window.HandleDirectionKey(Key.Down,false).GetAwaiter().GetResult();Check(moves==4,"lower limit sent command");
        // Escape/deactivation while waiting for feedback must cancel the deferred action.
        Set("commandGoalValid",false);
        pendingFeedback=new TaskCompletionSource<PipeResponse>();
        var pending=window.HandleDirectionKey(Key.Right,false);Sync("Disarm");
        pendingFeedback.SetResult(new PipeResponse{FcsId=BitConverter.SingleToUInt32Bits(180),AssignedTargetId=BitConverter.SingleToUInt32Bits(0)});pendingFeedback=null;
        Pump(pending);
        Check(moves==4,"cancelled input sent a delayed command");
        window.HandleDirectionKey(Key.Right,false).GetAwaiter().GetResult();Check(moves==5,"direction input required re-arming after cancellation");
        foreach(var (index,step) in new[]{(3,5f),(4,10f),(5,30f)}) {
            motorPan=180;motorTilt=10;Set("commandGoalValid",false);Call("Tick");
            ((ComboBox)Field("stepSize")).SelectedIndex=index;
            int before=moves;window.HandleDirectionKey(Key.Right,false).GetAwaiter().GetResult();
            Check(moves==before+1&&lastCommand==(180-step,10f),"large horizontal step must follow the mirrored camera axis in one command");
            window.HandleDirectionKey(Key.Up,false).GetAwaiter().GetResult();
            Check(moves==before+2&&lastCommand==(180-step,10+step),"large vertical step must preserve mirrored pan");
            Call("Tick");motorTilt=80;Set("commandGoalValid",false);
            if(step==30){window.HandleDirectionKey(Key.Up,false).GetAwaiter().GetResult();Check(moves==before+2,"large step exceeded tilt limit");}
        }
        motorPan=182;motorTilt=8;Set("commandGoalValid",false);((ComboBox)Field("stepSize")).SelectedIndex=1;Call("Tick");commands.Clear();targets.SelectedItem=data.Targets[0];Call("CaptureReference");int movementCommandStart=commands.Count;
        int pulseBeforeLaserMove=laserPulses,offBeforeLaserMove=laserOffs;
        pendingPoint=new TaskCompletionSource<PipeResponse>();
        var firstLaserMove=window.HandleDirectionKey(Key.Right,false);var secondLaserMove=window.HandleDirectionKey(Key.Up,false);
        Check(commands.Contains(CommandType.CalibrationPoint),"laser-on movement did not send the first point command");
        Set("laserRenewAt",DateTimeOffset.UtcNow.AddSeconds(-1));Call("Tick");
        int firstPulse=commands.LastIndexOf(CommandType.CalibrationLaserPulse,movementCommandStart-1),firstPoint=commands.FindIndex(movementCommandStart,c=>c==CommandType.CalibrationPoint),timerRenewal=commands.FindIndex(firstPoint+1,c=>c==CommandType.CalibrationLaserPulse);
        Check(firstPulse>=0&&firstPulse<firstPoint&&firstPoint<timerRenewal,"laser pulse, movement, and busy-tick watchdog renewal were not ordered");
        Check(laserPulses==pulseBeforeLaserMove+1&&laserOffs==offBeforeLaserMove&&!commands.Skip(movementCommandStart).Contains(CommandType.CalibrationLaserOff)&&(bool)Field("laserContinuous"),"movement stopped continuous FCS laser or inserted laser OFF");
        Set("laserRenewAt",DateTimeOffset.UtcNow.AddSeconds(-1));var laserPointReply=pendingPoint;pendingPoint=null;laserPointReply.SetResult(new PipeResponse{IsConnected=1});Pump(Task.WhenAll(firstLaserMove,secondLaserMove));
        int inlineRenewal=commands.FindIndex(timerRenewal+1,c=>c==CommandType.CalibrationLaserPulse),secondPoint=commands.FindIndex(firstPoint+1,c=>c==CommandType.CalibrationPoint);
        Check(timerRenewal<inlineRenewal&&inlineRenewal<secondPoint&&commands.Skip(movementCommandStart).Count(c=>c==CommandType.CalibrationPoint)==2,"nudge queue did not renew the laser inline before its next point command: "+string.Join(",",commands));
        Check(laserPulses==pulseBeforeLaserMove+2&&laserOffs==offBeforeLaserMove&&(bool)Field("laserContinuous")&&((TextBlock)Field("status")).Text.Contains("FCS 레이저 계속 ON"),"rapid nudges did not finish with the FCS laser maintained");
        motorPan=lastCommand.Pan;motorTilt=lastCommand.Tilt;Check((bool)Field("motionPending"),"laser-on move did not enter arrival tracking");Call("Tick");
        Check(!(bool)Field("motionPending"),"laser-on Tick did not poll feedback and clear completed movement");
        Call("RequestLaserOff");
        Sync("Disarm");
        motorPan=182;motorTilt=8;Call("Tick");Sync("Update");
        var root=(FrameworkElement)window.Content;root.Measure(new Size(1080,780));root.Arrange(new Rect(0,0,1080,780));root.UpdateLayout();
        var bitmap=new RenderTargetBitmap(1080,780,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
        Directory.CreateDirectory("camera-calibration-check/artifacts");
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var output=File.Create("camera-calibration-check/artifacts/calibration.png"))encoder.Save(output);
        window.Close();File.Delete(path);
        Console.WriteLine("PASS: wrap, automatic saved-session resume, training coarse aim, automatic calibration laser, immediate direction control, rapid accumulated movement, held-out error, duplicate rejection, collection, persistence, disconnect/motion rejection, guarded continuous laser renewal/OFF ordering, WPF render");
    }
}

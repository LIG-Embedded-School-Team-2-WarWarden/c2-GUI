using AadcCommandCenter;
using System.Text.Json;
using System.IO;
using System.Reflection;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class GeometryChecks
{
    internal static void Run()
    {
        void Check(bool pass,string reason){if(!pass)throw new Exception(reason);}
        var quarterTurn=new GeometryCalibration(true,new double[3],new[]{0d,0,Math.PI/2}).Angles(new[]{1d,0,0},new double[2]);
        Check(Math.Abs(quarterTurn.Pan-180)<1e-8&&Math.Abs(quarterTurn.Tilt)<1e-8,"Rodrigues axis/sign convention");
        var displaced=new GeometryCalibration(true,new[]{1d,0,0},new double[3]);
        Check(Math.Abs(displaced.Angles(new[]{0d,1,0},new double[2]).Pan-225)<1e-8,"near lever arm");
        Check(Math.Abs(displaced.Angles(new[]{0d,10,0},new double[2]).Pan-185.7105931375)<1e-8,"far lever arm must differ from near");
        var truth=new GeometryCalibration(true,new[]{.32,-.17,.12},new[]{.045,-.03,.055});
        double[] origin={.5,.3,.1},offsets={2,-3,0};
        uint id=1;
        CalibrationSample Sample(double range,double heading,double tilt,bool check)
        {
            double pan=heading*Math.PI/180,el=tilt*Math.PI/180;
            var relative=new[]{range*Math.Sin(pan)*Math.Cos(el),range*Math.Cos(pan)*Math.Cos(el),range*Math.Sin(el)};
            var actual=truth.Angles(relative,offsets);var baseline=GeometryCalibration.Identity.Angles(relative,new double[2]);
            return new(DateTimeOffset.UtcNow,id++,relative[0]+origin[0],relative[1]+origin[1],relative[2]+origin[2],baseline.Pan,baseline.Tilt,
                Math.Round(actual.Pan,1),Math.Round(actual.Tilt,1),check);
        }
        var samples=new List<CalibrationSample>();
        foreach(double range in new[]{3d,7,15})foreach(double heading in new[]{-35d,0,35})foreach(double tilt in new[]{-10d,12,35})samples.Add(Sample(range,heading,tilt,false));
        foreach(double range in new[]{4d,9,14})foreach(double heading in new[]{-20d,20})samples.Add(Sample(range,heading,20,true));
        var fit=GeometrySolver.Fit(samples,origin,offsets);
        Check(fit.CanApply&&fit.ValidationRms<.1,"3D fit did not generalize across held-out distances");
        Check(fit.ValidationRms<fit.BaselineRms*.2,"3D model did not improve over fixed offsets");
        Check(GeometrySolver.Norm(fit.Model.Translation.Zip(truth.Translation,(a,b)=>a-b).ToArray())<.03,"translation not recovered");
        Check(GeometrySolver.Norm(fit.Model.Rotation.Zip(truth.Rotation,(a,b)=>a-b).ToArray())<.01,"rotation not recovered");
        var before=fit.Model.Translation.ToArray();
        var corrupt=samples.Select(s=>s.Validation?s with{ActualPan=s.ActualPan+8}:s).ToArray();
        var failed=GeometrySolver.Fit(corrupt,origin,offsets);
        Check(!failed.CanApply&&failed.Model.Translation.Zip(before,(a,b)=>Math.Abs(a-b)).Max()<1e-9,"validation contaminated fit or failed to reject bad model");
        Check(!GeometrySolver.Fit(samples.Where(s=>!s.Validation),origin,offsets).CanApply,"unvalidated model accepted");
        var narrow=Enumerable.Range(0,12).Select(i=>Sample(5, i-6,10+i*.1,false));
        try{GeometrySolver.Fit(narrow,origin,offsets);throw new Exception("narrow coverage accepted");}catch(InvalidOperationException){}
        var repeated=Enumerable.Repeat(samples[0],12);
        try{GeometrySolver.Fit(repeated,origin,offsets);throw new Exception("duplicate geometry points accepted");}catch(InvalidOperationException){}
        var line=Enumerable.Range(0,18).Select(i=>{
            double[] p={-3+i*.5,4+i*.2,-1+i*.3};var angles=truth.Angles(p,offsets);
            return new CalibrationSample(DateTimeOffset.UtcNow,(uint)i,p[0]+origin[0],p[1]+origin[1],p[2]+origin[2],0,0,angles.Pan,angles.Tilt,false);
        });
        try{GeometrySolver.Fit(line,origin,offsets);throw new Exception("collinear ill-conditioned geometry accepted");}catch(InvalidOperationException){}
        // Outside the training range is a prediction, not a coverage guarantee.
        var remote=Sample(25,15,18,true);var p=new[]{remote.X-origin[0],remote.Y-origin[1],remote.Z-origin[2]};
        var predicted=fit.Model.Angles(p,offsets);
        Check(Math.Abs(CalibrationMath.Wrap(predicted.Pan-remote.ActualPan))<.12&&Math.Abs(predicted.Tilt-remote.ActualTilt)<.12,"physical transform extrapolation regression");
        Directory.CreateDirectory("camera-calibration-check/artifacts");
        File.WriteAllText("camera-calibration-check/artifacts/geometry-fixture.json",JsonSerializer.Serialize(new{fit.Model,Offsets=offsets,Origin=origin,Samples=samples}));
        var dashboard=new DashboardData();dashboard.FcsAssets.Add(new FcsAsset(1,true,false,true,false,135,0,(float)origin[0],(float)origin[1],(float)origin[2]));
        var folder=Directory.CreateTempSubdirectory("c2-geometry-ui-");
        GeometryCalibration? applied=null;int trimWrites=0;
        Task<PipeResponse> Send(CommandType command,uint target,float pan,float tilt){if(command==CommandType.SetCameraSettings)trimWrites++;return Task.FromResult(new PipeResponse());}
        var window=new CalibrationWindow(dashboard,()=>true,Send,offsets.Select(x=>(float)x).ToArray(),Path.Combine(folder.FullName,"session.json"),saveGeometry:m=>{applied=m;return Task.CompletedTask;});
        object Field(string name)=>typeof(CalibrationWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
        void Invoke(string name)=>typeof(CalibrationWindow).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);
        var stored=(ObservableCollection<CalibrationSample>)Field("samples");foreach(var sample in samples)stored.Add(sample);
        Invoke("Calculate");Invoke("Update");
        Check(((Button)Field("primary")).IsEnabled,"valid geometry cannot be applied in wizard");
        ((Task)typeof(CalibrationWindow).GetMethod("Primary",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)!).GetAwaiter().GetResult();
        Check(applied?.Enabled==true&&trimWrites==0,"geometry apply overwrote manual trim");
        var history=Directory.GetFiles(Path.Combine(folder.FullName,"calibration-models"));Check(history.Length==1,"applied model history absent");
        var application=JsonSerializer.Deserialize<CalibrationApplication>(File.ReadAllText(history[0]))!;
        Check(application.State=="Applied"&&application.Samples.Count==samples.Count&&application.Fit.CanApply,"applied evidence not preserved");
        var root=(FrameworkElement)window.Content;root.Measure(new Size(1050,820));root.Arrange(new Rect(0,0,1050,820));root.UpdateLayout();
        var bitmap=new RenderTargetBitmap(1050,820,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create("camera-calibration-check/artifacts/geometry-result.png"))png.Save(output);
        window.Close();
        // Validation preview moves to the candidate, keeps the raw reference and all stored evidence.
        var probe=samples.Last();
        dashboard.Targets.Add(new TargetRecord(99,0,0,10,(float)probe.X,(float)probe.Y,(float)probe.Z,1,0,true,1,false));
        float mp=359,mt=0;int previewMoves=0;bool cancelPreview=false;
        CalibrationWindow? preview=null;
        Task<PipeResponse> PreviewSend(CommandType command,uint target,float pan,float tilt) {
            if(command==CommandType.CalibrationPoint) {
                Check(Math.Abs(CalibrationMath.Wrap(pan-mp))<=30.01&&Math.Abs(tilt-mt)<=30.01,"preview exceeded command bound");
                mp=pan;mt=tilt;previewMoves++;
                if(cancelPreview)typeof(CalibrationWindow).GetMethod("Disarm",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(preview,null);
            }
            return Task.FromResult(new PipeResponse{FcsId=BitConverter.SingleToUInt32Bits(command==CommandType.CalibrationReference?180:mp),AssignedTargetId=BitConverter.SingleToUInt32Bits(command==CommandType.CalibrationReference?5:mt)});
        }
        string previewPath=Path.Combine(folder.FullName,"preview.json");
        preview=new CalibrationWindow(dashboard,()=>true,PreviewSend,offsets.Select(x=>(float)x).ToArray(),previewPath);
        object PF(string name)=>typeof(CalibrationWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(preview)!;
        void PC(string name)=>((Task)typeof(CalibrationWindow).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(preview,null)!).GetAwaiter().GetResult();
        var points=(ObservableCollection<CalibrationSample>)PF("samples");foreach(var sample in samples)points.Add(sample);
        ((ComboBox)PF("targets")).SelectedItem=dashboard.Targets[0];((CheckBox)PF("validation")).IsChecked=true;
        PC("CaptureReference");
        var expected=fit.Model.Angles(new[]{probe.X-origin[0],probe.Y-origin[1],probe.Z-origin[2]},offsets);
        Check(previewMoves>1&&Math.Abs(CalibrationMath.Wrap(mp-expected.Pan))<.06&&Math.Abs(mt-expected.Tilt)<.06,"preview missed corrected angle");
        Check(points.Count==samples.Count&&!File.Exists(previewPath),"preview changed saved observations");
        Check((bool)PF("keyboardEnabled"),"fine adjustment not enabled after preview");
        mp+=.2f;PC("SaveSample");Check(points.Last().Validation&&Math.Abs(points.Last().ActualPan-mp)<.001&&points.Last().BasePan==180,"validation did not preserve measured angle and raw baseline");
        var savedBefore=File.ReadAllText(previewPath);cancelPreview=true;mp=0;int commandsBefore=previewMoves;
        try{PC("CaptureReference");throw new Exception("cancelled preview accepted");}catch(IOException){}
        Check(previewMoves==commandsBefore+1&&File.ReadAllText(previewPath)==savedBefore,"cancelled preview continued or changed data");
        preview.Close();
        Console.WriteLine($"PASS: 3D transform, quantized measurements, held-out distance error {fit.ValidationRms:F4} deg (baseline {fit.BaselineRms:F3}), coverage/validation rejection");
    }
}

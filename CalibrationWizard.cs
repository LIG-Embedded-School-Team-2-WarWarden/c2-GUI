using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AadcCommandCenter;

internal sealed class CalibrationWindow : Window
{
    private readonly DashboardData data;
    private readonly Func<bool> live;
    private readonly Func<CommandType,uint,float,float,Task<PipeResponse>> send;
    private readonly float[] original,installation;
    private readonly string path;
    private readonly GeometryCalibration originalGeometry;
    private readonly Func<GeometryCalibration,Task> saveGeometry;
    private readonly ObservableCollection<CalibrationSample> samples=new();
    private readonly ComboBox targets=new() { DisplayMemberPath="DisplayName",MinWidth=180,FontSize=16,Margin=new Thickness(0,12,0,12) };
    // The guided flow chooses training/validation, rather than asking the user to understand a checkbox.
    private readonly CheckBox validation=new();
    private readonly ComboBox stepSize=new() { ItemsSource=new[]{"0.1° · 정밀","0.5° · 미세","1°","5° · 큰 이동","10° · 큰 이동","30° · 큰 이동"},SelectedIndex=1,MinWidth=180,Margin=new Thickness(0,8,0,12) };
    private readonly TextBlock heading=Text("1. 맞출 객체를 선택하세요",24);
    private readonly TextBlock instruction=Text("",15);
    private readonly TextBlock progress=Text("",13);
    private readonly TextBlock coverage=Text("가까운 곳·중간·먼 곳과 높이가 다른 지점을 골라주세요.",13);
    private readonly TextBlock status=Text("",14);
    private readonly TextBlock feedback=Text("각도 확인 중…",13);
    private readonly TextBlock keyState=Text("방향키 조절 꺼짐",14);
    private readonly TextBlock result=Text("",16);
    private readonly StackPanel pickPanel=new(),alignPanel=new(),reviewPanel=new();
    private readonly TargetScene scene;
    private readonly Button primary,verify,arm;
    private readonly List<Button> actions=new(),arrows=new();
    private readonly DataGrid table=new() { AutoGenerateColumns=false,IsReadOnly=true,Height=160,Margin=new Thickness(0,8,0,8) };
    private readonly DispatcherTimer timer=new() { Interval=TimeSpan.FromMilliseconds(300) };
    private readonly SemaphoreSlim io=new(1,1);
    private (uint Id,double X,double Y,double Z,double Pan,double Tilt,DateTimeOffset Time)? reference;
    private GeometryFit? fit;
    private bool busy,closed,keyboardEnabled,review,applied,motionPending;
    private int inputEpoch;
    private float actualPan,actualTilt;
    private DateTimeOffset feedbackAt;

    private static TextBlock Text(string text,double size=14)=>new() { Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.WhiteSmoke,Margin=new Thickness(0,4,0,6) };
    private float[] Installation()
    {
        var rdr=data.RdrAssets.FirstOrDefault();var fcs=data.FcsAssets.FirstOrDefault();
        return new[]{rdr?.PositionX??0,rdr?.PositionY??0,rdr?.PositionZ??0,
            fcs?.PositionX??0,fcs?.PositionY??0,fcs?.PositionZ??0};
    }
    private (double X,double Y,double Z) RdrLocal(double x,double y,double z)
    {
        var rdr=data.RdrAssets.FirstOrDefault();
        return (x-(rdr?.PositionX??0),y-(rdr?.PositionY??0),z-(rdr?.PositionZ??0));
    }
    internal CalibrationWindow(DashboardData data,Func<bool> live,Func<CommandType,uint,float,float,Task<PipeResponse>> send,float[] original,string? storagePath=null,GeometryCalibration? originalGeometry=null,Func<GeometryCalibration,Task>? saveGeometry=null)
    {
        this.originalGeometry=originalGeometry??GeometryCalibration.Identity;
        this.saveGeometry=saveGeometry??(_=>throw new IOException("3D 보정을 지원하는 ECS가 필요합니다."));
        this.data=data;this.live=live;this.send=send;this.original=original.ToArray();installation=Installation();
        path=storagePath??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AadcCommandCenter","calibration-session.json");
        Title="FCS 방향 맞추기 · 캘리브레이션";Width=1080;Height=820;MinWidth=780;MinHeight=600;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=new SolidColorBrush(Color.FromRgb(11,21,31));Foreground=Brushes.WhiteSmoke;
        FontFamily=new FontFamily("Malgun Gothic");Focusable=true;
        var root=new StackPanel { Margin=new Thickness(24),Background=Background };
        Content=new ScrollViewer { Content=root,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
        root.Children.Add(Text("FCS 방향 맞추기",28));root.Children.Add(Text("객체를 고르고, 화면 중심에 맞춘 뒤 저장하세요. 보정은 RDR 기준 상대좌표로 계산되어 절대 설치 위치가 바뀌어도 유지됩니다.",14));
        root.Children.Add(progress);root.Children.Add(coverage);
        var body=new Grid { Margin=new Thickness(0,12,0,12) };body.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1.1,GridUnitType.Star) });body.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });root.Children.Add(body);
        scene=new TargetScene(data,()=> (targets.SelectedItem as TargetRecord)?.Id) { Height=390,Margin=new Thickness(0,0,20,0) };body.Children.Add(scene);
        scene.SelectTarget=id=> { if(reference is null&&!review&&!busy)targets.SelectedItem=data.Targets.FirstOrDefault(t=>t.Id==id); };
        var guide=new StackPanel();Grid.SetColumn(guide,1);body.Children.Add(guide);guide.Children.Add(heading);guide.Children.Add(instruction);
        guide.Children.Add(pickPanel);guide.Children.Add(alignPanel);guide.Children.Add(reviewPanel);
        targets.ItemsSource=data.Targets;pickPanel.Children.Add(targets);
        pickPanel.Children.Add(Text("왼쪽 3D 화면의 점을 눌러도 선택할 수 있습니다. 객체가 없다면 이 창을 닫고 ‘라이다 구동’을 먼저 누르세요.",13));
        alignPanel.Children.Add(Text("한 번 누를 때 움직일 각도",13));alignPanel.Children.Add(stepSize);
        alignPanel.Children.Add(Text("멀리 떨어져 있으면 5°~30°로 먼저 옮기고, 가까워지면 0.1°~1°로 맞추세요.",12));
        arm=Button("방향키 조절 켜기",()=> { if(keyboardEnabled)Disarm();else {keyboardEnabled=true;inputEpoch++;Keyboard.Focus(this);Update();}return Task.CompletedTask; },false);
        alignPanel.Children.Add(arm);alignPanel.Children.Add(keyState);
        var arrowGrid=new Grid { HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,6,0,8) };
        for(int i=0;i<3;i++){arrowGrid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(72)});arrowGrid.RowDefinitions.Add(new RowDefinition {Height=new GridLength(44)});}
        foreach(var (label,key,row,col) in new[]{("↑",Key.Up,0,1),("←",Key.Left,1,0),("→",Key.Right,1,2),("↓",Key.Down,2,1)}) {
            var b=new Button { Content=label,FontSize=23,Margin=new Thickness(3),Focusable=false };b.Click+=async(_,_)=>await Nudge(key);arrows.Add(b);Grid.SetRow(b,row);Grid.SetColumn(b,col);arrowGrid.Children.Add(b);
        }
        alignPanel.Children.Add(arrowGrid);alignPanel.Children.Add(feedback);
        alignPanel.Children.Add(Text("실제 FCS 영상을 보면서 맞추세요. 키보드 방향키와 위 버튼은 같은 동작입니다. 길게 눌러도 한 번만 이동합니다. Esc는 추가 입력을 끕니다.",12));
        reviewPanel.Children.Add(result);
        primary=Button("이 객체 맞추기",Primary);primary.Background=new SolidColorBrush(Color.FromRgb(39,157,128));primary.Foreground=Brushes.White;primary.FontSize=17;primary.FontWeight=FontWeights.Bold;guide.Children.Add(primary);
        verify=Button("다음: 다른 객체로 정확도 확인",()=> { _=FitGeometry();validation.IsChecked=true;reference=null;review=false;Disarm();status.Text="계산에 쓰지 않은 가까운 곳·중간·먼 곳의 객체를 6개 이상 맞춰주세요.";return Task.CompletedTask; });guide.Children.Add(verify);
        var back=Button("객체 다시 선택",()=> {reference=null;review=false;applied=false;Disarm();return Task.CompletedTask;});guide.Children.Add(back);
        root.Children.Add(new Border { Background=new SolidColorBrush(Color.FromRgb(23,40,53)),Padding=new Thickness(12),Child=status });
        var details=new StackPanel();var expander=new Expander { Header="상세 보기 · 저장한 지점 / 이전 작업 / 복원",Foreground=Brushes.LightGray,Content=details,Margin=new Thickness(0,16,0,0) };root.Children.Add(expander);
        table.ItemsSource=samples;
        foreach(var (title,binding) in new[]{("객체","Target"),("X (m)","X"),("Y (m)","Y"),("높이 (m)","Z"),("3D 오차 (°)","ModelError"),("확인용","Validation")})
            table.Columns.Add(new DataGridTextColumn { Header=title,Binding=new System.Windows.Data.Binding(binding) { StringFormat="{0:0.###}" },Width=125 });
        details.Children.Add(table);var management=new WrapPanel();details.Children.Add(management);
        management.Children.Add(Button("저장한 작업 이어하기",LoadSamples));
        management.Children.Add(Button("선택 지점 삭제",()=> { if(table.SelectedItem is CalibrationSample s){int i=samples.IndexOf(s);samples.Remove(s);try{Persist();}catch{samples.Insert(i,s);throw;}fit=null;review=false;applied=false;}return Task.CompletedTask; }));
        management.Children.Add(Button("시작 전 3D 보정으로 복원",async()=> {Check();await this.saveGeometry(this.originalGeometry);applied=false;status.Text="이 창을 열기 전 3D 보정값으로 복원했습니다. 기존 팬·틸트 미세 조정은 그대로입니다.";}));
        management.Children.Add(Button("3D 보정 끄기",async()=> {Check();await this.saveGeometry(GeometryCalibration.Identity);applied=false;status.Text="3D 보정을 껐습니다. 기존 공식과 팬·틸트 미세 조정만 사용합니다.";}));
        management.Children.Add(Button("지점 더 모으기",()=> {validation.IsChecked=false;review=false;applied=false;reference=null;fit=null;return Task.CompletedTask;}));
        details.Children.Add(Text("지점은 RDR 기준 상대좌표로 PC에 자동 저장됩니다. 절대 설치 좌표가 바뀌어도 이어갈 수 있지만, RDR과 FCS의 실제 장착 관계가 바뀌었다면 새 작업을 시작하세요. 0.1°는 통신 명령의 최소 단위이며 모터 엔코더 1틱과는 다릅니다.",12));
        targets.SelectionChanged+=(_,_)=> {if(reference is null)Update();};
        // Changing the increment is part of the same adjustment session.
        // While the combo owns focus, arrow keys navigate choices only.
        stepSize.DropDownClosed+=(_,_)=> {if(keyboardEnabled&&!closed)Keyboard.Focus(this);};
        PreviewKeyDown+=async(_,e)=> {
            if(e.Key==Key.Escape){Disarm();e.Handled=true;return;}
            if(!keyboardEnabled||!IsDirection(e.Key))return;
            // Controls keep their normal navigation; movement must be explicitly re-enabled afterwards.
            if(e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase || targets.IsKeyboardFocusWithin || stepSize.IsKeyboardFocusWithin || table.IsKeyboardFocusWithin)return;
            e.Handled=true;await HandleDirectionKey(e.Key,e.IsRepeat);
        };
        targets.GotKeyboardFocus+=(_,_)=>Disarm();table.GotKeyboardFocus+=(_,_)=>Disarm();
        PreviewGotKeyboardFocus+=(_,e)=> {
            if(e.NewFocus is DependencyObject next && IsStepInput(next))return;
            if(e.NewFocus!=this)Disarm();
        };
        Deactivated+=(_,_)=>Disarm();Closed+=(_,_)=> {closed=true;Disarm();timer.Stop();};
        timer.Tick+=async(_,_)=>await Tick();Loaded+=(_,_)=>timer.Start();
        status.Text=File.Exists(path)?"이전에 저장한 작업이 있습니다. 이어서 하려면 아래 ‘상세 보기’에서 불러오세요.":"먼저 정지한 객체를 선택하세요. 서로 다른 거리와 방향에서 반복하면 더 정확해집니다.";
        Update();
    }
    private Button Button(string text,Func<Task> action,bool disarm=true)
    {
        var b=new Button { Content=text,Padding=new Thickness(12,9,12,9),Margin=new Thickness(0,5,8,5),Focusable=false };
        actions.Add(b);b.Click+=async(_,_)=>await Run(action,disarm);return b;
    }
    private void Update()
    {
        int count=samples.Count(s=>!s.Validation),checks=samples.Count(s=>s.Validation);
        progress.Text=$"① 거리·방향별 수집  →  ② 다른 위치에서 확인  →  ③ 3D 보정 적용     |     수집 {count}/12개 이상 · 확인 {checks}/6개 이상";
        {
            string Range(bool check) {
                var distances=samples.Where(s=>s.Validation==check).Select(s=>Math.Sqrt(s.X*s.X+s.Y*s.Y+s.Z*s.Z)).ToArray();
                return distances.Length==0?"아직 없음":$"{distances.Min():F2}~{distances.Max():F2} m";
            }
            coverage.Text=$"RDR 기준 수집 거리: {Range(false)}   |   별도 확인 거리: {Range(true)}";
        }
        pickPanel.Visibility=!review&&reference is null?Visibility.Visible:Visibility.Collapsed;
        alignPanel.Visibility=!review&&reference is not null?Visibility.Visible:Visibility.Collapsed;
        reviewPanel.Visibility=review?Visibility.Visible:Visibility.Collapsed;
        heading.Text=applied?"완료! 보정값을 저장했습니다":review?"3. 결과를 확인하고 적용하세요":reference is not null?"화면 중심에 객체를 맞춰주세요":validation.IsChecked==true?"2. 다른 위치에서 확인하세요":"1. 맞출 객체를 선택하세요";
        instruction.Text=review?"거리·방향을 반영한 모델입니다. 검증한 범위 밖은 추가 확인이 필요합니다. 적용해도 장치는 움직이지 않습니다.":reference is not null?(validation.IsChecked==true?$"TGT-{reference.Value.Id} · 자동 이동 후 안 맞는 부분만 방향키로 조정하고 저장하세요.":$"TGT-{reference.Value.Id} 선택됨 · 방향키 조절을 켠 뒤 실제 영상을 보며 맞추세요."):validation.IsChecked==true?"새 위치를 선택하고 아래 버튼을 누르면 보정 각도로 자동 이동합니다. 안 맞는 부분만 미세 조정해 6개 이상 확인하세요.":"가까운 곳·중간·먼 곳, 좌우와 높이가 다른 정지 객체를 12개 이상 맞춥니다. 20~30개 수집을 권합니다.";
        primary.Content=applied?"닫기":review?"이 보정값 사용하기":reference is null?(validation.IsChecked==true?"보정된 각도로 이동":"이 객체 맞추기"):"중심에 맞췄어요 · 저장";
        primary.IsEnabled=!busy&&(applied||review&&fit?.CanApply==true||!review&&(reference is not null||targets.SelectedItem is TargetRecord));
        verify.Visibility=!review&&reference is null&&count>=GeometrySolver.MinimumTraining&&validation.IsChecked!=true?Visibility.Visible:Visibility.Collapsed;
        verify.IsEnabled=!busy;arm.Content=keyboardEnabled?"방향키 조절 끄기":"방향키 조절 켜기";
        arm.IsEnabled=!busy&&reference is not null;
        keyState.Text=motionPending?"이동 중 · 도착 확인을 기다려주세요":keyboardEnabled?$"방향키 조절 켜짐 · 한 번에 {StepDegrees:0.0}°":"방향키 조절 꺼짐 · 위 버튼을 눌러 시작하세요";
        foreach(var b in arrows)b.IsEnabled=keyboardEnabled&&!busy&&!motionPending;
        targets.IsEnabled=!busy&&reference is null;
        scene.InvalidateVisual();
    }
    private double StepDegrees=>stepSize.SelectedIndex switch {0=>.1,2=>1,3=>5,4=>10,5=>30,_=>.5};
    private bool IsStepInput(DependencyObject node)
    {
        while(node!=null) {
            if(node==stepSize || node is ComboBoxItem item && ItemsControl.ItemsControlFromItemContainer(item)==stepSize)return true;
            node=node is Visual || node is System.Windows.Media.Media3D.Visual3D?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
    private static bool IsDirection(Key key)=>key is Key.Left or Key.Right or Key.Up or Key.Down;
    private void Disarm(){keyboardEnabled=false;inputEpoch++;if(arm is not null)Update();}
    private void Check(){if(!live())throw new IOException("연결이 끊겼습니다. ECS 연결을 확인한 뒤 다시 시도하세요.");}
    private async Task Run(Func<Task> action,bool disarm=true)
    {
        if(busy||closed)return;if(disarm)Disarm();busy=true;foreach(var b in actions)b.IsEnabled=false;Update();
        await io.WaitAsync();
        try{if(!closed)await action();}catch(Exception e){status.Text=e.Message;}
        finally{io.Release();busy=false;foreach(var b in actions)b.IsEnabled=true;Update();}
    }
    private async Task Primary()
    {
        if(applied){Close();return;}
        if(review){
            Check();if(fit?.CanApply!=true)throw new IOException("확인 지점의 분포와 오차가 검증 기준을 통과해야 적용할 수 있습니다.");
            var history=Path.Combine(Path.GetDirectoryName(path)!,"calibration-models",DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N")+".json");
            var record=new CalibrationApplication(1,"Prepared",DateTimeOffset.UtcNow,original,installation,originalGeometry,fit,samples.ToList());
            CalibrationStorage.SaveApplication(history,record); // Preserve exactly the evidence used for this application.
            await saveGeometry(fit.Model);applied=true;
            status.Text="3D 보정 저장 완료. 다음 객체 지정부터 적용됩니다. 기존 팬·틸트 미세 조정값은 바꾸지 않았습니다.";
            try{CalibrationStorage.SaveApplication(history,record with {State="Applied"});}
            catch(IOException){status.Text+=" 적용 기록의 상태 갱신에 실패했습니다. Prepared 기록은 남아 있습니다.";}
            catch(UnauthorizedAccessException){status.Text+=" 적용 기록의 상태 갱신 권한이 없습니다. Prepared 기록은 남아 있습니다.";}
        }
        else if(reference is null)await CaptureReference();else await SaveSample();
    }
    private static (float Pan,float Tilt) Angles(PipeResponse r)
    {
        var p=BitConverter.UInt32BitsToSingle(r.FcsId);var t=BitConverter.UInt32BitsToSingle(r.AssignedTargetId);
        if(!float.IsFinite(p)||!float.IsFinite(t)||p<0||p>=360||t< -90||t>90)throw new IOException("장치에서 잘못된 각도를 받았습니다.");return(p,t);
    }
    private async Task<(float Pan,float Tilt)> ReadFeedback()
    {
        var a=Angles(await send(CommandType.CalibrationFeedback,0,0,0));actualPan=a.Pan;actualTilt=a.Tilt;feedbackAt=DateTimeOffset.UtcNow;
        feedback.Text=$"현재 각도   좌우 {actualPan:F1}° / 위아래 {actualTilt:F1}°";motionPending=false;return a;
    }
    private async Task Tick()
    {
        scene.InvalidateVisual();if(busy||closed||!await io.WaitAsync(0))return;
        try{Check();await ReadFeedback();}
        catch(IOException e){feedbackAt=default;feedback.Text="각도 확인 대기 · "+e.Message;if(!live())Disarm();}
        catch(Exception e){feedbackAt=default;feedback.Text=e.Message;Disarm();}
        finally{io.Release();Update();}
    }
    // Explicit discrete commands, no OS auto-repeat and no queued movement.
    internal Task HandleDirectionKey(Key key,bool repeat)=>repeat?Task.CompletedTask:Nudge(key);
    internal async Task Nudge(Key key)
    {
        if(!IsDirection(key)||!keyboardEnabled||reference is null||busy||motionPending||closed)return;
        int epoch=inputEpoch;double step=StepDegrees;
        await Run(async()=> {
            Check();if(epoch!=inputEpoch||!keyboardEnabled||closed)return;
            var a=await ReadFeedback();
            if(epoch!=inputEpoch||!keyboardEnabled||closed)return;
            var t=data.Targets.FirstOrDefault(t=>t.Id==reference?.Id);
            if(t is null||!t.CanAssign)throw new IOException("선택한 객체를 관측할 수 없습니다. 객체를 다시 선택하세요.");
            double pan=a.Pan+(key==Key.Right?step:key==Key.Left?-step:0),tilt=a.Tilt+(key==Key.Up?step:key==Key.Down?-step:0);
            pan=(Math.Round(pan*10,MidpointRounding.AwayFromZero)/10+360)%360;
            tilt=Math.Round(tilt*10,MidpointRounding.AwayFromZero)/10;
            if(tilt< -30||tilt>90)throw new IOException("위아래 이동 범위(-30°~90°)의 끝입니다. 반대 방향으로 조절하세요.");
            await send(CommandType.CalibrationPoint,0,(float)pan,(float)tilt);
            motionPending=true;feedbackAt=default;status.Text=$"{step:0.0}° 이동 요청 · 도착 후 다음 방향키를 눌러주세요.";
        },false);
    }
    private async Task CaptureReference()
    {
        Check();var t=targets.SelectedItem as TargetRecord??throw new IOException("맞출 객체를 먼저 선택하세요.");if(!t.CanAssign)throw new IOException("현재 보이는 객체를 선택하세요.");
        var local=RdrLocal(t.LidarX,t.LidarY,t.LidarZ);
        var a=Angles(await send(CommandType.CalibrationReference,t.Id,0,0));reference=(t.Id,local.X,local.Y,local.Z,a.Pan,a.Tilt,DateTimeOffset.UtcNow);
        if(validation.IsChecked==true) {
            try { await PreviewValidation(); }
            catch { reference=null;Disarm();throw; }
        } else status.Text="방향키 조절을 켜고 실제 영상의 중심에 객체를 맞추세요. 맞춘 뒤 ‘중심에 맞췄어요 · 저장’을 누르세요.";
        Update();
    }
    // Preview the candidate model without installing it or changing any saved observations.
    private async Task PreviewValidation()
    {
        var candidate=FitGeometry();var r=reference!.Value;
        var goal=candidate.Model.Angles(new[]{r.X,r.Y,r.Z},original.Select(x=>(double)x).ToArray());
        double pan=(Math.Round(goal.Pan*10,MidpointRounding.AwayFromZero)/10+360)%360;
        double tilt=Math.Round(goal.Tilt*10,MidpointRounding.AwayFromZero)/10;
        if(!double.IsFinite(pan)||!double.IsFinite(tilt)||tilt< -30||tilt>90)throw new IOException("예측 각도가 이동 범위를 벗어났습니다. 다른 객체를 선택하세요.");
        int epoch=inputEpoch;
        void Guard() {
            Check();if(closed||epoch!=inputEpoch)throw new IOException("자동 이동을 중단했습니다. 객체를 다시 선택하세요.");
            var t=data.Targets.FirstOrDefault(t=>t.Id==r.Id);
            var local=t is null?default:RdrLocal(t.LidarX,t.LidarY,t.LidarZ);
            if(t is null||!t.CanAssign||Math.Sqrt(Math.Pow(local.X-r.X,2)+Math.Pow(local.Y-r.Y,2)+Math.Pow(local.Z-r.Z,2))>.05)
                throw new IOException("객체가 움직였거나 사라져 자동 이동을 중단했습니다. 다시 선택하세요.");
        }
        var current=await ReadFeedback();
        // Respect the existing 30-degree command bound, with confirmed arrival between commands.
        for(int segment=0;segment<12;segment++) {
            Guard();double dp=CalibrationMath.Wrap(pan-current.Pan),dt=tilt-current.Tilt;
            if(Math.Abs(dp)<=.15&&Math.Abs(dt)<=.15) {
                stepSize.SelectedIndex=0;keyboardEnabled=true;inputEpoch++;Keyboard.Focus(this);
                status.Text=$"보정 각도 도착 · 좌우 {pan:F1}° / 위아래 {tilt:F1}°. 맞으면 바로 저장하고, 다르면 방향키로 미세 조정하세요.";
                return;
            }
            float nextPan=(float)((Math.Round((current.Pan+Math.Clamp(dp,-30,30))*10)/10+360)%360);
            float nextTilt=(float)(Math.Round((current.Tilt+Math.Clamp(dt,-30,30))*10)/10);
            status.Text="보정된 각도로 자동 이동 중… Esc 또는 창 전환으로 다음 이동을 중단할 수 있습니다.";Update();
            await send(CommandType.CalibrationPoint,0,nextPan,nextTilt);motionPending=true;feedbackAt=default;
            var deadline=DateTimeOffset.UtcNow.AddSeconds(20);
            while(true) {
                Guard();
                if(DateTimeOffset.UtcNow>deadline)throw new IOException("도착 확인 시간이 초과되었습니다. 객체를 다시 선택하세요.");
                try {
                    current=await ReadFeedback();
                    if(Math.Abs(CalibrationMath.Wrap(current.Pan-nextPan))<=.15&&Math.Abs(current.Tilt-nextTilt)<=.15)break;
                } catch(IOException) { Guard(); }
                await Task.Delay(200);
            }
        }
        throw new IOException("자동 이동을 완료하지 못했습니다. 객체를 다시 선택하세요.");
    }
    private async Task SaveSample()
    {
        Check();var r=reference??throw new IOException("객체를 먼저 선택하세요.");var t=data.Targets.FirstOrDefault(t=>t.Id==r.Id);
        if(t is null||!t.CanAssign||DateTimeOffset.UtcNow-r.Time>TimeSpan.FromMinutes(5))throw new IOException("객체가 사라졌거나 선택 후 5분이 지났습니다. ‘객체 다시 선택’을 누르세요.");
        var local=RdrLocal(t.LidarX,t.LidarY,t.LidarZ);
        var now=Angles(await send(CommandType.CalibrationReference,r.Id,0,0));
        if(Math.Sqrt(Math.Pow(local.X-r.X,2)+Math.Pow(local.Y-r.Y,2)+Math.Pow(local.Z-r.Z,2))>.05||Math.Abs(CalibrationMath.Wrap(now.Pan-r.Pan))>.2||Math.Abs(now.Tilt-r.Tilt)>.2)
            throw new IOException("선택한 객체가 움직였습니다. ‘객체 다시 선택’을 누르고 다시 맞춰주세요.");
        var actual=await ReadFeedback();var s=new CalibrationSample(DateTimeOffset.UtcNow,r.Id,r.X,r.Y,r.Z,r.Pan,r.Tilt,actual.Pan,actual.Tilt,validation.IsChecked==true);
        samples.Add(s);fit=null;try{Persist();}catch{samples.Remove(s);throw;}reference=null;Disarm();
        if(s.Validation){
            if(samples.Count(x=>x.Validation)>=GeometrySolver.MinimumValidation){Calculate();if(!fit!.CanApply)review=false;}
            else status.Text=$"확인 지점 {samples.Count(x=>x.Validation)}/6개 저장. 가까운 곳·중간·먼 곳의 다른 위치를 계속 맞춰주세요.";
        }else status.Text=$"저장했습니다! 가까운 곳·중간·먼 곳과 높이가 다른 위치도 맞춰주세요. 현재 {samples.Count(x=>!x.Validation)}개입니다.";Update();
    }
    private void Calculate()
    {
        fit=FitGeometry();review=true;
        for(int i=0;i<samples.Count;i++)samples[i].ModelError=fit.Errors[i];table.Items.Refresh();
        result.Text=$"확인 지점 오차  {fit.ValidationRms:F2}°  (보정 전 {fit.BaselineRms:F2}°)\n가장 큰 오차 {fit.Maximum:F2}°\n\n수집 거리 {fit.MinimumDistance:F2}~{fit.MaximumDistance:F2} m\n좌우 분포 {fit.PanSpan:F1}° / 높이 분포 {fit.TiltSpan:F1}°\n\n기존 팬·틸트 값은 유지됩니다.";
        status.Text=$"검증 오차 {fit.ValidationRms:F2}° · "+fit.Guidance;
    }
    private GeometryFit FitGeometry()
    {
        return GeometrySolver.Fit(samples,new double[3],original.Select(x=>(double)x).ToArray());
    }
    private Task LoadSamples()
    {
        var archive=JsonSerializer.Deserialize<CalibrationArchive>(File.ReadAllText(path))??throw new IOException("저장된 작업이 없습니다.");
        if(archive.Version is not (1 or 2 or 3))throw new IOException("지원하지 않는 이전 작업 형식입니다.");
        samples.Clear();
        foreach(var sample in RdrLocalSamples(archive))samples.Add(sample);
        fit=null;reference=null;review=false;applied=false;validation.IsChecked=samples.Any(s=>s.Validation);
        if(samples.Count(s=>!s.Validation)>=GeometrySolver.MinimumTraining&&samples.Count(s=>s.Validation)>=GeometrySolver.MinimumValidation)Calculate();else status.Text=validation.IsChecked==true?$"{samples.Count}개 지점을 RDR 상대좌표로 불러왔습니다. 새 객체를 선택해 보정 각도로 이동하고 확인을 이어가세요.":$"{samples.Count}개 지점을 RDR 상대좌표로 불러왔습니다. 3D 모델에 필요한 위치를 추가로 모아주세요.";return Task.CompletedTask;
    }
    internal static IReadOnlyList<CalibrationSample> RdrLocalSamples(CalibrationArchive archive)
    {
        double oldRdrX=archive.Installation.ElementAtOrDefault(0),oldRdrY=archive.Installation.ElementAtOrDefault(1),oldRdrZ=archive.Installation.ElementAtOrDefault(2);
        return archive.Samples.Select(stored=>archive.Version>=3?stored:
            stored with {X=stored.X-oldRdrX,Y=stored.Y-oldRdrY,Z=stored.Z-oldRdrZ}).ToArray();
    }
    private void Persist()=>CalibrationStorage.Save(path,new(3,original,Installation(),samples.ToList(),originalGeometry));
}

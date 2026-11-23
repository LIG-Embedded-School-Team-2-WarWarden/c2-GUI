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
        var app = new Application();
        Check(LidarEmissionStatus.Text(true,null)=="구동 중 / 방출 상태 미확인", "unknown was confused with OFF");
        Check(LidarEmissionStatus.Text(true,false).Contains("OFF 확인"), "confirmed OFF missing");
        Check(LidarEmissionStatus.Text(true,true).Contains("ON 확인"), "confirmed ON missing");
        Check(LidarEmissionStatus.ThermalText(7,false).Contains("중단하지 않음"), "warning must be nonblocking");
        Check(LidarEmissionStatus.ThermalText(8,false).Contains("주파수 저하"), "down-frequency classification");
        Check(LidarEmissionStatus.ThermalText(9,false).Contains("보호 정지"), "high-temperature protection");
        Check(LidarEmissionStatus.ThermalText(null,false).Contains("미확인"), "unknown must not look normal");
        Check(LidarEmissionStatus.ThermalText(3,true).Contains("자동 재개 안 함"), "protection must survive cooling");
        var window = new MainWindow();
        try
        {
            var commandType = typeof(MainWindow).Assembly.GetType("AadcCommandCenter.CommandType")!;
            Check(Convert.ToUInt32(Enum.Parse(commandType, "StartContinuousPhotos")) == 1210 &&
                Convert.ToUInt32(Enum.Parse(commandType, "GetContinuousPhotoStatus")) == 1211 &&
                Convert.ToUInt32(Enum.Parse(commandType, "StopContinuousPhotos")) == 1212,
                "continuous photo IPC command contract changed");
            CheckContinuousPhotoResponses(commandType);
            CheckTargetAngleEditor();
            Check(window.FindName("ContinuousPhotoButton") is Button &&
                window.FindName("ContinuousPhotoStatus") is TextBlock,
                "continuous photo controls missing from assignment panel");
            var apply = typeof(MainWindow).GetMethod("ApplyFrame", BindingFlags.NonPublic|BindingFlags.Instance)!;
            typeof(MainWindow).GetField("_isSharedMemoryLive",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,true);
            var grid = (DataGrid)window.FindName("TargetGrid");
            SharedFrame Frame(byte status, byte valid, uint id=7) => new() { payload = new SharedPayload {
                rdrCount=1, fcsCount=1, selectedRdrId=1, selectedFcsId=1, targetCount=1,
                rdrAssets=new[]{new SharedRdrAsset { rdrId=1,isEnabled=1,isOperating=1,isConnected=1,azimuthDeg=135,fieldOfViewDeg=90 }},
                fcsAssets=new[]{new SharedFcsAsset { fcsId=1,isEnabled=1,isOperating=1,isConnected=1 }},
                targets=new[]{new SharedTarget { targetId=id,rdrId=1,lidarX=1,lidarY=3,lidarZ=.5f,distanceM=3.2f,status=status,valid=valid }} }};
            apply.Invoke(window,new object[]{Frame(0,1)});
            void SetThermal(uint? state, bool protection, long age = 0) {
                typeof(MainWindow).GetField("_rdrThermalState",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,state);
                typeof(MainWindow).GetField("_rdrThermalStop",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,protection);
                typeof(MainWindow).GetField("_rdrThermalAt",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,Environment.TickCount64-age);
                typeof(MainWindow).GetMethod("UpdateRdrEmissionLine",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,null);
            }
            var thermalLine=(TextBlock)window.FindName("RdrThermalLine");
            SetThermal(7,false);Check(thermalLine.Text.Contains("중단하지 않음"),"warning view missing");
            SetThermal(9,true);Check(thermalLine.Text.Contains("보호 정지"),"protection view missing");
            SetThermal(7,false,3000);Check(thermalLine.Text.Contains("미확인"),"stale temperature retained");
            SetThermal(7,false);
            var original=(TargetRecord)grid.Items[0]; original.SelectedFcsId=1;
            Check(original.CanAssign,"observed should be usable");
            apply.Invoke(window,new object[]{Frame(4,0)});
            Check(grid.Items.Count==1 && ReferenceEquals(original,grid.Items[0]),"paused row identity lost");
            Check(original.StatusText=="관측 일시중지" && !original.CanAssign && original.LidarX==1,"paused display/control mismatch");
            var assignment=new AssignmentView(7,1);
            var setStatus = typeof(AssignmentView).GetMethod("SetCommandStatus", BindingFlags.NonPublic | BindingFlags.Instance)!;
            setStatus.Invoke(assignment, new object[] { Enum.Parse(setStatus.GetParameters()[0].ParameterType, "Confirmed"), 0u });
            bool CanAim() => assignment.Stages.Any(stage => stage.ActionLabel == "조준" && stage.CanAdvance);
            assignment.Refresh(original,new FcsAsset(1,true,true,true,false,180,0,0,0,0));
            Check(!CanAim(), "paused target allowed manual aim");
            if (Environment.GetEnvironmentVariable("RETENTION_RENDER") is string output)
            {
                typeof(MainWindow).GetMethod("UpdateRdrEmissionLine", BindingFlags.NonPublic|BindingFlags.Instance)!
                    .Invoke(window,null);
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
            ((DashboardData)window.DataContext).Assignments.Add(assignment);
            FrameFreshnessChecks.Run(s => connection.Invoke(window,new object[]{s}),
                f => apply.Invoke(window,new object[]{f}), () => grid.Items.Count, CanAim);
            Check(!CanAim() && assignment.LidarXText == "—", "offline assignment remained usable");
            Check(((TextBlock)window.FindName("RdrEmissionLine")).Text.Contains("확인 불가"), "offline emission status retained");
            Check(((TextBlock)window.FindName("RdrThermalLine")).Text.Contains("연결 대기"),"offline thermal status retained");
            Check(assignment.Stages[^1].ActionLabel is null, "offline result button remained visible");
            var manualStatus = typeof(AssignmentView).GetMethod("SetManualStatus", BindingFlags.NonPublic|BindingFlags.Instance)!;
            void Manual(AssignmentView view, string stateName, string reasonName, uint step) => manualStatus.Invoke(view,
                new object[] { Enum.Parse(manualStatus.GetParameters()[0].ParameterType,stateName),
                    Enum.Parse(manualStatus.GetParameters()[1].ParameterType,reasonName),step });
            var restored = new AssignmentView(7,1);
            Manual(restored,"Failed","ResultUnconfirmed",7);
            restored.Refresh(null,null);
            Check(restored.Stages.Take(4).All(s=>s.Status=="완료"),"reconnected failure lost completed stages");
            Check(restored.StateText.Contains("재관측 시간 초과") && restored.Stages[^1].CanAdvance,"failure reason/cancel missing");
            Check(!restored.Stages.Take(4).Any(s=>s.CanAdvance),"failed run enabled a stage command");
            Manual(restored,"Failed","StatusPending",6);restored.Refresh(null,null);
            Check(restored.Stages.Take(3).All(s=>s.Status=="완료")&&restored.Stages[3].Status=="중단","post-fire failure progress wrong");
            Check(restored.StateText.Contains("FCS"),"failure reason not distinguished");
            Manual(restored,"Failed","LidarThermalFault",4);Check(restored.StateText.Contains("고온 오류"),"thermal reason lost");
            Manual(restored,"Failed","LidarStopWarningUnconfirmed",4);Check(restored.StateText.Contains("온도 경고"),"warning reason lost");
            Manual(restored,"Confirmed","Success",2);restored.Refresh(null,null);
            Check(!restored.Stages.Any(s=>s.Status=="완료"),"retry kept old completion");
            Check(restored.CanEditAimAngles,"confirmed target angle was not editable");
            Manual(restored,"Aimed","Success",3);restored.Refresh(null,null);
            Check(restored.CanEditAimAngles,"aimed target angle could not be corrected");
            Manual(restored,"ScanPaused","Success",4);restored.Refresh(null,null);
            Check(restored.CanEditAimAngles,"paused target angle could not be corrected");
            Manual(restored,"ManualAiming","Success",2);restored.Refresh(null,null);
            Check(!restored.CanEditAimAngles,"angle editor remained enabled while FCS was moving");
            Manual(restored,"Confirmed","Success",2);restored.Refresh(null,null);
            typeof(AssignmentView).GetProperty("IsBatch", BindingFlags.NonPublic|BindingFlags.Instance)!
                .SetValue(restored, true);
            typeof(AssignmentView).GetProperty("BatchFlags", BindingFlags.NonPublic|BindingFlags.Instance)!
                .SetValue(restored, 13u);
            Check(restored.StageCommandsEnabled && restored.CanEditAimAngles,
                "paused assignment was not ready before continuous photo lock");
            typeof(AssignmentView).GetProperty("ContinuousPhotosActive",
                BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(restored, true);
            Check(!restored.StageCommandsEnabled && !restored.CanUsePrimaryAction &&
                !restored.CanEditAimAngles && !restored.CanUseListAction,
                "continuous photos did not lock row controls");
            typeof(AssignmentView).GetProperty("ContinuousPhotosActive",
                BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(restored, false);
            Check(restored.StageCommandsEnabled && restored.CanEditAimAngles,
                "row controls did not recover after continuous photos");
            typeof(AssignmentView).GetProperty("IsBatch", BindingFlags.NonPublic|BindingFlags.Instance)!
                .SetValue(restored, false);
            typeof(AssignmentView).GetProperty("BatchFlags", BindingFlags.NonPublic|BindingFlags.Instance)!
                .SetValue(restored, 0u);
            var assignmentGrid = (DataGrid)window.FindName("AssignmentGrid");
            var actionColumn = assignmentGrid.Columns.OfType<DataGridTemplateColumn>()
                .Single(column => Equals(column.Header, "조작"));
            var actionButton = (Button)actionColumn.CellTemplate.LoadContent();
            actionButton.DataContext = restored;
            void CheckCaptureButton(string label, bool enabled, string background)
            {
                actionButton.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
                Check(Equals(actionButton.Content, label) && actionButton.IsEnabled == enabled &&
                    actionButton.Background is SolidColorBrush brush &&
                    brush.Color == (Color)ColorConverter.ConvertFromString(background),
                    $"capture button mismatch for {label}: {actionButton.Content}, {actionButton.IsEnabled}, {actionButton.Background}");
            }

            var setPending = typeof(AssignmentView).GetMethod("SetPendingStageCommand", BindingFlags.NonPublic | BindingFlags.Instance)!;
            foreach (var stopCommand in new[] { "StopManualCapture", "StopBatchCapture" })
            {
                Manual(restored,"Firing","Success",5);restored.Refresh(null,null);
                CheckCaptureButton("중지", true, "#6B4A0D");
                setPending.Invoke(restored, new[] { Enum.Parse(
                    Nullable.GetUnderlyingType(setPending.GetParameters()[0].ParameterType)!, stopCommand) });
                CheckCaptureButton("중지 확인 중", false, "#303437");
                setPending.Invoke(restored, new object?[] { null });
                CheckCaptureButton("중지", true, "#6B4A0D");
            }
            Manual(restored,"StoppingCapture","Success",5);restored.Refresh(null,null);
            CheckCaptureButton("중지 확인 중", false, "#303437");
            Check(!restored.Stages[3].CanAdvance && restored.TargetId == 7,
                "stopping must keep the assignment and wait for confirmed return");
            Manual(restored,"Failed","StatusPending",5);restored.Refresh(null,null);
            Check(!Equals(actionButton.Content, "촬영 종료") && !actionButton.IsEnabled,
                "a failed stop must not look like confirmed capture completion");
            Manual(restored,"FireConfirmed","Success",6);restored.Refresh(null,null);
            CheckCaptureButton("촬영 종료", false, "#303437");
            Check(restored.Stages[3].CanAdvance&&restored.Stages[3].Status=="재개 대기","manual resume unavailable");
            Manual(restored,"TargetStillPresent","Success",8);restored.Refresh(null,null);
            Check(restored.Stages[^1].ActionLabel=="할당 해제"&&restored.Stages[^1].CanAdvance,"missing ID must offer release instead of broken retry");
            Check(restored.Stages[^1].Status.Contains("이전 ID 없음"),"missing ID explanation absent");
            apply.Invoke(window,new object[]{Frame(0,1)});
            restored.Refresh((TargetRecord)grid.Items[0],null);
            Check(restored.Stages[^1].ActionLabel=="다시 시도","same-ID retry must remain available");
            apply.Invoke(window,new object[]{Frame(1,0)});
            var hit=(TargetRecord)grid.Items[0];
            Check(hit.StatusText=="HIT" && !hit.CanAssign && hit.LidarX==1,"ECS HIT display failed");
            apply.Invoke(window,new object[]{Frame(0,1,8)});
            Check(grid.Items.Count==1 && ((TargetRecord)grid.Items[0]).Id==8,"GUI retained an ECS-evicted HIT");
            apply.Invoke(window,new object[]{Frame(1,0)});
            connection.Invoke(window,new object[]{"WAITING"});
            Check(grid.Items.Count==0,"GUI cached HIT after ECS disconnected");
            Console.WriteLine("PASS: WPF ApplyFrame paused/reacquiring visibility, row continuity, command gates, new ID and ordinary loss");
        }
        finally { window.Close(); app.Shutdown(); }
    }

    static void CheckContinuousPhotoResponses(Type commandType)
    {
        var windowType = typeof(MainWindow);
        var responseType = windowType.Assembly.GetType("AadcCommandCenter.PipeResponse")!;
        var resultType = windowType.Assembly.GetType("AadcCommandCenter.ResultCode")!;
        var parser = windowType.GetMethod("TryReadContinuousPhotoResponse",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        object Response(string command, uint result, uint total, uint phase, uint confirmed)
        {
            object response = Activator.CreateInstance(responseType)!;
            void Field(string name, uint value) => responseType.GetField(name)!.SetValue(response, value);
            Field("CommandType", Convert.ToUInt32(Enum.Parse(commandType, command)));
            Field("ResultCode", result);
            Field("FcsId", 1);
            Field("AssignedTargetId", total);
            Field("AssignmentState", phase);
            Field("FireState", confirmed);
            Field("IsConnected", 1);
            return response;
        }
        bool Parse(object response, string command, int previous, int? total, out object? state)
        {
            var args = new object?[] { response, Enum.Parse(commandType, command), previous, total, null, null };
            bool accepted = (bool)parser.Invoke(null, args)!;
            state = args[4];
            return accepted;
        }

        uint busy = Convert.ToUInt32(Enum.Parse(resultType, "DeviceBusy"));
        Check(Parse(Response("StartContinuousPhotos", busy, 0, 0, 0),
                "StartContinuousPhotos", 0, null, out var rejected) && rejected != null,
            "authoritative rejected start was not accepted");
        Check(Parse(Response("StopContinuousPhotos", 0, 0, 0, 0),
                "StopContinuousPhotos", 5, 9, out _),
            "authoritative idle response after confirmed frames was rejected");
        Check(!Parse(Response("GetContinuousPhotoStatus", 0, 9, 1, 4),
                "GetContinuousPhotoStatus", 5, 9, out _),
            "confirmed photo count was allowed to move backwards");
        Check(!Parse(Response("GetContinuousPhotoStatus", 0, 18, 1, 5),
                "GetContinuousPhotoStatus", 5, 9, out _),
            "planned photo total was allowed to change while running");
    }

    static void CheckTargetAngleEditor()
    {
        var type = typeof(MainWindow).Assembly.GetType("AadcCommandCenter.TargetAngleEditorWindow")!;
        var valid = type.GetMethod("IsValidAngles", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool Valid(float azimuth, float elevation) => (bool)valid.Invoke(null, new object[] { azimuth, elevation })!;
        Check(Valid(0, -30) && Valid(359.9f, 90) && !Valid(360, 0) && !Valid(20, -30.1f),
            "operator angle limits changed");

        var editor = (Window)Activator.CreateInstance(type, BindingFlags.NonPublic | BindingFlags.Instance,
            null, new object[] { 7u, 135f, -5f }, null)!;
        try
        {
            IEnumerable<object> Children(object parent) => parent switch
            {
                Panel panel => panel.Children.Cast<object>().SelectMany(child => new[] { child }.Concat(Children(child))),
                ContentControl content when content.Content is not null =>
                    new[] { content.Content }.Concat(Children(content.Content)),
                _ => Enumerable.Empty<object>()
            };
            Check(Children(editor).OfType<Button>().Any(button => Equals(button.Content, "자동 계산 복원")),
                "automatic ECS angle restore control missing");
        }
        finally { editor.Close(); }
    }
}

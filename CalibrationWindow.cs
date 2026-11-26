using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AadcCommandCenter;

public partial class MainWindow
{
    private async void CalibrationClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_isSharedMemoryLive || _data.FcsAssets.FirstOrDefault() is not { IsConnected:true })
                throw new IOException("ECS와 FCS 연결을 확인하세요.");
            if (_data.Assignments.Any(a => a.HasAssignment)) throw new IOException("타겟 할당을 해제한 뒤 캘리브레이션을 시작하세요.");
            var offsets=GetFcsAngleOffsets();
            uint geometryToken=(uint)Random.Shared.Next(1,int.MaxValue);
            async Task<PipeResponse> GeometryRequest(CommandType type,double[] values)
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var response=await _pipeClient.SendAsync(type,geometryToken,cancellationToken:timeout.Token,
                    positionX:(float)values[0],positionY:(float)values[1],positionZ:(float)values[2]);
                if(response.ResultCode!=0)throw new IOException("3D 보정: "+CommandResultText((ResultCode)response.ResultCode));
                return response;
            }
            double[] Triple(PipeResponse response)
            {
                var values=new[]{(double)BitConverter.UInt32BitsToSingle(response.FcsId),BitConverter.UInt32BitsToSingle(response.AssignedTargetId),BitConverter.UInt32BitsToSingle(response.AssignmentState)};
                if(values.Any(v=>!double.IsFinite(v)))throw new IOException("잘못된 3D 보정 응답입니다.");return values;
            }
            var translation=await GeometryRequest(CommandType.ReadGeometryTranslation,new double[3]);
            var rotation=await GeometryRequest(CommandType.ReadGeometryRotation,new double[3]);
            var rdrAnchor=await GeometryRequest(CommandType.ReadGeometryRdrAnchor,new double[3]);
            var fcsAnchor=await GeometryRequest(CommandType.ReadGeometryFcsAnchor,new double[3]);
            var originalGeometry=new GeometryCalibration(translation.FireState==1,Triple(translation),Triple(rotation),
                Triple(rdrAnchor),Triple(fcsAnchor));
            if(!GetFcsAngleOffsets().SequenceEqual(offsets))throw new IOException("각도 보정이 변경되었습니다. 창을 다시 열어주세요.");
            async Task SaveGeometry(GeometryCalibration model)
            {
                await GeometryRequest(CommandType.StageGeometryTranslation,model.Translation);
                await GeometryRequest(CommandType.StageGeometryRotation,model.Rotation);
                await GeometryRequest(CommandType.CommitGeometry,new[]{model.Enabled?1d:0d,0,0});
            }
            async Task<PipeResponse> Send(CommandType command,uint target,float pan,float tilt)
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var r=await _pipeClient.SendAsync(command,target,
                    rdrId:command==CommandType.SetCameraSettings?0u:1u,
                    fcsId:command==CommandType.SetCameraSettings?0u:1u,
                    cancellationToken:timeout.Token,positionX:pan,positionY:tilt);
                if(r.ResultCode!=0) throw new IOException(CommandResultText((ResultCode)r.ResultCode));
                return r;
            }
            var window=new CalibrationWindow(_data,()=>_isSharedMemoryLive,Send,offsets,originalGeometry:originalGeometry,saveGeometry:SaveGeometry) { Owner=this };
            window.ShowDialog();
            await RefreshCameraSettingsAsync();
        }
        catch(Exception error) { ShowAlert(error.Message,"캘리브레이션"); }
    }
}

using System.IO;
using System.Text.Json;

namespace AadcCommandCenter;

public sealed record CalibrationSample(DateTimeOffset Time, uint Target, double X, double Y, double Z,
    double BasePan, double BaseTilt, double ActualPan, double ActualTilt, bool Validation)
{
    public double? ModelError { get; set; }
    public double PanOffset => CalibrationMath.Wrap(ActualPan - BasePan);
    public double TiltOffset => ActualTilt - BaseTilt;
}
public sealed record CalibrationFit(double Pan, double Tilt, double TrainingRms, double ValidationRms, double Maximum);
public static class CalibrationMath
{
    public static double Wrap(double a) => ((a + 180) % 360 + 360) % 360 - 180;
    public static CalibrationFit Fit(IEnumerable<CalibrationSample> source)
    {
        var all = source.ToArray(); var train = all.Where(s => !s.Validation).ToArray();
        if (train.Length < 3) throw new InvalidOperationException("서로 다른 위치에서 학습 지점을 3개 이상 저장하세요.");
        var distinct=new List<CalibrationSample>();
        foreach(var s in train)
            if(distinct.All(p=>Math.Sqrt(Math.Pow(s.X-p.X,2)+Math.Pow(s.Y-p.Y,2)+Math.Pow(s.Z-p.Z,2))>.05)) distinct.Add(s);
        if(distinct.Count<3) throw new InvalidOperationException("서로 5cm 이상 떨어진 학습 위치가 3개 이상 필요합니다.");
        if (all.Any(s => new[] { s.X,s.Y,s.Z,s.BasePan,s.BaseTilt,s.ActualPan,s.ActualTilt }.Any(v => !double.IsFinite(v))))
            throw new InvalidOperationException("유효하지 않은 측정값입니다.");
        var sin = train.Average(s => Math.Sin(s.PanOffset * Math.PI / 180));
        var cos = train.Average(s => Math.Cos(s.PanOffset * Math.PI / 180));
        if (Math.Sqrt(sin*sin+cos*cos) < .9) throw new InvalidOperationException("팬 오차가 일관되지 않습니다. 기준점과 좌표계를 확인하세요.");
        double pan = Math.Atan2(sin,cos)*180/Math.PI, tilt = train.Average(s => s.TiltOffset);
        if (Math.Abs(tilt)>90) throw new InvalidOperationException("틸트 보정 범위를 벗어났습니다.");
        double Error(CalibrationSample s) => Math.Pow(Wrap(s.PanOffset-pan),2)+Math.Pow(s.TiltOffset-tilt,2);
        var validation = all.Where(s => s.Validation).ToArray();
        return new(pan,tilt,Math.Sqrt(train.Average(Error)),validation.Length==0?double.NaN:Math.Sqrt(validation.Average(Error)),Math.Sqrt(all.Max(Error)));
    }
}
public sealed record CalibrationArchive(int Version, float[] OriginalOffsets, float[] Installation, List<CalibrationSample> Samples,GeometryCalibration? Geometry=null);
public sealed record CalibrationApplication(int Version,string State,DateTimeOffset RecordedUtc,float[] AngleTrim,
    float[] Installation,GeometryCalibration PreviousModel,GeometryFit Fit,List<CalibrationSample> Samples);
public static class CalibrationStorage
{
    public static void SaveApplication(string path,CalibrationApplication application)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path+".tmp",JsonSerializer.Serialize(application,new JsonSerializerOptions{WriteIndented=true}));
        File.Move(path+".tmp",path,true);
    }
    public static void Save(string path, CalibrationArchive archive)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp=path+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(archive,new JsonSerializerOptions { WriteIndented=true }));
        if(File.Exists(path))File.Copy(path,path+".bak",true);
        File.Move(temp,path,true);
    }
}

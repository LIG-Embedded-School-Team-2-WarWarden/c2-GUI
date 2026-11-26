namespace AadcCommandCenter;

public sealed record GeometryCalibration(bool Enabled,double[] Translation,double[] Rotation,
    double[]? RdrAnchor=null,double[]? FcsAnchor=null)
{
    public static GeometryCalibration Identity=>new(false,new double[3],new double[3]);
    public double[] Rotate(double[] point)
    {
        if(!Enabled)return point.ToArray();
        var r=Rotation;double theta=GeometrySolver.Norm(r);
        double a=theta<1e-8?1-theta*theta/6:Math.Sin(theta)/theta;
        double b=theta<1e-8?.5-theta*theta/24:(1-Math.Cos(theta))/(theta*theta);
        var cross=GeometrySolver.Cross(r,point);var twice=GeometrySolver.Cross(r,cross);
        return Enumerable.Range(0,3).Select(i=>point[i]+a*cross[i]+b*twice[i]).ToArray();
    }
    public double[] Transform(double[] point)
    {
        var rotated=Rotate(point);
        return Enabled?Enumerable.Range(0,3).Select(i=>rotated[i]+Translation[i]).ToArray():rotated;
    }
    public double[] TransformInstalled(double[] point,double[] currentRdr,double[] currentFcs)
    {
        var transformed=Transform(point);
        if(!Enabled||RdrAnchor is not { Length:3 } rdrAnchor||FcsAnchor is not { Length:3 } fcsAnchor)
            return transformed;
        var baselineChange=Enumerable.Range(0,3).Select(i=>(currentRdr[i]-currentFcs[i])-
            (rdrAnchor[i]-fcsAnchor[i])).ToArray();
        var correction=Rotate(baselineChange);
        return Enumerable.Range(0,3).Select(i=>transformed[i]+correction[i]).ToArray();
    }
    public (double Pan,double Tilt) Angles(double[] point,double[] offsets,
        double[]? currentRdr=null,double[]? currentFcs=null)
    {
        var q=currentRdr is { Length:3 } rdr&&currentFcs is { Length:3 } fcs
            ?TransformInstalled(point,rdr,fcs):Transform(point);
        double horizontal=Math.Sqrt(q[0]*q[0]+q[1]*q[1]);
        if(horizontal<.001)throw new InvalidOperationException("타겟이 회전축 바로 위/아래에 있어 방향을 정할 수 없습니다.");
        return ((180+Math.Atan2(q[0],q[1])*180/Math.PI+offsets[0]+720)%360,Math.Atan2(q[2],horizontal)*180/Math.PI+offsets[1]);
    }
}
public sealed record GeometryFit(GeometryCalibration Model,double TrainingRms,double ValidationRms,
    double Maximum,double BaselineRms,double MinimumDistance,double MaximumDistance,double PanSpan,double TiltSpan,
    double Condition,bool CanApply,string Guidance,double[] Errors);

// Six-DOF rigid transform fitted to unit bearings, using damped nonlinear least squares.
// Parameters: q = Rodrigues(rotation) * RDR-local point + translation.
// Absolute map installation coordinates are deliberately outside this model.
// Translation is normalized by median range during fitting for meaningful conditioning.
public static class GeometrySolver
{
    public const int MinimumTraining=12,MinimumValidation=6;
    internal static double Norm(double[] v)=>Math.Sqrt(v.Sum(x=>x*x));
    internal static double[] Cross(double[] a,double[] b)=>new[]{a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]};
    private static double[] Unit(double[] v){double n=Norm(v);if(n<.05||!double.IsFinite(n))throw new InvalidOperationException("회전축에 너무 가까운 타겟 또는 잘못된 좌표입니다.");return v.Select(x=>x/n).ToArray();}
    private static double[] Bearing(CalibrationSample s,double[] offsets)
    {
        double pan=(s.ActualPan-offsets[0]-180)*Math.PI/180,tilt=(s.ActualTilt-offsets[1])*Math.PI/180;
        return new[]{Math.Sin(pan)*Math.Cos(tilt),Math.Cos(pan)*Math.Cos(tilt),Math.Sin(tilt)};
    }
    private static double Angular(double[] a,double[] b)=>Math.Acos(Math.Clamp(a.Zip(b,(x,y)=>x*y).Sum(),-1,1))*180/Math.PI;
    public static GeometryFit Fit(IEnumerable<CalibrationSample> source,double[] origin,double[] offsets)
    {
        var all=source.ToArray();var train=all.Where(s=>!s.Validation).ToArray();var checks=all.Where(s=>s.Validation).ToArray();
        if(origin.Length!=3||offsets.Length<2||origin.Concat(offsets).Any(x=>!double.IsFinite(x)))throw new InvalidOperationException("설치 위치/각도 설정이 잘못되었습니다.");
        if(all.Any(s=>new[]{s.X,s.Y,s.Z,s.ActualPan,s.ActualTilt}.Any(x=>!double.IsFinite(x))))throw new InvalidOperationException("유효하지 않은 측정값이 있습니다.");
        double[] Point(CalibrationSample s)=>new[]{s.X-origin[0],s.Y-origin[1],s.Z-origin[2]};
        double Separation(CalibrationSample a,CalibrationSample b)=>Norm(new[]{a.X-b.X,a.Y-b.Y,a.Z-b.Z});
        var unique=new List<CalibrationSample>();foreach(var s in train)if(unique.All(p=>Separation(s,p)>.05))unique.Add(s);
        if(unique.Count<MinimumTraining)throw new InvalidOperationException($"서로 다른 위치가 {unique.Count}개입니다. 가까운 곳·중간·먼 곳에 걸쳐 {MinimumTraining}개 이상 모아주세요.");
        var xyz=train.Select(Point).ToArray();var measured=train.Select(s=>Bearing(s,offsets)).ToArray();
        var ranges=xyz.Select(Norm).ToArray();double near=ranges.Min(),far=ranges.Max(),scale=ranges.Order().ElementAt(ranges.Length/2);
        var nominal=xyz.Select(p=>GeometryCalibration.Identity.Angles(p,new double[2])).ToArray();
        var pans=nominal.Select(a=>a.Pan).Order().ToArray();
        double gap=360+pans[0]-pans[^1];for(int i=1;i<pans.Length;i++)gap=Math.Max(gap,pans[i]-pans[i-1]);
        double panSpan=360-gap,tiltSpan=nominal.Max(a=>a.Tilt)-nominal.Min(a=>a.Tilt);
        int Bin(double d)=>Math.Clamp((int)((d-near)/(far-near)*3),0,2);
        if(far-near<.5||far/Math.Max(near,.05)<1.5)throw new InvalidOperationException("거리 차이가 부족합니다. 가까운 곳과 먼 곳을 더 모아주세요 (최소 0.5 m 차이, 거리 비율 1.5배).");
        if(Enumerable.Range(0,3).Any(i=>ranges.Count(d=>Bin(d)==i)<2))throw new InvalidOperationException("가까운 곳·중간·먼 곳에 각각 2개 이상이 필요합니다.");
        if(panSpan<20||tiltSpan<8)throw new InvalidOperationException($"방향 분포가 좁습니다 (좌우 {panSpan:F1}°, 높이 {tiltSpan:F1}°). 좌우와 높이가 다른 타겟을 더 모아주세요.");
        GeometryCalibration Model(double[] p)=>new(true,p.Take(3).Select(x=>x*scale).ToArray(),p.Skip(3).ToArray());
        double[] Residual(double[] p)
        {
            var model=Model(p);if(Norm(model.Translation)>100||Norm(model.Rotation)>Math.PI)throw new InvalidOperationException("보정 모델 범위를 벗어났습니다.");
            return xyz.SelectMany((point,i)=>Unit(model.Transform(point)).Zip(measured[i],(a,b)=>a-b)).ToArray();
        }
        double[]? best=null;double bestCost=double.PositiveInfinity;
        // Multiple nearby orientations reduce sensitivity to a single local minimum.
        foreach(var seed in new[]{new double[6],new[]{0d,0,0,.2,0,0},new[]{0d,0,0,-.2,0,0},new[]{0d,0,0,0,0,.2},new[]{0d,0,0,0,0,-.2}})
        {
            var p=seed.ToArray();double lambda=.001;var residual=Residual(p);double cost=residual.Sum(x=>x*x);
            for(int iteration=0;iteration<180;iteration++)
            {
                var j=Jacobian(p,residual,Residual);var h=Normal(j);var gradient=new double[6];
                for(int a=0;a<6;a++){for(int i=0;i<residual.Length;i++)gradient[a]-=j[i,a]*residual[i];h[a,a]+=lambda;}
                var delta=Solve(h,gradient);if(delta is null)break;
                double length=Norm(delta);if(length>.5)delta=delta.Select(x=>x*.5/length).ToArray();
                var candidate=p.Zip(delta,(a,b)=>a+b).ToArray();double[] next;double nextCost;
                try{next=Residual(candidate);nextCost=next.Sum(x=>x*x);}catch(InvalidOperationException){lambda*=10;continue;}
                if(nextCost<cost){double improvement=cost-nextCost;p=candidate;residual=next;cost=nextCost;lambda=Math.Max(1e-10,lambda/3);if(improvement<1e-13||Norm(delta)<1e-8)break;}
                else {lambda*=10;if(lambda>1e12)break;}
            }
            if(cost<bestCost){bestCost=cost;best=p;}
        }
        if(best is null)throw new InvalidOperationException("모델을 계산하지 못했습니다. 기준점을 다시 확인하세요.");
        var modelFit=Model(best);var eigen=Eigenvalues(Normal(Jacobian(best,Residual(best),Residual)));
        double condition=eigen.Max()/Math.Max(1e-30,eigen.Min());
        if(eigen.Min()<=1e-9||condition>1e7)throw new InvalidOperationException("위치·방향을 구분하기 어려운 배치입니다. 거리와 높이가 다른 지점을 추가하세요.");
        double Error(CalibrationSample s)=>Angular(Unit(modelFit.Transform(Point(s))),Bearing(s,offsets));
        double BaseError(CalibrationSample s)=>Angular(Unit(Point(s)),Bearing(s,offsets));
        double rms=Math.Sqrt(train.Average(s=>Math.Pow(Error(s),2)));
        var validChecks=new List<CalibrationSample>();foreach(var s in checks)if(train.All(t=>Separation(s,t)>.05)&&validChecks.All(t=>Separation(s,t)>.05))validChecks.Add(s);
        bool spread=validChecks.Count>=MinimumValidation&&Enumerable.Range(0,3).All(b=>validChecks.Any(s=>Bin(Norm(Point(s)))==b));
        double validation=checks.Length==0?double.NaN:Math.Sqrt(checks.Average(s=>Math.Pow(Error(s),2)));
        double baseline=checks.Length==0?double.NaN:Math.Sqrt(checks.Average(s=>Math.Pow(BaseError(s),2)));
        double maxValidation=checks.Length==0?double.PositiveInfinity:checks.Max(Error);
        bool good=spread&&validation<=1&&maxValidation<=2&&validation<=baseline+.1&&rms<=1&&train.Max(Error)<=2;
        string guidance=!spread?$"계산에 쓰지 않은 확인 지점을 가까운 곳·중간·먼 곳에 걸쳐 {MinimumValidation}개 이상 모아주세요. 현재 독립 확인 지점 {validChecks.Count}개입니다.":
            !good?"검증 오차가 큽니다. 잘못 맞춘 지점을 확인하고 다시 수집하세요 (평균 1° 이하, 최대 2° 이하).":
            "검증 기준을 통과했습니다. 표시된 범위 밖의 거리·방향은 추가 확인이 필요합니다.";
        return new(modelFit,rms,validation,all.Max(Error),baseline,near,far,panSpan,tiltSpan,condition,good,guidance,all.Select(Error).ToArray());
    }
    private static double[,] Jacobian(double[] p,double[] residual,Func<double[],double[]> function)
    {
        var j=new double[residual.Length,6];const double epsilon=1e-5;
        for(int a=0;a<6;a++){var q=p.ToArray();q[a]+=epsilon;var next=function(q);for(int i=0;i<residual.Length;i++)j[i,a]=(next[i]-residual[i])/epsilon;}return j;
    }
    private static double[,] Normal(double[,] j)
    {
        var h=new double[6,6];for(int a=0;a<6;a++)for(int b=0;b<6;b++)for(int i=0;i<j.GetLength(0);i++)h[a,b]+=j[i,a]*j[i,b];return h;
    }
    private static double[]? Solve(double[,] matrix,double[] vector)
    {
        var a=(double[,])matrix.Clone();var b=vector.ToArray();int n=b.Length;
        for(int k=0;k<n;k++){
            int pivot=k;for(int i=k+1;i<n;i++)if(Math.Abs(a[i,k])>Math.Abs(a[pivot,k]))pivot=i;
            if(Math.Abs(a[pivot,k])<1e-15)return null;
            for(int j=k;j<n;j++)(a[k,j],a[pivot,j])=(a[pivot,j],a[k,j]);(b[k],b[pivot])=(b[pivot],b[k]);
            for(int i=k+1;i<n;i++){double factor=a[i,k]/a[k,k];for(int j=k;j<n;j++)a[i,j]-=factor*a[k,j];b[i]-=factor*b[k];}
        }
        var x=new double[n];for(int i=n-1;i>=0;i--){x[i]=b[i];for(int j=i+1;j<n;j++)x[i]-=a[i,j]*x[j];x[i]/=a[i,i];}return x;
    }
    private static double[] Eigenvalues(double[,] input)
    {
        var a=(double[,])input.Clone();for(int iteration=0;iteration<100;iteration++){
            int p=0,q=1;for(int i=0;i<6;i++)for(int j=i+1;j<6;j++)if(Math.Abs(a[i,j])>Math.Abs(a[p,q])){p=i;q=j;}
            if(Math.Abs(a[p,q])<1e-12)break;
            double angle=.5*Math.Atan2(2*a[p,q],a[q,q]-a[p,p]),c=Math.Cos(angle),s=Math.Sin(angle),pp=a[p,p],qq=a[q,q],pq=a[p,q];
            for(int i=0;i<6;i++)if(i!=p&&i!=q){double ip=a[i,p],iq=a[i,q];a[i,p]=a[p,i]=c*ip-s*iq;a[i,q]=a[q,i]=s*ip+c*iq;}
            a[p,p]=c*c*pp-2*s*c*pq+s*s*qq;a[q,q]=s*s*pp+2*s*c*pq+c*c*qq;a[p,q]=a[q,p]=0;
        }return Enumerable.Range(0,6).Select(i=>a[i,i]).ToArray();
    }
}

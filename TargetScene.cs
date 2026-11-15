using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace AadcCommandCenter;

// Orthographic 3D view: depth-sorted points, vertical projections and orbit controls.
internal sealed class TargetScene : FrameworkElement
{
    private readonly DashboardData data;
    private readonly Func<uint?> selected;
    internal Action<uint>? SelectTarget;
    private double yaw=-.6,pitch=.55,zoom=1;
    private Point last,down;
    private bool orbit;
    private readonly List<(Point P,uint Id)> hit=new();
    internal TargetScene(DashboardData data,Func<uint?> selected)
    {
        this.data=data;this.selected=selected;ClipToBounds=true;MinHeight=220;
        MouseLeftButtonDown+=(_,e)=> {orbit=true;last=down=e.GetPosition(this);CaptureMouse();};
        MouseMove+=(_,e)=> {if(!orbit)return;var p=e.GetPosition(this);yaw+=(p.X-last.X)*.008;pitch=Math.Clamp(pitch+(p.Y-last.Y)*.008,.05,1.5);last=p;InvalidateVisual();};
        MouseLeftButtonUp+=(_,e)=> {var p=e.GetPosition(this);orbit=false;ReleaseMouseCapture();if((p-down).Length<5){var nearest=hit.OrderBy(h=>(h.P-p).Length).FirstOrDefault();if((nearest.P-p).Length<18)SelectTarget?.Invoke(nearest.Id);}};
        LostMouseCapture+=(_,_)=>orbit=false;
        MouseWheel+=(_,e)=> {zoom=Math.Clamp(zoom*(e.Delta>0?1.12:1/1.12),.2,8);InvalidateVisual();};
    }
    protected override void OnRender(DrawingContext dc)
    {
        double w=ActualWidth,h=ActualHeight;if(w<1||h<1)return;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(8,16,25)),null,new Rect(0,0,w,h));hit.Clear();
        var points=data.Targets.Where(t=>t.CanAssign).ToArray();
        var fcs=data.FcsAssets.FirstOrDefault();var rdr=data.RdrAssets.FirstOrDefault();
        var all=points.Select(t=>new Point3D(t.LidarX,t.LidarY,t.LidarZ)).ToList();
        if(fcs!=null)all.Add(new(fcs.PositionX,fcs.PositionY,fcs.PositionZ));
        if(rdr!=null)all.Add(new(rdr.PositionX,rdr.PositionY,rdr.PositionZ));
        all.Add(new());
        double cx=(all.Min(p=>p.X)+all.Max(p=>p.X))/2,cy=(all.Min(p=>p.Y)+all.Max(p=>p.Y))/2;
        double extent=Math.Max(1,all.Max(p=>Math.Max(Math.Abs(p.X-cx),Math.Max(Math.Abs(p.Y-cy),Math.Abs(p.Z)))))*1.3;
        double scale=Math.Min(w,h)*.37/extent*zoom;
        (Point Screen,double Depth) Project(double x,double y,double z)
        {
            x-=cx;y-=cy;double a=x*Math.Cos(yaw)-y*Math.Sin(yaw),b=x*Math.Sin(yaw)+y*Math.Cos(yaw);
            return(new(w/2+a*scale,h*.57+(b*Math.Sin(pitch)-z*Math.Cos(pitch))*scale),b*Math.Cos(pitch)+z*Math.Sin(pitch));
        }
        void Label(string text,Point at,Brush color,double size=12)=>dc.DrawText(new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Malgun Gothic"),size,color,VisualTreeHelper.GetDpi(this).PixelsPerDip),at);
        void Line(double x1,double y1,double z1,double x2,double y2,double z2,Brush color,double thickness=1)=>dc.DrawLine(new Pen(color,thickness),Project(x1,y1,z1).Screen,Project(x2,y2,z2).Screen);
        double step=Math.Pow(10,Math.Floor(Math.Log10(extent)));if(extent/step<2)step/=2;
        var grid=new SolidColorBrush(Color.FromRgb(34,53,66));
        for(int i=-8;i<=8;i++) {double k=i*step;Line(k,-8*step,0,k,8*step,0,grid);Line(-8*step,k,0,8*step,k,0,grid);}
        Line(0,0,0,extent,0,0,Brushes.IndianRed,2);Line(0,0,0,0,extent,0,Brushes.LightGreen,2);Line(0,0,0,0,0,extent,Brushes.DodgerBlue,2);
        Label("X 동쪽",Project(extent,0,0).Screen,Brushes.IndianRed);Label("Y 북쪽",Project(0,extent,0).Screen,Brushes.LightGreen);Label("Z 높이",Project(0,0,extent).Screen,Brushes.DodgerBlue);
        void Asset(string name,double x,double y,double z,Brush color) {var p=Project(x,y,z).Screen;dc.DrawRectangle(color,null,new Rect(p.X-5,p.Y-5,10,10));Label(name,new(p.X+8,p.Y),color);}
        if(fcs!=null)Asset("FCS",fcs.PositionX,fcs.PositionY,fcs.PositionZ,Brushes.Gold);
        if(rdr!=null)Asset("RDR",rdr.PositionX,rdr.PositionY,rdr.PositionZ,Brushes.LimeGreen);
        foreach(var t in points.OrderBy(t=>Project(t.LidarX,t.LidarY,t.LidarZ).Depth))
        {
            var p=Project(t.LidarX,t.LidarY,t.LidarZ).Screen;bool active=selected()==t.Id;var color=active?Brushes.Gold:Brushes.Cyan;
            Line(t.LidarX,t.LidarY,0,t.LidarX,t.LidarY,t.LidarZ,color);
            dc.DrawEllipse(null,new Pen(color,1),Project(t.LidarX,t.LidarY,0).Screen,4,2);
            dc.DrawEllipse(color,new Pen(Brushes.White,active?2:1),p,active?7:5,active?7:5);hit.Add((p,t.Id));
            Label($"TGT-{t.Id}"+(active?$"\n({t.LidarX:F2}, {t.LidarY:F2}, {t.LidarZ:F2}) m":""),new(p.X+10,p.Y-12),color);
        }
        Label($"3D 객체 위치 · 바닥 격자 {step:G3} m",new(12,12),Brushes.WhiteSmoke,15);
        Label("드래그: 시점 회전  ·  휠: 확대  ·  객체 클릭: 선택",new(12,h-26),Brushes.LightGray);
    }
}

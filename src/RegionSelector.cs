namespace LightCapture;

sealed class RegionSelector : Form
{
    Point? anchor;
    Point pointer;
    Rectangle selection;
    readonly TaskCompletionSource<Rectangle?> completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public RegionSelector()
    {
        AutoScaleMode=AutoScaleMode.None; FormBorderStyle=FormBorderStyle.None;
        ShowInTaskbar=false; TopMost=true; StartPosition=FormStartPosition.Manual;
        Bounds=SystemInformation.VirtualScreen; BackColor=Color.Black; Opacity=.38;
        Cursor=Cursors.Cross; DoubleBuffered=true; KeyPreview=true;
    }
    public Task<Rectangle?> SelectAsync()
    {
        Show(); Activate(); Focus(); return completion.Task;
    }
    public void Cancel() { completion.TrySetResult(null); Close(); }
    internal static Rectangle Between(Point a,Point b,Rectangle bounds)
    {
        var r=Rectangle.FromLTRB(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y),Math.Max(a.X,b.X),Math.Max(a.Y,b.Y));
        return Rectangle.Intersect(r,bounds);
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.SetWindowDisplayAffinity(Handle,0x11); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if(e.Button==MouseButtons.Right) { Cancel(); return; }
        if(e.Button==MouseButtons.Left) { anchor=Control.MousePosition; Capture=true; }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); pointer=Control.MousePosition;
        if(anchor is {} start) selection=Between(start,pointer,SystemInformation.VirtualScreen);
        Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if(e.Button!=MouseButtons.Left || anchor==null) return;
        selection=Between(anchor.Value,Control.MousePosition,SystemInformation.VirtualScreen); Capture=false;
        if(selection.Width<16 || selection.Height<16) { anchor=null; selection=Rectangle.Empty; Invalidate(); return; }
        completion.TrySetResult(selection); Close();
    }
    protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg,Keys keyData)
    { if(keyData==Keys.Escape) { Cancel(); return true; } return base.ProcessCmdKey(ref msg,keyData); }
    protected override void OnFormClosed(FormClosedEventArgs e) { completion.TrySetResult(null); base.OnFormClosed(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if(!selection.IsEmpty)
        {
            var r=selection; r.Offset(-Left,-Top);
            using var fill=new SolidBrush(Color.White); using var pen=new Pen(Color.Cyan,3);
            e.Graphics.FillRectangle(fill,r); e.Graphics.DrawRectangle(pen,r);
            DrawHint(e.Graphics,$"{r.Width} × {r.Height}  ·  松开开始录制",new Point(r.Left,Math.Max(4,r.Top-38)));
        }
        else
        {
            var p=PointToClient(pointer); p.X=Math.Clamp(p.X,8,Math.Max(8,Width-390)); p.Y=Math.Clamp(p.Y+24,8,Math.Max(8,Height-44));
            DrawHint(e.Graphics,"拖动框选 GIF 区域  ·  Esc / 右键取消",p);
        }
    }
    void DrawHint(Graphics g,string text,Point point)
    {
        using var font=new Font("Microsoft YaHei UI",11,FontStyle.Bold);
        var size=TextRenderer.MeasureText(text,font); var box=new Rectangle(point,new Size(size.Width+16,size.Height+12));
        g.FillRectangle(Brushes.Black,box); TextRenderer.DrawText(g,text,font,new Point(point.X+8,point.Y+6),Color.White);
    }
}

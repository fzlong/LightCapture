using LightCapture;
using System.Drawing.Imaging;
using System.Text.Json;

if(args.FirstOrDefault()=="--desktop-probe")
{
    string output=Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
    var settings=new Settings { SaveFolder=output,GifFps=15,GifMaxSide=640,GifMaxSeconds=10 };
    var recorder=new GifRecorder(settings);
    var screen=Screen.PrimaryScreen!.Bounds;
    await recorder.Start(new Rectangle(screen.Left+20,screen.Top+20,240,160));
    await Task.Delay(1200);
    var saved=await recorder.StopAndExport();
    if(saved==null) throw new Exception("Desktop capture produced no GIF.");
    using var image=Image.FromFile(saved.File);
    if(image.Width!=240||image.Height!=160||image.GetFrameCount(FrameDimension.Time)<2) throw new Exception("Desktop capture dimensions/frames wrong.");
    File.WriteAllText(Path.Combine(output,"desktop-probe.json"),JsonSerializer.Serialize(saved,new JsonSerializerOptions {WriteIndented=true}));
    return;
}

string dir=Path.Combine(Path.GetFullPath(args.FirstOrDefault()??Path.Combine(AppContext.BaseDirectory,"validation")),"run-"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")); Directory.CreateDirectory(dir);
var old=new Settings { Hotkeys=["Ctrl+Z","Alt+F9","Alt+Shift+F10","Alt+F10","Alt+F1","Alt+R"] };
Settings.Upgrade(old);
if(old.Hotkeys.Length!=7||old.Hotkeys[0]!="Ctrl+Z"||old.Hotkeys[6]!="Alt+F2") throw new Exception("Old hotkey migration failed.");
if(RegionSelector.Between(new(100,200),new(-400,-100),new(-1920,-1080,3840,2160))!=new Rectangle(-400,-100,500,300)) throw new Exception("Reverse/multi-monitor region geometry failed.");
var s=new Settings { GifFps=15,GifMaxSide=640,GifMaxSeconds=60 };
var capture=GifRecorder.CaptureArguments(new(-400,20,300,200),s,"out.mkv");
if(capture[Array.IndexOf(capture,"-offset_x")+1]!="-400") throw new Exception("Negative display coordinates lost.");
Console.WriteLine("PASS: configuration migration and reverse/negative-coordinate selection.");

using var font=new Font("Segoe UI",17,FontStyle.Regular);
for(int frame=0;frame<30;frame++)
{
    using var bmp=new Bitmap(640,360);
    using(var g=Graphics.FromImage(bmp))
    {
        g.Clear(Color.WhiteSmoke);
        g.FillRectangle(Brushes.DarkSlateGray,0,0,640,68);
        g.DrawString("LightCapture / GIF quality test",font,Brushes.White,20,20);
        for(int row=0;row<5;row++) { g.DrawString($"Region capture  {row+1}  /  15 fps",font,Brushes.Black,24,90+row*42); }
        g.FillRectangle(Brushes.Coral,380+(frame*5)%180,120,40,100);
    }
    bmp.Save(Path.Combine(dir,$"frame{frame:000}.png"),ImageFormat.Png);
}
var source=Path.Combine(dir,"ui.mkv");
await Tools.Run(Tools.Find("ffmpeg.exe"),"-hide_banner","-loglevel","error","-y","-framerate","15","-i",Path.Combine(dir,"frame%03d.png"),"-c:v","ffv1",source);
var optimized=await GifExporter.Export(source,Path.Combine(dir,"ui-optimized.gif"),s);
var baseline=Path.Combine(dir,"ui-naive.gif");
await Tools.Run(Tools.Find("ffmpeg.exe"),"-hide_banner","-loglevel","error","-y","-i",source,baseline);
using(var image=Image.FromFile(optimized.File))
{
    int frames=image.GetFrameCount(FrameDimension.Time);
    if(image.Width!=640||image.Height!=360||frames<20) throw new Exception("GIF lost dimensions or animation.");
    var delays=image.GetPropertyItem(0x5100)!.Value!;
    double duration=Enumerable.Range(0,delays.Length/4).Sum(i=>BitConverter.ToInt32(delays,i*4))/100.0;
    if(Math.Abs(duration-2)>.15) throw new Exception($"Animation duration changed: {duration}");
}
await Tools.Run(Tools.Find("ffmpeg.exe"),"-hide_banner","-loglevel","error","-ignore_loop","1","-i",optimized.File,"-f","null","NUL");
long naiveBytes=new FileInfo(baseline).Length;
if(optimized.Bytes>=naiveBytes) throw new Exception("Optimized UI GIF is not smaller than direct conversion.");
Console.WriteLine($"PASS: UI GIF {optimized.Bytes} vs naive {naiveBytes} bytes; palette {optimized.Colors}; SSIM128={optimized.Quality:0.0000}.");

var motion=Path.Combine(dir,"motion.mkv");
await Tools.Run(Tools.Find("ffmpeg.exe"),"-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=800x450:rate=15","-t","2","-c:v","ffv1",motion);
var motionGif=await GifExporter.Export(motion,Path.Combine(dir,"motion-optimized.gif"),s);
using(var image=Image.FromFile(motionGif.File)) if(image.Width!=640||image.Height!=360) throw new Exception("Aspect-preserving size limit failed.");
await Tools.Run(Tools.Find("ffmpeg.exe"),"-hide_banner","-loglevel","error","-ignore_loop","1","-i",motionGif.File,"-f","null","NUL");
Console.WriteLine($"PASS: motion GIF {motionGif.Bytes} bytes; palette {motionGif.Colors}; SSIM128={motionGif.Quality:0.0000}.");
await File.WriteAllTextAsync(Path.Combine(dir,"results.json"),JsonSerializer.Serialize(new {UI=optimized,NaiveBytes=naiveBytes,Motion=motionGif},new JsonSerializerOptions {WriteIndented=true}));

using LightCapture;
using System.Drawing.Imaging;
using System.Text.Json;

if(args.FirstOrDefault()=="--benchmark")
{
    string folder=Path.GetFullPath(args[1]);var results=new List<object>();
    foreach(string name in new[]{"ui","gradient"})
    {
        var settings=new Settings {GifFps=15,GifMaxSide=960,GifQuality=80};
        var result=await GifExporter.Export(Path.Combine(folder,name+".mkv"),Path.Combine(folder,name+"-current.gif"),settings);
        if(result.Encoder!="gifski") throw new Exception("Fast encoder was not selected.");
        using var image=Image.FromFile(result.File);
        if(image.Width!=(name=="ui"?640:960)||image.Height!=(name=="ui"?360:540)) throw new Exception("Streaming header dimensions were not preserved.");
        results.Add(result);Console.WriteLine(JsonSerializer.Serialize(result));
    }
    File.WriteAllText(Path.Combine(folder,"implementation-benchmark.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions {WriteIndented=true}));
    return;
}

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
var old=new Settings { HotkeySchemaVersion=0,Hotkeys=["Ctrl+Z","Alt+F9","Alt+Shift+F10","Alt+F10","Alt+F1","Alt+R"] };
Settings.Upgrade(old);
if(old.Hotkeys.Length!=6||old.Hotkeys[0]!="Ctrl+Z"||old.Hotkeys[5]!="Alt+2") throw new Exception("Old hotkey migration failed.");
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
    using var first=new Bitmap(image);
    foreach(var item in new[]{(new Point(10,10),Color.DarkSlateGray),(new Point(5,300),Color.WhiteSmoke)})
    {
        var actual=first.GetPixel(item.Item1.X,item.Item1.Y);var expected=item.Item2;
        if(Math.Max(Math.Abs(actual.R-expected.R),Math.Max(Math.Abs(actual.G-expected.G),Math.Abs(actual.B-expected.B)))>4) throw new Exception("GIF shifted the color of a static flat area.");
    }
}
await Tools.Run(Tools.Find("ffmpeg.exe"),"-hide_banner","-loglevel","error","-ignore_loop","1","-i",optimized.File,"-f","null","NUL");
long naiveBytes=new FileInfo(baseline).Length;
if(optimized.Bytes>=naiveBytes) throw new Exception("Optimized UI GIF is not smaller than direct conversion.");
Console.WriteLine($"PASS: UI GIF {optimized.Bytes} vs naive {naiveBytes} bytes; encoder {optimized.Encoder}; {optimized.EncodingSeconds:0.00}s.");

var motion=Path.Combine(dir,"motion.mkv");
await Tools.Run(Tools.Find("ffmpeg.exe"),"-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=800x450:rate=15","-t","2","-c:v","ffv1",motion);
var motionGif=await GifExporter.Export(motion,Path.Combine(dir,"motion-optimized.gif"),s);
using(var image=Image.FromFile(motionGif.File)) if(image.Width!=640||image.Height!=360) throw new Exception("Aspect-preserving size limit failed.");
await Tools.Run(Tools.Find("ffmpeg.exe"),"-hide_banner","-loglevel","error","-ignore_loop","1","-i",motionGif.File,"-f","null","NUL");
Console.WriteLine($"PASS: motion GIF {motionGif.Bytes} bytes; encoder {motionGif.Encoder}; {motionGif.EncodingSeconds:0.00}s.");
var portrait=Path.Combine(dir,"portrait.mkv");
await Tools.Run(Tools.Find("ffmpeg.exe"),"-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=240x400:rate=15","-t","0.5","-c:v","ffv1",portrait);
var portraitGif=await GifExporter.Export(portrait,Path.Combine(dir,"portrait.gif"),s);
using(var image=Image.FromFile(portraitGif.File)) if(image.Width!=240||image.Height!=400) throw new Exception("Portrait GIF was stretched or resized by encoder defaults.");
Console.WriteLine("PASS: portrait aspect ratio and flat RGB colors preserved.");
string unicodeFolder=Path.Combine(dir,"GIF export 含空格");Directory.CreateDirectory(unicodeFolder);
var unicodeGif=await GifExporter.Export(portrait,Path.Combine(unicodeFolder,"片段 01.gif"),s);
if(!File.Exists(unicodeGif.File)) throw new Exception("Unicode/spaced GIF output path failed.");
Console.WriteLine("PASS: Unicode and spaced output paths supported.");
await File.WriteAllTextAsync(Path.Combine(dir,"results.json"),JsonSerializer.Serialize(new {UI=optimized,NaiveBytes=naiveBytes,Motion=motionGif},new JsonSerializerOptions {WriteIndented=true}));

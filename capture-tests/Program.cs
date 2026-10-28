using LightCapture;
using System.Diagnostics;
if(args.FirstOrDefault()=="--desktop-smoke")
{
    string folder=Path.GetFullPath(args[1]); Directory.CreateDirectory(folder);
    var native=new CaptureEngine(new Settings { SystemAudio=false,Fps=30,SaveFolder=folder });
    try
    {
        await native.ToggleReplay(); await Task.Delay(2200);
        if(!native.Running||native.Recovering) throw new Exception("Native WGC capture failed to start.");
        await native.SaveReplay(); await native.Screenshot();
        foreach(var file in Directory.GetFiles(folder,"*.mp4")) await Tools.Run(Tools.Find("ffmpeg.exe"),"-hide_banner","-loglevel","error","-i",file,"-f","null","NUL");
        Console.WriteLine("PASS: native WGC -> NVENC -> RAM replay -> MP4 and PNG, fully decoded.");
    }
    finally { await native.Shutdown(); }
    return;
}
string dir=Path.GetFullPath(args.FirstOrDefault()??"validation"); Directory.CreateDirectory(dir);
var processes=new List<Process>();
var settings=new Settings { SystemAudio=false,Fps=30,ReplaySeconds=10,SaveFolder=dir };
Process Factory(string[] original)
{
    var options=original.ToList(); int index=options.IndexOf("-filter_complex");
    options[index+1]="[0:v]null[v]";
    options.InsertRange(index,["-re","-f","lavfi","-i","testsrc2=size=160x90:rate=30"]);
    var process=Process.Start(Tools.Info(Tools.Find("ffmpeg.exe"),options.ToArray()))!;
    processes.Add(process); return process;
}
var engine=new CaptureEngine(settings,Factory);
try
{
    await engine.ToggleReplay(); await Task.Delay(2200);
    processes[^1].Kill(true); await processes[^1].WaitForExitAsync();
    await engine.Maintain();
    if(!engine.Replay) throw new Exception("FAIL: capture interruption silently disables replay intent.");
    for(int i=0;i<40&&!engine.Running;i++) { await Task.Delay(150); await engine.Maintain(); }
    if(!engine.Running||processes.Count<2) throw new Exception("FAIL: encoder was not recreated after capture interruption.");
    await Task.Delay(2200); await engine.SaveReplay();
    if(Directory.GetFiles(dir,"Replay_*.mp4").Length==0) throw new Exception("FAIL: replay could not be saved after recovery.");
    Console.WriteLine("PASS: capture interruption preserves replay intent, recreates capture, and exports replay.");
}
finally { await engine.Shutdown(); }

var manual=new CaptureEngine(settings,Factory);
try
{
    await manual.ToggleRecording(); await Task.Delay(2200);
    processes[^1].Kill(true); await processes[^1].WaitForExitAsync(); await manual.Maintain();
    if(!manual.Recording||!manual.Running) throw new Exception("FAIL: manual recording intent was lost during recovery.");
    await Task.Delay(2200); await manual.ToggleRecording();
    var parts=Directory.GetFiles(dir,"Recording_*.mp4");
    if(parts.Length<2) throw new Exception("FAIL: interrupted recording did not preserve both parts.");
    foreach(var part in parts) await Tools.Run(Tools.Find("ffmpeg.exe"),"-hide_banner","-loglevel","error","-i",part,"-f","null","NUL");
    Console.WriteLine("PASS: interrupted manual recording preserves playable segments and continues recording.");
}
finally { await manual.Shutdown(); }

int attempts=0;
var unavailable=new CaptureEngine(settings,_=> { attempts++; throw new IOException("Synthetic unavailable desktop"); });
await unavailable.ToggleReplay();
if(!unavailable.Replay||!unavailable.Recovering) throw new Exception("FAIL: startup failure disabled replay intent.");
await unavailable.ToggleReplay(); int stoppedAt=attempts;
await unavailable.Maintain();
if(unavailable.Replay||unavailable.Recovering||attempts!=stoppedAt) throw new Exception("FAIL: disabled replay continued retrying.");
await unavailable.Shutdown();
Console.WriteLine("PASS: unavailable desktop retries without losing intent; disabling replay cancels recovery.");

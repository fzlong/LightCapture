using System.Diagnostics;
using System.Text.Json;
namespace LightCapture;

static class ObsSelfTest
{
    public static async Task Run(string directory,int captureMethod=0)
    {
        string folder=Path.GetFullPath(directory); Directory.CreateDirectory(folder);
        foreach(string stale in new[]{"result.json","error.txt"}) {var file=Path.Combine(folder,stale);if(File.Exists(file)) File.Delete(file);}
        var settings=new Settings {SaveFolder=folder,Fps=30,ReplaySeconds=10,CacheMegabytes=128,SystemAudio=captureMethod==0,CaptureMethod=captureMethod};
        var log=new List<string>(); var engine=new ObsCaptureEngine(settings); engine.Message+=s=>log.Add(s);
        try
        {
            await engine.ToggleReplay();
            if(!engine.Running||engine.Recovering) throw new Exception("OBS capture did not start: "+engine.LastError+"; "+string.Join("; ",log));
            await Task.Delay(3200); await engine.Maintain();
            if(engine.CacheBytes<=0||engine.CacheSeconds<2) throw new Exception("No encoded packets in native RAM replay");
            double before=engine.CacheSeconds;
            await engine.SaveReplay();
            await engine.ToggleRecording(); await Task.Delay(2500); await engine.ToggleRecording();
            await engine.Maintain();
            if(engine.CacheSeconds<before) throw new Exception("Starting/stopping manual recording cleared replay");
            await engine.SaveReplay();
            if(captureMethod==0)
            {
                int session=engine.WorkerPid;double accumulated=engine.CacheSeconds;
                foreach(int method in new[]{1,2})
                {
                    await engine.SwitchCaptureBackend(method); await Task.Delay(1500);await engine.Maintain();
                    if(engine.Recovering||engine.WorkerPid!=session||engine.CacheSeconds<accumulated) throw new Exception("Capture backend switch restarted or cleared replay");
                }
                await engine.SaveReplay();
            }
            await Task.Delay(8000);await engine.Maintain();
            if(engine.CacheSeconds<8||engine.CacheSeconds>12.5) throw new Exception("Replay rolling retention did not converge to 10 seconds: "+engine.CacheSeconds);
            await engine.SaveReplay();
            // Actual worker failure, including any child muxer: UI intent must survive recovery.
            await engine.ToggleRecording();await Task.Delay(2500);
            int oldPid=engine.WorkerPid;
            using(var process=Process.GetProcessById(oldPid)) {process.Kill(true);await process.WaitForExitAsync();}
            await engine.Maintain();
            if(!engine.Replay||!engine.Recording||!engine.Recovering) throw new Exception("Worker failure lost replay/recording request");
            for(int i=0;i<40&&engine.Recovering;i++) {await Task.Delay(250);await engine.Maintain();}
            if(engine.Recovering||!engine.Running||engine.WorkerPid==oldPid) throw new Exception("Worker did not recover");
            await Task.Delay(2200);await engine.SaveReplay();
            await engine.ToggleRecording();
            var interrupted=Directory.GetFiles(folder,"*.mkv");
            if(interrupted.Length==0) throw new Exception("Interrupted recording MKV was not retained");
            foreach(string file in interrupted) await Tools.Run(Tools.Find("ffmpeg.exe"),"-hide_banner","-loglevel","error","-i",file,"-f","null","NUL");
            long bytes=engine.CacheBytes;double seconds=engine.CacheSeconds;
            await engine.ToggleReplay();
            if(engine.Running||engine.Recovering) throw new Exception("Disabled replay still runs");
            // Manual capture must work without replay, including its shared AAC encoder.
            await engine.ToggleRecording();await Task.Delay(2200);await engine.ToggleRecording();
            foreach(string file in Directory.GetFiles(folder,"*.mp4"))
                await Tools.Run(Tools.Find("ffmpeg.exe"),"-hide_banner","-loglevel","error","-i",file,"-f","null","NUL");
            await File.WriteAllTextAsync(Path.Combine(folder,"result.json"),JsonSerializer.Serialize(new {Passed=true,CacheBytes=bytes,CacheSeconds=seconds,Messages=log,Files=Directory.GetFiles(folder,"*.mp4")},new JsonSerializerOptions {WriteIndented=true}));
        }
        finally {await engine.Shutdown();}
    }
}

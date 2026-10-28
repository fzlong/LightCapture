using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
namespace LightCapture;
static class SelfTest
{
    public static async Task Run()
    {
        string folder=Path.Combine(AppContext.BaseDirectory,"validation"); Directory.CreateDirectory(folder);
        var settings=new Settings { SaveFolder=folder,Fps=30,ReplaySeconds=10,SystemAudio=true,CacheMegabytes=128 };
        var messages=new List<string>(); var engine=new CaptureEngine(settings); engine.Message+=s=>messages.Add(s);
        try
        {
            // Real WASAPI loopback test source; low volume and short duration.
            using var tone=new WaveOutEvent(); tone.Init(new SignalGenerator { Gain=.015,Frequency=440,Type=SignalGeneratorType.Sin }); tone.Play();
            await engine.ToggleReplay(); await Task.Delay(5500);
            await engine.SaveReplay();
            await engine.ToggleRecording(); await Task.Delay(3500); await engine.ToggleRecording();
            await Task.Delay(2000); await engine.SaveReplay();
            long cache=engine.CacheBytes; double duration=engine.CacheSeconds;
            if(cache<=0||cache>128L*1024*1024+64L*1024*1024) throw new Exception("RAM cache size invalid");
            await engine.ToggleReplay();
            await engine.Screenshot();
            // Independent manual recording without replay.
            await engine.ToggleRecording(); await Task.Delay(2500); await engine.ToggleRecording();
            tone.Stop();
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"self-test.json"),JsonSerializer.Serialize(new { CacheBytes=cache,CacheSeconds=duration,Messages=messages,Files=Directory.GetFiles(folder),Error=engine.LastError },new JsonSerializerOptions { WriteIndented=true }));
        }
        finally { await engine.Shutdown(); }
    }
}

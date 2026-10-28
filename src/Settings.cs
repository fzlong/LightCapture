using System.Text.Json;
using System.Diagnostics;
using Microsoft.Win32;
namespace LightCapture;
sealed class Settings
{
    public string SaveFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),"LightCapture");
    public int Fps { get; set; } = 60;
    public int ReplaySeconds { get; set; } = 60;
    public int Quality { get; set; } = 23;
    public int CacheMegabytes { get; set; } = 512;
    public int DisplayIndex { get; set; }
    // OBS display capture: 0 auto, 1 WGC, 2 DXGI. Same values retained for screenshot compatibility.
    public int CaptureMethod { get; set; }
    public bool SystemAudio { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool ReplayOnLaunch { get; set; }
    public int GifFps { get; set; } = 15;
    public int GifMaxSide { get; set; } = 960;
    public int GifMaxSeconds { get; set; } = 60;
    public string[] Hotkeys { get; set; } = ["Alt+Z","Alt+F9","Alt+Shift+F10","Alt+F10","Alt+F1","Alt+R","Alt+F2"];
    public static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LightCapture");
    public static Settings Load()
    {
        try { return Upgrade(JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(DataFolder,"settings.json")))??new()); }
        catch { return new(); }
    }
    internal static Settings Upgrade(Settings value)
    {
        var defaults=new Settings();
        value.Hotkeys=Enumerable.Range(0,defaults.Hotkeys.Length).Select(i=>value.Hotkeys?.ElementAtOrDefault(i) is {Length:>0} saved?saved:defaults.Hotkeys[i]).ToArray();
        value.GifFps=Math.Clamp(value.GifFps,8,30); value.GifMaxSide=Math.Clamp(value.GifMaxSide,320,1920); value.GifMaxSeconds=Math.Clamp(value.GifMaxSeconds,10,300);
        return value;
    }
    public void Save() { Directory.CreateDirectory(DataFolder); File.WriteAllText(Path.Combine(DataFolder,"settings.json"),JsonSerializer.Serialize(this,new JsonSerializerOptions { WriteIndented=true })); }
    public static bool AutoStart
    {
        get { try { using var k=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return k?.GetValue("LightCapture")!=null; } catch(System.Security.SecurityException) { return false; } catch(UnauthorizedAccessException) { return false; } }
        set
        {
            using var k=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if(value) k.SetValue("LightCapture",StartupCommand,RegistryValueKind.String); else k.DeleteValue("LightCapture",false);
            if(value ? k.GetValue("LightCapture") as string!=StartupCommand : k.GetValue("LightCapture")!=null)
                throw new IOException("Windows 开机启动项写入后校验失败。");
        }
    }
    internal static string StartupCommand=>$"\"{Environment.ProcessPath??throw new IOException("无法确定主程序路径")}\" --background";
    internal static bool RefreshAutoStartPath()
    {
        using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if(key?.GetValue("LightCapture") is not string old || old==StartupCommand) return false;
        // Only migrate an existing entry for this app; a disabled/missing entry stays disabled.
        string executable=old.StartsWith('"')?old.Split('"').ElementAtOrDefault(1)??"":old.Split(' ',2)[0];
        if(!string.Equals(Path.GetFileName(executable),"LightCapture.exe",StringComparison.OrdinalIgnoreCase)) return false;
        var registered=ExecutableVersion(executable);var current=ExecutableVersion(Environment.ProcessPath!);
        if(registered!=null && current!=null && registered>current) return false;
        AutoStart=true;return true;
    }
    static Version? ExecutableVersion(string executable)
    {
        try {return File.Exists(executable)&&Version.TryParse(FileVersionInfo.GetVersionInfo(executable).FileVersion,out var version)?version:null;}
        catch(IOException) {return null;}
        catch(UnauthorizedAccessException) {return null;}
    }
}
static class Tools
{
    public static string Find(string name)
    {
        foreach(var dir in new[]{AppContext.BaseDirectory,Path.Combine(AppContext.BaseDirectory,"tools"),Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","tools")),@"C:\ffmpeg\bin"}.Concat((Environment.GetEnvironmentVariable("PATH")??"").Split(';')))
        { var p=Path.Combine(dir,name); if(File.Exists(p)) return p; }
        throw new FileNotFoundException($"缺少 {name}，请将它放在程序的 tools 文件夹。");
    }
    public static ProcessStartInfo Info(string exe, params string[] args)
    {
        var p=new ProcessStartInfo(exe) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardInput=true,RedirectStandardOutput=true };
        foreach(var a in args) p.ArgumentList.Add(a); return p;
    }
    public static async Task Run(string exe, params string[] args)
    {
        using var p=Process.Start(Info(exe,args))!; var err=p.StandardError.ReadToEndAsync(); var output=p.StandardOutput.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await p.WaitForExitAsync(timeout.Token); } catch { try { p.Kill(true); } catch { } throw; }
        await output; var error=await err;
        if(p.ExitCode!=0) throw new IOException(error[^Math.Min(error.Length,2500)..]);
    }
}

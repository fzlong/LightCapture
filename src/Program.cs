namespace LightCapture;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        if(args.Contains("--obs-worker")) { ObsWorker.Run(args[^1]); return; }
        if(args.Contains("--obs-self-test")||args.Contains("--obs-self-test-wgc"))
        {
            try { ObsSelfTest.Run(args[^1],args.Contains("--obs-self-test-wgc")?1:0).GetAwaiter().GetResult(); }
            catch(Exception ex) { File.WriteAllText(Path.Combine(args[^1],"error.txt"),ex.ToString()); Environment.ExitCode=1; }
            return;
        }
        if(args.Contains("--fps-worker")) { FpsMonitor.Worker(args[^1]).GetAwaiter().GetResult(); return; }
        if (args.Contains("--self-test"))
        {
            try { SelfTest.Run().GetAwaiter().GetResult(); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"self-test-error.txt"),ex.ToString()); Environment.ExitCode=1; }
            return;
        }
        // Runs before single-instance forwarding, so opening a newer release repairs login startup
        // even if the previous release still owns the tray window. Worker/test modes never touch it.
        try { Settings.RefreshAutoStartPath(); }
        catch(Exception ex)
        {
            try {Directory.CreateDirectory(Settings.DataFolder);File.AppendAllText(Path.Combine(Settings.DataFolder,"app.log"),$"{DateTime.Now:O} 开机启动路径更新失败：{ex.Message}\n");} catch { }
        }
        using var mutex = new Mutex(true, @"Local\LightCapture.Main", out bool first);
        if (!first) { Native.PostMessage(Native.HWND_BROADCAST,Native.ShowMessage,IntPtr.Zero,IntPtr.Zero); return; }
        try { Application.Run(new MainWindow(args.Contains("--background"))); }
        catch(Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"startup-error.txt"),ex.ToString()); }
    }
}

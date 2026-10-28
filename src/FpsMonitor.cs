using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Principal;
namespace LightCapture;

sealed class FpsMonitor : IDisposable
{
    NamedPipeServerStream? pipe;
    Process? worker;
    CancellationTokenSource? lifetime;
    int generation;
    int headerSeen;
    long acceptedSamples;
    readonly object sync=new();
    readonly Dictionary<(uint,string),Queue<(DateTime Time,double Ms)>> samples=new();
    public bool Active=>pipe?.IsConnected==true;
    public string? Error { get; private set; }
    public string WaitingMessage=>Volatile.Read(ref acceptedSamples)>0?"FPS · 前台无帧数据":Volatile.Read(ref headerSeen)>0?"FPS · 尚无帧事件":"FPS · 等待采集数据";
    public async Task Start()
    {
        if(Active) return;
        Dispose(); Error=null;
        var name="LightCaptureFPS_"+Guid.NewGuid().ToString("N");
        var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        pipe=server;
        var cancel=new CancellationTokenSource(); lifetime=cancel;
        int version=generation;
        bool admin=new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        var info=new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("--fps-worker"); info.ArgumentList.Add(name);
        if(!admin) info.Verb="runas";
        try
        {
            // ShellExecute/UAC and pipe startup must never run on the window thread.
            var started=await Task.Run(()=>Process.Start(info)).ConfigureAwait(false);
            if(version!=Volatile.Read(ref generation)) { started?.Dispose(); throw new OperationCanceledException(); }
            worker=started;
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await server.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if(version!=Volatile.Read(ref generation)) throw new OperationCanceledException();
            _=Read(server);
        }
        catch { if(version==Volatile.Read(ref generation)) Dispose(); throw; }
    }
    Task Read(Stream stream)
    {
        int version=Volatile.Read(ref generation);
        var token=lifetime?.Token??CancellationToken.None;
        // Even an already-buffered stream can complete every await synchronously.
        // Isolate the entire intake loop, including its first iteration, from UI callers.
        return Task.Run(()=>Consume(stream,version,token));
    }
    async Task Consume(Stream stream,int version,CancellationToken token)
    {
        try
        {
            using var reader=new StreamReader(stream,System.Text.Encoding.UTF8,false,4096,true);
            int pidIndex=-1,msIndex=-1,swapIndex=-1;
            while(await reader.ReadLineAsync(token).ConfigureAwait(false) is {} line)
            {
                if(version!=Volatile.Read(ref generation)) return;
                if(line.StartsWith("ERROR:")) { Error=line[6..]; continue; }
                var p=line.Split(',').Select(value=>value.Trim().Trim('"','\ufeff')).ToArray();
                int Find(string name)=>Array.FindIndex(p,value=>string.Equals(value,name,StringComparison.OrdinalIgnoreCase));
                if(Find("ProcessID") is var processColumn && processColumn>=0)
                {
                    pidIndex=processColumn; msIndex=Find("msBetweenPresents"); swapIndex=Find("SwapChainAddress");
                    if(msIndex<0) { Error="PresentMon 输出缺少帧间隔列，无法计算 FPS。"; return; }
                    Volatile.Write(ref headerSeen,1); continue;
                }
                if(pidIndex<0||msIndex<0||p.Length<=Math.Max(pidIndex,msIndex)) continue;
                if(!uint.TryParse(p[pidIndex],out var pid)||!double.TryParse(p[msIndex],CultureInfo.InvariantCulture,out var ms)||ms<=0||ms>1000) continue;
                string swap=swapIndex>=0&&swapIndex<p.Length?p[swapIndex]:"";
                lock(sync)
                {
                    if(version!=Volatile.Read(ref generation)) return;
                    var key=(pid,swap);
                    if(!samples.TryGetValue(key,out var q))
                    {
                        if(samples.Count>=256) samples.Remove(samples.MinBy(s=>s.Value.LastOrDefault().Time).Key);
                        samples[key]=q=new();
                    }
                    var now=DateTime.UtcNow; q.Enqueue((now,ms)); while(q.Count>4096 || q.Count>0&&q.Peek().Time<now.AddSeconds(-1)) q.Dequeue();
                    Interlocked.Increment(ref acceptedSamples);
                    if(samples.Count>256) foreach(var k in samples.Where(s=>s.Value.Count==0||s.Value.Last().Time<now.AddSeconds(-5)).Select(s=>s.Key).ToArray()) samples.Remove(k);
                }
            }
            if(version==Volatile.Read(ref generation)) Error??="FPS 采集已结束";
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) { if(version==Volatile.Read(ref generation)) Error=ex.Message; }
    }
    public double? Get(uint pid)
    {
        lock(sync)
        {
            var q=samples.Where(s=>s.Key.Item1==pid&&s.Value.Count>0&&s.Value.Last().Time>DateTime.UtcNow.AddSeconds(-2)).Select(s=>s.Value).OrderByDescending(q=>q.Count).FirstOrDefault();
            return q is {Count:>0}?1000/q.Average(v=>v.Ms):null;
        }
    }
    public void Dispose()
    {
        Interlocked.Increment(ref generation);
        // Closing the duplex pipe notifies the helper's control reader. No synchronous
        // flush/write to an unresponsive helper is allowed on the window thread.
        var cancel=Interlocked.Exchange(ref lifetime,null); cancel?.Cancel();
        Interlocked.Exchange(ref pipe,null)?.Dispose();
        Interlocked.Exchange(ref worker,null)?.Dispose();
        cancel?.Dispose();
        lock(sync) samples.Clear();
        Volatile.Write(ref headerSeen,0); Interlocked.Exchange(ref acceptedSamples,0);
    }
    public static async Task Worker(string name)
    {
        if(!name.StartsWith("LightCaptureFPS_")||name.Length!=48) return;
        using var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(15000); } catch { return; }
        using var output=new StreamWriter(pipe,new System.Text.UTF8Encoding(false),4096,true) { AutoFlush=true };
        using var input=new StreamReader(pipe,System.Text.Encoding.UTF8,false,4096,true);
        Process? pm=null;
        try
        {
            pm=Process.Start(CollectorStartInfo(Tools.Find("PresentMon.exe"),name));
            var errors=pm!.StandardError.ReadToEndAsync();
            var copy=Task.Run(async()=> { while(await pm.StandardOutput.ReadLineAsync() is {} line) await output.WriteLineAsync(line); });
            var stop=input.ReadLineAsync();
            var exited=pm.WaitForExitAsync();
            await Task.WhenAny(stop,copy,exited);
            if(!pm.HasExited) { pm.Kill(true); await pm.WaitForExitAsync(); }
            try { await copy; } catch { }
            if(pm.ExitCode!=0) { try { await output.WriteLineAsync("ERROR:"+await errors); } catch { } }
        }
        catch(Exception ex) { try { await output.WriteLineAsync("ERROR:"+ex.Message); } catch { } }
        finally { if(pm!=null) { try { if(!pm.HasExited) pm.Kill(true); } catch { } pm.Dispose(); } }
    }
    internal static ProcessStartInfo CollectorStartInfo(string executable,string sessionName)
        // Keep display tracking enabled: this driver/PresentMon combination otherwise
        // emits no frame rows. GPU-duration and input tracking can still be omitted.
        =>Tools.Info(executable,"--output_stdout","--no_console_stats","--v1_metrics","--no_track_gpu","--no_track_input","--session_name",sessionName);
}

sealed class FpsOverlay : Form
{
    readonly Label label=new() { Dock=DockStyle.Fill,Text="FPS · 等待采集数据",ForeColor=Color.LimeGreen,BackColor=Color.FromArgb(25,25,25),Font=new Font("Segoe UI",15,FontStyle.Bold),TextAlign=ContentAlignment.MiddleCenter };
    public FpsOverlay()
    {
        FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; TopMost=true; StartPosition=FormStartPosition.Manual;
        Bounds=new Rectangle(24,24,230,45); Controls.Add(label); Opacity=.88;
    }
    protected override bool ShowWithoutActivation=>true;
    protected override CreateParams CreateParams { get { var p=base.CreateParams; p.ExStyle|=0x08000000|0x20|0x80; return p; } }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.SetWindowDisplayAffinity(Handle,0x11); }
    public void UpdateFps(double? fps,string? error,string waiting="FPS · 等待采集数据") { label.Text=error!=null?"FPS · 采集不可用":fps is {} f?$"{f:0} FPS":waiting; }
}

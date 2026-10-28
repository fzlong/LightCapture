using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
namespace LightCapture;

sealed class ObsCaptureEngine
{
    readonly Settings settings;
    readonly SemaphoreSlim gate=new(1,1);
    Process? worker;
    NamedPipeServerStream? pipe;
    StreamReader? input;
    StreamWriter? output;
    bool replay,recording,recovering;
    int failures;
    DateTime nextRetry,missingSince;
    bool triedCaptureFallback;
    volatile ObsReply state=new();
    public bool Replay=>replay;
    public bool Recording=>recording;
    public bool Recovering=>recovering;
    public bool Running=>worker is {HasExited:false} && pipe?.IsConnected==true;
    public long CacheBytes=>state.CacheBytes;
    public double CacheSeconds=>state.CacheSeconds;
    public string? LastError {get;private set;}
    internal int WorkerPid=>worker?.Id??0;
    public event Action<string>? Message;
    public ObsCaptureEngine(Settings settings)=>this.settings=settings;

    async Task<ObsReply> Request(ObsCommand command,int seconds=20)
    {
        if(output==null||input==null) throw new IOException("捕获后台未连接");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        await output.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(),timeout.Token);
        string line=await input.ReadLineAsync(timeout.Token)??throw new IOException("OBS 捕获后台已退出");
        var reply=JsonSerializer.Deserialize<ObsReply>(line)??throw new IOException("OBS 后台回复无效");
        if(reply.Error!=null) throw new IOException(reply.Error);
        state=reply; return reply;
    }
    async Task EnsureStarted()
    {
        if(Running) return;
        await StopWorker();
        string pipeName="LightCapture.OBS."+Guid.NewGuid().ToString("N");
        pipe=new NamedPipeServerStream(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        var executable=Path.Combine(AppContext.BaseDirectory,"obs-runtime","bin","64bit","LightCapture.exe");
        if(!File.Exists(executable)) throw new FileNotFoundException("缺少 OBS 捕获后台，请完整解压轻录文件夹。");
        var info=Tools.Info(executable,"--obs-worker",pipeName);
        worker=Process.Start(info)??throw new IOException("无法启动 OBS 捕获后台");
        // Always drain both streams. OBS diagnostics must never block the encoder.
        var active=worker;
        _=DrainLog(active.StandardOutput,"stdout"); _=DrainLog(active.StandardError,"stderr");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await pipe.WaitForConnectionAsync(timeout.Token);
        input=new StreamReader(pipe,new UTF8Encoding(false),false,4096,true);
        output=new StreamWriter(pipe,new UTF8Encoding(false),4096,true) {AutoFlush=true};
        await Request(new("start",settings)); missingSince=default;triedCaptureFallback=false;
    }
    static async Task DrainLog(StreamReader reader,string channel)
    {
        try
        {
            Directory.CreateDirectory(Settings.DataFolder);
            // One log writer per worker stream, with short shared appends; capped between sessions.
            string file=Path.Combine(Settings.DataFolder,"obs-worker-"+channel+".log");
            if(File.Exists(file)&&new FileInfo(file).Length>4*1024*1024) File.WriteAllText(file,"");
            while(await reader.ReadLineAsync() is {} line)
            {
                try {await File.AppendAllTextAsync(file,$"{DateTime.Now:O} {line}\n");} catch(IOException) { }
            }
        }
        catch { }
    }
    void Recovery(Exception error)
    {
        LastError=error.Message; recovering=replay||recording;
        nextRetry=DateTime.UtcNow.AddSeconds(Math.Min(15,Math.Pow(2,Math.Min(++failures,4))));
        state=new();
        if(failures==1||failures%5==0) Message?.Invoke("捕获暂不可用，将自动恢复："+error.Message);
    }
    async Task StartRequested()
    {
        await EnsureStarted();
        if(replay && !state.Replay) await Request(new("replay",Enabled:true));
        if(recording && !state.Recording) await Request(new("record",Enabled:true));
        // Do not report successful startup until actual encoded frames and a capture texture exist.
        for(int i=0;i<60;i++)
        {
            await Task.Delay(100); await Request(new("status"));
            if(state.Frames>0 && state.SourceWidth>0 && state.SourceHeight>0) {recovering=false;failures=0;LastError=null;return;}
            if(i==29 && settings.CaptureMethod==0) await Request(new("method",Method:1));
        }
        throw new IOException("OBS 未收到显示器画面，将重试。请检查显示器选择和捕获方式。");
    }
    public async Task ToggleReplay()
    {
        await gate.WaitAsync();
        try
        {
            if(!replay)
            {
                replay=true;
                try {await StartRequested();}
                catch(Exception ex) {await StopWorker();Recovery(ex);}
            }
            else
            {
                replay=false;
                if(!recording) {recovering=false; await StopWorker();}
                else if(Running) await Request(new("replay",Enabled:false));
            }
        }
        finally {gate.Release();}
    }
    public async Task ToggleRecording()
    {
        await gate.WaitAsync();
        try
        {
            if(!recording)
            {
                recording=true;
                try {await StartRequested();Message?.Invoke("录制已开始，游戏和桌面连续录制");}
                catch(Exception ex) {await StopWorker();Recovery(ex);}
            }
            else
            {
                recording=false;
                try
                {
                    if(Running) {var reply=await Request(new("record",Enabled:false),180);if(reply.Path!=null) Message?.Invoke("已保存："+reply.Path);}
                }
                finally {if(!replay) {recovering=false;await StopWorker();}}
            }
        }
        finally {gate.Release();}
    }
    public async Task SaveReplay()
    {
        await gate.WaitAsync();
        try
        {
            if(!replay) throw new InvalidOperationException("请先开启即时回放。");
            if(!Running||recovering) throw new IOException("正在恢复显示器捕获，恢复后会自动累计回放。");
            var reply=await Request(new("save"),75);
            if(reply.Path==null) throw new IOException("OBS 没有返回已保存的回放文件。");
            Message?.Invoke("已保存："+reply.Path);
        }
        finally {gate.Release();}
    }
    internal async Task SwitchCaptureBackend(int method)
    {
        await gate.WaitAsync();
        try {await Request(new("method",Method:method));}
        finally {gate.Release();}
    }
    public async Task Screenshot()
    {
        var snapshot=new CaptureEngine(settings); snapshot.Message+=s=>Message?.Invoke(s);
        await snapshot.Screenshot();
    }
    public async Task Maintain()
    {
        if(!await gate.WaitAsync(0)) return;
        try
        {
            if(!replay&&!recording) return;
            if(Running && !recovering)
            {
                try
                {
                    await Request(new("status"),5);
                    if((replay&&!state.Replay)||(recording&&!state.Recording)||state.PacketAge>10) throw new IOException("OBS 编码输出中断");
                    if(state.SourceWidth==0||state.SourceHeight==0)
                    {
                        if(missingSince==default) {missingSince=DateTime.UtcNow; Message?.Invoke("显示器捕获暂不可用，OBS 正在重新连接；现有回放缓存继续保留。");}
                        if(settings.CaptureMethod==0 && !triedCaptureFallback && DateTime.UtcNow-missingSince>TimeSpan.FromSeconds(8))
                        {
                            int method=state.CaptureMethod==1?2:1;
                            await Request(new("method",Method:method)); triedCaptureFallback=true; missingSince=DateTime.UtcNow;
                            Message?.Invoke("正在切换显示器捕获方式，保留原有回放缓存。");
                        }
                        // Give libobs time to recreate the texture without losing its replay buffer.
                        if(DateTime.UtcNow-missingSince>TimeSpan.FromSeconds(20)) throw new IOException("显示器捕获持续不可用");
                    }
                    else {missingSince=default;triedCaptureFallback=false;}
                    if(recording && new DriveInfo(Path.GetPathRoot(settings.SaveFolder)!).AvailableFreeSpace<512L*1024*1024)
                    {
                        recording=false; var reply=await Request(new("record",Enabled:false),180);
                        Message?.Invoke("磁盘空间不足，录制已停止。"+(reply.Path==null?"":"已保存："+reply.Path));
                        if(!replay) await StopWorker();
                    }
                    return;
                }
                catch(Exception ex) {await StopWorker();Recovery(ex);return;}
            }
            if(!recovering) {Recovery(new IOException("OBS 捕获后台意外退出"));await StopWorker();}
            if(DateTime.UtcNow<nextRetry) return;
            try {await StartRequested();Message?.Invoke("显示器捕获已恢复，回放重新累计。"+(recording?"录制已在新文件中续录，中断前 MKV 保留。":""));}
            catch(Exception ex) {await StopWorker();Recovery(ex);}
        }
        finally {gate.Release();}
    }
    async Task StopWorker()
    {
        var active=worker;
        try
        {
            if(active is {HasExited:false})
            {
                try {if(pipe?.IsConnected==true && output!=null) await Request(new("stop"),12);} catch { }
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try {await active.WaitForExitAsync(timeout.Token);} catch(OperationCanceledException) {active.Kill(true);await active.WaitForExitAsync();}
            }
        }
        finally
        {
            input?.Dispose(); output?.Dispose(); pipe?.Dispose(); active?.Dispose();
            input=null; output=null; pipe=null; worker=null; state=new();
        }
    }
    public async Task Shutdown()
    {
        if(recording) await ToggleRecording();
        await gate.WaitAsync();
        try {replay=recording=recovering=false;await StopWorker();}
        finally {gate.Release();}
    }
}

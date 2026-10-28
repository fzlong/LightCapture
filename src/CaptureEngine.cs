using System.Diagnostics;
namespace LightCapture;

sealed record MemorySegment(byte[] Data, double Start, double End);
sealed class CaptureEngine
{
    readonly Settings settings;
    readonly Func<string[],Process>? startCapture;
    readonly SemaphoreSlim gate=new(1,1);
    readonly object sync=new();
    readonly Queue<MemorySegment> ring=new();
    MemoryStream current=new();
    byte[]? pat,pmt;
    double currentStart,latestTime,origin=-1;
    long ringBytes;
    Process? capture;
    AudioFeed? audio;
    Task? reader;
    Task<string>? errors;
    FileStream? recording;
    string? recordingFile;
    bool replay;
    bool recordingRequested;
    bool recoveryPending;
    int recoveryFailures;
    DateTime nextRecovery;
    public bool Recording=>recordingRequested;
    public bool Recovering=>recoveryPending;
    public bool Replay=>replay;
    public bool Running=>capture is { HasExited:false };
    public long CacheBytes { get { lock(sync) return ringBytes+current.Length; } }
    public double CacheSeconds { get { lock(sync) return Math.Max(0,latestTime-(ring.Count>0?ring.Peek().Start:currentStart)); } }
    public string? LastError { get; private set; }
    public event Action<string>? Message;
    public CaptureEngine(Settings s,Func<string[],Process>? startCapture=null) { settings=s; this.startCapture=startCapture; }
    public async Task ToggleReplay()
    {
        await gate.WaitAsync();
        try
        {
            if(!replay)
            {
                replay=true;
                try { await EnsureStarted(); recoveryPending=false; }
                catch(Exception ex) { QueueRecovery(ex); }
            }
            else { replay=false; if(!Recording) { recoveryPending=false; await StopSession(); } else lock(sync) { ring.Clear(); ringBytes=0; } }
        }
        finally { gate.Release(); }
    }
    public async Task ToggleRecording()
    {
        await gate.WaitAsync();
        try
        {
            if(!Recording)
            {
                recordingRequested=true;
                try { await EnsureStarted(); BeginRecordingPart(); recoveryPending=false; Message?.Invoke("录制已开始"); }
                catch(Exception ex) { QueueRecovery(ex); }
            }
            else
            {
                recordingRequested=false;
                string? path=EndRecordingPart();
                if(!replay) { recoveryPending=false; await StopSession(); }
                if(path!=null) await Remux(path,Path.ChangeExtension(path.Replace(".pending",""),"mp4"));
            }
        }
        finally { gate.Release(); }
    }
    void BeginRecordingPart()
    {
        Directory.CreateDirectory(settings.SaveFolder);
        string path=Path.Combine(settings.SaveFolder,$"Recording_{DateTime.Now:yyyyMMdd_HHmmss_fff}.pending.ts");
        lock(sync)
        {
            var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
            try { current.WriteTo(output); recording=output; recordingFile=path; }
            catch { output.Dispose(); throw; }
        }
    }
    string? EndRecordingPart()
    {
        lock(sync)
        {
            var path=recordingFile; recording?.Dispose(); recording=null; recordingFile=null; return path;
        }
    }
    void QueueRecovery(Exception error)
    {
        LastError=error.Message; recoveryPending=replay||recordingRequested;
        nextRecovery=DateTime.UtcNow.AddSeconds(Math.Min(15,Math.Pow(2,Math.Min(++recoveryFailures,4))));
        if(recoveryFailures==1 || recoveryFailures%5==0) Message?.Invoke("捕获暂不可用，将自动重试："+error.Message);
    }
    internal static string VideoSource(Settings s,bool legacy=false)
    {
        if(s.CaptureMethod==2 || legacy) return $"ddagrab=output_idx={s.DisplayIndex}:framerate={s.Fps}:draw_mouse=1";
        var screens=Screen.AllScreens;
        if(s.DisplayIndex<0||s.DisplayIndex>=screens.Length) throw new ArgumentException("显示器编号无效，请在设置中重新选择。");
        var bounds=screens[s.DisplayIndex].Bounds;
        return $"gfxcapture=monitor_idx={s.DisplayIndex}:max_framerate={s.Fps}:width={bounds.Width/2*2}:height={bounds.Height/2*2}:resize_mode=scale_aspect:display_border=0";
    }
    async Task EnsureStarted(bool legacy=false)
    {
        if(Running) return;
        if(capture!=null) await StopSession();
        lock(sync) { ring.Clear(); ringBytes=0; current.SetLength(0); pat=pmt=null; origin=-1; latestTime=currentStart=0; }
        var args=new List<string> {"-hide_banner","-loglevel","warning","-y","-filter_complex",VideoSource(settings,legacy)+"[v]"};
        try
        {
            if(settings.SystemAudio)
            {
                audio=new(); audio.Start();
                args.AddRange(["-thread_queue_size","512","-probesize","32","-analyzeduration","0","-f","f32le","-ar",audio.Rate.ToString(),"-ac",audio.Channels.ToString(),"-i",audio.PipePath]);
            }
            args.AddRange(["-map","[v]"]);
            if(audio!=null) args.AddRange(["-map","0:a","-c:a","aac","-b:a","192k"]);
            args.AddRange(["-c:v","h264_nvenc","-preset","p1","-tune","ll","-rc","vbr","-cq",settings.Quality.ToString(),"-b:v","12M","-maxrate","40M","-bufsize","40M","-g",(settings.Fps*2).ToString(),"-bf","0","-force_key_frames","expr:gte(t,n_forced*2)","-streamid","0:256","-f","mpegts","-mpegts_flags","+resend_headers+pat_pmt_at_frames","-muxdelay","0","pipe:1"]);
            capture=startCapture?.Invoke(args.ToArray())??Process.Start(Tools.Info(Tools.Find("ffmpeg.exe"),args.ToArray()))!;
            errors=capture.StandardError.ReadToEndAsync();
            var active=capture;
            reader=Task.Run(()=>ReadPackets(active.StandardOutput.BaseStream));
            for(int i=0;i<100;i++)
            {
                if(capture.HasExited) throw new IOException(await errors);
                if(reader.IsFaulted) await reader;
                lock(sync) { if(latestTime>0.2 && current.Length>0) return; }
                await Task.Delay(100);
            }
            throw new TimeoutException("捕获启动超时，请检查桌面权限、显示器编号、音频设备。");
        }
        catch
        {
            await StopSession();
            if(settings.CaptureMethod==0 && !legacy) { await EnsureStarted(true); return; }
            throw;
        }
    }
    async Task ReadPackets(Stream stream)
    {
        byte[] packet=new byte[188];
        while(true)
        {
            int read=0;
            while(read<188) { int n=await stream.ReadAsync(packet.AsMemory(read)); if(n==0) return; read+=n; }
            if(packet[0]!=0x47) throw new IOException("视频传输流失去同步。");
            AcceptPacket(packet);
        }
    }
    void AcceptPacket(byte[] p)
    {
        int pid=((p[1]&31)<<8)|p[2];
        int adaptation=(p[3]>>4)&3;
        bool random=pid==256 && (adaptation==2 || adaptation==3) && p[4]>0 && (p[5]&0x40)!=0;
        double? time=null;
        if(pid==256 && (adaptation==2 || adaptation==3) && p[4]>=7 && (p[5]&0x10)!=0)
        {
            long value=((long)p[6]<<25)|((long)p[7]<<17)|((long)p[8]<<9)|((long)p[9]<<1)|(uint)(p[10]>>7);
            time=value/90000.0;
        }
        lock(sync)
        {
            if(pid==0) pat=(byte[])p.Clone();
            if(pid==4096) pmt=(byte[])p.Clone();
            if(time!=null) { if(origin<0) origin=time.Value; latestTime=time.Value-origin; }
            if(random)
            {
                if(current.Length>0 && latestTime>currentStart+0.1)
                { ring.Enqueue(new(current.ToArray(),currentStart,latestTime)); ringBytes+=current.Length; }
                current.SetLength(0); currentStart=latestTime;
                if(pat!=null) current.Write(pat); if(pmt!=null) current.Write(pmt);
                Prune();
            }
            current.Write(p); recording?.Write(p);
            if(current.Length>64*1024*1024) throw new IOException("编码器没有按预期产生关键帧，已停止以保护内存。");
        }
    }
    void Prune()
    {
        double retain=replay?settings.ReplaySeconds:2;
        long limit=settings.CacheMegabytes*1024L*1024L;
        while(ring.Count>0 && (ring.Peek().End<latestTime-retain || ringBytes+current.Length>limit)) ringBytes-=ring.Dequeue().Data.Length;
    }
    public async Task SaveReplay()
    {
        await gate.WaitAsync();
        try
        {
            if(!replay) throw new InvalidOperationException("请先开启即时回放。");
            if(recoveryPending || !Running) throw new InvalidOperationException("正在恢复捕获，暂时没有可保存的新回放；恢复后会自动重新累计。");
            List<byte[]> snapshot;
            lock(sync) { snapshot=ring.Select(s=>s.Data).ToList(); if(current.Length>0) snapshot.Add(current.ToArray()); }
            if(snapshot.Count==0) throw new IOException("暂无回放画面，请等待几秒。");
            Directory.CreateDirectory(settings.SaveFolder);
            var file=Path.Combine(settings.SaveFolder,$"Replay_{DateTime.Now:yyyyMMdd_HHmmss_fff}.pending.ts");
            await using(var output=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true))
                foreach(var bytes in snapshot) await output.WriteAsync(bytes);
            await Remux(file,Path.ChangeExtension(file.Replace(".pending",""),"mp4"));
        }
        finally { gate.Release(); }
    }
    async Task Remux(string input,string output)
    {
        var temp=output+".partial.mp4";
        try
        {
            await Tools.Run(Tools.Find("ffmpeg.exe"),"-hide_banner","-loglevel","error","-y","-fflags","+genpts","-i",input,"-map","0","-c","copy","-avoid_negative_ts","make_zero","-movflags","+faststart",temp);
            File.Move(temp,output); File.Delete(input); Message?.Invoke("已保存："+output);
        }
        catch { Message?.Invoke("导出失败，原始视频保留在："+input); throw; }
        finally { if(File.Exists(temp)) File.Delete(temp); }
    }
    public async Task Screenshot()
    {
        Directory.CreateDirectory(settings.SaveFolder);
        var file=Path.Combine(settings.SaveFolder,$"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        await Tools.Run(Tools.Find("ffmpeg.exe"),"-hide_banner","-loglevel","error","-y","-filter_complex",VideoSource(settings)+",hwdownload,format=bgra","-frames:v","1",file);
        Message?.Invoke("已保存："+file);
    }
    public async Task Maintain()
    {
        if(!await gate.WaitAsync(0)) return;
        try
        {
            if(capture is { HasExited:true } || reader is { IsFaulted:true })
            {
                string cause=reader?.Exception?.GetBaseException().Message??(errors?.IsCompleted==true?await errors!:"录屏意外退出");
                string? interrupted=EndRecordingPart();
                await StopSession();
                recoveryPending=replay||recordingRequested; nextRecovery=DateTime.UtcNow;
                Message?.Invoke("捕获中断，正在自动恢复。"+cause);
                if(interrupted!=null)
                {
                    try { await Remux(interrupted,Path.ChangeExtension(interrupted.Replace(".pending",""),"mp4")); }
                    catch(Exception ex) { Message?.Invoke("中断前的录像保留在 "+interrupted+"："+ex.Message); }
                }
            }
            if(recoveryPending && DateTime.UtcNow>=nextRecovery && (replay||recordingRequested))
            {
                try
                {
                    await EnsureStarted();
                    if(recordingRequested) BeginRecordingPart();
                    recoveryPending=false; recoveryFailures=0; LastError=null;
                    Message?.Invoke(replay?"捕获已恢复，即时回放从恢复时重新累计。"+(recordingRequested?"手动录制已分段续录。":""):"捕获已恢复，手动录制已分段续录。");
                }
                catch(Exception ex) { QueueRecovery(ex); }
            }
            if(Recording && new DriveInfo(Path.GetPathRoot(settings.SaveFolder)!).AvailableFreeSpace<512L*1024*1024)
            { EndRecordingPart(); recordingRequested=false; Message?.Invoke("磁盘空间不足，录制已停止，原始 TS 已保留。"); if(!replay) { recoveryPending=false; await StopSession(); } }
        }
        finally { gate.Release(); }
    }
    async Task StopSession()
    {
        if(capture!=null)
        {
            try
            {
                if(!capture.HasExited)
                {
                    try { await capture.StandardInput.WriteLineAsync("q"); await capture.StandardInput.FlushAsync(); }
                    catch(IOException) { } // A failed capture may already have closed its input pipe.
                    catch(InvalidOperationException) { }
                    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    try { await capture.WaitForExitAsync(timeout.Token); } catch(OperationCanceledException) { capture.Kill(true); await capture.WaitForExitAsync(); }
                }
                if(errors!=null) LastError=await errors;
                if(reader!=null) { try { await reader; } catch(Exception ex) { LastError=ex.Message; } }
            }
            finally { audio?.Dispose(); audio=null; capture.Dispose(); capture=null; reader=null; }
        }
        else { audio?.Dispose(); audio=null; }
        lock(sync) { ring.Clear(); ringBytes=0; current.SetLength(0); }
    }
    public async Task Shutdown()
    {
        if(Recording) await ToggleRecording();
        await gate.WaitAsync(); try { replay=false; recoveryPending=false; await StopSession(); } finally { gate.Release(); }
    }
}

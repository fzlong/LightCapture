using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using static LightCapture.ObsNative;
namespace LightCapture;

sealed record ObsCommand(string Action,Settings? Settings=null,bool Enabled=false,int Method=0);
sealed record ObsReply(string? Error=null,string? Path=null,bool Replay=false,bool Recording=false,
    long CacheBytes=0,double CacheSeconds=0,long Frames=0,double PacketAge=0,uint SourceWidth=0,uint SourceHeight=0,int CaptureMethod=0);

// Native capture lives outside the UI process. Pipe EOF also stops capture if the UI crashes.
sealed class ObsWorker : IDisposable
{
    readonly Settings settings;
    readonly string root;
    readonly PacketCallback packets;
    readonly object metricsLock=new();
    readonly Queue<(long Size,long Time,bool Key)> metadata=new();
    IntPtr source,scene,audioSource,videoEncoder,audioEncoder,replay,record;
    bool initialized;
    string? recordingPath;
    long cacheBytes,frames,lastPacket=Environment.TickCount64;
    int keyframes;
    int captureMethod;
    string monitorId="";
    public ObsWorker(Settings settings)
    {
        this.settings=settings; packets=OnPacket;
        root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..",".."));
        if(!File.Exists(Path.Combine(root,"bin","64bit","obs.dll"))) throw new FileNotFoundException("缺少 OBS 32.2.2 运行库，请保留轻录文件夹中的 obs-runtime。");
        SetDllDirectory(Path.Combine(root,"bin","64bit"));
        Directory.SetCurrentDirectory(Path.Combine(root,"bin","64bit"));
    }
    public static void Run(string pipeName)
    {
        using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous);
        pipe.Connect(15000);
        using var input=new StreamReader(pipe); using var output=new StreamWriter(pipe) { AutoFlush=true };
        ObsWorker? worker=null;
        try
        {
            while(input.ReadLine() is {} line)
            {
                ObsReply reply;
                try
                {
                    var command=JsonSerializer.Deserialize<ObsCommand>(line)??throw new IOException("无效捕获命令");
                    if(command.Action=="start") { worker=new(command.Settings!); worker.Initialize(); }
                    if(worker==null) throw new InvalidOperationException("捕获未初始化");
                    string? path=null;
                    switch(command.Action)
                    {
                        case "replay": worker.SetReplay(command.Enabled); break;
                        case "record": path=worker.SetRecording(command.Enabled); break;
                        case "save": path=worker.SaveReplay(); break;
                        case "method": worker.SetCaptureMethod(command.Method); break;
                        case "stop": worker.Dispose(); worker=null; break;
                    }
                    reply=worker?.Status()??new();
                    if(path!=null) reply=reply with {Path=path};
                }
                catch(Exception ex) { reply=new(Error:ex.Message); }
                output.WriteLine(JsonSerializer.Serialize(reply));
                if(worker==null) break;
            }
        }
        finally { worker?.Dispose(); }
    }
    void Initialize()
    {
        Directory.CreateDirectory(settings.SaveFolder);
        if(!obs_startup("en-US",null!,IntPtr.Zero)) throw new IOException("OBS 初始化失败");
        initialized=true;
        var screens=Screen.AllScreens;
        if(settings.DisplayIndex<0||settings.DisplayIndex>=screens.Length) throw new IOException("选定显示器不存在，请重新选择显示器。");
        var screen=screens[settings.DisplayIndex];
        monitorId=screen.DeviceName; captureMethod=settings.CaptureMethod;
        var video=new VideoInfo {Graphics="libobs-d3d11.dll",FpsNum=(uint)settings.Fps,FpsDen=1,
            BaseWidth=(uint)(screen.Bounds.Width/2*2),BaseHeight=(uint)(screen.Bounds.Height/2*2),
            Width=(uint)(screen.Bounds.Width/2*2),Height=(uint)(screen.Bounds.Height/2*2),Format=2,GpuConversion=true,Colorspace=2,Range=1,Scale=2};
        int result=obs_reset_video(ref video);
        if(result!=0) throw new IOException("OBS D3D11 视频初始化失败："+result);
        var audio=new AudioInfo {Rate=48000,Speakers=2};
        if(!obs_reset_audio(ref audio)) throw new IOException("OBS 音频初始化失败");
        foreach(string module in new[]{"win-capture","obs-ffmpeg","obs-nvenc","win-wasapi"})
        {
            var path=Path.Combine(root,"obs-plugins","64bit",module+".dll");
            if(obs_open_module(out var handle,path,Path.Combine(root,"data","obs-plugins",module))!=0 || !obs_init_module(handle))
                throw new IOException("OBS 组件载入失败："+module);
        }
        obs_post_load_modules();
        // Monitor capture remains active across foreground application changes. No game injection.
        using(var data=new Data(new {monitor_id=screen.DeviceName,method=settings.CaptureMethod==1?2:settings.CaptureMethod==2?1:0,capture_cursor=true,force_sdr=true}))
            source=obs_source_create("monitor_capture","Selected display",data.Handle,IntPtr.Zero);
        Require(source,"显示器捕获");
        scene=obs_scene_create("Continuous display"); Require(scene,"显示器场景");
        var item=obs_scene_add(scene,source); Require(item,"显示器场景项");
        var canvas=new Vec2 {X=video.BaseWidth,Y=video.BaseHeight};
        obs_sceneitem_set_bounds_type(item,2); obs_sceneitem_set_bounds(item,ref canvas);
        obs_set_output_source(0,obs_scene_get_source(scene));
        if(settings.SystemAudio)
        {
            using var data=new Data(new {device_id="default",use_device_timing=true});
            audioSource=obs_source_create("wasapi_output_capture","System sound",data.Handle,IntPtr.Zero);
            Require(audioSource,"系统声音捕获"); obs_set_output_source(1,audioSource);
        }
        using(var data=new Data(new {rate_control="CQP",cqp=settings.Quality,preset="p3",preset2="p3",tune="hq",multipass="disabled",lookahead=false,psycho_aq=false,bf=0,keyint_sec=2}))
            videoEncoder=obs_video_encoder_create("obs_nvenc_h264_tex","Shared NVENC",data.Handle,IntPtr.Zero);
        Require(videoEncoder,"NVENC 编码器"); obs_encoder_set_video(videoEncoder,obs_get_video());
        // Replay output is audiovisual; encode silence too when desktop audio is disabled.
        using(var data=new Data(new {bitrate=192}))
            audioEncoder=obs_audio_encoder_create("ffmpeg_aac","Shared AAC",data.Handle,0,IntPtr.Zero);
        Require(audioEncoder,"AAC 编码器"); obs_encoder_set_audio(audioEncoder,obs_get_audio());
    }
    static void Require(IntPtr handle,string name) { if(handle==IntPtr.Zero) throw new IOException(name+"创建失败，请查看 obs-worker.log。"); }
    public void SetCaptureMethod(int method)
    {
        if(method is <0 or >2) throw new ArgumentException("无效的捕获方式");
        using var data=new Data(new {monitor_id=monitorId,method=method==1?2:method==2?1:0,capture_cursor=true,force_sdr=true});
        obs_source_update(source,data.Handle); captureMethod=method;
    }
    sealed class Data : IDisposable
    {
        public readonly IntPtr Handle;
        public Data(object value) { Handle=obs_data_create_from_json(JsonSerializer.Serialize(value)); Require(Handle,"OBS 设置"); }
        public void Dispose()=>obs_data_release(Handle);
    }
    IntPtr CreateOutput(string id,object options)
    {
        using var data=new Data(options);
        var result=obs_output_create(id,id,data.Handle,IntPtr.Zero); Require(result,id);
        obs_output_set_video_encoder(result,videoEncoder); obs_output_set_audio_encoder(result,audioEncoder,0);
        obs_output_add_packet_callback(result,packets,IntPtr.Zero);
        if(!obs_output_start(result))
        {
            string error=Marshal.PtrToStringUTF8(obs_output_get_last_error(result))??"未知错误";
            obs_output_release(result); throw new IOException("OBS 输出启动失败："+error);
        }
        lastPacket=Environment.TickCount64;
        return result;
    }
    public void SetReplay(bool enabled)
    {
        if(enabled && replay==IntPtr.Zero)
        {
            lock(metricsLock) {metadata.Clear(); cacheBytes=0; keyframes=0;}
            replay=CreateOutput("replay_buffer",new {directory=settings.SaveFolder,format="Replay_%CCYY%MM%DD_%hh%mm%ss",extension="mp4",allow_spaces=true,max_time_sec=settings.ReplaySeconds,max_size_mb=settings.CacheMegabytes});
        }
        else if(!enabled && replay!=IntPtr.Zero) { StopOutput(ref replay); lock(metricsLock) {metadata.Clear(); cacheBytes=0; keyframes=0;} }
    }
    public string? SetRecording(bool enabled)
    {
        if(enabled && record==IntPtr.Zero)
        {
            recordingPath=Path.Combine(settings.SaveFolder,$"Recording_{DateTime.Now:yyyyMMdd_HHmmss_fff}.mkv");
            record=CreateOutput("ffmpeg_muxer",new {path=recordingPath,muxer_settings="flush_packets=1 cluster_time_limit=1000"});
            for(int i=0;i<80;i++)
            {
                Thread.Sleep(100);
                if(obs_output_get_total_frames(record)>0 && File.Exists(recordingPath) && new FileInfo(recordingPath).Length>0) return null;
                if(!obs_output_active(record)) break;
            }
            StopOutput(ref record);
            throw new IOException("普通录像未成功写入首帧，请查看捕获日志和磁盘空间。");
        }
        if(!enabled && record!=IntPtr.Zero)
        {
            StopOutput(ref record); var input=recordingPath!; recordingPath=null;
            var file=Path.ChangeExtension(input,"mp4"); var temp=file+".partial.mp4";
            try
            {
                Tools.Run(Tools.Find("ffmpeg.exe"),"-hide_banner","-loglevel","error","-y","-i",input,"-map","0","-c","copy","-movflags","+faststart",temp).GetAwaiter().GetResult();
                File.Move(temp,file); File.Delete(input); return file;
            }
            catch { throw new IOException("录像已保留为 MKV："+input+"；转换 MP4 失败。"); }
            finally {if(File.Exists(temp)) File.Delete(temp);}
        }
        return null;
    }
    string? LastReplay()
    {
        var data=new CallData();
        try
        {
            if(!proc_handler_call(obs_output_get_proc_handler(replay),"get_last_replay",ref data)) throw new IOException("OBS 不支持读取回放路径");
            return calldata_get_string(ref data,"path",out var path)?Marshal.PtrToStringUTF8(path):null;
        }
        finally {if(data.Stack!=IntPtr.Zero) bfree(data.Stack);}
    }
    public string SaveReplay()
    {
        if(replay==IntPtr.Zero || !obs_output_active(replay)) throw new IOException("即时回放尚未启动");
        lock(metricsLock) { if(metadata.Count==0) throw new IOException("暂时没有回放画面，请等待几秒。"); }
        string? before=LastReplay();
        var data=new CallData();
        if(!proc_handler_call(obs_output_get_proc_handler(replay),"save",ref data)) throw new IOException("OBS 保存回放命令失败");
        for(int i=0;i<600;i++)
        {
            Thread.Sleep(100);
            string? path=LastReplay();
            if(!string.IsNullOrEmpty(path) && path!=before && File.Exists(path) && new FileInfo(path).Length>0) return path;
            if(!obs_output_active(replay)) throw new IOException("回放输出已中断");
        }
        throw new TimeoutException("回放导出超时，请查看 obs-worker.log；捕获仍继续运行。");
    }
    void OnPacket(IntPtr output,IntPtr pointer,IntPtr time,IntPtr param)
    {
        if(pointer==IntPtr.Zero) return;
        try
        {
            var p=Marshal.PtrToStructure<Packet>(pointer);
            if(p.Type==1) {Interlocked.Increment(ref frames); Interlocked.Exchange(ref lastPacket,Environment.TickCount64);}
            if(output!=replay) return;
            lock(metricsLock)
            {
                bool key=p.Type==1&&p.Keyframe;
                // Mirror libobs replay retention using metadata only, without duplicating encoded payloads.
                while(metadata.Count>0 && keyframes>2 && (cacheBytes+(long)p.Size>settings.CacheMegabytes*1048576L || p.DtsUsec-metadata.Peek().Time>settings.ReplaySeconds*1000000L))
                {
                    var old=metadata.Dequeue(); cacheBytes-=old.Size;
                    if(old.Key)
                    {
                        keyframes--;
                        while(metadata.Count>0 && !metadata.Peek().Key) cacheBytes-=metadata.Dequeue().Size;
                    }
                }
                metadata.Enqueue(((long)p.Size,p.DtsUsec,key)); cacheBytes+=(long)p.Size; if(key) keyframes++;
            }
        }
        catch { /* Never unwind a managed exception through the C ABI. */ }
    }
    public ObsReply Status()
    {
        lock(metricsLock)
        {
            double seconds=metadata.Count>1?(metadata.Last().Time-metadata.Peek().Time)/1000000.0:0;
            return new(Replay:replay!=IntPtr.Zero&&obs_output_active(replay),Recording:record!=IntPtr.Zero&&obs_output_active(record),
                CacheBytes:cacheBytes,CacheSeconds:seconds,Frames:Interlocked.Read(ref frames),PacketAge:(Environment.TickCount64-Interlocked.Read(ref lastPacket))/1000.0,
                SourceWidth:source==IntPtr.Zero?0:obs_source_get_width(source),SourceHeight:source==IntPtr.Zero?0:obs_source_get_height(source),CaptureMethod:captureMethod);
        }
    }
    void StopOutput(ref IntPtr output)
    {
        if(output==IntPtr.Zero) return;
        obs_output_stop(output);
        var watch=Stopwatch.StartNew();
        while(obs_output_active(output)&&watch.ElapsedMilliseconds<8000) Thread.Sleep(50);
        if(obs_output_active(output)) obs_output_force_stop(output);
        obs_output_remove_packet_callback(output,packets,IntPtr.Zero);
        obs_output_release(output); output=IntPtr.Zero;
    }
    public void Dispose()
    {
        if(!initialized) return;
        StopOutput(ref record); StopOutput(ref replay);
        obs_set_output_source(0,IntPtr.Zero); obs_set_output_source(1,IntPtr.Zero);
        if(scene!=IntPtr.Zero) {obs_source_remove(obs_scene_get_source(scene));obs_scene_release(scene);}
        if(source!=IntPtr.Zero) obs_source_release(source);
        if(audioSource!=IntPtr.Zero) obs_source_release(audioSource);
        if(videoEncoder!=IntPtr.Zero) obs_encoder_release(videoEncoder);
        if(audioEncoder!=IntPtr.Zero) obs_encoder_release(audioEncoder);
        obs_shutdown(); initialized=false;
    }
}

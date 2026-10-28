using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
namespace LightCapture;

sealed record GifResult(string File,int Colors,double Quality,long Bytes);

static class GifExporter
{
    internal static string Normalize(Settings s)=>$"fps={s.GifFps},scale=w='min({s.GifMaxSide},iw)':h='min({s.GifMaxSide},ih)':force_original_aspect_ratio=decrease:flags=lanczos";
    public static async Task<GifResult> Export(string input,string output,Settings settings)
    {
        string dir=Path.Combine(Path.GetDirectoryName(input)!,"palettes-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        string temp=output+".partial.gif";
        try
        {
            string small=Path.Combine(dir,"128.gif");
            await Encode(input,small,128,settings,dir).ConfigureAwait(false);
            double quality=await Score(input,small,settings).ConfigureAwait(false);
            int colors=128;
            if(quality<.975 || double.IsNaN(quality))
            {
                string full=Path.Combine(dir,"256.gif");
                await Encode(input,full,256,settings,dir).ConfigureAwait(false);
                small=full; colors=256;
            }
            File.Copy(small,temp,true); File.Move(temp,output,false);
            return new(output,colors,quality,new FileInfo(output).Length);
        }
        finally
        {
            if(File.Exists(temp)) File.Delete(temp);
            // dir is a freshly-created private directory underneath this recording's cache.
            try { Directory.Delete(dir,true); } catch(IOException) { }
        }
    }
    static async Task Encode(string input,string output,int colors,Settings settings,string dir)
    {
        var palette=Path.Combine(dir,$"palette-{colors}.png");
        // Two passes keep memory bounded; a split + palettegen graph would buffer the whole clip.
        await Tools.Run(Tools.Find("ffmpeg.exe"),"-hide_banner","-loglevel","error","-y","-threads","2","-i",input,"-vf",Normalize(settings)+$",palettegen=max_colors={colors}:reserve_transparent=1:stats_mode=full","-frames:v","1","-update","1",palette).ConfigureAwait(false);
        string graph=$"[0:v]{Normalize(settings)}[v];[v][1:v]paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle[out]";
        await Tools.Run(Tools.Find("ffmpeg.exe"),"-hide_banner","-loglevel","error","-y","-threads","2","-i",input,"-i",palette,"-filter_complex_threads","2","-filter_complex",graph,"-map","[out]","-an","-gifflags","+offsetting+transdiff","-loop","0",output).ConfigureAwait(false);
    }
    static async Task<double> Score(string input,string gif,Settings settings)
    {
        // GIF delays are centiseconds. Align by decoded frame index so rounded GIF
        // timestamps do not compare one moving frame against its neighbour.
        string graph=$"[0:v]{Normalize(settings)},settb=expr=1/{settings.GifFps},setpts=N,format=gbrp[a];[1:v]settb=expr=1/{settings.GifFps},setpts=N,format=gbrp[b];[a][b]ssim";
        using var process=Process.Start(Tools.Info(Tools.Find("ffmpeg.exe"),"-hide_banner","-threads","2","-i",input,"-ignore_loop","1","-i",gif,"-filter_complex_threads","2","-lavfi",graph,"-an","-f","null","NUL"))!;
        var error=process.StandardError.ReadToEndAsync(); var output=process.StandardOutput.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch { try { process.Kill(true); } catch { } throw; }
        await output.ConfigureAwait(false);
        var match=Regex.Matches(await error.ConfigureAwait(false),@"All:([0-9.]+)").LastOrDefault();
        return process.ExitCode==0 && match!=null && double.TryParse(match.Groups[1].Value,CultureInfo.InvariantCulture,out var score)?score:double.NaN;
    }
}

sealed class GifRecorder
{
    readonly Settings settings;
    readonly SemaphoreSlim gate=new(1,1);
    Process? process;
    Task<string>? error;
    string? source;
    readonly Stopwatch clock=new();
    public bool Recording=>process!=null;
    public bool Exporting { get; private set; }
    public bool CaptureFinished=>process is {HasExited:true};
    public TimeSpan Elapsed=>clock.Elapsed;
    public event Action<string>? Message;
    public GifRecorder(Settings settings) { this.settings=settings; }
    internal static string[] CaptureArguments(Rectangle region,Settings s,string file)=>
        ["-hide_banner","-loglevel","warning","-y","-f","gdigrab","-framerate",s.GifFps.ToString(),"-draw_mouse","1","-offset_x",region.X.ToString(),"-offset_y",region.Y.ToString(),"-video_size",$"{region.Width}x{region.Height}","-i","desktop","-t",s.GifMaxSeconds.ToString(),"-vf",GifExporter.Normalize(s),"-an","-c:v","ffv1","-level","3","-g","1","-threads","2",file];
    public async Task Start(Rectangle region)
    {
        await gate.WaitAsync();
        try
        {
            if(Recording || Exporting) throw new InvalidOperationException("GIF 已在录制或导出。");
            if(region.Width<16 || region.Height<16) throw new ArgumentException("框选区域至少为 16 × 16 像素。");
            string folder=Path.Combine(Settings.DataFolder,"gif-cache",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            if(new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace<512L*1024*1024) throw new IOException("GIF 临时目录的磁盘空间不足 512 MB。");
            source=Path.Combine(folder,"capture.mkv");
            process=Process.Start(Tools.Info(Tools.Find("ffmpeg.exe"),CaptureArguments(region,settings,source)))!;
            error=process.StandardError.ReadToEndAsync(); _=process.StandardOutput.ReadToEndAsync(); clock.Restart();
            await Task.Delay(400);
            if(process.HasExited) { string message=await error; process.Dispose(); process=null; throw new IOException("区域录制启动失败："+message); }
        }
        finally { gate.Release(); }
    }
    public async Task<GifResult?> StopAndExport()
    {
        await gate.WaitAsync();
        try
        {
            if(process==null) return null;
            Exporting=true; var active=process; process=null; clock.Stop();
            try
            {
                if(!active.HasExited)
                {
                    await active.StandardInput.WriteLineAsync("q").ConfigureAwait(false); await active.StandardInput.FlushAsync().ConfigureAwait(false);
                    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    try { await active.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                    catch(OperationCanceledException) { active.Kill(true); await active.WaitForExitAsync().ConfigureAwait(false); }
                }
                var diagnostic=error==null?"":await error.ConfigureAwait(false);
                if(active.ExitCode!=0) throw new IOException("GIF 捕获被中断："+diagnostic);
                Directory.CreateDirectory(settings.SaveFolder);
                var output=Path.Combine(settings.SaveFolder,$"GIF_{DateTime.Now:yyyyMMdd_HHmmss_fff}.gif");
                var result=await GifExporter.Export(source!,output,settings).ConfigureAwait(false);
                File.Delete(source!); Directory.Delete(Path.GetDirectoryName(source!)!);
                Message?.Invoke($"GIF 已保存（{result.Bytes/1048576.0:0.##} MB）：{result.File}");
                return result;
            }
            catch { Message?.Invoke("GIF 导出失败，原始画面保留在："+source); throw; }
            finally { active.Dispose(); Exporting=false; }
        }
        finally { gate.Release(); }
    }
}

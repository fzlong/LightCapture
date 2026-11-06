using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
namespace LightCapture;

sealed record GifResult(string File,int Colors,long Bytes,string Encoder,double EncodingSeconds);

static class GifExporter
{
    internal static string Normalize(Settings s)=>$"fps={s.GifFps},scale=w='min({s.GifMaxSide},iw)':h='min({s.GifMaxSide},ih)':force_original_aspect_ratio=decrease:flags=lanczos";
    public static async Task<GifResult> Export(string input,string output,Settings settings,Action<string>? progress=null)
    {
        var clock=Stopwatch.StartNew();
        string temp=output+".partial.gif";
        try
        {
            string? gifski;
            try {gifski=Tools.Find("gifski.exe");} catch(FileNotFoundException) {gifski=null;}
            string encoder;
            if(gifski!=null)
            {
                encoder="gifski";progress?.Invoke("GIF 正在快速保色压制…");
                await EncodeGifski(input,temp,settings,gifski).ConfigureAwait(false);
            }
            else
            {
                encoder="FFmpeg";progress?.Invoke("GIF 保色编码器未安装，正在使用 FFmpeg 兼容压制…");
                await EncodeCompatible(input,temp,settings).ConfigureAwait(false);
            }
            if(!File.Exists(temp)||new FileInfo(temp).Length<14) throw new IOException("GIF 编码没有产生有效文件。");
            File.Move(temp,output,false);
            return new(output,256,new FileInfo(output).Length,encoder,clock.Elapsed.TotalSeconds);
        }
        finally
        {
            if(File.Exists(temp)) File.Delete(temp);
        }
    }
    static async Task EncodeCompatible(string input,string output,Settings settings)
    {
        string dir=Path.Combine(Path.GetDirectoryName(input)!,"palettes-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            var palette=Path.Combine(dir,"palette.png");
            // A fixed 256-color two-pass fallback avoids the former 128-color attempt, full SSIM
            // pass and second encode. Do not buffer an entire clip in a split/palettegen graph.
            await Tools.Run(Tools.Find("ffmpeg.exe"),"-hide_banner","-loglevel","error","-y","-threads","4","-filter_threads","4","-i",input,"-vf",Normalize(settings)+",palettegen=max_colors=256:reserve_transparent=1:stats_mode=full","-frames:v","1","-update","1",palette).ConfigureAwait(false);
            string graph=$"[0:v]{Normalize(settings)}[v];[v][1:v]paletteuse=dither=sierra2_4a:diff_mode=rectangle[out]";
            await Tools.Run(Tools.Find("ffmpeg.exe"),"-hide_banner","-loglevel","error","-y","-threads","4","-i",input,"-i",palette,"-filter_complex_threads","4","-filter_complex",graph,"-map","[out]","-an","-gifflags","+offsetting+transdiff","-loop","0",output).ConfigureAwait(false);
        }
        finally {try {Directory.Delete(dir,true);} catch(IOException) { }}
    }
    internal static string GifskiFilter(Settings settings)=>Normalize(settings)+":out_color_matrix=bt709:out_range=tv,format=yuv444p";
    internal static string[] GifskiArguments(string output,int width,int height,Settings settings)=>
        ["--fast","--fps",settings.GifFps.ToString(CultureInfo.InvariantCulture),"--quality",Math.Clamp(settings.GifQuality,50,100).ToString(CultureInfo.InvariantCulture),
         "--width",width.ToString(CultureInfo.InvariantCulture),"--height",height.ToString(CultureInfo.InvariantCulture),"--repeat","0","--quiet","--y4m-color-override","bt709","--output",output,"-"];
    static async Task EncodeGifski(string input,string output,Settings settings,string gifski)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(180,settings.GifMaxSeconds*4)));
        using var decoder=Process.Start(Tools.Info(Tools.Find("ffmpeg.exe"),"-hide_banner","-loglevel","error","-threads","4","-filter_threads","4","-i",input,"-vf",GifskiFilter(settings),"-an","-pix_fmt","yuv444p","-f","yuv4mpegpipe","pipe:1"))!;
        var decodeError=decoder.StandardError.ReadToEndAsync();
        Process? encoder=null;Task? pumping=null;
        try
        {
            // Read just the Y4M header (no StreamReader read-ahead into binary frames). Explicit
            // dimensions prevent gifski's default size limit from shrinking or stretching the GIF.
            var header=new List<byte>();byte[] one=new byte[1];
            while(header.Count<4096)
            {
                if(await decoder.StandardOutput.BaseStream.ReadAsync(one,timeout.Token).ConfigureAwait(false)==0) throw new IOException("FFmpeg 未输出 GIF 原始帧："+await decodeError.ConfigureAwait(false));
                header.Add(one[0]);if(one[0]==10) break;
            }
            string text=Encoding.ASCII.GetString(header.ToArray());
            var dimensions=Regex.Match(text,@"^YUV4MPEG2 W(\d+) H(\d+) ");
            if(!dimensions.Success||header[^1]!=10) throw new IOException("FFmpeg 输出的 GIF 原始帧格式无效。");
            int width=int.Parse(dimensions.Groups[1].Value,CultureInfo.InvariantCulture),height=int.Parse(dimensions.Groups[2].Value,CultureInfo.InvariantCulture);
            encoder=Process.Start(Tools.Info(gifski,GifskiArguments(output,width,height,settings)))!;
            var encodeError=encoder.StandardError.ReadToEndAsync();var encodeOutput=encoder.StandardOutput.ReadToEndAsync();
            var sink=encoder;
            pumping=Task.Run(async()=>
            {
                try
                {
                    await sink.StandardInput.BaseStream.WriteAsync(header.ToArray(),timeout.Token).ConfigureAwait(false);
                    await decoder.StandardOutput.BaseStream.CopyToAsync(sink.StandardInput.BaseStream,65536,timeout.Token).ConfigureAwait(false);
                }
                catch {Kill(decoder);throw;}
                finally {sink.StandardInput.Close();}
            });
            await Task.WhenAll(pumping,decoder.WaitForExitAsync(timeout.Token),encoder.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            await encodeOutput.ConfigureAwait(false);
            string error=await encodeError.ConfigureAwait(false),decode=await decodeError.ConfigureAwait(false);
            if(decoder.ExitCode!=0||encoder.ExitCode!=0) throw new IOException("GIF 保色压制失败："+(error+decode)[^Math.Min(error.Length+decode.Length,1800)..]);
        }
        catch
        {
            Kill(decoder);if(encoder!=null) Kill(encoder);
            if(pumping!=null) {try {await pumping.ConfigureAwait(false);} catch { }}
            throw;
        }
        finally {encoder?.Dispose();}
    }
    static void Kill(Process process) {try {if(!process.HasExited) process.Kill(true);} catch(InvalidOperationException) { }}
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
                var result=await GifExporter.Export(source!,output,settings,text=>Message?.Invoke(text)).ConfigureAwait(false);
                File.Delete(source!); Directory.Delete(Path.GetDirectoryName(source!)!);
                Message?.Invoke($"GIF 已保存（{result.Bytes/1048576.0:0.##} MB，压制 {result.EncodingSeconds:0.#} 秒）：{result.File}");
                return result;
            }
            catch { Message?.Invoke("GIF 导出失败，原始画面保留在："+source); throw; }
            finally { active.Dispose(); Exporting=false; }
        }
        finally { gate.Release(); }
    }
}

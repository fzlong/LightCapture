using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.IO.Pipes;
using System.Text.Json;
using LightCapture;

if(args.Contains("--native-probe"))
{
    var info=FpsMonitor.CollectorStartInfo(args[^2],"LightCaptureProbe_"+Guid.NewGuid().ToString("N"));
    foreach(var arg in new[]{"--timed","8","--terminate_after_timed"}) info.ArgumentList.Add(arg);
    using var collector=Process.Start(info)!;
    var text=collector.StandardOutput.ReadToEndAsync(); var errors=collector.StandardError.ReadToEndAsync();
    await collector.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
    string csv=await text;
    using var native=new FpsMonitor(); using var bytes=new MemoryStream(Encoding.UTF8.GetBytes(csv));
    await ((Task)typeof(FpsMonitor).GetMethod("Read",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(native,[bytes])!).WaitAsync(TimeSpan.FromSeconds(5));
    var processes=csv.Split('\n').Skip(1).Select(line=>line.Trim().Split(',')).Where(p=>p.Length>2&&uint.TryParse(p[1],out _)).GroupBy(p=>(Name:p[0],Pid:uint.Parse(p[1]))).Select(g=>new {g.Key.Name,g.Key.Pid,Frames=g.Count(),Fps=native.Get(g.Key.Pid)}).ToArray();
    File.WriteAllText(args[^1],JsonSerializer.Serialize(new {Exit=collector.ExitCode,Bytes=csv.Length,Rows=processes,Error=await errors},new JsonSerializerOptions {WriteIndented=true}));
    File.WriteAllText(Path.ChangeExtension(args[^1],"csv"),csv);
    return;
}

if(args.Contains("--output_stdout"))
{
    // Captured native pipe output uses ASCII-compatible text, unlike file output.
    byte[] csv=Encoding.UTF8.GetBytes("Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,msInPresentAPI,msBetweenPresents\r\ngame.exe,987,0x1,DXGI,0,0,0,1.0,0.1,20\r\n");
    Console.OpenStandardOutput().Write(csv);
    Console.OpenStandardError().Write(Encoding.UTF8.GetBytes("capture diagnostic\r\n"));
    return;
}

using(var collector=Process.Start(FpsMonitor.CollectorStartInfo(Environment.ProcessPath!,"Fixture"))!)
{
    var text=collector.StandardOutput.ReadToEndAsync(); var error=collector.StandardError.ReadToEndAsync();
    await collector.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    string decoded=await text;
    if(decoded.Contains('\0')) throw new Exception("Native collector pipe output decoded incorrectly.");
    if(!(await error).Contains("capture diagnostic")) throw new Exception("PresentMon diagnostics decoded incorrectly.");
    using var native=new FpsMonitor(); using var bytes=new MemoryStream(Encoding.UTF8.GetBytes(decoded));
    await ((Task)typeof(FpsMonitor).GetMethod("Read",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(native,[bytes])!).WaitAsync(TimeSpan.FromSeconds(2));
    if(native.Get(987)!=50) throw new Exception("Native collector byte-to-FPS integration failed.");
    Console.WriteLine("PASS: subprocess stdout/stderr -> worker text -> parser -> 50 FPS.");
}

using var monitor=new FpsMonitor();
using var feed=new FrameFloodStream();
var watch=Stopwatch.StartNew();
var consume=(Task)typeof(FpsMonitor).GetMethod("Read",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(monitor,[feed])!;
long returned=watch.ElapsedMilliseconds;
await consume.WaitAsync(TimeSpan.FromSeconds(12));
Console.WriteLine($"Reader call returned in {returned} ms (UI budget: 100 ms). Sample={monitor.Get(123):0.00}");
if(returned>100) { Console.WriteLine("FAIL: FPS reading blocks its caller before returning a Task."); Environment.ExitCode=1; }
else Console.WriteLine("PASS: frame intake runs away from the UI caller.");
if(monitor.Get(123) is not {} measured || Math.Abs(measured-60)>0.1) throw new Exception("Frame intake lost valid FPS data.");
foreach(var csv in new[]{
    "Application,ProcessID,SwapChainAddress,MsBetweenPresents\ngame.exe,321,0x2,10\n",
    "\ufeff\"Application\",\"processid\",\"swapchainaddress\",\"msbetweenpresents\"\n\"game.exe\",\"321\",\"0x2\",\"10\"\n",
    "SwapChainAddress,msBetweenPresents,Application,ProcessID\n0x2,10,game.exe,321\n"})
{
    using var variant=new FpsMonitor(); using var bytes=new MemoryStream(Encoding.UTF8.GetBytes(csv));
    await ((Task)typeof(FpsMonitor).GetMethod("Read",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(variant,[bytes])!).WaitAsync(TimeSpan.FromSeconds(2));
    if(variant.Get(321)!=100) throw new Exception("PresentMon schema variation rejected.");
}
Console.WriteLine("PASS: real v1 schema, case variations, BOM/quotes, and reordered columns produce correct FPS.");
using(var invalid=new FpsMonitor())
using(var bytes=new MemoryStream(Encoding.UTF8.GetBytes("Application,ProcessID\ngame.exe,123\n")))
{
    await ((Task)typeof(FpsMonitor).GetMethod("Read",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(invalid,[bytes])!).WaitAsync(TimeSpan.FromSeconds(2));
    if(invalid.Error==null || !invalid.Error.Contains("缺少")) throw new Exception("Invalid schema silently waits forever.");
}

// A silent connected helper must not prevent the UI from disabling FPS.
using var quiet=new FpsMonitor();
string name="LightCaptureRegression_"+Guid.NewGuid().ToString("N");
using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
var connected=server.WaitForConnectionAsync(); await client.ConnectAsync(2000); await connected;
typeof(FpsMonitor).GetField("pipe",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(quiet,server);
typeof(FpsMonitor).GetField("lifetime",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(quiet,new CancellationTokenSource());
var pending=(Task)typeof(FpsMonitor).GetMethod("Read",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(quiet,[server])!;
using var control=new StreamReader(client,Encoding.UTF8,false,1024,true);
var disconnected=control.ReadLineAsync();
await Task.Delay(50);
watch.Restart(); quiet.Dispose(); long stopped=watch.ElapsedMilliseconds;
await pending.WaitAsync(TimeSpan.FromSeconds(2));
if(await disconnected.WaitAsync(TimeSpan.FromSeconds(2))!=null || stopped>100) throw new Exception("Silent FPS helper blocks shutdown.");
if(quiet.Get(123)!=null || quiet.Error!=null) throw new Exception("Stopped FPS reader published stale state.");
Console.WriteLine($"PASS: silent helper disconnected in {stopped} ms; no stale samples or errors.");

sealed class FrameFloodStream : Stream
{
    // Real PresentMon 2.6 --v1_metrics schema (lowercase 'ms').
    readonly byte[] line=Encoding.UTF8.GetBytes("Client-Win64-Shipping.exe,123,0x111,DXGI,0,0,0,1.0,0.1,16.667\n");
    readonly byte[] header=Encoding.UTF8.GetBytes("Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,msInPresentAPI,msBetweenPresents\n");
    int position,remaining=10000;
    public override int Read(byte[] buffer,int offset,int count)
    {
        if(position==0) { header.CopyTo(buffer,offset); position=1; return header.Length; }
        if(remaining<=0) return 0;
        Thread.Sleep(2); int n=Math.Min(count/line.Length,remaining);
        for(int i=0;i<n;i++) line.CopyTo(buffer,offset+i*line.Length);
        remaining-=n; return n*line.Length;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)
    { byte[] temp=new byte[buffer.Length]; int n=Read(temp,0,temp.Length); temp.AsMemory(0,n).CopyTo(buffer); return ValueTask.FromResult(n); }
    public override bool CanRead=>true; public override bool CanSeek=>false; public override bool CanWrite=>false;
    public override long Length=>throw new NotSupportedException(); public override long Position { get=>throw new NotSupportedException(); set=>throw new NotSupportedException(); }
    public override void Flush(){} public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException(); public override void SetLength(long v)=>throw new NotSupportedException(); public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
}

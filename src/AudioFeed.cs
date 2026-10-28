using System.IO.Pipes;
using System.Diagnostics;
using NAudio.Wave;
namespace LightCapture;
sealed class AudioFeed : IDisposable
{
    readonly WasapiLoopbackCapture capture=new();
    readonly BufferedWaveProvider buffer;
    readonly NamedPipeServerStream pipe;
    readonly CancellationTokenSource cancel=new();
    Task? pump;
    public string PipePath { get; }
    public int Rate=>capture.WaveFormat.SampleRate;
    public int Channels=>capture.WaveFormat.Channels;
    public AudioFeed()
    {
        if(capture.WaveFormat.BitsPerSample!=32) throw new NotSupportedException("音频设备需要 32 位浮点格式。");
        buffer=new(capture.WaveFormat) { BufferDuration=TimeSpan.FromSeconds(2),DiscardOnBufferOverflow=true,ReadFully=true };
        var name="LightCaptureAudio_"+Guid.NewGuid().ToString("N"); PipePath=@"\\.\pipe\"+name;
        pipe=new(name,PipeDirection.Out,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        capture.DataAvailable+=(_,e)=>buffer.AddSamples(e.Buffer,0,e.BytesRecorded);
    }
    public void Start()
    {
        pump=Task.Run(async()=>
        {
            try
            {
                await pipe.WaitForConnectionAsync(cancel.Token); capture.StartRecording();
                byte[] block=new byte[Rate/50*capture.WaveFormat.BlockAlign]; var clock=Stopwatch.StartNew(); long count=0;
                while(!cancel.IsCancellationRequested)
                {
                    buffer.Read(block,0,block.Length); await pipe.WriteAsync(block,cancel.Token); count++;
                    var wait=count*20-clock.ElapsedMilliseconds; if(wait>0) await Task.Delay((int)wait,cancel.Token);
                }
            }
            catch(OperationCanceledException) { } catch(ObjectDisposedException) { } catch(IOException) { }
        });
    }
    public void Dispose()
    {
        cancel.Cancel(); try { capture.StopRecording(); } catch { } pipe.Dispose();
        try { pump?.GetAwaiter().GetResult(); } catch { } capture.Dispose(); cancel.Dispose();
    }
}

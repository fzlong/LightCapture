using System.Runtime.InteropServices;
namespace LightCapture;

// ABI pinned to the official OBS Studio 32.2.2 x64 distribution.
static class ObsNative
{
    const string D="obs.dll";
    [StructLayout(LayoutKind.Sequential)] internal struct VideoInfo
    {
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string Graphics;
        public uint FpsNum,FpsDen,BaseWidth,BaseHeight,Width,Height;
        public int Format; public uint Adapter;
        [MarshalAs(UnmanagedType.I1)] public bool GpuConversion;
        public int Colorspace,Range,Scale;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct AudioInfo { public uint Rate; public int Speakers; }
    [StructLayout(LayoutKind.Sequential)] internal struct Vec2 { public float X,Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct CallData { public IntPtr Stack; public nuint Size,Capacity; [MarshalAs(UnmanagedType.I1)] public bool Fixed; }
    [StructLayout(LayoutKind.Sequential)] internal struct Packet
    {
        public IntPtr Data; public nuint Size; public long Pts,Dts; public int TimebaseNum,TimebaseDen,Type;
        [MarshalAs(UnmanagedType.I1)] public bool Keyframe;
        public long DtsUsec,SysDtsUsec; public int Priority,DropPriority; public nuint Track; public IntPtr Encoder;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void PacketCallback(IntPtr output,IntPtr packet,IntPtr time,IntPtr param);
    [DllImport("kernel32",CharSet=CharSet.Unicode)] internal static extern bool SetDllDirectory(string path);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool obs_startup([MarshalAs(UnmanagedType.LPUTF8Str)]string locale,[MarshalAs(UnmanagedType.LPUTF8Str)]string config,IntPtr profiler);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void obs_shutdown();
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int obs_reset_video(ref VideoInfo info);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool obs_reset_audio(ref AudioInfo info);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int obs_open_module(out IntPtr module,[MarshalAs(UnmanagedType.LPUTF8Str)]string path,[MarshalAs(UnmanagedType.LPUTF8Str)]string data);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool obs_init_module(IntPtr module);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void obs_post_load_modules();
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr obs_data_create_from_json([MarshalAs(UnmanagedType.LPUTF8Str)]string json);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void obs_data_release(IntPtr data);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr obs_source_create([MarshalAs(UnmanagedType.LPUTF8Str)]string id,[MarshalAs(UnmanagedType.LPUTF8Str)]string name,IntPtr settings,IntPtr hotkeys);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void obs_source_release(IntPtr source);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void obs_source_remove(IntPtr source);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void obs_source_update(IntPtr source,IntPtr settings);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void obs_set_output_source(uint channel,IntPtr source);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern uint obs_source_get_width(IntPtr source);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern uint obs_source_get_height(IntPtr source);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr obs_scene_create([MarshalAs(UnmanagedType.LPUTF8Str)]string name);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr obs_scene_get_source(IntPtr scene);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr obs_scene_add(IntPtr scene,IntPtr source);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void obs_scene_release(IntPtr scene);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void obs_sceneitem_set_bounds_type(IntPtr item,int type);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void obs_sceneitem_set_bounds(IntPtr item,ref Vec2 size);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr obs_video_encoder_create([MarshalAs(UnmanagedType.LPUTF8Str)]string id,[MarshalAs(UnmanagedType.LPUTF8Str)]string name,IntPtr settings,IntPtr hotkeys);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr obs_audio_encoder_create([MarshalAs(UnmanagedType.LPUTF8Str)]string id,[MarshalAs(UnmanagedType.LPUTF8Str)]string name,IntPtr settings,nuint mixer,IntPtr hotkeys);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void obs_encoder_release(IntPtr encoder);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr obs_get_video();
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr obs_get_audio();
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void obs_encoder_set_video(IntPtr encoder,IntPtr video);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void obs_encoder_set_audio(IntPtr encoder,IntPtr audio);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr obs_output_create([MarshalAs(UnmanagedType.LPUTF8Str)]string id,[MarshalAs(UnmanagedType.LPUTF8Str)]string name,IntPtr settings,IntPtr hotkeys);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void obs_output_set_video_encoder(IntPtr output,IntPtr encoder);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void obs_output_set_audio_encoder(IntPtr output,IntPtr encoder,nuint index);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool obs_output_start(IntPtr output);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool obs_output_active(IntPtr output);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void obs_output_stop(IntPtr output);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void obs_output_force_stop(IntPtr output);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void obs_output_release(IntPtr output);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr obs_output_get_last_error(IntPtr output);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern int obs_output_get_total_frames(IntPtr output);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr obs_output_get_proc_handler(IntPtr output);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void obs_output_add_packet_callback(IntPtr output,PacketCallback callback,IntPtr param);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void obs_output_remove_packet_callback(IntPtr output,PacketCallback callback,IntPtr param);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool proc_handler_call(IntPtr handler,[MarshalAs(UnmanagedType.LPUTF8Str)]string name,ref CallData data);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool calldata_get_string(ref CallData data,[MarshalAs(UnmanagedType.LPUTF8Str)]string name,out IntPtr value);
    [DllImport(D,CallingConvention=CallingConvention.Cdecl)] internal static extern void bfree(IntPtr data);
}

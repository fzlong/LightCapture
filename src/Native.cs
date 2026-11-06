using System.Runtime.InteropServices;
namespace LightCapture;
static class Native
{
    public static readonly IntPtr HWND_BROADCAST=new(0xffff);
    public static readonly uint ShowMessage=RegisterWindowMessage("LightCapture.ShowMenu");
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd,int id);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd,uint affinity);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd,uint msg,IntPtr wparam,IntPtr lparam);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint RegisterWindowMessage(string message);
    public static (uint Mods,uint Key) Parse(string text)
    {
        uint mods=0; Keys key=Keys.None;
        foreach(var part in text.Split('+',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries))
        {
            switch(part.ToUpperInvariant())
            {
                case "ALT": mods|=1; break;
                case "CTRL": case "CONTROL": mods|=2; break;
                case "SHIFT": mods|=4; break;
                default:
                    // Enum.TryParse("2") yields Keys.RButton (VK 0x02), not the '2' key (VK 0x32).
                    if(part.Length==1 && part[0]>='0' && part[0]<='9') key=(Keys)(0x30+part[0]-'0');
                    else if(part.All(char.IsDigit)||!Enum.TryParse(part,true,out key)||key==Keys.None||(uint)key>255) throw new ArgumentException("无效快捷键："+text);
                    break;
            }
        }
        if(key==Keys.None||mods==0) throw new ArgumentException("快捷键需要修饰键加一个按键。");
        return(mods|0x4000,(uint)key);
    }
}

using System.Reflection;
using Microsoft.Win32;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        var assembly=Assembly.Load("LightCapture");
        var type=assembly.GetType("LightCapture.MainWindow")!;
        var settings=assembly.GetType("LightCapture.Settings")!;
        var migrate=settings.GetMethod("RefreshAutoStartPath",BindingFlags.Static|BindingFlags.NonPublic)!;
        string run=@"Software\Microsoft\Windows\CurrentVersion\Run";
        using var key=Registry.CurrentUser.CreateSubKey(run);
        var before=key.GetValue("LightCapture");
        var kind=before==null?RegistryValueKind.String:key.GetValueKind("LightCapture");
        try
        {
            // Reproduce a login entry left behind by an older release.
            string old=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","old","LightCapture.exe"));
            key.SetValue("LightCapture",$"\"{old}\" --background");
            if(!(bool)migrate.Invoke(null,null)! || key.GetValue("LightCapture") as string!=$"\"{Environment.ProcessPath}\" --background") throw new Exception("FAIL: launching a newer release leaves the obsolete login path.");
            if((bool)migrate.Invoke(null,null)!) throw new Exception("FAIL: startup migration is not idempotent.");
            Console.WriteLine("PASS: existing old login path migrates to the current executable; repeated migration is unchanged.");
            using var form=(Form)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[false],null)!;
            // Exercise the actual handler while the general settings Save path would be blocked.
            type.GetField("busy",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(form,true);
            var engine=type.GetField("engine",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(form)!;
            engine.GetType().GetField("replay",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(engine,true);
            var checkbox=(CheckBox)type.GetField("autoStart",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(form)!;
            checkbox.Checked=false;
            if(key.GetValue("LightCapture")!=null) throw new Exception("FAIL: unchecking startup did not immediately remove the login entry.");
            checkbox.Checked=true;
            if(key.GetValue("LightCapture") is not string value || value!=$"\"{Environment.ProcessPath}\" --background") throw new Exception("FAIL: checking startup did not register the current executable.");
            checkbox.Checked=false;
            if(key.GetValue("LightCapture")!=null) throw new Exception("FAIL: startup remains registered after disabling again.");
            if((bool)migrate.Invoke(null,null)! || key.GetValue("LightCapture")!=null) throw new Exception("FAIL: launching a new version re-enabled startup after the user disabled it.");
            Console.WriteLine("PASS: actual settings checkbox immediately enables/disables startup without Save or capture shutdown.");
            Console.WriteLine("PASS: disabled startup stays disabled when the application is relaunched.");
        }
        finally
        {
            if(before==null) key.DeleteValue("LightCapture",false); else key.SetValue("LightCapture",before,kind);
        }
    }
}

using LightCapture;
using System.Text.Json;

void Check(bool condition,string message) {if(!condition) throw new Exception(message);}
foreach(int digit in Enumerable.Range(0,10))
    Check(Native.Parse("Alt+"+digit).Key==0x30u+digit,"Digit shortcut parsed as a mouse/control key instead of the top-row digit: Alt+"+digit);
Console.WriteLine("PASS: Alt+0..9 use the Windows digit virtual keys.");

foreach(var keys in new[]{new[]{"Ctrl+Z","Alt+F9","Alt+Shift+F10","Alt+F10","Alt+F1","Ctrl+R"},new[]{"Ctrl+Z","Alt+F9","Alt+Shift+F10","Alt+F10","Alt+F1","Ctrl+R","Alt+F2"}})
{
    var value=Settings.FromJson(JsonSerializer.Serialize(new {Hotkeys=keys,ReplaySeconds=300,CacheMegabytes=1024,SaveFolder="D:\\Clips"}));
    Check(value.Hotkeys.SequenceEqual(new[]{"Ctrl+Z","Alt+4","Alt+Shift+F10","Alt+3","Ctrl+R","Alt+2"}),"Legacy action indices were shifted or requested keys were not applied.");
    Check(value.ReplaySeconds==300&&value.CacheMegabytes==1024&&value.SaveFolder=="D:\\Clips","Hotkey migration changed unrelated capture settings.");
    Check(!value.Hotkeys.Contains("Alt+F1"),"Screenshot shortcut was retained.");
}
var defaults=Settings.FromJson("{}");
Check(defaults.Hotkeys.SequenceEqual(new Settings().Hotkeys),"Missing legacy keys migrated with wrong defaults.");
var customized=new Settings {Hotkeys=["Ctrl+M","Ctrl+K","Ctrl+T","Ctrl+L","Ctrl+R","Ctrl+J"]};
Check(Settings.FromJson(JsonSerializer.Serialize(customized)).Hotkeys.SequenceEqual(customized.Hotkeys),"New-schema custom shortcuts were reset on restart.");
Check(Native.Parse("Alt+F2").Key==0x71,"Function keys regressed.");
Console.WriteLine("PASS: six-/seven-action legacy configuration migrates once, screenshot removed, capture preferences and future custom keys retained.");

namespace LightCapture;

static class AppIcon
{
    public static Icon Load()
    {
        using var stream=typeof(AppIcon).Assembly.GetManifestResourceStream("LightCapture.AppIcon")
            ?? throw new InvalidOperationException("程序图标资源缺失。");
        using var source=new Icon(stream);
        return (Icon)source.Clone();
    }
}

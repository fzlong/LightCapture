using System.Diagnostics;
namespace LightCapture;

sealed class MainWindow : Form
{
    readonly Settings settings=Settings.Load();
    readonly ObsCaptureEngine engine;
    readonly GifRecorder gif;
    readonly FpsMonitor fps=new();
    readonly FpsOverlay overlay=new();
    readonly Icon appIcon=AppIcon.Load();
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer timer=new() { Interval=1000 };
    readonly Label status=new() { AutoSize=false,Dock=DockStyle.Fill,Text="就绪",ForeColor=Color.DimGray,Padding=new Padding(8) };
    readonly Button record=new(),replay=new(),saveReplay=new(),shot=new(),fpsButton=new(),menuButton=new(),gifButton=new();
    readonly List<TextBox> keyBoxes=new();
    readonly List<string> conflicts=new();
    readonly bool background;
    bool exiting,busy,ticking,fpsStarting,gifBusy,updatingStartup;
    RegionSelector? selector;
    Task? captureMaintenance;
    int fpsRequest;
    TextBox folder=null!;
    NumericUpDown fpsInput=null!,secondsInput=null!,cacheInput=null!,displayInput=null!,qualityInput=null!,gifFps=null!,gifSize=null!,gifSeconds=null!;
    ComboBox captureMethod=null!;
    CheckBox systemAudio=null!,minimized=null!,autoStart=null!,replayOnStart=null!;
    readonly string[] names=["呼出 / 收起菜单","录制开始 / 停止","即时回放开关","保存最近回放","保存屏幕快照","FPS 显示开关","框选 GIF / 停止保存"];
    public MainWindow(bool background)
    {
        this.background=background;
        engine=new(settings); engine.Message+=Notify;
        gif=new(settings); gif.Message+=Notify;
        Text="LightCapture · 轻录 v"+typeof(MainWindow).Assembly.GetName().Version?.ToString(3); Size=new(560,830); MinimumSize=new(510,760);
        StartPosition=FormStartPosition.CenterScreen; Font=new("Microsoft YaHei UI",10); Icon=appIcon;
        var tabs=new TabControl { Dock=DockStyle.Fill };
        var home=new TabPage("录制菜单") { Padding=new Padding(18) };
        var prefs=new TabPage("设置与快捷键") { Padding=new Padding(16),AutoScroll=true };
        tabs.TabPages.AddRange([home,prefs]); Controls.Add(tabs);
        var grid=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=11 };
        grid.ColumnStyles.Add(new(SizeType.Percent,100));
        grid.RowStyles.Add(new(SizeType.Absolute,68));
        for(int i=1;i<=7;i++) grid.RowStyles.Add(new(SizeType.Absolute,60));
        grid.RowStyles.Add(new(SizeType.Absolute,48)); grid.RowStyles.Add(new(SizeType.Percent,100)); grid.RowStyles.Add(new(SizeType.Absolute,45));
        grid.Controls.Add(new Label { Text="LightCapture  /  轻录\nOBS 内核 · 游戏 / 桌面连续回放",AutoSize=false,Dock=DockStyle.Fill,Font=new(Font.FontFamily,13,FontStyle.Bold) },0,0);
        Button[] buttons=[menuButton,record,replay,saveReplay,shot,fpsButton,gifButton];
        for(int i=0;i<buttons.Length;i++)
        {
            int id=i; var b=buttons[i]; b.Dock=DockStyle.Fill; b.Margin=new Padding(0,4,0,4); b.TextAlign=ContentAlignment.MiddleLeft;
            b.FlatStyle=FlatStyle.System; b.Click+=async(_,_)=>await Act(id); grid.Controls.Add(b,0,i+1);
        }
        var open=new Button { Text="打开保存文件夹",Dock=DockStyle.Fill }; open.Click+=(_,_)=> { Directory.CreateDirectory(settings.SaveFolder); Process.Start(new ProcessStartInfo(settings.SaveFolder) { UseShellExecute=true }); };
        grid.Controls.Add(open,0,8); grid.Controls.Add(status,0,9);
        var hide=new Button { Text="收进托盘 · Alt+Z 再次打开",Dock=DockStyle.Fill }; hide.Click+=(_,_)=>Hide(); grid.Controls.Add(hide,0,10); home.Controls.Add(grid);
        BuildSettings(prefs);
        var context=new ContextMenuStrip();
        context.Items.Add("打开菜单",null,(_,_)=>ShowMenu());
        context.Items.Add("录制开始 / 停止",null,async(_,_)=>await Act(1));
        context.Items.Add("保存即时回放",null,async(_,_)=>await Act(3));
        context.Items.Add("框选 GIF / 停止保存",null,async(_,_)=>await Act(6));
        context.Items.Add(new ToolStripSeparator()); context.Items.Add("退出",null,async(_,_)=>await Exit());
        tray=new NotifyIcon { Icon=appIcon,Text="LightCapture · 轻录",Visible=true,ContextMenuStrip=context };
        tray.DoubleClick+=(_,_)=>ShowMenu();
        FormClosing+=(_,e)=> { if(!exiting && e.CloseReason==CloseReason.UserClosing) { e.Cancel=true; Hide(); } };
        timer.Tick+=(_,_)=>
        {
            if(ticking) return; ticking=true;
            try
            {
                if(captureMaintenance==null || captureMaintenance.IsCompleted)
                {
                    if(captureMaintenance?.Exception is {} failure) Notify("捕获恢复失败："+failure.GetBaseException().Message);
                    captureMaintenance=Task.Run(()=>engine.Maintain());
                }
                if(gif.CaptureFinished && !gifBusy) _=ToggleGif();
                if(overlay.Visible)
                {
                    if(fps.Error is {} problem)
                    {
                        overlay.Hide(); fps.Dispose(); Notify("FPS 采集已停止："+problem);
                    }
                    else
                    {
                        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out var pid);
                        overlay.UpdateFps(fps.Get(pid),null,fps.WaitingMessage);
                    }
                }
                RefreshState();
            }
            catch(Exception ex) { Notify(ex.Message); }
            finally { ticking=false; }
        };
        Shown+=async(_,_)=>
        {
            RegisterKeys(); RefreshState(); timer.Start();
            if(background || settings.StartMinimized) Hide();
            if(settings.ReplayOnLaunch) await Act(2);
        };
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.SetWindowDisplayAffinity(Handle,0x11); }
    void BuildSettings(TabPage page)
    {
        var g=new TableLayoutPanel { Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Padding=new Padding(0,0,12,12) };
        g.ColumnStyles.Add(new(SizeType.Absolute,165)); g.ColumnStyles.Add(new(SizeType.Percent,100));
        void Add(string name,Control c) { int row=g.RowCount++; g.RowStyles.Add(new(SizeType.Absolute,36)); g.Controls.Add(new Label { Text=name,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft },0,row); c.Dock=DockStyle.Fill; g.Controls.Add(c,1,row); }
        NumericUpDown Number(int value,int min,int max) => new() { Minimum=min,Maximum=max,Value=Math.Clamp(value,min,max) };
        folder=new() { Text=settings.SaveFolder }; Add("保存文件夹",folder);
        var browse=new Button { Text="选择文件夹…" }; browse.Click+=(_,_)=> { using var d=new FolderBrowserDialog { SelectedPath=folder.Text }; if(d.ShowDialog()==DialogResult.OK) folder.Text=d.SelectedPath; }; Add("",browse);
        fpsInput=Number(settings.Fps,15,120); Add("录制帧率",fpsInput);
        secondsInput=Number(settings.ReplaySeconds,10,600); Add("回放时长（秒）",secondsInput);
        cacheInput=Number(settings.CacheMegabytes,128,4096); Add("内存缓存上限 MB",cacheInput);
        displayInput=Number(settings.DisplayIndex,0,15); Add("显示器索引（从 0）",displayInput);
        captureMethod=new() { DropDownStyle=ComboBoxStyle.DropDownList };
        captureMethod.Items.AddRange(["OBS 自动（显示器连续捕获）","OBS Windows Graphics Capture","OBS Desktop Duplication"]);
        captureMethod.SelectedIndex=Math.Clamp(settings.CaptureMethod,0,2); Add("捕获方式",captureMethod);
        qualityInput=Number(settings.Quality,15,35); Add("CQ（越低越清晰）",qualityInput);
        systemAudio=new() { Text="录制系统播放声音",Checked=settings.SystemAudio }; Add("系统声音",systemAudio);
        autoStart=new() { Text="登录 Windows 后启动（立即生效）",Checked=Settings.AutoStart }; Add("开机启动",autoStart);
        autoStart.CheckedChanged+=(_,_)=>ApplyAutoStart();
        minimized=new() { Text="打开时收进托盘",Checked=settings.StartMinimized }; Add("最小化启动",minimized);
        replayOnStart=new() { Text="启动后自动开启回放",Checked=settings.ReplayOnLaunch }; Add("自动回放",replayOnStart);
        gifFps=Number(settings.GifFps,8,30); Add("GIF 帧率",gifFps);
        gifSize=Number(settings.GifMaxSide,320,1920); Add("GIF 最长边（像素）",gifSize);
        gifSeconds=Number(settings.GifMaxSeconds,10,300); Add("GIF 最长录制（秒）",gifSeconds);
        for(int i=0;i<names.Length;i++) { var t=new TextBox { Text=settings.Hotkeys.ElementAtOrDefault(i)??new Settings().Hotkeys[i] }; keyBoxes.Add(t); Add(names[i],t); }
        var save=new Button { Text="保存设置并重新注册快捷键" }; save.Click+=(_,_)=>SaveSettings(); Add("",save);
        var help=new Label { AutoSize=true,MaximumSize=new Size(450,0),Text="录制选定显示器的全部内容：游戏、桌面、切换过程都进入同一份回放，切回桌面不会清空缓存。\n快捷键冲突时，请关闭 NVIDIA Overlay 或修改按键。FPS 首次开启需管理员授权；独占全屏可能遮挡 FPS 浮窗。\nOBS 以关键帧保留回放，达到内存上限时保留较新的画面；缓存显示为估算值。" };
        int r=g.RowCount++; g.Controls.Add(help,0,r); g.SetColumnSpan(help,2); page.Controls.Add(g);
    }
    void SaveSettings()
    {
        try
        {
            if(engine.Replay || engine.Recording || engine.Running || gif.Recording || gif.Exporting || gifBusy || selector!=null) throw new InvalidOperationException("请先停止录制、GIF 并关闭即时回放，再更改捕获设置。");
            var keys=keyBoxes.Select(b=>b.Text.Trim()).ToArray(); var parsed=keys.Select(Native.Parse).ToArray();
            if(parsed.Distinct().Count()!=names.Length) throw new ArgumentException("快捷键不能重复。");
            var path=Path.GetFullPath(folder.Text); Directory.CreateDirectory(path);
            settings.SaveFolder=path; settings.Fps=(int)fpsInput.Value; settings.ReplaySeconds=(int)secondsInput.Value; settings.CacheMegabytes=(int)cacheInput.Value;
            settings.DisplayIndex=(int)displayInput.Value; settings.CaptureMethod=captureMethod.SelectedIndex; settings.Quality=(int)qualityInput.Value; settings.SystemAudio=systemAudio.Checked;
            settings.StartMinimized=minimized.Checked; settings.ReplayOnLaunch=replayOnStart.Checked; settings.Hotkeys=keys;
            settings.GifFps=(int)gifFps.Value; settings.GifMaxSide=(int)gifSize.Value; settings.GifMaxSeconds=(int)gifSeconds.Value;
            settings.Save(); RegisterKeys(); RefreshState(); Notify(conflicts.Count==0?"设置已保存":"设置已保存，但存在快捷键冲突："+string.Join("、",conflicts));
        }
        catch(Exception ex) { Notify(ex.Message); }
    }
    void ApplyAutoStart()
    {
        if(updatingStartup) return;
        bool requested=autoStart.Checked;
        try
        {
            Settings.AutoStart=requested;
            Notify(requested?"已开启开机启动，启动路径已更新为当前版本。":"已关闭开机启动，Windows 启动项已移除。");
        }
        catch(Exception ex)
        {
            updatingStartup=true;
            try {autoStart.Checked=!requested;} finally {updatingStartup=false;}
            Notify("开机启动修改失败："+ex.Message);
        }
    }
    void RegisterKeys()
    {
        conflicts.Clear();
        for(int i=0;i<names.Length;i++)
        {
            Native.UnregisterHotKey(Handle,i+1);
            try { var key=Native.Parse(settings.Hotkeys[i]); if(!Native.RegisterHotKey(Handle,i+1,key.Mods,key.Key)) conflicts.Add(settings.Hotkeys[i]); }
            catch(Exception ex) { conflicts.Add(ex.Message); }
        }
        if(conflicts.Count>0) Notify("快捷键已被占用："+string.Join("、",conflicts)+"。请关闭 NVIDIA Overlay 或在设置中修改。");
    }
    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
        if(m.Msg==0x312) { int id=m.WParam.ToInt32()-1; BeginInvoke(async()=>await Act(id)); }
        if((uint)m.Msg==Native.ShowMessage) ShowMenu();
        base.WndProc(ref m);
    }
    void ShowMenu() { Show(); WindowState=FormWindowState.Normal; Activate(); }
    public async Task Act(int id)
    {
        if(id==0) { if(Visible) Hide(); else ShowMenu(); return; }
        if(id==5) { await ToggleFps(); return; }
        if(id==6) { await ToggleGif(); return; }
        if(busy) {Notify("正在处理录制或保存请求，请稍候。");return;} busy=true;
        try
        {
            status.Text="正在处理…";
            switch(id)
            {
                case 1: await engine.ToggleRecording(); break;
                case 2: await engine.ToggleReplay(); Notify(engine.Replay?engine.Recovering?"即时回放已请求开启，正在恢复显示器捕获":"即时回放已开启，游戏和桌面连续缓存在内存":"即时回放已关闭"); break;
                case 3: await engine.SaveReplay(); break;
                case 4: await engine.Screenshot(); break;
            }
            RefreshState();
        }
        catch(Exception ex) { Notify(ex.Message); }
        finally { busy=false; }
    }
    async Task ToggleGif()
    {
        if(selector!=null) { selector.Cancel(); return; }
        if(gifBusy) { Notify("GIF 正在启动或压缩，请稍候。"); return; }
        gifBusy=true;
        try
        {
            if(gif.Recording)
            {
                var export=gif.StopAndExport(); RefreshState(); await export;
            }
            else
            {
                bool wasVisible=Visible; Hide();
                Rectangle? region;
                using(var picker=new RegionSelector())
                {
                    selector=picker;
                    try { region=await picker.SelectAsync(); } finally { selector=null; }
                }
                if(region==null) { if(wasVisible && !busy && !IsDisposed) ShowMenu(); return; }
                await Task.Delay(180); // Allow the selection surface to leave the compositor.
                await gif.Start(region.Value);
                Notify($"GIF 录制中：{region.Value.Width} × {region.Value.Height}，再次按 {settings.Hotkeys[6]} 停止；最长 {settings.GifMaxSeconds} 秒。");
            }
        }
        catch(Exception ex) { Notify(ex.Message); }
        finally { gifBusy=false; if(!IsDisposed) RefreshState(); }
    }
    async Task ToggleFps()
    {
        int request=++fpsRequest;
        if(fpsStarting || overlay.Visible)
        {
            fpsStarting=false; overlay.Hide(); fps.Dispose(); RefreshState(); Notify("FPS 已关闭"); return;
        }
        fpsStarting=true; RefreshState();
        Notify("正在启动 FPS 采集；可再次按快捷键取消，其他功能仍可使用。");
        try
        {
            await fps.Start();
            if(request!=fpsRequest || exiting) return;
            overlay.Show(); Hide(); Notify("FPS 已开启。数据来自前台程序的 Present 事件。");
        }
        catch(OperationCanceledException)
        {
            if(request==fpsRequest) Notify("FPS 启动已取消或超时，请检查管理员授权。");
        }
        catch(Exception ex) { if(request==fpsRequest) Notify("FPS 启动失败："+ex.Message); }
        finally { if(request==fpsRequest) { fpsStarting=false; RefreshState(); } }
    }
    void RefreshState()
    {
        string gifText=gif.Exporting?"GIF · 正在压缩保存":gif.Recording?$"● GIF 录制中 {gif.Elapsed:mm\\:ss} · 点击停止":"GIF · 框选区域录制";
        string[] info=["呼出 / 收起菜单",engine.Recording?engine.Recovering?"录制 · 捕获恢复中":"● 正在录制 · 点击停止":"录制 · 开始",engine.Replay?engine.Recovering?"即时回放 · 捕获恢复中":$"即时回放 · 已开启  {engine.CacheSeconds:0}s / {engine.CacheBytes/1048576.0:0} MB":"即时回放 · 已关闭","保存最近 "+settings.ReplaySeconds+" 秒回放","屏幕快照 · 保存 PNG",fpsStarting?"FPS 显示 · 启动中（再次点击取消）":overlay.Visible?"FPS 显示 · 已开启":"FPS 显示 · 已关闭",gifText];
        Button[] buttons=[menuButton,record,replay,saveReplay,shot,fpsButton,gifButton];
        for(int i=0;i<names.Length;i++) buttons[i].Text=$"  {info[i]}\n  {settings.Hotkeys.ElementAtOrDefault(i)}";
        tray.Text=engine.Recovering?"LightCapture · 正在恢复捕获":gif.Recording?"LightCapture · GIF 录制中，Alt+F2 停止":gif.Exporting?"LightCapture · GIF 压缩中":engine.Recording?"LightCapture · 正在录制":engine.Replay?"LightCapture · 内存回放中":"LightCapture · 就绪";
    }
    void Notify(string message)
    {
        if(IsDisposed) return;
        if(InvokeRequired) { BeginInvoke(()=>Notify(message)); return; }
        status.Text=message;
        tray.BalloonTipTitle="LightCapture · 轻录"; tray.BalloonTipText=message.Length>220?message[..220]:message; tray.ShowBalloonTip(2500);
        try { Directory.CreateDirectory(Settings.DataFolder); File.AppendAllText(Path.Combine(Settings.DataFolder,"app.log"),$"{DateTime.Now:O} {message}\n"); } catch { }
    }
    async Task Exit()
    {
        if(busy || gifBusy) { Notify("正在处理或保存文件，请稍后退出。"); return; }
        busy=true;
        fpsRequest++; fpsStarting=false; fps.Dispose();
        selector?.Cancel();
        try { timer.Stop(); await Task.WhenAll(engine.Shutdown(),gif.StopAndExport()); }
        catch(Exception ex) { MessageBox.Show("退出前保存失败，原始文件已保留，请查看日志。\n"+ex.Message,"LightCapture"); }
        finally
        {
            fps.Dispose(); overlay.Dispose(); tray.Visible=false; tray.Dispose(); appIcon.Dispose();
            for(int i=1;i<=names.Length;i++) Native.UnregisterHotKey(Handle,i);
            exiting=true; Close();
        }
    }
}

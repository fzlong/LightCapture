<p align="center"><img src="src/Assets/lightcapture.svg" width="96" alt="LightCapture icon"></p>

# LightCapture · 轻录

面向 Windows / NVIDIA 的轻量托盘录屏工具：录屏、内存即时回放、截图、框选 GIF 和 FPS 浮窗。当前版本 **v0.3.1**，仍处于早期开发阶段。

录制和即时回放基于 **OBS/libobs**，持续捕获选定的整块显示器。游戏和切回桌面的过程进入同一份回放；保存片段不会清空缓存。不需要启动 OBS 的完整界面，不需要账号或云端服务。

## 功能

- H.264 NVENC 硬件编码，WASAPI 系统声音。
- OBS 原生内存循环回放，按时间和压缩数据容量淘汰旧画面，保存时才写 MP4。
- 普通录制与回放共享编码器；普通录像先写 MKV，正常停止后无重编码转 MP4。
- 独立捕获后台进程、输出健康检查、捕获方式切换及失败重试。后台重启后回放从新画面重新累计，无法补回丢失的内容。
- 区域 GIF：调色板优化、差分和透明帧优化，可设置帧率、最长边及最长录制时长。
- PresentMon 提交帧率浮窗；托盘、快捷键、最小化启动、可选开机启动。
- 开机启动开关立即生效，无需停止录制；新版启动时更新已有的旧版启动路径，已关闭的启动项保持关闭。

## 默认快捷键

| 功能 | 快捷键 |
| --- | --- |
| 呼出 / 隐藏菜单 | Alt+Z |
| 开始 / 停止普通录制 | Alt+F9 |
| 开启 / 关闭即时回放 | Alt+Shift+F10 |
| 保存最近回放 | Alt+F10 |
| PNG 屏幕快照 | Alt+F1 |
| FPS 浮窗 | Alt+R |
| 框选 GIF / 停止并保存 | Alt+F2 |

设置中可修改快捷键。与 NVIDIA Overlay 等软件冲突时，请调整其中一方的按键。关闭窗口会收进托盘；真正退出请使用托盘菜单。

## 从源码构建

当前以 **Windows 11 x64 + NVIDIA NVENC** 为测试环境。需要 [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)、PowerShell 和支持 NVENC 的 NVIDIA 驱动。运行构建后的框架依赖程序需要 .NET 9 Windows Desktop Runtime。

```powershell
git clone https://github.com/fzlong/LightCapture.git
cd LightCapture
.\Setup-Dependencies.ps1
.\Build-OBS.ps1
.\app\LightCapture.exe
```

依赖脚本从官方 GitHub Releases 下载并校验 **OBS Studio 32.2.2 x64**、**PresentMon 2.6.0 x64**。OBS ABI 绑定到 32.2.2，请勿直接替换为其他版本。

另行安装 [Windows FFmpeg](https://ffmpeg.org/download.html)，将 `ffmpeg.exe` 所在目录加入 PATH，或将它放入 `app\tools`。截图、GIF 和普通录像转 MP4 会用到 FFmpeg；录制和内存回放使用随构建打包的 OBS 模块。具体所需功能见 [tools/README.md](tools/README.md)。

已有官方 OBS ZIP 解压目录时可直接指定，仍需准备 `tools\PresentMon.exe`：

```powershell
.\Build-OBS.ps1 -ObsRoot C:\Dependencies\OBS-32.2.2 -OutputFolder .\app
```

请完整保留构建后的文件夹。`obs-runtime\bin\64bit\LightCapture.exe` 是内部后台副本，使用外层 `app\LightCapture.exe` 启动。源码仓库不包含原生依赖二进制、录像或个人配置。

## 使用与限制

默认新配置保留最近 60 秒、压缩缓存 512 MB；达到容量上限时可保存的时长会缩短。OBS 以关键帧为单位保留数据，显示值是估算值，整个程序的内存占用会高于缓存载荷。

配置与日志保存在 `%LOCALAPPDATA%\LightCapture`；视频默认保存在当前用户“视频”目录下的 `LightCapture`。开机启动只修改当前用户的 `LightCapture` Run 项，不要求整套程序以管理员身份运行；FPS 首次开启需要独立采集进程的管理员授权。

尚未验证所有游戏、独占全屏模式切换、HDR、多显示器或长时间运行。部分游戏、反作弊及受保护内容可能限制捕获；持续黑屏或冻结画面也不一定能被健康检查识别。FPS 显示的是 Present 提交频率，浮窗在独占全屏下可能被遮挡。FPS 的跨游戏兼容性仍需完善。

## 验证

以下命令运行 FPS 管道与解析回归，不需要实际游戏：

```powershell
dotnet build src/LightCapture.csproj -c Release
dotnet run --project tests/FpsRegression.csproj -c Release
```

其他测试：

- `startup-tests/StartupRegression.csproj`：实际隐藏设置窗体和当前用户启动项；会短暂修改轻录启动项并在 finally 恢复。请在本机知情运行，不要与正在修改启动设置的轻录同时运行。
- `gif-tests/GifRegression.csproj`：区域几何、参数迁移与真实 FFmpeg GIF 压缩导出。
- `capture-tests/CaptureRegression.csproj`：旧 FFmpeg 捕获后端的恢复回归；当前主录制后端是 libobs。
- 构建后运行 `app\LightCapture.exe --obs-self-test C:\Temp\LightCaptureTest` 验证真实显示器、NVENC、回放、缓存淘汰和后台恢复；`--obs-self-test-wgc` 验证 WGC。会录制当前屏幕并写入指定目录，不会自动操作前台游戏。

原生捕获和 GIF 测试需要相应的桌面、GPU、FFmpeg 环境；CI 仅验证构建与 FPS 软件回归。Windows 注册表读回通过不等同于已经做过重启登录测试。

## 源码结构

| 位置 | 用途 |
| --- | --- |
| `src/MainWindow.cs` | 托盘菜单、设置和快捷键 |
| `src/ObsCaptureEngine.cs` | 捕获后台控制、状态监测与恢复 |
| `src/ObsWorker.cs`、`src/ObsNative.cs` | libobs 原生接口、捕获和输出 |
| `src/GifRecorder.cs`、`src/RegionSelector.cs` | 区域 GIF |
| `src/FpsMonitor.cs` | PresentMon 采集、解析和 FPS 浮窗 |
| `Build-OBS.ps1` | 构建并打包所需 OBS 模块 |

## 开源许可

LightCapture 源码采用 **GPL-2.0-or-later**，见 [LICENSE](LICENSE)。

OBS Studio、FFmpeg、PresentMon、NAudio 等组件各自保留原许可和版权声明，见 [third-party-notices](third-party-notices/README.md)。本项目不隶属于 OBS Project、NVIDIA 或 Intel。

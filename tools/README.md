# External tools

Run `Setup-Dependencies.ps1` at the repository root to download the official PresentMon 2.6.0 x64 and gifski 1.34.0 Windows executables here. Downloaded archives are SHA256-verified before extraction. Binaries are excluded from Git.

Install a compatible Windows FFmpeg build separately and add its `bin` directory to PATH, or put `ffmpeg.exe` in the built application's `tools` directory. Region GIF capture uses `gdigrab`/FFV1; fast GIF export uses RGB-to-BT.709 YUV444 conversion and a YUV4MPEG stream into gifski. The fallback uses `palettegen` and `paletteuse`. The legacy capture regression test requires NVIDIA `h264_nvenc`.

- PresentMon: https://github.com/GameTechDev/PresentMon/releases/tag/v2.6.0
- FFmpeg: https://ffmpeg.org/download.html
- gifski: https://github.com/ImageOptim/gifski/releases/tag/1.34.0 (AGPL-3.0-or-later; built as a separate executable, not linked into LightCapture)

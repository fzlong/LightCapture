# External tools

Run `Setup-Dependencies.ps1` at the repository root to download the official PresentMon 2.6.0 x64 executable here. Its SHA256 is verified before use. Binaries are excluded from Git.

Install a compatible Windows FFmpeg build separately and add its `bin` directory to PATH, or put `ffmpeg.exe` in the built application's `tools` directory. Region GIF capture uses `gdigrab`; GIF export uses `palettegen`, `paletteuse` and `ssim`. Screenshots use `gfxcapture`/`ddagrab`. The legacy capture regression test also requires NVIDIA `h264_nvenc`.

- PresentMon: https://github.com/GameTechDev/PresentMon/releases/tag/v2.6.0
- FFmpeg: https://ffmpeg.org/download.html

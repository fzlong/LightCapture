# Third-party components

LightCapture 0.3.1 builds against the official, unmodified OBS Studio 32.2.2 Windows x64 libraries and helper executables. The source repository does not include these binaries. The build script selects the required modules: libobs, Direct3D 11, Windows Graphics Capture support, Windows display capture, WASAPI, FFmpeg output/AAC and NVENC.

- OBS Studio: https://github.com/obsproject/obs-studio/tree/32.2.2
- Official binaries: https://github.com/obsproject/obs-studio/releases/tag/32.2.2
- Corresponding OBS source archive: https://github.com/obsproject/obs-studio/releases/download/32.2.2/OBS-Studio-32.2.2-Sources.tar.gz
- OBS license: GNU GPL v2 or later; full text in OBS-COPYING.txt. See the upstream source tree for individual file notices and dependency build manifests.
- OBS dependency sources/build information: https://github.com/obsproject/obs-deps
- FFmpeg libraries: https://ffmpeg.org/ ; https://ffmpeg.org/legal.html
- x264: https://www.videolan.org/developers/x264.html
- curl: https://curl.se/docs/copyright.html
- librist: https://code.videolan.org/rist/librist
- SRT: https://github.com/Haivision/srt
- zlib: https://zlib.net/zlib_license.html
- winpthreads: https://www.mingw-w64.org/
- PresentMon 2.6.0: https://github.com/GameTechDev/PresentMon/tree/v2.6.0 (MIT).
- NAudio 2.2.1: https://github.com/naudio/NAudio (MIT).

An independently installed FFmpeg executable is used for screenshots, GIF export and recording remux. It is not copied into the package. Its licensing depends on the installed build.

LightCapture source is provided under GPL-2.0-or-later in src/; build instructions are in the repository README and Build-OBS.ps1. The current target is Windows x64 / NVIDIA / .NET 9. No OBS frontend, browser, account or cloud service is started by the capture engine. Individual dependency licenses remain in effect.

param(
    [string]$ObsRoot=(Join-Path $PSScriptRoot '.dependencies\obs-studio-32.2.2'),
    [string]$OutputFolder=(Join-Path $PSScriptRoot 'app'),
    [switch]$NoRestore
)
$ErrorActionPreference='Stop'
$ObsRoot=(Resolve-Path -LiteralPath $ObsRoot).Path
if(!(Test-Path -LiteralPath (Join-Path $ObsRoot 'bin\64bit\obs.dll'))) {throw 'Provide an extracted official OBS Studio 32.2.2 Windows x64 ZIP with -ObsRoot.'}
if((Get-Item -LiteralPath (Join-Path $ObsRoot 'bin\64bit\obs.dll')).VersionInfo.ProductVersion -ne '32.2.2') {throw 'The native interop is pinned to OBS 32.2.2. Use that exact official distribution.'}
$source=Join-Path $PSScriptRoot 'src\LightCapture.csproj'
$buildArgs=@('publish',$source,'-c','Release','-o',$OutputFolder)
if($NoRestore) {$buildArgs+='--no-restore'}
& dotnet @buildArgs
if($LASTEXITCODE -ne 0) {throw 'dotnet publish failed'}
$OutputFolder=(Resolve-Path -LiteralPath $OutputFolder).Path
$runtime=Join-Path $OutputFolder 'obs-runtime'
$bin=Join-Path $runtime 'bin\64bit'
$plugins=Join-Path $runtime 'obs-plugins\64bit'
$data=Join-Path $runtime 'data\obs-plugins'
New-Item -ItemType Directory -Force $bin,$plugins,$data,(Join-Path $OutputFolder 'tools') | Out-Null
# Explicit dependency closure of OBS 32.2.2; no Qt frontend, browser, scenes UI or streaming plugins.
$libraries=@('obs.dll','libobs-d3d11.dll','libobs-winrt.dll','avcodec-62.dll','avdevice-62.dll','avfilter-11.dll','avformat-62.dll','avutil-60.dll','swscale-9.dll','swresample-6.dll','libx264-164.dll','libcurl.dll','librist.dll','srt.dll','w32-pthreads.dll','zlib.dll','obs-nvenc-test.exe','obs-ffmpeg-mux.exe')
foreach($name in $libraries) {Copy-Item -LiteralPath (Join-Path $ObsRoot "bin\64bit\$name") -Destination $bin -Force}
Copy-Item -LiteralPath (Join-Path $ObsRoot 'data\libobs') -Destination (Join-Path $runtime 'data') -Recurse -Force
foreach($name in @('win-capture','obs-ffmpeg','obs-nvenc','win-wasapi')) {
    Copy-Item -LiteralPath (Join-Path $ObsRoot "obs-plugins\64bit\$name.dll") -Destination $plugins -Force
    Copy-Item -LiteralPath (Join-Path $ObsRoot "data\obs-plugins\$name") -Destination $data -Recurse -Force
}
$resolvedRuntime=(Resolve-Path -LiteralPath $runtime).Path
Get-ChildItem -LiteralPath $resolvedRuntime -Recurse -File -Filter '*.pdb' | ForEach-Object {
    if(!$_.FullName.StartsWith($resolvedRuntime+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {throw 'Unexpected symbol file path'}
    Remove-Item -LiteralPath $_.FullName
}
# OBS finds its helper executables beside the host executable. Keep the capture worker here.
Get-ChildItem -LiteralPath $OutputFolder -File | Where-Object {$_.Extension -in @('.dll','.json') -or $_.Name -eq 'LightCapture.exe'} | Copy-Item -Destination $bin -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'tools\PresentMon.exe') -Destination (Join-Path $OutputFolder 'tools') -Force
if(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'third-party-notices')) {Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'third-party-notices') -Destination $OutputFolder -Recurse -Force}
foreach($name in @('README.md','LICENSE')) {
    if(Test-Path -LiteralPath (Join-Path $PSScriptRoot $name)) {Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $OutputFolder -Force}
}
Write-Output "Built: $OutputFolder\LightCapture.exe"

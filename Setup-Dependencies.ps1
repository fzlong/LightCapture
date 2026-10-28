param([string]$CacheRoot=(Join-Path $PSScriptRoot '.dependencies'))
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force $CacheRoot,(Join-Path $PSScriptRoot 'tools') | Out-Null
$CacheRoot=(Resolve-Path -LiteralPath $CacheRoot).Path

function Get-VerifiedFile([string]$Url,[string]$Destination,[string]$Sha256) {
    if(!(Test-Path -LiteralPath $Destination) -or (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -ne $Sha256) {
        Invoke-WebRequest -Uri $Url -OutFile $Destination
    }
    if((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -ne $Sha256) {throw "SHA256 mismatch: $Destination"}
}

$zip=Join-Path $CacheRoot 'OBS-Studio-32.2.2-Windows-x64.zip'
Get-VerifiedFile 'https://github.com/obsproject/obs-studio/releases/download/32.2.2/OBS-Studio-32.2.2-Windows-x64.zip' $zip '4D6E40E3AB155F56B30DE517380566A206D74B63CDF5AD49AA596924768F97E1'
$obs=Join-Path $CacheRoot 'obs-studio-32.2.2'
Expand-Archive -LiteralPath $zip -DestinationPath $obs -Force
Get-VerifiedFile 'https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe' (Join-Path $PSScriptRoot 'tools\PresentMon.exe') 'B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF'
Write-Output "Dependencies ready. Build with: .\Build-OBS.ps1 -ObsRoot `"$obs`""
Write-Output 'FFmpeg is not downloaded: install a compatible Windows FFmpeg build and add its bin folder to PATH, or place ffmpeg.exe in app\tools.'

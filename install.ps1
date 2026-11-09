<#
  VLC Commentary - installer (Windows)

  1. Compiles bin\VlcCommentary.exe with the C# compiler built into Windows.
  2. Turns on VLC's web interface, localhost only, with a random password.
     (Your vlcrc is backed up first.)
  3. Saves the app settings to %APPDATA%\VlcCommentary\config.ini
  4. Adds a Start menu shortcut and launches the app.

  Usage:   powershell -ExecutionPolicy Bypass -File install.ps1 [-Startup] [-NoLaunch]
    -Startup   also start VLC Commentary automatically when you sign in
#>
param([switch]$Startup, [switch]$NoLaunch, [int]$Port = 8765)
$ErrorActionPreference = 'Stop'
# 8765 rather than VLC's default 8080: Steam and many dev servers already use 8080.
$root = $PSScriptRoot

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# ---------------------------------------------------------------- 1. build
Step "Building VlcCommentary.exe"
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw ".NET Framework 4 C# compiler not found at $csc" }

Get-Process VlcCommentary -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force "$root\bin" | Out-Null
& $csc /nologo /target:winexe /optimize+ /out:"$root\bin\VlcCommentary.exe" `
    /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "$root\src\*.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# ---------------------------------------------------------------- 2. app settings
$cfgDir  = Join-Path $env:APPDATA 'VlcCommentary'
$cfgFile = Join-Path $cfgDir 'config.ini'
New-Item -ItemType Directory -Force $cfgDir | Out-Null

$password = $null
if (Test-Path $cfgFile) {
    $m = Select-String -Path $cfgFile -Pattern '^password=(.+)$' | Select-Object -First 1
    if ($m) { $password = $m.Matches[0].Groups[1].Value.Trim() }
}
if (-not $password) {
    $chars = 'abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'.ToCharArray()
    $password = -join (1..20 | ForEach-Object { $chars | Get-Random })
}
if (-not (Test-Path $cfgFile)) {
    Step "Writing $cfgFile"
    @(
        '# VLC Commentary settings. Restart the app after editing.'
        "password=$password"
        "port=$Port"
        'hotkey=Ctrl+Alt+N'
        'autopause=false'
        "notes_dir=$([Environment]::GetFolderPath('MyDocuments'))\VLC Commentary"
    ) | Set-Content -Path $cfgFile -Encoding ASCII
} else {
    # Keep the user's other settings; just make sure the port matches VLC.
    (Get-Content $cfgFile) -replace '^port=.*$', "port=$Port" | Set-Content -Path $cfgFile -Encoding ASCII
}

# ---------------------------------------------------------------- 3. VLC web interface
$vlcrc = Join-Path $env:APPDATA 'vlc\vlcrc'
if (-not (Test-Path $vlcrc)) {
    Write-Warning "VLC settings file not found. Open VLC once, close it, and run this script again."
} else {
    if (Get-Process vlc -ErrorAction SilentlyContinue) {
        Write-Warning "VLC is open. Close it and run this script again so the settings stick."
        exit 1
    }
    Step "Enabling VLC web interface (localhost only)"
    $backup = "$vlcrc.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
    Copy-Item $vlcrc $backup
    Write-Host "    backup: $backup"

    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $lines = [System.Collections.Generic.List[string]]([IO.File]::ReadAllLines($vlcrc, $utf8))

    function Set-Opt([string]$key, [string]$value) {
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match "^#?$([regex]::Escape($key))=(.*)$") {
                if ($key -eq 'extraintf' -and -not $lines[$i].StartsWith('#')) {
                    $cur = $Matches[1].Trim()
                    if ($cur -and ($cur -split ':') -notcontains 'http') { $value = "$cur`:http" }
                    elseif ($cur) { $value = $cur }
                }
                $lines[$i] = "$key=$value"
                return
            }
        }
        Write-Warning "Option '$key' not found in vlcrc; set it in VLC preferences manually."
    }
    Set-Opt 'extraintf'     'http'
    Set-Opt 'http-host'     '127.0.0.1'
    Set-Opt 'http-port'     "$Port"
    Set-Opt 'http-password' $password
    [IO.File]::WriteAllLines($vlcrc, $lines, $utf8)
}

# ---------------------------------------------------------------- 4. shortcuts + launch
$exe = Join-Path $root 'bin\VlcCommentary.exe'
$shell = New-Object -ComObject WScript.Shell
function New-Shortcut($path) {
    $s = $shell.CreateShortcut($path); $s.TargetPath = $exe; $s.WorkingDirectory = $root; $s.Save()
}
New-Shortcut (Join-Path ([Environment]::GetFolderPath('Programs')) 'VLC Commentary.lnk')
Step "Start menu shortcut added"
if ($Startup) {
    New-Shortcut (Join-Path ([Environment]::GetFolderPath('Startup')) 'VLC Commentary.lnk')
    Step "Will start automatically when you sign in"
}

if (-not $NoLaunch) { Start-Process $exe; Step "VLC Commentary is running (tray icon)" }

Write-Host ""
Write-Host "Done. Open a movie in VLC and press Ctrl+Alt+N to add a note." -ForegroundColor Green
Write-Host "Notes are saved in: $([Environment]::GetFolderPath('MyDocuments'))\VLC Commentary"

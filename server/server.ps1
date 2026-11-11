# Big Ambitions server console (BAMP Server Browser mod).
# - prepares a separate server folder (multiplayer identity distinct from your own game)
# - starts the game with no window, no sound, without going through the Steam launcher
# - shows the live log and accepts commands: status, players, say <msg>, save, stop
#
# Steam must be running and signed in with an account that owns the game (checked by the mod).
param(
    [string]$GameDir   = "C:\Program Files (x86)\Steam\steamapps\common\Big Ambitions",
    [string]$ServerDir = "$env:USERPROFILE\BigAmbitionsServer",
    [string]$Name      = "Serveur",
    [int]$Port         = 7777
)
$ErrorActionPreference = "Stop"
$Data    = Join-Path $env:USERPROFILE "AppData\LocalLow\Hovgaard Games\Big Ambitions"
$SB      = Join-Path $Data "BAMPServerBrowser"
$LogFile = Join-Path $SB "server.log"
$CmdFile = Join-Path $SB "command.txt"
$StopFile = Join-Path $SB "stop"

function Info($m) { Write-Host "[server] $m" -ForegroundColor Cyan }

# 1) Server folder: copy of the executable + junctions to the game data (the GBs of data are not copied).
if (-not (Test-Path (Join-Path $ServerDir "Big Ambitions.exe"))) {
    Info "Preparing $ServerDir"
    New-Item -ItemType Directory -Force $ServerDir | Out-Null
    foreach ($f in "Big Ambitions.exe", "UnityPlayer.dll", "UnityCrashHandler64.exe") { Copy-Item (Join-Path $GameDir $f) (Join-Path $ServerDir $f) -Force }
    foreach ($d in "Big Ambitions_Data", "MonoBleedingEdge") {
        if (-not (Test-Path (Join-Path $ServerDir $d))) { cmd /c mklink /J "$(Join-Path $ServerDir $d)" "$(Join-Path $GameDir $d)" | Out-Null }
    }
}
# After a game update, copy the executable again if it changed.
foreach ($f in "Big Ambitions.exe", "UnityPlayer.dll") {
    $a = Join-Path $GameDir $f; $b = Join-Path $ServerDir $f
    if ((Get-Item $a).LastWriteTimeUtc -ne (Get-Item $b).LastWriteTimeUtc -or (Get-Item $a).Length -ne (Get-Item $b).Length) { Copy-Item $a $b -Force; Info "$f updated" }
}

# 2) Server multiplayer identity (multiplayer mod config specific to this folder).
$key = (Split-Path $ServerDir -Leaf) -replace ' ', '_'
$cfg = Join-Path $Data "BigAmbitionsMP\BigAmbitionsMP.cfg.$key.json"
if (-not (Test-Path $cfg)) {
    New-Item -ItemType Directory -Force (Split-Path $cfg) | Out-Null
    $id = "server-" + [guid]::NewGuid().ToString("N")
    @{ HostPort = "$Port"; StableId = $id; PlayerId = $Name; HostIP = "127.0.0.1"; Port = "$Port" } | ConvertTo-Json | Set-Content -Encoding UTF8 $cfg
    Info "Server identity created: $Name ($id)"
}

# 3) Start.
if (Get-Process -Name "Big Ambitions" -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$ServerDir*" }) { Info "The server is already running."; exit 1 }
New-Item -ItemType Directory -Force $SB | Out-Null
Remove-Item $StopFile, $CmdFile -ErrorAction SilentlyContinue
if (Test-Path $LogFile) { Move-Item $LogFile "$LogFile.prev" -Force }
# The game rewrites its display settings (registry) on exit: back them up and restore them afterwards.
$RegBackup = Join-Path $SB "display-settings.reg"
reg export "HKCU\Software\Hovgaard Games\Big Ambitions" "$RegBackup" /y | Out-Null
$proc = Start-Process -FilePath (Join-Path $ServerDir "Big Ambitions.exe") -WorkingDirectory $ServerDir -PassThru `
    -ArgumentList @("-batchmode", "-nographics", "-disable-force-steam", "-bamp-dedicated", "-logFile", "`"$LogFile`"")
Info "Started (PID $($proc.Id)). Commands: status | players | say <message> | save | stop"

# 4) Live log + command input.
$filter = '\[Dedicated\]|\[ServerBrowser\] (registered|register failed)|\[Server\].*(ACCEPTED|join|left|disconnect|kick)|\[Chat\]|Exception'
$pos = 0; $line = ""; $stopping = $false
[Console]::TreatControlCAsInput = $true
try {
    while (-not $proc.HasExited) {
        if (Test-Path $LogFile) {
            $fs = [IO.File]::Open($LogFile, 'Open', 'Read', 'ReadWrite,Delete')
            try {
                if ($fs.Length -lt $pos) { $pos = 0 }
                $fs.Seek($pos, 'Begin') | Out-Null
                $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)
                while (($l = $sr.ReadLine()) -ne $null) {
                    if ($l -match $filter -and $l -notmatch 'Patched Patch_') {
                        Write-Host ($l -replace '^\[Mod:[^\]]+\] ', '' -replace '\[ServerBrowser\] ', '')
                    }
                }
                $pos = $fs.Position
            } finally { $fs.Dispose() }
        }
        while ([Console]::KeyAvailable) {
            $k = [Console]::ReadKey($true)
            if ($k.Key -eq 'C' -and $k.Modifiers -band [ConsoleModifiers]::Control) { $line = "stop"; $k = $null }
            if ($k -eq $null -or $k.Key -eq 'Enter') {
                Write-Host ""
                $cmd = $line.Trim(); $line = ""
                if ($cmd) {
                    Add-Content -Encoding UTF8 $CmdFile $cmd
                    if ($cmd -eq "stop") { $stopping = $true; Info "Stopping: saving, then closing…" }
                }
            } elseif ($k.Key -eq 'Backspace') {
                if ($line.Length) { $line = $line.Substring(0, $line.Length - 1); Write-Host "`b `b" -NoNewline }
            } elseif ($k.KeyChar -and -not [char]::IsControl($k.KeyChar)) {
                $line += $k.KeyChar; Write-Host $k.KeyChar -NoNewline
            }
        }
        Start-Sleep -Milliseconds 300
    }
} finally {
    [Console]::TreatControlCAsInput = $false
}
if (Test-Path $RegBackup) { reg import "$RegBackup" 2>$null | Out-Null; Info "Game display settings restored." }
Info "Server stopped (code $($proc.ExitCode))."

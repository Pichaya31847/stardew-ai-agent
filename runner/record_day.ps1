# One-click recording batch (docs/howto-record-day.md steps 1-6): launch, record, run, stop recording at the end.
#   runner\record_day.bat [days] [out]      e.g.  runner\record_day.bat 1 runs-ep01
# Opens OBS on the "stardew" scene if it isn't running (WebSocket 4455). Does not restore saves or clear old logs.
param([int]$Days = 1, [string]$Out = "runs-ep01")
$ErrorActionPreference = "Stop"
# read py's UTF-8 output as UTF-8 (5.1 decodes native output with the OEM code page: Thai turns to mojibake)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$game = "C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
$api = "http://127.0.0.1:8770"   # not "localhost": Invoke-RestMethod can stall on ::1
# OBS finds the runner, recorder and SMAPI consoles by title and shows them over P4's quarter
$Host.UI.RawUI.WindowTitle = "Stardew Runner"
# the AI CLIs share this console and may retitle it (Node's process.title): put the title back every 2 s
Add-Type 'public static class KeepTitle { static System.Threading.Timer t; public static void Start(string s) {
    t = new System.Threading.Timer(_ => { try { if (System.Console.Title != s) System.Console.Title = s; } catch {} }, null, 0, 2000); } }'
[KeepTitle]::Start("Stardew Runner")

function Wait-Api($pred, $what, $limit = 600) {
    $t = 0
    while ($t -lt $limit) {
        try { $s = Invoke-RestMethod "$api/api/state" -TimeoutSec 2; if (& $pred $s) { return $s } } catch {}
        Start-Sleep 2; $t += 2
    }
    throw "Timed out waiting for $what"
}

if (-not (Get-Process obs64 -ErrorAction SilentlyContinue)) {
    # OBS wants its own folder as the working directory; the recorder checks the scene later
    $obs = "C:\Program Files\obs-studio\bin\64bit"
    Start-Process "$obs\obs64.exe" -WorkingDirectory $obs -ArgumentList "--scene","stardew","--disable-shutdown-check"
}
if (Get-Process StardewModdingAPI -ErrorAction SilentlyContinue) { throw "The game is already running: close it first so the run starts from the saved morning" }

# the game must open on the primary monitor (displayIndex 0), not the second one
$prefs = "$env:APPDATA\StardewValley\startup_preferences"
(Get-Content $prefs -Raw) -replace '<displayIndex>\d+</displayIndex>', '<displayIndex>0</displayIndex>' | Set-Content $prefs -Encoding utf8

$env:SDL_VIDEO_MINIMIZE_ON_FOCUS_LOSS = "0"
$env:PYTHONIOENCODING = "utf-8"
Start-Process "$game\StardewModdingAPI.exe" -WorkingDirectory $game

# hold the world the moment it answers, or the clock runs while no AI is connected;
# stop_now (not pause) is the hold run_days.py lifts by itself when its AIs start
Wait-Api { param($s) $s.ready } "the game to load the save" | Out-Null
Invoke-RestMethod -Method Post "$api/api/control" -Body '{"action":"stop_now"}' | Out-Null
Wait-Api { param($s) $s.screens -eq 4 } "the 4 split screens" | Out-Null

# exclusive fullscreen 4K, music off
Invoke-RestMethod -Method Post "$api/api/debug/p1/set_display" -Body '{"fullscreen":true,"w":3840,"h":2160,"music":0}' | Out-Null
$looks = Get-Content runner/looks.json -Raw | ConvertFrom-Json
foreach ($p in 'p1','p2','p3') { Invoke-RestMethod -Method Post "$api/api/debug/$p/set_look" -Body ($looks.$p | ConvertTo-Json -Compress) | Out-Null }

$state = Invoke-RestMethod "$api/api/state"
Write-Host "Ready: day $($state.day) time $($state.time), screens $($state.screens)"
if ($state.time -ne "06:00") { Write-Warning "Clock is $($state.time), not 06:00: the save was not a day-start save. Check before recording." }
Invoke-RestMethod -Method Post "$api/api/debug/p1/capture" -Body '{}' | % text  # frame saved to stardew-capture.png in the temp folder

# The recorder gets its own window; the runner runs here. run_days lifts the hold once the AIs are connected.
# Copies of both outputs go to <out>/recorder.out and <out>/run_days.out (Tee-Object would write UTF-16).
$stop = "$Out/recorder.stop"
Remove-Item $stop -ErrorAction SilentlyContinue
Start-Process powershell -ArgumentList "-Command","`$Host.UI.RawUI.WindowTitle='Stardew Recorder'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; cd '$repo'; py -u runner/obs_recorder.py --out '$Out' --stop-on-exit --stop-file '$stop' -v 2>&1 | % { `$_; `$_ | Out-File -Append -Encoding utf8 '$Out/recorder.out' }"
Start-Sleep 3
$ErrorActionPreference = "Continue"  # PowerShell 5.1 turns the runner's stderr lines into errors; "Stop" would end here
py -u runner/run_days.py --multi --days $Days --out $Out --skip-smoke --game-cmd "`"$game\StardewModdingAPI.exe`"" 2>&1 |
    % { $line = "$_"; $line; $line | Out-File -Append -Encoding utf8 "$Out/run_days.out" }
$code = $LASTEXITCODE

# Batch over (the night's money summary is already recorded: the runner ends after the next morning starts).
# The recorder stops OBS and closes its window; the AI CLIs died with the runner (game_watchdog.die_with_runner).
New-Item $stop -ItemType File -Force | Out-Null
if ($code -ne 0) {
    # -File exits 0 unless told otherwise: record_day.bat keeps its window open only on a non-zero code
    Write-Warning "run_days.py ended with exit code $code (see $Out/run_days.out). Recording stops in a moment."
    exit $code
}
Write-Host "Batch done. Recording stops in a moment; the game stays open with the world held."

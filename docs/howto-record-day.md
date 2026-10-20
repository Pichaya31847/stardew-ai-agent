# How to: record one day, from launching the game to an MP4

The short path for one batch. The one-time setup (DSR, OBS scene, cabins, wallets) and the reasons behind each step
are in [recording.md](recording.md).

Before you start:
- `Mods/StardewMCP/config.json`: `LogDir` is `<out>/logs` (here `runs-ep01/logs`) and `AutoLoadSave` is the save folder.
  `<out>` may be any folder, spaces allowed (e.g. one per episode next to the videos); setting OBS's recording folder
  (Settings → Output) to the same `<out>` keeps each day's video with its logs.
- OBS is open on the recording scene with WebSocket on port 4455. If WebSocket needs a password, put it in
  `OBS_WS_PASSWORD`. The scene has **one** game source, a Game Capture fitted to the canvas (Transform → Fit to
  screen), plus the three `/overlay?player=pN` strips. No Window or Display Capture of the game stays enabled above it:
  in exclusive fullscreen they only get stale frames, and covering the Game Capture they recorded a frozen picture with
  old purple screens (2026-10-19). `obs_recorder.py` refuses such a scene, and a Game Capture that shows only black.
- Start everything from your own desktop (a terminal you opened, or a double-click), never from an agent's background
  shell: a game started on a hidden desktop renders where OBS can't hook it.

## Quick way: one script

```
runner\record_day.bat 1 runs-ep01
```

It does steps 1–5 below: opens OBS on the `stardew` scene if it isn't running, puts the game on the main monitor, launches it, holds the world the moment it answers, sets
exclusive fullscreen 4K and music 0, applies the looks, warns if the clock isn't 06:00, opens the recorder in its own
window and runs the runner in its own (output also in `runs-ep01/recorder.out` and `run_days.out`). Close the game first.

When the runner ends (after the next morning has started, so the night's money summary is in the file) it drops
`<out>/recorder.stop`: the recorder stops OBS and closes. Every AI CLI runs in a Windows job tied to the runner, so it
dies with the runner however that ends, even when its window is closed: nothing keeps spending a subscription. The game
stays open with the world held.

## 1. Launch the game (terminal 1, PowerShell)

The game must open on the main monitor: `displayIndex` in `%APPDATA%\StardewValley\startup_preferences` is `0`.

```powershell
& "C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley\StardewModdingAPI.exe"
```

As soon as the API answers, hold the world so the clock doesn't run while no AI is connected (use `127.0.0.1`:
PowerShell can stall on `localhost`; while it stalled once, the clock ran from 6:00 to 21:40 and pulling it back with
`set_time` left the farmhand screens dark at a wrong time):

```powershell
Invoke-RestMethod -Method Post 127.0.0.1:8770/api/control -Body '{"action":"stop_now"}'
```

`stop_now` is the hold `run_days.py` lifts itself when its AIs start; a `pause` waits for you to run
`py runner/run_control.py resume`.

Wait for `ready = True` and `screens = 4` (the mod loads the save and joins P2–P4 itself):

```powershell
Invoke-RestMethod 127.0.0.1:8770/api/state | Select ready,screens,paused,time
```

Exclusive fullscreen at 3840×2160 and music off, if the game options don't have them yet:

```powershell
Invoke-RestMethod -Method Post 127.0.0.1:8770/api/debug/p1/set_display -Body '{"fullscreen":true,"w":3840,"h":2160,"music":0}'
```

## 2. Check the resolution

```powershell
Invoke-RestMethod -Method Post 127.0.0.1:8770/api/debug/p1/capture -Body '{}'
```

The frame is saved as `stardew-capture.png` in your temp folder (`$env:TEMP`). The result must say `(3840x2160)`. `1920x1080` means the game isn't in exclusive fullscreen 4K: fix it in the game options.

P2–P4 can be black in this picture: if the farmhands joined after the world was held, their fade-in waits until the
world runs. It finishes about a second after the first AI acts.

## 3. Apply the looks

The farmhands (Gemini, GPT, Camera) are created again by the mod on every launch until the first night saves them,
so their looks from `runner/looks.json` must be set again after each launch:

```powershell
$looks = Get-Content runner/looks.json -Raw | ConvertFrom-Json; foreach ($p in 'p1','p2','p3') { Invoke-RestMethod -Method Post "http://127.0.0.1:8770/api/debug/$p/set_look" -Body ($looks.$p | ConvertTo-Json -Compress) }
```

## 4. Start the recorder (terminal 2)

```powershell
py runner/obs_recorder.py --out runs-ep01 --stop-on-exit -v
```

It starts OBS recording and pauses it whenever the world is frozen.

## 5. Run the day (terminal 3)

```powershell
$env:PYTHONIOENCODING="utf-8"
```

```powershell
py -u runner/run_days.py --multi --days 1 --out runs-ep01 --skip-smoke --game-cmd "`"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley\StardewModdingAPI.exe`""
```

With the `stop_now` hold from step 1, `run_days.py` lifts it when the AIs start (after a `pause`, run
`py runner/run_control.py resume`). A day takes
about 1.5–2 h of real time. For more days, change `--days`; the next batch continues from the day in the save.

## 6. Get the MP4

When `run_days.py` has finished, press Ctrl+C in terminal 2: OBS stops and leaves an `.mkv` in its recording folder
(Settings → Output; OBS's default is `$HOME\Videos`, or `<out>` if you set it as above). Remux the newest one to MP4
(no re-encode):

```powershell
Get-ChildItem $HOME\Videos\*.mkv | Sort LastWriteTime | Select -Last 1 | % { ffmpeg -i $_.FullName -c copy ($_.FullName -replace '\.mkv$','.mp4') }
```

Markers and per-day story tables for editing: `py runner/story_export.py --out runs-ep01 --media-dir $HOME\Videos`
(see [recording.md](recording.md#after-the-batch)).

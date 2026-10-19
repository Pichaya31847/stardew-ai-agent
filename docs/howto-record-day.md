# How to: record one day, from launching the game to an MP4

The short path for one batch. The one-time setup (DSR, OBS scene, cabins, wallets) and the reasons behind each step
are in [recording.md](recording.md).

Before you start:
- `Mods/StardewMCP/config.json`: `LogDir` is `<out>/logs` (here `runs-ep01/logs`) and `AutoLoadSave` is the save folder.
- Game options: exclusive **Fullscreen at 3840×2160** (DSR 4.00x).
- OBS is open on the recording scene (Game Capture + the three `/overlay?player=pN` strips) with WebSocket on port 4455.
  If WebSocket needs a password, put it in `OBS_WS_PASSWORD`.
- Game Music volume is 0.

## 1. Launch the game (terminal 1, PowerShell)

```powershell
& "C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley\StardewModdingAPI.exe"
```

As soon as the API answers, hold the world so the clock doesn't run while no AI is connected:

```powershell
Invoke-RestMethod -Method Post localhost:8770/api/control -Body '{"action":"pause"}'
```

Wait for `ready = True` and `screens = 4` (the mod loads the save and joins P2–P4 itself):

```powershell
Invoke-RestMethod localhost:8770/api/state | Select ready,screens,paused,time
```

## 2. Check the resolution

```powershell
Invoke-RestMethod -Method Post localhost:8770/api/debug/p1/capture -Body '{"path":"C:/Users/admin/Videos/check.png"}'
```

The result must say `(3840x2160)`. `1920x1080` means the game isn't in exclusive fullscreen 4K: fix it in the game options.

P2–P4 can be black in this picture: if the farmhands joined after the world was held, their fade-in waits until the
world runs. It finishes about a second after the first AI acts.

## 3. Apply the looks

The farmhands (Gemini, GPT, Camera) are created again by the mod on every launch until the first night saves them,
so their looks from `runner/looks.json` must be set again after each launch:

```powershell
$looks = Get-Content runner/looks.json -Raw | ConvertFrom-Json; foreach ($p in 'p1','p2','p3') { Invoke-RestMethod -Method Post "http://localhost:8770/api/debug/$p/set_look" -Body ($looks.$p | ConvertTo-Json -Compress) }
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

Don't run `run_control.py resume` yourself: `run_days.py` lifts the hold when the AIs are connected. A day takes
about 1.5–2 h of real time. For more days, change `--days`; the next batch continues from the day in the save.

## 6. Get the MP4

When `run_days.py` has finished, press Ctrl+C in terminal 2: OBS stops and leaves an `.mkv` in `C:\Users\admin\Videos`.
Remux the newest one to MP4 (no re-encode):

```powershell
Get-ChildItem $HOME\Videos\*.mkv | Sort LastWriteTime | Select -Last 1 | % { ffmpeg -i $_.FullName -c copy ($_.FullName -replace '\.mkv$','.mp4') }
```

Markers and per-day story tables for editing: `py runner/story_export.py --out runs-ep01 --media-dir $HOME\Videos`
(see [recording.md](recording.md#after-the-batch)).

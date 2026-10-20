# Recording a batch

One episode (30–50 min) ≈ 10 in-game days ≈ one unattended batch (about 1.5–2 h of real time per day).
The recording holds only the game; the VO is written from the logs and recorded afterwards in DaVinci Resolve.

## Once: display and game

1. NVIDIA Control Panel → Manage 3D settings → DSR - Factors: **4.00x (legacy)**, DSR - Smoothness **0%**.
   On a 1920×1080 monitor this adds 3840×2160.
2. Game options: **Fullscreen** (exclusive) at **3840×2160**. Each split-screen quadrant is then 1920×1080 and the
   mod's auto zoom (1.5 × width / 960 = 3.0) keeps about 20 tiles in view.
3. `setx SDL_VIDEO_MINIMIZE_ON_FOCUS_LOSS 0` (new terminals pick it up), so the game keeps rendering when OBS has focus.
4. PC sleep and display sleep off for the batch.

## Once: OBS

- Video: base and output **3840×2160**, **60 fps**.
- Output → Advanced. Recording: its **own encoder** (not "same as stream", or pause does not work), NVENC (AV1 or
  HEVC), CQP; container **MKV** (survives a crash); automatic file splitting **off** (the recorder starts a new file
  per day itself). Remux to MP4 after the batch (File → Remux Recordings).
- Audio: game audio on track 1. No mic (the VO is recorded later).
- Music: the recorder pauses OBS hundreds of times a day and every pause cuts the audio. Set the game's Music
  volume to 0 (Options, keep Sound) and lay one continuous music bed in Resolve; cuts in sound effects are barely heard.
- Tools → WebSocket Server Settings: enabled, port 4455, a password; put it in `OBS_WS_PASSWORD`.
- Scene with:
  - Game Capture of Stardew Valley, full canvas.
  - Three Browser sources, 1920×360 each, custom CSS cleared, each on the top edge of its own AI's quadrant
    (transparent; a box with the AI's latest thought appears top left, clear of the clock, the chat in the bottom left
    and the toolbar, which the mod pins to the bottom):

    | Source | URL | Position |
    |---|---|---|
    | think P1 | `http://localhost:8770/overlay?player=p1` | 0, 0 |
    | think P2 | `http://localhost:8770/overlay?player=p2` | 1920, 0 |
    | think P3 | `http://localhost:8770/overlay?player=p3` | 0, 1080 |

    Add `&mock=1` to see sample text without the game. Speech bubbles and the names above the farmers are drawn at
    their real place in the world, the same on every screen, and are cut at the screen edge like the game's own.

## Once per farm, before day 1

With `EnableDebugApi` on and the save loaded (POST with a JSON body):

```
curl -X POST localhost:8770/api/debug/p1/starting_cabins -d "{\"n\":3}"
curl -X POST localhost:8770/api/debug/p1/separate_wallets -d "{}"
```

`starting_cabins` is only needed on a farm made without cabins: it places the game's own starting cabins next to the
farmhouse, and the mod then joins Gemini, GPT and Camera.

Then give each AI its look from `runner/looks.json` (PowerShell):

```
$looks = Get-Content runner/looks.json -Raw | ConvertFrom-Json
foreach ($p in 'p1','p2','p3') { Invoke-RestMethod -Method Post "http://localhost:8770/api/debug/$p/set_look" -Body ($looks.$p | ConvertTo-Json -Compress) }
```

Separate wallets: `run_days.py --multi` refuses a shared one. The game itself saves only at the end of a day, so keep
all of this with a save right away: hold the world first, or the clock runs while no AI is connected.

```
curl -X POST localhost:8770/api/control -d "{\"action\":\"pause\"}"
curl -X POST localhost:8770/api/debug/p1/set_time -d "{\"time\":600}"
curl -X POST localhost:8770/api/debug/p1/save -d "{}"
```

Loading that save starts the same day at the saved time. Pants are in the seat colour the strips use
(P1 orange พี่ส้ม, P2 blue พี่ฟ้า, P3 green พี่เขียว).

To design a look: `POST /api/debug/p1/makeover` opens the full character creator on P1's screen with the mouse unlocked
(farmhand screens take no mouse). Press OK, read it with `POST /api/debug/p1/look`, put it in `runner/looks.json` under
the seat, then `set_look` P1 back to its own look.

## Each batch

Step by step from launching the game to the MP4: [howto-record-day.md](howto-record-day.md).

Terminal 1 (the game running, save loaded):

```
py runner/run_control.py resume
py runner/smoke.py
```

Terminal 2:

```
py runner/obs_recorder.py --out runs-ep01 --stop-on-exit
```

Terminal 3:

```
py runner/run_days.py --multi --days 10 --out runs-ep01 --skip-smoke --game-cmd "<SMAPI command>"
```

- The recorder pauses OBS as soon as it sees the world frozen (`--min-pause 0`, polled every 0.05 s): the frames
  before and after a frozen stretch are the same, so the cut is seamless, while every frozen frame let through shows
  as a hitch (a 0.45 s one with the old 0.3 s min-pause, 2026-10-19). Only the sound is cut (see Music above).
- `LogDir` in `Mods/StardewMCP/config.json` must be `<out>/logs` (set it before launching the game);
  `run_days.py` refuses to start otherwise, since the AIs would lose their diaries and the export its logs.
- The next batch is the same command with the same `--out`; it continues from the day in the save.
- A provider outage or usage limit holds the whole world (no footage) until that AI answers again, at most
  `--outage-cap-hours` (default 6). Past the cap the batch stops and prints how to replay the day
  (`runner/restore_day.py`).
- When the runner exits for any reason the world stays held (stop_now); the next batch lifts it.

## After the batch

1. Stop the recorder (Ctrl+C), remux the MKVs, move them off the system drive (a 4K day can be tens of GB).
2. `py runner/story_export.py --out runs-ep01 --media-dir <folder with the MP4s>` writes:
   - `edit/markers.csv`: import into Resolve with the AI Life Sim marker importer;
   - `edit/story_day_NNN.md`: per day, video time | game time | P1 | P2 | P3 | world, for writing the VO.
3. In Resolve (1920×1080 timeline): crop the focused player's quadrant at 100 %; its thought box is already in it.

## Trial: record continuously, cut the still time afterwards

Pausing OBS from the recorder lags the game (polling + websocket), so the start of live stretches and the
result shown when a tool ends (item received, HUD message) get cut. The trial records everything and cuts the
frozen stretches from the picture itself, into a Resolve timeline that can still be trimmed by hand.

1. Record about 10 min of a warm-up with `py runner/obs_recorder.py --no-pause --out runs-test --stop-on-exit`
   (still one file per day; `video_map.jsonl` gets only start / stop). Remux to MP4.
2. Install once: `py -m pip install auto-editor` (check the flags with `auto-editor --help`; they change between versions).
3. Cut each file:

   ```
   auto-editor day_001.mp4 --edit motion:threshold=0.02 --margin 0.3sec,1.5sec --export resolve
   ```

   `--edit motion` keeps frames that differ from the previous one; the world is frozen for all four screens at
   once, so a still grid means everyone waits for an AI. `--margin before,after` keeps 0.3 s before movement
   starts and 1.5 s after it stops (the item box, a thought appearing). It writes `day_001_ALTERED.fcpxml`.
4. Resolve: File → Import → Timeline → the `.fcpxml`, with the MP4 in the media pool. A cut that came out short
   is dragged longer by its edge: the source file has every frame.

Check before switching the batch to it:
- [ ] A frozen grid is truly still (water, rain, clouds): if not, raise `threshold` until the waits are cut.
- [ ] The item-received box and a new thought stay long enough to read; raise the after-margin if not.
- [ ] GB per hour of continuous 4K (still frames should compress to almost nothing).
- [ ] `story_export.py` markers are at raw-file times: check whether they still land on the cut timeline.

## Rehearsal checklist (3 seats, 3 days, a throwaway save and `--out`)

- [ ] One file per day; every file remuxes.
- [ ] Day 1: the strangers find each other and their first talk is in the footage.
- [ ] A strip cropped onto a 1080p timeline is readable.
- [ ] 10 random markers land within 0.3 s; the last `stop` in `video_map.jsonl` matches `ffprobe` duration (±2 frames);
      OBS shows 0 skipped frames. Rehearsal (2026-10-19): every file ran 0.24 s (14 frames) past its `stop`, the same in
      all three files despite ~150 pauses each, so it does not build up with pauses. If the markers still land, the extra
      frames are at the end of the file and can be ignored.
- [ ] Network unplugged ~12 min mid-day: game time doesn't move, no footage of the wait, everyone resumes.
- [ ] Game window closed mid-day: the watchdog relaunches it and replays the day.
- [ ] One AI in bed, F8: the clock stops.
- [ ] 15 min after the batch ends the world is still held.
- [ ] `usage_p3.csv` (opencode) has non-zero tokens.
- [ ] GB per day measured; if it is too much, raise CQP or record at 30 fps.

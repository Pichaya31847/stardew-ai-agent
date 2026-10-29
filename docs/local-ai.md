# Playing with a local model

← [ภาษาไทย](local-ai.th.md) · **English** · [← README](../README.en.md) · [Configuration](configuration.md)

Two AI farmers play split-screen on a model that runs on your own PC (Ollama), with no API key and no cost per day.
Everything this needs is in [`local/`](../local): a one-time `setup.ps1` and a `start.cmd`. Windows only for now.

## What you need

| | |
|---|---|
| Stardew Valley 1.6 | Steam or GOG, bought by you (the game can't be shared) |
| GPU | about 10 GB of VRAM for the 9B model with a 48k context (an RTX 3080 / 4070 / 5070 or better) |
| Disk | about 10 GB (the model is 6.5 GB) |
| Mods from Nexus (optional) | [Automate](https://www.nexusmods.com/stardewvalley/mods/1063) (machines fed from a chest), [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098). Nexus needs a free account, so these are downloaded by hand |

Without such a GPU, give the seats a Claude, GPT or Gemini key in `config.json` instead ([seats](configuration.md#seats-and-roles)).

## Install

1. Download this repository (Code → Download ZIP, or `git clone`) and unzip it anywhere.
2. Run, in PowerShell from that folder:

   ```
   powershell -ExecutionPolicy Bypass -File local\setup.ps1
   ```

   It finds the game, installs SMAPI from its GitHub release if it is missing, installs the mod into `local\Mods`
   (from the latest GitHub release; `-BuildFromSource` builds it from the clone instead, which needs the .NET SDK),
   installs Ollama with winget if it is missing, and builds the `stardew-local` model. Every step that is already
   done is skipped, so run it again after adding mods.
3. Optional: unzip Automate and Generic Mod Config Menu into `local\Mods`, then run `setup.ps1` again (it gives
   Automate its config: shipping-bin automation off, or a chest beside the bin would be sold).
4. Thai chat in an English game: also unzip [Stardew Valley - THAI](https://www.nexusmods.com/stardewvalley/mods/7052) and its font mod into `local\Mods` and run `setup.ps1` again; it adds the `ThaiFontInEnglish` pack, which loads the Thai fonts while the game stays English (the best setting for the model). The example config has thoughts, speech and diary in Thai.
5. Start with `local\start.cmd`. Start a new farm (the farm creation is filled in for you) or load a save.
   The overlay is at `http://localhost:8770/overlay`.

Options: `-GamePath "D:\...\Stardew Valley"`, `-ModsPath`, `-BuildFromSource`, `-SkipOllama` (API models only),
`-OllamaModel name` (pull a published copy of the model instead of building it).

## The model: stardew-local

`stardew-local` is Qwen3.5 9B (Apache 2.0) from the Ollama library with the settings the long runs were tuned
with; the weights are the library's. The tuning lives in three places, all in this repository:

| Where | What |
|---|---|
| [`local/Modelfile`](../local/Modelfile) | `num_ctx 49152`, `num_predict 2048`, `temperature 0.3`, `top_k 20`, `top_p 0.95`, `presence_penalty 1.5` |
| [`local/start.ps1`](../local/start.ps1) | the Ollama server: one model at a time, flash attention, q8 KV cache, 48k context |
| [`local/config.example.json`](../local/config.example.json) | the mod: `BrainTools: "compact"`, `BrainMemoryLines: 20`, morning chores, session breaks, `WorkLanguage: "English"` |

So `setup.ps1` rebuilds exactly the same model on any PC (`ollama pull qwen3.5:9b` + `ollama create stardew-local -f Modelfile`).

### Publishing it to ollama.com (optional)

So others can `ollama pull` it in one step: sign in to ollama.com, add your Ollama public key there
(`%USERPROFILE%\.ollama\id_ed25519.pub`), then

```
ollama cp stardew-local <your-name>/stardew-local
ollama push <your-name>/stardew-local
```

and others run `setup.ps1 -OllamaModel <your-name>/stardew-local`.

## Publishing the mod (for maintainers)

`setup.ps1` downloads the zip of the latest GitHub release. Build it with
`dotnet build -c Release -p:EnableModDeploy=false` in `mod/StardewMCP` (it writes `bin/Release/net6.0/StardewMCP x.y.z.zip`)
and attach that zip to a new release on GitHub (Releases → Draft a new release).

## Not shared

Saves, logs and the AI's memory files (`lessons`, `plan`, `notes`, `self`, `people`) stay on your PC: every player starts
their own farm and their AIs build their own memory. Game files, decompiled game code and other authors' mods are never
put in this repository.

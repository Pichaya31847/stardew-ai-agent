<#
.SYNOPSIS
  One-time setup for playing Stardew MCP with a local model (Ollama) on Windows.

.DESCRIPTION
  1. Finds Stardew Valley (Steam or GOG), or takes -GamePath.
  2. Installs SMAPI from its official GitHub release if it is missing.
  3. Installs the Stardew MCP mod into a separate mods folder (local\Mods): from this repository's latest GitHub
     release, or built from this clone with -BuildFromSource. Copies config.example.json as its config.
  4. Checks the optional mods from Nexus (Automate, Generic Mod Config Menu) and gives Automate its config.
  5. Installs Ollama (winget) if it is missing, then builds the stardew-local model from the Modelfile
     (ollama pull qwen3.5:9b + ollama create), or pulls a published copy with -OllamaModel.
  6. Writes settings.json for start.cmd.
  Run it again any time: every step that is already done is skipped.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File local\setup.ps1
  powershell -ExecutionPolicy Bypass -File local\setup.ps1 -GamePath "D:\SteamLibrary\steamapps\common\Stardew Valley" -BuildFromSource
#>
param(
    [string]$GamePath = '',
    [string]$ModsPath = '',
    [string]$Repo = 'Pichaya31847/stardew-ai-agent',
    [switch]$BuildFromSource,
    [string]$OllamaModel = '',
    [switch]$SkipOllama
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$downloads = Join-Path $here 'downloads'
New-Item -ItemType Directory -Force $downloads | Out-Null
if (-not $ModsPath) { $ModsPath = Join-Path $here 'Mods' }
New-Item -ItemType Directory -Force $ModsPath | Out-Null

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Note($text) { Write-Host "   $text" }
function IsGameFolder($p) { $p -and (Test-Path (Join-Path $p 'Stardew Valley.dll')) }

# ---------------------------------------------------------------- 1. the game
Step 'Stardew Valley'
if (-not (IsGameFolder $GamePath)) {
    $candidates = @()
    foreach ($key in 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKCU:\SOFTWARE\Valve\Steam') {
        try {
            $steam = (Get-ItemProperty $key -ErrorAction Stop).InstallPath
            if (-not $steam) { $steam = (Get-ItemProperty $key -ErrorAction Stop).SteamPath }
            if ($steam) {
                $candidates += Join-Path $steam 'steamapps\common\Stardew Valley'
                $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
                if (Test-Path $vdf) {
                    foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                        $candidates += Join-Path ($m.Groups[1].Value -replace '\\\\', '\') 'steamapps\common\Stardew Valley'
                    }
                }
            }
        } catch { }
    }
    $candidates += 'C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley', 'C:\Program Files (x86)\GOG Galaxy\Games\Stardew Valley', 'C:\GOG Games\Stardew Valley'
    $GamePath = $candidates | Where-Object { IsGameFolder $_ } | Select-Object -First 1
}
if (-not (IsGameFolder $GamePath)) {
    $GamePath = Read-Host 'Stardew Valley folder (the one with "Stardew Valley.dll")'
    if (-not (IsGameFolder $GamePath)) { Write-Error "Not a Stardew Valley folder: $GamePath"; exit 1 }
}
Note "Game: $GamePath"

# ---------------------------------------------------------------- 2. SMAPI
Step 'SMAPI'
if (Test-Path (Join-Path $GamePath 'StardewModdingAPI.exe')) {
    Note 'Already installed.'
} else {
    $rel = Invoke-RestMethod 'https://api.github.com/repos/Pathoschild/SMAPI/releases/latest'
    $asset = $rel.assets | Where-Object { $_.name -match '^SMAPI-[\d.]+-installer\.zip$' } | Select-Object -First 1
    $zip = Join-Path $downloads $asset.name
    Note "Downloading $($asset.name) ($([math]::Round($asset.size / 1MB)) MB) from github.com/Pathoschild/SMAPI"
    Invoke-WebRequest $asset.browser_download_url -OutFile $zip
    $dir = Join-Path $downloads 'smapi-installer'
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    Expand-Archive $zip -DestinationPath $dir
    $installer = Get-ChildItem $dir -Recurse -Filter 'SMAPI.Installer.exe' | Where-Object { $_.FullName -match '\\windows\\' } | Select-Object -First 1
    if (-not $installer) { Write-Error "SMAPI.Installer.exe not found in $dir; run the installer there by hand."; exit 1 }
    & $installer.FullName --install --no-prompt --game-path $GamePath
    if (-not (Test-Path (Join-Path $GamePath 'StardewModdingAPI.exe'))) { Write-Error 'SMAPI did not install.'; exit 1 }
    Note 'Installed.'
}

# ---------------------------------------------------------------- 3. Stardew MCP
Step 'Stardew MCP mod'
$mcp = Join-Path $ModsPath 'StardewMCP'
if ($BuildFromSource) {
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if (-not $dotnet) { Write-Error 'The .NET 6+ SDK is needed to build (https://dotnet.microsoft.com/download).'; exit 1 }
    $proj = Join-Path $here '..\mod\StardewMCP'
    & $dotnet build $proj -c Release "-p:GamePath=$GamePath" -p:EnableModDeploy=false -p:EnableModZip=true
    if ($LASTEXITCODE -ne 0) { Write-Error 'Build failed.'; exit 1 }
    $zip = Get-ChildItem (Join-Path $proj 'bin\Release\net6.0') -Filter '*.zip' | Sort-Object LastWriteTime | Select-Object -Last 1
} else {
    $rel = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest"
    $asset = $rel.assets | Where-Object { $_.name -match '\.zip$' } | Select-Object -First 1
    if (-not $asset) { Write-Error "The latest release of $Repo has no zip; run again with -BuildFromSource."; exit 1 }
    $zip = Get-Item (Join-Path $downloads $asset.name) -ErrorAction SilentlyContinue
    if (-not $zip) {
        Note "Downloading $($asset.name) from github.com/$Repo ($($rel.tag_name))"
        Invoke-WebRequest $asset.browser_download_url -OutFile (Join-Path $downloads $asset.name)
        $zip = Get-Item (Join-Path $downloads $asset.name)
    }
}
$keep = if (Test-Path (Join-Path $mcp 'config.json')) { Get-Content (Join-Path $mcp 'config.json') -Raw } else { $null }
Expand-Archive $zip.FullName -DestinationPath $ModsPath -Force
if ($keep) { Set-Content (Join-Path $mcp 'config.json') $keep -Encoding UTF8; Note 'Kept your config.json.' }
else {
    $cfg = Get-Content (Join-Path $here 'config.example.json') -Raw
    if ($OllamaModel) { $cfg = $cfg.Replace('"stardew-local"', '"' + $OllamaModel + '"') }
    Set-Content (Join-Path $mcp 'config.json') $cfg -Encoding UTF8
    Note 'Wrote config.json from config.example.json (two AI farmers on the local model, split-screen).'
}
Note "Installed into $mcp"

# ---------------------------------------------------------------- 4. mods from Nexus
Step 'Optional mods (Nexus needs a free account, so download them yourself into the mods folder)'
$optional = @(
    @{ Id = 'Automate'; Url = 'https://www.nexusmods.com/stardewvalley/mods/1063'; Why = 'machines fed from a chest beside them' },
    @{ Id = 'GenericModConfigMenu'; Url = 'https://www.nexusmods.com/stardewvalley/mods/5098'; Why = 'change the settings in game' }
)
foreach ($m in $optional) {
    $found = Get-ChildItem $ModsPath -Directory | Where-Object { $_.Name -like "*$($m.Id)*" } | Select-Object -First 1
    if ($found) { Note "$($m.Id): installed" } else { Note "$($m.Id): not installed ($($m.Why)): $($m.Url)" }
}
$automate = Get-ChildItem $ModsPath -Directory | Where-Object { $_.Name -like '*Automate*' } | Select-Object -First 1
if ($automate -and -not (Test-Path (Join-Path $automate.FullName 'config.json'))) {
    Copy-Item (Join-Path $here 'automate-config.json') (Join-Path $automate.FullName 'config.json')
    Note 'Automate: shipping-bin automation turned off (a chest beside the bin would be sold).'
}
Note "Mods folder: $ModsPath (unzip each mod there, then run setup.ps1 again)"

# ---------------------------------------------------------------- 5. Ollama and the model
$ollamaExe = ''
if (-not $SkipOllama) {
    Step 'Ollama and the stardew-local model'
    $ollamaExe = (Get-Command ollama -ErrorAction SilentlyContinue).Source
    if (-not $ollamaExe -and (Test-Path "$env:LOCALAPPDATA\Programs\Ollama\ollama.exe")) { $ollamaExe = "$env:LOCALAPPDATA\Programs\Ollama\ollama.exe" }
    if (-not $ollamaExe) {
        if (Get-Command winget -ErrorAction SilentlyContinue) {
            Note 'Installing Ollama with winget...'
            winget install -e --id Ollama.Ollama --accept-source-agreements
            $ollamaExe = "$env:LOCALAPPDATA\Programs\Ollama\ollama.exe"
        }
        if (-not (Test-Path $ollamaExe)) { Write-Error 'Install Ollama from https://ollama.com/download, then run setup.ps1 again.'; exit 1 }
    }
    Note "Ollama: $ollamaExe"
    try { $null = Invoke-RestMethod 'http://127.0.0.1:11434/api/version' -TimeoutSec 2 }
    catch {
        Start-Process -FilePath $ollamaExe -ArgumentList 'serve' -WindowStyle Hidden
        for ($i = 0; $i -lt 60; $i++) { Start-Sleep 1; try { $null = Invoke-RestMethod 'http://127.0.0.1:11434/api/version' -TimeoutSec 2; break } catch { } }
    }
    if ($OllamaModel) {
        Note "Pulling $OllamaModel"
        & $ollamaExe pull $OllamaModel
    } else {
        Note 'Pulling qwen3.5:9b (about 6.5 GB) and building stardew-local from the Modelfile'
        & $ollamaExe pull qwen3.5:9b
        & $ollamaExe create stardew-local -f (Join-Path $here 'Modelfile')
    }
    if ($LASTEXITCODE -ne 0) { Write-Error 'The model step failed.'; exit 1 }
}

# ---------------------------------------------------------------- 6. settings for start.cmd
@{ GamePath = $GamePath; ModsPath = $ModsPath; OllamaExe = $ollamaExe; OllamaModels = '' } |
    ConvertTo-Json | Set-Content (Join-Path $here 'settings.json') -Encoding UTF8
Step 'Done'
Note 'Start the game with local\start.cmd. Start a new farm or load a save; the AIs begin when the day starts.'
Note "Overlay for OBS or a browser: http://localhost:8770/overlay  Settings: $mcp\config.json"

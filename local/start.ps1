# Starts Ollama (if it isn't running) with the settings the local runs use, then Stardew Valley through SMAPI with the
# mods folder setup.ps1 filled. Paths come from settings.json next to this script (written by setup.ps1).
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$settingsFile = Join-Path $here 'settings.json'
if (-not (Test-Path $settingsFile)) { Write-Error 'settings.json is missing: run setup.ps1 first.'; exit 1 }
$s = Get-Content $settingsFile -Raw | ConvertFrom-Json

# The server settings matter as much as the Modelfile: one model at a time, flash attention and a q8 KV cache keep
# the 48k context of the 9B model in about 10 GB of VRAM.
$env:OLLAMA_HOST = '127.0.0.1:11434'
$env:OLLAMA_NUM_PARALLEL = '1'
$env:OLLAMA_MAX_LOADED_MODELS = '1'
$env:OLLAMA_FLASH_ATTENTION = '1'
$env:OLLAMA_KV_CACHE_TYPE = 'q8_0'
$env:OLLAMA_CONTEXT_LENGTH = '49152'
if ($s.OllamaModels) { $env:OLLAMA_MODELS = $s.OllamaModels }

function Test-Ollama { try { $null = Invoke-RestMethod 'http://127.0.0.1:11434/api/version' -TimeoutSec 2; $true } catch { $false } }

if ($s.OllamaExe -and -not (Test-Ollama)) {
    Write-Host 'Starting Ollama...'
    Start-Process -FilePath $s.OllamaExe -ArgumentList 'serve' -WindowStyle Hidden
    for ($i = 0; $i -lt 60 -and -not (Test-Ollama); $i++) { Start-Sleep -Seconds 1 }
    if (-not (Test-Ollama)) { Write-Error 'Ollama did not start.'; exit 1 }
}
if (Get-Process -Name StardewModdingAPI -ErrorAction SilentlyContinue) { Write-Host 'Stardew Valley is already running.'; exit 1 }

$smapi = Join-Path $s.GamePath 'StardewModdingAPI.exe'
Write-Host "Starting Stardew Valley with the mods in $($s.ModsPath)"
Start-Process -FilePath $smapi -ArgumentList "--mods-path `"$($s.ModsPath)`"" -WorkingDirectory $s.GamePath

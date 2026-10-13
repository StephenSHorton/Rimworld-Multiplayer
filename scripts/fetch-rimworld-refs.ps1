# Copy the RimWorld 1.7 assemblies Multiplayer compiles against into Refs\RimWorld\.
# They are Ludeon's and the game's own files: Refs\ is gitignored, never commit them.
#
# Usage:
#   .\scripts\fetch-rimworld-refs.ps1
#   .\scripts\fetch-rimworld-refs.ps1 -RimWorldDir "D:\Games\RimWorld"
#
# RimWorld 1.7 keeps its managed DLLs in the install root (no RimWorldWin64_Data\Managed).

param(
    [string]$RimWorldDir = $(if ($env:RIMWORLD_DIR) { $env:RIMWORLD_DIR } else { "C:\Program Files (x86)\Steam\steamapps\common\RimWorld" })
)

$ErrorActionPreference = "Stop"
$dest = Join-Path $PSScriptRoot "..\Refs\RimWorld"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

foreach ($dll in @("Assembly-CSharp.dll", "UnityStubs.dll", "com.rlabrecque.steamworks.net.dll")) {
    $src = Join-Path $RimWorldDir $dll
    if (-not (Test-Path $src)) {
        throw "Missing $src. Is this a RimWorld 1.7 install?"
    }
    Copy-Item $src (Join-Path $dest $dll) -Force
    Write-Host "copied $dll"
}

Write-Host "RimWorld refs are in $dest"

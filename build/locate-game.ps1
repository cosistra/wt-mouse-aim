<#
.SYNOPSIS
  Resolve the Nuclear Option install folder and a BepInEx 5 core reference dir.

.DESCRIPTION
  Prints exactly ONE line to stdout: "<GamePath>|<BepInExCore>".
  All diagnostics go to stderr via [Console]::Error.WriteLine. (Note: with
  `powershell.exe -File`, the Write-Warning stream is folded into STDOUT, which
  would corrupt the build's capture of that single line — so we write the console
  error handle directly instead.) The build (csproj) captures stdout and splits
  on '|', so nothing else may be written to stdout.

  GamePath resolution order:
    a. -GamePath parameter (from MSBuild, may be empty) if it holds NuclearOption.exe
    b. $env:NUCLEAR_OPTION_PATH if it holds NuclearOption.exe
    c. Steam: SteamPath (HKCU) / InstallPath (HKLM WOW6432Node), check the default
       library, then every path in steamapps\libraryfolders.vdf
    d. else: error to stderr, exit 1

  BepInExCore resolution order:
    a. <game>\BepInEx\core   (if BepInEx.dll present — a real install)
    b. <repoRoot>\.deps\BepInEx\core  (repo-local reference cache)
    c. download BepInEx 5 x64 zip into <repoRoot>\.deps, extract, use its core

  The .deps cache is reference-only; BepInEx is NEVER installed into the game.
#>
[CmdletBinding()]
param(
    [string]$GamePath = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$BepInExUrl = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.3/BepInEx_win_x64_5.4.23.3.zip'
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$DepsDir = Join-Path $RepoRoot '.deps'

# Diagnostics -> real stderr (stdout must stay clean for the build to parse).
function Warn([string]$msg) { [Console]::Error.WriteLine("[locate-game] $msg") }
function Fail([string]$msg) { [Console]::Error.WriteLine("[locate-game] ERROR: $msg"); exit 1 }

function Test-GameDir([string]$dir) {
    if ([string]::IsNullOrWhiteSpace($dir)) { return $false }
    return Test-Path -LiteralPath (Join-Path $dir 'NuclearOption.exe')
}

function Get-SteamRoot {
    foreach ($p in @(
        @{ Path = 'HKCU:\Software\Valve\Steam'; Name = 'SteamPath' },
        @{ Path = 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam'; Name = 'InstallPath' }
    )) {
        try {
            $v = (Get-ItemProperty -LiteralPath $p.Path -Name $p.Name -ErrorAction Stop).($p.Name)
            if (-not [string]::IsNullOrWhiteSpace($v)) { return $v }
        } catch { }
    }
    return $null
}

function Resolve-GamePath {
    # (a) explicit parameter
    if (Test-GameDir $GamePath) { Warn "Game via -GamePath: $GamePath"; return $GamePath }

    # (b) environment override
    $envPath = $env:NUCLEAR_OPTION_PATH
    if (Test-GameDir $envPath) { Warn "Game via NUCLEAR_OPTION_PATH: $envPath"; return $envPath }

    # (c) Steam metadata
    $steam = Get-SteamRoot
    if ($steam) {
        $libs = New-Object System.Collections.Generic.List[string]
        $libs.Add($steam)
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            $text = Get-Content -LiteralPath $vdf -Raw
            foreach ($m in [regex]::Matches($text, '"path"\s*"([^"]*)"')) {
                $libs.Add(($m.Groups[1].Value -replace '\\\\', '\'))
            }
        }
        foreach ($lib in $libs) {
            $cand = Join-Path $lib 'steamapps\common\Nuclear Option'
            if (Test-GameDir $cand) { Warn "Game via Steam library: $cand"; return $cand }
        }
    }

    Fail @"
Could not locate Nuclear Option. Tried -GamePath, NUCLEAR_OPTION_PATH, and Steam libraries.
Set the game folder (the one containing NuclearOption.exe) via either:
  * an environment variable:  NUCLEAR_OPTION_PATH=<path>
  * an MSBuild property:      dotnet build /p:GamePath="<path>"
"@
}

function Resolve-BepInExCore([string]$game) {
    # (a) a real BepInEx install in the game folder
    $inGame = Join-Path $game 'BepInEx\core'
    if (Test-Path -LiteralPath (Join-Path $inGame 'BepInEx.dll')) {
        Warn "BepInEx from game install: $inGame"
        return $inGame
    }

    # (b) repo-local reference cache
    $cacheCore = Join-Path $DepsDir 'BepInEx\core'
    if (Test-Path -LiteralPath (Join-Path $cacheCore 'BepInEx.dll')) {
        Warn "BepInEx from .deps cache: $cacheCore"
        return $cacheCore
    }

    # (c) download + extract into the cache
    Warn "BepInEx not found; downloading reference DLLs to $DepsDir ..."
    New-Item -ItemType Directory -Force -Path $DepsDir | Out-Null
    $zip = Join-Path $DepsDir 'BepInEx_win_x64.zip'
    Invoke-WebRequest -Uri $BepInExUrl -OutFile $zip -UseBasicParsing
    # The zip carries a top-level BepInEx\ folder, so extract to the .deps root.
    $extract = Join-Path $DepsDir 'BepInEx'
    if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
    Expand-Archive -LiteralPath $zip -DestinationPath $DepsDir -Force
    Remove-Item -LiteralPath $zip -Force
    if (Test-Path -LiteralPath (Join-Path $cacheCore 'BepInEx.dll')) {
        Warn "BepInEx downloaded to: $cacheCore"
        return $cacheCore
    }
    Fail "BepInEx download/extract did not produce $cacheCore\BepInEx.dll"
}

$game = Resolve-GamePath
$core = Resolve-BepInExCore $game

# The one and only stdout line.
Write-Output ("{0}|{1}" -f $game, $core)

# WT Mouse Aim — Nuclear Option

A War Thunder–style mouse-aim ("smart instructor") mod for [Nuclear Option](https://store.steampowered.com/app/2168680/Nuclear_Option/),
built as a BepInEx 5 / HarmonyX plugin.

You point a **world-locked aim marker** where you want the nose to go; a thin instructor
rolls and pulls the aircraft onto that vector, then levels out as the nose arrives. The
game's own fly-by-wire still governs the envelope. Works in cockpit and 3rd-person views.

## Install

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx) (Mono build) into your Nuclear
   Option folder and run the game once to generate its folders.
2. Drop `NuclearOption-MouseAim.dll` into `<game>\BepInEx\plugins\WTMouseAim\`.
3. (Optional but recommended) Install the BepInEx **ConfigurationManager** plugin to tune
   everything live with **F1**.

First launch writes the config to `<game>\BepInEx\config\com.no.wtmouseaim.cfg`.

## Controls

| Input | Action |
|---|---|
| **Mouse** | Move the aim marker — the plane flies its nose onto it |
| **Stick / keyboard / pedals** | Per-axis manual override (take any axis instantly; release to hand it back) |
| **Right Mouse (hold)** | Freeze the marker and free-look the camera (War Thunder style) |
| **F7** | Toggle **Fly Level** — hold wings-level, velocity vector on the horizon |
| **F1** | Open the live config (with ConfigurationManager) |

## Tuning

Everything is live-tunable via F1, grouped into config sections: **Aim** (sensitivity,
smoothing, cone), **Control** (the instructor's gains and the roll-then-pull behaviour),
**Camera**, **FlyLevel**, and **HUD** (overlay + diagnostic logging). Sensible defaults
ship out of the box; the in-game descriptions explain each knob.

## Build from source

Requires the .NET SDK. Point `<GamePath>` in `NuclearOption-MouseAim.csproj` at your
install (the folder containing `NuclearOption.exe`, with BepInEx 5 installed into it):

```
dotnet build NuclearOption-MouseAim.csproj -c Release
```

Then copy `bin\Release\NuclearOption-MouseAim.dll` into `<game>\BepInEx\plugins\WTMouseAim\`.

## Requirements

- Nuclear Option
- BepInEx 5 (Mono)

## License

MIT — see [LICENSE](LICENSE). Game code is **not** included or redistributed.

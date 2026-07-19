# WT Mouse Aim — Nuclear Option

A War Thunder–style **mouse-aim** mod for [Nuclear Option](https://store.steampowered.com/app/2168680/Nuclear_Option/),
built as a BepInEx 5 / HarmonyX plugin.

You point a **world-locked aim marker** where you want the nose to go. A thin "instructor"
flies the aircraft onto that vector — it rolls the lift vector onto the marker, pulls up into
it, and levels out as the nose arrives. You stop flying the airframe and start flying the
*reticle*, the way you do in War Thunder's mouse-aim. The game's own fly-by-wire still governs
the flight envelope (stall, G, AoA, auto-trim), so the mod is a layer on top, not a replacement.

Works in both **cockpit** and **3rd-person** views, and on **every airframe** — fixed-wing
jets, helicopters, and VTOLs all fly off the same reticle.

---

## See it in action

**Cockpit gunnery** — point the reticle at a target and the instructor flies the guns onto it.

https://github.com/user-attachments/assets/e1ec4f22-4545-4636-aac7-f4b432983ea4

**Third-person gunnery** — the same point-and-shoot, now with the WT-style chase camera tracking the aim.

https://github.com/user-attachments/assets/445cf942-f47b-4fd4-85a4-887fec76c296

**Third-person combat** — maneuvering and firing in 3rd-person; the camera leads with the marker through the turns.

https://github.com/user-attachments/assets/9d779c48-f08e-4a09-bcf5-a8cdd02b59cd

> **Tip:** the 3rd-person camera looks and reads best with the **Quality of Life (QoL)** overhaul mod installed (available in NOMM) and the game's **third-person HUD** turned on.

**Target marking + missiles** — designating targets and sending missiles while the aim layer keeps the nose where you point.

https://github.com/user-attachments/assets/78deb5d6-fac2-4617-9171-f3c5fb8ce341

**Fly Level (F7)** — hands-off level flight holding heading and the velocity vector on the horizon, free-look panning around as the plane skims the water.

https://github.com/user-attachments/assets/00afefa5-f5c9-479c-9c92-4d6c2e1452b7

---

## What it does

- **Point-and-chase aiming.** Move the mouse to place a marker; the plane chases its nose onto
  it. It's a "turn once and arrive" follow-point, not a rate joystick — the marker stays put in
  the world until you move it, and the aircraft settles onto it.
- **Roll-then-pull turn law.** For anything but tiny corrections it banks first to put the lift
  vector on the target, *then* pulls — the efficient turn — instead of mushing across the wrong
  plane. No negative-G bunting.
- **Fine capture that actually centres.** A small integrator defeats the fly-by-wire's
  rate-command residual so the nose lands *on* the marker instead of parking a degree short.
- **Manual override — "my controls / your controls".** Touch the stick, keyboard, or rudder and the
  instructor hands you the plane: *any* input switches the whole instructor **off on every axis** so you
  fly it directly. What happens on release depends on what you were doing, WT-style:
  - **Aiming** (marker live under your mouse): your input is a *correction* — roll adjustments, a full
    elevator pull — and it **never moves your aim marker**. Let go and the instructor resumes flying
    toward the target you kept aimed (immediately by default; `ManualHandoffAimTime` adds a hands-off
    pause).
  - **Free-looking** (RMB / Free Look held): your input is *steering* — the marker is dragged onto the
    nose, and after release (`ManualHandoffTime`, 1 s after your last input) the instructor re-engages
    holding the new heading instead of pulling back to the old aim point.

  Set `ManualHandoffTime` to 0 for the classic **per-axis** blend instead (you own only the axis you
  touch; the mouse keeps aiming the rest).
- **Right-mouse free-look.** Hold RMB to freeze the reticle and look around (the plane keeps
  flying to the frozen point), then the view eases back when you let go.
- **Camera follow.** The cockpit view leans toward the marker; the 3rd-person orbit camera sits
  WT-style behind-and-above and tracks the aim, with pole-stable horizon leveling through loops.
- **Fly Level toggle (F7).** Locks the current heading and holds true level flight (velocity
  vector on the horizon, AoA-corrected) until you nudge the stick or toggle off.
- **Every airframe.** Fixed-wing, helicopters, and hover-VTOLs all fly off the same chase law
  (rotorcraft steer their cyclic + tail rotor; collective stays on your throttle). Helicopters can
  be opted out via the `ControlRotorcraft` setting if you prefer them on stock controls.
- **One-key master toggle (F10).** Flip the whole mod on/off in flight without opening the menu —
  a brief on-screen toast confirms the change.
- **Clean HUD by default.** Out of the box you see just the reticle, the airframe marker, and the
  Fly Level banner. The diagnostic readouts (status, live stick command, anomaly/phase) are hidden
  behind `ShowDebugHud` for tuning.
- **Live tuning (F1).** 50+ parameters — sensitivity, gains, the roll-then-pull behaviour,
  camera, HUD — all tunable in-game with sensible defaults out of the box.

## How it works (under the hood)

The mouse drives a **world-space aim direction** via raw Win32 mouse input (so big sweeps
aren't clamped by the screen edge), clamped to a cone around the nose. Each physics tick the
mod hooks `PilotPlayerState.PlayerAxisControls` and writes stick commands from a small control
law: a bank-angle servo proportional to heading error, a gated pitch pull (the "roll-then-pull"
coordination), rate damping to kill wobble, and a leaky integrator for the final few degrees.
Because Nuclear Option's FBW reads pitch/yaw as a commanded **angular rate**, the law is tuned
around that rather than fighting it. Camera patches on the cockpit and orbit camera states make
the view follow the same marker. It's deliberately a *thin* instructor — the game still owns the
flight model.

For the full picture, [**ARCHITECTURE.md**](ARCHITECTURE.md) has the system diagram: an at-a-glance
map of every subsystem and where the mod/game boundary sits, then zoom-ins on the frame timeline, the
aim rig, the control-law pipeline, the camera patches, and the telemetry loop.

---

## Install

### Option A — via NOMM (recommended, easiest)

[**NOMM** (Nuclear Option Mod Manager)](https://github.com/Combat787/NOMM) installs and updates
this mod for you, including BepInEx and the ConfigurationManager dependency.

1. Download and run [NOMM](https://github.com/Combat787/NOMM/releases/latest).
2. Point it at your Nuclear Option install if it doesn't auto-detect it.
3. Search for **WT Mouse Aim**, click install. Done — NOMM handles BepInEx and dependencies.

NOMM sources its mod list from the community [**NOMNOM**](https://github.com/KopterBuzz/NOMNOM)
registry, where this mod is listed.

### Option B — manual

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) (the **Mono x64** build) into
   your Nuclear Option folder and run the game once so it generates its folders.
2. Drop `NuclearOption-MouseAim.dll` (from the
   [latest release](https://github.com/cosistra/wt-mouse-aim/releases/latest)) into
   `<game>\BepInEx\plugins\WTMouseAim\`.
3. *(Recommended)* Install the BepInEx
   [**ConfigurationManager**](https://github.com/BepInEx/BepInEx.ConfigurationManager/releases)
   plugin to tune everything live with **F1**.

First launch writes the config to `<game>\BepInEx\config\com.no.wtmouseaim.cfg`.

---

## Controls

| Input | Action |
|---|---|
| **Mouse** | Move the aim marker — the plane flies its nose onto it |
| **Stick / keyboard / pedals** | Per-axis manual override (take any axis instantly; release to hand it back) |
| **Right Mouse (hold)** | Freeze the marker and free-look the camera (War Thunder style) |
| **F7** | Toggle **Fly Level** — hold wings-level, velocity vector on the horizon |
| **F10** | Toggle the whole mod **ON/OFF** (master switch) |
| **F1** | Open the live config (requires ConfigurationManager) |

## Tuning

Everything is live-tunable via F1, grouped into config sections: **Aim** (sensitivity,
smoothing, cone), **Control** (the instructor's gains and the roll-then-pull behaviour),
**Camera**, **FlyLevel**, and **HUD** (overlay, the `ShowDebugHud` readout toggle, and diagnostic
logging). Sensible defaults ship out of the box; the in-game descriptions explain each knob. If a
command ever misbehaves, the mod writes a single compact `[anomaly]` line to the BepInEx log —
handy for bug reports.

---

## Build from source

Requires the .NET SDK. Point `<GamePath>` in `NuclearOption-MouseAim.csproj` at your install
(the folder containing `NuclearOption.exe`, with BepInEx 5 installed into it):

```
dotnet build NuclearOption-MouseAim.csproj -c Release
```

Then copy `bin\Release\NuclearOption-MouseAim.dll` into `<game>\BepInEx\plugins\WTMouseAim\`.

Maintainers: [`release.ps1`](release.ps1) builds, tags, and publishes a GitHub release in one step.

## Requirements

- Nuclear Option
- BepInEx 5 (Mono x64)
- *(optional)* BepInEx.ConfigurationManager — for live F1 tuning

## License

MIT — see [LICENSE](LICENSE). Game code is **not** included or redistributed.

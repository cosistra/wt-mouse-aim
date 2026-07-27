# Architecture — WT Mouse Aim

<!-- ARCH-VERSION: 0.68.0 -->

The system diagram for this mod. **L0** is the at-a-glance map; **L1** sections zoom into each box.
Every box carries a stable node id (`aim_rig`, `chase_apply`, …) — the [Node index](#node-index) maps
each id to the file and type that implements it, and that table is what keeps the diagram honest.

> **Agents: this file is part of the code.** When you change structure, add a subsystem, move a stage
> in the `Apply` pipeline, or add/remove a Harmony patch, update the matching L1 diagram and the node
> index in the *same* change. See [Keeping this current](#keeping-this-current).

**Colour convention, used in every diagram on this page:**

| | meaning |
|---|---|
| 🟦 **blue** | **Mod** — code in this repo |
| 🟥 **red** | **Game** — Nuclear Option `Assembly-CSharp` (read-only; we patch or read it) |
| 🟨 **amber** | **Platform** — Unity, BepInEx, Harmony, Win32 |
| 🟩 **green** | **Artifacts & offline tools** — logs, CSVs, the Python analyser |

---

## L0 — System map

```mermaid
flowchart TB
    subgraph PLAT["🟨 PLATFORM"]
        direction LR
        unity["Unity engine<br/>Update · FixedUpdate · OnGUI"]
        bepin["BepInEx 5 (Mono x64)<br/>plugin host + config + log"]
        harmony["HarmonyX<br/>runtime patcher"]
        win32["Win32 user32.dll<br/>GetCursorPos / SetCursorPos"]
    end

    subgraph MOD["🟦 MOD — NuclearOptionMouseAim"]
        direction TB
        plugin["<b>plugin</b><br/>WTMouseAimPlugin<br/>lifecycle · hotkeys · HUD overlay"]
        cfg["<b>cfg</b><br/>Cfg<br/>~80 live-tunable binds"]
        aim_rig["<b>aim_rig</b><br/>AimRig<br/>world-locked aim marker<br/>+ cursor regime"]
        chase["<b>chase</b><br/>ChaseController<br/>the instructor: marker → stick"]
        seam["<b>seam</b><br/>PilotPlayerStatePatch<br/>own/skip the native stick"]
        campatch["<b>campatch</b><br/>Camera patches ×3<br/>view follows the marker"]
        telem["<b>telem</b><br/>ManeuverRecorder · AnomalyLog<br/>instrumentation sinks"]
    end

    subgraph GAME["🟥 GAME — Assembly-CSharp (read-only)"]
        direction TB
        pps["PilotPlayerState<br/>PlayerAxisControls"]
        aircraft["Aircraft<br/>GetInputs · FilterInputs · rb"]
        filters["ControlsFilter.FlyByWire<br/>HeloControlsFilter<br/>RelaxedStabilityController"]
        camstates["CameraCockpitState<br/>CameraOrbitState<br/>CameraStateManager"]
        misc["CursorManager · GameManager<br/>Rewired · DynamicMap"]
        phys["Rigidbody flight physics"]
    end

    subgraph OUT["🟩 ARTIFACTS & OFFLINE TOOLS"]
        direction LR
        log["LogOutput.log<br/>[anomaly] [maneuver] [seam]"]
        csv["mouseaim-rec-VER-RUN-NN-*.csv<br/>45-column capture (v0.65)"]
        pytool["debugtests/analyze-wobble.py<br/>--digest · scoring · --selftest<br/>debugtests/replay-pitcheff.py<br/>_pitchEff replay (v0.64) · --selftest"]
    end

    bepin --> plugin
    unity --> plugin
    harmony --> seam
    harmony --> campatch
    win32 <--> aim_rig

    plugin --> cfg
    plugin --> aim_rig
    cfg -.->|"reads"| aim_rig
    cfg -.->|"reads"| chase
    cfg -.->|"reads"| campatch

    aim_rig -->|"aim direction<br/>(world unit vector)"| chase
    aim_rig -->|"aim direction"| campatch
    aim_rig -->|"marker + boresight"| plugin

    pps -.->|"patched"| seam
    seam --> chase
    chase -->|"pitch / roll / yaw"| aircraft
    aircraft --> filters --> phys
    phys -->|"attitude · rates · velocity"| chase
    filters -.->|"probed via reflection"| chase

    camstates -.->|"patched"| campatch
    campatch --> camstates
    misc <--> aim_rig

    chase --> telem
    telem --> log
    telem --> csv
    csv --> pytool
    log --> pytool

    classDef mod fill:#1e3a8a,stroke:#60a5fa,color:#eff6ff
    classDef game fill:#7f1d1d,stroke:#f87171,color:#fef2f2
    classDef plat fill:#78350f,stroke:#fbbf24,color:#fffbeb
    classDef art fill:#14532d,stroke:#4ade80,color:#f0fdf4
    class plugin,cfg,aim_rig,chase,seam,campatch,telem mod
    class pps,aircraft,filters,camstates,misc,phys game
    class unity,bepin,harmony,win32 plat
    class log,csv,pytool art
```

**The one-sentence version:** the mouse moves a **world-locked point** in front of the aircraft
(`aim_rig`); an autopilot-like "instructor" (`chase`) computes the stick deflections that fly the
nose onto that point; a Harmony patch (`seam`) writes those deflections instead of the game's native
mouse-joystick input; the camera looks where the marker is (`campatch`); and everything the
instructor does is instrumented (`telem`) so misbehaviour is diagnosable offline rather than guessed at.

---

## L1.1 — Frame timeline (who runs when)

The single most load-bearing fact about this mod: **aiming and flying run on different clocks.**
The marker moves on the render frame (`Update`), the stick is written on the physics tick
(`FixedUpdate`), and the HUD draws in `OnGUI`.

```mermaid
flowchart TB
    subgraph U["🟨 Update — once per rendered frame"]
        direction TB
        u1["plugin.Update<br/>hotkeys: master toggle · fly-level · record"]
        u2["aim_rig.Update<br/>1. pick cursor regime<br/>2. read raw Win32 mouse delta<br/>3. rotate world aim vector<br/>4. clamp into MaxAimAngle cone"]
        u1 --> u2
    end

    subgraph F["🟨 FixedUpdate — physics tick"]
        direction TB
        f1["🟥 PilotPlayerState.PlayerAxisControls"]
        f2["🟦 seam PREFIX → chase.BeginFrame<br/>decide ownership; return false to SKIP native<br/>(cockpit only — orbit needs native's view axes)"]
        f3["🟥 native body — runs only if not skipped"]
        f4["🟦 seam POSTFIX → chase.Apply<br/>always runs; writes ci.pitch/roll/yaw"]
        f5["🟥 Aircraft.FilterInputs<br/>RelaxedStabilityController → FBW → surfaces"]
        f6["🟥 Rigidbody integration"]
        f1 --> f2 --> f3 --> f4 --> f5 --> f6
    end

    subgraph L["🟨 Camera state update"]
        l1["🟥 CameraCockpitState / CameraOrbitState .UpdateState"]
        l2["🟦 campatch prefix/postfix<br/>override view toward the marker"]
        l1 --> l2
    end

    subgraph G["🟨 OnGUI — IMGUI draw"]
        g1["plugin.OnGUI<br/>reticle · cone ring · boresight<br/>toasts · REC badge · G-LOC fade · debug HUD"]
    end

    U --> F --> L --> G
    F -.->|"attitude feeds back next tick"| F

    classDef mod fill:#1e3a8a,stroke:#60a5fa,color:#eff6ff
    classDef game fill:#7f1d1d,stroke:#f87171,color:#fef2f2
    class u1,u2,f2,f4,l2,g1 mod
    class f1,f3,f5,f6,l1 game
```

**Why the prefix is conditional.** Returning `false` from the prefix skips the native body — which is
what stops the game's own mouse virtual-joystick from fighting us for the stick. But that same native
body also processes *view* axes, so skipping it in third-person killed free-look. Hence:
**skip native only in the cockpit**; in orbit let it run and simply overwrite the flight controls in
the postfix. Harmony runs postfixes even when a prefix skipped the original, so `Apply` always lands.

---

## L1.2 — `aim_rig`: the world-locked marker

The marker is **not** a joystick. It is a direction in *world space*. You place it; the aircraft flies
its nose onto it; the offset eases to zero on arrival. Nothing in the rig ever snaps it back to the
nose — the only two things that move it are the mouse nudge and the cone clamp.

```mermaid
flowchart TB
    ctx{"context?<br/>enabled · local aircraft · alive"}
    ctx -->|no| rel["ReleaseCursor<br/>hand pointer back to the game<br/>+ resync CursorManager's private cache"]

    ctx -->|yes| regime["choose cursor regime"]
    regime --> r1["<b>aimCapture</b> — hidden + lockState=None<br/>we own the mouse; Win32 recentre each frame"]
    regime --> r2["<b>flyHidden</b> — hidden + Locked<br/>the game's own flying regime (free-look lives here)"]
    regime --> r3["<b>visible</b> — normal pointer<br/>menu / map / pause / UI flag"]

    r1 --> read["ReadMouseDelta (Win32)<br/>GetCursorPos − screen centre, then warp back<br/>×0.1 to match Unity's legacy axis scale"]
    read --> smooth["one-pole smoothing<br/>MouseSmoothing"]
    smooth --> frame{"camera mode?"}
    frame -->|cockpit| fa["frame = airframe up/right<br/>(view rolls with the plane)"]
    frame -->|orbit| fb["frame = HORIZON-LOCKED screen axes<br/>right = up × camFwd<br/>(re-derived, so camera roll can't feed back)"]
    fa --> rot["rotate the world aim vector<br/>yaw about upAxis · pitch about rightAxis"]
    fb --> rot
    rot --> clamp["cone clamp — ONLY if off > MaxAimAngle<br/>(guarded: RotateTowards snaps to the nose<br/>when near-parallel — that was the 'glued to centre' bug)"]
    clamp --> out(["<b>AimForward</b><br/>consumed by chase, campatch, HUD"])

    r2 --> look["orbit free-look: expose LookDelta<br/>so free-look feel == aim feel"]

    classDef mod fill:#1e3a8a,stroke:#60a5fa,color:#eff6ff
    class ctx,regime,r1,r2,r3,read,smooth,frame,fa,fb,rot,clamp,out,rel,look mod
```

**Why Win32 instead of Unity's mouse axis.** Unity's legacy `Mouse X/Y` is focus-gated — it stays dead
until the window receives a focus event (an alt-tab), so aiming was broken on a fresh launch. Reading
the OS cursor gives a true hardware delta from frame 1. The recentre-each-frame trick also means big
sweeps never clamp at the desktop edge. `lockState = None` during capture is essential: `Locked` would
let Unity warp the cursor too and zero our own delta.

**Writers of the aim vector** (there are only three, deliberately): the mouse nudge above; the cone
clamp; and `SetAimForward`, which the chase calls during manual flight so that releasing the stick
leaves the instructor holding the heading you ended on.

---

## L1.3 — `chase`: the instructor (the heart of the mod)

`ChaseController.Apply` is one pipeline per physics tick: measure → estimate regime → probe what the
airframe can do → pick a control law → condition the output → hand off to manual if the pilot is on
the stick → instrument.

```mermaid
flowchart TB
    subgraph MEAS["1 · MEASURE"]
        m1["aimDir = marker, or the latched<br/>heading when Fly Level is active<br/>(flown as TRUE level: pitched up by live AoA<br/>so the velocity vector, not the nose, sits level)"]
        m2["body-frame marker: local = InverseTransformDirection<br/>x=right y=up z=fwd · off = angle(nose, aim)"]
        m3["nose rates from Δforward/Δup per tick<br/>pitchRate · yawRate · rollRate · noseTurnDeg"]
        m1 --> m2 --> m3
    end

    subgraph EST["2 · ESTIMATE REGIME"]
        e1["bigTurn blend — continuous ramp<br/>fine direct-nudge ↔ bank-and-pull"]
        e2["azErr — world azimuth error<br/>+ vertical deprojection (v0.58)"]
        e3["heading-rate LPF (v0.51)<br/>nose-only, so lead can't fight a mouse flick"]
        e4["yaw-weakness estimate (v0.35)<br/>is the rudder actually CLOSING the error?"]
        e5["hover blend heliBlend (v0.43)<br/>fwd airspeed + AutoHover + tilt angle"]
        e1 --> e2 --> e3 --> e4 --> e5
    end

    subgraph PROBE["3 · PROBE THE AIRFRAME (reflection, all fail-soft)"]
        p1["<b>FBW probe</b> v0.55<br/>ControlsFilter.FlyByWire<br/>cornerSpeed · maxPitchAngularVel<br/>gLimit · alphaLimit"]
        p2["<b>canard probe</b> v0.57<br/>RelaxedStabilityController<br/>KR-67 Ifrit only"]
        p3["<b>helo probe</b> v0.58<br/>private heloFlyByWire + TiltWing /<br/>SwivelDuct / Compound archetypes"]
    end

    subgraph LAW["4 · CONTROL LAW (one law — EvolvedLegacy; Unified removed v0.65)"]
        l1["<b>ApplyEvolvedLegacy</b> ← the only law<br/>speed-aware bank target · slew limit<br/>v0.64: pErrTerm scaled by measured _pitchEff,<br/>reversal-gated floor v0.65 C1, latch-fixed v0.67<br/>(fixed-wing only — rotorcraft untouched)<br/>q + AoA demand schedules · helo rate normalisation<br/>(forced for all rotorcraft)<br/>v0.65 B2: sub-0.5° fine-settle micro-bank; v0.67:<br/>turn demand RAMPS in over [0.5°,2°] (no gate-exit step)<br/>v0.67: down-hemisphere roll-to-align suppressed →<br/>bounded pushover closes below-targets (no 90° hang)"]
    end

    subgraph COND["5 · CONDITION"]
        c1["anticipatory lead → brake-only clamp<br/>→ proportional floor → achievability cap<br/>(v0.67: cap also folds the LIVE alpha margin,<br/>not just gLimit — turn demand ≤ what the wing<br/>can pull at this AoA)"]
        c2["<b>AoA envelope</b> — ceiling gates (v0.55)<br/>predictive lead; cut only the command<br/>driving AoA OUTWARD"]
        c2b["<b>AoA-utilization demand schedule</b> (v0.59)<br/>live AoA vs this airframe's PROBED ceiling,<br/>folded into qSched. Fast-attack / slow-release<br/>so demand can't snap hot again as AoA falls<br/>back through the ceiling — the loaded-jet fix"]
        c2c["<b>AoA recovery bias</b> (v0.59, damped v0.62)<br/>restoring pitch ∝ predicted excess past either<br/>ceiling. Continuous + symmetric, exactly zero<br/>inside the envelope — the gates only CUT,<br/>so past the ceiling nothing flew it back in.<br/>v0.62: uses the TWO-SIDED predicted AoA<br/>(AoA + rate·lead), NOT the gates' one-sided<br/>preds — the lead is its damping term, so it<br/>fades as recovery develops instead of holding<br/>to the crossing (that was the +43→-47 bang-bang)"]
        c2d["fine-capture boost, gated by that schedule<br/>(NOT by speed — a slow LIGHT jet keeps its feel;<br/>only genuine near-ceiling AoA softens it)"]
        c3["fine leaky integrator (v0.24)<br/>FBW is a RATE law, so P alone parks short"]
        c4["invert canard remap (v0.57)<br/>so the game delivers the pitch we meant"]
        c5["output slew limit"]
        c1 --> c2 --> c2b --> c2c --> c2d --> c3 --> c4 --> c5
    end

    subgraph MAN["6 · MANUAL HANDOFF"]
        h1{"ManualHandoffTime > 0 ?"}
        h1 -->|yes| h2["<b>global hard handoff</b> v0.49<br/>any axis past deadzone → instructor fully OFF<br/>on all axes; re-engages after a hold timer<br/>(shorter while aiming, longer while free-looking)"]
        h1 -->|no| h3["<b>per-axis blend</b> v0.05–v0.48<br/>touch an axis → own that axis instantly<br/>ease back over ManualReturnTime"]
        h2 --> h4["while manual: SetAimForward drags the<br/>marker onto the nose, so release holds<br/>the heading you ended on"]
    end

    subgraph WRITE["7 · WRITE + INSTRUMENT"]
        w1["ci.pitch / ci.roll / ci.yaw<br/>(nose-up = NEGATIVE pitch)"]
        w2["ClassifyPhase → LEVEL/FINE/ALIGN/PULL/TURN/HOLD"]
        w3["DetectAnomalies — 8 detectors<br/>+ 64-frame ring buffer trail dump"]
        w4["TrackManeuver — one summary per turn"]
        w5["ManeuverRecorder.Sample"]
        w1 --> w2 --> w3 --> w4 --> w5
    end

    MEAS --> EST --> PROBE --> LAW --> COND --> MAN --> WRITE

    classDef mod fill:#1e3a8a,stroke:#60a5fa,color:#eff6ff
    class m1,m2,m3,e1,e2,e3,e4,e5,p1,p2,p3,l1,c1,c2,c2b,c2c,c2d,c3,c4,c5,h1,h2,h3,h4,w1,w2,w3,w4,w5 mod
```

### Why the probes exist

The mod does not model the aircraft — it **asks the game** what the aircraft can do, then commands only
that. The game's FBW reads pitch/yaw as a commanded **angular rate**, and that rate authority collapses
below corner speed; commanding more than the airframe can deliver is what produced the low-speed
oscillation and windup. Three airframe families need three different questions:

| probe | reads | fixes |
|---|---|---|
| **FBW** (v0.55) | `ControlsFilter.FlyByWire` public params + private `gLimit`/`alphaLimit` trio via `AccessTools` field refs | achievability cap + AoA ceiling |
| **canard** (v0.57) | `RelaxedStabilityController.canardRange` | KR-67 Ifrit's ~5.3 Hz fine-aim limit cycle — the game *replaces* pitch with a quadratic, locally-reversed remap before the FBW sees it, so the mod pre-inverts it |
| **helo** (v0.58) | private nested `heloFlyByWire` via `Traverse` + tilt/nozzle archetype components | rotorcraft rate normalisation; tilt angle drives the hover regime |

**Every probe fails soft.** A missing component, a disabled FBW, or a renamed field after a game update
leaves the probe not-ok and the previous version's behaviour exactly intact. That contract is
non-negotiable for new probes — reflection against game internals is the one part of this mod that a
patch can silently break.

**The FBW rate pair also feeds a measured signal (v0.60).** Beyond the static probe params, `Apply`
reads the FBW's *live* commanded-vs-achieved pitch rate (`GetTargetPitchAngVel` / `GetPitchAngVel` —
the same pair the recorder logs) and low-passes the **signed** ratio into `_pitchEff`, the pitch twin
of `_yawWeak`: fast-attack / slow-release so load, mush, density and damage all show up generically as
achieved &lt; commanded. **v0.64: the ratio is signed, not magnitude-only.** Through v0.63 both sides
were `Abs`'d, which blinded it to a REVERSED plant — the FS-12 departure case, where the airframe
pitches opposite the command at ~3x magnitude and the old estimator read a confident `1.00`. `Clamp01`
now floors a reversed plant to `0`. The noise gate stays on magnitude (gating on the signed command
would skip every nose-down frame). `ApplyEvolvedLegacy` (the only law) consumes it — it scales
`pErrTerm` by an `effFloor`-floored factor (fixed-wing only; the estimator is not measured for
`_collective`, so rotorcraft are untouched). **v0.65 C1: the floor is reversal-gated** — below a
`revThresh` (0.15) the floor is dropped and demand collapses to the measured near-0 value, so on a
reversed plant (`_pitchEff ≈ 0`) the law stops forcing 30% pitch into a plant moving the opposite way
(the sustained rec04/rec05 relay). Healthy and genuine low-q mush cases are byte-identical to v0.64.
**v0.67: the C1 floor could latch.** A gated-out command (`|cmd| < 0.05`, e.g. after C1 collapsed the
pitch to ~0) carries no information, and pure-holding `_pitchEff` there pinned a *transient* low-q mush
at ~0 forever — C1 kept pitch ×0, so the stick stayed ~0, so `cmd` stayed dead and it could never
re-measure (rec14 froze the pull ~4 s at railed bank). The dead-command branch now floors the estimate
at `Max(_pitchEff, revThresh)`: a latched-low estimate rises toward ~15% (the slow release tau) so the
pitch re-establishes a self-probe → `cmd > 0.05` → re-measure; a healthy estimate is untouched, so a
brief neutral stick never drags a good jet down. The v0.59 AoA recovery bias is scaled by it too,
unfloored — a reversed plant cannot be recovered by commanding more, and the traces show that term
sustaining the limit cycle. Fail-soft: holds its last value on any read miss (probe unavailable / manual
/ read error — distinct from the dead-command self-probe above, which only fires with a live FBW).

### The generality rule (the reason the probes carry so much weight)

**No per-airframe constants, ever.** Every gain, schedule and gate must key off either (a) a parameter
*probed from the game's own components* for the airframe being flown, or (b) *live physical state* —
dynamic pressure, AoA, measured rates and effectiveness. Loadout and mass are never constants; they
show up as achieved-vs-commanded discrepancy and must be handled that way.

A fix that only works because a tuned constant happens to suit one plane is **wrong even if it closes
the report**. Before shipping a control-law change, check it against a light jet at high q, a loaded
jet mushing near its alpha limit above corner speed, a low-limit STOL trainer, and a hovering helo.

This is what the v0.59 demand schedule is an instance of: `qSched` keyed off dynamic pressure *alone*,
so a loaded airframe needing high AoA to make its commanded G read as high-q while the plant was
actually mushing — gain stayed hot, the nose departed, and the gate plus damping relay-cycled it.
The fix folds *live* AoA against the *probed* ceiling into the same schedule, so it is airframe-
agnostic by construction. `GENERALITY-REVIEW.md` is the standing audit of the law against this rule.

---

## L1.4 — `campatch`: camera follow

```mermaid
flowchart TB
    subgraph CK["CockpitCameraPatch — postfix on CameraCockpitState.UpdateState"]
        k1{"stand down?<br/>TrackIR · free-look · no context"}
        k1 -->|yes| k2["leave native alone"]
        k1 -->|no| k3["marker → pan/tilt targets<br/>seed from live angles on rising edge<br/>smooth → set localRotation"]
    end

    subgraph OR["CameraOrbitPatch — prefix + postfix on CameraOrbitState.UpdateState"]
        o1["<b>PREFIX</b> — write native panView/tiltView<br/>keeps the native pivot roughly on the aim,<br/>so free-look starts from the current view"]
        o2["<b>POSTFIX</b> — override the FINAL pose<br/>position rigidly from the live plane pos<br/>(smoothing POSITION lags then jumps at speed)<br/>only DIRECTION is slerped · pole guard<br/>native terrain linecast replicated"]
        o3["free-look: horizon-locked yaw/pitch from<br/>AimRig.LookDelta · timed ease-back on release"]
        o1 --> o2 --> o3
    end

    subgraph SW["CameraSwitchStatePatch — CameraStateManager.SwitchState"]
        s1["observe mode transitions<br/>so the rig re-seeds cleanly"]
    end

    classDef mod fill:#1e3a8a,stroke:#60a5fa,color:#eff6ff
    class k1,k2,k3,o1,o2,o3,s1 mod
```

**The v0.22 lesson encoded here:** steering the orbit camera purely through native `panView`/`tiltView`
left the view off the aim by up to ~25–33° of elevation-dependent error, because native's tilt orbits
the camera through an arm that already carries a built-in downward angle and *then* re-aims at the
plane. Not fixable with a better tilt formula. So the work is split: prefix keeps the native pivot
approximately right (for free-look hand-off), postfix overrides the final pose outright.

---

## L1.5 — `telem`: the instrumentation loop

Diagnostics here are **instrument-first** — the mod reports what it did rather than leaving you to
guess. This is the loop that turns "it felt wrong" into a number.

```mermaid
flowchart LR
    chase["🟦 chase.Apply"]

    chase -->|"event only,<br/>per-type cooldown"| an["🟦 AnomalyLog<br/>overshoot · over-roll · hunt · yaw-wag<br/>roll-wobble · az-limit-cycle · persistent-miss · overstress"]
    chase -->|"one line per<br/>completed turn"| mv["🟦 [maneuver] summary"]
    chase -->|"hotkey-armed,<br/>rate-limited"| rec["🟦 ManeuverRecorder<br/>45-column CSV, self-describing<br/># header: gains · law · airframe · FBW params · run/rec index<br/>v0.63: + tgtPRaw/aoaGU/aoaGD/aoaRec/qSched/pEff<br/>(the pitch decision variables)<br/>v0.65: + settleOn (B2 fine-settle gate engaged)"]
    chase -->|"live"| hud["🟦 debug HUD + anomaly flash"]

    an --> logf["🟩 LogOutput.log<br/>+ mouseaim-anomalies-VER-RUN-SESSION.log"]
    mv --> logf
    rec --> csvf["🟩 mouseaim-rec-SESSION.csv"]

    logf --> tool["🟩 analyze-wobble.py"]
    csvf --> tool
    tool --> d1["<b>--digest</b> — ~30-line phase-segmented timeline<br/>ALWAYS run this first; raw CSV to an LLM<br/>is expensive and mostly steady-state redundancy"]
    tool --> d2["default — death-wobble scoring<br/>episodes · frequency · amplitude · roll-rail %"]
    tool --> d3["--selftest"]

    sess(["session id — yyyyMMdd-HHmmss<br/>the human join key across all three artifacts<br/>(Time.time is the per-row numeric join key)"])
    sess -.-> logf
    sess -.-> csvf

    classDef mod fill:#1e3a8a,stroke:#60a5fa,color:#eff6ff
    classDef art fill:#14532d,stroke:#4ade80,color:#f0fdf4
    class chase,an,mv,rec,hud mod
    class logf,csvf,tool,d1,d2,d3,sess art
```

---

## L1.6 — `cfg`: configuration & live tuning

```mermaid
flowchart LR
    file["🟨 BepInEx/config/com.no.wtmouseaim.cfg"] <--> cfg["🟦 Cfg — ConfigEntry binds"]
    f1["🟨 ConfigurationManager (F1)"] <--> cfg
    cfg -->|"SettingChanged hook"| logline["🟩 config logged at startup<br/>+ on every live edit"]
    cfg -->|"NoteConfigChange"| csvcomment["🟩 '# cfg' comment row<br/>inside the active recording"]

    cfg --> sec["<b>sections</b><br/>General · HUD · Aim · Control · Camera<br/>Recorder · FlyLevel · ZZZ-Panic Button"]

    classDef mod fill:#1e3a8a,stroke:#60a5fa,color:#eff6ff
    classDef plat fill:#78350f,stroke:#fbbf24,color:#fffbeb
    classDef art fill:#14532d,stroke:#4ade80,color:#f0fdf4
    class cfg,sec mod
    class file,f1 plat
    class logline,csvcomment art
```

The tuning loop this enables: **fly → F1 → change a gain → feel it immediately → write the good value
back into `Cfg.cs` defaults.** A config change made mid-recording is stamped into the CSV, so a
recording always explains what it was flown with.

---

## Node index

The contract between the diagrams and the code. **Every `.cs` file and every top-level type in the
repo must appear here.** `debugtests/check-architecture.py` enforces exactly that.

| node id | implemented by | type(s) | role |
|---|---|---|---|
| `plugin` | `WTMouseAimPlugin.cs` | `WTMouseAimPlugin` | BepInEx entry point, hotkeys, IMGUI overlay, `PluginVersion` SoT, session id |
| `cfg` | `Cfg.cs` | `Cfg`, `ConfigurationManagerAttributes` | every config bind + F1 metadata |
| `aim_rig` | `AimRig.cs` | `AimRig`, `Guards` | world-locked marker, Win32 raw mouse, cursor regimes; `Guards` = "should the mod be passive" |
| `chase` | `ChaseController.cs` | `ChaseController` | the instructor: measure → estimate → probe → law → condition → handoff → write |
| `seam` | `ChaseController.cs` | `PilotPlayerStatePatch` | Harmony prefix/postfix on `PilotPlayerState.PlayerAxisControls` |
| `campatch` | `CameraPatches.cs` | `CockpitCameraPatch`, `CameraOrbitPatch`, `CameraSwitchStatePatch` | view follows the marker in cockpit + orbit |
| `telem` | `Recording.cs` | `ManeuverRecorder`, `AnomalyLog` | CSV capture + event-only anomaly sink |

### Game types we patch or read

| game type | how | where |
|---|---|---|
| `PilotPlayerState.PlayerAxisControls` | **patched** (prefix + postfix) | `seam` |
| `CameraCockpitState.UpdateState` | **patched** (postfix) | `campatch` |
| `CameraOrbitState.UpdateState` | **patched** (prefix + postfix) | `campatch` |
| `CameraStateManager.SwitchState` | **patched** (prefix + postfix) | `campatch` |
| `ControlsFilter.FlyByWire` | read via `AccessTools` field refs | `chase` FBW probe |
| `HeloControlsFilter.heloFlyByWire` | read via `Traverse` (private nested) | `chase` helo probe |
| `RelaxedStabilityController` | read via `AccessTools` field refs | `chase` canard probe |
| `TiltWingController`, `SwivelDuctSystem`, `CompoundHeloController` | `GetComponentInChildren` | `chase` archetype fingerprint |
| `CursorManager.visible` | read **and written** via reflection | `aim_rig` cache resync |
| `Aircraft`, `GameManager`, `CameraStateManager`, `Rewired`, `DynamicMap`, `RadialMenuMain`, `NuclearOption.UI.LeaderboardMenu`, `TargetCalc`, `PlayerSettings` | public API | various |

---

## Sign conventions

Verify against the decompiled source before changing any of these.

| quantity | convention |
|---|---|
| `local` | `InverseTransformDirection(aimDir)` — x = right, y = up, z = forward |
| `ci.pitch` | **nose-up = NEGATIVE** |
| `ci.roll` | positive = roll right |
| `ci.yaw` | positive = yaw right |
| `azErr` | positive = marker right of heading |
| `t.right.y` | negative = right wing down |
| FBW pitch/yaw | a commanded **angular rate**, not a deflection |

---

## Keeping this current

This diagram rots the moment code moves, and a stale map causes wrong-file edits. Three mechanisms,
weakest to strongest:

**1. The rule (in `CLAUDE.md`).** Structural changes update `ARCHITECTURE.md` in the *same* change —
same standing rule as `CLAUDE.md` itself. "Structural" means: a file added/removed/renamed; a
top-level type added/removed; a Harmony patch added/removed/retargeted; a stage added, removed, or
reordered in the `Apply` pipeline; a new game type read by reflection; a new artifact or offline tool.

**2. The checker (runs in seconds).**

```
python debugtests/check-architecture.py          # verify
python debugtests/check-architecture.py --fix-version   # sync the version stamp only
```

It fails if a `.cs` file or top-level type is missing from the node index, if the index names a type
that no longer exists, if a `[HarmonyPatch]` target isn't listed in the game-types table, or if the
`ARCH-VERSION` stamp has drifted from `PluginVersion`. It is stdlib-only and needs no game install —
same contract as the other tools in `debugtests/`.

**3. The gates.** Two, so this doesn't rely on anyone remembering:
- a **Stop hook** in the committed `.claude/settings.json` runs the checker when an agent finishes a
  turn and feeds any drift back to it (exit 2) before it can hand back — silent when clean, and
  end-of-turn rather than per-edit so a multi-step refactor isn't nagged mid-flight;
- **`release.ps1`** runs the checker before it builds, so a drifted diagram can't ship.

**When the checker passes but the diagram is still wrong** — a reordered pipeline stage, a changed
signal name, a law that now does something different — the checker cannot see that. Re-read the L1
section you touched. The node index is mechanically enforceable; the prose and the arrows are not.

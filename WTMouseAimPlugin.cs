using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NuclearOptionMouseAim
{
    // War Thunder–style mouse aim for Nuclear Option.
    //
    // BUILD STEP 4 (this file): point-and-chase flight. The aim marker is a WORLD-LOCKED desired
    // direction (not an airframe-relative offset), accumulated from the mouse and clamped to a cone
    // around the nose. A thin "instructor" (brihernandez MouseFlight law, spec §3.1/3.2) rolls and
    // pulls the plane to fly its nose ONTO that world vector — and because the marker is world-locked,
    // the nose converges and the marker slides back to the boresight once we arrive. The game's own
    // FlyByWire/AutoTrimmer keep it inside the envelope (we leave flightAssist on). The cockpit camera
    // also smoothly looks toward the marker. See warthunder_mouseaim_spec.md.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class WTMouseAimPlugin : BaseUnityPlugin
    {
        public const string PluginGuid    = "com.no.wtmouseaim";
        public const string PluginName    = "WT Mouse Aim";
        public const string PluginVersion = "0.54.0";

        internal static ManualLogSource Log;

        // Session id (v0.44): one short wallclock-derived id per game session, stamped into the startup
        // log line, every recording CSV header and the anomaly file header — the human-visible join key
        // that ties the three artifacts together (Time.time stays the per-row numeric join key). Lazy so
        // it's stable from first access regardless of which artifact opens first.
        private static string _sessionId;
        internal static string SessionId
        {
            get { if (_sessionId == null) _sessionId = System.DateTime.Now.ToString("yyyyMMdd-HHmmss"); return _sessionId; }
        }

        // 1x1 white texture for drawing dots/lines in OnGUI (IMGUI has no primitive line draw).
        private static Texture2D _px;

        private void Awake()
        {
            Log = Logger;
            Cfg.Bind(base.Config);

            _px = new Texture2D(1, 1);
            _px.SetPixel(0, 0, Color.white);
            _px.Apply();

            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(PilotPlayerStatePatch));
            harmony.PatchAll(typeof(CockpitCameraPatch));
            harmony.PatchAll(typeof(CameraOrbitPatch));
            harmony.PatchAll(typeof(CameraSwitchStatePatch));
            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded (world follow-point chase w/ body-frame roll-then-pull law [roll the lift vector onto the target, then pull up into it] + signed/clamped pull gate (no bunt) + yaw ease-down on big turns + pitch anti-overshoot brake + bank-servo azimuth deadband (anti fine-cone roll wobble) + roll-rate-smoothed damping (anti high-speed roll-PIO limit cycle) + fine integrator + per-axis manual override (anomaly logging suspended while you're on the stick) + Win32 raw mouse + 3rd-person orbit-camera override w/ hysteretic pole-stable horizon leveling + RMB free-look that keeps our orbit pivot (no snap) and eases the view back to your flight direction on release + AoA-true Fly Level toggle [{Cfg.FlyLevelKey.Value}] + phase/maneuver instrumentation + anomaly logging + all-aircraft control (fixed-wing + rotorcraft/VTOL, opt-out via ControlRotorcraft) + master ON/OFF hotkey [{Cfg.ToggleKey.Value}] + clean reticle-only HUD by default (debug readouts behind ShowDebugHud) + adjustable 3rd-person camera position (distance/height/side offsets) + 'I broke it, fix it please' reset-to-defaults button + measured-reactive LOADED-turn assist [shifts a weak-rudder side correction into a steep bank + real G-pull and fades the dead rudder out; the bank target is sized from a turn-RATE so it self-scales with airspeed (a small high-speed nudge commands the steep loaded bank that actually slews the nose), self-adapting per airframe/speed, no tuning needed] + maneuver recorder hotkey [{Cfg.RecordKey.Value}] -> timestamped CSV for tuning across aircraft (now tagged with the active control law per row) + A/B control-law toggle [{Cfg.ControlLawKey.Value}] switching Legacy<->EvolvedLegacy in flight (v0.42: EvolvedLegacy is now the DEFAULT/graduated law = universal atan(ωV/g) bank at all speeds/regimes + final-leg align-hold so the roll-to-align contribution persists until the target is genuinely close laterally, preventing the early wings-level-and-stop-short that Legacy shows in the final few degrees; v0.42 also lowered AssistTurnRateGain 1.5->0.9 to kill the high-speed eager/back-and-forth bank limit cycle. Legacy kept as the pristine A/B reference; BankToTurn abandoned and hidden from the toggle) + regime-aware hover handling (v0.43: on collective aircraft [helis/hover-VTOLs, takeoffDistance==0] EvolvedLegacy ramps from bank-to-turn to yaw-to-point as forward speed drops between HeliForwardSpeed and HeliHoverSpeed [or whenever the game's AutoHover is engaged] — bank is suppressed [wings level] and yaw authority raised by HeliYawScale so the tail rotor points the nose; fixed-wing unchanged) + self-describing recordings & a dedicated anomaly file (v0.44) + widened hover regime band (v0.45: HeliHoverSpeed 20->40, HeliForwardSpeed 60->100 so yaw-to-point stays engaged to higher forward speeds before reverting to bank-to-turn) + true wings-level hover (v0.46: gate the roll-to-align branch by heliBlend too — previously only the bank target was zeroed, so in hover the heli still banked toward the marker [roll] while yaw also swung the nose; now at full hover roll is a pure leveler and yaw alone points the nose) + wider hover-regime defaults/ranges (v0.47: HeliForwardSpeed default 150 so yaw-to-point covers the whole normal heli envelope; HeliForwardSpeed range now up to 300 and HeliHoverSpeed up to 150) + cursor-handoff fix (v0.48: toggling the mod OFF mid-flight no longer pops the Windows pointer over the game — ReleaseCursor now restores the game's true hidden+locked flight cursor [visible only for a real menu/UI flag] and resyncs CursorManager's private visibility cache so its Refresh() can't no-op against a stale value; new CursorLogging instrument emits a [cursor] line on each regime change) + manual input re-seeds the flight direction (v0.48: while you actively hold the stick/keyboard/pedals the aim marker is dragged onto the nose so the instructor stops pulling you back toward the old aim point [the RMB free-look + manual-stick complaint]; release and it holds the heading you ended on. Toggle via ManualReorients) + global hard handoff 'my controls / your controls' (v0.49: ManualHandoffTime>0 [default 1s] makes ANY manual input fully disable the instructor on ALL axes — you fly the plane directly — until that many seconds after your last input, then it re-engages flying straight ahead on the heading you ended on; the fix for 'the instructor still carries some control while I fly manually'. Set 0 for the old per-axis blend) + aim-preserving manual corrections (v0.50: manual input only re-seeds the marker while FREE-LOOKING [RMB/Free Look held]; while aiming, the marker stays on your mouse target — you roll-correct or pull, release, and the instructor resumes toward what you were aiming at, WT-style, after ManualHandoffAimTime [default 0 = immediately]) + anticipatory turn lead — THE death-wobble fix (v0.51: 8 user recordings showed the achieved bank lags the turn-rate bank command by a constant ~0.7s while that command was pure-proportional in the heading error, self-sustaining a 0.3-0.85Hz bank limit cycle at ALL speeds [±88° bank from ±6° aim error, roll railed]; the bank target is now computed from the PREDICTED error azErr - noseHeadingRate*TurnLeadTime [default 0.65s] so it rolls out early instead of after the overshoot; plus AssistOffPitchScale [default 0.5] cuts the instructor's pitch while the game's flight-assist is OFF, where the FBW's stick->rate gain doubles-triples and pitch diverged to AoA ±50°; new CSV columns headingRateFilt/azErrPred) + brake-only lead (v0.52: the v0.51 lead closed its own fast loop — near boresight the prediction was dominated by the heading-rate term, and the speed-scaled bank slope turned ±2°/s of rate ripple into a ±65° bank relay = a NEW 1.1-1.35Hz chatter in HOLD phase [confirmed in 16 v0.51 recordings at 280-510 m/s]; azErrPred is now clamped to [0, azErr] so the lead can roll out early but never command bank past the real error — big turns unchanged, the chatter loop can't self-sustain) + align-hold fine-cone deadzone (v0.53: with the bank pipeline quieted by v0.52, the KR67-class 570+ m/s recordings exposed a SECOND raw-error az→roll path — eAlign=phi/90 is a full-scale directional relay near boresight [phi snaps ±90° with the sign of a sub-degree error] and the align-hold blend weight |azErr|/EvolvedAlignHoldDeg fed it undeadzoned, rocking the wings ±30° at ~1.2Hz while targetBank sat at 0; the weight now subtracts FineBankDeadzone first, same guard the linear bank servo always had, so inside the fine cone roll is purely wings-level + the braked/clamped bank target) + de-rectified lead & slew-limited bank target (v0.54: 19 v0.53 recordings [KR67 450-536 m/s, AB4 Alcyon 226-518 m/s] showed the v0.52 brake-clamp RECTIFYING heading-rate ripple into a 0<->azErr sawtooth on the prediction — azErrPred pinned to exactly 0 while 1.5-5.7° of real error remained, commanding full wings-level mid-correction [the 'self-leveling fights the turn' feel], and the ~44°-bank-per-degree atan slope at 500 m/s turned that sawtooth into a bank target banging 0<->65° at ~1.5Hz that the roll servo faithfully chased [wings rocking ±14-30° from a 1-3° error]; three fixes in the same pipeline: the prediction now floors at 0.30*azErr so level flight is never commanded while error remains [floor self-releases as azErr->0], the heading-rate filter tau rose 0.18->0.35 to cut the ripple feeding the rectifier, and the bank target is slew-limited by new knob BankSlewRate [default 60°/s, 0=off] so it physically can't flap above the airframe's roll response; new CSV column tBankE records the bank target EvolvedLegacy actually flies [the targetBank column is the shared blend it does NOT fly]) — tune live via F1).");
            Logger.LogInfo($"[session] {SessionId} — recordings, the anomaly file and this log share this id for cross-referencing.");
        }

        // Master ON/OFF toast (v0.33): briefly surfaced when the toggle hotkey flips the mod, so the
        // change is confirmed on-screen even while the mod (and its overlay) are off.
        private static float _toastUntil = -999f;
        private static bool  _toastOn;

        private void Update()
        {
            // Master enable/disable hotkey (v0.33). Ungated by Enabled so it can always toggle back ON.
            // Setting .Value persists to the config file and logs via the SettingChanged hook.
            if (Input.GetKeyDown(Cfg.ToggleKey.Value))
            {
                Cfg.Enabled.Value = !Cfg.Enabled.Value;
                _toastUntil = Time.time + 2f;
                _toastOn = Cfg.Enabled.Value;
            }

            AimRig.Update();

            // Fly Level toggle (v0.24). Edge-triggered; needs an aircraft to latch the heading onto.
            if (Cfg.Enabled.Value && Cfg.FlyLevelEnabled.Value &&
                Input.GetKeyDown(Cfg.FlyLevelKey.Value) &&
                AimRig.TryGetContext(out var ac, out _) && !ac.disabled)
            {
                ChaseController.ToggleFlyLevel(ac);
            }

            // Maneuver recorder toggle (v0.35). Ungated by aircraft/Enabled so it can always be stopped;
            // it only writes rows while the chase is actually flying (Sample is called from Apply).
            if (Input.GetKeyDown(Cfg.RecordKey.Value))
            {
                bool on = ManeuverRecorder.Toggle();
                _toastUntil = Time.time + 2f;
                _toastOn = on; // reuse the toast: cyan "REC" on, amber off (label switched in OnGUI)
                _toastRec = true;
                _toastLaw = false;
            }
            // Control-law cycle (v0.38; v0.42 made it a 2-way toggle). Ungated like the other hotkeys so it
            // always works in flight; toggles Legacy <-> EvolvedLegacy, persists ControlLawMode (logged via
            // the SettingChanged hook), and reuses the toast — labelled with the law name in OnGUI. BankToTurn
            // is abandoned (not working) and excluded from the cycle; if ControlLawMode somehow holds it (a
            // stale cfg value), the toggle rescues out to Legacy. ApplyBankToTurn is retained but unreachable.
            else if (Input.GetKeyDown(Cfg.ControlLawKey.Value))
            {
                var next = Cfg.ControlLawMode.Value == ControlLawMode.Legacy
                    ? ControlLawMode.EvolvedLegacy : ControlLawMode.Legacy;
                Cfg.ControlLawMode.Value = next;
                _toastUntil = Time.time + 2f;
                _toastLaw = true;
                _toastRec = false;
                Log.LogInfo($"[controllaw] switched to {next}.");
            }
            else if (Time.time >= _toastUntil) { _toastRec = false; _toastLaw = false; }
        }

        // True while the active toast is a recorder toast (so OnGUI labels it REC/REC OFF, not ON/OFF).
        private static bool _toastRec;
        // True while the active toast is a control-law toast (so OnGUI names the law instead of ON/OFF).
        private static bool _toastLaw;

        private void OnGUI()
        {
            // Master-toggle toast — drawn BEFORE the overlay/enabled guard so it confirms an OFF flip too.
            if (Time.time < _toastUntil)
            {
                var tc = GUI.color;
                // Law toast is always cyan (informational); REC/master toasts stay cyan-on / amber-off.
                GUI.color = (_toastLaw || _toastOn) ? new Color(0.3f, 0.9f, 1f, 0.95f) : new Color(1f, 0.7f, 0.3f, 0.95f);
                const float tw = 240f;
                string msg = _toastLaw ? $"CONTROL LAW  {Cfg.ControlLawMode.Value}"
                           : _toastRec ? (_toastOn ? "MouseAim  REC START" : "MouseAim  REC STOP")
                                       : (_toastOn ? "WT MouseAim  ON"      : "WT MouseAim  OFF");
                GUI.Label(new Rect((Screen.width - tw) * 0.5f, Screen.height * 0.12f, tw, 24f), msg);
                GUI.color = tc;
            }

            // Persistent recording indicator — drawn BEFORE every gate (even on the clean HUD / mod-off)
            // so a running capture is always visible. Top-centre, red, with elapsed time + sample count.
            if (ManeuverRecorder.IsRecording)
            {
                var rc = GUI.color;
                GUI.color = new Color(1f, 0.25f, 0.2f, 0.95f);
                const float rw = 220f;
                GUI.Label(new Rect((Screen.width - rw) * 0.5f, Screen.height * 0.08f, rw, 24f),
                    $"● REC  {ManeuverRecorder.Elapsed:0.0}s  ({ManeuverRecorder.Samples})");
                GUI.color = rc;
            }

            if (!Cfg.ShowOverlay.Value || !Cfg.Enabled.Value)
                return;
            if (!AimRig.TryGetContext(out var ac, out var cam))
                return;
            if (ac.disabled) // plane destroyed/disabled — nothing to aim, so draw nothing
                return;

            Transform t = ac.transform;
            float dist = Cfg.AimDistance.Value;
            float off = Vector3.Angle(t.forward, AimRig.AimForward); // deg the nose is off the marker
            bool inOrbit = CameraStateManager.cameraMode == CameraMode.orbit;

            // Boresight = where the nose points; aim = the world-locked marker direction.
            bool boreVis = WorldToGui(cam, t.position + t.forward * dist, out var boreScreen);
            bool aimVis  = WorldToGui(cam, AimRig.MouseAimPos(ac),        out var aimScreen);

            // Cone ring: 36 points on the MaxAimAngle cone around the nose, projected to screen.
            // Hidden when the cone is wide (unlimited aim) — the ring would be behind you and useless.
            Color ring = new Color(1f, 1f, 1f, 0.35f);
            float half = Cfg.MaxAimAngle.Value;
            if (half <= 89f)
            {
                for (int a = 0; a < 36; a++)
                {
                    Vector3 dir = Quaternion.AngleAxis(a * 10f, t.forward) * (Quaternion.AngleAxis(half, t.right) * t.forward);
                    if (WorldToGui(cam, t.position + dir * dist, out var p))
                        Dot(p, 2f, ring);
                }
            }

            // Boresight cross (where the nose actually points). In cockpit we hide it once the nose is on
            // the marker (declutter — it sits under the reticle anyway). In 3rd-person the airframe is
            // off-centre from the reticle, so the cross is genuinely useful: always show it there.
            if (boreVis && (inOrbit || off >= 5f))
                Cross(boreScreen, 10f, 2f, new Color(0.6f, 0.9f, 1f, 0.9f)); // boresight: pale blue +
            if (aimVis)
                CircleOutline(aimScreen, 13f, new Color(1f, 0.95f, 0.4f, 0.55f)); // aim marker: faint yellow ring

            // Tiny text readout (top-left) so feel/cone clamping is verifiable. DEBUG-only (v0.33):
            // hidden by default so installers get a clean reticle-only HUD; ShowDebugHud reveals it.
            var prev = GUI.color;
            GUI.color = Color.white;
            if (Cfg.ShowDebugHud.Value)
            {
                string ctrl = !Cfg.WriteControl.Value ? "overlay-only"
                            : ChaseController.IsFlying ? "FLYING (mod owns stick)"
                            : "native";
                string spd = ChaseController._collective
                    ? $"spd={ChaseController._speed:0} m/s (fwd={ChaseController._vFwd:0}, heliBlend={ChaseController._heliBlend:0.00})"
                    : $"spd={ChaseController._speed:0} m/s";
                GUI.Label(new Rect(12f, 12f, 560f, 22f),
                    $"WT MouseAim  off={off:0.0}°  cone={half:0}°  law={Cfg.ControlLawMode.Value}  [{ctrl}]  {spd}");
                // Instructor's live stick command (what the mod is telling the plane, before manual override).
                GUI.Label(new Rect(12f, 30f, 560f, 22f),
                    $"instructor  pitch={ChaseController.LastPitch:+0.00;-0.00;0.00}  " +
                    $"yaw={ChaseController.LastYaw:+0.00;-0.00;0.00}  roll={ChaseController.LastRoll:+0.00;-0.00;0.00}");
            }
            // Fly Level indicator — distinct cyan so it's obvious the marker is being ignored on purpose.
            if (ChaseController.FlyLevelActive)
            {
                GUI.color = new Color(0.3f, 0.9f, 1f, 0.95f);
                GUI.Label(new Rect(12f, 48f, 560f, 22f),
                    $"FLY LEVEL  holding level — nudge the stick or press [{Cfg.FlyLevelKey.Value}] to release");
            }

            // Anomaly flash + live phase — DEBUG-only (v0.33), hidden unless ShowDebugHud is on.
            if (Cfg.ShowDebugHud.Value)
            {
                // Anomaly flash: show the most recent anomaly's index + type for a few seconds (or until the
                // next one replaces it) so the pilot can jot down "#N felt wrong" mid-flight and tune it later.
                if (Cfg.AnomalyLogging.Value &&
                    Time.time - ChaseController.LastAnomalyTime < ChaseController.AnomalyFlashSec &&
                    ChaseController.LastAnomalyIndex > 0)
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.95f); // white — salmon-red was unreadable against some scenes
                    GUI.Label(new Rect(12f, 66f, 560f, 22f),
                        $"ANOMALY #{ChaseController.LastAnomalyIndex}  {ChaseController.LastAnomalyType}");
                }
                // Live phase of the instructor's plan (LEVEL/FINE/ALIGN/PULL/TURN/HOLD) — white, under the readout.
                if (ChaseController.IsFlying && !string.IsNullOrEmpty(ChaseController.LastPhase))
                {
                    GUI.color = Color.white;
                    GUI.Label(new Rect(12f, 84f, 560f, 22f), $"PHASE: {ChaseController.LastPhase}");
                }
            }
            GUI.color = prev;

            // G-LOC warning: while the pilot is blacked/redded out from sustained G the game zeroes all
            // stick input (PilotPlayerState: pilotStrength < 0.2), so the mod can't fly either. Surface it
            // discreetly in amber, centred near the top, so the loss of control is explained rather than
            // feeling like a mod bug.
            if (ChaseController.PilotStrength < 0.2f)
            {
                var pc = GUI.color;
                GUI.color = new Color(1f, 0.55f, 0.1f, 0.9f); // amber/orange warning
                // Default GUI.Label is left-aligned (centering needs GUIStyle, which lives in a Unity
                // module we don't reference), so place a fixed-width rect at screen centre by hand.
                const float w = 240f;
                GUI.Label(new Rect((Screen.width - w) * 0.5f, Screen.height * 0.16f, w, 24f),
                    "OVER-G — PILOT UNCONSCIOUS");
                GUI.color = pc;
            }
        }

        // --- IMGUI primitives (origin top-left, y-down) ---

        private static bool WorldToGui(Camera cam, Vector3 world, out Vector3 gui)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            gui = new Vector3(sp.x, Screen.height - sp.y, sp.z);
            return sp.z > 0.01f; // in front of camera
        }

        private static void Dot(Vector3 c, float r, Color col)
        {
            var prev = GUI.color; GUI.color = col;
            GUI.DrawTexture(new Rect(c.x - r, c.y - r, r * 2f, r * 2f), _px);
            GUI.color = prev;
        }

        private static void Cross(Vector3 c, float len, float w, Color col)
        {
            var prev = GUI.color; GUI.color = col;
            GUI.DrawTexture(new Rect(c.x - len, c.y - w * 0.5f, len * 2f, w), _px);
            GUI.DrawTexture(new Rect(c.x - w * 0.5f, c.y - len, w, len * 2f), _px);
            GUI.color = prev;
        }

        private static void CircleOutline(Vector3 c, float radius, Color col)
        {
            var prev = GUI.color; GUI.color = col;
            const int seg = 28;
            for (int i = 0; i < seg; i++)
            {
                float ang = (i / (float)seg) * Mathf.PI * 2f;
                float x = c.x + Mathf.Cos(ang) * radius;
                float y = c.y + Mathf.Sin(ang) * radius;
                GUI.DrawTexture(new Rect(x - 1f, y - 1f, 2.5f, 2.5f), _px);
            }
            GUI.color = prev;
        }
    }
}

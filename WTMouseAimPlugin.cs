using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Rewired;
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
        public const string PluginVersion = "0.37.1";

        internal static ManualLogSource Log;

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
            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded (world follow-point chase w/ body-frame roll-then-pull law [roll the lift vector onto the target, then pull up into it] + signed/clamped pull gate (no bunt) + yaw ease-down on big turns + pitch anti-overshoot brake + bank-servo azimuth deadband (anti fine-cone roll wobble) + roll-rate-smoothed damping (anti high-speed roll-PIO limit cycle) + fine integrator + per-axis manual override (anomaly logging suspended while you're on the stick) + Win32 raw mouse + 3rd-person orbit-camera override w/ hysteretic pole-stable horizon leveling + RMB free-look that keeps our orbit pivot (no snap) and eases the view back to your flight direction on release + AoA-true Fly Level toggle [{Cfg.FlyLevelKey.Value}] + phase/maneuver instrumentation + anomaly logging + all-aircraft control (fixed-wing + rotorcraft/VTOL, opt-out via ControlRotorcraft) + master ON/OFF hotkey [{Cfg.ToggleKey.Value}] + clean reticle-only HUD by default (debug readouts behind ShowDebugHud) + adjustable 3rd-person camera position (distance/height/side offsets) + 'I broke it, fix it please' reset-to-defaults button + measured-reactive LOADED-turn assist [shifts a weak-rudder side correction into a steep bank + real G-pull and fades the dead rudder out; the bank target is sized from a turn-RATE so it self-scales with airspeed (a small high-speed nudge commands the steep loaded bank that actually slews the nose), self-adapting per airframe/speed, no tuning needed] + maneuver recorder hotkey [{Cfg.RecordKey.Value}] -> timestamped CSV for tuning across aircraft — tune live via F1).");
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
            }
            else if (Time.time >= _toastUntil) _toastRec = false;
        }

        // True while the active toast is a recorder toast (so OnGUI labels it REC/REC OFF, not ON/OFF).
        private static bool _toastRec;

        private void OnGUI()
        {
            // Master-toggle toast — drawn BEFORE the overlay/enabled guard so it confirms an OFF flip too.
            if (Time.time < _toastUntil)
            {
                var tc = GUI.color;
                GUI.color = _toastOn ? new Color(0.3f, 0.9f, 1f, 0.95f) : new Color(1f, 0.7f, 0.3f, 0.95f);
                const float tw = 220f;
                string msg = _toastRec ? (_toastOn ? "MouseAim  REC START" : "MouseAim  REC STOP")
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
                GUI.Label(new Rect(12f, 12f, 560f, 22f),
                    $"WT MouseAim  off={off:0.0}°  cone={half:0}°  [{ctrl}]");
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

    // ---------------------------------------------------------------------------------------------
    // Live-tunable config (BepInEx.ConfigurationManager can edit these in-game with F1).
    internal static class Cfg
    {
        public static ConfigEntry<bool>  Enabled;
        public static ConfigEntry<KeyCode> ToggleKey; // master enable/disable hotkey (default F10)
        public static ConfigEntry<bool>  ShowOverlay;
        public static ConfigEntry<bool>  ShowDebugHud; // show the diagnostic text readouts (off = clean reticle-only HUD)
        public static ConfigEntry<bool>  DebugLogging; // periodic BepInEx-log dump of mouse/aim/chase state (verbose)
        public static ConfigEntry<bool>  AnomalyLogging; // event-only log: fires one line when a command misbehaves
        public static ConfigEntry<float> MouseSensitivity; // degrees of aim offset per unit of mouse delta
        public static ConfigEntry<float> MouseSmoothing;   // 0..1 one-pole smoothing on the mouse delta
        public static ConfigEntry<float> MaxAimAngle;      // cone half-angle (deg) the marker is clamped within
        public static ConfigEntry<float> AimDistance;      // metres ahead the aim point is placed (projection only)
        public static ConfigEntry<bool>  InvertPitch;

        // --- Chase law (writes flight controls). Per-axis gains may be negative to flip a sign.
        public static ConfigEntry<bool>  WriteControl;        // actually drive the stick (off = overlay only)
        public static ConfigEntry<bool>  ControlRotorcraft;   // fly collective aircraft (helis/hover-VTOLs) too, not just fixed-wing
        public static ConfigEntry<float> PitchYawSensitivity; // base chase gain on the body-frame aim direction
        public static ConfigEntry<float> ChaseDamping;        // derivative damping on the nose's rotation rate
        public static ConfigEntry<float> RollDamping;         // derivative damping on the roll rate (anti bank-wobble)
        public static ConfigEntry<float> RollRateSmoothing;   // sec: low-pass time constant on rollRate feeding the damping term (anti high-speed roll PIO)
        public static ConfigEntry<float> RollGain;            // roll output scale (negative flips roll direction)
        public static ConfigEntry<float> PitchGain;           // pitch output scale (negative flips)
        public static ConfigEntry<float> YawGain;             // yaw/rudder output scale (negative flips)
        public static ConfigEntry<float> OutputSlew;          // max stick units/sec (anti-jerk rate limit)
        public static ConfigEntry<float> MaxBankAngle;        // deg: cap on the bank-angle servo's commanded bank
        public static ConfigEntry<float> RollPitchCoordination;// 0..1 gate pitch pull by lift-vector alignment (roll-then-pull)
        public static ConfigEntry<float> AlignAngle;          // deg above which roll-to-align is full (knee of the bigTurn ramp)
        public static ConfigEntry<float> TurnYawScale;        // 0..1 yaw authority fraction during a big turn
        public static ConfigEntry<float> PitchBrake;          // extra rate damping near the target (anti-overshoot)
        public static ConfigEntry<float> AuthorityRamp;       // engage/disengage blend speed (1/sec)
        public static ConfigEntry<bool>  ManualOverride;      // let the stick/keyboard/pedals override the chase per-axis
        public static ConfigEntry<float> ManualReturnTime;    // sec for an axis to ease back to mouse-aim after release
        public static ConfigEntry<float> ManualDeadzone;      // how far a manual axis must move before it counts as input
        public static ConfigEntry<bool>  RightClickFreeze;    // hold RMB to freeze the reticle + free-look (both views)

        // --- Fine capture (the last few degrees). Log-confirmed v0.22: inside ~5deg the residual is
        // almost pure azimuth with the wings level — roll blends to wings-level, yaw P-term is ~0.05
        // stick, and the heading error decays with a ~6 s time constant ("never quite aligns").
        public static ConfigEntry<float> FineAngle;           // deg: cone inside which the fine-capture aids ramp in
        public static ConfigEntry<float> FineGainBoost;       // extra proportional gain at zero offset (0 = off)
        public static ConfigEntry<float> FineBankGain;        // deg of target bank per deg of azimuth error (the bank-servo gain)
        public static ConfigEntry<float> FineBankDeadzone;    // deg of azimuth error below which the bank servo commands ZERO bank (anti fine-cone roll wobble)

        // --- Fine integrator (kills the steady-state residual the FBW rate-command leaves; v0.24).
        // The game's FlyByWire reads our pitch/yaw as a commanded ANGULAR RATE and PID-tracks it, so a
        // pure-proportional outer loop asymptotes and parks a fraction of a degree short. A small leaky,
        // clamped integrator on the aim error (fine regime only) winds in the bias needed to land on it.
        public static ConfigEntry<float> FineIntegralGain;    // Ki: integrator wind-in rate (0 = off)
        public static ConfigEntry<float> FineIntegralLeak;    // 1/s: bleed toward zero (anti-windup)
        public static ConfigEntry<float> FineIntegralCap;     // max stick units the integrator may add per axis

        // --- Measured-reactive bank-and-pull assist (v0.35). When the rudder is empirically failing to
        // move the heading (low measured yaw effectiveness — happens at high speed / on draggy airframes),
        // the instructor shifts a side correction into bank-and-pull instead of leaning on the weak rudder.
        public static ConfigEntry<bool>  YawAssistEnabled;    // master on/off for the reactive assist
        public static ConfigEntry<float> YawAssistStrength;   // 0..1 overall scale of the assist
        public static ConfigEntry<float> YawAssistResponse;   // sec: low-pass time constant on the yaw-weakness estimate
        public static ConfigEntry<float> CoordPullGain;       // 0..1 coordinating-pull authority (the "pitch into the bank")
        public static ConfigEntry<float> CoordPullCap;        // 0..1 cap on the coordinating-pull stick
        public static ConfigEntry<float> BankAuthGain;        // bank-gain boost per unit assist (steep banks when yaw weak)
        public static ConfigEntry<float> YawWeakFade;         // 0..1 how far the rudder fades out as the assist rises
        public static ConfigEntry<float> AssistTurnRateGain;  // 1/s: turn-rate-targeted bank (self-scales the loaded bank with airspeed)
        public static ConfigEntry<float> CoordPullReleaseAngle;// deg: error cone inside which the coordinating pull eases off

        // --- Maneuver recorder (v0.35): a hotkey dumps a bounded high-rate CSV of the control state so a
        // problem can be captured cleanly across aircraft and the assist calibrated against real data.
        public static ConfigEntry<KeyCode> RecordKey;         // start/stop the CSV capture (default F8)
        public static ConfigEntry<float>   RecordRateHz;      // samples per second written to the CSV

        // --- Fly Level autopilot (v0.24): toggle a key to hold wings-level + nose-on-horizon at the
        // heading captured when you pressed it. Ignores the reticle; a stick nudge or re-press releases it.
        public static ConfigEntry<bool>    FlyLevelEnabled;   // master on/off for the feature
        public static ConfigEntry<KeyCode> FlyLevelKey;       // toggle key (default F7)

        // --- Anomaly logging thresholds (v0.25): how far past "fine" a miss must go before the
        // event-only logger flags it. Sensitivity knobs so the log stays quiet in normal flight.
        public static ConfigEntry<float> AnomalyOvershootDeg; // deg the nose must rebound past its closest approach
        public static ConfigEntry<float> AnomalyOverRollDeg;  // deg actual bank may exceed the servo's target before flagging
        public static ConfigEntry<float> AnomalyLowSpeed;     // m/s below which yaw-wag (low-speed nose wander) is flagged
        public static ConfigEntry<float> AnomalyWobbleSpeed;  // m/s above which roll limit-cycle (high-speed bank wobble) is flagged
        public static ConfigEntry<bool>  AnomalyContext;      // dump a recent-frame trail line with each anomaly

        // --- Cockpit camera follow.
        public static ConfigEntry<bool>  CameraFollow;        // smoothly look toward the marker
        public static ConfigEntry<float> CameraFollowAmount;  // 0..1 fraction of the marker offset to look toward
        public static ConfigEntry<float> CameraFollowSmoothing;// seconds-ish smoothing (higher = lazier)
        public static ConfigEntry<float> CameraPitchOffset;   // 3p: deg the view is pitched down off the aim direction
        public static ConfigEntry<float> OrbitAimSmoothing;   // 3p: view-direction smoothing rate (1/s, higher = snappier)
        public static ConfigEntry<float> HorizonDeadzoneDeg;  // 3p: half-width of the no-horizon-level band at the pole (hysteresis)
        public static ConfigEntry<float> FreeLookReturnTime;  // 3p: seconds to ease the view back to the flight dir on release
        public static ConfigEntry<float> CameraDistanceOffset;// 3p: extra orbit distance (units of radius); + = farther back, - = closer/in front
        public static ConfigEntry<float> CameraHeightOffset;  // 3p: extra height vs plane (units of radius); + = higher, - = lower
        public static ConfigEntry<float> CameraSideOffset;    // 3p: lateral shift (units of radius); + = right of plane, - = left

        // --- "I broke it, fix it please" reset button (drawn as a real button in the F1 menu).
        public static ConfigEntry<bool>  ResetToDefaults;
        private static ConfigFile        _file;               // kept so the reset can walk every entry

        public static void Bind(ConfigFile cf)
        {
            Enabled          = cf.Bind("General", "Enabled", true,
                "Master ON/OFF for the whole mod. Off = stock game controls, no overlay, no camera follow.");
            ToggleKey        = cf.Bind("General", "ToggleKey", KeyCode.F10,
                "Key that enables/disables the whole mod in-flight (flips Enabled). Default F10. A brief on-screen toast confirms the change. Pick any single key.");
            ShowOverlay      = cf.Bind("HUD", "ShowOverlay", true,
                "Show the on-screen aim circle, boresight cross, and turn cone. Purely visual — no effect on handling.");
            ShowDebugHud     = cf.Bind("HUD", "ShowDebugHud", false,
                "Show the diagnostic text readouts in the top-left: mod status / nose-off-marker / cone, the instructor's live pitch/yaw/roll command, the ANOMALY flash, and the live PHASE. OFF by default so installers get a clean reticle-only HUD (the aim circle, airframe marker, FLY LEVEL banner, and G-LOC warning still show). Turn ON for tuning/debugging.");
            DebugLogging     = cf.Bind("HUD", "DebugLogging", false,
                "VERBOSE periodic trace: dumps mouse delta, marker-vs-nose angle, camera-vs-nose angle and chase outputs to the BepInEx log every ~0.1-0.2 s. Token-heavy — leave OFF normally and rely on AnomalyLogging. Flip it on for one run only when a problem feels wrong but the anomaly log stays quiet.");
            AnomalyLogging   = cf.Bind("HUD", "AnomalyLogging", true,
                "Event-only logging: stays silent until a command misbehaves, then writes ONE compact [anomaly] line — overshoot (nose crosses the marker), over-roll (banks past what the turn needs), hunt (output sign-flapping), or persistent-miss (saturated but not closing). Cheap to hand back, unlike the verbose DebugLogging trace. On by default.");

            MouseSensitivity = cf.Bind("Aim", "MouseSensitivity", 0.30f, new ConfigDescription(
                "Degrees the aim circle moves per unit of mouse motion. The raw Win32 delta is normalised to Unity's legacy axis scale (x0.1) so this number is read-backend independent. ~0.3 is a sane start; drag the slider for feel. Higher = the circle races with small hand movements; lower = finer, calmer aiming.",
                new AcceptableValueRange<float>(0.01f, 2.0f)));
            MouseSmoothing   = cf.Bind("Aim", "MouseSmoothing", 0.20f, new ConfigDescription(
                "Smooths raw mouse motion to kill jitter/stepping. 0 = raw (most responsive, can feel jumpy); ~0.2 = light; 0.5+ = very smooth but laggy.",
                new AcceptableValueRange<float>(0f, 0.9f)));
            MaxAimAngle      = cf.Bind("Aim", "MaxAimAngle", 180.0f, new ConfigDescription(
                "Half-angle (deg) the aim circle can sit off the nose. 180 = effectively unlimited (aim anywhere); lower it to cap how far off-boresight you can command. The cone ring is hidden above 89.",
                new AcceptableValueRange<float>(5f, 180f)));
            AimDistance      = cf.Bind("Aim", "AimDistance", 800.0f, new ConfigDescription(
                "How far ahead (m) the aim circle is drawn. Visual only — does not change handling. Set near your typical gun/engagement range.",
                new AcceptableValueRange<float>(100f, 3000f)));
            InvertPitch      = cf.Bind("Aim", "InvertPitch", false,
                "Flips vertical mouse so the circle (and the plane) aim up vs. down. Also flips the camera-follow tilt, since both follow the same circle.");

            WriteControl        = cf.Bind("Control", "WriteControl", true,
                "Let the mod actually fly the plane (drive the stick). Off = overlay/camera only, you keep manual control — handy for A/B comparing the feel.");
            ControlRotorcraft   = cf.Bind("Control", "ControlRotorcraft", true,
                "Also fly helicopters and hover-VTOLs (collective aircraft — the game flags them by takeoffDistance==0). They drive the SAME pitch/roll/yaw (cyclic + tail rotor); collective stays on your throttle, untouched. The chase law was tuned for forward flight, so the feel differs at low speed/hover — turn this OFF to leave rotorcraft on stock controls while keeping mouse-aim for fixed-wing.");
            PitchYawSensitivity = cf.Bind("Control", "PitchYawSensitivity", 3.0f, new ConfigDescription(
                "How hard the instructor pulls the nose toward the circle. Higher = snappier and closes faster, but can overshoot and wobble (raise ChaseDamping to compensate); lower = gentler, easier fine aiming. ~3 is balanced.",
                new AcceptableValueRange<float>(0.5f, 8f)));
            ChaseDamping        = cf.Bind("Control", "ChaseDamping", 0.25f, new ConfigDescription(
                "Calms the inputs as the nose nears the circle so it eases in instead of overshooting — opposes the nose's own turn rate (the anti-wobble term). ~0.25 is a smooth default. 0 = off (snappy, but the rudder can hunt side-to-side); raise toward ~0.4 if it still oscillates around the aim direction, lower if it feels sluggish to close.",
                new AcceptableValueRange<float>(0f, 1f)));
            RollDamping         = cf.Bind("Control", "RollDamping", 0.1f, new ConfigDescription(
                "Anti-wobble damping for the ROLL axis — opposes the rolling RATE so the bank eases onto its target instead of blowing through it. Keep it SMALL: the rate feedback is delayed (one-frame finite difference + the RollRateSmoothing low-pass), so too much of it flips from damping to DRIVING a high-speed roll limit cycle — the ±roll-stick buzz/PIO felt at high dynamic pressure. ~0.1 takes the edge off the roll-out jitter without sustaining the cycle; 0 = off (a touch jittery on-heading); raising past ~0.3 brings the high-speed wobble back. Only opposes the rolling MOTION, so it won't fight a held bank.",
                new AcceptableValueRange<float>(0f, 2f)));
            RollRateSmoothing   = cf.Bind("Control", "RollRateSmoothing", 0.06f, new ConfigDescription(
                "Low-pass time constant (seconds) on the roll RATE that feeds RollDamping. The high-speed roll wobble is a derivative-feedback limit cycle: when level on-heading the roll command is essentially -rollRate*RollDamping, and rollRate is a one-frame finite difference (~60 Hz); at high dynamic pressure the airframe is responsive enough that this delayed rate feedback flips from damping to DRIVING at ~6-7 Hz (a fast roll-stick dither / PIO). Smoothing the rate before the damping term rolls off that high-frequency content so the damping only opposes real, low-frequency roll motion — killing the wobble while keeping turn damping. Higher = more smoothing (more wobble margin, but slightly laggier roll-out damping); 0 = off (raw rate, old behaviour). ~0.05-0.10 is the useful band.",
                new AcceptableValueRange<float>(0f, 0.3f)));
            RollGain            = cf.Bind("Control", "RollGain", 1.0f, new ConfigDescription(
                "Roll authority scale. Lower if it banks too eagerly, raise for crisper rolls (faster roll-in and roll-out). Negative flips roll direction — if the plane rolls AWAY from level when on-target, set this negative.",
                new AcceptableValueRange<float>(-2f, 2f)));
            PitchGain           = cf.Bind("Control", "PitchGain", 1.0f, new ConfigDescription(
                "Pitch authority scale. Negative flips pitch direction (nose chases the wrong way vertically).",
                new AcceptableValueRange<float>(-2f, 2f)));
            YawGain             = cf.Bind("Control", "YawGain", 1.0f, new ConfigDescription(
                "Yaw/rudder authority scale — the lever for fine horizontal alignment while the wings stay level. Raise it if small sideways corrections barely move the nose; lower it if the nose wags or feels twitchy. Negative flips.",
                new AcceptableValueRange<float>(-2f, 3f)));
            OutputSlew          = cf.Bind("Control", "OutputSlew", 6.0f, new ConfigDescription(
                "Max stick travel per second — the anti-jerk rate limit. Lower = silkier but laggier; higher = sharper and more immediate, but can feel jerky.",
                new AcceptableValueRange<float>(1f, 20f)));
            MaxBankAngle        = cf.Bind("Control", "MaxBankAngle", 72.0f, new ConfigDescription(
                "Cap (deg) on the bank the instructor will roll into for a turn. The bank-angle servo (v0.25) commands a bank proportional to the heading error (FineBankGain deg per deg) up to THIS limit, then holds it and rolls out as the turn completes — so a hard side command rolls to a firm, BOUNDED bank instead of slamming to full deflection and over-rolling. Lower for gentler max banks; raise toward 85 for very hard turns.",
                new AcceptableValueRange<float>(10f, 85f)));
            RollPitchCoordination = cf.Bind("Control", "RollPitchCoordination", 0.9f, new ConfigDescription(
                "Roll-then-pull coordination (body-frame law, v0.26). Gates the pitch PULL by how well the lift vector (body-up, the way a pull swings the nose) is aligned with the target during a big turn, so the plane rolls the lift vector ONTO the target first, then pulls up into it — the efficient line — instead of pulling across the wrong plane and bunting. The gate is signed and clamped at zero, so it NEVER pushes (no negative-G). 0 = off (pull immediately); 1 = no pull until the lift vector is on the target, never a push. ~0.9 is a firm roll-then-pull. Lower if turns feel hesitant to pull; raise toward 1 if it pulls before it has rolled. Only active above FineAngle (scaled by the big-turn ramp); inside the fine cone the pull is direct.",
                new AcceptableValueRange<float>(0f, 1f)));
            AlignAngle          = cf.Bind("Control", "AlignAngle", 25.0f, new ConfigDescription(
                "Off-angle (deg) at which the roll-to-align law is at FULL strength — the knee of the 'big turn' ramp that runs from FineAngle (direct nudge) up to here (roll the lift vector onto the target, pull gated). Below FineAngle the law is the fine wings-level/azimuth bank servo; between the two it blends. Lower = commits to the roll-then-pull line sooner (smaller direction changes count as big turns); raise = only the largest reorientations roll first.",
                new AcceptableValueRange<float>(8f, 90f)));
            TurnYawScale        = cf.Bind("Control", "TurnYawScale", 0.35f, new ConfigDescription(
                "Yaw authority fraction during a big turn (v0.26). At full big-turn strength the yaw/rudder command is scaled to this, so the bank + pull do the work and the rudder stops pinning to its stops and adding to the messy feel. 1 = full yaw always (old behaviour); ~0.35 leaves a little coordinating rudder; 0 = no rudder mid-turn. Full yaw authority always returns inside the fine cone for final alignment.",
                new AcceptableValueRange<float>(0f, 1f)));
            PitchBrake          = cf.Bind("Control", "PitchBrake", 0.35f, new ConfigDescription(
                "Pitch anti-overshoot brake. Adds extra rate damping that ramps in as the nose nears the target while it's still swinging, so a fast large-angle pull (e.g. takeoff climb-out) decelerates ONTO the marker instead of crossing it and settling back. Opposes rotation only, so it vanishes once the nose stops turning and never fights a held attitude. 0 = off; raise if it still overshoots in pitch, lower if it feels like it brakes too early.",
                new AcceptableValueRange<float>(0f, 1.5f)));
            AuthorityRamp       = cf.Bind("Control", "AuthorityRamp", 5.0f, new ConfigDescription(
                "How fast control blends back to the game when the mod disengages (per second). Higher = snappier handoff.",
                new AcceptableValueRange<float>(1f, 15f)));
            ManualOverride      = cf.Bind("Control", "ManualOverride", true,
                "Let your stick / keyboard / rudder pedals take over PER AXIS while mouse-aim flies. Push roll and you get roll (the mod stops leveling the wings); push rudder and you get rudder — the mouse keeps aiming whatever axis you're NOT touching. Release and that axis eases back to mouse-aim. Off = the mod fully owns the stick (manual inputs ignored while flying).");
            ManualReturnTime    = cf.Bind("Control", "ManualReturnTime", 0.25f, new ConfigDescription(
                "How long (seconds) a released axis takes to ease back to mouse-aim after you let go of it. Lower = snaps back to the mouse fast; higher = hands the axis back gently. Manual takeover itself is always instant.",
                new AcceptableValueRange<float>(0.05f, 1f)));
            ManualDeadzone      = cf.Bind("Control", "ManualDeadzone", 0.05f, new ConfigDescription(
                "How far a manual axis must move before it counts as you taking over (ignores stick noise / a centred gamepad). Raise if the mod hands you control from a twitchy stick at rest; keyboard is digital so this barely matters there.",
                new AcceptableValueRange<float>(0f, 0.3f)));
            FineAngle           = cf.Bind("Control", "FineAngle", 6.0f, new ConfigDescription(
                "Cone half-angle (deg) inside which the fine-capture aids (FineGainBoost / FineBankGain) ramp in. They are at full strength with the nose on the circle and fade to nothing at this angle, so the large-angle behaviour is untouched.",
                new AcceptableValueRange<float>(2f, 15f)));
            FineGainBoost       = cf.Bind("Control", "FineGainBoost", 2.5f, new ConfigDescription(
                "Extra pitch/yaw pull for the last few degrees: multiplies the proportional term by up to (1 + this) as the offset closes. Cures the 'never quite centres' residual where a ~1 deg error left only ~0.05 stick. 0 = off. Raise if the nose still parks short of the circle; lower if it hunts around it.",
                new AcceptableValueRange<float>(0f, 5f)));
            FineBankGain        = cf.Bind("Control", "FineBankGain", 3.0f, new ConfigDescription(
                "The bank-angle servo gain (v0.25): degrees of commanded bank per degree of heading (azimuth) error, capped at MaxBankAngle. This now drives the bank across the WHOLE range — a small error leans a few degrees, a big side command rolls to a firm bank and holds it — replacing the old proportional roll-rate slam that over-rolled. 0 = no banking (wings-level, leans on the weak rudder). Raise to bank harder/sooner; lower if it over-banks or wing-rocks.",
                new AcceptableValueRange<float>(0f, 10f)));
            FineBankDeadzone    = cf.Bind("Control", "FineBankDeadzone", 2.5f, new ConfigDescription(
                "Heading-error deadband (deg) for the bank servo. Below this azimuth error the commanded bank is ZERO — the wings stay level and the rudder/yaw does the final fine capture, instead of the bank servo amplifying a sub-few-degree heading hunt into a continuous roll-stick dither (the fine-cone roll wobble). Above it the bank ramps in smoothly (the error past the deadband still feeds FineBankGain) so genuine side commands bank to turn normally. 0 = off (old behaviour); raise if the wings still rock on-heading, lower if small corrections won't bank.",
                new AcceptableValueRange<float>(0f, 15f)));
            FineIntegralGain    = cf.Bind("Control", "FineIntegralGain", 0.8f, new ConfigDescription(
                "The piece that actually lands the nose ON the circle. The game's fly-by-wire treats our pitch/yaw as a turn-RATE request, not a position, so a plain proportional pull always parks a fraction of a degree short — this small integrator winds in the steady bias needed to close that last bit. Only active inside FineAngle. 0 = off (back to the v0.23 'gets close but never quite centres' behaviour). Raise if it still parks short; lower if it slowly drifts past and hunts.",
                new AcceptableValueRange<float>(0f, 3f)));
            FineIntegralLeak    = cf.Bind("Control", "FineIntegralLeak", 0.5f, new ConfigDescription(
                "How fast the fine integrator bleeds back toward zero (per second) — the anti-windup safety. Higher = forgets faster (less chance of overshoot/hunt, but may not fully close); lower = holds its bias longer (closes harder, slower to let go). ~0.5 is a calm default.",
                new AcceptableValueRange<float>(0f, 4f)));
            FineIntegralCap     = cf.Bind("Control", "FineIntegralCap", 0.12f, new ConfigDescription(
                "Hard limit on how much stick (per axis) the fine integrator may add. Keeps it from winding up into a lurch if the nose is held off-target. ~0.12 is enough to defeat the rate-command residual without being felt as a kick.",
                new AcceptableValueRange<float>(0f, 0.3f)));

            YawAssistEnabled    = cf.Bind("Control", "YawAssistEnabled", true,
                "Reactive bank-and-pull (v0.35). The honest way to move the flight-path heading is to BANK and PULL, not to push rudder — and rudder gets weaker the faster you go, which is why a small sideways nudge 'leaves yaw on the table' at high speed and the nose parks short of the reticle. When ON, the instructor continuously MEASURES how much heading change your yaw command is actually buying; when that's poor it automatically rolls a touch more into the correction AND adds a coordinating pull so the bank turns the nose, instead of leaning on the weak rudder. Self-adapting per airframe and speed — no tuning needed to work. OFF = the v0.34 rudder-led fine capture.");
            YawAssistStrength   = cf.Bind("Control", "YawAssistStrength", 0.7f, new ConfigDescription(
                "Overall scale of the reactive bank-and-pull assist (YawAssistEnabled). 0 = effectively off; 1 = full authority shift into bank-and-pull when the rudder is measured to be ineffective. ~0.7 is a firm assist that still leaves the rudder doing the final fine alignment. Lower if side corrections now over-bank or feel too eager to roll; raise if the nose still parks short on high-speed sideways nudges. Only acts in the small/mid correction regime — big turns already roll-and-pull, so it fades out there.",
                new AcceptableValueRange<float>(0f, 1f)));
            YawAssistResponse   = cf.Bind("Control", "YawAssistResponse", 0.8f, new ConfigDescription(
                "How quickly (seconds) the measured yaw-weakness estimate reacts. It's a low-pass so a momentary blip doesn't swing the assist — but it has MEMORY, so once it has learned the rudder is weak in this regime (e.g. high speed) the very next nudge already banks-and-pulls. Lower = snappier/twitchier adaptation; higher = smoother but slower to notice the rudder is failing. ~0.8 s is a calm, responsive default.",
                new AcceptableValueRange<float>(0.1f, 3f)));
            CoordPullGain       = cf.Bind("Control", "CoordPullGain", 0.8f, new ConfigDescription(
                "Coordinating-pull authority — the 'pitch INTO the bank' half of the assist, and the REAL driver of a high-speed correction. Once banked into a side nudge, a level turn needs back-pressure or gravity just drops the nose and the bank does nothing (without it the nose mushes at under 1g and the heading barely moves). This adds a nose-up pull proportional to the commanded bank so the bank becomes a loaded turn that actually slews the nose. Always a PULL (nose-up, positive-G) — clamped so it can never push/bunt, and capped by CoordPullCap. 0 = bank only (the nose sags); ~0.8 loads a firm coordinated turn. Raise if high-speed side nudges still creep, lower if they balloon high or feel grabby.",
                new AcceptableValueRange<float>(0f, 1f)));
            CoordPullCap        = cf.Bind("Control", "CoordPullCap", 0.85f, new ConfigDescription(
                "Cap on the coordinating-pull stick so the assist can load real G for a firm, quick correction without ever pinning the pitch axis (the rest of the pitch budget stays for the normal pull-to-target and the anti-overshoot brake). ~0.85 lets a high-speed nudge pull hard enough to slew the nose in ~1-2 s. Lower if banked corrections pull too hard / bleed too much energy; raise toward 1 if they still feel soft at the very highest speeds.",
                new AcceptableValueRange<float>(0f, 1f)));
            BankAuthGain        = cf.Bind("Control", "BankAuthGain", 5.0f, new ConfigDescription(
                "How much steeper the instructor banks per unit of measured yaw-weakness (the assist). At full assist the fine bank-servo gain is multiplied by (1 + this), so a small side nudge at high speed commands a real loaded-turn bank (~45-60 deg, still capped by MaxBankAngle) instead of the shallow ~20 deg that turns at well under 1 deg/s. Backed by the coordinating pull this becomes an efficient loaded turn. Lower if high-speed nudges over-bank/feel violent; raise if they still don't bank enough to turn.",
                new AcceptableValueRange<float>(0f, 10f)));
            YawWeakFade         = cf.Bind("Control", "YawWeakFade", 1.0f, new ConfigDescription(
                "How far the rudder is faded OUT as the assist rises. When the rudder is measured ineffective (high speed) it's just sideslip and drag doing nothing for the heading, so the law commits to bank-and-pull instead. At full assist the fine-regime yaw command is scaled by (1 - this), so 1.0 leaves ~30% rudder for a little coordination (never fully zero), 0 = keep full rudder alongside the bank+pull. Low speed (rudder still effective) is unaffected either way. Lower if you want some rudder retained when weak; raise to commit harder to bank+pull.",
                new AcceptableValueRange<float>(0f, 1f)));
            AssistTurnRateGain  = cf.Bind("Control", "AssistTurnRateGain", 1.5f, new ConfigDescription(
                "THE high-speed fix (v0.37). The old assist banked PROPORTIONAL to the heading error, so a small nudge commanded a shallow bank that turns fast at low speed but barely at all when fast (a 12 deg bank at 400 m/s slews the nose ~0.3 deg/s — the nose mushed the last few degrees). Instead the bank is now sized from a target TURN-RATE (proportional to error, this gain in 1/s) converted to the bank that physically holds it: phi = atan(omega*V/g). Because V is in there, the SAME error commands a steep loaded bank when fast and a gentle one when slow — automatically, no per-speed tuning. Blended in by measured yaw-weakness, so low speed / strong-yaw airframes keep the old gentle servo untouched. Higher = asks for a faster turn (steeper bank, snappier, more G) for a given error; lower = gentler. ~1.5 slews a high-speed nudge onto aim in ~1 s. Lower if fast nudges now overshoot or feel violent.",
                new AcceptableValueRange<float>(0f, 4f)));
            CoordPullReleaseAngle = cf.Bind("Control", "CoordPullReleaseAngle", 2.0f, new ConfigDescription(
                "Heading-error cone (deg) inside which the coordinating pull eases back to zero. Outside it the pull stays at full strength so the loaded turn holds its G right down through the tail of the correction (the v0.36 pull tapered over the whole 6 deg fine cone, so it was already half-gone at 3 deg and the nose mushed); inside it the pull bleeds off so the bank+pull releases cleanly onto aim instead of overshooting. ~2 deg keeps the turn loaded until the nose is nearly on, then lets go. Raise if it overshoots / balloons past aim; lower if the very last degree still creeps.",
                new AcceptableValueRange<float>(0.5f, 8f)));
            RecordKey           = cf.Bind("Recorder", "RecordKey", KeyCode.F8,
                "Key that starts/stops the maneuver recorder. Press once to begin capturing, fly the maneuver, press again to stop — each capture writes its own timestamped CSV (mouseaim-rec-<date-time>.csv) into the BepInEx folder next to LogOutput.log, one row per sample. A 'REC' marker shows on-screen while it's running. For diagnosing/tuning feel across different aircraft. Default F8.");
            RecordRateHz        = cf.Bind("Recorder", "RecordRateHz", 20f, new ConfigDescription(
                "How many samples per second the maneuver recorder writes to the CSV. Higher = finer time resolution (bigger files); 20/s resolves a normal correction well without bloating the file. Sampling runs on the physics step, so very high values are capped by the fixed-update rate.",
                new AcceptableValueRange<float>(5f, 60f)));

            FlyLevelEnabled     = cf.Bind("FlyLevel", "Enabled", true,
                "Enable the 'Fly Level' toggle key. When you press it, the instructor locks the current heading and holds TRUE level flight — wings level, zero climb rate (the velocity vector on the horizon, accounting for angle-of-attack), ignoring the aim circle. Press again (or nudge the stick) to return to mouse-aim.");
            FlyLevelKey         = cf.Bind("FlyLevel", "Key", KeyCode.F7,
                "Key that toggles Fly Level on/off. Default F7. Pick any single key (this is one key, not a chord).");
            AnomalyOvershootDeg = cf.Bind("HUD", "AnomalyOvershootDeg", 5.0f, new ConfigDescription(
                "Overshoot sensitivity: how far (deg) the nose must rebound past its closest approach to the marker before the anomaly log flags an overshoot. Only counts once the nose actually arrived (closest approach < 5deg), so it flags a genuine crossing of the marker rather than mid-manoeuvre wobble. Lower = flags smaller overshoots (noisier log); higher = only flags gross ones.",
                new AcceptableValueRange<float>(0.5f, 20f)));
            AnomalyOverRollDeg  = cf.Bind("HUD", "AnomalyOverRollDeg", 12.0f, new ConfigDescription(
                "Over-roll sensitivity: how far (deg) the actual bank may exceed the bank-angle servo's target before the anomaly log flags an over-roll. Lower = stricter; higher = only flags large bank overshoots.",
                new AcceptableValueRange<float>(2f, 45f)));
            AnomalyLowSpeed     = cf.Bind("HUD", "AnomalyLowSpeed", 70.0f, new ConfigDescription(
                "Speed (m/s) below which the anomaly log flags YAW-WAG — the nose wagging left/right on the takeoff roll / low-speed regime, where rudder authority is low and the cruise-tuned yaw loop over-corrects. Set near your typical rotation/climb-out speed; 0 disables the check in practice.",
                new AcceptableValueRange<float>(0f, 200f)));
            AnomalyWobbleSpeed  = cf.Bind("HUD", "AnomalyWobbleSpeed", 200.0f, new ConfigDescription(
                "Speed (m/s) ABOVE which the anomaly log flags ROLL-WOBBLE — a small roll limit-cycle (the bank rocking back and forth a little, the +/-0.1-ish roll output you see jittering in the top-left at high speed) that the cruise-tuned roll loop falls into at high dynamic pressure, where the ailerons are far more effective than at the tuning speed. Flagged only while roughly on-heading (small off) so a normal hard turn doesn't trip it. Set near the speed where you first feel the bank get twitchy; very high = effectively disables the check.",
                new AcceptableValueRange<float>(50f, 600f)));
            AnomalyContext      = cf.Bind("HUD", "AnomalyContext", true,
                "When an anomaly fires, also emit one compact [anomaly:trail] line with the last ~20 frames of state (off, bank vs target, P/R/Y outputs, yaw rate, speed) so the LEAD-UP to the event is visible without any continuous logging. Throttled to once per second. Turn off for the leanest possible log.");
            RightClickFreeze    = cf.Bind("Control", "RightClickFreeze", true,
                "Hold RIGHT MOUSE to freeze the aim reticle (cockpit AND 3rd-person): the plane keeps flying to the frozen point while the mouse looks the camera around, then the view eases back to the aim when you release. War Thunder–style free-look. Off = only the game's bound Free Look freezes the reticle.");

            CameraFollow          = cf.Bind("Camera", "CameraFollow", true,
                "Smoothly turn the cockpit view toward the aim circle, so you look where you're steering.");
            CameraFollowAmount    = cf.Bind("Camera", "CameraFollowAmount", 0.5f, new ConfigDescription(
                "How far the view leans toward the circle. 0 = view stays forward (circle moves freely on screen); 1 = looks fully at the circle (which then sits glued to screen-centre — you can't see it lead the nose). ~0.5 lets the circle visibly lead.",
                new AcceptableValueRange<float>(0f, 1f)));
            CameraFollowSmoothing = cf.Bind("Camera", "CameraFollowSmoothing", 0.3f, new ConfigDescription(
                "View-follow lag (seconds-ish). Higher = lazier, smoother camera; lower = snappier, can feel twitchy.",
                new AcceptableValueRange<float>(0.02f, 1f)));
            CameraPitchOffset     = cf.Bind("Camera", "CameraPitchOffset", 11f, new ConfigDescription(
                "3rd-person only: degrees the view is pitched DOWN off the aim direction, trading the aim circle's screen position against the airframe's. 0 = circle dead-centre (plane low in frame); ~11 = circle a little above centre, plane a little below — 'behind and slightly above, looking down at the airframe'; ~22 = plane centred (circle high).",
                new AcceptableValueRange<float>(0f, 22f)));
            OrbitAimSmoothing     = cf.Bind("Camera", "OrbitAimSmoothing", 8f, new ConfigDescription(
                "3rd-person only: how quickly the orbit camera's view direction chases the aim circle (per second, frame-rate independent). Higher = snappier tracking; lower = lazier, smoother swings. Position never lags the plane — this only smooths the direction.",
                new AcceptableValueRange<float>(1f, 20f)));
            HorizonDeadzoneDeg    = cf.Bind("Camera", "HorizonDeadzoneDeg", 5f, new ConfigDescription(
                "3rd-person only: half-width (deg) of the no-horizon-level band straight up/down. Inside it the camera stops re-leveling to the world horizon and holds its current up so it can't flip through the singularity; leveling eases back in over this band as you come out (hysteresis), so over-the-top loops roll to the new level side smoothly instead of snapping. Smaller keeps the horizon level closer to vertical; larger widens the no-level hold band.",
                new AcceptableValueRange<float>(0f, 20f)));
            FreeLookReturnTime    = cf.Bind("Camera", "FreeLookReturnTime", 0.5f, new ConfigDescription(
                "3rd-person only: seconds for the view to smoothly swing back to your flight direction when you RELEASE free-look (RMB / Free Look). Classic free-look — the plane keeps flying the heading it held; only the camera eases back (smoothstep). 0.5 is a gentle swing; smaller = snappier return, larger = lazier.",
                new AcceptableValueRange<float>(0.05f, 2f)));
            CameraDistanceOffset  = cf.Bind("Camera", "CameraDistanceOffset", 0f, new ConfigDescription(
                "3rd-person only: how far the camera sits from the plane, on top of the stock zoom-aware distance (measured in orbit-radius units, so it scales with zoom). 0 = stock framing. Positive pulls the camera FARTHER back; negative brings it CLOSER (large negatives can push it in front of the plane). Try +/-0.5 at a time.",
                new AcceptableValueRange<float>(-1.8f, 3f)));
            CameraHeightOffset    = cf.Bind("Camera", "CameraHeightOffset", 0f, new ConfigDescription(
                "3rd-person only: camera height relative to the plane, on top of the stock rise (orbit-radius units). 0 = stock. Positive raises the camera (look down more at the airframe); negative lowers it (look up). Try +/-0.3 at a time.",
                new AcceptableValueRange<float>(-1.5f, 2f)));
            CameraSideOffset      = cf.Bind("Camera", "CameraSideOffset", 0f, new ConfigDescription(
                "3rd-person only: shift the camera left/right of the plane (orbit-radius units), for an over-the-shoulder framing. 0 = centred behind. Positive shifts to the plane's RIGHT, negative to the LEFT. Try +/-0.3 at a time.",
                new AcceptableValueRange<float>(-2f, 2f)));

            // "I broke it, fix it please" — a real button in the F1 (ConfigurationManager) menu that
            // restores every MouseAim setting to its default. The custom drawer below replaces the usual
            // checkbox with the button; the bound bool is just a carrier (its value is never read).
            ResetToDefaults = cf.Bind("ZZZ - Panic Button", "I broke it, fix it please", false, new ConfigDescription(
                "Click to reset ALL of this mod's settings (camera, control law, keybinds, HUD — everything) back to their defaults. Use this if you've tuned yourself into a corner and want a clean slate.",
                null, new ConfigurationManagerAttributes { CustomDrawer = DrawResetButton, HideDefaultButton = true, HideSettingName = true }));
            _file = cf;

            // Config logging (v0.29): dump the full control law ONCE at startup, then log just the
            // changed entry whenever a value is edited live (F1 menu) — so the log always shows what
            // gains produced any [anomaly]/[maneuver] line, without bloating each event line.
            cf.SettingChanged += (_, e) =>
            {
                var s = e.ChangedSetting;
                WTMouseAimPlugin.Log.LogInfo($"[config] {s.Definition.Section}/{s.Definition.Key} = {s.BoxedValue}");
            };
            LogSnapshot();
        }

        // One compact [config ...] line with every control-law knob — emitted at startup so the log is
        // self-describing for tuning/debugging. Live edits are logged per-entry via SettingChanged above.
        public static void LogSnapshot()
        {
            WTMouseAimPlugin.Log.LogInfo(
                $"[config sens={PitchYawSensitivity.Value:0.0} chaseDamp={ChaseDamping.Value:0.00} " +
                $"pitchG={PitchGain.Value:0.0} yawG={YawGain.Value:0.0} rollG={RollGain.Value:0.00} rollDamp={RollDamping.Value:0.00} rollSm={RollRateSmoothing.Value:0.00} " +
                $"bankGain={FineBankGain.Value:0.0} bankDz={FineBankDeadzone.Value:0.0} maxBank={MaxBankAngle.Value:0} " +
                $"fineAng={FineAngle.Value:0} fineBoost={FineGainBoost.Value:0.0} align={AlignAngle.Value:0} " +
                $"coord={RollPitchCoordination.Value:0.00} brake={PitchBrake.Value:0.00} yawSc={TurnYawScale.Value:0.00} slew={OutputSlew.Value:0.0} " +
                $"iGain={FineIntegralGain.Value:0.00} iLeak={FineIntegralLeak.Value:0.00} iCap={FineIntegralCap.Value:0.00} " +
                $"yawAssist={(YawAssistEnabled.Value ? 1 : 0)} yaStr={YawAssistStrength.Value:0.00} yaResp={YawAssistResponse.Value:0.00} " +
                $"coordPull={CoordPullGain.Value:0.00} coordCap={CoordPullCap.Value:0.00} bankAuth={BankAuthGain.Value:0.0} yawFade={YawWeakFade.Value:0.00} " +
                $"trGain={AssistTurnRateGain.Value:0.00} pullRel={CoordPullReleaseAngle.Value:0.0}]");
        }

        // Custom F1-menu widget for ResetToDefaults: a single button instead of a checkbox.
        private static void DrawResetButton(ConfigEntryBase _)
        {
            if (GUILayout.Button(new GUIContent("I broke it, fix it please",
                    "Reset every MouseAim setting back to its default."), GUILayout.ExpandWidth(true)))
                ResetAllToDefaults();
        }

        // Walk every bound entry and restore its default. Setting BoxedValue fires SettingChanged, so
        // each restored knob is logged via the hook in Bind(); we add one summary line + a fresh snapshot.
        private static void ResetAllToDefaults()
        {
            if (_file == null) return;
            var defs = new System.Collections.Generic.List<ConfigDefinition>(_file.Keys);
            foreach (var def in defs)
            {
                var entry = _file[def];
                if (entry == null || entry == ResetToDefaults) continue; // skip the panic button itself
                if (!Equals(entry.BoxedValue, entry.DefaultValue))
                    entry.BoxedValue = entry.DefaultValue;
            }
            WTMouseAimPlugin.Log.LogInfo("[config] ALL settings reset to defaults ('I broke it, fix it please').");
            LogSnapshot();
        }
    }

    // Minimal stand-in for BepInEx.ConfigurationManager's attributes class. ConfigurationManager finds
    // it in a ConfigDescription's tags by TYPE NAME via reflection (no hard assembly reference needed),
    // then reads these fields — so a local copy carrying just the bits we use drives the in-menu button.
    internal sealed class ConfigurationManagerAttributes
    {
        public System.Action<ConfigEntryBase> CustomDrawer;
        public bool? HideDefaultButton;
        public bool? HideSettingName;
    }

    // ---------------------------------------------------------------------------------------------
    // Shared "should the mod be passive right now?" checks. True when the player is in a menu, the
    // map is maximized, a radial/leaderboard is up, or the game is paused (timeScale 0). Used to
    // freeze the aim marker and the camera follow so they don't drift while you're not flying.
    internal static class Guards
    {
        public static bool MenusOpen() =>
            Time.timeScale == 0f ||
            DynamicMap.mapMaximized ||
            RadialMenuMain.IsInUse() ||
            Leaderboard.IsOpen();
    }

    // ---------------------------------------------------------------------------------------------
    // The aim rig. The marker is a WORLD-LOCKED follow point (spec §2): the mouse rotates a world-space
    // aim direction and the chase flies the nose ONTO that point. The point stays exactly where you put
    // it in the world — the plane follows it and the offset eases to zero as the nose arrives. This is
    // point-and-chase, NOT a joystick: the marker never rides the airframe (that was the v0.14 mistake),
    // and nothing here ever snaps it back to the nose. The only things that move it are the mouse nudge
    // and the cone clamp. Re-seeds to the boresight whenever we (re)acquire an aircraft.
    internal static class AimRig
    {
        private static Vector3 _aimForward = Vector3.zero; // world-space unit direction (the marker)
        private static int _lastAircraftId = -1;
        private static Vector2 _smoothedDelta;             // one-pole-smoothed mouse delta
        private static bool _captured;                     // we are reading the mouse for aiming this frame
        private static Vector3 _prevNoseDbg = Vector3.zero; // last Update's nose dir (diagnostic: plane-driven marker decay)
        private static Vector3 _prevAimDbg  = Vector3.zero; // last Update's marker dir (diagnostic: total marker rotation)
        private static float   _lastAimLog;                 // throttle the [aim] trace to ~5/sec
        private static bool _captureFresh;                 // true on the frame aiming begins (drop the recenter-warp jump)
        private static bool _managing;                     // we are currently driving the cursor regime at all
        private static Vector2 _lookDelta;                 // smoothed mouse delta exposed for 3p free-look (same units as aim)

        // Win32 raw mouse: GetCursorPos gives a true hardware delta from frame 1, immune to Unity's
        // focus-gated legacy "Mouse X/Y" axis (which stays dead until the window gets a focus event,
        // i.e. an alt-tab). We recenter the OS cursor to the primary-screen centre each captured frame
        // so big sweeps never clamp at the desktop edge.
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int n);
        private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
        // Unity's legacy "Mouse X" axis ≈ hardwarePixels x 0.1 (its default InputManager sensitivity).
        // Multiplying the raw Win32 pixel delta by the same factor keeps MouseSensitivity meaning the
        // same thing whether we read via Win32 or the Unity axis.
        private const float PixelToAxis = 0.1f;

        public static Vector3 AimForward => _aimForward;
        public static Vector3 MouseAimPos(Aircraft ac) => ac.transform.position + _aimForward * Cfg.AimDistance.Value;
        // Smoothed mouse delta during 3rd-person free-look (same units/smoothing as the aim), zero otherwise.
        // CameraOrbitPatch multiplies it by MouseSensitivity so free-look feel == aim feel.
        internal static Vector2 LookDelta => _lookDelta;

        public static void Update()
        {
            bool ok = TryGetContext(out var ac, out var cam);
            bool context = Cfg.Enabled.Value && ok && !ac.disabled; // dead plane => release cursor, stop aiming

            bool frozen    = AimFrozen();
            bool inCockpit = CameraStateManager.cameraMode == CameraMode.cockpit;
            bool inOrbit   = CameraStateManager.cameraMode == CameraMode.orbit;

            if (!context)
            {
                ReleaseCursor();       // hand the cursor back to the game (normal pointer for menus)
                _lastAircraftId = -1;  // force re-seed on next acquire
                _smoothedDelta = Vector2.zero;
                return;
            }

            // Choose a cursor regime that COOPERATES with the game's CursorManager (see ApplyCursorRegime):
            //   aimCapture — mouse-aim (cockpit OR orbit), not frozen: hidden + lockState=None + Win32
            //                recentre (raw delta from frame 1, no alt-tab needed).
            //   flyHidden  — flying but not aim-capturing (frozen / free-look): hidden + lockState=Locked,
            //                the game's own flying regime. 3rd-person orbit free-look ONLY reads the look
            //                axes when the cursor is hidden (CameraOrbitState gates on !Cursor.visible), so
            //                leaving it visible — our old release behaviour — was exactly why free-look
            //                died in 3p. This is also what lets the RMB/Free-Look freeze orbit the camera.
            //   visible    — a menu/pause/UI wants the pointer.
            bool flying     = Cfg.WriteControl.Value;  // context is already true here
            bool menuWants  = Guards.MenusOpen() || CursorManager.GetFlags() != CursorFlags.None;
            bool flyHidden  = flying && !menuWants;
            bool aimCapture = flyHidden && !frozen && (inCockpit || inOrbit);
            // Orbit free-look: WE drive the camera with the mouse (so it gets the same sensitivity as
            // aiming), so capture the cursor exactly like aiming (None+hidden + Win32 recenter) instead of
            // handing the look to native. Cockpit free-look stays native (Locked+hidden, below).
            bool lookCapture = flyHidden && frozen && inOrbit;
            ApplyCursorRegime(flyHidden, aimCapture || lookCapture);

            Transform t = ac.transform;
            int id = ac.GetInstanceID();
            if (id != _lastAircraftId || _aimForward == Vector3.zero)
            {
                _aimForward = t.forward; // seed on the nose
                _lastAircraftId = id;
                _smoothedDelta = Vector2.zero;
            }

            // The marker is WORLD-LOCKED: once placed in world space it stays exactly where you put it
            // while the plane flies its nose onto it (point-and-chase). Nothing here pulls it back toward
            // the nose; only the mouse nudge below and the cone clamp move it. Nudge it by the mouse delta
            // only while we own the cursor for aiming; otherwise it stays parked in the world (frozen
            // during menus/map/pause/free-look/external cam).
            Vector2 raw = Vector2.zero;
            if (aimCapture || lookCapture)
            {
                raw = ReadMouseDelta();
                float sm = Mathf.Clamp01(Cfg.MouseSmoothing.Value);
                _smoothedDelta = Vector2.Lerp(raw, _smoothedDelta, sm); // sm=0 -> raw, higher -> smoother/laggier
            }
            else
            {
                _smoothedDelta = Vector2.zero; // drop stale delta so it doesn't lurch on resume
            }

            // Expose the look delta for orbit free-look (CameraOrbitPatch). Same smoothed delta the aim
            // uses, so applying MouseSensitivity camera-side makes free-look and aiming feel identical.
            _lookDelta = lookCapture ? _smoothedDelta : Vector2.zero;

            if (aimCapture)
            {
                float sens = Cfg.MouseSensitivity.Value;
                float pan  = _smoothedDelta.x;
                float tilt = _smoothedDelta.y * (Cfg.InvertPitch.Value ? -1f : 1f);
                // Pick the rotation frame by view. Cockpit: the airframe up/right (== screen, since the
                // cockpit view rolls with the plane). Orbit: a HORIZON-LOCKED screen frame derived from the
                // camera's forward — "mouse up = up on screen" regardless of airframe roll/tilt (MouseFlight's
                // screen-relative aim, MouseFlightController.cs:136-137). We deliberately do NOT use the
                // camera's own up/right: the orbit cam carries a little roll (and lags), which would feed back
                // into the aim and let the marker slowly walk off on its own (the "drift" that a camera-toggle
                // reset). Re-deriving right = up x forward kills that roll feedback.
                Vector3 upAxis, rightAxis;
                if (inOrbit && cam != null)
                {
                    Vector3 camFwd = cam.transform.forward;
                    rightAxis = Vector3.Cross(Vector3.up, camFwd);
                    if (rightAxis.sqrMagnitude < 1e-4f)        // looking near-straight up/down: no stable horizon
                    { rightAxis = cam.transform.right; upAxis = cam.transform.up; }
                    else
                    { rightAxis.Normalize(); upAxis = Vector3.Cross(camFwd, rightAxis).normalized; }
                }
                else { upAxis = t.up; rightAxis = t.right; }
                _aimForward = Quaternion.AngleAxis(pan * sens, upAxis)
                            * Quaternion.AngleAxis(-tilt * sens, rightAxis)
                            * _aimForward;
            }

            // Clamp the marker into the cone around the CURRENT nose — but ONLY when it actually exceeds
            // the cone. Vector3.RotateTowards has a near-parallel degeneracy: when the marker is a hair off
            // the nose (under ~0.3deg) its rotation axis collapses and it snaps the marker ONTO the nose
            // instead of leaving it alone, then re-eats every tiny mouse nudge. That glued the marker to
            // the boresight ("snaps to 0, impossible to ease off") — the opposite of a max-angle clamp.
            // Guarding on offset > MaxAimAngle makes it a true limiter: inside the cone it's a genuine
            // no-op (marker perfectly free near centre), and it only pulls back when you over-command.
            Vector3 preClamp = _aimForward;
            if (Vector3.Angle(t.forward, _aimForward) > Cfg.MaxAimAngle.Value)
                _aimForward = Vector3.RotateTowards(t.forward, _aimForward, Cfg.MaxAimAngle.Value * Mathf.Deg2Rad, 0f).normalized;

            // Per-frame diagnostics. The "snaps to 0 / can't leave centre" question is: does the marker
            // sit still while the nose eats it (plane-driven decay), or does the marker itself fail to move?
            //   markerOff  : current nose->marker angle (the residual we're steering on)
            //   markerMoved: how far the marker rotated in WORLD since last Update (mouse + clamp)
            //   noseMoved  : how far the NOSE rotated in world since last Update (plane chasing in) — if this
            //                tracks markerOff shrinking, the plane is eating the offset (one-frame arrival)
            //   clamp      : how much the cone clamp pulled the marker back toward the nose this frame
            if (Cfg.DebugLogging.Value && Time.time - _lastAimLog >= 0.2f)
            {
                float markerOff   = Vector3.Angle(t.forward, _aimForward);
                float markerMoved = _prevAimDbg  == Vector3.zero ? 0f : Vector3.Angle(_aimForward, _prevAimDbg);
                float noseMoved   = _prevNoseDbg == Vector3.zero ? 0f : Vector3.Angle(t.forward, _prevNoseDbg);
                float clamp       = Vector3.Angle(preClamp, _aimForward);
                bool  active      = raw.sqrMagnitude > 1e-6f || markerOff > 0.02f || markerMoved > 0.01f || noseMoved > 0.01f;
                if (active)
                {
                    _lastAimLog = Time.time;
                    TryGetContext(out _, out var dcam);
                    float camOff = dcam != null ? Vector3.Angle(dcam.transform.forward, t.forward) : -1f;
                    WTMouseAimPlugin.Log.LogInfo(
                        $"[aim] t={Time.time:0.000} f={Time.frameCount} raw=({raw.x:0.000},{raw.y:0.000}) " +
                        $"markerOff={markerOff:0.000}deg markerMoved={markerMoved:0.000} noseMoved={noseMoved:0.000} " +
                        $"clamp={clamp:0.000} camOff={camOff:0.00} cap={_captured}");
                }
            }
            _prevAimDbg  = _aimForward;
            _prevNoseDbg = t.forward;
        }

        // Raw hardware mouse delta via Win32 — works from frame 1 with no alt-tab, unlike Unity's
        // focus-gated legacy axis. Read the OS cursor, take its delta from the primary-screen centre,
        // then warp it back to centre so the next frame measures from the same origin and big sweeps
        // never clamp at the desktop edge. Screen Y is +down, so we flip it to +up. The factor brings
        // the pixel delta onto Unity's legacy-axis scale so MouseSensitivity is read-backend independent.
        private static Vector2 ReadMouseDelta()
        {
            int cx = GetSystemMetrics(SM_CXSCREEN) / 2;
            int cy = GetSystemMetrics(SM_CYSCREEN) / 2;
            if (!GetCursorPos(out POINT p))
            {
                SetCursorPos(cx, cy);
                return Vector2.zero;
            }
            float dx = p.X - cx;
            float dy = p.Y - cy;
            SetCursorPos(cx, cy);                 // recenter for the next frame
            if (_captureFresh)
            {
                _captureFresh = false;            // first captured frame is just the warp-to-centre; ignore it
                return Vector2.zero;
            }
            return new Vector2(dx, -dy) * PixelToAxis;
        }

        // Three cursor regimes, scoped so each one cooperates with what reads the mouse:
        //   aimCapture (cockpit mouse-aim) — WE own the cursor: unlocked + hidden, and our Win32
        //              recenter produces the raw delta. lockState=None is essential: Locked would let
        //              Unity warp the cursor to centre too and zero our GetCursorPos delta.
        //   flyHidden  (3rd-person/orbit, or Free Look held) — the game's own flying regime: hidden +
        //              Locked. Orbit free-look only reads the look axes while the cursor is hidden, so
        //              this is what keeps 3p free-look alive. We do NOT touch Win32 here.
        //   visible    (menu/pause/UI) — a normal pointer.
        private static void ApplyCursorRegime(bool flyHidden, bool aimCapture)
        {
            _managing = true;
            if (aimCapture && !_captured) _captureFresh = true; // drop the recenter-warp jump as aiming starts
            _captured = aimCapture;
            if (aimCapture)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = false;
            }
            else if (flyHidden)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            else
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        // Hand the cursor back to the game when we stop managing it (no aircraft / mod off / scene change).
        // Leave a normal pointer as a safe default, then let CursorManager re-assert its own state.
        private static void ReleaseCursor()
        {
            if (!_managing) return;
            _managing = false;
            _captured = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            CursorManager.Refresh();
        }

        // Is the aim reticle currently frozen? True while the player holds the game's bound "Free Look"
        // OR (when RightClickFreeze is on) the right mouse button. While frozen the marker stops following
        // the mouse and stays parked in the world — the plane keeps flying to it while the camera free-
        // looks around (War Thunder–style). Same test drives both camera patches, so cockpit and orbit
        // agree on when to hand the view back to the game's free-look.
        public static bool AimFrozen()
        {
            var pi = GameManager.playerInput;
            bool freeLook = pi != null && pi.GetButton("Free Look");
            return freeLook || (Cfg.RightClickFreeze.Value && Input.GetMouseButton(1));
        }

        // True when we have a local aircraft and a main camera — in ANY camera mode. (The marker keeps
        // flying and is drawn in external/orbit views too; only the mouse-driven nudge is cockpit-only,
        // gated separately in Update.)
        public static bool TryGetContext(out Aircraft ac, out Camera cam)
        {
            ac = null; cam = null;
            var csm = SceneSingleton<CameraStateManager>.i;
            if (csm == null || csm.mainCamera == null)
                return false;
            if (!GameManager.GetLocalAircraft(out ac) || ac == null)
                return false;
            cam = csm.mainCamera;
            return true;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The seam patch. PlayerAxisControls runs each FixedUpdate (then Aircraft.FilterInputs).
    //   PREFIX  — when we're actively flying a fixed-wing, return false to SKIP the native body
    //             entirely. That's the fix for "the controls fight each other": the native mouse
    //             virtual-joystick / keyboard never writes the stick while we own it.
    //   POSTFIX — always runs (Harmony runs postfixes even when a prefix skips the original). When
    //             active it writes our chase output; on disengage it briefly ramps our last output
    //             back toward the native stick so the handoff is smooth.
    [HarmonyPatch(typeof(PilotPlayerState), "PlayerAxisControls")]
    internal static class PilotPlayerStatePatch
    {
        private static int _lastAircraftId;

        // Return false => skip native PlayerAxisControls (we own the stick this frame).
        private static bool Prefix(PilotPlayerState __instance)
        {
            if (!TryResolve(__instance, out var aircraft, out bool fixedWing, out float pilotStrength))
                return true; // can't resolve — let native run

            int id = aircraft.GetInstanceID();
            if (id != _lastAircraftId)
            {
                _lastAircraftId = id;
                string name = aircraft.definition != null ? aircraft.definition.name : "<unknown>";
                WTMouseAimPlugin.Log.LogInfo(
                    $"[seam] now flying '{name}' — fixedWing={fixedWing} (takeoffDistance={aircraft.GetAircraftParameters().takeoffDistance:0.##}).");
            }

            bool active = ChaseController.BeginFrame(aircraft, fixedWing, pilotStrength);

            // Skip the native body ONLY in the cockpit. There, native's mouse virtual-joystick would
            // fight us for the stick, so we own it outright. In external/orbit views we let native RUN —
            // it also processes the view / free-look axes, and skipping it killed 3rd-person free-look.
            // Our postfix still overwrites the flight controls afterward, so the plane keeps chasing the
            // marker either way; we just no longer steal the view processing in external cameras.
            bool ownStick = active && CameraStateManager.cameraMode == CameraMode.cockpit;
            return !ownStick;
        }

        private static void Postfix(PilotPlayerState __instance)
        {
            if (TryResolve(__instance, out var aircraft, out _, out _))
                ChaseController.Apply(aircraft);
        }

        private static bool TryResolve(PilotPlayerState inst, out Aircraft aircraft, out bool fixedWing, out float pilotStrength)
        {
            aircraft = null; fixedWing = false; pilotStrength = 1f;
            // PilotPlayerState never populates base.aircraft (it uses pilot.aircraft directly and
            // skips base.Initialize), so read the `pilot` field and go through public Pilot.aircraft.
            var pilot = Traverse.Create(inst).Field("pilot").GetValue<Pilot>();
            if (pilot == null) return false;
            aircraft = pilot.aircraft;
            if (aircraft == null) return false;
            fixedWing = aircraft.GetAircraftParameters().takeoffDistance > 0f;
            // GLOC: native zeroes inputs when pilotStrength < 0.2 (blackout). Read it so we can defer.
            pilotStrength = Traverse.Create(inst).Field("pilotStrength").GetValue<float>();
            return true;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Maneuver recorder (v0.35). A hotkey-gated, bounded high-rate capture of the control state to its
    // OWN timestamped CSV (one file per recording, next to LogOutput.log) — so a feel problem can be
    // recorded cleanly across several aircraft and the reactive assist calibrated against real numbers,
    // without the always-on [anomaly] log or the verbose [chase] trace. Start/stop with RecordKey.
    // All file IO is guarded: a failure aborts the recording and never throws into the game loop.
    internal static class ManeuverRecorder
    {
        private static System.IO.StreamWriter _w;   // open while recording, null otherwise
        private static float _startTime;            // Time.time at start (elapsed + summary)
        private static float _lastSample;           // throttle stamp (Time.time of last written row)
        private static int   _samples;              // rows written this recording
        private static string _path;                // current file path (for the summary line)

        public static bool IsRecording => _w != null;
        public static int  Samples     => _samples;
        public static float Elapsed    => IsRecording ? Time.time - _startTime : 0f;

        // CSV header — keep in lockstep with the Sample() row below.
        private const string Header =
            "t,off,azErr,elevErr,phi,bigTurn,bank,targetBank,outP,outR,outY," +
            "pitchRate,yawRate,rollRate,yawEff,yawWeak,spd,aoa,g,phase,flyLevel,engP,engR,engY";

        // Toggle on the hotkey. Returns the new state (true = now recording) for the on-screen toast.
        public static bool Toggle()
        {
            if (IsRecording) { Stop("toggled off"); return false; }
            return Start();
        }

        private static bool Start()
        {
            try
            {
                string dir  = BepInEx.Paths.BepInExRootPath; // folder that holds LogOutput.log
                string name = "mouseaim-rec-" + System.DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv";
                _path = System.IO.Path.Combine(dir, name);
                _w = new System.IO.StreamWriter(_path, false) { AutoFlush = true };
                _w.WriteLine(Header);
                _startTime  = Time.time;
                _lastSample = -999f; // force the first frame to sample
                _samples    = 0;
                WTMouseAimPlugin.Log.LogInfo($"[rec] recording -> {_path}");
                return true;
            }
            catch (System.Exception e)
            {
                WTMouseAimPlugin.Log.LogWarning($"[rec] could not start recording: {e.Message}");
                CloseQuietly();
                return false;
            }
        }

        // Stop and close, emitting a one-line summary. Safe to call when not recording (no-op).
        public static void Stop(string reason)
        {
            if (_w == null) return;
            float dur = Time.time - _startTime;
            int   n   = _samples;
            string path = _path;
            CloseQuietly();
            WTMouseAimPlugin.Log.LogInfo($"[rec] done ({reason}) dur={dur:0.0}s samples={n} -> {path}");
        }

        private static void CloseQuietly()
        {
            try { _w?.Flush(); _w?.Dispose(); } catch { /* ignore */ }
            _w = null;
        }

        // Write one row if recording and the per-second throttle (RecordRateHz) allows it. Called from
        // ChaseController.Apply with the already-computed control state — no recompute. A write failure
        // stops the recording cleanly rather than throwing.
        public static void Sample(
            float off, float azErr, float elevErr, float phi, float bigTurn, float bank, float targetBank,
            float outP, float outR, float outY, float pitchRate, float yawRate, float rollRate,
            float yawEff, float yawWeak, float spd, float aoa, float g, string phase, bool flyLevel,
            float engP, float engR, float engY)
        {
            if (_w == null) return;
            float now = Time.time;
            float minDt = 1f / Mathf.Clamp(Cfg.RecordRateHz.Value, 1f, 1000f);
            if (now - _lastSample < minDt) return;
            _lastSample = now;
            try
            {
                _w.WriteLine(
                    $"{now:0.000},{off:0.00},{azErr:0.00},{elevErr:0.00},{phi:0.0},{bigTurn:0.000}," +
                    $"{bank:0.0},{targetBank:0.0},{outP:0.000},{outR:0.000},{outY:0.000}," +
                    $"{pitchRate:0.000},{yawRate:0.000},{rollRate:0.000},{yawEff:0.000},{yawWeak:0.000}," +
                    $"{spd:0.0},{aoa:0.00},{g:0.00},{phase},{(flyLevel ? 1 : 0)},{engP:0.0},{engR:0.0},{engY:0.0}");
                _samples++;
            }
            catch (System.Exception e)
            {
                WTMouseAimPlugin.Log.LogWarning($"[rec] write failed, stopping: {e.Message}");
                CloseQuietly();
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The chase controller (thin "instructor"). Point-and-chase law from brihernandez/MouseFlight
    // (MIT), spec §3.1/3.2: compute the marker direction in the body frame, pitch/yaw toward it, and
    // blend roll between "bank toward target" (far off) and "wings level" (on target). We fully OWN
    // the stick while active (native is skipped); only a short ramp on disengage. The game's FBW/
    // AutoTrimmer limit G/AoA downstream, so no integral term — just P + a slew-rate limit.
    internal static class ChaseController
    {
        private static bool  _active;     // owning the stick this frame
        private static bool  _wasActive;  // last frame (edge detection)
        private static float _outP, _outR, _outY;
        private static float _disRamp;    // 1->0 disengage blend
        private static Vector3 _prevFwd;  // last frame's nose direction (for pitch/yaw rate damping)
        private static Vector3 _prevUp;   // last frame's up axis (for roll-rate damping)
        private static bool  _prevFwdValid;
        private static float _lastChaseLog; // throttle the [chase] trace to ~5/sec

        // Per-axis manual override-on-touch state. _eng* is 0 (mouse-aim owns the axis) .. 1 (you own it);
        // _mApply* freezes the manual value at release so the axis eases back to chase, not to zero.
        private static float _engP, _engR, _engY;
        private static float _mApplyP, _mApplyR, _mApplyY;
        private static Player _rewired;   // cached Rewired player 0 (same one PilotPlayerState reads)

        // Fine-regime integrator state (v0.24). The game's FBW is a rate-command law, so a proportional
        // outer loop asymptotes and parks short; these wind in the steady bias that closes the last bit.
        private static float _iPitch, _iYaw;

        // Fly Level autopilot (v0.24). When active, the chase ignores the marker and flies straight-and-
        // level at the heading captured on toggle-on (horizontal projection of the nose at that instant).
        public static bool FlyLevelActive;
        private static Vector3 _levelHeading = Vector3.forward; // world-space, horizontal, unit

        // Anomaly detection state (v0.25). Event-only logger: each detector keeps a little rolling state
        // and emits ONE [anomaly] line on the triggering frame, with a per-type cooldown.
        private static float _offMin = float.MaxValue;          // closest approach during the current command (overshoot)
        private static float _prevOff;                          // last frame's off (closing test)
        private static float _huntWinStart;                     // start of the 1 s sign-flip window
        private static int   _flipsP, _flipsY;                  // output sign-flips this window (hunt)
        private static float _prevSignP, _prevSignY;            // last non-trivial output sign per axis
        private static float _missTimer;                        // seconds off has stayed high while saturated (no progress)
        private static float _missAnchorOff;                    // off captured at the start of the current stall window
        private static float _yawWagWinStart;                   // start of the low-speed yaw-wag window
        private static int   _yawWagFlips;                      // yaw output sign-flips this window (low-speed wag)
        private static float _prevSignYW;                       // last non-trivial yaw output sign (wag counter)
        private static float _wobbleWinStart;                   // start of the high-speed roll-wobble window
        private static int   _wobbleFlips;                      // roll output sign-flips this window (high-speed wobble)
        private static float _prevSignRW;                       // last non-trivial roll output sign (wobble counter)
        private static float _anOvershootT, _anOverRollT, _anHuntT, _anMissT, _anYawWagT, _anWobbleT; // per-type cooldown stamps

        // Anomaly index + on-screen flash (v0.25.2). Every anomaly that clears its cooldown gets the next
        // sequential number (monotonic across the whole session, so #N is unambiguous even across respawns).
        // OnGUI flashes "#N type" for AnomalyFlashSec so you can call out which one felt wrong while flying.
        public  const  float AnomalyFlashSec = 4f;              // how long the on-screen index stays up (or until the next)
        private static int   _anomalyIndex;                     // running count of fired anomalies this session
        public  static int   LastAnomalyIndex;                  // surfaced to OnGUI
        public  static string LastAnomalyType = "";             // surfaced to OnGUI
        public  static float LastAnomalyTime  = -999f;          // Time.time of the last fired anomaly

        // Intent instrumentation (v0.26). The instructor classifies each frame into a ChasePhase so the
        // plan is legible on the HUD ("PHASE: ALIGN"), and emits ONE [maneuver] summary line per completed
        // turn — the "how did the planned path actually work out" record. Phase is surfaced always; the
        // maneuver tracker only runs while AnomalyLogging is on (it reuses the overshoot flag).
        public  static string LastPhase = "";                   // surfaced to OnGUI + the [anomaly] line
        private static bool   _manvActive;                      // a maneuver (off rose above AlignAngle) is in progress
        private static float  _manvStartT, _manvStartOff, _manvPeakOff; // start time / start & peak off
        private static float  _manvAlignT, _manvCaptureT;       // sec to first |phi|<20deg / first off<FineAngle (-1 = not yet)
        private static float  _manvPeakBank, _manvPeakG, _manvPeakRoll; // peak |bank| / |g| / |rollRate| over the maneuver
        private static float  _manvSettle;                      // sec off has stayed under FineAngle (capture-settle timer)
        private static bool   _manvOvershot;                    // an overshoot anomaly fired during this maneuver

        // Recent-frame ring buffer (v0.25.1): every FixedUpdate we stash a compact state snapshot here but
        // log NOTHING. When a detector fires, DumpTrail emits the last ~20 frames as a single [anomaly:trail]
        // line so the lead-UP to the event is visible (how the wag built / how the bank blew past) without
        // any continuous spam. Formatting only happens on a real anomaly, so the buffer itself is free.
        private struct AnFrame { public float t, off, bank, tgtBank, p, r, y, yr, rr, rf, spd, g; }
        private static readonly AnFrame[] _ring = new AnFrame[64];
        private static int   _ringHead;     // next write index
        private static int   _ringCount;    // valid entries (<= _ring.Length)
        private static float _lastTrailT;   // one trail dump per second across all anomaly types
        private static float _rollRateFilt;    // low-pass-filtered roll rate feeding the damping term (anti high-speed roll PIO)

        // Measured-reactive yaw-weakness estimate (v0.35). _yawEffFilt is a low-passed raw measure of how
        // much nose-yaw rate each unit of yaw command is buying (deg/s per stick); _yawWeak in [0,1] rises
        // when the rudder is empirically failing to CLOSE the heading error despite being commanded toward
        // it, and decays toward 0 otherwise. It has memory (the LPF) so it pre-biases the next nudge in the
        // same regime. _prevAzErr feeds the heading-closing rate. Reset on engage with the other detectors.
        private static float _yawWeak;          // 0 = rudder is working, 1 = rudder ineffective -> bank-and-pull
        private static float _yawEffFilt;       // low-passed |yawRate|/|outY| (diagnostic, logged to the CSV)
        private static float _prevAzErr;        // last frame's azimuth error (deg) for d|azErr|/dt
        private static bool  _prevAzErrValid;   // skip the first frame's bogus derivative
        private static float _closeRateFilt;    // low-passed heading-closing rate (deg/s) — noise-robust derivative

        public static bool IsFlying => _active;

        // Toggle Fly Level. On engage, latch the current horizontal heading so we hold THIS course (not
        // wherever the nose happens to drift). Capturing the heading here keeps Apply() purely reactive.
        public static void ToggleFlyLevel(Aircraft aircraft)
        {
            FlyLevelActive = !FlyLevelActive;
            if (FlyLevelActive && aircraft != null)
            {
                Vector3 f = aircraft.transform.forward;
                Vector3 h = new Vector3(f.x, 0f, f.z);
                // Degenerate (pointing near straight up/down): fall back to current velocity heading, else
                // keep whatever we last held so we never latch a zero vector.
                if (h.sqrMagnitude < 1e-4f && aircraft.rb != null)
                    h = new Vector3(aircraft.rb.velocity.x, 0f, aircraft.rb.velocity.z);
                if (h.sqrMagnitude >= 1e-4f) _levelHeading = h.normalized;
                _iPitch = _iYaw = 0f; // don't carry marker-chase windup into the level hold
            }
            if (Cfg.DebugLogging.Value)
                WTMouseAimPlugin.Log.LogInfo($"[flylevel] {(FlyLevelActive ? "ON  hdg=" + _levelHeading.ToString("0.00") : "OFF")}");
        }

        // Latest pilot G-tolerance (PilotPlayerState.pilotStrength), 1 = fine, <0.2 = blacked/redded out
        // and the game has zeroed all stick input. Surfaced for the overlay's G-LOC warning. Seeded to 1
        // so we never flash the warning before the first read.
        public static float PilotStrength { get; private set; } = 1f;

        // The instructor's own slewed stick command this frame (before any manual override blends on top),
        // surfaced for the top-left debug readout: "what the instructor is saying". 0 when not flying.
        public static float LastPitch { get; private set; }
        public static float LastRoll  { get; private set; }
        public static float LastYaw   { get; private set; }

        // The player's manual stick/keyboard/pedal source. Null until Rewired is ready.
        private static Player RewiredPlayer()
        {
            if (_rewired == null && ReInput.isReady)
                _rewired = ReInput.players.GetPlayer(0);
            return _rewired;
        }

        // Override-on-touch for one axis: push past the deadzone and you INSTANTLY take that axis
        // (engagement->1, manual value frozen); release and engagement eases 1->0 over ManualReturnTime so
        // the axis hands smoothly back to the still-running chase output. Returns the blended command.
        private static float BlendManual(float manual, float chase, ref float eng, ref float applied,
                                         float dz, float ret, float dt)
        {
            manual = Mathf.Clamp(manual, -1f, 1f);
            if (Mathf.Abs(manual) > dz) { applied = manual; eng = 1f; }   // instant grab
            else eng = Mathf.MoveTowards(eng, 0f, dt / Mathf.Max(0.01f, ret)); // ease back to mouse-aim
            return Mathf.Lerp(chase, applied, eng);
        }

        // Called from the prefix. Returns true if WE own the stick (native should be skipped).
        public static bool BeginFrame(Aircraft aircraft, bool fixedWing, float pilotStrength)
        {
            PilotStrength = pilotStrength; // surface for the overlay's G-LOC warning (runs every FixedUpdate)
            // NOTE: deliberately NOT gated on Guards.MenusOpen(). While a menu/map is up the sim keeps
            // running, and we want the instructor to keep flying the plane toward the frozen marker
            // (where you last aimed) instead of disengaging and flying straight. The mouse is frozen
            // separately in AimRig, so the aim direction holds; only the autopilot keeps tracking it.
            // NOTE: also deliberately NOT gated on camera mode. If you aim somewhere in the cockpit and
            // then switch to an external/orbit camera, the instructor keeps flying the plane toward that
            // marker (the reticle is drawn in those views too). Only the mouse-driven aiming is cockpit-
            // only; the chase itself runs in any view.
            bool active =
                Cfg.Enabled.Value &&
                Cfg.WriteControl.Value &&
                (fixedWing || Cfg.ControlRotorcraft.Value) &&  // collective aircraft (helis/VTOLs) only when opted in
                pilotStrength >= 0.2f &&                 // not blacked out
                aircraft.cockpit != null && !aircraft.cockpit.IsDetached();

            if (active && !_wasActive)
            {
                // Engage: seed our output from the current (native) stick so the takeover is smooth,
                // and hide the native virtual-joystick crosshair so it can't compete for the mouse.
                var ci = aircraft.GetInputs();
                _outP = ci.pitch; _outR = ci.roll; _outY = ci.yaw;
                _iPitch = _iYaw = 0f;  // fresh integrator on each engage
                _offMin = float.MaxValue; _prevOff = 0f; _missTimer = 0f; _missAnchorOff = 0f; // fresh anomaly detectors
                _flipsP = _flipsY = 0; _prevSignP = _prevSignY = 0f; _huntWinStart = 0f;
                _yawWagFlips = 0; _prevSignYW = 0f; _yawWagWinStart = 0f;
                _wobbleFlips = 0; _prevSignRW = 0f; _wobbleWinStart = 0f;
                _manvActive = false; _manvSettle = 0f; _manvOvershot = false; LastPhase = ""; // fresh maneuver tracker
                _ringHead = _ringCount = 0; // clear the context buffer for this command
                _prevFwdValid = false; // don't compute a huge rotation rate across the engage gap
                _yawWeak = 0f; _yawEffFilt = 0f; _closeRateFilt = 0f; _prevAzErrValid = false; // fresh yaw-weakness estimate
                HideNativeVirtualJoystick();
                WTMouseAimPlugin.Log.LogInfo($"WT Mouse Aim: ON ({(fixedWing ? "fixed-wing" : "rotorcraft")}) — chase control engaged.");
            }
            else if (!active && _wasActive)
            {
                _disRamp = 1f; // begin smooth handoff back to native
                WTMouseAimPlugin.Log.LogInfo("WT Mouse Aim: OFF — native control.");
            }

            _active = active;
            _wasActive = active;
            return active;
        }

        // Called from the postfix every frame (native may or may not have run).
        public static void Apply(Aircraft aircraft)
        {
            var ci = aircraft.GetInputs();
            if (ci == null) return;
            float dt = Time.fixedDeltaTime;

            if (_active)
            {
                Transform t = aircraft.transform;

                // The direction the instructor is flying the nose toward. Normally the world-locked aim
                // marker; in Fly Level mode it's the latched heading flown as TRUE level — see below.
                bool flyLevel = Cfg.FlyLevelEnabled.Value && FlyLevelActive;
                Vector3 aimDir;
                if (flyLevel)
                {
                    // "True level" = the VELOCITY VECTOR on the horizon (zero climb rate), not the nose.
                    // The plane flies at +AoA, so a nose-on-horizon target would leave the flight path
                    // descending by the AoA (the old behaviour — and the source of the unexplained
                    // sink/overshoot). Pitch the locked horizontal heading UP by the live angle-of-attack
                    // so that, once settled, the velocity vector sits level. AoA is the signed angle of the
                    // velocity off the nose about the body-right axis — the exact quantity the game's own
                    // RelaxedStabilityController reads (TargetCalc.GetAngleOnAxis, decompiled). Guard a
                    // near-zero velocity (no meaningful AoA) and clamp so a stall spike can't pitch wildly.
                    float aoaDeg = 0f;
                    if (aircraft.rb != null && aircraft.rb.velocity.sqrMagnitude > 4f)
                        aoaDeg = Mathf.Clamp(
                            TargetCalc.GetAngleOnAxis(t.forward, aircraft.rb.velocity, t.right), -20f, 20f);
                    // Rotate the horizontal heading up about world-right-of-heading by the AoA, so the nose
                    // leads the (level) velocity vector by exactly the AoA.
                    Vector3 hRight = Vector3.Cross(Vector3.up, _levelHeading); // points to the right of the course
                    aimDir = hRight.sqrMagnitude > 1e-6f
                        ? (Quaternion.AngleAxis(-aoaDeg, hRight) * _levelHeading).normalized
                        : _levelHeading;
                }
                else aimDir = AimRig.AimForward;

                // Marker direction in the body frame (unit): x = right, y = up, z = forward.
                Vector3 local = t.InverseTransformDirection(aimDir);
                float off = Vector3.Angle(t.forward, aimDir); // degrees the nose is off the target
                float sens = Cfg.PitchYawSensitivity.Value;

                // Nose rotation rate (world delta of the forward axis), split into the body up/right
                // components: how fast the nose is currently pitching up and yawing right. This is the
                // PLANE'S own motion (independent of the mouse), so damping on it never fights a flick.
                float pitchRate = 0f, yawRate = 0f, rollRate = 0f, noseTurnDeg = 0f;
                if (_prevFwdValid && dt > 1e-5f)
                {
                    Vector3 noseRate = (t.forward - _prevFwd) / dt;
                    pitchRate = Vector3.Dot(noseRate, t.up);    // +: nose swinging up
                    yawRate   = Vector3.Dot(noseRate, t.right); // +: nose swinging right
                    // Roll rate about the body forward axis: as the plane rolls right (right wing dropping)
                    // the up axis leans toward the right wing, so d(up)/dt . right > 0. Forward barely moves
                    // in a pure roll, so this needs the up axis, not noseRate.
                    rollRate  = Vector3.Dot((t.up - _prevUp) / dt, t.right); // +: rolling right
                    noseTurnDeg = Vector3.Angle(t.forward, _prevFwd); // total nose rotation this FixedUpdate
                }
                _prevFwd = t.forward;
                _prevUp  = t.up;
                _prevFwdValid = true;

                // PITCH/YAW: straight PROPORTIONAL pull toward the marker, MINUS a damping term on the
                // nose's own turn rate so the inputs fade out as it closes instead of overshooting (the
                // cure for the side-to-side wobble). In this game nose-up = NEGATIVE ci.pitch, so for an
                // above-nose marker -local.y already gives a pull-up command; +pitchRate*damp opposes the
                // climb as the nose arrives. The command tapers linearly to 0 as the offset closes, so the
                // nose decelerates into the marker instead of saturating full-deflection right up to the
                // crossing (which, with zero damping, was the limit-cycle hunt). Convergent (command(0)=0):
                // the nose still arrives and zeroes out — NOT a joystick.
                //
                // (v0.18's FineShape sqrt "fine assist" was removed in v0.19: it boosted small offsets so
                // hard that even a 6deg offset saturated the rudder, which is exactly what made it hunt.
                // v0.22 logs showed the opposite failure: at ~1deg of pure azimuth error the yaw P-term
                // was ~0.05 stick and the heading closed with a ~6 s time constant — "never aligns". The
                // FINE-CAPTURE boost below is the measured middle ground: a LINEAR gain ramp (max 1+boost
                // at zero offset, gone at FineAngle) instead of the sqrt's unbounded low-end slope, with
                // the rate damping and the output slew limiter still active against hunting.)
                float damp = Cfg.ChaseDamping.Value;
                float fineBlend = Mathf.Clamp01(1f - off / Mathf.Max(1f, Cfg.FineAngle.Value));
                float fineGain  = 1f + Cfg.FineGainBoost.Value * fineBlend;

                // BODY-FRAME ROLL-THEN-PULL (v0.26). One shared plan for all three axes: roll the lift
                // vector (body-up) onto the target, then pull up into it. Attitude-robust at any pitch (no
                // horizon-frame bank that degenerates when the nose is steep) and a pull is ALWAYS a pull
                // (no bunt). The target's bearing AROUND the boresight is the heart of it:
                //   phi       = where the target sits, measured from straight-up (the lift vector): 0 = dead
                //               above the nose, +90 = off the right wing, -90 = off the left, ±180 = below.
                //   alignFrac = signed lift-vector alignment: +1 target above, 0 to the side, -1 below.
                float phi       = Mathf.Atan2(local.x, local.y) * Mathf.Rad2Deg;
                float lateral   = Mathf.Sqrt(local.x * local.x + local.y * local.y);
                float alignFrac = lateral > 1e-4f ? local.y / lateral : 1f;

                // REGIME BLEND — a continuous ramp from the fine direct-nudge law (small errors) to the
                // roll-to-align law (big turns). 0 inside FineAngle, 1 at/above AlignAngle.
                float bigTurn = Mathf.Clamp01((off - Cfg.FineAngle.Value)
                                              / Mathf.Max(1f, Cfg.AlignAngle.Value - Cfg.FineAngle.Value));

                // WORLD-FRAME AZIMUTH ERROR (deg, + = marker right of the nose heading) — the heading the
                // rudder is asked to close. Drives the fine bank servo AND the v0.35 yaw-weakness estimate,
                // so it's computed up here ahead of the pitch/roll commands. Degenerate headings (nose
                // straight up/down) zero it for this frame.
                Vector3 aimHW  = new Vector3(aimDir.x, 0f, aimDir.z);
                Vector3 noseHW = new Vector3(t.forward.x, 0f, t.forward.z);
                float azErr = (aimHW.sqrMagnitude > 1e-6f && noseHW.sqrMagnitude > 1e-6f)
                    ? Vector3.SignedAngle(noseHW, aimHW, Vector3.up) : 0f;

                // MEASURED YAW-WEAKNESS (v0.35). Is the rudder actually CLOSING the heading? Watch the
                // standing azimuth error and how fast it's shrinking. A small side nudge the rudder handles
                // well (low speed) closes quickly -> weakness stays ~0, near-stock feel. The same nudge at
                // high speed lingers, because rudder/sideslip barely turns the flight path -> weakness rises
                // and the assist banks-and-pulls instead. Purely measured (no speed formula); the closing
                // rate is low-passed so frame-to-frame jitter doesn't fake progress. _yawEffFilt is a raw
                // |yawRate|/|outY| effectiveness, kept ONLY for the CSV so the estimator can be calibrated.
                // _outY here is last frame's committed yaw output (this frame's is slewed below).
                float yawEffInst = Mathf.Abs(_outY) > 0.1f ? Mathf.Abs(yawRate) / Mathf.Abs(_outY) : _yawEffFilt;
                _yawEffFilt += (dt / (0.5f + dt)) * (yawEffInst - _yawEffFilt);

                float closeRaw = _prevAzErrValid ? (Mathf.Abs(_prevAzErr) - Mathf.Abs(azErr)) / Mathf.Max(dt, 1e-4f) : 0f;
                _closeRateFilt += (dt / (0.2f + dt)) * (closeRaw - _closeRateFilt);
                bool manualNow = _engP > 0f || _engR > 0f || _engY > 0f;
                float weakInst = 0f;
                if (Cfg.YawAssistEnabled.Value && !manualNow && !flyLevel && Mathf.Abs(azErr) > 1.5f)
                    weakInst = 1f - Mathf.Clamp01(_closeRateFilt / 6f); // 0 = heading closing fast, 1 = stalled
                // Asymmetric low-pass: attack at YawAssistResponse, release ~4x slower so the estimate has
                // MEMORY — once the rudder is shown weak in this regime, the next nudge is already assisted.
                float wTau = weakInst > _yawWeak ? Cfg.YawAssistResponse.Value : Cfg.YawAssistResponse.Value * 4f;
                _yawWeak = Mathf.Clamp01(_yawWeak + (dt / (wTau + dt)) * (weakInst - _yawWeak));
                _prevAzErr = azErr; _prevAzErrValid = true;

                // ASSIST FACTOR — shift this side correction into bank-and-pull, strong only when the rudder
                // is measured weak AND we're in the small/mid regime. Fades out as bigTurn->1 (the v0.26
                // roll-to-align law already banks-and-pulls big turns), so it targets exactly the small-nudge
                // case that stalls on the rudder today.
                float assist = Cfg.YawAssistEnabled.Value ? _yawWeak * (1f - bigTurn) * Cfg.YawAssistStrength.Value : 0f;

                // FINE BANK SERVO target bank (v0.25), now assist-aware (v0.35): collapse the heading
                // deadband and bank harder when the rudder is weak, so a small side nudge actually banks to
                // turn instead of waiting on the rudder. Still capped at MaxBankAngle. Computed here (ahead
                // of pitch) so the coordinating pull below can size itself off the commanded bank.
                float azDz   = Cfg.FineBankDeadzone.Value * (1f - assist);
                float azBank = Mathf.Abs(azErr) <= azDz ? 0f : (Mathf.Abs(azErr) - azDz) * Mathf.Sign(azErr);
                // v0.36: when the rudder is measured weak, bank STEEPLY (BankAuthGain) so a small side
                // nudge commands a loaded-turn bank instead of the shallow one that mushes. This LINEAR
                // (bank proportional to error) servo is the low-speed / strong-yaw behaviour, left intact.
                float bankGain   = Cfg.FineBankGain.Value * (1f + Cfg.BankAuthGain.Value * assist);
                float linBank    = azBank * bankGain;
                // v0.37 — THE high-speed fix. The linear servo can't serve both 150 m/s and 400 m/s with
                // one slope: a given bank turns ~3x faster slow than fast, so the slope that nails the slow
                // plane leaves the fast one mushing the last few degrees (12deg bank @ 400 m/s ~ 0.3deg/s).
                // Size the bank from a target TURN-RATE instead: omega = k*azErr (rad/s), and the bank that
                // physically holds it is phi = atan(omega*V/g). V is in the formula, so the SAME error asks
                // for a steep loaded bank when fast and a gentle one when slow — automatically. Blend linear
                // -> turn-rate by measured yaw-weakness*(1-bigTurn): low speed / strong yaw keep the linear
                // servo exactly; weak-yaw/high-speed take the loaded bank. Self-rolls out as azErr -> 0.
                //   v0.37.1: the turn-rate path sees a NEAR-RAW error (a tiny fixed noise gate only), NOT the
                //   big anti-wobble deadzone azBank carries. With azBank the deadzone (~1-1.5deg in the tail)
                //   was subtracted before the turn-rate math, so a real 2deg error was seen as ~0.6deg, the
                //   commanded bank collapsed to ~25deg (max ~1.1g) and the loaded turn unwound at a CLIFF
                //   around 2deg — the v0.37 tail plateaued there. atan() on the raw error rolls out smoothly
                //   instead. The linear servo keeps its full deadzone (low-speed wobble guard) untouched.
                float vMag     = aircraft.rb != null ? aircraft.rb.velocity.magnitude : 200f;
                float azTR     = Mathf.Abs(azErr) <= 0.5f ? 0f : (Mathf.Abs(azErr) - 0.5f) * Mathf.Sign(azErr); // raw error, noise gate only
                float omegaDes = azTR * Mathf.Deg2Rad * Cfg.AssistTurnRateGain.Value;            // rad/s, signed
                float bankTR   = Mathf.Atan(omegaDes * Mathf.Max(50f, vMag) / 9.81f) * Mathf.Rad2Deg; // deg, signed
                float bankBlend  = Cfg.YawAssistEnabled.Value ? _yawWeak * (1f - bigTurn) : 0f;
                float targetBank = Mathf.Clamp(Mathf.Lerp(linBank, bankTR, bankBlend),
                                               -Cfg.MaxBankAngle.Value, Cfg.MaxBankAngle.Value);

                // PITCH ANTI-OVERSHOOT BRAKE (v0.25): extra rate damping that ramps in as the nose nears
                // the target (off small) while it's still swinging, so a fast large-angle pull (takeoff
                // climb-out) decelerates ONTO the marker instead of crossing it and settling back. Fades
                // out beyond ~25deg so it never slows the initial pull-in, and opposes RATE only so it
                // can't fight a settled attitude (vanishes once the nose stops turning).
                float brakeGate = Mathf.Clamp01(1f - off / 25f);
                float pitchDamp = damp + Cfg.PitchBrake.Value * brakeGate;

                // FINE-REGIME LEAKY INTEGRATOR (v0.24). The game's fly-by-wire reads our pitch/yaw as a
                // commanded ANGULAR RATE and PID-tracks it (decompiled ControlsFilter.FlyByWire), so a pure
                // proportional outer loop asymptotes and parks a fraction of a degree short — and on relaxed-
                // stability airframes RelaxedStabilityController dilutes small pitch toward an AoA-cancel.
                // This integrator winds in exactly the steady bias those two leave out. It's the same error
                // signal as the proportional term (so it reinforces), gated to the fine cone (fineBlend),
                // leaked toward zero so it can't run away, hard-capped, and suspended on manual override /
                // Fly Level. As the error -> 0 the steady value -> 0, so it stays convergent (no limit cycle).
                float iCap = Cfg.FineIntegralCap.Value;
                if (Cfg.FineIntegralGain.Value > 0f && iCap > 0f && !flyLevel)
                {
                    float ki = Cfg.FineIntegralGain.Value, leak = Cfg.FineIntegralLeak.Value;
                    if (_engP > 0f) _iPitch = 0f; // you own pitch — don't wind against your stick
                    else _iPitch = Mathf.Clamp(_iPitch + (-local.y * ki * fineBlend - _iPitch * leak) * dt, -iCap, iCap);
                    if (_engY > 0f) _iYaw = 0f;
                    else _iYaw   = Mathf.Clamp(_iYaw   + ( local.x * ki * fineBlend - _iYaw   * leak) * dt, -iCap, iCap);
                }
                else { _iPitch = _iYaw = 0f; }

                // PITCH — pull up toward the target, gated so a big turn only pulls once the lift vector is
                // ON it (and NEVER pushes: the gate is clamped at 0, killing the negative-G bunt the old
                // |local.y| symmetric coord term produced when a roll swung the target momentarily below the
                // nose). In the fine cone (bigTurn->0) the gate is 1 — the old direct pull, so a gentle
                // nose-down to a low marker is still allowed. -local.y is the pull command (nose-up = -pitch).
                float pullGate = Mathf.Lerp(1f, Mathf.Clamp01(alignFrac), Cfg.RollPitchCoordination.Value * bigTurn);
                // COORDINATING PULL (v0.35, reshaped v0.36) — the "pitch INTO the bank" half of the assist
                // and the REAL driver of the correction. Once banked, a level turn needs back-pressure or
                // gravity just drops the nose and the bank does nothing (the v0.35 tail mushed at 0.7-0.9g
                // with ~0 pull). Add a nose-up pull (nose-up = NEGATIVE pitch) proportional to the commanded
                // bank, scaled by assist. v0.37: the pull stays at FULL strength outside CoordPullReleaseAngle
                // (~2 deg) and only eases inside it — so the loaded turn holds its G right through the tail of
                // the correction (the v0.36 taper over the whole 6 deg fine cone was already half-gone at 3 deg,
                // so the nose mushed) and then releases cleanly onto aim. With the turn-rate bank above keeping
                // sin(targetBank) high in the tail, this is what actually loads the G. ALWAYS a pull: clamped
                // >= 0 (never a push/bunt) and capped (CoordPullCap). Convergent: -> 0 as azErr -> 0 (taper)
                // and as the bank rolls out.
                float pullTaper = Mathf.Clamp01(Mathf.Abs(azErr) / Mathf.Max(0.5f, Cfg.CoordPullReleaseAngle.Value));
                float coordPull = Mathf.Clamp(
                    Cfg.CoordPullGain.Value * Mathf.Abs(Mathf.Sin(targetBank * Mathf.Deg2Rad))
                    * pullTaper * assist,
                    0f, Cfg.CoordPullCap.Value);
                float tgtP = Mathf.Clamp((-local.y * sens * fineGain * pullGate + _iPitch + pitchRate * pitchDamp - coordPull) * Cfg.PitchGain.Value, -1f, 1f);

                // YAW — ease rudder authority down during a big turn (the logs showed yaw pinned ±1 adding to
                // the messy feel) so the bank + pull do the work; full authority returns in the fine cone for
                // final alignment. v0.36: ALSO fade the rudder P-term out as the assist rises (YawWeakFade) —
                // when the rudder is measured weak it's pure sideslip/drag doing nothing for the heading, so
                // commit to bank+pull instead. Keep the small capped _iYaw integrator for final low-speed
                // alignment (assist ~ 0 there, so this is identically stock at low speed).
                float yawScale = Mathf.Lerp(1f, Cfg.TurnYawScale.Value, bigTurn);
                float yawWeakFade = 1f - Cfg.YawWeakFade.Value * assist;
                float tgtY = Mathf.Clamp(( local.x * sens * fineGain * yawScale * yawWeakFade + _iYaw - yawRate * damp) * Cfg.YawGain.Value, -1f, 1f);

                // ROLL (v0.26): blend the FINE wings-level/azimuth bank servo (small errors) with a BODY-
                // FRAME roll-to-align (big turns), in matched sin-magnitude units so RollGain/RollDamping
                // keep their meaning. targetBank was computed up front (assist-aware bank servo) so the
                // coordinating pull could size off it; here it just becomes the roll error.
                //   eFine  — the v0.25 bank servo error: a target bank proportional to the heading (azimuth)
                //            error, capped at MaxBankAngle, null at that bank. t.right.y is the world-up
                //            component on the right wing (0 = level, <0 = right wing down), so
                //            (t.right.y + sin(targetBank)) is the bank error. Used inside the fine cone where
                //            the horizon bank is meaningful and the wings should level on-heading. v0.35: the
                //            deadband/gain that built targetBank are assist-aware, so when the rudder is weak
                //            a small side nudge banks instead of waiting on it (the wobble-guard deadband
                //            still applies at assist 0 — normal on-heading flight is unchanged).
                //   eAlign — roll the SHORT way to put the target at 12 o'clock: monotonic in phi (no false
                //            equilibrium except exactly ±180°, broken by any noise), so a target straight off
                //            the wing or below still rolls in the short way instead of pinning. This is
                //            attitude-robust where eFine degenerates (steep nose => meaningless horizon bank).
                // bigTurn blends fine->align; subtract the roll RATE (RollDamping) so it eases on without
                // overshooting. As the turn completes off shrinks, bigTurn->0, and the fine servo levels out.
                float eFine  = t.right.y + Mathf.Sin(targetBank * Mathf.Deg2Rad);
                float eAlign = Mathf.Clamp(phi / 90f, -1.5f, 1.5f);
                float rollErr = Mathf.Lerp(eFine, eAlign, bigTurn);

                // ROLL-RATE LOW-PASS (v0.31) — fix for the high-speed roll wobble. When level on-heading the
                // roll P-term (eFine) is ~0, so the command is essentially -rollRate*RollDamping*RollGain. The
                // rollRate is a one-frame finite difference (~60 Hz); at high dynamic pressure the airframe is
                // responsive enough that this DELAYED rate feedback flips from damping to DRIVING at ~6-7 Hz —
                // a self-sustaining limit cycle (logs: R=±0.05 tracking rr=±0.2, bank<0.2deg). Its amplitude is
                // set by the loop delay, not the gain, which is why the v0.30 qScale gain-cut to 0.35x left it
                // unchanged. Smoothing the rate (first-order LPF, time constant RollRateSmoothing) rolls off the
                // high-freq content so the damping only opposes real low-freq roll motion — breaking the cycle
                // while keeping turn damping. tau=0 -> raw rate (old behaviour).
                float rollTau = Cfg.RollRateSmoothing.Value;
                if (rollTau > 1e-4f) _rollRateFilt += (dt / (rollTau + dt)) * (rollRate - _rollRateFilt);
                else _rollRateFilt = rollRate;
                float rollRateF = _rollRateFilt;

                float tgtR = Mathf.Clamp((rollErr - rollRateF * Cfg.RollDamping.Value) * Cfg.RollGain.Value, -1f, 1f);

                // Slew-rate-limit the chase outputs (anti-jerk against mouse jitter / a fresh flick).
                // Symmetric on all three axes. _out* stay PURE chase values — manual override blends on
                // TOP of them below, so when you release a manual axis it hands back to the live chase.
                float slew = Cfg.OutputSlew.Value * dt;
                _outP = Mathf.MoveTowards(_outP, tgtP, slew);
                _outY = Mathf.MoveTowards(_outY, tgtY, slew);
                _outR = Mathf.MoveTowards(_outR, tgtR, slew);
                LastPitch = _outP; LastYaw = _outY; LastRoll = _outR; // surface for the top-left readout

                // Manual override-on-touch (per axis) — ALL THREE AXES IDENTICAL, roll included: when you
                // push an axis past the deadzone your input takes it over instantly; when you're not
                // touching it, the instructor flies it. Pitch/yaw hand back to their proportional pull;
                // roll hands back to the instructor's bank-to-turn (which levels the wings on-target). The
                // chase keeps running underneath (_out*), so the handback is seamless on every axis.
                float pOut = _outP, rOut = _outR, yOut = _outY;
                if (Cfg.ManualOverride.Value)
                {
                    var pl = RewiredPlayer();
                    if (pl != null)
                    {
                        float dz  = Cfg.ManualDeadzone.Value;
                        float ret = Cfg.ManualReturnTime.Value;
                        pOut = BlendManual(pl.GetAxis("Pitch"), _outP, ref _engP, ref _mApplyP, dz, ret, dt);
                        rOut = BlendManual(pl.GetAxis("Roll"),  _outR, ref _engR, ref _mApplyR, dz, ret, dt);
                        yOut = BlendManual(pl.GetAxis("Yaw"),   _outY, ref _engY, ref _mApplyY, dz, ret, dt);
                        // A stick/pedal nudge drops Fly Level — you've taken the controls back.
                        if (FlyLevelActive && (_engP >= 1f || _engR >= 1f || _engY >= 1f))
                            ToggleFlyLevel(aircraft);
                    }
                }
                else { _engP = _engR = _engY = 0f; }

                ci.pitch = Mathf.Clamp(pOut, -1f, 1f);
                ci.roll  = Mathf.Clamp(rOut, -1f, 1f);
                ci.yaw   = Mathf.Clamp(yOut, -1f, 1f);
                _disRamp = 1f; // primed for the next disengage

                // Current bank (deg, + = right wing down) — shared by the anomaly detectors and the trace.
                float bank = -Mathf.Asin(Mathf.Clamp(t.right.y, -1f, 1f)) * Mathf.Rad2Deg;

                // PHASE (v0.26): classify the instructor's plan this frame so it's legible on the HUD and in
                // the [anomaly] line. The plan is "roll the lift vector onto the target, then pull up into it".
                LastPhase = ClassifyPhase(flyLevel, off, phi, bigTurn, noseTurnDeg);

                // ANOMALY DETECTION (v0.25): event-only logger. Stays silent in normal flight and fires a
                // single [anomaly] line when a command misbehaves (overshoot / over-roll / hunt / miss).
                // This is the cheap-to-hand-back logger; the verbose [chase] trace below stays off by default.
                // TrackManeuver runs alongside it (reuses the overshoot flag) and emits one [maneuver] summary
                // per completed turn — the "how did the planned path actually work out" record.
                if (Cfg.AnomalyLogging.Value)
                {
                    DetectAnomalies(aircraft, off, bank, targetBank, bigTurn, yawRate, rollRate, dt);
                    TrackManeuver(aircraft, off, phi, bank, rollRate, dt);
                }

                // MANEUVER RECORDER (v0.35): when the user has armed a capture (RecordKey), write the live
                // control state to the CSV at RecordRateHz. Reuses everything already computed this frame —
                // no recompute. Throttling/IO live inside Sample(); it's a no-op when not recording.
                if (ManeuverRecorder.IsRecording)
                {
                    float elevErr = (Mathf.Asin(Mathf.Clamp(aimDir.y, -1f, 1f))
                                   - Mathf.Asin(Mathf.Clamp(t.forward.y, -1f, 1f))) * Mathf.Rad2Deg;
                    float spdR = aircraft.rb != null ? aircraft.rb.velocity.magnitude : -1f;
                    float aoaR = 0f;
                    if (aircraft.rb != null && aircraft.rb.velocity.sqrMagnitude > 4f)
                        aoaR = TargetCalc.GetAngleOnAxis(t.forward, aircraft.rb.velocity, t.right);
                    ManeuverRecorder.Sample(off, azErr, elevErr, phi, bigTurn, bank, targetBank,
                        _outP, _outR, _outY, pitchRate, yawRate, rollRate, _yawEffFilt, _yawWeak,
                        spdR, aoaR, aircraft.gForce, LastPhase, flyLevel, _engP, _engR, _engY);
                }

                // Chase trace. Normal cadence ~5/sec; inside 10deg ("fine capture") ~10/sec at higher
                // precision — the last-few-degrees stall is exactly what we're diagnosing.
                //   off       : nose->marker angle before this frame's command takes effect
                //   elevE/azE : world-frame error split (elevation vs azimuth, deg). A persistent elevE
                //               with azE~0 is gravity droop; a persistent azE is lateral residual.
                //   P/D (p/y) : the two additive terms inside tgt, per axis — proportional pull vs the
                //               rate-damping bite. If D ~cancels P while off stays put, the damping is
                //               strangling the final closure (the prime suspect).
                //   spd/bank/g: flight-state context for the residual.
                if (Cfg.DebugLogging.Value)
                {
                    bool fine = off < 10f; // fine-capture regime: the regime under investigation
                    if (Time.time - _lastChaseLog >= (fine ? 0.1f : 0.2f) &&
                        (off > 0.02f || Mathf.Abs(_outP) > 0.005f || Mathf.Abs(_outY) > 0.005f || Mathf.Abs(_outR) > 0.005f))
                    {
                        _lastChaseLog = Time.time;
                        float pTermP = -local.y * sens * fineGain * pullGate, dTermP = pitchRate * pitchDamp; // tgtP = (P+D)*PitchGain
                        float pTermY =  local.x * sens * fineGain * yawScale, dTermY = -yawRate  * damp;       // tgtY = (P+D)*YawGain
                        float elevE = (Mathf.Asin(Mathf.Clamp(aimDir.y, -1f, 1f)) - Mathf.Asin(Mathf.Clamp(t.forward.y, -1f, 1f))) * Mathf.Rad2Deg;
                        float spd  = aircraft.rb != null ? aircraft.rb.velocity.magnitude : -1f;
                        string f = fine ? "0.000" : "0.00";
                        WTMouseAimPlugin.Log.LogInfo(
                            $"[chase] t={Time.time:0.000} off={off:0.000}deg phi={phi:0.0} bigTurn={bigTurn:0.00} elevE={elevE:0.00} azE={azErr:0.00} noseTurn={noseTurnDeg:0.000} " +
                            $"fineG={fineGain:0.00} pull={pullGate:0.00} yawSc={yawScale:0.00} phase={LastPhase} tgtBank={targetBank:0.0} yawWeak={_yawWeak:0.00} assist={assist:0.00} coordPull={coordPull:0.000} iP/iY=({_iPitch.ToString(f)},{_iYaw.ToString(f)}){(flyLevel ? " LVL" : "")} " +
                            $"P(p/y)=({pTermP.ToString(f)},{pTermY.ToString(f)}) D(p/y)=({dTermP.ToString(f)},{dTermY.ToString(f)}) " +
                            $"tgt P/R/Y=({tgtP.ToString(f)},{tgtR.ToString(f)},{tgtY.ToString(f)}) " +
                            $"out P/R/Y=({_outP.ToString(f)},{_outR.ToString(f)},{_outY.ToString(f)}) " +
                            $"fin=({ci.pitch:0.00},{ci.roll:0.00},{ci.yaw:0.00}) man=({_engP:0.0},{_engR:0.0},{_engY:0.0}) " +
                            $"spd={spd:0} bank={bank:0.0} g={aircraft.gForce:0.0}");
                    }
                }
            }
            else if (_disRamp > 0f)
            {
                // Disengaging: native just wrote ci. Ramp from our last output back to native.
                _disRamp = Mathf.Max(0f, _disRamp - Cfg.AuthorityRamp.Value * dt);
                float a = _disRamp;
                ci.pitch = Mathf.Clamp(Mathf.Lerp(ci.pitch, _outP, a), -1f, 1f);
                ci.roll  = Mathf.Clamp(Mathf.Lerp(ci.roll,  _outR, a), -1f, 1f);
                ci.yaw   = Mathf.Clamp(Mathf.Lerp(ci.yaw,   _outY, a), -1f, 1f);
                LastPitch = LastYaw = LastRoll = 0f; // instructor not flying — readout reads zero
            }
            else { LastPitch = LastYaw = LastRoll = 0f; }
        }

        private static void HideNativeVirtualJoystick()
        {
            var fh = SceneSingleton<FlightHud>.i;
            if (fh != null && fh.virtualJoystickPos != null)
            {
                fh.SetVirtualJoystick(Vector3.zero);
                fh.virtualJoystickPos.gameObject.SetActive(false);
            }
        }

        // Event-only anomaly logger (v0.25). Each detector keeps small rolling state and fires at most
        // once per cooldown, writing a single compact [anomaly] line. Runs every FixedUpdate while flying,
        // so it catches the exact frame a command goes wrong without the per-frame [chase] spam.
        private static void DetectAnomalies(Aircraft ac, float off, float bank, float targetBank, float bigTurn, float yawRate, float rollRate, float dt)
        {
            float now = Time.time;
            float spdNow = ac.rb != null ? ac.rb.velocity.magnitude : -1f;

            // Stash this frame in the ring buffer FIRST (logs nothing; dumped only if an event fires below).
            _ring[_ringHead] = new AnFrame {
                t = now, off = off, bank = bank, tgtBank = targetBank,
                p = _outP, r = _outR, y = _outY, yr = yawRate, rr = rollRate, rf = _rollRateFilt, spd = spdNow, g = ac.gForce };
            _ringHead = (_ringHead + 1) % _ring.Length;
            if (_ringCount < _ring.Length) _ringCount++;

            // MANUAL-OVERRIDE GATE — when you're on the stick/pedals (any axis manually engaged, or easing back
            // after release) the airframe's attitude/rates are being driven by YOU, not the instructor, so the
            // chase law's reaction to your input would false-fire over-roll/wobble/overshoot/etc. Suspend all
            // detection while engaged and RESET the rolling windows so a flip count accrued during manual flight
            // doesn't fire the instant control hands back. The ring buffer above still fills, so an anomaly just
            // after handback keeps its pre-frames. _eng* are 0 whenever ManualOverride is off, so this is inert then.
            if (_engP > 0f || _engR > 0f || _engY > 0f)
            {
                _huntWinStart = _yawWagWinStart = _wobbleWinStart = now;
                _flipsP = _flipsY = _yawWagFlips = _wobbleFlips = 0;
                _prevSignP = _prevSignY = _prevSignYW = _prevSignRW = 0f;
                _missTimer = 0f;
                _offMin = off; _prevOff = off;
                return;
            }

            // OVERSHOOT — the nose closed toward the marker then the error grew back. Track the closest
            // approach; if off rebounds past it by AnomalyOvershootDeg after getting reasonably close
            // (<15deg), the nose crossed/passed the target. A large fresh command resets the baseline.
            if (off < _offMin) _offMin = off;
            if (_offMin < 5f && off > _offMin + Cfg.AnomalyOvershootDeg.Value)
            {
                Anomaly("overshoot", $"min={_offMin:0.0} rebound={(off - _offMin):0.0}deg", ref _anOvershootT, now, ac, off, bank);
                _manvOvershot = true; // mark the in-progress maneuver as having overshot (reused by [maneuver])
                _offMin = off; // start a fresh approach window
            }
            if (off > 40f) _offMin = off;

            // OVER-ROLL — actual bank overshot what the law actually asked for. GATED to the PURE fine regime
            // (bigTurn <= 0, i.e. off < FineAngle): only there does the fine bank servo alone govern roll, so
            // targetBank is the real commanded bank. Above FineAngle the v0.26 body-frame roll-to-align law
            // legitimately commands more bank than the azimuth servo would, so comparing to targetBank there
            // false-fires all through a normal turn (the bigTurn<0.5 gate let that happen — v0.26.1 fix). Also
            // require the bank to be DEEPENING (roll rate still increasing |bank|, same sign as bank) so a
            // turn-exit roll-out — bank momentarily above target but actively levelling — doesn't trip it.
            if (bigTurn <= 0f && Mathf.Abs(bank) > Mathf.Abs(targetBank) + Cfg.AnomalyOverRollDeg.Value
                && Mathf.Sign(rollRate) == Mathf.Sign(bank))
                Anomaly("over-roll", $"bank={bank:0.0} target={targetBank:0.0}", ref _anOverRollT, now, ac, off, bank);

            // HUNT — rapid output sign-flapping on pitch or yaw within a 1 s window while the error is NOT
            // meaningfully closing: a limit cycle / wing-rock rather than honest convergence.
            if (now - _huntWinStart > 1f) { _huntWinStart = now; _flipsP = _flipsY = 0; }
            CountFlip(_outP, ref _prevSignP, ref _flipsP);
            CountFlip(_outY, ref _prevSignY, ref _flipsY);
            if ((_flipsP >= 4 || _flipsY >= 4) && off > _prevOff - 0.5f)
            {
                Anomaly("hunt", $"flipsP={_flipsP} flipsY={_flipsY}", ref _anHuntT, now, ac, off, bank);
                _flipsP = _flipsY = 0; // consume the window so it doesn't re-fire every frame
            }

            // YAW-WAG (low speed) — the nose wags left/right on the takeoff roll / low-speed regime, where
            // rudder/aero authority is low and the cruise-tuned yaw loop over-corrects. Counts yaw OUTPUT
            // reversals in a 1 s window and flags repeated flapping while slow (independent of off, since the
            // wag happens while roughly on heading). Separate counter/window from the cruise "hunt" detector.
            if (now - _yawWagWinStart > 1f) { _yawWagWinStart = now; _yawWagFlips = 0; }
            CountFlip(_outY, ref _prevSignYW, ref _yawWagFlips);
            if (spdNow >= 0f && spdNow < Cfg.AnomalyLowSpeed.Value && _yawWagFlips >= 3)
            {
                Anomaly("yaw-wag", $"flips={_yawWagFlips} spd={spdNow:0}", ref _anYawWagT, now, ac, off, bank);
                _yawWagFlips = 0; // consume the window
            }

            // ROLL-WOBBLE (high speed) — a small roll limit-cycle: the bank rocks back and forth (the +/-0.1
            // roll output that flickers at high speed) because the cruise-tuned roll loop is over-effective at
            // high dynamic pressure. Counts ROLL output reversals in a 1 s window and flags repeated flapping
            // while fast AND roughly on-heading (small off), so an honest hard turn's roll-in/out doesn't trip
            // it. Separate counter/window from the yaw-wag and the pitch/yaw hunt detectors.
            if (now - _wobbleWinStart > 1f) { _wobbleWinStart = now; _wobbleFlips = 0; }
            CountFlip(_outR, ref _prevSignRW, ref _wobbleFlips);
            if (spdNow > Cfg.AnomalyWobbleSpeed.Value && off < 10f && _wobbleFlips >= 4)
            {
                Anomaly("roll-wobble", $"flips={_wobbleFlips} spd={spdNow:0}", ref _anWobbleT, now, ac, off, bank);
                _wobbleFlips = 0; // consume the window
            }

            // PERSISTENT-MISS — off stuck high while an axis is near saturation: the instructor is fighting
            // itself / not finding the efficient line. v0.26.1: require a genuine STALL, not an honest long
            // turn. With the G-limiter gone a big reorientation legitimately holds full elevator at high G for
            // many seconds while off steadily closes (logs showed off 90->19 over ~30 s) — that must NOT flag.
            // Anchor off when the stall window opens; if off drops progressDeg below the anchor the turn is
            // closing, so reset and re-anchor. Only fire when the timer reaches 2 s with off still within
            // progressDeg of the anchor — i.e. closing slower than progressDeg/2 deg/s (genuinely not closing).
            // progressDeg=4 => fires only below ~2 deg/s; the honest max-rate turns close at ~2.4 deg/s.
            const float progressDeg = 4f;
            bool saturated = Mathf.Abs(_outP) > 0.9f || Mathf.Abs(_outY) > 0.9f || Mathf.Abs(_outR) > 0.9f;
            if (off > 8f && saturated)
            {
                if (_missTimer <= 0f) _missAnchorOff = off;            // open a fresh stall window — anchor here
                else if (off < _missAnchorOff - progressDeg)           // closed progressDeg => making real progress
                { _missTimer = 0f; _missAnchorOff = off; }
                _missTimer += dt;
            }
            else _missTimer = 0f;
            if (_missTimer > 2f && off > _missAnchorOff - progressDeg)
            {
                Anomaly("persistent-miss", $"stuck {_missTimer:0.0}s off~{_missAnchorOff:0} (<{progressDeg:0}deg progress)", ref _anMissT, now, ac, off, bank);
                _missTimer = 0f;
            }

            _prevOff = off;
        }

        // Count a sign flip on an output axis, ignoring near-zero noise. Updates the running sign in place.
        private static void CountFlip(float val, ref float prevSign, ref int flips)
        {
            if (Mathf.Abs(val) < 0.05f) return;
            float s = Mathf.Sign(val);
            if (prevSign != 0f && s != prevSign) flips++;
            prevSign = s;
        }

        // Emit one [anomaly] line with a short flight-state + gain snapshot, honouring a per-type cooldown
        // (the ref stamp) so a single event can't flood the log.
        private static void Anomaly(string type, string detail, ref float lastStamp, float now, Aircraft ac, float off, float bank)
        {
            if (now - lastStamp < 1f) return; // per-type cooldown
            lastStamp = now;
            // Assign the next sequential index and flash it on-screen so the pilot can call out "#N felt wrong".
            _anomalyIndex++;
            LastAnomalyIndex = _anomalyIndex; LastAnomalyType = type; LastAnomalyTime = now;
            float spd = ac.rb != null ? ac.rb.velocity.magnitude : -1f;
            WTMouseAimPlugin.Log.LogWarning(
                $"[anomaly #{_anomalyIndex}] {type} t={now:0.000} {detail} off={off:0.0} bank={bank:0.0} phase={LastPhase} " +
                $"out P/R/Y=({_outP:0.00},{_outR:0.00},{_outY:0.00}) spd={spd:0} g={ac.gForce:0.0}{(FlyLevelActive ? " LVL" : "")} " +
                $"[sens={Cfg.PitchYawSensitivity.Value:0.0} bankGain={Cfg.FineBankGain.Value:0.0} bankDz={Cfg.FineBankDeadzone.Value:0.0} maxBank={Cfg.MaxBankAngle.Value:0} brake={Cfg.PitchBrake.Value:0.00} coord={Cfg.RollPitchCoordination.Value:0.00} align={Cfg.AlignAngle.Value:0} yawSc={Cfg.TurnYawScale.Value:0.00} rollG={Cfg.RollGain.Value:0.00} rollDamp={Cfg.RollDamping.Value:0.00} rollSm={Cfg.RollRateSmoothing.Value:0.00}]");
            if (Cfg.AnomalyContext.Value) DumpTrail(now);
        }

        // Dump the last ~20 ring-buffer frames as ONE compact [anomaly:trail] line so the lead-up to the
        // event is visible (how the wag built, how the bank blew past its target) without per-frame spam.
        // Throttled to once per second across all anomaly types so a multi-type frame doesn't repeat it.
        // Per frame: t | off / bank>tgtBank | P,R,Y outputs | yaw rate | spd. Oldest → newest, left → right.
        private static void DumpTrail(float now)
        {
            if (now - _lastTrailT < 1f || _ringCount == 0) return;
            _lastTrailT = now;
            int n = Mathf.Min(_ringCount, 20);
            var sb = new System.Text.StringBuilder("[anomaly:trail] (t off b>tgt P,R,Y yr rr rf spd)");
            for (int i = n - 1; i >= 0; i--)
            {
                int idx = ((_ringHead - 1 - i) % _ring.Length + _ring.Length) % _ring.Length;
                AnFrame f = _ring[idx];
                sb.Append($" {f.t:0.00}:{f.off:0}/{f.bank:0}>{f.tgtBank:0}/{f.p:0.00},{f.r:0.00},{f.y:0.00}/{f.yr:0.00}/{f.rr:0.00}/{f.rf:0.00}/{f.spd:0}");
            }
            WTMouseAimPlugin.Log.LogWarning(sb.ToString());
        }

        // PHASE CLASSIFICATION (v0.26). Map the current frame onto the instructor's plan so it's legible
        // ("roll the lift vector onto the target, then pull up into it"). |phi| small = target is at 12
        // o'clock (lift vector on it → pull); |phi| large = target off to the side (need to roll).
        //   LEVEL — Fly Level autopilot owns the plane (ignoring the marker).
        //   HOLD  — on target and settled (tiny off, nose barely moving).
        //   FINE  — inside the fine cone, closing the last few degrees.
        //   ALIGN — big turn, target still well off the lift vector: rolling it onto the target (pull gated).
        //   PULL  — big turn, lift vector roughly on the target: pulling up into it.
        //   TURN  — transitional (mid blend), neither cleanly aligning nor pulling.
        private static string ClassifyPhase(bool flyLevel, float off, float phi, float bigTurn, float noseTurnDeg)
        {
            if (flyLevel) return "LEVEL";
            if (off < Cfg.FineAngle.Value)
                return (off < 1.5f && noseTurnDeg < 0.15f) ? "HOLD" : "FINE";
            float aphi = Mathf.Abs(phi);
            if (bigTurn > 0.5f) return aphi > 25f ? "ALIGN" : "PULL";
            return "TURN";
        }

        // PER-MANEUVER SUMMARY (v0.26). A maneuver begins when off rises above AlignAngle and ends when the
        // nose settles back under FineAngle for ~0.3 s. On completion we emit ONE [maneuver] line — start/peak
        // off, time-to-align (first |phi|<20°, "lift vector on target"), time-to-capture (first off<FineAngle),
        // and peak bank / G / roll rate — the "how did the planned path actually work out" record. Cheap: one
        // line per completed turn. Reset on engage alongside the anomaly state.
        private static void TrackManeuver(Aircraft ac, float off, float phi, float bank, float rollRate, float dt)
        {
            float now = Time.time;

            if (!_manvActive && off > Cfg.AlignAngle.Value)
            {
                _manvActive = true;
                _manvStartT = now; _manvStartOff = off; _manvPeakOff = off;
                _manvAlignT = -1f; _manvCaptureT = -1f;
                _manvPeakBank = 0f; _manvPeakG = 0f; _manvPeakRoll = 0f;
                _manvSettle = 0f; _manvOvershot = false;
            }
            if (!_manvActive) return;

            _manvPeakOff  = Mathf.Max(_manvPeakOff, off);
            _manvPeakBank = Mathf.Max(_manvPeakBank, Mathf.Abs(bank));
            _manvPeakG    = Mathf.Max(_manvPeakG, Mathf.Abs(ac.gForce));
            _manvPeakRoll = Mathf.Max(_manvPeakRoll, Mathf.Abs(rollRate));
            if (_manvAlignT   < 0f && Mathf.Abs(phi) < 20f)        _manvAlignT   = now - _manvStartT;
            if (_manvCaptureT < 0f && off < Cfg.FineAngle.Value)   _manvCaptureT = now - _manvStartT;

            // Capture-settle: once off stays under FineAngle for ~0.3 s the turn is done — emit and reset.
            _manvSettle = off < Cfg.FineAngle.Value ? _manvSettle + dt : 0f;
            if (_manvSettle >= 0.3f)
            {
                float dur = now - _manvStartT;
                string align   = _manvAlignT   >= 0f ? $"{_manvAlignT:0.00}s"   : "n/a";
                string capture = _manvCaptureT >= 0f ? $"{_manvCaptureT:0.00}s" : "n/a";
                WTMouseAimPlugin.Log.LogInfo(
                    $"[maneuver] start={_manvStartOff:0}deg peak={_manvPeakOff:0}deg dur={dur:0.00}s " +
                    $"toAlign={align} toCapture={capture} peakBank={_manvPeakBank:0}deg peakG={_manvPeakG:0.0} " +
                    $"peakRollRate={_manvPeakRoll:0.00} overshoot={(_manvOvershot ? "Y" : "n")}");
                _manvActive = false;
                _manvSettle = 0f;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Cockpit camera follow. Native CameraCockpitState.UpdateState drives the view from panView/
    // tiltView (aircraft-local degrees) into cam.transform.localRotation, locking forward when not
    // free-looking. We POSTFIX it and, while actively aiming (not free-looking), override the view
    // rotation to smoothly look toward the marker — so the camera leads where you're commanding the
    // nose. We leave FOV, shake, free-look, and TrackIR to the native code.
    [HarmonyPatch(typeof(CameraCockpitState), "UpdateState")]
    internal static class CockpitCameraPatch
    {
        private static float _pan, _tilt;
        private static bool  _overriding;

        private static void Postfix(CameraStateManager cam)
        {
            if (!Cfg.Enabled.Value || !Cfg.CameraFollow.Value || cam == null || cam.transform == null)
            { _overriding = false; return; }
            if (PlayerSettings.useTrackIR)
            { _overriding = false; return; } // don't fight head-tracking

            if (AimRig.AimFrozen())
            { _overriding = false; return; } // frozen (Free Look / RMB): let native free-look look around

            // NOTE: we deliberately KEEP steering the view toward the (now frozen) marker while a
            // menu/map/pause is up. The aim direction doesn't change there, but the camera should stay
            // looking where you're flying instead of native snapping it back to the nose.

            if (!AimRig.TryGetContext(out var ac, out _))
            { _overriding = false; return; }

            Transform t = ac.transform;
            Vector3 local = t.InverseTransformDirection(AimRig.AimForward); // body-frame unit marker
            float amt = Cfg.CameraFollowAmount.Value;
            // Match native Euler(tiltView, panView, 0): +panView looks right, +tiltView looks down.
            float panTarget  =  Mathf.Atan2(local.x, Mathf.Max(0.001f, local.z)) * Mathf.Rad2Deg * amt;
            float tiltTarget = -Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f))        * Mathf.Rad2Deg * amt;

            // On the rising edge of overriding, seed from the camera's current local angles so there's
            // no jump from whatever native last set.
            if (!_overriding)
            {
                Vector3 e = cam.transform.localEulerAngles;
                _pan  = Norm(e.y);
                _tilt = Norm(e.x);
                _overriding = true;
            }

            float k = Mathf.Clamp01(2f * Time.unscaledDeltaTime / Mathf.Max(Cfg.CameraFollowSmoothing.Value, 0.01f));
            _pan  = Mathf.Lerp(_pan,  panTarget,  k);
            _tilt = Mathf.Lerp(_tilt, tiltTarget, k);
            cam.transform.localRotation = Quaternion.Euler(_tilt, _pan, 0f);
        }

        private static float Norm(float deg) => (deg > 180f) ? deg - 360f : deg;
    }

    // ---------------------------------------------------------------------------------------------
    // 3rd-person (orbit) camera follow — WT-style: the camera stays behind/above the plane but always
    // faces the aim direction, marker near screen centre.
    //
    // v0.22 verdict on the pan/tilt-steering approach (prefix writes native panView/tiltView): the pan
    // half tracks heading fine, but the RESULT was off the aim by an elevation-dependent error of up to
    // ~25-33 deg ([orbitcam:res] camOffAim, FOV 50 => marker offscreen) — native's tilt rotates the
    // camera around the plane through an arm that already carries a built-in downward angle (the 0.8r
    // up-offset), then look-at-plane re-aims it, so tilt never maps 1:1 to view elevation. Not fixable
    // by a better tilt formula without modelling that whole chain.
    //
    // So v0.23 splits the job:
    //   * Prefix (kept): still writes panView/tiltView. While we fly it keeps the NATIVE pivot pose
    //     roughly on the aim so RMB free-look starts from approximately the current view, and free-look
    //     itself stays 100% native.
    //   * Postfix (new): after native CameraMotion/Inputs have run, override the FINAL camera pose:
    //     position rigidly from the live plane position (the earlier world-space override failed by
    //     smoothing the POSITION — at speed that lags then jumps; only the view DIRECTION is smoothed
    //     here, frame-rate independent slerp, MouseFlight-style pole guard), native's terrain linecast
    //     replicated, then look along the smoothed aim. While the reticle is frozen (RMB free-look) or
    //     native's look-at-target is engaged the override stands down and re-seeds from the live pose on
    //     return, so transitions ease instead of snapping.
    [HarmonyPatch(typeof(CameraOrbitState), "UpdateState")]
    internal static class CameraOrbitPatch
    {
        private static float _pan;   // last stable orbit yaw — held through the vertical singularity
        private static bool  _prevNearPole;    // edge detection: pole-hold enter/exit diagnostics
        private static bool  _prevFrozen;      // edge detection: RMB/Free-Look freeze enter/exit
        private static float _lastOrbitLog;    // throttle the [orbitcam] trace to ~5/sec
        private static float _lastOrbitResLog; // throttle the [orbitcam:res] trace to ~5/sec
        private static Quaternion _aimRot = Quaternion.identity; // smoothed view rotation (postfix override)
        private static bool _aimRotValid;      // false => re-seed _aimRot from the live camera pose
        private static bool _levelingSuppressed; // true while inside the pole deadzone (horizon leveling off)
        private static float _flYaw, _flPitch; // free-look look angles (deg): heading + elevation, horizon-locked
        private static bool  _freeLookSeeded;  // rising-edge guard: seed _flYaw/_flPitch once when free-look begins
        private static bool  _wasFrozenPose;   // last pose frame was free-look (release starts the timed return)
        private static Quaternion _returnFrom = Quaternion.identity; // free-look view we ease back FROM on release
        private static float _returnT;         // 0..1 progress of the release return animation
        private static bool  _returning;       // true while easing the view back to the flight direction

        private static void Prefix(CameraOrbitState __instance)
        {
            if (!Cfg.Enabled.Value || !Cfg.CameraFollow.Value) return;
            if (CameraStateManager.cameraMode != CameraMode.orbit) return;
            if (!AimRig.TryGetContext(out var ac, out _)) return;
            if (ac.disabled) return;                                // dead: leave the native camera alone

            Vector3 aim = AimRig.AimForward;

            // Native pivot base looks along followVector (smoothed flat velocity). panView yaws it from that
            // heading to the aim's heading (around world up); tiltView pitches it to the aim's elevation.
            // Native then orbits the (plane-parented) camera by exactly these and smooths via its own pivot
            // Lerp — so a flick eases in instead of snapping, with no positional lag.
            var tr = Traverse.Create(__instance);
            Vector3 follow = tr.Field("followVector").GetValue<Vector3>();
            Vector3 followH = new Vector3(follow.x, 0f, follow.z);
            Vector3 aimH    = new Vector3(aim.x,   0f, aim.z);

            // panView: signed yaw from the velocity heading to the aim heading (matches native
            // Rotate(0,panView,0,World)). Near vertical BOTH headings collapse — aimH->0 when aiming straight
            // up/down, and followH->0 in a vertical climb — so the heading turns to noise and the camera whips
            // around the plane, throwing the aim offscreen (the "drift"). There, HOLD the last stable yaw: with
            // the view pitched near-vertical the heading barely changes the shot, and a frozen yaw stays steady
            // instead of spinning. Mirrors MouseFlight's |forward.y|>0.9 pole guard. We refresh _pan EVERY
            // frame, even while frozen/free-looking, so releasing free-look returns straight to the live aim
            // instead of snapping from a stale heading.
            bool nearPole   = Mathf.Abs(aim.y) > 0.9f;
            bool freshValid = followH.sqrMagnitude > 1e-3f && aimH.sqrMagnitude > 1e-3f;
            float freshPan  = freshValid
                ? Vector3.SignedAngle(followH.normalized, aimH.normalized, Vector3.up)
                : _pan;
            if (!nearPole && freshValid)
                _pan = freshPan;

            bool frozen = AimRig.AimFrozen();
            bool dbg    = Cfg.DebugLogging.Value;

            // Native's own accumulated look offsets BEFORE we overwrite them — nonzero growth here while
            // we're aiming means native Inputs() is fighting us for the orbit (it reads the same mouse).
            float nativePan = 0f, nativeTilt = 0f;
            if (dbg)
            {
                nativePan  = tr.Field("panView").GetValue<float>();
                nativeTilt = tr.Field("tiltView").GetValue<float>();
            }

            // Edge events (not throttled): the exact moments the two suspect mechanisms kick in/out.
            //   POLE  — how stale did the held yaw get vs a fresh computation while we were holding it?
            //   FREEZE— what offsets native accumulated during free-look, and what we resume to.
            if (dbg && nearPole != _prevNearPole)
                WTMouseAimPlugin.Log.LogInfo(
                    $"[orbitcam] POLE {(nearPole ? "ENTER" : "EXIT")} t={Time.time:0.000} heldPan={_pan:0.0} " +
                    $"freshPan={freshPan:0.0} stale={Mathf.DeltaAngle(freshPan, _pan):0.0} aimY={aim.y:0.00} " +
                    $"followHmag={followH.magnitude:0.00} freshValid={freshValid}");
            if (dbg && frozen != _prevFrozen)
                WTMouseAimPlugin.Log.LogInfo(
                    $"[orbitcam] FREEZE {(frozen ? "ENTER" : "EXIT")} t={Time.time:0.000} " +
                    $"nativePan={nativePan:0.0} nativeTilt={nativeTilt:0.0} ourPan={_pan:0.0}");
            _prevNearPole = nearPole;
            _prevFrozen   = frozen;

            // tiltView: native Rotate(tiltView,0,0,Self) pitches the view DOWN for +tilt, so aiming UP needs a
            // negative tilt. Subtract CameraPitchOffset to lift the resting view: the stock orbit cam parks
            // ~0.8*up above and 2*back behind, then looks AT the plane — ~22 of downward look that shoves the
            // level reticle to the top of the screen; this raises it back toward centre. Clamp short of straight
            // up/down (80, not 90): past there the look-at-plane up-vector goes degenerate and the view rolls/breaks.
            float tilt = Mathf.Clamp(
                -Mathf.Asin(Mathf.Clamp(aim.y, -1f, 1f)) * Mathf.Rad2Deg - Cfg.CameraPitchOffset.Value,
                -80f, 80f);

            // Steady-state trace: everything the pan/tilt computation depends on, ~5/sec.
            if (dbg && Time.time - _lastOrbitLog >= 0.2f)
            {
                _lastOrbitLog = Time.time;
                float aimHdg    = aimH.sqrMagnitude    > 1e-6f ? Vector3.SignedAngle(Vector3.forward, aimH.normalized,    Vector3.up) : float.NaN;
                float followHdg = followH.sqrMagnitude > 1e-6f ? Vector3.SignedAngle(Vector3.forward, followH.normalized, Vector3.up) : float.NaN;
                float aimElev   = Mathf.Asin(Mathf.Clamp(aim.y, -1f, 1f)) * Mathf.Rad2Deg;
                WTMouseAimPlugin.Log.LogInfo(
                    $"[orbitcam] t={Time.time:0.000} aimHdg={aimHdg:0.0} aimElev={aimElev:0.0} " +
                    $"followHdg={followHdg:0.0} followHmag={followH.magnitude:0.00} pan={_pan:0.0} tilt={tilt:0.0} " +
                    $"pole={(nearPole ? 1 : 0)} frozen={(frozen ? 1 : 0)} nativePan={nativePan:0.0} nativeTilt={nativeTilt:0.0} " +
                    $"curVis={(Cursor.visible ? 1 : 0)} curFlags={CursorManager.GetFlags()}");
            }

            if (frozen) return;                                     // frozen: native owns the orbit-look (free-look)

            tr.Field("panView").SetValue(_pan);
            tr.Field("tiltView").SetValue(tilt);
        }

        // After native CameraMotion/Inputs have run: take over the FINAL camera pose (see the block
        // comment above), then log where the camera actually ended up. camOffAim is the "is the marker
        // on screen" proxy — bigger than ~half the FOV and the aim marker is offscreen (the reported
        // failure). With the override active it should sit ~CameraPitchOffset and stay there.
        private static void Postfix(CameraOrbitState __instance, CameraStateManager cam)
        {
            if (cam == null || cam.mainCamera == null) return;
            bool overrode = ApplyAimPose(__instance, cam);

            if (!Cfg.DebugLogging.Value) return;
            if (CameraStateManager.cameraMode != CameraMode.orbit) return;
            if (!AimRig.TryGetContext(out var ac, out _)) return;
            if (Time.time - _lastOrbitResLog < 0.2f) return;
            _lastOrbitResLog = Time.time;
            Transform ct = cam.mainCamera.transform;
            WTMouseAimPlugin.Log.LogInfo(
                $"[orbitcam:res] t={Time.time:0.000} camOffAim={Vector3.Angle(ct.forward, AimRig.AimForward):0.0} " +
                $"camOffNose={Vector3.Angle(ct.forward, ac.transform.forward):0.0} fov={cam.mainCamera.fieldOfView:0} " +
                $"ovr={(overrode ? 1 : 0)} levelSuppressed={(_levelingSuppressed ? 1 : 0)}");
        }

        // The WT-style pose: camera 2r behind the (smoothed) aim direction and 0.8r above the plane,
        // looking along the aim pitched down by CameraPitchOffset. Returns true when it took the pose
        // this frame. Position is rigid to the live plane position (zero translational lag at speed);
        // only the view direction is smoothed (1 - exp(-rate*dt) slerp — frame-rate independent), with
        // MouseFlight's pole guard: near straight up/down keep the previous frame's up instead of world
        // up so the view doesn't flip/spin through the singularity. Native's terrain-collision pull-in
        // is replicated with native's own layerMask. Stand-down cases reset _aimRotValid so the next
        // override frame re-seeds smoothing from wherever the camera actually is — no snap on re-entry.
        private static bool ApplyAimPose(CameraOrbitState st, CameraStateManager cam)
        {
            if (!Cfg.Enabled.Value || !Cfg.CameraFollow.Value) { _aimRotValid = false; _freeLookSeeded = false; return false; }
            // Re-check the mode: native Inputs() (Center button etc.) may have switched state this frame.
            if (CameraStateManager.cameraMode != CameraMode.orbit) { _aimRotValid = false; _freeLookSeeded = false; return false; }
            if (!AimRig.TryGetContext(out var ac, out _) || ac.disabled) { _aimRotValid = false; _freeLookSeeded = false; return false; }
            var tr = Traverse.Create(st);
            if (tr.Field("lookAtTargetLerp").GetValue<float>() > 0f)        // native look-at-target engaged
            { _aimRotValid = false; _freeLookSeeded = false; return false; }

            Transform ct = cam.transform; // what native CameraMotion positions (mainCamera rides on it)
            Vector3 planePos = cam.cameraPivot != null ? cam.cameraPivot.position : ac.transform.position;

            // RMB / Free-Look: KEEP our orbit pose (same pivot, +0.8r up, CameraPitchOffset framing) and
            // look around with the MOUSE — instead of standing down to the native orbit, whose pivot looks
            // AT the plane with a different downward angle (that hand-off was the "snap a bit down" on RMB).
            // Horizon-locked yaw/pitch so the view can't roll or hit a pole singularity. Seeded from the
            // current aim view on entry, so engaging free-look is seamless; on release the view eases back
            // to the flight direction (the timed return below).
            if (AimRig.AimFrozen())
            {
                if (!_freeLookSeeded)
                {
                    Vector3 seed = (_aimRotValid ? _aimRot : ct.rotation) * Vector3.forward;
                    _flYaw   = Mathf.Atan2(seed.x, seed.z) * Mathf.Rad2Deg;
                    _flPitch = Mathf.Asin(Mathf.Clamp(seed.y, -1f, 1f)) * Mathf.Rad2Deg;
                    _freeLookSeeded = true;
                }
                // Drive the look with the SAME smoothed mouse delta + MouseSensitivity as aiming (from
                // AimRig), so free-look and aim feel identical. d.x = mouse right (+), d.y = mouse up (+).
                Vector2 d = AimRig.LookDelta;
                float sens = Cfg.MouseSensitivity.Value;
                _flYaw  += d.x * sens;
                _flPitch = Mathf.Clamp(_flPitch + d.y * sens * (Cfg.InvertPitch.Value ? -1f : 1f), -85f, 85f);

                Quaternion look = Quaternion.Euler(-_flPitch, _flYaw, 0f);
                PlaceOrbitCamera(tr, ct, planePos, look * Vector3.forward, look);
                _aimRotValid   = false; // release re-seeds the aim smoother (from this pose, via _wasFrozenPose)
                _wasFrozenPose = true;
                return true;
            }
            _freeLookSeeded = false;    // free-look ended; the next entry re-seeds the look angles

            Vector3 aim = AimRig.AimForward;
            if (aim.sqrMagnitude < 0.5f) return false;

            // Seed the smoother. Straight out of free-look, START A TIMED EASE back to the flight
            // direction: the plane keeps flying the heading it held (no commit) and the camera swings
            // back over FreeLookReturnTime. Otherwise just ease in from the live pose.
            if (_wasFrozenPose)
            {
                _aimRot      = Quaternion.Euler(-_flPitch, _flYaw, 0f); // the free-look view we return FROM
                _returnFrom  = _aimRot;
                _returnT     = 0f;
                _returning   = true;
                _aimRotValid = true;
                _wasFrozenPose = false;
            }
            else if (!_aimRotValid)
            {
                _aimRot      = ct.rotation;
                _aimRotValid = true;
                _returning   = false;
            }

            // Pole-stable horizon leveling with a hysteresis deadzone. Replaces the old hard
            // |aim.y|>0.9 world-up / prev-up switch, which flipped the camera's up instantly through
            // the singularity (an abrupt ~180deg roll over the top of a loop). Away from vertical we
            // level to the world horizon; inside the deadzone we hold the current (continuous) up so
            // nothing can flip; leveling eases back in over the band as we come out, and only
            // re-enables once past the far edge — hysteresis, so hovering right at the pole can't
            // oscillate. The OrbitAimSmoothing slerp below turns the up-vector hand-off into a smooth
            // roll to the new level side instead of a snap. holdUp comes from the previous smoothed
            // rotation, so the view direction never inverts through vertical.
            float vertical = 90f - Mathf.Abs(Mathf.Asin(Mathf.Clamp(aim.y, -1f, 1f)) * Mathf.Rad2Deg); // 0 at pole, 90 at horizon
            float dz = Cfg.HorizonDeadzoneDeg.Value;
            if (!_levelingSuppressed && vertical <= 0f) _levelingSuppressed = true;
            else if (_levelingSuppressed && vertical >= dz) _levelingSuppressed = false;

            Vector3 holdUp = _aimRot * Vector3.up;   // continuous: never flips through the pole
            Vector3 upRef  = holdUp;
            if (!_levelingSuppressed)
            {
                Vector3 right = Vector3.Cross(Vector3.up, aim);
                if (right.sqrMagnitude > 1e-5f)
                {
                    Vector3 levelUp = Vector3.Cross(aim, right.normalized).normalized;
                    float w = dz > 0f ? Mathf.InverseLerp(0f, dz, vertical) : 1f; // ease leveling in over the band
                    upRef = Vector3.Slerp(holdUp, levelUp, w);
                }
            }
            Quaternion want = Quaternion.LookRotation(aim, upRef);
            if (_returning)
            {
                // Fixed-duration smoothstep swing from the free-look view back to the live flight dir.
                _returnT += Time.deltaTime / Mathf.Max(Cfg.FreeLookReturnTime.Value, 0.01f);
                if (_returnT >= 1f) { _returnT = 1f; _returning = false; }
                _aimRot = Quaternion.Slerp(_returnFrom, want, Mathf.SmoothStep(0f, 1f, _returnT));
            }
            else
            {
                _aimRot = Quaternion.Slerp(_aimRot, want,
                    1f - Mathf.Exp(-Cfg.OrbitAimSmoothing.Value * Time.deltaTime));
            }

            PlaceOrbitCamera(tr, ct, planePos, _aimRot * Vector3.forward, _aimRot);
            return true;
        }

        // Position the camera 2r behind the look direction and 0.8r above the plane (native's orbit
        // geometry), pull it in off terrain with native's own linecast, and look along lookRot pitched
        // down by CameraPitchOffset. Shared by the aim-tracking and free-look paths so engaging or
        // releasing free-look never changes the pivot/offset (the source of the old RMB snap).
        private static void PlaceOrbitCamera(Traverse tr, Transform ct, Vector3 planePos, Vector3 dir, Quaternion lookRot)
        {
            float maxR = tr.Field("followingMaxRadius").GetValue<float>();
            float zoom = tr.Field("viewDistAdjust").GetValue<float>();
            float r = 1f + maxR * (1f + zoom);                       // native's num2: zoom-aware orbit radius
            // Stock framing is 2r behind the look dir + 0.8r above. The three user offsets ride on top,
            // each scaled by r so they hold their feel across zoom. Side uses a horizontal "camera right"
            // (up x dir); guard the degenerate near-vertical look where that cross collapses.
            Vector3 right = Vector3.Cross(Vector3.up, dir);
            right = right.sqrMagnitude > 1e-6f ? right.normalized : Vector3.right;
            Vector3 camPos = planePos
                - dir   * ((2f + Cfg.CameraDistanceOffset.Value) * r)
                + Vector3.up * ((0.8f + Cfg.CameraHeightOffset.Value) * r)
                + right * (Cfg.CameraSideOffset.Value * r);

            // Terrain pull-in, same math as native CameraMotion's linecast block.
            Vector3 armN = (planePos - camPos).normalized;
            int mask = tr.Field("layerMask").GetValue<int>();
            if (Physics.Linecast(planePos, camPos, out RaycastHit hitInfo, mask))
            {
                float dot = Mathf.Max(Vector3.Dot(armN, hitInfo.normal), 0.1f);
                camPos = hitInfo.point + armN / dot;
            }

            ct.SetPositionAndRotation(camPos, lookRot * Quaternion.Euler(Cfg.CameraPitchOffset.Value, 0f, 0f));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Diagnostic: log every native camera-state transition (Center button -> chase, death -> free,
    // look-at switches, etc.) so a "camera suddenly reframed" report can be attributed to a native
    // state switch vs our orbit math. cameraMode is updated inside EnterState, so the postfix sees
    // the new mode.
    [HarmonyPatch(typeof(CameraStateManager), "SwitchState")]
    internal static class CameraSwitchStatePatch
    {
        private static void Prefix(CameraStateManager __instance, out string __state)
        {
            __state = __instance.currentState != null ? __instance.currentState.GetType().Name : "<none>";
        }

        private static void Postfix(CameraStateManager __instance, string __state)
        {
            if (!Cfg.DebugLogging.Value) return;
            string now = __instance.currentState != null ? __instance.currentState.GetType().Name : "<none>";
            WTMouseAimPlugin.Log.LogInfo(
                $"[camstate] t={Time.time:0.000} {__state} -> {now} (mode={CameraStateManager.cameraMode})");
        }
    }
}

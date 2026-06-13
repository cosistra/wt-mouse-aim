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
        public const string PluginVersion = "0.23.0";

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
            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded (world follow-point + proportional chase w/ rate damping + per-axis manual override + Win32 raw mouse + 3rd-person mouse-aim w/ orbiting camera + RMB freeze — tune live via F1).");
        }

        private void Update()
        {
            AimRig.Update();
        }

        private void OnGUI()
        {
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

            // Tiny text readout (top-left) so feel/cone clamping is verifiable.
            var prev = GUI.color;
            GUI.color = Color.white;
            string ctrl = !Cfg.WriteControl.Value ? "overlay-only"
                        : ChaseController.IsFlying ? "FLYING (mod owns stick)"
                        : "native";
            GUI.Label(new Rect(12f, 12f, 560f, 22f),
                $"WT MouseAim  off={off:0.0}°  cone={half:0}°  [{ctrl}]");
            // Instructor's live stick command (what the mod is telling the plane, before manual override).
            GUI.Label(new Rect(12f, 30f, 560f, 22f),
                $"instructor  pitch={ChaseController.LastPitch:+0.00;-0.00;0.00}  " +
                $"yaw={ChaseController.LastYaw:+0.00;-0.00;0.00}  roll={ChaseController.LastRoll:+0.00;-0.00;0.00}");
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
        public static ConfigEntry<bool>  ShowOverlay;
        public static ConfigEntry<bool>  DebugLogging; // periodic BepInEx-log dump of mouse/aim/chase state
        public static ConfigEntry<float> MouseSensitivity; // degrees of aim offset per unit of mouse delta
        public static ConfigEntry<float> MouseSmoothing;   // 0..1 one-pole smoothing on the mouse delta
        public static ConfigEntry<float> MaxAimAngle;      // cone half-angle (deg) the marker is clamped within
        public static ConfigEntry<float> AimDistance;      // metres ahead the aim point is placed (projection only)
        public static ConfigEntry<bool>  InvertPitch;

        // --- Chase law (writes flight controls). Per-axis gains may be negative to flip a sign.
        public static ConfigEntry<bool>  WriteControl;        // actually drive the stick (off = overlay only)
        public static ConfigEntry<float> PitchYawSensitivity; // base chase gain on the body-frame aim direction
        public static ConfigEntry<float> ChaseDamping;        // derivative damping on the nose's rotation rate
        public static ConfigEntry<float> RollDamping;         // derivative damping on the roll rate (anti bank-wobble)
        public static ConfigEntry<float> AggressiveTurnAngle; // deg off-target at which we commit to a hard bank
        public static ConfigEntry<float> RollGain;            // roll output scale (negative flips roll direction)
        public static ConfigEntry<float> PitchGain;           // pitch output scale (negative flips)
        public static ConfigEntry<float> YawGain;             // yaw/rudder output scale (negative flips)
        public static ConfigEntry<float> OutputSlew;          // max stick units/sec (anti-jerk rate limit)
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
        public static ConfigEntry<float> FineBankGain;        // deg of target bank per deg of azimuth error (0 = off)
        public static ConfigEntry<float> FineBankCap;         // max bank (deg) the fine servo may command

        // --- Cockpit camera follow.
        public static ConfigEntry<bool>  CameraFollow;        // smoothly look toward the marker
        public static ConfigEntry<float> CameraFollowAmount;  // 0..1 fraction of the marker offset to look toward
        public static ConfigEntry<float> CameraFollowSmoothing;// seconds-ish smoothing (higher = lazier)
        public static ConfigEntry<float> CameraPitchOffset;   // 3p: deg the view is pitched down off the aim direction
        public static ConfigEntry<float> OrbitAimSmoothing;   // 3p: view-direction smoothing rate (1/s, higher = snappier)

        public static void Bind(ConfigFile cf)
        {
            Enabled          = cf.Bind("General", "Enabled", true,
                "Master ON/OFF for the whole mod. Off = stock game controls, no overlay, no camera follow.");
            ShowOverlay      = cf.Bind("HUD", "ShowOverlay", true,
                "Show the on-screen aim circle, boresight cross, and turn cone. Purely visual — no effect on handling.");
            DebugLogging     = cf.Bind("HUD", "DebugLogging", false,
                "Dump mouse delta, marker-vs-nose angle, camera-vs-nose angle and chase outputs to the BepInEx log/console (~twice a second). For diagnosing aim issues; leave off normally.");

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
            PitchYawSensitivity = cf.Bind("Control", "PitchYawSensitivity", 3.0f, new ConfigDescription(
                "How hard the instructor pulls the nose toward the circle. Higher = snappier and closes faster, but can overshoot and wobble (raise ChaseDamping to compensate); lower = gentler, easier fine aiming. ~3 is balanced.",
                new AcceptableValueRange<float>(0.5f, 8f)));
            ChaseDamping        = cf.Bind("Control", "ChaseDamping", 0.25f, new ConfigDescription(
                "Calms the inputs as the nose nears the circle so it eases in instead of overshooting — opposes the nose's own turn rate (the anti-wobble term). ~0.25 is a smooth default. 0 = off (snappy, but the rudder can hunt side-to-side); raise toward ~0.4 if it still oscillates around the aim direction, lower if it feels sluggish to close.",
                new AcceptableValueRange<float>(0f, 1f)));
            RollDamping         = cf.Bind("Control", "RollDamping", 0.3f, new ConfigDescription(
                "Anti-wobble damping for the ROLL axis specifically — the bank-angle counterpart of ChaseDamping. In a hard bank-to-turn (45deg+ off-target) the wings can rock back and forth while the nose pulls around; this eases the roll command off as the bank builds so it settles instead of hunting. Raise toward ~0.5 if it still rocks at high bank; 0 = off. Only opposes the rolling MOTION, so it won't fight a held bank.",
                new AcceptableValueRange<float>(0f, 1f)));
            AggressiveTurnAngle = cf.Bind("Control", "AggressiveTurnAngle", 10.0f, new ConfigDescription(
                "Nose-off-target angle (deg) at which the plane fully commits to banking. Below it the bank rolls in proportionally (precise fine aiming); above it it's a full bank-to-turn. LOWER = banks sooner and turns harder; higher = stays wings-level longer and leans on the weak rudder (can feel like a deadzone).",
                new AcceptableValueRange<float>(2f, 60f)));
            RollGain            = cf.Bind("Control", "RollGain", 1.0f, new ConfigDescription(
                "Roll authority scale. Lower if it banks too eagerly, raise for crisper rolls. Negative flips roll direction — if the plane rolls AWAY from level when on-target, set this negative.",
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
            FineGainBoost       = cf.Bind("Control", "FineGainBoost", 2.0f, new ConfigDescription(
                "Extra pitch/yaw pull for the last few degrees: multiplies the proportional term by up to (1 + this) as the offset closes. Cures the 'never quite centres' residual where a ~1 deg error left only ~0.05 stick. 0 = off. Raise if the nose still parks short of the circle; lower if it hunts around it.",
                new AcceptableValueRange<float>(0f, 5f)));
            FineBankGain        = cf.Bind("Control", "FineBankGain", 3.0f, new ConfigDescription(
                "Inside FineAngle the wings-level servo is re-targeted to a small bank proportional to the horizontal error (deg of bank per deg of azimuth error), so lift helps carry the nose across instead of leaving the job to the weak rudder. 0 = off (pure wings-level, the old behaviour). Raise to close sideways residuals faster; lower if it wing-rocks.",
                new AcceptableValueRange<float>(0f, 10f)));
            FineBankCap         = cf.Bind("Control", "FineBankCap", 25.0f, new ConfigDescription(
                "Maximum bank (deg) the fine bank servo may command. Keeps small corrections from turning into a corkscrew (v0.22 logs showed an 80 deg bank chasing a 6 deg error).",
                new AcceptableValueRange<float>(5f, 60f)));
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
        }
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
            ApplyCursorRegime(flyHidden, aimCapture);

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
            if (aimCapture)
            {
                raw = ReadMouseDelta();
                float sm = Mathf.Clamp01(Cfg.MouseSmoothing.Value);
                _smoothedDelta = Vector2.Lerp(raw, _smoothedDelta, sm); // sm=0 -> raw, higher -> smoother/laggier

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
            else
            {
                _smoothedDelta = Vector2.zero; // drop stale delta so it doesn't lurch on resume
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

        public static bool IsFlying => _active;

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
                fixedWing &&
                pilotStrength >= 0.2f &&                 // not blacked out
                aircraft.cockpit != null && !aircraft.cockpit.IsDetached();

            if (active && !_wasActive)
            {
                // Engage: seed our output from the current (native) stick so the takeover is smooth,
                // and hide the native virtual-joystick crosshair so it can't compete for the mouse.
                var ci = aircraft.GetInputs();
                _outP = ci.pitch; _outR = ci.roll; _outY = ci.yaw;
                _prevFwdValid = false; // don't compute a huge rotation rate across the engage gap
                HideNativeVirtualJoystick();
                WTMouseAimPlugin.Log.LogInfo("WT Mouse Aim: ON (fixed-wing) — chase control engaged.");
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

                // Marker direction in the body frame (unit): x = right, y = up, z = forward.
                Vector3 local = t.InverseTransformDirection(AimRig.AimForward);
                float off = Vector3.Angle(t.forward, AimRig.AimForward); // degrees the nose is off the marker
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
                float tgtP = Mathf.Clamp((-local.y * sens * fineGain + pitchRate * damp) * Cfg.PitchGain.Value, -1f, 1f);
                float tgtY = Mathf.Clamp(( local.x * sens * fineGain - yawRate   * damp) * Cfg.YawGain.Value,   -1f, 1f);

                // World-frame azimuth error (deg, + = marker right of the nose heading). Drives the fine
                // bank servo; degenerate headings (straight up/down) just disable the servo this frame.
                Vector3 aimW   = AimRig.AimForward;
                Vector3 aimHW  = new Vector3(aimW.x, 0f, aimW.z);
                Vector3 noseHW = new Vector3(t.forward.x, 0f, t.forward.z);
                float azErr = (aimHW.sqrMagnitude > 1e-6f && noseHW.sqrMagnitude > 1e-6f)
                    ? Vector3.SignedAngle(noseHW, aimHW, Vector3.up) : 0f;

                // ROLL: bank-to-turn blend. Far off-target => bank hard toward the marker; near-target
                // => the fine bank servo. v0.22's pure wings-level small-offset behaviour left the weak
                // rudder alone on the lateral residual, so instead the servo banks a LITTLE, proportional
                // to the azimuth error (FineBankGain deg per deg, capped at FineBankCap) and lets lift
                // carry the nose across; as azErr->0 the target bank ->0 and it converges to wings-level
                // exactly as before. t.right.y is the world-up component on the wing (0 = level, <0 =
                // right wing down), so (t.right.y + sin(targetBank)) is a proportional servo about the
                // commanded bank with the same gain the old wings-level term had.
                float aggressiveRoll = Mathf.Clamp(local.x * sens, -1f, 1f);
                float fineBank = Mathf.Clamp(azErr * Cfg.FineBankGain.Value,
                                             -Cfg.FineBankCap.Value, Cfg.FineBankCap.Value);
                float fineRoll = t.right.y + Mathf.Sin(fineBank * Mathf.Deg2Rad);
                // Linear blend (no ease-in square — that was starving the bank on small/medium offsets
                // and leaving the weak rudder to crawl the last few degrees: the "deadzone"). At/above
                // AggressiveTurnAngle it fully commits to the bank; below it rolls in proportionally.
                float rollBlend = Mathf.Clamp01(off / Mathf.Max(1f, Cfg.AggressiveTurnAngle.Value));
                // Rate-damp the roll the same way pitch/yaw are damped: in a hard bank-to-turn the proportional
                // roll command overshoots the target bank and reverses, rocking the wings while the nose pulls
                // around. Subtracting the live roll RATE eases the command off as the bank builds so it settles
                // instead of hunting. It opposes only the rolling MOTION — once the bank is held and roll rate
                // ~0 the term vanishes, so it doesn't fight the sustained bank itself.
                float tgtR = Mathf.Clamp(
                    (Mathf.Lerp(fineRoll, aggressiveRoll, rollBlend) - rollRate * Cfg.RollDamping.Value)
                    * Cfg.RollGain.Value, -1f, 1f);

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
                    }
                }
                else { _engP = _engR = _engY = 0f; }

                ci.pitch = Mathf.Clamp(pOut, -1f, 1f);
                ci.roll  = Mathf.Clamp(rOut, -1f, 1f);
                ci.yaw   = Mathf.Clamp(yOut, -1f, 1f);
                _disRamp = 1f; // primed for the next disengage

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
                        float pTermP = -local.y * sens * fineGain, dTermP = pitchRate * damp; // tgtP = (P+D)*PitchGain
                        float pTermY =  local.x * sens * fineGain, dTermY = -yawRate  * damp; // tgtY = (P+D)*YawGain
                        float elevE = (Mathf.Asin(Mathf.Clamp(aimW.y, -1f, 1f)) - Mathf.Asin(Mathf.Clamp(t.forward.y, -1f, 1f))) * Mathf.Rad2Deg;
                        float spd  = aircraft.rb != null ? aircraft.rb.velocity.magnitude : -1f;
                        float bank = -Mathf.Asin(Mathf.Clamp(t.right.y, -1f, 1f)) * Mathf.Rad2Deg; // +: right wing down
                        string f = fine ? "0.000" : "0.00";
                        WTMouseAimPlugin.Log.LogInfo(
                            $"[chase] t={Time.time:0.000} off={off:0.000}deg elevE={elevE:0.00} azE={azErr:0.00} noseTurn={noseTurnDeg:0.000} " +
                            $"fineG={fineGain:0.00} fineBank={fineBank:0.0} " +
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
                $"ovr={(overrode ? 1 : 0)}");
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
            if (!Cfg.Enabled.Value || !Cfg.CameraFollow.Value) { _aimRotValid = false; return false; }
            // Re-check the mode: native Inputs() (Center button etc.) may have switched state this frame.
            if (CameraStateManager.cameraMode != CameraMode.orbit) { _aimRotValid = false; return false; }
            if (!AimRig.TryGetContext(out var ac, out _) || ac.disabled) { _aimRotValid = false; return false; }
            if (AimRig.AimFrozen()) { _aimRotValid = false; return false; } // RMB free-look: native owns the view
            var tr = Traverse.Create(st);
            if (tr.Field("lookAtTargetLerp").GetValue<float>() > 0f)        // native look-at-target engaged
            { _aimRotValid = false; return false; }

            Vector3 aim = AimRig.AimForward;
            if (aim.sqrMagnitude < 0.5f) return false;

            Transform ct = cam.transform; // what native CameraMotion positions (mainCamera rides on it)
            Vector3 planePos = cam.cameraPivot != null ? cam.cameraPivot.position : ac.transform.position;

            Vector3 upRef = (_aimRotValid && Mathf.Abs(aim.y) > 0.9f) ? _aimRot * Vector3.up : Vector3.up;
            Quaternion want = Quaternion.LookRotation(aim, upRef);
            if (!_aimRotValid) { _aimRot = ct.rotation; _aimRotValid = true; } // ease in from the live pose
            _aimRot = Quaternion.Slerp(_aimRot, want,
                1f - Mathf.Exp(-Cfg.OrbitAimSmoothing.Value * Time.deltaTime));

            Vector3 dir = _aimRot * Vector3.forward;
            float maxR = tr.Field("followingMaxRadius").GetValue<float>();
            float zoom = tr.Field("viewDistAdjust").GetValue<float>();
            float r = 1f + maxR * (1f + zoom);                       // native's num2: zoom-aware orbit radius
            Vector3 camPos = planePos - dir * (2f * r) + Vector3.up * (0.8f * r);

            // Terrain pull-in, same math as native CameraMotion's linecast block.
            Vector3 armN = (planePos - camPos).normalized;
            int mask = tr.Field("layerMask").GetValue<int>();
            if (Physics.Linecast(planePos, camPos, out RaycastHit hitInfo, mask))
            {
                float dot = Mathf.Max(Vector3.Dot(armN, hitInfo.normal), 0.1f);
                camPos = hitInfo.point + armN / dot;
            }

            ct.SetPositionAndRotation(camPos,
                _aimRot * Quaternion.Euler(Cfg.CameraPitchOffset.Value, 0f, 0f));
            return true;
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

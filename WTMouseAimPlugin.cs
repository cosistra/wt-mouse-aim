using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
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
        public const string PluginVersion = "0.13.0";

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
            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded (point-and-chase + Win32 raw mouse + any-camera chase — tune live via F1).");
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

            Transform t = ac.transform;
            float dist = Cfg.AimDistance.Value;
            float off = Vector3.Angle(t.forward, AimRig.AimForward); // deg the nose is off the marker

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

            if (boreVis && off >= 5f) // hide the boresight once the nose is basically on the marker
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
            GUI.color = prev;
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
        public static ConfigEntry<float> AggressiveTurnAngle; // deg off-target at which we commit to a hard bank
        public static ConfigEntry<float> RollGain;            // roll output scale (negative flips roll direction)
        public static ConfigEntry<float> PitchGain;           // pitch output scale (negative flips)
        public static ConfigEntry<float> YawGain;             // yaw/rudder output scale (negative flips)
        public static ConfigEntry<float> OutputSlew;          // max stick units/sec (anti-jerk rate limit)
        public static ConfigEntry<float> AuthorityRamp;       // engage/disengage blend speed (1/sec)

        // --- Cockpit camera follow.
        public static ConfigEntry<bool>  CameraFollow;        // smoothly look toward the marker
        public static ConfigEntry<float> CameraFollowAmount;  // 0..1 fraction of the marker offset to look toward
        public static ConfigEntry<float> CameraFollowSmoothing;// seconds-ish smoothing (higher = lazier)

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
            ChaseDamping        = cf.Bind("Control", "ChaseDamping", 0.0f, new ConfigDescription(
                "Calms the inputs as the nose nears the circle so it eases in instead of overshooting — opposes the nose's own turn rate. 0 = off (default; snappy). Raise toward ~0.3 ONLY if the plane death-wobbles/oscillates around the aim direction.",
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

            CameraFollow          = cf.Bind("Camera", "CameraFollow", true,
                "Smoothly turn the cockpit view toward the aim circle, so you look where you're steering.");
            CameraFollowAmount    = cf.Bind("Camera", "CameraFollowAmount", 0.5f, new ConfigDescription(
                "How far the view leans toward the circle. 0 = view stays forward (circle moves freely on screen); 1 = looks fully at the circle (which then sits glued to screen-centre — you can't see it lead the nose). ~0.5 lets the circle visibly lead.",
                new AcceptableValueRange<float>(0f, 1f)));
            CameraFollowSmoothing = cf.Bind("Camera", "CameraFollowSmoothing", 0.3f, new ConfigDescription(
                "View-follow lag (seconds-ish). Higher = lazier, smoother camera; lower = snappier, can feel twitchy.",
                new AcceptableValueRange<float>(0.02f, 1f)));
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
    // The aim rig. The marker is a WORLD-LOCKED unit direction (not an airframe-relative offset):
    // mouse delta nudges it (screen-relative, since the cockpit cam rolls with the plane we use the
    // aircraft axes), and each frame it is clamped to within MaxAimAngle of the current nose. Because
    // it is world-locked, the plane can fly its nose ONTO it and the offset shrinks to zero — that is
    // what makes the controller a point-and-chase instructor instead of a rate joystick. Re-seeds to
    // the boresight whenever we (re)acquire an aircraft or leave cockpit.
    internal static class AimRig
    {
        private static Vector3 _aimForward = Vector3.zero; // world-space unit direction (the marker)
        private static int _lastAircraftId = -1;
        private static Vector2 _smoothedDelta;             // one-pole-smoothed mouse delta
        private static bool _captured;                     // we are reading the mouse for aiming this frame
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
            bool ok = TryGetContext(out var ac, out _);
            bool context = Cfg.Enabled.Value && ok;

            var pi = GameManager.playerInput;
            bool freeLook = pi != null && pi.GetButton("Free Look");
            bool inCockpit = CameraStateManager.cameraMode == CameraMode.cockpit;

            if (!context)
            {
                ReleaseCursor();       // hand the cursor back to the game (normal pointer for menus)
                _lastAircraftId = -1;  // force re-seed on next acquire
                _smoothedDelta = Vector2.zero;
                return;
            }

            // Choose a cursor regime that COOPERATES with the game's CursorManager (see ApplyCursorRegime):
            //   aimCapture — cockpit mouse-aim: hidden + lockState=None + Win32 recentre (raw delta from
            //                frame 1, no alt-tab needed).
            //   flyHidden  — flying but not aim-capturing (external/orbit, or Free Look held): hidden +
            //                lockState=Locked, the game's own flying regime. 3rd-person orbit free-look
            //                ONLY reads the look axes when the cursor is hidden (CameraOrbitState gates on
            //                !Cursor.visible), so leaving it visible — our old release behaviour — was
            //                exactly why free-look died in 3p.
            //   visible    — a menu/pause/UI wants the pointer.
            bool flying     = Cfg.WriteControl.Value;  // context is already true here
            bool menuWants  = Guards.MenusOpen() || CursorManager.GetFlags() != CursorFlags.None;
            bool flyHidden  = flying && !menuWants;
            bool aimCapture = flyHidden && !freeLook && inCockpit;
            ApplyCursorRegime(flyHidden, aimCapture);

            Transform t = ac.transform;
            int id = ac.GetInstanceID();
            if (id != _lastAircraftId || _aimForward == Vector3.zero)
            {
                _aimForward = t.forward; // seed on the nose
                _lastAircraftId = id;
                _smoothedDelta = Vector2.zero;
            }

            // The marker is WORLD-LOCKED (exactly v0.10): once placed in world space it stays put while
            // the plane flies its nose onto it and the offset converges to zero — the point-and-chase
            // instructor feel. The only thing that moves it is the mouse nudge below and the cone clamp.
            // Nudge the marker by mouse delta only while we own the cursor for aiming; otherwise it stays
            // parked in the world (frozen during menus/map/pause/free-look/external cam).
            Vector2 raw = Vector2.zero;
            if (aimCapture)
            {
                raw = ReadMouseDelta();
                float sm = Mathf.Clamp01(Cfg.MouseSmoothing.Value);
                _smoothedDelta = Vector2.Lerp(raw, _smoothedDelta, sm); // sm=0 -> raw, higher -> smoother/laggier

                float sens = Cfg.MouseSensitivity.Value;
                float pan  = _smoothedDelta.x;
                float tilt = _smoothedDelta.y * (Cfg.InvertPitch.Value ? -1f : 1f);
                // Rotate about the aircraft up/right (== screen up/right in cockpit; rolls with the plane).
                _aimForward = Quaternion.AngleAxis(pan * sens, t.up)
                            * Quaternion.AngleAxis(-tilt * sens, t.right)
                            * _aimForward;
            }
            else
            {
                _smoothedDelta = Vector2.zero; // drop stale delta so it doesn't lurch on resume
            }

            // Clamp to the cone around the CURRENT nose. If inside the cone this is a no-op (marker
            // stays world-locked); at the edge it "sticks" = a sustained max-rate turn command.
            _aimForward = Vector3.RotateTowards(t.forward, _aimForward, Cfg.MaxAimAngle.Value * Mathf.Deg2Rad, 0f).normalized;

            if (Cfg.DebugLogging.Value && Time.frameCount % 30 == 0)
            {
                TryGetContext(out _, out var dcam);
                float camOff = dcam != null ? Vector3.Angle(dcam.transform.forward, t.forward) : -1f;
                WTMouseAimPlugin.Log.LogInfo(
                    $"[aim] raw=({raw.x:0.00},{raw.y:0.00}) sm=({_smoothedDelta.x:0.00},{_smoothedDelta.y:0.00}) " +
                    $"markerOff={Vector3.Angle(t.forward, _aimForward):0.00}deg camOff={camOff:0.00}deg cap={_captured}");
            }
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
        private static Vector3 _prevFwd;  // last frame's nose direction (for rotation-rate damping)
        private static bool  _prevFwdValid;

        public static bool IsFlying => _active;

        // Called from the prefix. Returns true if WE own the stick (native should be skipped).
        public static bool BeginFrame(Aircraft aircraft, bool fixedWing, float pilotStrength)
        {
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
                float pitchRate = 0f, yawRate = 0f;
                if (_prevFwdValid && dt > 1e-5f)
                {
                    Vector3 noseRate = (t.forward - _prevFwd) / dt;
                    pitchRate = Vector3.Dot(noseRate, t.up);    // +: nose swinging up
                    yawRate   = Vector3.Dot(noseRate, t.right); // +: nose swinging right
                }
                _prevFwd = t.forward;
                _prevFwdValid = true;

                // PITCH/YAW: proportional pull toward the marker, MINUS a damping term on the nose's own
                // turn rate so the inputs fade out as it closes instead of overshooting (the cure for the
                // wobble). In this game nose-up = NEGATIVE ci.pitch, so for an above-nose marker -local.y
                // already gives a pull-up command; +pitchRate*damp opposes the climb as the nose arrives.
                float damp = Cfg.ChaseDamping.Value;
                float tgtP = Mathf.Clamp((-local.y * sens + pitchRate * damp) * Cfg.PitchGain.Value, -1f, 1f);
                float tgtY = Mathf.Clamp(( local.x * sens - yawRate   * damp) * Cfg.YawGain.Value,   -1f, 1f);

                // ROLL: bank-to-turn blend. Far off-target => bank hard toward the marker; on-target
                // => level the wings (t.right.y is the world-up component on the wing: 0 = level,
                // <0 = right wing down). As the nose arrives, off->0 so roll->wings-level and the
                // marker returns to the boresight: convergence, not a sustained rate.
                float aggressiveRoll = Mathf.Clamp(local.x * sens, -1f, 1f);
                float wingsLevelRoll = t.right.y;
                // Linear blend (no ease-in square — that was starving the bank on small/medium offsets
                // and leaving the weak rudder to crawl the last few degrees: the "deadzone"). At/above
                // AggressiveTurnAngle it fully commits to the bank; below it rolls in proportionally.
                float rollBlend = Mathf.Clamp01(off / Mathf.Max(1f, Cfg.AggressiveTurnAngle.Value));
                float tgtR = Mathf.Clamp(Mathf.Lerp(wingsLevelRoll, aggressiveRoll, rollBlend) * Cfg.RollGain.Value, -1f, 1f);

                float slew = Cfg.OutputSlew.Value * dt; // anti-jerk rate limit
                _outP = Mathf.MoveTowards(_outP, tgtP, slew);
                _outR = Mathf.MoveTowards(_outR, tgtR, slew);
                _outY = Mathf.MoveTowards(_outY, tgtY, slew);

                ci.pitch = Mathf.Clamp(_outP, -1f, 1f);
                ci.roll  = Mathf.Clamp(_outR, -1f, 1f);
                ci.yaw   = Mathf.Clamp(_outY, -1f, 1f);
                _disRamp = 1f; // primed for the next disengage

                if (Cfg.DebugLogging.Value && Time.frameCount % 30 == 0)
                    WTMouseAimPlugin.Log.LogInfo(
                        $"[chase] off={off:0.00}deg local=({local.x:0.000},{local.y:0.000}) " +
                        $"tgt P/R/Y=({tgtP:0.00},{tgtR:0.00},{tgtY:0.00}) out P/R/Y=({_outP:0.00},{_outR:0.00},{_outY:0.00})");
            }
            else if (_disRamp > 0f)
            {
                // Disengaging: native just wrote ci. Ramp from our last output back to native.
                _disRamp = Mathf.Max(0f, _disRamp - Cfg.AuthorityRamp.Value * dt);
                float a = _disRamp;
                ci.pitch = Mathf.Clamp(Mathf.Lerp(ci.pitch, _outP, a), -1f, 1f);
                ci.roll  = Mathf.Clamp(Mathf.Lerp(ci.roll,  _outR, a), -1f, 1f);
                ci.yaw   = Mathf.Clamp(Mathf.Lerp(ci.yaw,   _outY, a), -1f, 1f);
            }
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

            var pi = GameManager.playerInput;
            if (pi != null && pi.GetButton("Free Look"))
            { _overriding = false; return; } // let native free-look look around

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
}

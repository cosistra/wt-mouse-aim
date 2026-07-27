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
        public const string PluginVersion = "0.68.0";

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

        // RUN INDEX (v0.63) — a small monotonic integer that survives game restarts, so two boots of the
        // game are "R7" and "R8" rather than two opaque wallclock ids. The session id above is unique but
        // unorderable at a glance; when a report says "the wobble in R8 rec 3" that has to be greppable.
        // Backed by a one-line counter file next to LogOutput.log, bumped once per Awake. Fail-soft: any
        // IO problem yields run 0, which reads as "unknown run" rather than colliding with a real one.
        private static int _runIndex = -1;
        internal static int RunIndex
        {
            get
            {
                if (_runIndex >= 0) return _runIndex;
                _runIndex = 0;
                try
                {
                    string p = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "mouseaim-run.txt");
                    int last = 0;
                    if (System.IO.File.Exists(p)) int.TryParse(System.IO.File.ReadAllText(p).Trim(), out last);
                    _runIndex = last + 1;
                    System.IO.File.WriteAllText(p, _runIndex.ToString());
                }
                catch { /* leave 0 = unknown run */ }
                return _runIndex;
            }
        }

        // Filename-safe version + run tag shared by every artifact this session ("v0.63.0-R8").
        internal static string RunTag => $"v{PluginVersion}-R{RunIndex}";

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
            // ponytail: the load line is a LOAD LINE — version + keys + where the history lives.
            // (It used to mirror the entire changelog; that lives in CHANGELOG.md now.)
            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded — WT-style mouse-aim instructor (EvolvedLegacy law). "
                + $"Keys: [{Cfg.ToggleKey.Value}] master on/off, [{Cfg.FlyLevelKey.Value}] fly level, "
                + $"[{Cfg.RecordKey.Value}] maneuver recorder, F1 config, RMB free-look. "
                + "Version history: CHANGELOG.md.");
            Logger.LogInfo($"[session] run R{RunIndex}  id {SessionId}  ({RunTag}) — recordings, the anomaly file and this log share this id for cross-referencing.");
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
            else if (Time.time >= _toastUntil) { _toastRec = false; }
        }

        // True while the active toast is a recorder toast (so OnGUI labels it REC/REC OFF, not ON/OFF).
        private static bool _toastRec;

        private void OnGUI()
        {
            // Master-toggle toast — drawn BEFORE the overlay/enabled guard so it confirms an OFF flip too.
            if (Time.time < _toastUntil)
            {
                var tc = GUI.color;
                // REC/master toasts are cyan-on / amber-off.
                GUI.color = _toastOn ? new Color(0.3f, 0.9f, 1f, 0.95f) : new Color(1f, 0.7f, 0.3f, 0.95f);
                const float tw = 300f;
                string msg = _toastRec ? (_toastOn ? $"MouseAim  REC START  {ManeuverRecorder.Tag}"
                                                   : $"MouseAim  REC STOP  {ManeuverRecorder.Tag}")
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
                const float rw = 300f;
                GUI.Label(new Rect((Screen.width - rw) * 0.5f, Screen.height * 0.08f, rw, 24f),
                    $"● REC  {ManeuverRecorder.Tag}  {ManeuverRecorder.Elapsed:0.0}s  ({ManeuverRecorder.Samples})");
                GUI.color = rc;
            }

            if (!Cfg.ShowOverlay.Value || !Cfg.Enabled.Value)
                return;
            if (!AimRig.TryGetContext(out var ac, out var cam))
                return;
            if (ac.disabled) // plane destroyed/disabled — nothing to aim, so draw nothing
                return;

            // G-LOC fade-to-black (v0.55): progressively grey the whole screen as the pilot's G-tolerance
            // drops toward the 0.2 blackout point, so third-person pilots (who get none of the game's
            // cockpit-only black-out) see it coming instead of an instant control cut. Drawn FIRST so the
            // reticle/HUD/amber text all render on top of the grey (IMGUI draws in call order). Same signal
            // as the amber "OVER-G" text below; reuses the 1x1 _px texture.
            if (Cfg.GLocFadeEnabled.Value)
            {
                // t = 0 at the onset, 1 at the 0.2 blackout point (InverseLerp clamps and stays 1 below it).
                float ft = Mathf.InverseLerp(Cfg.GLocFadeOnset.Value, 0.2f, ChaseController.PilotStrength);
                float alpha = ft * Cfg.GLocFadeMaxAlpha.Value;
                if (alpha > 0f)
                {
                    var fc = GUI.color;
                    GUI.color = new Color(0f, 0f, 0f, alpha);
                    GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _px);
                    GUI.color = fc;
                }
            }

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
                    $"WT MouseAim  off={off:0.0}°  cone={half:0}°  law=EL  [{ctrl}]  {spd}");
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

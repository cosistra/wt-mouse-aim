using System.Collections.Generic;
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
        public const string PluginVersion = "0.96.0";

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
            // The drone seam is patched UNCONDITIONALLY, even with Drone/DroneEnabled off: the flag is
            // live-tunable from F1, so a conditional patch would need a restart to take effect. The
            // postfix costs one integer compare per aircraft per fixed step while no drone is alive.
            harmony.PatchAll(typeof(TestDronePatch));
            // ponytail: the load line is a LOAD LINE — version + keys + where the history lives.
            // (It used to mirror the entire changelog; that lives in CHANGELOG.md now.)
            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded — WT-style mouse-aim instructor (EvolvedLegacy law). "
                + $"Keys: [{Cfg.ToggleKey.Value}] master on/off, [{Cfg.FlyLevelKey.Value}] fly level, "
                + $"[{Cfg.RecordKey.Value}] maneuver recorder, F1 config, RMB free-look. "
                // Card keys belong here too: CLAUDE.md promises this line lists every active binding.
                + $"Cards: [{Cfg.ScenarioRunKey.Value}] run, [{Cfg.ScenarioRecordKey.Value}] record, "
                + $"[{Cfg.ScenarioAbortKey.Value}] abort, [{Cfg.ScenarioEntryKey.Value}] on-condition. "
                + $"Drones ({(Cfg.DroneEnabled.Value ? "on" : "off")}): [{Cfg.DroneSpawnKey.Value}] spawn, "
                + $"[{Cfg.DroneDespawnKey.Value}] despawn all. "
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

            // FRAME TIME, sampled here because here is the only place it is real: Time.unscaledDeltaTime
            // returns fixedUnscaledDeltaTime (a constant) when read from FixedUpdate, which is where
            // this lived until v0.92.1 and why the recorder's `frameMs` column held one value for a
            // whole 352-capture batch. Unconditional, like the AimRig call below — it is a recorder
            // signal, not a drone signal, and the harness's own gate is inside it.
            TestDrone.SampleFrameTime();

            AimRig.Update();

            // Fly Level toggle (v0.24). Edge-triggered; needs an aircraft to latch the heading onto.
            if (Cfg.Enabled.Value && Cfg.FlyLevelEnabled.Value &&
                Input.GetKeyDown(Cfg.FlyLevelKey.Value) &&
                AimRig.TryGetContext(out var ac, out _) && !ac.disabled)
            {
                ChaseController.For(ac).ToggleFlyLevel(ac);
            }

            // Maneuver recorder toggle (v0.35). Ungated by aircraft/Enabled so it can always be stopped;
            // it only writes rows while the chase is actually flying (Sample is called from Apply).
            if (Input.GetKeyDown(Cfg.RecordKey.Value))
            {
                bool on = ManeuverRecorder.ToggleLocal();   // v0.86: the LOCAL player's recorder
                _toastUntil = Time.time + 2f;
                _toastOn = on; // reuse the toast: cyan "REC" on, amber off (label switched in OnGUI)
                _toastRec = true;
            }
            else if (Time.time >= _toastUntil) { _toastRec = false; }

            // Scenario player (M1). Key EDGES are a per-frame thing, so they're read here; everything
            // that has to be deterministic (the card clock, the demand write) happens on the fixed
            // step inside ScenarioPlayer.Tick. Ungated by Enabled so a running card can always be
            // stopped, and idle unless one of these is pressed.
            if (Input.GetKeyDown(Cfg.ScenarioRunKey.Value))    ScenarioPlayer.ToggleSuite();
            if (Input.GetKeyDown(Cfg.ScenarioRecordKey.Value)) ScenarioPlayer.ToggleRecord();
            if (Input.GetKeyDown(Cfg.ScenarioAbortKey.Value))  ScenarioPlayer.AbortLocal("abort key");
            if (Input.GetKeyDown(Cfg.ScenarioEntryKey.Value))  ScenarioPlayer.ForceEntryNow();

            // Uncrewed test drones (v0.81, phase 1). Gated on DroneEnabled so the keys are DEAD, not
            // merely harmless, while the harness is off — F2/F9 are otherwise perfectly ordinary keys
            // and nobody expects a flight-control mod to consume them.
            if (Cfg.DroneEnabled.Value)
            {
                if (Input.GetKeyDown(Cfg.DroneSpawnKey.Value))   TestDrone.RequestLaunch();
                if (Input.GetKeyDown(Cfg.DroneDespawnKey.Value)) TestDrone.DespawnAll();
            }

            // Sandbox (v0.95): put the OPERATOR airborne. Deliberately OUTSIDE the DroneEnabled gate
            // above — this is for hand-flying the law, not part of the harness, and needing to arm
            // the drone subsystem to use it would be a lie about what it does.
            if (Input.GetKeyDown(Cfg.SandboxKey.Value)) PlayerSpawn.Trigger();
        }

        // The mod's only fixed-step hook that exists independently of an aircraft. The drone harness
        // needs one: its launch stagger has to be counted on the same clock the run is measured on
        // (not a coroutine, and not the render frame), and with zero drones alive there is no pilot of
        // ours for the drone seam's per-pilot postfix to fire on. Also where frame time is sampled.
        private void FixedUpdate()
        {
            TestDrone.FixedTick();
        }

        // True while the active toast is a recorder toast (so OnGUI labels it REC/REC OFF, not ON/OFF).
        private static bool _toastRec;

        private void OnGUI()
        {
            // v0.86: the recorder and the card player are per-aircraft now, so the HUD reads the LOCAL
            // player's — never a drone's, exactly as it already did for ChaseController.Player. Both are
            // null until he has an aircraft, so every read below is null-guarded.
            var rec  = ManeuverRecorder.Player;
            var card = ScenarioPlayer.Player;

            // Master-toggle toast — drawn BEFORE the overlay/enabled guard so it confirms an OFF flip too.
            if (Time.time < _toastUntil)
            {
                var tc = GUI.color;
                // REC/master toasts are cyan-on / amber-off.
                GUI.color = _toastOn ? new Color(0.3f, 0.9f, 1f, 0.95f) : new Color(1f, 0.7f, 0.3f, 0.95f);
                const float tw = 300f;
                string tag = rec != null ? rec.Tag : "";
                string msg = _toastRec ? (_toastOn ? $"MouseAim  REC START  {tag}"
                                                   : $"MouseAim  REC STOP  {tag}")
                                       : (_toastOn ? "WT MouseAim  ON"      : "WT MouseAim  OFF");
                GUI.Label(new Rect((Screen.width - tw) * 0.5f, Screen.height * 0.12f, tw, 24f), msg);
                GUI.color = tc;
            }

            // Persistent recording indicator — drawn BEFORE every gate (even on the clean HUD / mod-off)
            // so a running capture is always visible. Top-centre, red, with elapsed time + sample count.
            if (rec != null && rec.IsRecording)
            {
                var rc = GUI.color;
                GUI.color = new Color(1f, 0.25f, 0.2f, 0.95f);
                const float rw = 300f;
                GUI.Label(new Rect((Screen.width - rw) * 0.5f, Screen.height * 0.08f, rw, 24f),
                    $"● REC  {rec.Tag}  {rec.Elapsed:0.0}s  ({rec.Samples})");
                GUI.color = rc;
            }

            // Scenario NOTICE — why a card refused to start, or that it just moved the aircraft onto
            // its entry condition. Drawn before every gate (and whether or not a card is running,
            // since the common case is that one DIDN'T start): pressing the run key must never look
            // like pressing a dead key. Amber, just under the card indicator.
            string notice = ScenarioPlayer.Notice;
            if (!string.IsNullOrEmpty(notice))
            {
                var nc = GUI.color;
                GUI.color = new Color(1f, 0.75f, 0.2f, 0.95f);
                const float nw = 640f;
                GUI.Label(new Rect((Screen.width - nw) * 0.5f, Screen.height * 0.09f, nw, 24f), notice);
                GUI.color = nc;
            }

            // Scenario/test-card indicator — like the REC indicator, drawn BEFORE every gate so a
            // running card is visible even on the clean HUD: which card, which segment, time left.
            if (card != null && card.Active)
            {
                var cc = GUI.color;
                GUI.color = new Color(0.5f, 1f, 0.5f, 0.95f);
                const float cw = 520f;
                GUI.Label(new Rect((Screen.width - cw) * 0.5f, Screen.height * 0.05f, cw, 24f), card.HudLine);
                GUI.color = cc;
            }

            DrawRunBoard();

            if (!Cfg.ShowOverlay.Value || !Cfg.Enabled.Value)
                return;
            if (!AimRig.TryGetContext(out var ac, out var cam))
                return;
            if (ac.disabled) // plane destroyed/disabled — nothing to aim, so draw nothing
                return;

            // v0.82: the instructor is per-aircraft now, so the HUD reads the LOCAL PLAYER's
            // controller rather than a set of statics. Null until it has flown one fixed step (and
            // after a respawn), so every read below carries its pre-0.82 static default as the
            // fallback — the overlay looks identical, it just can no longer show a drone's numbers.
            var chase = ChaseController.Player;

            // G-LOC fade-to-black (v0.55): progressively grey the whole screen as the pilot's G-tolerance
            // drops toward the 0.2 blackout point, so third-person pilots (who get none of the game's
            // cockpit-only black-out) see it coming instead of an instant control cut. Drawn FIRST so the
            // reticle/HUD/amber text all render on top of the grey (IMGUI draws in call order). Same signal
            // as the amber "OVER-G" text below; reuses the 1x1 _px texture.
            if (Cfg.GLocFadeEnabled.Value)
            {
                // t = 0 at the onset, 1 at the 0.2 blackout point (InverseLerp clamps and stays 1 below it).
                float ft = Mathf.InverseLerp(Cfg.GLocFadeOnset.Value, 0.2f, chase != null ? chase.PilotStrength : 1f);
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
                            : chase != null && chase.IsFlying ? "FLYING (mod owns stick)"
                            : "native";
                string spd = chase != null && chase._collective
                    ? $"spd={chase._speed:0} m/s (fwd={chase._vFwd:0}, heliBlend={chase._heliBlend:0.00})"
                    : $"spd={(chase != null ? chase._speed : 0f):0} m/s";
                GUI.Label(new Rect(12f, 12f, 560f, 22f),
                    $"WT MouseAim  off={off:0.0}°  cone={half:0}°  law=EL  [{ctrl}]  {spd}");
                // Instructor's live stick command (what the mod is telling the plane, before manual override).
                GUI.Label(new Rect(12f, 30f, 560f, 22f),
                    $"instructor  pitch={(chase != null ? chase.LastPitch : 0f):+0.00;-0.00;0.00}  " +
                    $"yaw={(chase != null ? chase.LastYaw : 0f):+0.00;-0.00;0.00}  roll={(chase != null ? chase.LastRoll : 0f):+0.00;-0.00;0.00}");
            }
            // Fly Level indicator — distinct cyan so it's obvious the marker is being ignored on purpose.
            if (chase != null && chase.FlyLevelActive)
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
                if (chase != null && chase.IsFlying && !string.IsNullOrEmpty(chase.LastPhase))
                {
                    GUI.color = Color.white;
                    GUI.Label(new Rect(12f, 84f, 560f, 22f), $"PHASE: {chase.LastPhase}");
                }
            }
            GUI.color = prev;

            // G-LOC warning: while the pilot is blacked/redded out from sustained G the game zeroes all
            // stick input (PilotPlayerState: pilotStrength < 0.2), so the mod can't fly either. Surface it
            // discreetly in amber, centred near the top, so the loss of control is explained rather than
            // feeling like a mod bug.
            if (chase != null && chase.PilotStrength < 0.2f)
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

        // =============================================================================================
        // HARNESS RUN BOARD (v0.90). An unattended drone batch is 20+ minutes of wall clock whose only
        // progress signal was `[card]` lines in LogOutput.log — so "is it still going?" and "how long
        // left?" meant alt-tabbing to a text file. This is that answer on screen, plus the PREFLIGHT of
        // what WILL fly, which is the more valuable half: every setup mistake this harness can make
        // (no card ticked, the Drone knobs disagreeing with the card) is invisible until after the
        // launch, and then costs the whole batch.
        //
        // DRAWN PRE-GATE, i.e. before ShowOverlay/Enabled and before the local-aircraft resolve. That
        // is not laziness about where to put it: the operator watching a batch is usually in no
        // aircraft at all (ejected, spectating, sitting in the map screen), which is exactly when
        // every gate below would have returned already.
        //
        // Top-LEFT, below y=110: the other pre-gate items are top-CENTRE, and 110 clears the
        // post-gate debug ladder (rows at y=12..84, 22 high) so the two never overlap when the
        // operator is flying with ShowDebugHud on.
        private const float BoardX = 12f, BoardTop = 110f, BoardW = 780f, BoardRow = 18f;
        // DroneCount goes to 16 and a 17-line panel is unreadable; the tail collapses to a count.
        private const int   BoardMaxRows = 8;

        // Reused, not allocated per call: OnGUI runs at least twice a frame (layout + repaint).
        private static readonly List<ScenarioPlayer> _board = new List<ScenarioPlayer>();

        private static void DrawRunBoard()
        {
            // The whole cost of this feature when the harness is not in use: one bool read. Deliberately
            // NOT gated on ShowDebugHud as well — the board is the harness's only progress instrument,
            // and an operator who ticked DroneEnabled has already said he is running a batch.
            if (!Cfg.DroneEnabled.Value) return;

            ScenarioPlayer.CollectRunning(_board);
            if (_board.Count > 0) DrawFlying();
            else                  DrawPreflight();
        }

        private static readonly Color BoardGreen = new Color(0.5f, 1f, 0.5f, 0.95f);   // running, matches the card indicator
        private static readonly Color BoardAmber = new Color(1f, 0.75f, 0.2f, 0.95f);  // warning / idle, matches Notice
        private static readonly Color BoardDim   = new Color(0.72f, 0.72f, 0.72f, 0.9f);

        // Dim backing panel, sized to the row count — IMGUI text over a bright sky is unreadable
        // otherwise, and this one is read at a glance from across the room.
        private static void BoardPanel(int rows)
        {
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.DrawTexture(new Rect(BoardX - 6f, BoardTop - 4f, BoardW + 12f, rows * BoardRow + 8f), _px);
            GUI.color = prev;
        }

        private static void BoardLine(int row, Color col, string text)
        {
            var prev = GUI.color;
            GUI.color = col;
            GUI.Label(new Rect(BoardX, BoardTop + row * BoardRow, BoardW, BoardRow + 4f), text);
            GUI.color = prev;
        }

        private static void DrawFlying()
        {
            int n = _board.Count, shown = Mathf.Min(n, BoardMaxRows);
            BoardPanel(1 + shown + (n > shown ? 1 : 0));

            // Header aggregates over the MAX: the batch is finished when the slowest aircraft is, and
            // the drones are launched on a stagger, so the leader's ETA would read as "nearly done"
            // with a full card still to fly on the last lane.
            int runI = 0, runN = 0; float left = 0f;
            for (int i = 0; i < n; i++)
            {
                var s = _board[i];
                if (s.RunIndex > runI) runI = s.RunIndex;
                if (s.RunCount > runN) runN = s.RunCount;
                if (s.SuiteSecondsLeft > left) left = s.SuiteSecondsLeft;
            }
            BoardLine(0, BoardGreen,
                $"HARNESS  {n} flying   run {runI}/{runN}   {ScenarioPlayer.Clock(left)} left");

            for (int i = 0; i < shown; i++)
            {
                var s = _board[i];
                // The local player's own aircraft can be flying a card too (he presses the run key
                // while the drones fly theirs); DroneIdOf returns 0 for anything not in the harness.
                int d = TestDrone.DroneIdOf(s.AircraftId);
                BoardLine(1 + i, BoardGreen,
                    $" {(d > 0 ? "#" + d : "YOU")} {s.PlaneName}   {s.CardName}   "
                  + $"run {s.RunIndex}/{s.RunCount}  arm {s.ArmLabel}   "
                  + $"seg {s.SegIndex}/{s.SegCount} '{s.SegTag}'  {ScenarioPlayer.Clock(s.SegSecondsLeft)}   "
                  + $"card {ScenarioPlayer.Clock(s.CardSecondsLeft)}   {s.RecSamples} samples");
            }
            if (n > shown) BoardLine(1 + shown, BoardDim, $" ...and {n - shown} more");
        }

        // WHAT WILL FLY, before the key is pressed. Every value comes from ScenarioPlayer.Preview()
        // and TestDrone's own three resolvers — the same pair the launch itself uses — so the board
        // physically cannot promise something different from what spawns.
        // Polled, not recomputed per draw: Preview() walks the card library and builds its "who
        // decided" strings, and OnGUI runs at least twice a frame for a panel whose inputs only
        // change when the operator ticks a checkbox. Half a second is under human reaction time.
        private static float _preAt = -999f;
        private static ScenarioPlayer.Preflight _pre;

        private static void DrawPreflight()
        {
            if (Time.unscaledTime - _preAt > 0.5f)
            {
                _preAt = Time.unscaledTime;
                _pre = ScenarioPlayer.Preview(true);   // quiet: this is a repaint, not an operator action
            }
            var p = _pre;
            // CountOf, not Cfg.DroneCount: since v0.91 the card's airframe list decides the fleet size,
            // so the knob is only one of three possible answers and quoting it would be wrong exactly
            // when the card is driving — which is the case this panel exists to make visible.
            string head = $"HARNESS  ready   [{Cfg.DroneSpawnKey.Value}] to launch {TestDrone.CountOf(p)}";

            if (p.Cards == 0)
            {
                // THE #1 SETUP MISTAKE, and until now it only surfaced as a log warning AFTER the
                // launch — by which point N drones are airborne flying a level-hold that measures
                // nothing. Amber, and it names the fix.
                BoardPanel(2);
                BoardLine(0, BoardAmber, head);
                BoardLine(1, BoardAmber, "  NO CARD SELECTED — the drones would fly the level-hold and measure nothing. "
                                       + "Tick one in F1 > 'Scenario Cards'.");
                return;
            }

            BoardPanel(4);
            BoardLine(0, BoardGreen, head);
            BoardLine(1, BoardDim,
                $"  card  {p.Name}{(p.Cards > 1 ? $" (+{p.Cards - 1} more)" : "")}  x{p.Repeat} runs   "
              + $"{p.AllDuration:0}s each   {ScenarioPlayer.Clock(p.AllDuration * p.Repeat)} per drone");
            // PER VALUE, not one marker for the line: airframe, altitude and speed fall back
            // independently, and "the card is driving this run" is only true of the ones marked so.
            // This distinction is the whole reason the panel is worth drawing before a launch.
            BoardLine(2, BoardDim,
                $"  plant {TestDrone.AirframeOf(p)} {Src(!string.IsNullOrEmpty(p.Airframe))}   "
              + $"{TestDrone.AltOf(p):0} m {Src(p.StartAlt > 0f)}   "
              // SpeedText, not a number: a v0.93 corner-relative card has a DIFFERENT entry speed per
              // lane, so it reads "1.00x corner (per airframe)". Printing one number here would be a
              // promise the spawn does not keep — on a panel whose whole job is that it cannot be.
              + $"{TestDrone.SpeedText(p)} {Src(TestDrone.SpeedFromCard(p))}   "
              + $"x{TestDrone.CountOf(p)} drones ({p.CountSrc})");
            BoardLine(3, BoardDim, string.IsNullOrEmpty(p.ArmKnob)
                ? "  A/B   none — one arm; set a card's armToggle or Scenario/ScenarioArmToggle to interleave"
                // "per aircraft" earns its width: until v0.94 a multi-drone launch stood the schedule
                // down, so an operator who learned that rule still believes he must fly A/Bs one at a
                // time. This line is where he finds out he no longer does.
                : $"  A/B   {p.ArmKnob}   ABBA per aircraft, by run index   (from {p.ArmSrc})");
        }

        private static string Src(bool fromCard) => fromCard ? "[from card]" : "[from F1]";

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

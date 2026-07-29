using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace NuclearOptionMouseAim
{
    // ---------------------------------------------------------------------------------------------
    // SCENARIO PLAYER — M1 of plans/instructor-feedback-loop.md ("close the loop", §5/§5.2).
    //
    // Flies a TEST CARD by substituting the aim demand: a card is an ordered list of segments, each
    // holding (or replaying) an aim direction for a fixed number of seconds, so the same stimulus can
    // be re-flown against a later build and the two CSVs diffed. It writes ONLY AimRig.SetAimForward —
    // the control law, its gains and every schedule are untouched, which is what makes a run a valid
    // measurement of the law rather than of the harness.
    //
    // THREE THINGS MAKE IT MEASURABLE RATHER THAN JUST AUTOMATED:
    //   1. ZERO-TICK LAG (plan §5.1 M-1). Tick() is called from PilotPlayerStatePatch.Prefix, i.e.
    //      inside the same PlayerAxisControls invocation whose POSTfix calls ChaseController.Apply —
    //      so the demand for step N is written before Apply reads AimRig.AimForward on step N. A bare
    //      Update/FixedUpdate would leave the ordering to Unity and put a frame-rate-dependent
    //      zero-order hold between the demand and the law (exactly the coupling M-1 names).
    //   2. WORLD-FIXED DEMAND. The heading frame is captured ONCE at card start and every az/el in the
    //      card is resolved in that frame. The demand therefore never chases the nose: the aircraft
    //      converging on it is a real closed-loop response, not a treadmill.
    //   3. FIXED-STEP RECORDING. A recorded card samples AimRig.AimForward on the same fixed-step
    //      clock, so replay is frame-rate independent and a recorded card is indistinguishable from a
    //      scripted one (both end up as az/el in the card frame).
    //
    // Nothing here runs until a hotkey starts it: with no card running Tick() is two field reads and a
    // return, so flight behaviour is byte-identical to a build without this file.
    internal static class ScenarioPlayer
    {
        // -----------------------------------------------------------------------------------------
        // CARD MODEL. JsonUtility shape: [Serializable], PUBLIC FIELDS ONLY. Unity's own serializer
        // ships in UnityEngine.CoreModule (already referenced), so there is deliberately no parser in
        // this file — a hand-rolled one would be code to maintain for nothing. Its behaviour is the
        // fail-soft contract the probes already use: unknown keys are ignored, missing keys keep the
        // C# default, and a malformed file throws where Load() catches it.
        // `cls` (not `class`) because the latter is a C# keyword; the JSON key must match the field.
        [System.Serializable]
        internal class Seg
        {
            public string tag = "";     // recorder SegmentTag for every row of this segment
            public float  dur;          // seconds
            public float  az, el;       // constant demand (deg, card frame) — used when there is no track
            public bool   deriveAzRate; // sweep az at the per-airframe rate derived at card start
            public float[] trackAz;     // per-step demand samples (deg); null/empty => constant az/el
            public float[] trackEl;
        }

        [System.Serializable]
        internal class Card
        {
            public string name = "";      // card id; for a file card this is always the file basename
            public string cls = "";       // comma list of Pilot.PilotType names ("" = any airframe class)
            public float  step = 0.02f;   // seconds between track samples (the fixed step at record time)
            public string airframe = "";  // jsonKey it was recorded on (informational)
            public float  startSpeed;     // m/s at record start — the condition the card intends
            public float  startAlt;       // m MSL at record start
            public Seg[]  segments;

            public float Duration
            {
                get { float d = 0f; if (segments != null) for (int i = 0; i < segments.Length; i++) d += segments[i].dur; return d; }
            }
        }

        private const string FolderName  = "wtmouseaim-cards";
        private const float  BuiltInStep = 0.02f;   // track spacing for generated built-in tracks
        private const int    MaxSamples  = 60000;   // ~20 min at 50 Hz — a forgotten recording can't eat RAM
        private const float  FloorAltM   = 500f;    // card aborts below this (MSL); ~4 s of margin in a 30 deg dive at 250 m/s
        private const float  RecoverElDeg = 10f;    // demand handed back on a floor abort: wings-level, slight climb

        private static readonly List<Card> _cards = new List<Card>();
        private static readonly Dictionary<string, ConfigEntry<bool>> _enable =
            new Dictionary<string, ConfigEntry<bool>>();
        private static ConfigFile _cf;             // kept so a freshly recorded card can bind live

        // --- playback state (null card == not running: the whole hot-path gate) ---
        private static List<Card> _queue;
        private static int        _qi;             // index into _queue
        private static Card       _card;
        private static int        _si;             // index into _card.segments
        private static float      _tSeg;           // seconds into the current segment
        private static bool       _frameSet;       // card frame captured (false => StartCard on next tick)
        private static Quaternion _frame;          // heading frame captured at CARD START (world-fixed)
        private static int        _acId;           // aircraft the card started on (respawn => abort)
        private static int        _lastLogSeg = -1;
        private static float      _derivedRate;    // per-airframe sweep rate for deriveAzRate segments

        // --- entry placement audit (see AuditEntry) ---
        private static Aircraft   _auditAc;
        private static int        _auditFrame = -1;
        private static float      _auditSpeed;     // what the placement commanded, to audit against
        private static bool       _placed;         // this card has had its placement applied

        // --- card-recording state ---
        private static bool       _recording;
        private static Quaternion _recFrame;
        private static float      _recStep;
        private static readonly List<float> _recAz = new List<float>();
        private static readonly List<float> _recEl = new List<float>();
        private static string     _recCls = "", _recAirframe = "";
        private static float      _recSpeed, _recAlt;

        public static bool Active => _card != null || _recording;

        // THE CARD OWNS THE AIRCRAFT while this is true. A variable the harness merely ASKS the pilot
        // not to touch is not controlled — one slipped mouse nudge silently rewrites the stimulus and
        // the run still scores, looking like a law difference. So every manual path into the aircraft
        // is overridden for the duration, not politely requested:
        //   - mouse -> marker : AimRig.Update drops aimCapture (the card writes the demand instead)
        //   - pitch/roll/yaw  : already owned — the seam prefix skips native in cockpit, and the
        //                       postfix overwrites in external views
        //   - throttle/brake  : PostTick below, from the seam postfix
        // NOT `Active`: recording a card is the one mode where the mouse MUST drive the marker.
        // Stopping a run is a deliberate act: the abort key or the run key, never an accidental twitch.
        public static bool Playing => _card != null;

        // ON-SCREEN NOTICE. Every reason a card declines to start used to go to LogOutput.log only,
        // so pressing the run key out of condition looked identical to pressing a dead key — the
        // v0.72 session hit exactly that. Anything that refuses, or silently changes the aircraft,
        // says so on screen. Drawn by the plugin's OnGUI before its HUD gates, so it shows on the
        // clean HUD too.
        private static string _notice = "";
        private static float  _noticeUntil;
        private const  float  NoticeSecs = 4f;   // long enough to read after a key press, short enough not to sit in a capture

        public static string Notice => Time.time < _noticeUntil ? _notice : "";

        private static void Notify(string msg)
        {
            _notice = msg;
            _noticeUntil = Time.time + NoticeSecs;
        }

        // One line for the plugin's OnGUI overlay: what is running, which segment, how long is left.
        public static string HudLine
        {
            get
            {
                if (_recording)
                    return $"REC CARD  {_recAz.Count * _recStep:0.0}s  ({_recAz.Count} samples)  [{_recCls}]";
                if (_card == null) return "";
                var segs = _card.segments;
                if (segs == null || _si >= segs.Length) return $"CARD {_card.name}";
                // The stop key is named here because a stick twitch no longer aborts: with the card
                // owning stick, throttle and marker, this line is the only thing telling the pilot how
                // to get the aircraft back.
                return $"CARD {_card.name} ({_qi + 1}/{_queue.Count})  seg {_si + 1}/{segs.Length} '{segs[_si].tag}'  "
                     + $"{Mathf.Max(0f, segs[_si].dur - _tSeg):0.0}s   [{Cfg.ScenarioAbortKey.Value}] to stop";
            }
        }

        // =========================================================================================
        // SELECTION (plan §5.2) — no custom UI. One ConfigEntry<bool> per card, so the F1
        // ConfigurationManager panel IS the enable/disable checklist. Built-ins default ON (they are
        // the designed set); file cards default OFF (a folder of ad-hoc captures must not silently
        // join a suite). Cfg.ScenarioCardSet overrides the whole thing for a scripted run.
        // =========================================================================================
        public static void BindCards(ConfigFile cf)
        {
            _cf = cf;
            _cards.Clear();
            _enable.Clear();
            foreach (var c in BuiltIns()) Register(c, true);

            int fromDisk = 0;
            try
            {
                string dir = CardDir();
                if (System.IO.Directory.Exists(dir))
                {
                    var files = System.IO.Directory.GetFiles(dir, "*.json");
                    System.Array.Sort(files, System.StringComparer.OrdinalIgnoreCase); // deterministic run order
                    foreach (var f in files)
                    {
                        var c = Load(f);
                        if (c != null && Register(c, false)) fromDisk++;
                    }
                }
                else System.IO.Directory.CreateDirectory(dir);
            }
            catch (System.Exception e)
            {
                WTMouseAimPlugin.Log.LogWarning($"[card] could not scan {FolderName}: {e.Message}");
            }
            WTMouseAimPlugin.Log.LogInfo(
                $"[card] {_cards.Count} card(s) bound ({_cards.Count - fromDisk} built-in, {fromDisk} from disk) — "
                + $"tick them in F1 > 'Scenario Cards'; [{Cfg.ScenarioRunKey.Value}] run, "
                + $"[{Cfg.ScenarioRecordKey.Value}] record, [{Cfg.ScenarioAbortKey.Value}] abort.");
        }

        private static bool Register(Card c, bool builtIn)
        {
            if (c == null || string.IsNullOrEmpty(c.name)) return false;
            if (_enable.ContainsKey(c.name))
            {
                WTMouseAimPlugin.Log.LogWarning($"[card] duplicate card name '{c.name}' — the later one is ignored.");
                return false;
            }
            _cards.Add(c);
            _enable[c.name] = _cf.Bind("Scenario Cards", c.name, builtIn, new ConfigDescription(
                $"Include this test card when the run hotkey ({Cfg.ScenarioRunKey.Value}) starts a suite. "
                + $"{(builtIn ? "Built-in" : "Recorded")} card: {c.segments?.Length ?? 0} segments, "
                + $"{c.Duration:0}s, airframe class '{(string.IsNullOrEmpty(c.cls) ? "any" : c.cls)}'. "
                + "Cards whose class doesn't match the aircraft you're flying are skipped automatically."));
            return true;
        }

        public static string CardDir() =>
            System.IO.Path.Combine(BepInEx.Paths.ConfigPath, FolderName);

        // Read one card file. EVERY failure path returns null with one log line — a malformed card is
        // skipped, never thrown (same contract as the FBW/canard/helo probes).
        private static Card Load(string path)
        {
            try
            {
                var c = JsonUtility.FromJson<Card>(System.IO.File.ReadAllText(path));
                if (c == null) { WTMouseAimPlugin.Log.LogWarning($"[card] {System.IO.Path.GetFileName(path)}: not a card object — skipped."); return null; }
                c.name = Sanitize(System.IO.Path.GetFileNameWithoutExtension(path)); // the FILE is the identity
                string bad = Validate(c);
                if (bad != null) { WTMouseAimPlugin.Log.LogWarning($"[card] {System.IO.Path.GetFileName(path)}: {bad} — skipped."); return null; }
                return c;
            }
            catch (System.Exception e)
            {
                WTMouseAimPlugin.Log.LogWarning($"[card] {System.IO.Path.GetFileName(path)}: {e.Message} — skipped.");
                return null;
            }
        }

        // The one shared sanity check, run on file cards AND on the built-ins at startup — so a broken
        // built-in shows up as a log line on every boot instead of as a mystery mid-flight.
        private static string Validate(Card c)
        {
            if (c.segments == null || c.segments.Length == 0) return "no segments";
            if (c.step <= 0f) c.step = BuiltInStep;
            for (int i = 0; i < c.segments.Length; i++)
            {
                var s = c.segments[i];
                if (s == null) return $"segment {i} is null";
                if (s.dur <= 0f) return $"segment {i} ('{s.tag}') has dur <= 0";
                if (string.IsNullOrEmpty(s.tag)) s.tag = "seg" + i;
                if (s.trackAz != null && s.trackAz.Length > 0 &&
                    (s.trackEl == null || s.trackEl.Length != s.trackAz.Length))
                    return $"segment {i} ('{s.tag}') trackAz/trackEl length mismatch";
            }
            if (c.segments[0].tag != "arm")
                return "first segment must be tagged 'arm' (a few seconds of steady demand, excluded from scoring)";
            return null;
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "card";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char ch in s)
                sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.' ? ch : '-');
            return sb.ToString();
        }

        // Airframe class straight from the game (research-A §1) — NOT the old takeoffDistance
        // heuristic. Fail-soft: an unreadable pilot list reads as Plane, i.e. the fixed-wing card.
        private static string ClassOf(Aircraft ac)
        {
            try
            {
                if (ac != null && ac.pilots != null && ac.pilots.Length > 0 && ac.pilots[0] != null)
                    return ac.pilots[0].pilotType.ToString();
            }
            catch { /* fall through */ }
            return "Plane";
        }

        private static bool ClassMatches(Card c, string cls)
        {
            if (string.IsNullOrEmpty(c.cls)) return true;
            foreach (var part in c.cls.Split(','))
                if (part.Trim().Equals(cls, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static Card ByName(string n)
        {
            foreach (var c in _cards)
                if (c.name.Equals(n, System.StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        // =========================================================================================
        // HOTKEY ENTRY POINTS (called from the plugin's Update — key edges are a per-frame thing;
        // everything that must be deterministic happens in Tick, on the fixed step).
        // =========================================================================================
        // The cards a run would fly RIGHT NOW: the ScenarioCardSet override if one is set, else every
        // ticked checkbox, minus anything whose airframe class doesn't match what you're in. Shared so
        // the entry-condition key places you where the run key would actually start you — two answers
        // to "which card" is how they drift apart.
        private static List<Card> SelectCards(Aircraft ac)
        {
            string cls = ClassOf(ac);
            var sel = new List<Card>();
            string ov = Cfg.ScenarioCardSet.Value;
            if (!string.IsNullOrEmpty(ov))
            {
                foreach (var raw in ov.Split(','))
                {
                    string n = raw.Trim();
                    if (n.Length == 0) continue;
                    var c = ByName(n);
                    if (c == null) WTMouseAimPlugin.Log.LogWarning($"[card] ScenarioCardSet names '{n}', which is not a known card.");
                    else sel.Add(c);
                }
            }
            else
            {
                foreach (var c in _cards)
                    if (_enable.TryGetValue(c.name, out var e) && e.Value) sel.Add(c);
            }

            for (int i = sel.Count - 1; i >= 0; i--)
                if (!ClassMatches(sel[i], cls))
                {
                    WTMouseAimPlugin.Log.LogInfo($"[card] skipping '{sel[i].name}' (class '{sel[i].cls}' != '{cls}').");
                    sel.RemoveAt(i);
                }

            // Replicates. Expanded AFTER the class filter so the count means "runs you will actually
            // fly", not "runs requested, some of which silently vanish".
            //
            // Repeated as a BLOCK (A,B,A,B) rather than per-card (A,A,B,B): fuel is re-pinned per
            // placement but everything else a long session drifts — air temperature at altitude,
            // wherever the aircraft has wandered to on the map — accumulates one way, and blocking
            // spreads that equally over every card instead of loading it all onto the last one.
            //
            // The same Card OBJECT is added N times on purpose. Playback state (_si, _tSeg, _frameSet,
            // _placed, _derivedRate) all lives on ScenarioPlayer and is reset by NextCard, so nothing
            // is carried in the Card itself and aliasing is safe.
            int rep = Mathf.Clamp(Cfg.ScenarioRepeat.Value, 1, 20);
            if (rep > 1 && sel.Count > 0)
            {
                var once = new List<Card>(sel);
                for (int r = 1; r < rep; r++) sel.AddRange(once);
                // Deliberately NOT logged here: SelectCards is also called by the standalone entry key,
                // which flies nothing, and "repeat x4 -> 4 runs" printed on a placement is exactly the
                // kind of miscount this option exists to remove. ToggleSuite's "suite start: N card(s)"
                // is the one authoritative count, and it is already correct.
            }
            return sel;
        }

        // Standalone entry-condition key. Puts the aircraft exactly where a run would start it WITHOUT
        // starting the run — so you can get on condition, look around and press the run key when ready,
        // and so the teleport can be exercised on its own when it misbehaves (it has, twice).
        public static void ForceEntryNow()
        {
            if (_card != null) { Notify("CARD RUNNING — abort it first"); return; }
            if (_recording)   { Notify("RECORDING — stop it first");     return; }
            if (!AimRig.TryGetContext(out var ac, out _) || ac == null || ac.disabled)
            {
                WTMouseAimPlugin.Log.LogWarning("[card] no local aircraft — nothing to place.");
                Notify("ENTRY: no aircraft");
                return;
            }
            Card c = null;
            foreach (var x in SelectCards(ac)) if (x.startSpeed > 0f) { c = x; break; }
            if (c == null)
            {
                WTMouseAimPlugin.Log.LogWarning(
                    "[card] no enabled card declares an entry condition — nothing to place the aircraft on.");
                Notify("ENTRY: no card declares one — see F1 > Scenario Cards");
                return;
            }
            PlaceOnCondition(ac, c);
        }

        public static void ToggleSuite()
        {
            if (_card != null) { Abort("run key pressed again"); return; }
            if (_recording)   { StopRecord("run key pressed"); }

            if (!AimRig.TryGetContext(out var ac, out _) || ac == null || ac.disabled)
            {
                WTMouseAimPlugin.Log.LogWarning("[card] no local aircraft — nothing to fly.");
                Notify("CARD: no aircraft");
                return;
            }

            var sel = SelectCards(ac);
            if (sel.Count == 0)
            {
                string cls = ClassOf(ac);
                WTMouseAimPlugin.Log.LogWarning(
                    $"[card] no enabled card matches airframe class '{cls}' — tick one in F1 > 'Scenario Cards' "
                    + "or set Scenario/ScenarioCardSet.");
                Notify($"CARD: none enabled for '{cls}' — see F1 > Scenario Cards");
                return;
            }
            // Gate on the FIRST card's declared entry condition. With ScenarioForceEntry on (default)
            // StartCard puts the aircraft on condition instead, so this is only the fallback for when
            // forcing is off or failed — refusing costs 5 seconds of setup, flying out-of-condition
            // costs a 3-minute run that scores against a different question.
            string entry = Cfg.ScenarioForceEntry.Value ? null : EntryConditionError(sel[0], ac);
            if (entry != null)
            {
                WTMouseAimPlugin.Log.LogWarning(
                    $"[card] '{sel[0].name}' not started — {entry}. Get on condition and press the run key again.");
                Notify($"CARD REFUSED: {entry}");
                return;
            }
            if (!Cfg.Enabled.Value || !Cfg.WriteControl.Value)
            {
                WTMouseAimPlugin.Log.LogWarning(
                    "[card] the instructor is not flying (Enabled/WriteControl off) — the card will move the "
                    + "marker but nothing will chase it. The capture will not be a law measurement.");
                Notify("CARD: instructor is OFF — capture will not measure the law");
            }

            float total = 0f; foreach (var c in sel) total += c.Duration;
            WTMouseAimPlugin.Log.LogInfo($"[card] suite start: {sel.Count} card(s), {total:0}s total, class '{ClassOf(ac)}'.");
            _queue = sel; _qi = 0; _card = sel[0]; _si = 0; _tSeg = 0f;
            _frameSet = false; _placed = false; _lastLogSeg = -1; _acId = ac.GetInstanceID();
        }

        public static void ToggleRecord()
        {
            if (_recording) { StopRecord("record key"); return; }
            if (_card != null) { Abort("record key pressed"); }
            if (!AimRig.TryGetContext(out var ac, out _) || ac == null || ac.disabled)
            { WTMouseAimPlugin.Log.LogWarning("[card] no local aircraft — cannot record a card."); return; }

            _recFrame = HeadingFrame(ac);
            _recStep  = Mathf.Max(1e-4f, Time.fixedDeltaTime);
            _recAz.Clear(); _recEl.Clear();
            _recCls      = ClassOf(ac);
            _recAirframe = "";
            _recSpeed = _recAlt = 0f;
            try
            {
                if (ac.definition != null) _recAirframe = ac.definition.jsonKey;
                if (ac.rb != null) _recSpeed = ac.rb.velocity.magnitude;
                _recAlt = ac.GlobalPosition().y;
            }
            catch { /* metadata is a bonus, not a dependency */ }
            _acId = ac.GetInstanceID();
            _recording = true;
            WTMouseAimPlugin.Log.LogInfo(
                $"[card] recording a card — fly the demand with the mouse, press [{Cfg.ScenarioRecordKey.Value}] again to save. "
                + $"({_recCls}, {_recAirframe}, {_recSpeed:0} m/s, {_recAlt:0} m, step {_recStep:0.###}s)");
        }

        // Called from the seam POSTFIX, after ChaseController.Apply. Throttle is the one flight input
        // the pilot still owns during a card: the game reads it in PlayerThrottleAxis1Controls, which
        // runs in Update and is NOT the method the seam patches — so neither the prefix's skip nor the
        // postfix's stick write touches it. Writing it here lands after native's Update-time write and
        // immediately before Aircraft.FilterInputs consumes it, so the card's value is what flies.
        //
        // Fixed position, not a speed controller: entry speed is already forced, the demand sequence is
        // fixed, so a fixed throttle makes the whole energy profile repeatable. A speed hold would be a
        // second control loop fighting the one under test.
        //
        // ponytail: customAxis1 (flaps / tilt / nozzles) is deliberately NOT written. Zeroing it would
        // retract a tiltrotor's nozzles mid-card. If a card ever needs to own it, it has to be per
        // archetype, not a blanket write.
        // Returns true when the card took the throttle — the Update-time seam uses that to decide
        // whether to skip native (see PilotThrottlePatch).
        public static bool OwnInputs(Aircraft ac)
        {
            if (_card == null || ac == null) return false;
            try
            {
                var ci = ac.GetInputs();
                if (ci == null) return false;
                ci.brake = 0f;                          // wheel brake; the AIRBRAKE rides on throttle (below)
                if (_card.startSpeed <= 0f) return false;   // ungated card (hover): the pilot keeps the collective
                ci.throttle = EntryThrottle();
                return true;
            }
            catch { return false; }                     // never let input ownership take the card down
        }

        // Throttle the card flies at, 0..1, straight from config.
        //
        // NOT the airframe's cruiseThrottle. That field (AircraftParameters.cruiseThrottle, default
        // 0.9) is the AI's cruise-hold setpoint, not a dry-thrust bound — on the Ifrit it lit the
        // afterburner, the aircraft then accelerated well past the speed its own turn demand was
        // derived at, and the first hard turn pulled it past the G limit. A baseline has to be a
        // speed the airframe can still MANEUVER at, not its fastest.
        //
        // ponytail: one constant shared by every airframe — allowed; the standing rule bans per-PLANE
        // constants, not shared ones. The principled version is the airframe's own afterburner
        // detent, which lives in JetNozzle's private Afterburner[] and is reachable only by
        // reflecting into a private nested class. Do that if 0.7 proves wrong on another airframe.
        //
        // v0.77 — THE FLOOR IS A MANOEUVRING THROTTLE, NOT AN EPSILON, AND AN OUT-OF-BAND VALUE HEALS.
        // The old floor was 0.001: one ulp clear of the game's exact-zero airbrake test (Airbrake.Update
        // reads the SAME ControlInputs the card writes — `openAmount += (throttle == 0f ? +open : -open)`)
        // and therefore "safe", but it is idle thrust. That mattered because a stored config value
        // outlives the code default that produced it: BepInEx only writes a key it has not already
        // written, so v0.73's `0 = use the airframe's cruiseThrottle` survived the v0.74 rewrite that
        // gave 0 a new meaning. R18 flew the whole card at 0.001 — 33% RPM (the engine's own minRPM
        // idle floor, Turbojet.FixedUpdate), speed decaying from 250 to 116 m/s, and two of four runs
        // dropping 3.5 km into the altitude-floor abort. Nothing in the capture said "throttle": it
        // read as a control-law energy failure.
        //
        // So anything below a manoeuvring throttle means UNSET, not "fly at idle", and is snapped back
        // to the default in the config itself — which fires SettingChanged, so the heal is logged as a
        // [config] line, lands in the recording header, and leaves F1 showing what is actually flying
        // instead of a value the card is quietly ignoring. Fires once; after that this is a compare.
        //
        // ponytail: this makes a deliberately low-power card unrepresentable. That is the trade — a
        // card that wants an idle-thrust regime should declare it as a card field, where it is visible
        // and scoped, not by leaving a global at a value that looks like a mistake and usually is one.
        private const float MinThrottle = 0.25f;
        private static float EntryThrottle()
        {
            var e = Cfg.ScenarioThrottle;
            if (e.Value < MinThrottle) e.Value = (float)e.DefaultValue;
            return Mathf.Min(e.Value, 1f);
        }

        // A teleport is a one-tick velocity STEP, and the game reads G by differencing velocity across
        // fixed steps. Pilot.Pilot_OnAeroInputsApplied:
        //     accel = (velocityPrev == Vector3.zero) ? Vector3.zero : rb.velocity - velocityPrev;
        //     accel /= Time.fixedDeltaTime * 9.81f;
        //     if (accel.magnitude > 20f) TakeGForceDamage(magnitude * magnitude);  // (sqrG-400)*0.007
        // so forcing 250 m/s onto an aircraft doing 80 reads as ~870 g and applies four figures of
        // damage: the airframe is destroyed on the spot. That is precisely why the v0.73 entry force
        // killed the aircraft when the run key was pressed slow or diving, and was harmless when the
        // pilot was already near the card's entry speed — the two explosions and the one good run.
        //
        // Zeroing velocityPrev takes the engine's OWN escape hatch: that zero check exists so a
        // freshly spawned aircraft doesn't report a spike on its first tick. The game's spawner sets
        // rb.velocity exactly the way we do (Aircraft start-up: rb.MovePosition/MoveRotation then
        // rb.velocity = startingVelocity) and gets away with it only because velocityPrev is still
        // default at Instantiate time. One tick of G is suppressed; the trackers re-arm from the new
        // state on the next one. Correct whichever side of our seam PilotAeroInputs runs on, since
        // the zero is read, not the difference.
        //
        // Called BEFORE the velocity write on purpose: if it throws, the teleport never happens and
        // the caller aborts, rather than leaving an aircraft mid-step with a live 800 g reading.
        private static void ResetGLoadTrackers(Aircraft ac)
        {
            ac.velocityPrev = Vector3.zero;             // physics-LOD tracker; the DAMAGE comes from Pilot's
            var pilots = ac.pilots;
            if (pilots == null) return;
            for (int i = 0; i < pilots.Length; i++)
                if (pilots[i] != null) pilots[i].velocityPrev = Vector3.zero;
        }

        // AN AIRCRAFT IS NOT ONE RIGIDBODY. Under complex physics — which is what anything the player
        // flies is in — every AeroPart is unparented to the world, given its OWN Rigidbody and joined
        // back to its parent part with a FixedJoint (Aircraft.SetComplexPhysics -> AeroPart.CreateRB /
        // CreateJoints). Writing Aircraft.rb therefore moves the fuselage root and leaves the wings,
        // tail and gear exactly where they were: every joint is stretched by the displacement, and
        // PhysX pays that back as a velocity impulse of roughly err/dt across the whole assembly.
        //
        // Measured, R15: a 14 m step added ~262 m/s and a 35 m step added ~665 m/s — 19x err in both,
        // i.e. linear in the displacement, which is the solver's signature and not a game-code bug.
        // The G path then read 133 g and 342 g and destroyed the airframe. (v0.74's velocityPrev
        // zeroing, below, is a DIFFERENT bug with an identical symptom: that one suppressed a phantom
        // reading of our own velocity write; this is real velocity the aircraft genuinely acquires.)
        //
        // THE FIX IS TO MOVE THE WHOLE ASSEMBLY, not just the root. Apply one rigid transform — the
        // same rotation about the same pivot, the same translation, the same velocity — to every body,
        // and no joint sees any relative change, so there is nothing for the solver to correct.
        //
        // What this deliberately does NOT do is merge the parts (Aircraft.SetSimplePhysics) and rebuild
        // them afterwards. That also works, and v0.75.0 shipped it, but it is a trap twice over:
        //   - MergeWithParent DESTROYS the part rigidbodies and joints, and Unity DEFERS destruction to
        //     end-of-frame. Called from FixedUpdate, Physics.Simulate() then runs with the joints still
        //     alive and now stretched — the same explosion, one layer down. It only appeared to work
        //     from the Update-clock hotkey, which is why the entry key survived and the run key did not.
        //   - Destroying and recreating components invalidates any reference the GAME cached to them.
        //     Unity's == overload reports a destroyed object as null WITHOUT throwing, so a stale
        //     cache does not announce itself; it just silently stops updating (suspected cause of the
        //     cockpit HUD going quiet after a placement).
        // A rigid transform destroys nothing, needs no frame staging, and works identically in simple
        // or complex physics: merged parts share the root's Rigidbody and are skipped by the identity
        // check below, so there is no physics-mode branch at all.
        private static void MoveAssembly(Aircraft ac, Rigidbody rb, Quaternion dRot, Vector3 pivot,
                                         Vector3 dPos, Quaternion rot1, Vector3 vel)
        {
            var parts = ac.partLookup;
            if (parts != null)
                for (int i = 0; i < parts.Count; i++)
                {
                    var pr = parts[i] != null ? parts[i].rb : null;
                    if (pr == null || pr == rb) continue;          // root, or merged onto it
                    pr.position        = pivot + dRot * (pr.position - pivot) + dPos;
                    pr.rotation        = dRot * pr.rotation;
                    pr.velocity        = vel;                      // rigid + zero spin => one velocity everywhere
                    pr.angularVelocity = Vector3.zero;
                }
            rb.position        = pivot + dPos;
            rb.rotation        = rot1;
            rb.velocity        = vel;
            rb.angularVelocity = Vector3.zero;
            Physics.SyncTransforms();                              // parts hang off transforms, not body poses
        }

        // AUDIT THE PLACEMENT, two frames on. The joint spike showed up one or two ticks after the
        // write, so by now any of it would be in the velocity — and the failure mode is the aircraft
        // simply ceasing to exist, which leaves nothing in the log to read afterwards. One line here
        // turns "it exploded" into a number. Flight time is the scarce resource on this project; a run
        // that fails without saying why costs another one.
        private static void AuditEntry()
        {
            if (_auditFrame < 0 || Time.frameCount < _auditFrame) return;
            var ac = _auditAc; _auditAc = null; _auditFrame = -1;
            if (ac == null || ac.disabled)
            {
                WTMouseAimPlugin.Log.LogWarning("[card] entry audit: the aircraft is gone — the placement killed it.");
                return;
            }
            float v = ac.rb != null ? ac.rb.velocity.magnitude : 0f;
            if (_auditSpeed > 0f && Mathf.Abs(v - _auditSpeed) > 0.2f * _auditSpeed)
                WTMouseAimPlugin.Log.LogWarning(
                    $"[card] entry audit: speed is {v:0} m/s after commanding {_auditSpeed:0} — the "
                    + "placement injected velocity. Expect damage; do not score this run.");
            else
                WTMouseAimPlugin.Log.LogInfo($"[card] entry audit: {v:0} m/s, clean (commanded {_auditSpeed:0}).");
        }

        public static void Abort(string reason)
        {
            if (_recording) { StopRecord(reason); return; }
            if (_card == null) return;
            WTMouseAimPlugin.Log.LogWarning($"[card] ABORT ({reason}) — '{_card.name}' segment "
                + $"{(_card.segments != null && _si < _card.segments.Length ? _card.segments[_si].tag : "?")} at {_tSeg:0.0}s.");
            Finish("abort: " + reason);
        }

        private static void Finish(string reason)
        {
            ManeuverRecorder.Stop(reason);
            ManeuverRecorder.SegmentTag = "";
            ManeuverRecorder.CardTag    = "";
            _card = null; _queue = null; _qi = _si = 0; _tSeg = 0f; _frameSet = false; _placed = false; _lastLogSeg = -1;
        }

        // =========================================================================================
        // THE TICK. Called from PilotPlayerStatePatch.Prefix, BEFORE ChaseController.BeginFrame and
        // therefore before the postfix's Apply() reads AimRig.AimForward — same fixed step, no lag.
        // =========================================================================================
        public static void Tick(Aircraft ac)
        {
            AuditEntry();                                      // owed from an earlier placement
            if (_card == null && !_recording) return;           // idle: nothing but the int compare above

            float dt = Time.fixedDeltaTime;
            if (ac == null || ac.disabled || ac.GetInstanceID() != _acId)
            { Abort("aircraft changed or gone"); return; }

            if (_recording) { TickRecord(); return; }

            // A stick twitch used to abort the run. It no longer does — an accidental nudge killing a
            // 3-minute capture is the same class of problem as one silently polluting it, and the card
            // already owns pitch/roll/yaw (prefix skips native in cockpit, postfix overwrites in
            // external views), so a twitch changes nothing about what was flown. Stopping is
            // deliberate: the abort key, the run key, the altitude floor, or losing the aircraft.

            // SAFETY: a demand sequence has no survival instinct. Nothing in a card pulls out of a
            // dive, so a run that loses the energy to hold its demand descends until it hits the sea
            // — which in an unattended sweep is a crashed run, a garbage capture and a lost slot.
            // The floor turns that into a CLEAN TRUNCATION the scorer can see was aborted.
            // Handing back a level demand is half the guard, not a nicety: AimRig keeps whatever the
            // card last wrote, and the instructor keeps chasing it after the card ends, so a bare
            // Abort() here would stop the card and fly into the water anyway.
            // ponytail: MSL, correct on the naval test range; use a terrain query if a card ever
            // flies over land.
            // `_frameSet` gates it: the floor guards a RUNNING card, and the placement below is what
            // lifts the aircraft to the card's entry altitude. Checking it first would refuse every run
            // started from the runway — which is exactly where you press the key.
            if (_frameSet && ac.GlobalPosition().y < FloorAltM)
            {
                AimRig.SetAimForward(HeadingFrame(ac) * (Quaternion.Euler(-RecoverElDeg, 0f, 0f) * Vector3.forward));
                Abort($"altitude floor ({FloorAltM:0} m MSL)");
                return;
            }

            if (!_frameSet)
            {
                // Place first, start second — and place ONCE PER CARD, so every card in a suite gets its
                // own entry condition rather than inheriting the state the previous one left behind.
                // Returning after the placement gives it a tick to settle before the card is timed.
                if (Cfg.ScenarioForceEntry.Value && _card.startSpeed > 0f && !_placed)
                {
                    _placed = true;
                    if (!PlaceOnCondition(ac, _card)) { Finish("entry condition could not be set"); }
                    return;
                }
                StartCard(ac);
            }

            _tSeg += dt;
            var segs = _card.segments;
            while (_si < segs.Length && _tSeg >= segs[_si].dur) { _tSeg -= segs[_si].dur; _si++; }
            if (_si >= segs.Length) { NextCard(); return; }

            var s = segs[_si];
            if (_si != _lastLogSeg)
            {
                _lastLogSeg = _si;
                WTMouseAimPlugin.Log.LogInfo($"[card] {_card.name} seg {_si + 1}/{segs.Length} '{s.tag}' ({s.dur:0.#}s)");
            }
            ManeuverRecorder.SegmentTag = s.tag;   // rows self-label; the recorder derives tSeg from the change
            AimRig.SetAimForward(Demand(s, _tSeg));
        }

        // Sustained-turn stimulus, derived from the airframe instead of hardcoded. The INSTANTANEOUS
        // (lift/structure-limited) turn rate at speed V is w = g*sqrt(n^2-1)/V for load factor n. No
        // jet can SUSTAIN that — holding max-lift needs thrust = drag there — so the card asks for a
        // fraction of it.
        //
        // This is why v1 killed every Ifrit run: its fixed 20 deg/s was, at 250 m/s and n=9, exactly
        // 20.1 deg/s — 100% of the structural ceiling, with zero margin. The aircraft did the only
        // thing left and banked to 85 deg pulling 9 g, which is a descending spiral by definition.
        //
        // Derived ONCE at card start from the entry speed (not live V) so the stimulus stays a fixed,
        // reproducible input for a given airframe + entry condition — a rate that chased live speed
        // would be a feedback loop, and two builds could then be fed different demands.
        // ponytail: 0.6 is the usual sustained/instantaneous ratio, not a solved thrust-drag point.
        // The sidecar already dumps thrust and the Cd curve, so M3 can compute the real one.
        private const float SustainedFrac = 0.6f;
        private const float RateMinDegS = 3f, RateMaxDegS = 30f;

        private static float SustainableTurnRate(Aircraft ac)
        {
            float n = 7f, v = 250f;                        // fail-soft: a mid jet, if nothing reads
            try
            {
                var p = ac.GetAircraftParameters();
                if (p != null && p.aircraftGLimit > 1f) n = p.aircraftGLimit;
                if (ac.rb != null && ac.rb.velocity.magnitude > 20f) v = ac.rb.velocity.magnitude;
            }
            catch { /* probe convention: never throw, fly the default */ }
            float w = Mathf.Rad2Deg * (9.81f * Mathf.Sqrt(Mathf.Max(0f, n * n - 1f)) / v);
            return Mathf.Clamp(w * SustainedFrac, RateMinDegS, RateMaxDegS);
        }

        // Entry-condition gate. A card's score is only comparable to another run of the same card if
        // both started from the same state, so a card that DECLARES startSpeed/startAlt refuses to fly
        // outside them. Until v2 these two fields were written by the recorder and read by nothing —
        // which meant "I hand-flew to roughly 250" was an uncontrolled input feeding every score.
        // Cards that declare nothing (startSpeed 0) are ungated, so ad-hoc recordings still just work.
        private const float SpeedTolFrac = 0.15f, AltTolM = 800f;

        // Put the aircraft ON the card's declared entry condition rather than asking the pilot to fly
        // there. Hand-flying to "roughly 250 m/s at roughly 4000 m" is not repeatable to the 1-3% the
        // metrics now resolve, and the R13 session showed the residue: with everything else held,
        // turn360's deltaEnergyHeightM still spread 35% on throttle-setting alone.
        //
        // Heading is PRESERVED (only pitch/roll are zeroed) so the card's world-fixed frame still
        // points where the pilot set up, and the card's opening `arm` segment — excluded from scoring
        // — absorbs the transient. Runs on the fixed step, from StartCard, before any demand is written.
        //
        // MASS is pinned here too, via fuel. Fuel burn is a ONE-WAY drift across a session, which is
        // the dangerous kind: the R13 Ifrit runs lost 1255 kg (5.1% of gross) monotonically over four
        // back-to-back runs — larger than the 1-3% spread the metrics resolve, so an uncontrolled
        // tank turns a mass trend into what reads as a law difference.
        //
        // Aircraft.fuelLevel is NOT the current gauge — it is the TARGET ratio that Refuel() writes
        // into the tanks (FuelTank.Refuel does `fuelMass = fuelCapacity * ratio` with a signed
        // part.ModifyMass, so it sets absolutely and drains down as happily as it fills up).
        // Refuel(null) — a null refueler skips the "Refueled by <name>" HUD banner.
        // Credit: the sibling InfiniteAmmo mod documents the fuelLevel-vs-GetFuelLevel trap.
        //
        // ponytail: stores are NOT touched. A card fires nothing, so loadout mass is already constant
        // within a session; revisit if a card ever shoots.
        //
        private static bool PlaceOnCondition(Aircraft ac, Card c)
        {
            try
            {
                var rb = ac.rb;
                if (rb == null) return false;

                // Keep the heading, level the attitude. A flattened forward vanishes only if the nose
                // is exactly vertical; fall back to the current transform forward rather than snapping
                // to an arbitrary world axis.
                Vector3 fwd = ac.transform.forward; fwd.y = 0f;
                fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : ac.transform.forward;

                float alt0 = ac.GlobalPosition().y, v0 = rb.velocity.magnitude;
                float fuel0 = -1f, fuelTgt = Cfg.ScenarioEntryFuel.Value;
                if (fuelTgt > 0f)
                {
                    fuel0 = ac.GetFuelLevel();          // the ACTUAL ratio; ac.fuelLevel is the target
                    ac.fuelLevel = fuelTgt;
                    ac.Refuel(null);                    // null refueler => no "Refueled by" HUD banner
                }

                ResetGLoadTrackers(ac);                 // MUST precede the velocity write — see above
                _auditAc = ac; _auditSpeed = c.startSpeed; _auditFrame = Time.frameCount + 2;

                // Shift by the altitude ERROR. A delta is the same in the global and local frames as
                // long as they differ only by a translation, which avoids having to know whether the
                // floating origin shifts y at all.
                Vector3 dPos = Vector3.zero;
                if (c.startAlt > 0f)
                {
                    float err = c.startAlt - alt0;
                    if (Mathf.Abs(err) > 1f) dPos = Vector3.up * err;
                }
                Quaternion rot1 = Quaternion.LookRotation(fwd, Vector3.up);
                MoveAssembly(ac, rb, rot1 * Quaternion.Inverse(rb.rotation), rb.position, dPos,
                             rot1, fwd * c.startSpeed);

                string fuelMsg = fuel0 >= 0f ? $", fuel {fuel0:0.00} -> {fuelTgt:0.00}" : "";
                WTMouseAimPlugin.Log.LogInfo(
                    $"[card] entry condition set: {v0:0} -> {c.startSpeed:0} m/s, {alt0:0} -> {c.startAlt:0} m"
                    + $"{fuelMsg}, wings level (heading unchanged).");
                Notify($"ON CONDITION  {c.startSpeed:0} m/s  {c.startAlt:0} m"
                    + (fuel0 >= 0f ? $"  fuel {fuelTgt:P0}" : ""));
                return true;
            }
            catch (System.Exception e)
            {
                // A half-applied entry condition is worse than no run: forcing BYPASSES the pre-flight
                // gate, so falling through here would fly the card from whatever state the pilot was
                // in and score it as if it were on condition. Refuse instead.
                WTMouseAimPlugin.Log.LogWarning($"[card] could not set entry condition ({e.GetType().Name}: {e.Message}) — refusing the run.");
                Notify("CARD REFUSED: could not set entry condition — see log");
                return false;
            }
        }

        private static string EntryConditionError(Card c, Aircraft ac)
        {
            if (c.startSpeed <= 0f) return null;
            try
            {
                float v = ac.rb != null ? ac.rb.velocity.magnitude : 0f;
                float alt = ac.GlobalPosition().y;
                float dv = c.startSpeed * SpeedTolFrac;
                if (Mathf.Abs(v - c.startSpeed) > dv)
                    return $"airspeed {v:0} m/s, card wants {c.startSpeed:0} +/- {dv:0}";
                if (c.startAlt > 0f && Mathf.Abs(alt - c.startAlt) > AltTolM)
                    return $"altitude {alt:0} m, card wants {c.startAlt:0} +/- {AltTolM:0}";
            }
            catch { return null; }                          // unreadable state gates nothing
            return null;
        }

        private static void StartCard(Aircraft ac)
        {
            // ORDER IS LOAD-BEARING: put the aircraft on condition FIRST, because SustainableTurnRate
            // reads live airspeed. Deriving the sweep rate before the force would key the card's
            // headline stimulus to whatever speed the pilot happened to be at — the exact
            // uncontrolled input the entry condition exists to remove.
            // The placement already ran, a tick ago (see the _placed gate in Tick) — so live airspeed is
            // the card's entry speed and SustainableTurnRate derives the sweep rate from the right one.
            _frame = HeadingFrame(ac);
            _derivedRate = SustainableTurnRate(ac);
            _frameSet = true;
            // Bracket the card with the recorder. Start() is private; Toggle() is the safe door (it
            // can't double-open a writer). The card name goes into the CSV filename so two builds'
            // runs of the same card sort together and diff.
            if (ManeuverRecorder.IsRecording) ManeuverRecorder.Stop("card boundary");
            ManeuverRecorder.CardTag    = _card.name;
            ManeuverRecorder.SegmentTag = "";
            ManeuverRecorder.Toggle();
            WTMouseAimPlugin.Log.LogInfo(
                $"[card] '{_card.name}' start ({_card.segments.Length} segments, {_card.Duration:0}s) — "
                + $"heading frame locked, demand is world-fixed from here. "
                + $"Derived sweep rate {_derivedRate:0.0} deg/s, throttle {EntryThrottle():0.00}.");
        }

        private static void NextCard()
        {
            ManeuverRecorder.Stop($"card '{_card.name}' complete");
            ManeuverRecorder.SegmentTag = "";
            _qi++;
            if (_queue == null || _qi >= _queue.Count)
            {
                WTMouseAimPlugin.Log.LogInfo("[card] suite complete.");
                Finish("suite complete");
                return;
            }
            // No separate settle gap: the next card opens with its own `arm` segment, which IS the
            // settle (steady demand on the heading the previous card left the aircraft on).
            _card = _queue[_qi]; _si = 0; _tSeg = 0f; _frameSet = false; _placed = false; _lastLogSeg = -1;
        }

        // World-fixed heading frame: the aircraft's heading projected onto the horizontal plane, so a
        // card's az/el mean the same thing whatever bank/pitch it happened to start in.
        private static Quaternion HeadingFrame(Aircraft ac)
        {
            Vector3 f = ac.transform.forward;
            Vector3 flat = new Vector3(f.x, 0f, f.z);
            // Pointing exactly straight up/down has no heading, so fall back to world north rather than
            // asking LookRotation for a degenerate frame — a card started in a vertical is a rare edge,
            // and world north is at least well-defined and reproducible.
            return flat.sqrMagnitude > 1e-4f
                ? Quaternion.LookRotation(flat.normalized, Vector3.up)
                : Quaternion.identity;
        }

        // (az, el) in the card frame -> a world direction. az + = right of the captured heading,
        // el + = above the horizon (Unity's +X euler pitches DOWN, hence -el).
        private static Vector3 Demand(Seg s, float t)
        {
            float az = s.az, el = s.el;
            if (s.trackAz != null && s.trackAz.Length > 0)
            {
                // Indexed by TIME, not by tick count, so a card recorded at one fixed step replays
                // correctly at another. Nearest sample: the step is ~20 ms, well under anything the
                // airframe can respond to.
                float step = _card.step > 1e-4f ? _card.step : BuiltInStep;
                int i = Mathf.Clamp(Mathf.RoundToInt(t / step), 0, s.trackAz.Length - 1);
                az = s.trackAz[i];
                el = s.trackEl != null && i < s.trackEl.Length ? s.trackEl[i] : 0f;
            }
            else if (s.deriveAzRate) az += _derivedRate * t;
            return _frame * (Quaternion.Euler(-el, az, 0f) * Vector3.forward);
        }

        // =========================================================================================
        // CARD RECORDING (plan §5.2). Samples the aim DEMAND — not the stick — on the fixed-step
        // clock, stored in the card-start heading frame. That is why replay works at all: recorder
        // CSVs can never be played back as input (they log outputs), but the demand track can.
        // =========================================================================================
        private static void TickRecord()
        {
            Vector3 local = Quaternion.Inverse(_recFrame) * AimRig.AimForward;
            if (local.sqrMagnitude < 1e-6f) return;
            local.Normalize();
            _recAz.Add(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg);
            _recEl.Add(Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg);
            if (_recAz.Count >= MaxSamples) StopRecord("sample cap reached");
        }

        private static void StopRecord(string reason)
        {
            _recording = false;
            int n = _recAz.Count;
            if (n < 10)
            {
                WTMouseAimPlugin.Log.LogWarning($"[card] recording discarded ({reason}) — only {n} samples.");
                _recAz.Clear(); _recEl.Clear();
                return;
            }

            var track = new Seg
            {
                tag = "rec",
                dur = n * _recStep,
                trackAz = _recAz.ToArray(),
                trackEl = _recEl.ToArray(),
            };
            // Every card opens with an `arm` segment (steady demand, excluded from scoring) — here it
            // holds the first sampled direction, so replay settles onto the recording's start state
            // before the recorded motion begins.
            var arm = new Seg { tag = "arm", dur = 4f, az = _recAz[0], el = _recEl[0] };
            var card = new Card
            {
                name       = Sanitize($"rec-{System.DateTime.Now:yyyyMMdd-HHmmss}"),
                cls        = _recCls,
                step       = _recStep,
                airframe   = _recAirframe,
                startSpeed = _recSpeed,
                startAlt   = _recAlt,
                segments   = new[] { arm, track },
            };
            _recAz.Clear(); _recEl.Clear();

            string path = null;
            try
            {
                string dir = CardDir();
                System.IO.Directory.CreateDirectory(dir);
                path = System.IO.Path.Combine(dir, card.name + ".json");
                System.IO.File.WriteAllText(path, JsonUtility.ToJson(card, true));
                WTMouseAimPlugin.Log.LogInfo(
                    $"[card] saved ({reason}) {n} samples / {track.dur:0.0}s -> {path}. Rename the FILE to rename the card.");
                if (_cf != null && Register(card, false))                    // runnable now, no restart
                    WTMouseAimPlugin.Log.LogInfo($"[card] '{card.name}' bound — tick it in F1 > 'Scenario Cards'.");
            }
            catch (System.Exception e)
            {
                WTMouseAimPlugin.Log.LogWarning($"[card] could not save {path ?? "<no path>"}: {e.Message}");
            }
        }

        // =========================================================================================
        // BUILT-IN LIBRARY — test card v1, Appendix A of the plan.
        //
        // Segment azimuths are ABSOLUTE in the card frame; the STEP SIZE a segment tests is its
        // difference from the previous segment's demand, and the tag names it (az90 = a 90-degree
        // step). Signs alternate so a full card doesn't wander off in one direction.
        // =========================================================================================
        private static IEnumerable<Card> BuiltIns()
        {
            // v2, not v1: the segment order and the turn rate both changed, so a v1 capture is NOT
            // comparable to a v2 one. Renaming makes that impossible to do by accident — the card id
            // is in the CSV filename and the `# card` header, so provenance carries into the scorer.
            // Entry condition is declared here and enforced by EntryConditionError at start.
            var fw = new Card { name = "fixedwing-v2", cls = "Plane", step = BuiltInStep,
                                startSpeed = 250f, startAlt = 4000f,
                                segments = FixedWingSegs(0f).ToArray() };

            // Rotorcraft fly the SAME card and append their own segments (Appendix A). No entry gate:
            // a hover card has no meaningful entry airspeed, and startSpeed 0 means ungated.
            var rs = FixedWingSegs(0f);
            var sink = rs[rs.Count - 1];                    // turn360 — the energy sink stays LAST
            rs.RemoveAt(rs.Count - 1);
            float az = rs[rs.Count - 1].az;
            rs.Add(Hold("hover",     30f, az,       0f));   // hold: position RMS / drift from the pos columns
            rs.Add(Hold("hoveryaw",  15f, az + 90f, 0f));   // pedal turn — nose authority at zero forward speed
            rs.Add(Hold("bobup",     15f, az + 90f, 25f));  // vertical demand at hover
            sink.az = az + 90f;                             // no gratuitous step into the sweep
            rs.Add(sink);
            // ponytail: Appendix A's `translate` and `transition` segments are NOT here — a 50 m lateral
            // translate and a hover<->wing transition cannot be commanded through an aim direction
            // (they need position demand and customAxis1). They belong to M2's TestPilotState, which
            // writes ControlInputs directly. A segment that can't produce its stimulus is worse than
            // a missing one: it scores as perfect tracking.
            var rc = new Card { name = "rotorcraft-v2", cls = "Helo,Tiltwing,VTOL", step = BuiltInStep, segments = rs.ToArray() };

            // SWEEP-ONLY card: 36 s instead of ~3 min, for when the question is ONLY about the
            // sustained turn (the v0.78 marker-rate feed-forward is the first such question). Four
            // replicates cost 2.5 min of wall clock instead of 12, which is the difference between
            // an A/B you run twice and one you avoid running.
            //
            // NOT comparable to fixedwing-v2's turn360, and deliberately so. There the sweep runs
            // LAST and enters at ~235 m/s off a spent energy state; here it enters at the gated 250,
            // needing n=5.46 rather than 5.24 — a ~4% harder demand. So this card carries its own
            // baseline: run both A/B arms ON IT, never against an R19 number.
            //
            // ponytail: the arm segment is 6 s, not v2's 4 s. There the sweep is preceded by 2.5 min
            // of settled flight; here it is preceded by a teleport, and the extra 2 s is free
            // (excluded from scoring) insurance against measuring the placement transient.
            var sweep = new Card { name = "fixedwing-sweep", cls = "Plane", step = BuiltInStep,
                                   startSpeed = 250f, startAlt = 4000f,
                                   segments = new[] {
                                       Hold("arm", 6f, 0f, 0f),
                                       new Seg { tag = "turn360", dur = 30f, az = 0f, el = 0f, deriveAzRate = true },
                                   } };

            foreach (var c in new[] { fw, rc, sweep })
            {
                // The built-ins go through the SAME validator as a file card, so a bad edit here shows
                // up as one log line on every boot rather than as a mystery mid-flight.
                string bad = Validate(c);
                if (bad != null) WTMouseAimPlugin.Log.LogWarning($"[card] BUILT-IN '{c.name}' is invalid: {bad}");
                else yield return c;
            }
        }

        // SEGMENT ORDER IS LOAD-BEARING, not cosmetic. v1 ran the sustained turn ninth of nineteen, so
        // every segment after it — reversal, astern and all ten micro-steps — was flown from a wrecked
        // energy state at the wrong speed and altitude. The micro-steps are the entire reason this card
        // exists (small corrections at high q is the mod's weakest regime), and v1 measured them in a
        // spiral. v2 spends the energy budget in order of increasing cost: the precise, cheap
        // measurements run first at the gated entry condition, and the sustained turn — the one segment
        // that deliberately eats the aircraft's energy — runs LAST, where it can contaminate nothing.
        private static List<Seg> FixedWingSegs(float az0)
        {
            var s = new List<Seg> { Hold("arm", 4f, az0, 0f) };   // settle; excluded from scoring

            // 1. HIGH-q PRECISION FIRST, still at the entry speed the card gated on.
            float cur = az0;
            float[] micro = { 0.2f, -0.4f, 0.6f, -0.8f, 1.0f, -0.2f, 0.4f, -0.6f, 0.8f, -1.0f };
            for (int i = 0; i < micro.Length; i++)                // 0.2..1 deg steps; deltas sum to 0
            { cur += micro[i]; s.Add(Hold("micro" + (i + 1), 2f, cur, 0f)); }
            s.Add(Walk("fine", 20f, cur, 0f, 0.3f, 1337));        // bounded <= 0.3 deg, seeded

            // 2. STEP RESPONSE, small to large. Each az is a step from the PREVIOUS demand.
            s.Add(Hold("az10",  15f, cur + 10f, 0f));             // 10
            s.Add(Hold("az30",  15f, cur - 20f, 0f));             // 30
            s.Add(Hold("az90",  15f, cur + 70f, 0f));             // 90
            s.Add(Hold("az150", 15f, cur - 80f, 0f));             // 150
            cur -= 80f;

            // 3. ELEVATION. elDn holds 10 s at -20, not v1's 15 s at -30: the STEP is what gets scored,
            //    and v1's hold alone dumped ~1900 m at 250 m/s before the turn segment even began.
            s.Add(Hold("elUp", 15f, cur,  30f));
            s.Add(Hold("elDn", 10f, cur, -20f));

            // 4. WRAP CASES — cheap in energy, and dead-astern is a known sign-convention trap.
            s.Add(Hold("reversal", 15f, cur + 180f, 5f));         // el offset breaks the 180 tie
            s.Add(Hold("astern",   15f, cur + 360f, 0f));         // exactly dead astern

            // 5. THE ENERGY SINK, LAST. Rate comes from SustainableTurnRate(ac) at card start.
            s.Add(new Seg { tag = "turn360", dur = 30f, az = cur + 360f, el = 0f, deriveAzRate = true });
            return s;
        }

        private static Seg Hold(string tag, float dur, float az, float el) =>
            new Seg { tag = tag, dur = dur, az = az, el = el };

        // Deterministic bounded random walk — SEEDED on purpose: experiment (i) (the score noise
        // floor) compares two runs of the same card, which is meaningless if the stimulus differs.
        private static Seg Walk(string tag, float dur, float az0, float el0, float amp, int seed)
        {
            int n = Mathf.RoundToInt(dur / BuiltInStep) + 1;
            var ta = new float[n]; var te = new float[n];
            var rnd = new System.Random(seed);
            float a = 0f, e = 0f, k = amp * 0.15f;
            for (int i = 0; i < n; i++)
            {
                a = Mathf.Clamp(a + (float)(rnd.NextDouble() - 0.5) * k, -amp, amp);
                e = Mathf.Clamp(e + (float)(rnd.NextDouble() - 0.5) * k, -amp, amp);
                ta[i] = az0 + a; te[i] = el0 + e;
            }
            return new Seg { tag = tag, dur = dur, az = az0, el = el0, trackAz = ta, trackEl = te };
        }

    }
}

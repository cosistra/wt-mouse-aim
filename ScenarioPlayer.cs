using System;
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
    //
    // ONE INSTANCE PER AIRCRAFT (v0.86), same registry as ChaseController (v0.82) and ManeuverRecorder.
    // The reason is the whole point of the uncrewed harness: N drones flying N cards side by side cost
    // one card length of wall clock instead of N, and one shared _si/_tSeg/_frame would make that N
    // aircraft all flying whichever card was started last, from whichever heading frame was captured
    // last. Split by the same test the recorder uses — DOES THIS VALUE REACH A CSV ROW OR A PER-FLIGHT
    // DECISION? — which lands three groups on the static side, each saying why in place:
    //   * the CARD LIBRARY (_cards/_enable/_cf): shared, read-only config. Loaded once, never mutated
    //     by playback; N players reading it is exactly right.
    //   * the ON-SCREEN NOTICE: one screen per process, the same judgment as AnomalyLog's one stream.
    // (The A/B ARM SCHEDULE was the third until v0.94, when the swept lever moved off Cfg and onto the
    // controller — see ApplyArm. It is per-instance now, and N aircraft fly N concurrent A/Bs.)
    internal sealed class ScenarioPlayer
    {
        // -----------------------------------------------------------------------------------------
        // CARD MODEL — PUBLIC FIELDS ONLY, read and written with **Newtonsoft**, which ships in the
        // game's Managed folder. Behaviour is the fail-soft contract the probes already use: unknown
        // keys are ignored (that is what `note` relies on), missing keys keep the C# default, and a
        // malformed file throws where Load() catches it.
        //
        // NOT UnityEngine.JsonUtility, and this is not a preference. JsonUtility silently DROPS the
        // `Seg[] segments` field in BOTH directions — `ToJson` wrote every recorded card with no
        // `segments` key at all (see any pre-v0.90.1 `rec-*.json`) and `FromJson` read every disk card
        // back with `segments == null`, which Validate then rejected as "no segments — skipped". So
        // from v0.71 to v0.90 NO card file on disk ever loaded, and nothing caught it: the built-in
        // cards are constructed in C# and never touch a serializer, so every gate and every batch we
        // flew went through the one path that could not fail. The load line said "0 from disk" the
        // whole time. `debugtests/test-card-model.py` is the check that would have.
        //
        // `cls` (not `class`) because the latter is a C# keyword; the JSON key must match the field.
        // --- CARD-MODEL BEGIN ---
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

        // One config knob a card pins for its own duration. `key` uses the SAME "Section/Key" grammar
        // as Cfg.ScenarioArmToggle (bare key => section "Control", where every control-law lever
        // lives), and `value` is the TOML text form of whatever type that entry actually is — parsed
        // by BepInEx's own TomlTypeConverter, so bool/int/float/string/KeyCode all work with no
        // per-type code here. A string pair rather than a typed union because the card model is a
        // fixed shape of public fields — one `value` string keeps every knob type on one code path.
        [System.Serializable]
        internal class CfgOverride
        {
            public string key   = "";
            public string value = "";
        }

        [System.Serializable]
        internal class Card
        {
            public string name = "";      // card id; for a file card this is always the file basename
            public string cls = "";       // comma list of Pilot.PilotType names ("" = any airframe class)
            public float  step = 0.02f;   // seconds between track samples (the fixed step at record time)
            public string airframe = "";  // jsonKey(s) the drone harness SPAWNS — comma list = one per lane (v0.91)
            public float  startSpeed;     // m/s at record start — the condition the card intends
            public float  startAlt;       // m MSL at record start

            // ENTRY SPEED AS A MULTIPLE OF THE LANE AIRFRAME'S OWN CORNER SPEED (v0.93). 0 = unset,
            // i.e. `startSpeed` stands and the card behaves exactly as it did before this field.
            // When > 0 it WINS over `startSpeed`, which stays as the absolute form and as the
            // fail-soft fallback for an airframe whose envelope cannot be read.
            //
            // Why: one absolute number for every lane is what makes the shipped 250 m/s grid
            // unflyable by CAS1 (Vmax 205.6) and COIN (141.7) — v0.92's pre-spawn gate refuses those
            // lanes, correctly, so a 10-airframe card flies 8. A multiple of corner speed is flyable
            // by the whole roster AND enters every airframe at the equivalent AERODYNAMIC state (its
            // own best turn-rate point) instead of at an equivalent number, which is the stronger
            // reason of the two. See ResolveStartSpeed for the resolution order.
            public float  startSpeedCorner;

            public Seg[]  segments;

            // --- SELF-DESCRIPTION (v0.90). A card is the whole test, not just the stimulus.
            //
            // Everything below used to be an operator-set global that had to be matched to the card by
            // hand — DroneAirframe / DroneSpawnAlt / DroneSpawnSpeed / ScenarioRepeat /
            // ScenarioArmToggle — and a mismatch does not refuse, it produces a capture that scores
            // fine and answers a different question. (R18's "energy failure" was one such: a global
            // left at a value the card was quietly ignoring.) Making them card fields means the
            // operator ticks ONE checkbox and presses the spawn key.
            //
            // Every one of them falls back to the corresponding Cfg knob, so a card that declares
            // nothing behaves exactly as it did before this field existed — which is what keeps the
            // shipped grid and every ad-hoc recording valid.
            public int    repeat;         // replicate count; 0 = fall back to Cfg.ScenarioRepeat
            public string armToggle = ""; // A/B knob to interleave; "" = fall back to Cfg.ScenarioArmToggle
            public CfgOverride[] config;  // knobs pinned for this card's duration; null/empty = none

            // HOW MANY DRONES (v0.91). 0 = "as many as `airframe` names", and only if that names
            // nothing does Cfg.DroneCount stand. That middle rule is not a convenience: a card whose
            // airframe list is the fleet it wants tested is INCOMPLETE without it — name 12 airframes,
            // leave the global at 4, and the batch flies the first 4 by lane and silently answers a
            // different question. Set it explicitly only to fly a MULTIPLE of the list (count 8 over a
            // 4-key list = two drones per airframe, since lanes wrap).
            public int    count;

            // Derived, never stored. Without the attribute Newtonsoft writes it into every recorded
            // card (it serializes get-only properties), and a hand-editable artifact that carries a
            // number nothing reads back is an invitation to change it and expect an effect.
            [Newtonsoft.Json.JsonIgnore]
            public float Duration
            {
                get { float d = 0f; if (segments != null) for (int i = 0; i < segments.Length; i++) d += segments[i].dur; return d; }
            }
        }
        // --- CARD-MODEL END ---

        private const string FolderName  = "wtmouseaim-cards";
        private const float  BuiltInStep = 0.02f;   // track spacing for generated built-in tracks
        private const int    MaxSamples  = 60000;   // ~20 min at 50 Hz — a forgotten recording can't eat RAM
        private const float  FloorAltM   = 500f;    // card aborts below this (MSL); ~4 s of margin in a 30 deg dive at 250 m/s
        private const float  RecoverElDeg = 10f;    // demand handed back on a floor abort: wings-level, slight climb

        private static readonly List<Card> _cards = new List<Card>();
        private static readonly Dictionary<string, ConfigEntry<bool>> _enable =
            new Dictionary<string, ConfigEntry<bool>>();
        private static ConfigFile _cf;             // kept so a freshly recorded card can bind live

        // --- playback state, PER AIRCRAFT (null card == not running: the whole hot-path gate) ---
        private List<Card> _queue;
        private int        _qi;             // index into _queue
        private Card       _card;
        private int        _si;             // index into _card.segments
        private float      _tSeg;           // seconds into the current segment
        private bool       _frameSet;       // card frame captured (false => StartCard on next tick)
        private Quaternion _frame;          // heading frame captured at CARD START (world-fixed)
        private int        _acId;           // aircraft the card started on (respawn => abort)
        private int        _lastLogSeg = -1;
        private float      _derivedRate;    // per-airframe sweep rate for deriveAzRate segments
        // Replicates this run lost to an abort. Reported once at suite end so a lane that aborted
        // every replicate is distinguishable IN THE LOG from a lane that never ran — the per-capture
        // half is already there (each aborted replicate now writes its own CSV with its own
        // `# stop … reason=abort:` line), and before v0.99.1 there was no such half, because the
        // first abort ended the lane and the missing replicates left no artifact at all.
        private int        _aborted;

        // --- entry placement audit (see AuditEntry) ---
        private Aircraft   _auditAc;
        private int        _auditFrame = -1;
        private float      _auditSpeed;     // what the placement commanded, to audit against
        private bool       _placed;         // this card has had its placement applied

        // --- entry ANCHOR (v0.84). See PlaceOnCondition: one spot on the map + one heading, captured
        // on the first placement of a run and re-imposed by every replicate after it. Held in the
        // GlobalPosition (datum-relative) frame so a floating-origin rebase mid-session cannot move it.
        //
        // v0.86: PER AIRCRAFT, which is the only reading that survives N of them. One shared anchor
        // would stack every drone on the same spot on the first replicate — a mid-air collision, not a
        // measurement. Each aircraft anchors where it ALREADY IS on its own first placement, so the
        // lateral separation is the one TestDrone already built (AbeamM + LaneM * slot, laid out on the
        // launch stagger) rather than a second spacing constant invented here to fight the first.
        private bool       _anchorSet;
        private Vector3    _anchorPos;
        private Vector3    _anchorFwd;      // horizontal unit heading captured with it

        // --- A/B arm interleaving (v0.84; PER AIRCRAFT since v0.94 — see ApplyArm) ---
        // No `_armSaved` and no `_armOwner`: nothing writes the global knob any more, so there is
        // nothing to save, nothing to restore and nothing for two suites to fight over.
        private ConfigEntry<bool> _armEntry;      // the toggle THIS suite is alternating; null = no schedule
        private int               _armIdx = -1;   // arm the current card is flying (-1 = no schedule)
        // Cards per replicate — the BLOCK the queue is built from (SelectCards repeats the whole
        // selection, c1,c2,c1,c2…). The arm is indexed by `_qi / _block`, i.e. by REPLICATE; see
        // ApplyArm for what indexing by queue position did instead.
        private int               _block = 1;

        // =========================================================================================
        // THE REGISTRY (v0.86) — one player per aircraft, keyed by Aircraft.GetInstanceID(), the same
        // key ChaseController, ManeuverRecorder and TestDrone use.
        // =========================================================================================
        private static readonly Dictionary<int, ScenarioPlayer> _byAc = new Dictionary<int, ScenarioPlayer>();
        private Aircraft         _ac;    // this player's aircraft — read by the eviction sweep
        private ManeuverRecorder _rec;   // its recorder, resolved once here so no later call has to

        internal static ScenarioPlayer For(Aircraft ac)
        {
            int id = ac.GetInstanceID();
            if (_byAc.TryGetValue(id, out var s)) return s;
            Sweep();   // eviction on the MISS path only, exactly as ChaseController.For does it
            s = new ScenarioPlayer { _ac = ac, _acId = id, _rec = ManeuverRecorder.For(ac) };
            _byAc[id] = s;
            return s;
        }

        // THE HOTKEYS' AND THE HUD'S PLAYER: the LOCAL one, never a drone's — the run/record/abort keys
        // and the on-screen card indicator all mean "the aircraft I am sitting in". Derived from the
        // game's own GetLocalAircraft, which an uncrewed drone can never satisfy.
        internal static ScenarioPlayer Player =>
            GameManager.GetLocalAircraft(out var ac) && ac != null && _byAc.TryGetValue(ac.GetInstanceID(), out var s)
                ? s : null;

        // Is the LOCAL player's card running? The bare bool two hot paths want (AimRig's marker
        // ownership and the Update-time throttle seam), both of which are about the human's aircraft.
        internal static bool PlayerPlaying { get { var s = Player; return s != null && s.Playing; } }

        // Drop an aircraft's playback state, ABORTING a card first — which is what closes its recorder
        // with a reason instead of leaving a capture that reads as a clean completion. Idempotent.
        internal static void Forget(Aircraft ac) { if (ac != null) Forget(ac.GetInstanceID()); }

        internal static void Forget(int aircraftId)
        {
            if (!_byAc.TryGetValue(aircraftId, out var s)) return;
            s.Abort("aircraft gone");
            _byAc.Remove(aircraftId);
        }

        // ponytail: linear scan on a path that runs once per new aircraft — see ChaseController.Sweep.
        private static void Sweep()
        {
            List<int> dead = null;
            foreach (var kv in _byAc)
                if (kv.Value._ac == null) (dead ?? (dead = new List<int>())).Add(kv.Key);
            if (dead == null) return;
            foreach (int k in dead) Forget(k);
        }

        // THE DEMAND THIS AIRCRAFT'S CARD IS ASKING FOR (world-space unit direction), or zero when no
        // card is running. The local player's card ALSO writes AimRig — that marker is the human's, one
        // per process, and it is what ChaseController.Apply reads — so for him this is a mirror and the
        // behaviour is v0.85's exactly.
        // v0.87 (phase 2) gave this its consumer: TestDrone.ChaseCard passes it straight into
        // ChaseController.FlyUncrewed, so a drone chases its own card through the same Apply the human
        // flies. That call is gated on `Playing`, deliberately — after a card ends (or the altitude
        // floor aborts it) this field holds the LAST demand written, exactly as the player's marker
        // does, and a drone must not go on chasing a dead card's final direction; with no card it
        // falls back to the harness's level-hold instead.
        public Vector3 AimDemand { get; private set; }

        private void SetDemand(Vector3 dir)
        {
            if (dir.sqrMagnitude < 1e-6f) return;
            AimDemand = dir.normalized;
            if (ReferenceEquals(this, Player)) AimRig.SetAimForward(dir);
        }

        // The arm, folded into Cfg.SnapshotString() and therefore into every capture's `# config`
        // header. `arm=` is a bare number so scorecard.py's existing cfg_params() regex picks it up
        // with no change on the Python side; `armKnob=` names WHICH toggle, because "arm=1" alone is
        // only meaningful if you already know what was being swept. Empty when nothing is scheduled,
        // so the startup config line and every hand-flown capture read exactly as they did before.
        //
        // v0.94: PER AIRCRAFT, like everything else about the arm. Two aircraft flying opposite arms
        // in the same instant is the whole point of the release, and a static tag would have labelled
        // both captures with whichever one wrote it last — the exact "scores fine, answers a different
        // question" artifact the A/B exists to avoid.
        public string ArmTag =>
            _armEntry == null ? "" : $"arm={_armIdx} armKnob={_armEntry.Definition.Key} ";

        // ...asked BY AIRCRAFT, for the recorder header. NON-CREATING on purpose: a hand-flown
        // capture has no ScenarioPlayer at all and must not gain one just by opening a recorder.
        internal static string ArmTagFor(Aircraft ac) =>
            ac != null && _byAc.TryGetValue(ac.GetInstanceID(), out var s) ? s.ArmTag : "";

        // --- card-recording state (per aircraft: it samples THIS aircraft's aim demand) ---
        private bool       _recording;
        private Quaternion _recFrame;
        private float      _recStep;
        private readonly List<float> _recAz = new List<float>();
        private readonly List<float> _recEl = new List<float>();
        private string     _recCls = "", _recAirframe = "";
        private float      _recSpeed, _recAlt;

        public bool Active => _card != null || _recording;

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
        public bool Playing => _card != null;

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
        public string HudLine
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
        // RUN-BOARD PROGRESS (v0.90). What the plugin's on-screen harness board reads, once per
        // aircraft per OnGUI — and OnGUI runs TWICE a frame (layout + repaint), so nothing here may
        // allocate or walk the queue. Everything below is a field read except CardSecondsLeft, which
        // walks this card's segment durations out of the cache built at each card boundary.
        //
        // Read-only by construction: the board is an instrument, and an instrument that can change
        // what it is measuring is not one.
        // =========================================================================================
        public int    AircraftId => _acId;          // TestDrone.DroneIdOf's key: "#3" vs "YOU"
        public string CardName   => _card != null ? _card.name : "";
        public int    RunIndex   => _qi + 1;        // the queue IS the replicate expansion (SelectCards)
        public int    RunCount   => _queue != null ? _queue.Count : 0;
        public int    SegIndex   => _si + 1;
        public int    SegCount   => _durs != null ? _durs.Length : 0;
        public string SegTag     => _card != null && _card.segments != null && _si < _card.segments.Length
                                  ? _card.segments[_si].tag : "";
        public int    RecSamples => _rec != null ? _rec.Samples : 0;

        // The airframe's display name, resolved once. Cached rather than read per frame because it is
        // constant for the life of the aircraft; "" if it cannot be read, which reads as unknown
        // rather than throwing into OnGUI (same fail-soft contract as the probes).
        private string _planeName;
        public string PlaneName
        {
            get
            {
                if (_planeName != null) return _planeName;
                _planeName = "";
                try { if (_ac != null && _ac.definition != null) _planeName = _ac.definition.name; }
                catch { /* naming is a bonus */ }
                return _planeName;
            }
        }

        // WHICH ARM THIS AIRCRAFT IS FLYING. Per aircraft since v0.94, so the board's lines can now
        // legitimately disagree — a batch of four drones mid-ABBA reads A/B/B/A, and that is the
        // release working rather than a display bug. "-" = nothing is being interleaved.
        public string ArmLabel => _armEntry == null ? "-" : (_armIdx == 1 ? "B" : "A");

        public float SegSecondsLeft =>
            _durs != null && _si < _durs.Length ? Mathf.Max(0f, _durs[_si] - _tSeg) : 0f;
        public float CardSecondsLeft  => _durs != null ? SegsLeft(_durs, _si, _tSeg) : 0f;
        public float SuiteSecondsLeft => CardSecondsLeft + _laterDur;

        // Cached at each card boundary so the two above cost no allocation and no walk of the queue.
        // _durs is the current card's segment durations (the pure arithmetic below takes plain floats,
        // which is what makes it checkable outside Unity); _laterDur is every queue entry after _qi.
        private float[] _durs;
        private float   _laterDur;

        private void IndexCard()
        {
            var segs = _card != null ? _card.segments : null;
            if (segs == null) { _durs = null; _laterDur = 0f; return; }
            _durs = new float[segs.Length];
            for (int i = 0; i < segs.Length; i++) _durs[i] = segs[i].dur;
            _laterDur = 0f;
            if (_queue != null) for (int i = _qi + 1; i < _queue.Count; i++) _laterDur += _queue[i].Duration;
        }

        // Every aircraft currently flying a card, into a list the CALLER owns and reuses — a returned
        // IEnumerable would allocate an iterator on a path that runs twice a frame. Keeps _byAc private.
        //
        // `Playing`, not `Active`: a card being RECORDED by hand has no queue, no segment schedule and
        // therefore no ETA, so it has nothing to put in a board row. It is already on screen — the
        // top-centre card indicator renders HudLine, which handles the recording case.
        internal static void CollectRunning(List<ScenarioPlayer> into)
        {
            into.Clear();
            foreach (var kv in _byAc)
                if (kv.Value._card != null) into.Add(kv.Value);
        }

        // -----------------------------------------------------------------------------------------
        // BOARD MATH. The two non-trivial pieces of the run board, kept PURE (plain numbers, no Unity
        // and no game types) for one reason: `debugtests/test-board-math.py` extracts the region
        // between the markers below, compiles it with the .NET SDK and runs it against its own case
        // table. So the check exercises THIS code rather than a Python copy of it — which is the only
        // version of that check worth having. Run it after touching either function:
        //     python debugtests/test-board-math.py
        // Keep both inside the markers, and keep them free of anything the SDK alone cannot compile.
        // --- BOARD-MATH BEGIN ---
        // Seconds as m:ss above a minute, "0.0s" below — an ETA of "252.4s" is not a number anyone
        // plans a coffee break around, and a segment countdown of "0:12" hides the tenths that say
        // whether it is about to change. Negative and NaN both clamp to zero: the board runs while a
        // card is mid-boundary, and "NaNs left" would look like a mod fault rather than a rounding one.
        internal static string Clock(float sec)
        {
            if (!(sec > 0f)) sec = 0f;                  // written as a NOT so NaN lands here too
            // 59.95, not 60: the branch below rounds to 0.1 s, so anything that would print as
            // "60.0s" belongs on the m:ss side instead.
            if (sec < 59.95f) return $"{sec:0.0}s";
            int m = (int)(sec / 60f);
            return $"{m}:{(int)(sec - m * 60f):00}";
        }

        // Seconds left in a card: the segments from `si` on, less the time already spent in `si`.
        // Summing FORWARD rather than (Duration - elapsed) keeps it correct when `si` has run off the
        // end of the array, which it legitimately does for the tick between the last segment expiring
        // and NextCard being called.
        internal static float SegsLeft(float[] durs, int si, float tSeg)
        {
            float left = 0f;
            for (int i = si > 0 ? si : 0; i < durs.Length; i++) left += durs[i];
            left -= tSeg;
            return left > 0f ? left : 0f;
        }
        // --- BOARD-MATH END ---

        // =========================================================================================
        // SELECTION (plan §5.2) — no custom UI. One ConfigEntry<bool> per card, so the F1
        // ConfigurationManager panel IS the enable/disable checklist. EVERY card defaults OFF,
        // built-ins included (v0.96; they defaulted ON as "the designed set" until the shipped grid
        // moved to disk and left them the legacy path). Registered first, they land at sel[0] — and
        // sel[0] alone dictates the FLEET (airframe/count/repeat/armToggle + the spawn alt/speed; see
        // SelectRaw for the per-card half), so one built-in checkbox nobody ticked collapsed a 10-lane
        // fleet card to a single Multirole1 at Cfg defaults: a capture that scores fine and answers a
        // different question.
        // Cfg.ScenarioCardSet overrides the whole thing for a scripted run.
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
            _enable[c.name] = _cf.Bind("Scenario Cards", c.name, false, new ConfigDescription(
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
                var c = Newtonsoft.Json.JsonConvert.DeserializeObject<Card>(System.IO.File.ReadAllText(path));
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

            // AIRFRAME MUST BE A jsonKey, NOT PROSE — healed, never fatal. For sixteen shipped cards
            // and every hand-written one before v0.90 this field was DOCUMENTATION ("any jet at the
            // fixedwing-v2 entry condition"), because nothing read it; v0.90 gave it behaviour and
            // pointed the drone spawn at it. An Encyclopedia jsonKey never contains whitespace, so
            // whitespace is the one unambiguous marker of the old meaning — blank it and the launch
            // falls back to Cfg.DroneAirframe, i.e. exactly the pre-v0.90 behaviour. Skipping the card
            // instead would turn a stale comment into a night of drones that never launch, and passing
            // it through would refuse the spawn with a prose sentence as the jsonKey. The human
            // description belongs in `note`, which the C# ignores by construction (Card has no such
            // field, and the deserializer ignores keys it can't map).
            //
            // v0.91: the field is a COMMA LIST (one jsonKey per drone lane, wrapping), so the test
            // is per TOKEN, not over the whole string — "Fighter1, Multirole1" is a two-airframe
            // fleet, while "any jet at the fixedwing-v2 entry condition" is still prose. A jsonKey
            // never contains whitespace, so a token that does is unambiguous; whitespace merely
            // AROUND the commas is formatting and is trimmed by AirframeList either way.
            if (!string.IsNullOrEmpty(c.airframe))
            {
                bool prose = false;
                foreach (string tok in c.airframe.Split(','))
                {
                    string t = tok.Trim();
                    foreach (char ch in t) if (char.IsWhiteSpace(ch)) { prose = true; break; }
                    if (prose) break;
                }
                if (prose)
                {
                    WTMouseAimPlugin.Log.LogWarning(
                        $"[card] '{c.name}': airframe '{c.airframe}' has an entry containing whitespace, so it is "
                        + "not a comma list of spawnable jsonKeys — ignoring it (the drone harness will use "
                        + "Drone/DroneAirframe). Put the human description in 'note' instead.");
                    c.airframe = "";
                }
            }
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
        // WHICH CARDS ARE SELECTED, before anything aircraft-specific: the ScenarioCardSet override if
        // one is set, else every ticked checkbox. Split out of SelectCards (v0.90) because the drone
        // preflight has to answer "what would a run fly?" BEFORE the aircraft exists — it is choosing
        // the airframe to spawn — and the class filter and the replicate expansion below both need one.
        // `quiet` exists for ONE caller: the run board, which previews this on a GUI poll rather than
        // on an operator action. A misspelled ScenarioCardSet is a real warning when a key press asks
        // the question and log spam when a repaint does — and an unattended batch's log is the only
        // artifact anyone reads afterwards.
        private static List<Card> SelectRaw(bool quiet = false)
        {
            var sel = new List<Card>();
            string ov = Cfg.ScenarioCardSet.Value;
            if (!string.IsNullOrEmpty(ov))
            {
                foreach (var raw in ov.Split(','))
                {
                    string n = raw.Trim();
                    if (n.Length == 0) continue;
                    var c = ByName(n);
                    if (c == null)
                    {
                        if (!quiet) WTMouseAimPlugin.Log.LogWarning($"[card] ScenarioCardSet names '{n}', which is not a known card.");
                    }
                    else sel.Add(c);
                }
            }
            else
            {
                foreach (var c in _cards)
                    if (_enable.TryGetValue(c.name, out var e) && e.Value) sel.Add(c);
            }
            // WHAT sel[0] ACTUALLY DECIDES, because the earlier wording here overstated it and cost a
            // design cycle. Two different questions get answered from this list:
            //
            //   SPAWN-TIME, answered ONCE per fleet with no aircraft in hand (Preview -> TestDrone):
            //   airframe, count, repeat, armToggle, and the alt/speed the drone is first placed at.
            //   These come from sel[0] alone and CANNOT vary per card — one drone is one airframe for
            //   its whole life, and the replicate/arm schedule indexes the queue as a block.
            //
            //   PER-CARD, re-answered at every card boundary (NextCard -> Tick): the card's segments,
            //   its config overrides (ApplyOverrides/RestoreOverrides), and its OWN entry condition —
            //   `_placed` resets per card and Tick calls PlaceOnCondition(ac, _card), not sel[0], so
            //   card 2 is flown to its own startAlt/startSpeed/startSpeedCorner before it is timed.
            //
            // So a multi-card selection is already a SEQUENCE OF DIFFERENT EXPERIMENTS on one airframe,
            // not one experiment with extra segments bolted on. Say only what is true: the ceiling is
            // the airframe roster and the replicate/arm schedule, not the test.
            //
            // WARN ON THE DISAGREEMENT, NOT ON THE COUNT (v0.99.1). This used to fire whenever
            // sel.Count > 1, which is the CORRECT case — a multi-card entry is the recommended shape
            // now — so the line was noise the operator learned to skip, and it was silent on the
            // actual mistake: a LATER card declaring a fleet field. Those are dropped on the floor,
            // the batch flies sel[0]'s fleet, and the capture scores fine against a different
            // question. Only a field that DIFFERS from sel[0]'s is lost; one that agrees is merely
            // redundant and says nothing worth a line. `quiet` is honoured around the whole loop, not
            // per warning: the run board polls this on repaint, twice a frame.
            if (!quiet)
                for (int i = 1; i < sel.Count; i++)
                {
                    var c = sel[i];
                    var lost = new List<string>(4);
                    if (!string.IsNullOrEmpty(c.airframe)  && c.airframe  != sel[0].airframe)  lost.Add($"airframe '{c.airframe}'");
                    if (c.count  > 0 && c.count  != sel[0].count)  lost.Add($"count {c.count}");
                    if (c.repeat > 0 && c.repeat != sel[0].repeat) lost.Add($"repeat {c.repeat}");
                    if (!string.IsNullOrEmpty(c.armToggle) && c.armToggle != sel[0].armToggle) lost.Add($"armToggle '{c.armToggle}'");
                    if (lost.Count == 0) continue;
                    WTMouseAimPlugin.Log.LogWarning(
                        $"[card] '{c.name}' declares {string.Join(", ", lost.ToArray())}, but it is not the "
                        + $"FIRST card in this selection — '{sel[0].name}' owns the fleet and those are "
                        + "IGNORED. Put it first, or give it its own entry in Scenario/ScenarioBatchQueue "
                        + "(a ';' starts a new fleet, which re-resolves airframe/count/repeat/armToggle).");
                }
            return sel;
        }

        // =========================================================================================
        // PREFLIGHT (v0.90) — what a run would fly, answerable with NO AIRCRAFT IN HAND.
        //
        // This exists for exactly one caller: TestDrone, which has to decide what airframe to spawn
        // and at what speed/altitude to place it, and which therefore cannot ask a question whose
        // answer needs the aircraft. So no class filter here (that is a per-aircraft, post-spawn
        // test — conflating the two would let a card's `cls` decide what metal gets spawned, which
        // is a different and wrong idea) and no replicate expansion (the count is reported, not
        // applied; each drone flies the queue itself).
        //
        // NEVER THROWS. It runs on a hotkey path before anything is spawned; a throw there would
        // cancel a launch with a stack trace instead of a refusal line.
        internal struct Preflight
        {
            public int    Cards;        // cards selected before replicates; 0 = nothing would fly
            public string Name;         // the first one, which is the one that decides everything below
            public string Airframe;     // "" = the card doesn't say; the Cfg value stands
            public float  StartAlt;     // <= 0 = doesn't say
            public float  StartSpeed;   // <= 0 = doesn't say
            // v0.93. Carried alongside StartSpeed rather than resolved into it, because with a
            // corner-relative card there IS no single answer for the batch — the number is per lane,
            // and this struct is answered with no aircraft in hand. TestDrone.SpeedOfLane turns the
            // pair into a speed once it knows which airframe the lane flies.
            public float  StartSpeedCorner;
            public float  Duration;     // seconds, one replicate of the first card
            // Every selected card summed, i.e. ONE replicate of the whole queue. Duration alone is the
            // first card's, so a multi-card selection times x Repeat off it under-reports the batch by
            // the rest of the queue — which on the run board is an ETA the operator plans around.
            public float  AllDuration;
            public int    Repeat;
            public string RepeatSrc;    // human-readable "who decided", for the launch log
            public string ArmKnob;      // "" = no A/B schedule
            public string ArmSrc;
            public int    Count;        // drones to launch (v0.91)
            public string CountSrc;
        }

        internal static Preflight Preview(bool quiet = false)
        {
            var p = new Preflight { Name = "", Airframe = "", RepeatSrc = "", ArmKnob = "", ArmSrc = "", CountSrc = "" };
            try
            {
                var sel = SelectRaw(quiet);
                p.Cards = sel.Count;
                if (sel.Count == 0) return p;
                var c = sel[0];
                p.Name       = c.name;
                p.Airframe   = c.airframe ?? "";
                p.StartAlt   = c.startAlt;
                p.StartSpeed = c.startSpeed;
                p.StartSpeedCorner = c.startSpeedCorner;
                p.Duration   = c.Duration;
                foreach (var x in sel) p.AllDuration += x.Duration;
                p.Repeat     = ResolveRepeat(c, out string rsrc); p.RepeatSrc = rsrc;
                p.Count      = ResolveCount(c, out string csrc);  p.CountSrc  = csrc;
                // Report the NAME only, resolved through the shared grammar but without touching the
                // config: SetUpArmSchedule does the real lookup a moment later and owns the warnings.
                // Two warnings for one typo would read as two problems.
                string spec = ResolveArmSpec(c, out string asrc);
                p.ArmSrc = asrc;
                if (SplitSpec(spec, out _, out string key)) p.ArmKnob = key;
            }
            catch (System.Exception e)
            {
                WTMouseAimPlugin.Log.LogWarning($"[card] preflight failed ({e.Message}) — the Drone settings stand as written.");
            }
            return p;
        }

        // Replicate count for a selection. The FIRST card's own `repeat` wins over Cfg.ScenarioRepeat,
        // because the card is the test and the number of replicates is part of it — an operator who
        // ticks a card designed for 8 runs and leaves the global at 1 gets a batch with no statistics
        // and nothing says so. The first card decides for the whole queue, matching how startSpeed and
        // the arm toggle are already read (a suite is blocked, so "per card" would mean interleaving
        // different replicate counts, which is not a design anyone can score).
        private static int ResolveRepeat(Card first, out string src)
        {
            bool fromCard = first != null && first.repeat > 0;
            src = fromCard ? $"card '{first.name}'" : "Cfg.ScenarioRepeat";
            return Mathf.Clamp(fromCard ? first.repeat : Cfg.ScenarioRepeat.Value, 1, 20);
        }

        // How many drones a selection wants (v0.91). THREE sources, in this order, and the middle one
        // is the whole point of the field:
        //   1. the card's own `count`, if it declares one;
        //   2. else, the NUMBER OF AIRFRAMES the card names — because a card whose `airframe` is the
        //      fleet it wants tested has already said how many drones it needs, and taking the number
        //      from a global instead is the failure this replaces: 12 keys against Cfg.DroneCount 4
        //      flies the first four lanes and answers a different question without refusing;
        //   3. else Cfg.DroneCount, i.e. exactly the pre-v0.91 behaviour for a card that says nothing.
        // Clamped to the same 1..16 as the Cfg knob — one clamp for the value, wherever it came from,
        // so a card cannot reach a fleet size the operator could not have set by hand.
        //
        // ResolveCount + CountKeys sit between markers because debugtests/test-fleet-resolve.py
        // extracts and compiles them verbatim: between them they decide HOW MANY drones fly and,
        // with TestDrone.AirframeList, what each lane is — a wrong answer here writes a full batch
        // of captures that score fine and answer a different question.
        // --- FLEET-RESOLVE BEGIN ---
        private static int ResolveCount(Card first, out string src)
        {
            if (first != null && first.count > 0)
            {
                src = $"card '{first.name}' count";
                return Mathf.Clamp(first.count, 1, 16);
            }
            int keys = CountKeys(first != null ? first.airframe : null);
            if (keys > 0)
            {
                src = $"card '{first.name}' airframe list ({keys} named)";
                return Mathf.Clamp(keys, 1, 16);
            }
            src = "Cfg.DroneCount";
            return Mathf.Clamp(Cfg.DroneCount.Value, 1, 16);
        }

        // Non-empty comma-separated entries in an airframe list. Mirrors TestDrone.AirframeList's
        // trim-and-drop-empties rule; kept here as a count-only twin rather than shared, because that
        // one returns the lane assignment and this one runs with no aircraft in hand from Preview.
        private static int CountKeys(string list)
        {
            if (string.IsNullOrEmpty(list)) return 0;
            int n = 0;
            foreach (string tok in list.Split(',')) if (tok.Trim().Length > 0) n++;
            return n;
        }
        // --- FLEET-RESOLVE END ---

        // The A/B knob spec a selection would sweep — the first card's `armToggle` if it declares one,
        // else the global. Same "first card decides" rule and the same reason as ResolveRepeat.
        private static string ResolveArmSpec(Card first, out string src)
        {
            bool fromCard = first != null && !string.IsNullOrEmpty(first.armToggle);
            src = fromCard ? $"card '{first.name}' armToggle" : "Cfg.ScenarioArmToggle";
            return fromCard ? first.armToggle : Cfg.ScenarioArmToggle.Value;
        }

        // The cards a run would fly RIGHT NOW: the ScenarioCardSet override if one is set, else every
        // ticked checkbox, minus anything whose airframe class doesn't match what you're in. Shared so
        // the entry-condition key places you where the run key would actually start you — two answers
        // to "which card" is how they drift apart.
        private static List<Card> SelectCards(Aircraft ac)
        {
            string cls = ClassOf(ac);
            var sel = SelectRaw();

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
            int rep = ResolveRepeat(sel.Count > 0 ? sel[0] : null, out _);
            if (rep > 1 && sel.Count > 0)
            {
                var once = new List<Card>(sel);
                for (int r = 1; r < rep; r++) sel.AddRange(once);
                // Deliberately NOT logged here: SelectCards is also called by the standalone entry key,
                // which flies nothing, and "repeat x4 -> 4 runs" printed on a placement is exactly the
                // kind of miscount this option exists to remove. ToggleSuite's "suite start: N card(s)"
                // is the one authoritative count, and it is already correct — and since v0.90 it also
                // names WHICH source set the replicate count, which is the same ResolveRepeat call.
            }
            return sel;
        }

        // =========================================================================================
        // A/B ARMS (v0.84). A batch flown as A×N then B×N converts ANY one-way session drift into a
        // fake effect: the R21 forensics measured 0.077 deg of pure first-half/second-half drift in
        // terminalOffDeg against that split's own 0.073 deg minimum detectable effect, i.e. doing
        // nothing at all read as significant. ABBA spreads a monotonic trend evenly over both arms,
        // which turns it from a confound into nuisance variance.
        //
        // ABBA by REPLICATE INDEX: ((i+1) >> 1) & 1 gives 0,1,1,0, 0,1,1,0, … Balanced when the
        // REPLICATE count is a multiple of 4; the suite-start log prints the whole schedule and its
        // per-card A/B tally so an unbalanced batch is visible BEFORE it flies rather than after.
        //
        // v0.99.1 — REPLICATE, not queue position, and the difference only shows with two cards up.
        // The queue is blocked (c1,c2,c1,c2 — see SelectCards), so `ArmOf(_qi)` handed card c1 the
        // arms at 0,2,4,6 = A,B,A,B: equal counts, mean position 1 vs 2 inside c1's OWN sequence,
        // while the queue-wide tally matched perfectly and nothing warned. `ApplyArm` divides by
        // `_block` first; `ArmOf` itself is unchanged, which is why the extracted program in
        // debugtests/test-arm-schedule.py still compiles against it.
        //
        // ponytail: one toggle, one fixed sequence, no factorial designs, no per-card arms. The
        // ceiling is a single boolean knob swept over one batch. If a real 2-factor experiment is
        // ever needed the upgrade is a list of (knob, value) pairs and a Latin square here — but
        // that is a different tool, and this one has to stay something you can read in ten seconds.
        //
        // v0.94 — CONCURRENT A/B. WHAT EACH AIRCRAFT'S OWN ABBA DOES AND DOES NOT BUY.
        //
        // THE INVARIANT ABBA EXISTS FOR: both arms must have the same MEAN POSITION IN THE BATCH, so
        // that a drift which is monotonic in run order contributes equally to both and cancels instead
        // of masquerading as an effect. (Not equal counts — ABBAAB has equal counts and still leans A
        // early. The balance check in ToggleSuite is on sum(index) for exactly that reason.)
        //
        // Until v0.94 the swept knob was a process-global `Cfg` entry the law read globally, so N
        // aircraft could not be on different arms in the same instant and ApplyArm stood the whole
        // schedule down whenever a second aircraft was mid-card: every A/B was a one-drone serial run,
        // which is why all five `e*` attribution cards pin `count: 1`. Now the lever is read through
        // ChaseController.Arm() off a PER-AIRCRAFT assignment, so nothing has to stand down.
        //
        // EACH AIRCRAFT IS ITS OWN INTERNALLY-BALANCED A/B, indexed by its OWN queue index (_qi) —
        // unchanged arithmetic, and still exactly "this run's position in this aircraft's batch". What
        // that buys: the drift-cancelling invariant holds within every lane, which is the unit of
        // analysis that matters, because compare-runs.py groups by (airframe, card, arm) and refuses
        // to pool across airframes anyway. A 4-lane fleet card is four independent A/Bs, not one.
        //
        // What it does NOT buy, deliberately: the arms are not balanced ACROSS aircraft at a given
        // wall-clock instant. Lane 0 and lane 1 launch a stagger apart and may both be on A at the
        // same second. That is fine — a confound would have to be one that hits the fleet at one
        // instant AND correlates with lane, and the two candidates (a frame hitch, an airframe
        // difference) are already handled: `frameMs` is a per-row column and airframes are never
        // pooled. Interleaving the launch order instead would trade a real per-lane guarantee for a
        // cosmetic fleet-wide one.
        //
        // ponytail: still one toggle and one fixed sequence — no factorial designs, no per-card arms.
        // A real 2-factor experiment would be a list of (knob, value) pairs and a Latin square here;
        // that is a different tool and this one has to stay readable in ten seconds.
        //
        // ArmOf lives between markers because debugtests/test-arm-schedule.py extracts and compiles
        // it verbatim — a Python copy of the sequence would agree with itself forever.
        // --- ARM-SCHEDULE BEGIN ---
        // `replicateIndex`, not the queue index — the caller divides by _block first (v0.99.1).
        internal static int ArmOf(int replicateIndex) => ((replicateIndex + 1) >> 1) & 1;
        // --- ARM-SCHEDULE END ---

        // =========================================================================================
        // NAMING A CONFIG ENTRY FROM TEXT. One grammar — "Key" or "Section/Key", bare keys defaulting
        // to Control, which is where every control-law lever lives, both halves non-empty and at most
        // one slash — shared by ScenarioArmToggle, a card's `armToggle` and every `config[].key`. One
        // parser, so the three cannot drift into three slightly different spellings of the same idea.
        //
        // Between markers because debugtests/test-spec-grammar.py extracts and compiles it verbatim,
        // and cross-checks scorecard.py's hand-written Python copy (`split_spec`) against ONE shared
        // case table — that copy is the offline half of `card_setup_problems`, and a copy that is
        // stricter or looser than this one flags cards that fly fine, or passes cards that do not.
        // (That test found exactly one such divergence, multi-slash; it was closed here in v0.96.)
        // =========================================================================================
        // --- SPEC-GRAMMAR BEGIN ---
        private static bool SplitSpec(string spec, out string sec, out string key)
        {
            sec = "Control"; key = "";
            if (string.IsNullOrEmpty(spec)) return false;
            spec = spec.Trim();
            int slash = spec.IndexOf('/');
            // At most one slash. "A/B/C" used to parse as section "A", key "B/C" — which no bound
            // entry can ever match, so it was a typo that only surfaced as a resolve warning after
            // the batch flew. Refusing it here is the same answer, said before anything spawns, and
            // it is what scorecard.split_spec (the offline card check) already did.
            if (slash != spec.LastIndexOf('/')) return false;
            if (slash >= 0) { sec = spec.Substring(0, slash).Trim(); key = spec.Substring(slash + 1).Trim(); }
            else key = spec;
            // Both halves must be non-empty: "/Foo" and "Foo/" are typos, and silently reading them as
            // a bare key would resolve the wrong entry (or none) without saying so.
            return sec.Length > 0 && key.Length > 0;
        }
        // --- SPEC-GRAMMAR END ---

        // Any bound entry, of ANY type — a card can pin a float or a KeyCode, not just the bool the
        // arm schedule sweeps. ConfigFile implements IDictionary<ConfigDefinition, ConfigEntryBase>
        // but implements most of it EXPLICITLY, so the interface cast is required, not stylistic.
        // Silent: every caller has its own, more specific warning to print.
        private static ConfigEntryBase ResolveEntry(string spec, out string sec, out string key)
        {
            if (!SplitSpec(spec, out sec, out key) || _cf == null) return null;
            var dict = (IDictionary<ConfigDefinition, ConfigEntryBase>)_cf;
            return dict.TryGetValue(new ConfigDefinition(sec, key), out var e) ? e : null;
        }

        // The bool toggle to interleave, or null (no schedule). `spec` comes from the card when it
        // declares one and from Cfg.ScenarioArmToggle otherwise; `source` only names which, for the
        // warnings — an operator chasing a typo needs to know WHICH of the two he mistyped.
        private static ConfigEntry<bool> ResolveArm(string spec, string source)
        {
            if (string.IsNullOrEmpty(spec)) return null;
            var e = ResolveEntry(spec, out string sec, out string key);
            if (e == null)
            {
                WTMouseAimPlugin.Log.LogWarning(
                    $"[card] {source} names '{sec}/{key}', which is not a bound setting — "
                    + "arms are NOT being interleaved. Check the spelling against the F1 panel.");
                Notify($"ARM: '{key}' is not a setting — not interleaving");
                return null;
            }
            var b = e as ConfigEntry<bool>;
            if (b == null)
            {
                // Distinct from "not found" on purpose: until v0.90 both landed on the same message,
                // so pointing the sweep at a real-but-numeric knob read as a spelling mistake and the
                // operator went looking for a typo that was not there.
                WTMouseAimPlugin.Log.LogWarning(
                    $"[card] {source} names '{sec}/{key}', which is a {e.SettingType.Name}, not an ON/OFF "
                    + "setting — arms are NOT being interleaved. An A/B arm has to be a bool; use "
                    + "the card's `config` list to pin a value of any other type.");
                Notify($"ARM: '{key}' is not ON/OFF — not interleaving");
                return null;
            }
            return b;
        }

        // =========================================================================================
        // CARD CONFIG OVERRIDES (v0.90). A card pins the knobs its test needs and hands them back
        // when it is done, so "tick the card, press the key" is the whole operator procedure.
        //
        // WHY THE VALUES ARE SAVED AND RESTORED rather than just written: the knobs are process-
        // global Cfg entries that the human's own flying reads too. A card that left them set would
        // silently retune the mod for the rest of the session, and the next hand-flown capture would
        // measure a law nobody chose.
        // =========================================================================================
        private ConfigEntryBase[] _ovEntries;      // pins THIS aircraft holds, in apply order = its release list
        private string            _ovNote = "";    // what was applied, verbatim into the '# override' header

        // -----------------------------------------------------------------------------------------
        // THE PINS ARE PROCESS-GLOBAL; THE PLAYERS ARE NOT — SO THE PINS ARE REFCOUNTED (v0.99.1).
        //
        // A `Cfg` entry is one per process and a ScenarioPlayer is one per AIRCRAFT, so a 16-lane
        // fleet ran ApplyOverrides sixteen times over the same entries and RestoreOverrides sixteen
        // times — and the FIRST lane to finish its card handed the knob back while the other fifteen
        // were still flying under it. Measured on one batch: 1469 rows across 61 of 512 legs flew at
        // the wrong ScenarioThrottle, stepping in exactly the 3 s launch stagger. Lanes 2..N also
        // "saved" the already-pinned value, so their own restores later RE-pinned it — which is why
        // the corruption reads as a square wave and not as one step, and why `_ovSaved` is gone: a
        // per-aircraft copy of a shared value is the defect, not the bookkeeping around it.
        //
        // One shared table keyed by the entry. The FIRST holder saves the pre-card value and writes
        // it; every later holder only increments; the value goes back when the LAST holder releases.
        // Static for the same reason the card library is: it describes a process-global resource, not
        // a flight. Every exit releases — Finish (normal end and, via it, Abort), NextCard at each
        // card boundary, and Forget(int), which Aborts BEFORE dropping the registry entry so a drone
        // that dies mid-card still lets go. A whole fleet lost without any of those (a scene unload)
        // self-heals: the next For() miss runs Sweep, which Forgets every dead player.
        private sealed class Pin
        {
            public object Saved;    // the value to put back when the last holder releases
            public int    Refs;     // how many aircraft are flying under it
            public string Text;     // the TOML text it was pinned to, i.e. what a second card must match
            public string Owner;    // the card that pinned it first, named in the disagreement warning
        }
        private static readonly Dictionary<ConfigEntryBase, Pin> _pins =
            new Dictionary<ConfigEntryBase, Pin>();

        // Acquire one pin. Returns false — having ALREADY logged — in exactly one case: another
        // aircraft holds this entry at a DIFFERENT value. A refcount cannot resolve that, because a
        // process-global knob has room for one answer and two concurrently-flying cards are asking
        // for two. First value wins, so the lanes already flying under it keep flying ONE condition;
        // the refused card is named, and since a refused pin never reaches `_ovNote` its capture's
        // '# override' header does not advertise a pin it did not get.
        // ponytail: one value per knob per process, plus a warning. If two concurrent cards ever
        // legitimately need different values, the upgrade is per-aircraft reads the way
        // ChaseController.Arm() already does it for the five A/B levers — far bigger than this defect.
        private static bool PinShared(ConfigEntryBase e, object v, string text, string card)
        {
            if (_pins.TryGetValue(e, out var pin))
            {
                if (pin.Text != text)
                {
                    WTMouseAimPlugin.Log.LogWarning(
                        $"[card] '{card}' pins '{e.Definition.Key}' to '{text}', but '{pin.Owner}' is already "
                        + $"flying with it pinned to '{pin.Text}' on another aircraft — that override is "
                        + "SKIPPED and the first value stands. A Cfg entry is process-global: two cards in "
                        + "the air at once cannot pin one knob to two values. Give them their own entries "
                        + "in Scenario/ScenarioBatchQueue (a ';' starts a new fleet, which flies alone).");
                    Notify($"OVERRIDE REFUSED: '{e.Definition.Key}' is pinned by another card");
                    return false;
                }
                pin.Refs++;
                return true;
            }
            _pins[e] = new Pin { Saved = e.BoxedValue, Refs = 1, Text = text, Owner = card };
            e.BoxedValue = v;
            return true;
        }

        // Release one pin; the value goes back only when the LAST holder lets go. An entry that is not
        // held is a no-op rather than a decrement, so releasing twice cannot drive the count negative
        // and strand the knob — see check-architecture.py's card-pin invariant, which is what stops a
        // future edit from writing BoxedValue around this pair.
        private static void UnpinShared(ConfigEntryBase e)
        {
            if (e == null || !_pins.TryGetValue(e, out var pin)) return;
            if (--pin.Refs > 0) return;
            _pins.Remove(e);
            try { e.BoxedValue = pin.Saved; }
            catch (System.Exception ex)
            {
                WTMouseAimPlugin.Log.LogWarning(
                    $"[card] could not restore '{e.Definition.Key}' ({ex.Message}) — it is left at the "
                    + "card's value. Reset it in F1 before flying anything else.");
            }
        }

        private void ApplyOverrides(Card c)
        {
            // The placement re-enters this path on the next tick (StartCard is deferred a tick so the
            // teleport can settle), and _ovEntries non-null means "this card's pins are already live".
            // Without the guard the second pass would restore-then-reapply every entry, firing two
            // spurious SettingChanged events per knob for no change in value.
            if (_ovEntries != null) return;
            var ovs = c != null ? c.config : null;
            if (ovs == null || ovs.Length == 0) return;

            var entries = new List<ConfigEntryBase>(ovs.Length);
            var note    = new System.Text.StringBuilder();
            foreach (var o in ovs)
            {
                if (o == null || string.IsNullOrEmpty(o.key)) continue;
                var e = ResolveEntry(o.key, out string sec, out string key);
                if (e == null)
                {
                    WTMouseAimPlugin.Log.LogWarning(
                        $"[card] '{c.name}' pins '{sec}/{key}', which is not a bound setting — that override "
                        + "is SKIPPED (the rest still apply). Check the spelling against the F1 panel.");
                    continue;
                }
                // REFUSE TO PIN THE KNOB THE A/B SCHEDULE IS SWEEPING. Since v0.94 the arm WINS (the
                // law reads it through ChaseController.Arm, which ignores the config value for the
                // swept knob), so the pin no longer collapses the batch onto one arm — it does
                // something just as bad the other way round: it silently does nothing to the flying,
                // while `# config` prints the pinned value and `# override` claims the card set it.
                // Either direction produces a capture that scores fine and describes a run that did
                // not happen, so this stays the one override failure that is named loudly and skipped.
                if (_armEntry != null && ReferenceEquals(e, _armEntry))
                {
                    WTMouseAimPlugin.Log.LogWarning(
                        $"[card] '{c.name}' pins '{sec}/{key}' to '{o.value}', but that is the knob the A/B "
                        + "schedule is sweeping — the override is REFUSED. The arm would win and the pin "
                        + "would change nothing about what flew, while the capture's header advertised it. "
                        + "Drop it from the card's `config` list, or sweep a different knob.");
                    Notify($"OVERRIDE REFUSED: '{key}' is the A/B knob");
                    continue;
                }
                try
                {
                    // BepInEx's own TOML reader, so one call covers bool/int/float/string/KeyCode and
                    // anything else that is bindable — a hand-rolled parser here would be a second,
                    // subtly different definition of what a config value looks like.
                    object v = TomlTypeConverter.ConvertToValue(o.value, e.SettingType);
                    // ACQUIRE, never write. PinShared owns the save and the write, so the value is
                    // taken once for the whole fleet and handed back once — see the refcount above.
                    if (!PinShared(e, v, o.value, c.name)) continue;
                    entries.Add(e);
                    note.Append(note.Length > 0 ? " " : "").Append(sec).Append('/').Append(key)
                        .Append('=').Append(o.value);
                }
                catch (System.Exception ex)
                {
                    // Fail-soft like every probe in this codebase: one named line, skip this one, fly
                    // the rest. Never throw — this runs inside the game's fixed step.
                    WTMouseAimPlugin.Log.LogWarning(
                        $"[card] '{c.name}' pins '{sec}/{key}' to '{o.value}', which is not a valid "
                        + $"{e.SettingType.Name} ({ex.Message}) — that override is SKIPPED.");
                }
            }

            // Assigned even when everything failed, so the re-entry guard above still holds and the
            // warnings are printed once per card rather than once per tick until the card starts.
            _ovEntries = entries.ToArray();
            _ovNote    = note.ToString();
            if (_ovEntries.Length > 0)
                WTMouseAimPlugin.Log.LogInfo(
                    $"[card] '{c.name}' pinned {_ovEntries.Length} setting(s) for this card: {_ovNote} "
                    + "(restored when the card ends).");
        }

        // Hand back every pin THIS AIRCRAFT holds. IDEMPOTENT — called from Finish, from Abort (via
        // Finish) and at every card boundary in NextCard, and one card must never inherit the previous
        // one's pins. The `_ovEntries == null` early return plus nulling it below is what makes a
        // double release impossible: the second call has no list to walk, so it cannot decrement a
        // refcount someone else is still holding.
        private void RestoreOverrides()
        {
            if (_ovEntries == null) return;
            for (int i = 0; i < _ovEntries.Length; i++) UnpinShared(_ovEntries[i]);
            _ovEntries = null; _ovNote = "";
        }

        // Put the current card's arm onto THIS AIRCRAFT, once per card, BEFORE the recorder opens — so
        // ArmTag lands in that capture's own `# config` header and the capture self-identifies.
        //
        // v0.94: this writes the CONTROLLER, never `_armEntry.Value`. Writing the global was what
        // forced the old stand-down (one knob, N aircraft) and what made a card's own pin of the swept
        // knob dangerous. Nothing here touches the operator's config, so his own aircraft goes on
        // flying whatever F1 says while a fleet sweeps around him.
        //
        // v0.99.1: THE INDEX IS THE REPLICATE, `_qi / _block` — NOT the queue position. With more than
        // one card selected the queue is BLOCKED (c1,c2,c1,c2… — see SelectCards), so `ArmOf(_qi)` gave
        // card c1 the arms at queue indices 0,2,4,6 = A,B,A,B: equal counts, but mean position 1 vs 2
        // WITHIN c1's own sequence. `SetUpArmSchedule`'s balance check ran over the whole queue (A at
        // 0,3,4,7 and B at 1,2,5,6, both summing 14) and reported balanced, while `compare-runs.py`
        // groups by (airframe, CARD, arm) — slicing along exactly the confounded axis. That is the R21
        // confound ABBA exists to kill, reintroduced by ticking a second checkbox and reported as fine.
        // Dividing first gives every card in a replicate the same arm and every card the same balanced
        // ABBA over replicates; `_block == 1` makes a single-card selection byte-identical to v0.99.
        private void ApplyArm()
        {
            if (_armEntry == null) { _armIdx = -1; return; }
            _armIdx = ArmOf(_qi / _block);
            ChaseController.SetArm(_acId, _armEntry.Definition.Key, _armIdx == 1);
        }

        // Standalone entry-condition key. Puts the aircraft exactly where a run would start it WITHOUT
        // starting the run — so you can get on condition, look around and press the run key when ready,
        // and so the teleport can be exercised on its own when it misbehaves (it has, twice).
        //
        // THE THREE HOTKEY DOORS (this, ToggleSuite, ToggleRecord) plus AbortLocal are static and
        // resolve the LOCAL aircraft themselves: a key press means "the aircraft I am sitting in".
        // Their bodies are instance methods, so a phase-2 drone runner drives the same code with a
        // different aircraft and no second copy of the logic can drift out of agreement with this one.
        public static void ForceEntryNow()
        {
            if (!AimRig.TryGetContext(out var ac, out _) || ac == null || ac.disabled)
            {
                WTMouseAimPlugin.Log.LogWarning("[card] no local aircraft — nothing to place.");
                Notify("ENTRY: no aircraft");
                return;
            }
            For(ac).ForceEntry(ac);
        }

        private void ForceEntry(Aircraft ac)
        {
            if (_card != null) { Notify("CARD RUNNING — abort it first"); return; }
            if (_recording)   { Notify("RECORDING — stop it first");     return; }
            Card c = null;
            // "Declares an entry condition" now means EITHER form — a corner-relative card carries no
            // startSpeed at all, and testing the raw field would make F3 skip straight past it.
            foreach (var x in SelectCards(ac)) if (EntrySpeed(x) > 0f) { c = x; break; }
            if (c == null)
            {
                WTMouseAimPlugin.Log.LogWarning(
                    "[card] no enabled card declares an entry condition — nothing to place the aircraft on.");
                Notify("ENTRY: no card declares one — see F1 > Scenario Cards");
                return;
            }
            // F3 means "put me on condition HERE", so it always re-anchors to where you are now. A run
            // started afterwards then anchors on the same spot, which is what makes this key an honest
            // preview of where the run begins.
            _anchorSet = false;
            PlaceOnCondition(ac, c);
        }

        public static void ToggleSuite()
        {
            if (!AimRig.TryGetContext(out var ac, out _) || ac == null || ac.disabled)
            {
                WTMouseAimPlugin.Log.LogWarning("[card] no local aircraft — nothing to fly.");
                Notify("CARD: no aircraft");
                return;
            }
            For(ac).StartSuite(ac);
        }

        // Start (or stop) a suite on ONE aircraft. Public-by-instance so phase 2 can run a card on a
        // drone through exactly this path.
        internal void StartSuite(Aircraft ac)
        {
            if (_card != null) { Abort("run key pressed again"); return; }
            if (_recording)   { StopRecord("run key pressed"); }

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
            // The replicate count is resolved inside SelectCards (it is what expanded `sel`); re-derive
            // it here purely to NAME ITS SOURCE. A card that carries its own `repeat` and a global left
            // at something else look identical in the run count, and the operator has to be able to see
            // which one he is actually flying before three minutes a run go by.
            int rep = ResolveRepeat(sel[0], out string repSrc);
            WTMouseAimPlugin.Log.LogInfo(
                $"[card] suite start: {sel.Count} card(s), {total:0}s total, class '{ClassOf(ac)}', "
                + $"replicates from {repSrc}.");
            // The block the queue was built from — cards per replicate, i.e. the post-class-filter card
            // count, recovered from the expansion rather than re-counted so the two cannot disagree.
            // ApplyArm indexes the A/B by `_qi / _block`; see there for what queue-indexing cost.
            _block = Mathf.Max(1, sel.Count / Mathf.Max(1, rep));
            _queue = sel; _qi = 0; _card = sel[0]; _si = 0; _tSeg = 0f;
            _frameSet = false; _placed = false; _lastLogSeg = -1; _acId = ac.GetInstanceID();
            _aborted = 0;
            IndexCard();             // run-board caches; must follow every write to _card/_qi/_queue
            _anchorSet = false;      // this run anchors HERE; the replicates after it come back to it
            _rec.EntryNote = "";     // an UNGATED card must not inherit the last one's note

            SetUpArmSchedule(sel.Count, sel[0]);
        }

        // Arm schedule. Resolved and PRINTED IN FULL before a single run flies — the schedule and its
        // A/B tally are the whole check on this feature, and reading them after the batch is three
        // minutes per run too late.
        //
        // NO OWNER since v0.94. There is no longer one shared thing to own: the sweep writes this
        // aircraft's own arm through ChaseController, so every suite resolves its own schedule and N
        // of them interleave side by side. What went with the owner: `_armSaved` (nothing to restore
        // — the global is never written) and the stand-down warning in ApplyArm.
        private void SetUpArmSchedule(int runs, Card first)
        {
            // The CARD's armToggle wins over the global when it declares one — the knob under test is
            // part of the test, and matching it by hand is exactly the mismatch this release removes.
            string spec = ResolveArmSpec(first, out string armSrc);
            _armEntry = ResolveArm(spec, armSrc);
            if (_armEntry == null) return;

            // The balance check is on the SUM OF INDICES per arm, not on the counts. Equal counts are
            // not the point — ABBA works by giving both arms the same average position, so that a
            // trend linear in run order cancels. A,B (n=2) has equal counts and is still a fully
            // confounded blocked design; ABBAAB (n=6) has equal counts and still leans A early. Both
            // are caught by comparing sum(i), and neither is by comparing n.
            //
            // v0.99.1 — THE SUBJECT OF THAT CHECK IS THE CARD, NOT THE QUEUE, and that is the fix. The
            // arm is now indexed by REPLICATE (`_qi / _block`, see ApplyArm), so the sequence a single
            // card flies is ArmOf over the replicate index — and the card is the analysis unit, since
            // `compare-runs.py` groups by (airframe, card, arm). Tallying over the whole queue was what
            // let the old defect through: 2 cards x repeat 4 gave every card A,B,A,B internally while
            // the queue-wide sums matched exactly and nothing warned. Balanced exactly when the
            // REPLICATE count is a multiple of 4; the display below is still the whole queue, because
            // that is the sequence the operator will watch fly.
            int reps = Mathf.Max(1, runs / Mathf.Max(1, _block));
            var sb = new System.Text.StringBuilder(runs);
            for (int i = 0; i < runs; i++) sb.Append(ArmOf(i / _block) == 1 ? 'B' : 'A');
            int nA = 0, sumA = 0, sumB = 0;
            for (int r = 0; r < reps; r++)
            {
                if (ArmOf(r) == 1) sumB += r; else { nA++; sumA += r; }
            }
            int nB = reps - nA;
            string key = _armEntry.Definition.Key;
            WTMouseAimPlugin.Log.LogInfo(
                $"[card] A/B arms on '{key}' (from {armSrc}; A = {key} OFF, B = ON): {sb} — "
                + $"{reps} replicate(s) x {_block} card(s), {nA} A / {nB} B per card. "
                + $"Each capture names its own arm on the '# config' line (arm=/armKnob=). "
                + $"THIS AIRCRAFT only (v0.94): the arm is per-aircraft state read through the controller, "
                + $"so other aircraft sweep their own schedules and the F1 value of '{key}' is never written.");
            if (nA != nB || sumA != sumB)
                WTMouseAimPlugin.Log.LogWarning(
                    $"[card] arm schedule is UNBALANCED WITHIN EACH CARD over {reps} replicate(s): {nA}/{nB}, "
                    + $"mean replicate index {(nA > 0 ? (float)sumA / nA : 0f):0.0} vs "
                    + $"{(nB > 0 ? (float)sumB / nB : 0f):0.0}. One arm sits earlier in every card's own "
                    + "sequence than the other, so a one-way session drift will still lean on it — use a "
                    + "REPLICATE count that is a MULTIPLE OF 4 (card 'repeat' / ScenarioRepeat; the number "
                    + "of cards selected does not enter into it).");
            Notify($"ARMS {sb} on {key}");
        }

        public static void ToggleRecord()
        {
            if (!AimRig.TryGetContext(out var ac, out _) || ac == null || ac.disabled)
            { WTMouseAimPlugin.Log.LogWarning("[card] no local aircraft — cannot record a card."); return; }
            For(ac).StartRecord(ac);
        }

        // Stop whatever the local player has running. Static because it is a hotkey door; a no-op
        // before he has an aircraft, which is the same as v0.85 (nothing could be running either).
        public static void AbortLocal(string reason) => Player?.Abort(reason);

        private void StartRecord(Aircraft ac)
        {
            if (_recording) { StopRecord("record key"); return; }
            if (_card != null) { Abort("record key pressed"); }

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
        public bool OwnInputs(Aircraft ac)
        {
            if (_card == null || ac == null) return false;
            try
            {
                var ci = ac.GetInputs();
                if (ci == null) return false;
                ci.brake = 0f;                          // wheel brake; the AIRBRAKE rides on throttle (below)
                // Ungated card (hover): the pilot keeps the collective. Through EntrySpeed so a
                // corner-relative card counts as gated — and cached on a reference compare, because
                // this runs every fixed step.
                if (EntrySpeed(_card) <= 0f) return false;
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
        // `internal` since v0.95 so PlayerSpawn can reuse it. This pair (ResetGLoadTrackers +
        // MoveAssembly) is the whole safe-teleport primitive, and both halves were learned by
        // destroying the airframe — anything that moves an aircraft MUST call both, so they are
        // shared rather than reimplemented.
        internal static void ResetGLoadTrackers(Aircraft ac)
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
        //
        // TWO THINGS HAVE BEEN TRIED HERE AND BOTH ARE GONE. Read this before adding a third; the
        // graveyard is the point of the comment.
        //
        // The problem both were aimed at is REAL and is still open (ledger #51). `AeroPart
        // .CheckAttachment` (decompile :74349-74365) detaches purely on GEOMETRY — 0.5 m between the
        // part's TRANSFORM and `attachInfo.localPosition`, the pose recorded at Awake (:84155),
        // measured in its PARENT PART's frame, no force and no damage anywhere in the test — and
        // `UnitPart.TakeDamage`'s detach clause explicitly excludes AeroParts (`&& !(this is
        // AeroPart)`, :84304), so for an aircraft that geometric test is the ONLY way a part can
        // leave. It reads as intermittent because the detector is a SAMPLER: `Aircraft.PartChecker`
        // (:60157-60180, driven from LocalSimFixedUpdate :61976) checks ONE part per fixed step,
        // round-robin, so a short-lived excursion is caught with probability ~k/N and missed
        // otherwise. Observed rate: R33 Darkreach 1/35; R35 4/35, 2/35, and EW1 1/38.
        //
        // (1) v0.96.1's MOD-SIDE AUDIT COULD NEVER FIRE. It iterated the same list the move loop had
        // just written, skipped every part the move loop skipped, and rebuilt its target with the
        // same expression the move used — with `pivot == rb.position` at both call sites
        // (`PlaceOnCondition` here, `PlayerSpawn.Place`) and `dRot == rot1 * Inverse(rb.rotation)`,
        // `pivot + dRot*(p0-pivot) + dPos` and `(pivot+dPos) + rot1*inv0*(p0-pivot)` are the same
        // number, so `err` was float noise by construction. Zero `[place]` lines over R35's 186
        // placements is what a tautology returns, not evidence that nothing was left behind — and the
        // skip meant it never examined the shared-body parts it was written to check.
        //
        // (2) v0.97.0 CALLED THE GAME'S OWN `AeroPart.Repair` (:74231) on every non-detached part,
        // reasoning that it writes exactly the quantity CheckAttachment compares, in the frame it
        // compares in. That part is right — `Repair` does write exactly that — and it is as far as
        // the reasoning goes: R36, the first batch flown with it, lost **32 of 32 placements —
        // 100%, every airframe**. Picking the correct quantity does not make it safe to write here.
        //
        // WHY, PRECISELY — the ban below is drawn around the MECHANISM, not around `Repair`, and the
        // first telling of this got the mechanism wrong. `Rigidbody.position` writes the PhysX pose
        // and leaves the Transform holding its OLD value until the next simulation step, and
        // `Physics.SyncTransforms` copies Transform -> PhysX, NEVER the reverse. So when the Repair
        // loop ran, every part transform still held the PRE-teleport pose: it read one
        // (`attachInfo.parentPart.xform`) and wrote another (`xform.position/rotation`), both
        // pre-teleport, so its arithmetic was near-correct and beside the point. The write DIRTIES
        // the transform, and since `AeroPart.CreateRB` (:74418) unparents every part it bodies, a
        // dirty transform plus a sync IS a body teleport — the parts went straight back to the OLD
        // lane. `Aircraft.rb` was untouched (the root part has `attachInfo == null`, so `Repair`
        // no-ops on it) and stayed at the anchor, so `Physics.Simulate` ran with the root 13.8-41 km
        // from its own parts. That is a CANCELLATION of the move, not a small displacement paid back
        // at ~err/dt, and deleting only the second sync would NOT have saved it — the physics step
        // syncs dirty transforms before simulating regardless. THE LETHAL ACT IS WRITING A TRANSFORM
        // IN HERE AT ALL. `Repair` never threw, so the try/catch logged nothing: a green build, a
        // green checker and a silent log meant exactly nothing here.
        //
        // THE SIGNATURE, AND THE NATURAL EXPERIMENT IN THE SAME LOG. Each kill lands on a
        // `PlaceOnCondition` at `segment arm at 0.0s`, speed going ~150 m/s -> 10602-172586 m/s in ONE
        // fixed step, log reading `ABORT (aircraft gone)` / `despawned (pilot killed)`, with
        // `Pilot.TakeGForceDamage` (:85989) doing the rest. **Replicate 1 is NOT placement-free** —
        // every one of R36's 32 carries a `# entry … snapBackM=0.0 … ctrlReset=1` line, so the
        // placement RAN on all of them, as a ZERO-DISPLACEMENT one. That is the experiment: 32
        // placements of 0 m — same unconditional `MoveAssembly` call, `Repair` loop included — were
        // 32/32 CLEAN, against 32/32 FATAL for the 32 of 13.8-41 km. The fault scales with the SIZE OF
        // THE MOVE and with nothing else; no code path was skipped. (Do not read the 10602-172586 m/s
        // spread as err/dt either — it is saturated by breakForce and solver clamps and is not
        // reproducible run to run: 20415 m at the rig's measured 19x predicts ~388000 m/s and read
        // 60147.)
        //
        // SO THE MOVE STAYS A RIGID TRANSFORM AND NOTHING ELSE, for the reasons at the top of this
        // comment. #51 stays open and INSTRUMENTED rather than fixed — `dmgFrac` (column 65) records it per
        // row and the v0.96 damage abort ends the run — because a 1-in-35 intermittent shed is
        // enormously cheaper than a 100% kill, and that is the trade a third attempt has to beat. Its
        // premise is now in doubt too: this method is an EXACT rigid transform whose float32 grain at
        // the 60-100 km a lane flies is ~0.004 m, ~125x under CheckAttachment's 0.5 m, so the
        // placement cannot produce an attach failure at all.
        // ponytail: if you do try again, fix the DISPLACEMENT, not the detection, and pick ONE of
        // exactly two shapes — MIXING THEM IS WHAT KILLED R36. (i) ALL-TRANSFORM MOVE: write
        // `xform.position/rotation` for every part AND the root with the same rigid formula, then one
        // `Physics.SyncTransforms()` as the last statement, and NO `rb.position` anywhere. Safe for
        // the exact reason the Repair loop was not — transform reads are uniformly stale, so the
        // formula lands on a self-consistent pre-move pose and the single sync commits one coherent
        // new one. It is what `FloatingOrigin.OriginShift` (:19380-19384) does. (ii) RE-CAPTURE, NOT
        // RESTORE: `UnitPart.CreateAttachInfo(attachInfo.parentPart)` (:84151) rebaselines to the
        // CURRENT pose, writes no transform and reads only relative geometry, so stale transforms
        // cannot hurt it; its costs are an `onParentDetached` subscription leaked on every call
        // (:84156, `+=` with no `-=`, replacing the game's own detach reference for the rest of the
        // flight) and a silently cleared `detachedFromParentPart`. Note also that `PartChecker`
        // iterates the PRIVATE `Aircraft.partsWithAero` (:60559), not `partLookup`, so a mod-side
        // re-derivation is already looking at the wrong set. Do NOT move `xform` alongside `rb` in
        // the loop below — that is the mixed scheme, which is precisely what R36 flew — and do not
        // reintroduce either graveyard entry above.
        internal static void MoveAssembly(Aircraft ac, Rigidbody rb, Quaternion dRot, Vector3 pivot,
                                          Vector3 dPos, Quaternion rot1, Vector3 vel)
        {
            var parts = ac.partLookup;
            int n = parts != null ? parts.Count : 0;

            for (int i = 0; i < n; i++)
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

            // The one sync, and it is the last statement on purpose: everything above wrote BODY
            // poses, and this is what pushes them to PhysX before the next `Physics.Simulate`.
            Physics.SyncTransforms();
        }

        // AUDIT THE PLACEMENT, two frames on. The joint spike showed up one or two ticks after the
        // write, so by now any of it would be in the velocity — and the failure mode is the aircraft
        // simply ceasing to exist, which leaves nothing in the log to read afterwards. One line here
        // turns "it exploded" into a number. Flight time is the scarce resource on this project; a run
        // that fails without saying why costs another one.
        private void AuditEntry()
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

        // =========================================================================================
        // ABORT — AND WHETHER IT KILLS THE REPLICATE OR THE WHOLE LANE (v0.99.1).
        //
        // It used to always kill the lane, because `Finish` nulls `_queue` and `_queue` IS the
        // replicate expansion (SelectCards repeats the selection in place). Measured on the STOL
        // batch: a 10-airframe fleet at ScenarioRepeat 4 was expected to write 40 captures and wrote
        // **13**. Nine lanes hit the altitude floor 19-26 s into replicate 1 and each abort took that
        // lane's other three replicates with it; the one lane that stayed up wrote its full 4. No
        // airframe missing, nothing damaged — the entire shortfall was this teardown.
        //
        // So the caller says which it is, and the DEFAULT IS FATAL: every existing caller keeps its
        // behaviour and only a reason that is demonstrably per-replicate opts out. Today that is the
        // altitude floor alone. The three that stay fatal each have a reason of their own:
        //   * airframe damage — an aircraft with a part missing is not the airframe the previous
        //     replicate flew (the same argument the abort itself is built on), so every remaining
        //     replicate would be non-comparable; and since the check is re-armed the moment the next
        //     card starts, a recoverable version would burn the queue in a few fixed steps writing
        //     one one-row capture per replicate.
        //   * aircraft changed or gone / Forget — there is no aircraft left to fly the next one.
        //   * the operator's keys, and the instructor declining — intent, not a mishap.
        //
        // WHAT MAKES THE NEXT REPLICATE VALID is `NextCard`, reused rather than re-implemented: it
        // already closes the recorder, releases this card's pins, advances `_qi`, resets `_si`,
        // `_tSeg`, `_frameSet`, `_placed`, `_lastLogSeg` and `_rec.EntryNote`, re-indexes the board
        // caches, and ends the suite properly when the queue runs out. The next tick then re-places
        // (which is also what re-drops the controller) and re-arms. `_anchorSet` is deliberately NOT
        // reset — the anchor is per RUN, and re-anchoring here would let a lane that aborted low and
        // downrange restart the rest of its replicates somewhere else, which is the confound the
        // anchor exists to remove. `_armEntry` is not cleared either: the next replicate's ApplyArm
        // needs the schedule it is halfway through. check-architecture.py's card-reset invariant is
        // what keeps that field list honest as fields are added.
        public void Abort(string reason, bool fatal = true)
        {
            if (_recording) { StopRecord(reason); return; }
            if (_card == null) return;
            _aborted++;
            WTMouseAimPlugin.Log.LogWarning($"[card] ABORT ({reason}) — '{_card.name}' segment "
                + $"{(_card.segments != null && _si < _card.segments.Length ? _card.segments[_si].tag : "?")} at {_tSeg:0.0}s"
                + (fatal ? " — the suite ends here." : $" — REPLICATE {_qi + 1} only; the lane flies on."));
            if (fatal) { Finish("abort: " + reason); return; }
            // Stop with the ABORT's reason before handing over: NextCard would otherwise stamp
            // "card '<name>' complete" into a CSV that was truncated, and `# stop` is what the scorer
            // and index-captures.py read to exclude a truncated run. Its own Stop is then a no-op.
            _rec.Stop("abort: " + reason);
            NextCard();
        }

        private void Finish(string reason)
        {
            _rec.Stop(reason);
            // AFTER the recorder is closed — restoring fires SettingChanged, and a '# cfg' line landing
            // in the capture that just finished would read as the law changing during the run. Same
            // reasoning as the apply side in Tick, mirrored.
            RestoreOverrides();
            _rec.SegmentTag    = "";
            _rec.CardTag       = "";
            _rec.EntryNote     = "";
            _rec.OverrideNote  = "";
            // Drop THIS AIRCRAFT'S arm assignment (v0.94). Nothing to restore — the sweep never wrote
            // the config — and nothing to coordinate: clearing one aircraft's arm cannot disturb a
            // schedule another aircraft is still flying, which is what the old ownership dance existed
            // to prevent. The despawn path clears it again via TestDrone.ForgetState; both are
            // idempotent, and one of them always runs.
            if (_armEntry != null) { ChaseController.SetArm(_acId, null, false); _armEntry = null; }
            _armIdx = -1;
            _anchorSet = false;
            // THE SHORTFALL, NAMED, while _queue is still here to count against. A lane that aborted
            // every replicate and a lane that never ran both end up as "fewer captures than expected"
            // on the analysis side; this is the line that tells them apart, and it says CAPS ABORTED
            // for the same reason the skip line says SKIPPING — so `index-captures.py --check`'s short
            // replicate counts have something to grep for.
            if (_aborted > 0)
                WTMouseAimPlugin.Log.LogWarning(
                    $"[card] suite ended with {_aborted} of {(_queue != null ? _queue.Count : 0)} "
                    + "replicate(s) ABORTED. Each one wrote its own capture with its reason on the "
                    + "'# stop' line, so a short count for this lane is those aborts and not a dropped "
                    + "recording — which is the distinction the analysis side could not make before.");
            _aborted = 0;
            _card = null; _queue = null; _qi = _si = 0; _tSeg = 0f; _frameSet = false; _placed = false; _lastLogSeg = -1;
            IndexCard();             // drops the caches with the card, so a stale ETA cannot outlive it
        }

        // =========================================================================================
        // THE TICK. For the PLAYER: from PilotPlayerStatePatch.Prefix, before ChaseController.BeginFrame
        // and therefore before the postfix's Apply() reads AimRig.AimForward — same fixed step, no lag.
        // For a DRONE (v0.86): from TestDrone.OnPilotStep, immediately before Drone.Fly reads it —
        // the same zero-tick property at that aircraft's own seam. Both are inside the game's own
        // per-pilot fixed step, so the two are directly comparable.
        // =========================================================================================
        public void Tick(Aircraft ac)
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
                SetDemand(HeadingFrame(ac) * (Quaternion.Euler(-RecoverElDeg, 0f, 0f) * Vector3.forward));
                // NOT FATAL (v0.99.1) — this is THE recoverable abort, and the only one. A card that
                // ran out of energy says nothing about the airframe: it is intact, still flying, and
                // the next replicate's placement lifts it back to the entry altitude and drops the
                // controller, which is exactly the state a fresh lane starts from. Nine of ten lanes
                // in the STOL batch died here on replicate 1 and lost their other three. The level
                // demand written above is what keeps it out of the water for the tick or two until
                // that placement runs.
                Abort($"altitude floor ({FloorAltM:0} m MSL)", fatal: false);
                return;
            }

            // SAFETY, the other kind: A PART FELL OFF. Over-G damages the PILOT only
            // (Pilot.TakeGForceDamage), so joint-break detachment is the only in-flight airframe
            // damage this rig produces — and it is enough to end the run, because an aircraft with a
            // part missing is not the same airframe the previous replicate flew and cannot contribute
            // a comparable sample. THRESHOLD IS ANY DETACHMENT, deliberately not the game's own 0.12
            // (the AI's "abandon this aircraft" test, decompile :12206/:13466): that number asks
            // whether the thing can still fight, and this is a measurement rig.
            // Same _frameSet gate and the same CLEAN TRUNCATION as the floor above — the reason names
            // the ratio, so the CSV's '# stop' line says how bent it was without opening the rows;
            // dmgFrac (per row) and the sidecar's detachedRatioAtStart carry the rest. Here rather
            // than in TestDrone because this one placement covers the drones AND the player.
            // Read is fail-soft and, unlike the recorder's, defaults to NOT damaged: "could not read
            // it" must never abort a good run, and the -1 in the column is what says the probe failed.
            if (_frameSet)
            {
                float dmg = 0f;
                try { if (ac.partDamageTracker != null) dmg = ac.partDamageTracker.GetDetachedRatio(); }
                catch { /* unreadable — see above */ }
                if (dmg > 0f) { Abort($"airframe damage (detached ratio {dmg:0.000})"); return; }
            }

            if (!_frameSet)
            {
                // ORDER IS LOAD-BEARING, TWICE OVER.
                //
                // (a) OVERRIDES BEFORE ApplyArm. If a card pins the knob the schedule sweeps, the ARM
                //     must win — ApplyArm running second guarantees it even if the refusal in
                //     ApplyOverrides were ever removed or bypassed. Belt to that braces.
                // (b) BOTH BEFORE StartCard, which is what calls _rec.Toggle(). ConfigFile.SettingChanged
                //     drives ManeuverRecorder.NoteConfigChange, which writes a '# cfg' line into every
                //     capture that is OPEN at the time. Writing the card's own setup after its recorder
                //     opened would stamp the card's own configuration into its own CSV as a mid-run
                //     config CHANGE — which is precisely the signal those lines exist to flag. Applying
                //     here (and restoring after _rec.Stop, see Finish/NextCard) keeps a card's setup out
                //     of its own capture and inside its '# config' / '# override' header instead.
                ApplyOverrides(_card);
                // Arm BEFORE the placement, so the whole run — including the placement tick's own pass
                // through Apply — flies one arm, and so the value is already in the config when
                // StartCard opens the recorder and stamps the '# config' header.
                ApplyArm();
                // Place first, start second — and place ONCE PER CARD, so every card in a suite gets its
                // own entry condition rather than inheriting the state the previous one left behind.
                // Returning after the placement gives it a tick to settle before the card is timed.
                if (Cfg.ScenarioForceEntry.Value && EntrySpeed(_card) > 0f && !_placed)
                {
                    _placed = true;
                    var placed = PlaceOnCondition(ac, _card);
                    // SKIP THE CARD, DO NOT END THE QUEUE (v0.99.1). An infeasible entry means this
                    // one pairing of card and airframe is wrong; the rest of the queue is unaffected
                    // and an unattended night must not lose it. NextCard is safe here: no recorder was
                    // opened (Stop is a no-op with no writer), RestoreOverrides is idempotent, and it
                    // ends the suite properly if this was the last entry. A `Failed` placement is the
                    // other thing entirely — the state is half-written — so that still ends the run.
                    if (placed == Placement.Infeasible)
                    {
                        WTMouseAimPlugin.Log.LogWarning(
                            $"[card] SKIPPING '{_card.name}' — its entry condition is outside this "
                            + "airframe's envelope (the refusal above names the bound). The rest of the "
                            + "queue still flies; this card contributes no capture, so a short replicate "
                            + "count for it means READ THE LOG, not a dropped recording.");
                        NextCard();
                    }
                    else if (placed == Placement.Failed) Finish("entry condition could not be set");
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
                // THE ENTRY CONDITION IS WRITTEN ONCE AND HELD BY NOTHING (v0.99.1) — so check that it
                // survived the `arm` segment, at the one boundary where it still means something.
                // Segment 0 is always `arm` (Validate refuses a card whose first tag is anything else)
                // and every segment after it is scored, so `_si == 1` is the last instant before the
                // measurement starts. Same root cause as the envelope gate above, one level further
                // on: EntryConditionError already answers "is this aircraft on the card's declared
                // condition?" and had exactly ONE call site — StartSuite's pre-flight refusal, which
                // ScenarioForceEntry (default ON) skips, i.e. the check nobody reaches.
                if (_si == 1) AuditHold(ac);
                _lastLogSeg = _si;
                WTMouseAimPlugin.Log.LogInfo($"[card] {_card.name} seg {_si + 1}/{segs.Length} '{s.tag}' ({s.dur:0.#}s)");
            }
            _rec.SegmentTag = s.tag;   // rows self-label; the recorder derives tSeg from the change
            SetDemand(SegDemand(s, _tSeg));
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

        // THE CLAMP IS KEPT AND MADE VISIBLE (v0.99.1). A `deriveAzRate` card's premise is that every
        // airframe flies the same FRACTION OF ITS OWN structural g — that is what makes one card
        // comparable across the roster under the one-law rule. The clamp breaks that premise for any
        // airframe whose derived rate lands outside 3..30 deg/s (measured: 4 of 10 on the STOL batch's
        // roster), and until now it did so silently: the card, the log and the capture all read as
        // though the demand were airframe-derived. The rate is still clamped — 30 deg/s bounds the
        // demand at something the roster can actually be asked for, and the number predates this fix
        // — but a clipped lane now says so by name at every card start, so a batch that pools those
        // four with the other six is a decision someone made rather than one nobody saw.
        // ponytail: a log line, not a column. The rate is constant for the whole capture, the card
        // name plus the sidecar's gLimit recover it, and the recorder's column contract is not worth
        // spending on a value that cannot vary within a run.
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
            float want = w * SustainedFrac;
            float got  = Mathf.Clamp(want, RateMinDegS, RateMaxDegS);
            if (Mathf.Abs(got - want) > 0.05f)
                WTMouseAimPlugin.Log.LogWarning(
                    $"[card] SWEEP RATE CLIPPED: {SustainedFrac:0.00} x this airframe's own instantaneous "
                    + $"rate is {want:0.0} deg/s at {v:0} m/s and {n:0.0} g, outside the "
                    + $"{RateMinDegS:0}..{RateMaxDegS:0} deg/s band — it will fly {got:0.0}. A deriveAzRate "
                    + "card claims every airframe flies the same FRACTION of its own structural limit; "
                    + "for this one it does not, so do not read its rate-derived metrics as the same "
                    + "stimulus the unclipped lanes flew.");
            return got;
        }

        // =========================================================================================
        // WHAT SPEED DOES THIS CARD WANT *THIS AIRFRAME* PLACED AT? (v0.93)
        //
        // ONE resolver, and that is the whole of the feature. `startSpeed` is read on the placement,
        // by the entry-condition gate, by the force-entry key, by the throttle-ownership test and by
        // three notices — so converting only the spawn site would place the aircraft at (say) 180 m/s
        // while EntryConditionError still demanded 250 and refused the run forever. Every read below
        // this line goes through here.
        //
        // PRIMITIVES rather than a Card, because the other caller — TestDrone's pre-spawn per-lane
        // check — holds a Preflight and no aircraft at all. A second copy of the policy over there is
        // exactly the drift this signature exists to prevent, and the two answers MUST agree: the
        // v0.92 envelope gate checks the speed the placement will later write.
        internal static float ResolveStartSpeed(float startSpeed, float startSpeedCorner, string jsonKey)
        {
            // The absolute form, and the overwhelmingly common one: byte-identical to pre-v0.93.
            if (startSpeedCorner <= 0f) return startSpeed;
            if (TestDrone.TryEnvelope(jsonKey, out var e) && e.Corner > 0f)
                return startSpeedCorner * e.Corner;
            // FAIL-SOFT, the same doctrine as the FBW / canard / helo probes: "could not read it" is
            // never "the corner speed is zero". A zero here would place every lane at 0 m/s, and a
            // probe that destroys a batch because a field was missing is worse than no probe. So fall
            // back to the card's absolute startSpeed — and if that is 0 too the card is simply
            // ungated, which is existing behaviour (the rotor cards live there).
            WTMouseAimPlugin.Log.LogWarning(
                $"[card] startSpeedCorner {startSpeedCorner:0.00}x could not be resolved for airframe "
                + $"'{jsonKey}' (no corner speed readable from Encyclopedia) — falling back to the card's "
                + $"absolute startSpeed of {startSpeed:0} m/s.");
            return startSpeed;
        }

        internal static float EffectiveStartSpeed(Card c, string jsonKey) =>
            c == null ? 0f : ResolveStartSpeed(c.startSpeed, c.startSpeedCorner, jsonKey);

        // THE INSTANCE FORM. Same resolver, but the answer is constant for (this aircraft, this card)
        // — the Encyclopedia lookup cannot change mid-flight — while one of its callers is OwnInputs,
        // which runs on every fixed step. Cached on a REFERENCE compare because the queue holds the
        // same Card object once per replicate (see SelectCards), so that is the whole cache key, and
        // one lookup per aircraft is what a batch actually costs.
        private Card  _entrySpeedFor;
        private float _entrySpeedVal;

        private float EntrySpeed(Card c)
        {
            if (c == null) return 0f;
            if (!ReferenceEquals(c, _entrySpeedFor))
            {
                _entrySpeedFor = c;
                _entrySpeedVal = EffectiveStartSpeed(c, JsonKeyOf(_ac));
            }
            return _entrySpeedVal;
        }

        // This aircraft's Encyclopedia key — the same string TestDrone spawned it by and the recorder
        // names its sidecar with. "" if it cannot be read, which resolves as "unknown envelope" and
        // therefore as the fail-soft fallback above; same contract as PlaneName.
        private static string JsonKeyOf(Aircraft ac)
        {
            try { return ac != null && ac.definition != null ? ac.definition.jsonKey : ""; }
            catch { return ""; }
        }

        // Entry-condition gate. A card's score is only comparable to another run of the same card if
        // both started from the same state, so a card that DECLARES startSpeed/startAlt refuses to fly
        // outside them. Until v2 these two fields were written by the recorder and read by nothing —
        // which meant "I hand-flew to roughly 250" was an uncontrolled input feeding every score.
        // Cards that declare nothing (neither startSpeed nor startSpeedCorner, so EntrySpeed resolves
        // to 0) are ungated, so ad-hoc recordings still just work.
        private const float SpeedTolFrac = 0.15f, AltTolM = 800f;

        // Put the aircraft ON the card's declared entry condition rather than asking the pilot to fly
        // there. Hand-flying to "roughly 250 m/s at roughly 4000 m" is not repeatable to the 1-3% the
        // metrics now resolve, and the R13 session showed the residue: with everything else held,
        // turn360's deltaEnergyHeightM still spread 35% on throttle-setting alone.
        //
        // v0.84 — WHAT "THE SAME STATE" HAD TO GROW TO MEAN. Ten replicates of one card (R21) came out
        // NOT exchangeable: terminalOffDeg correlated with run index at r = -0.824, and a first-half /
        // second-half split of a single unchanged arm produced 0.077 deg of pure drift against that
        // split's own 0.073 deg minimum detectable effect — i.e. changing nothing measured as
        // significant. Reading the ten captures back, the placement itself was fine (first sample of
        // every run: 250.1 m/s, 4000.0 m, to the recorder's own precision). Three things leaked around
        // it, all of them landing on the 6 s `arm` window and therefore on the state the SCORED segment
        // actually starts from:
        //
        //   1. POSITION was never reset. Only an altitude delta was applied, so the aircraft walked
        //      30 km downrange over the batch (posZ 527 -> 30 395 m) and no two replicates flew the
        //      same piece of map.
        //   2. THE AIM DEMAND WAS STALE FOR ONE TICK. The placement returned without writing one, so
        //      Apply ran that same tick against the PREVIOUS card's last demand and the freshly levelled
        //      attitude. Measured at the first recorded sample: outP +0.089 / +0.021 / +0.061 on runs
        //      1-3 against -0.487 / -0.487 / -0.487 on runs 8-10 — half a stick of leftover pitch. Those
        //      runs climbed during `arm` (3972 m vs 3965 m) and therefore entered the scored segment
        //      slower (271.3 vs 273.2 m/s). That is the drift, in the recorded columns.
        //   3. THE CONTROLLER CARRIED OVER. ChaseController is per-AIRCRAFT (v0.82) and every replicate
        //      is flown by the SAME aircraft, so integrators, the heading/marker-rate filters, the
        //      _pitchEff estimator and the slewed output all crossed the boundary from the end of one
        //      run's 80-degree-bank descending turn into the next run's entry.
        //
        // So the placement now re-establishes an ANCHOR (position + heading, captured on the first
        // placement of a run), writes the demand the card is about to ask for, and drops the controller.
        // Heading is anchored rather than merely preserved: it still points where the pilot set up — it
        // is captured from them on the first placement — but every replicate after that gets the same
        // one, which is what makes the trajectories comparable instead of merely the entry states.
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
        // WHAT IS DELIBERATELY *NOT* RESET, and why — an uncontrolled quantity that is not written down
        // is the exact failure this whole function exists to stop, so each one either has an argument or
        // an instrument:
        //   - ENGINE SPOOL / RPM. Not reset, and it does not drift: OwnInputs pins ci.throttle to
        //     ScenarioThrottle on EVERY tick a card is loaded, including across the card boundary, so
        //     the engine is at the same steady state for every replicate after the first. The `thr`
        //     column records the commanded value and the first-sample `spd` records the achieved one,
        //     so a disagreement is visible rather than assumed away.
        //   - AIRFRAME DAMAGE. The game exposes no repair we can call, and a damaged airframe is
        //     permanently different. Not resettable, so it is INSTRUMENTED instead: the `# entry`
        //     header line records the pre-placement speed/altitude and how far the aircraft had to be
        //     snapped back, and the audit two frames later logs the achieved speed. A run that
        //     silently stopped performing shows up as a drifting `snapBackM` and a failing audit.
        //   - WALL-CLOCK / SESSION AGE. Unresettable by definition; already a column (`tWall`), which
        //     is what lets a batch covary it out instead of arguing about it.
        //
        // WHY A THREE-STATE RESULT AND NOT A BOOL (v0.99.1). The two ways a placement can fail need
        // OPPOSITE degradations, and collapsing them cost either a whole suite or a run flown from a
        // half-written state. `Infeasible` = the envelope gate refused BEFORE anything was written, so
        // the aircraft is untouched and the honest degradation is "skip this card, fly the rest of the
        // queue" — an unattended night must not lose nine cards to one bad pairing. `Failed` = the
        // write threw somewhere in the middle, so the state is unknown and the suite ends, which is
        // what the catch below has always argued for.
        private enum Placement { Placed, Infeasible, Failed }

        private Placement PlaceOnCondition(Aircraft ac, Card c)
        {
            try
            {
                var rb = ac.rb;
                if (rb == null) return Placement.Failed;

                // THE ONE READ of the card's entry speed on this path (v0.93). Resolved once here and
                // used by the velocity write, the audit, the header note and both notices below — a
                // second `c.startSpeed` anywhere in this function would write one speed and report
                // another the moment a card is corner-relative.
                float vTgt = EntrySpeed(c);

                // THE v0.92 ENVELOPE GATE, ON THE PER-CARD PATH (v0.99.1). Until now it guarded only
                // the SPAWN velocity — sel[0]'s speed, once, in TestDrone.LaunchDue — so a later card
                // in a multi-card selection could ask 250 m/s of a CAS1 (Vmax 205.6) and the write
                // below would happily do it: the airframe cannot hold it, the capture measures the
                // decay, and it scores fine while answering a different question. That is the v0.92
                // failure one level down, and §0 of plans/multi-card-queue.md makes multi-card entries
                // the recommended shape, so it gets exercised. Ahead of every write, so an infeasible
                // card costs nothing and leaves the aircraft exactly as it was. Fail-soft in the same
                // direction as the spawn site: an unreadable envelope returns true and never refuses.
                if (vTgt > 0f && !TestDrone.EntrySpeedFlyable(JsonKeyOf(ac), vTgt))
                {
                    Notify($"CARD SKIPPED: '{c.name}' entry speed is outside this airframe's envelope");
                    return Placement.Infeasible;
                }

                var g = ac.GlobalPosition();                    // the game's own datum-relative struct
                Vector3 gp0 = new Vector3(g.x, g.y, g.z);
                float alt0 = gp0.y, v0 = rb.velocity.magnitude;

                // THE ANCHOR. Captured from the pilot on the FIRST placement of a run (so this is still
                // "where you set up"), then re-imposed by every replicate after it. Held in the
                // GlobalPosition frame — datum-relative — so a floating-origin rebase partway through a
                // long batch cannot move the target out from under us.
                if (!_anchorSet)
                {
                    Vector3 f0 = ac.transform.forward; f0.y = 0f;
                    // A flattened forward vanishes only if the nose is exactly vertical; fall back to the
                    // current transform forward rather than snapping to an arbitrary world axis.
                    _anchorFwd = f0.sqrMagnitude > 1e-6f ? f0.normalized : ac.transform.forward.normalized;
                    _anchorPos = gp0;
                    _anchorSet = true;
                }
                Vector3 fwd = _anchorFwd;

                float fuel0 = -1f, fuelTgt = Cfg.ScenarioEntryFuel.Value;
                if (fuelTgt > 0f)
                {
                    fuel0 = ac.GetFuelLevel();          // the ACTUAL ratio; ac.fuelLevel is the target
                    ac.fuelLevel = fuelTgt;
                    ac.Refuel(null);                    // null refueler => no "Refueled by" HUD banner
                }

                ResetGLoadTrackers(ac);                 // MUST precede the velocity write — see above
                _auditAc = ac; _auditSpeed = vTgt; _auditFrame = Time.frameCount + 2;

                // Snap back to the anchor in ALL THREE axes, not just altitude. A delta is the same in
                // the global and the physics frame as long as they differ only by a translation, which
                // is why this can be computed in GlobalPosition and applied to rb.position without
                // having to know whether (or when) the floating origin rebased.
                Vector3 tgt  = new Vector3(_anchorPos.x, c.startAlt > 0f ? c.startAlt : alt0, _anchorPos.z);
                Vector3 dPos = tgt - gp0;
                // Velocity along the level nose. v0.88 wrote it one measured trim-AoA BELOW the nose, on
                // the theory that AoA = 0 is zero lift and that the resulting 1 g catch was the entry
                // thump. R23 disproved it outright: run 01 of that batch is written UNTRIMMED (first
                // placement of the run, so no trim measured yet) and has the CLEANEST entry of the four
                // — no AoA overshoot at all, against 2.87 deg on the three trimmed ones. The transient
                // is the rate discontinuity now handled in ChaseController, not a lift hole, and
                // pre-pitching the velocity only stacked on top of it. Reverted rather than kept and
                // ignored: it also made each replicate's entry depend on a value measured during the
                // PREVIOUS replicate, a cross-replicate coupling in a rig whose whole purpose is
                // replicate independence (Gate A).
                Quaternion rot1 = Quaternion.LookRotation(fwd, Vector3.up);
                MoveAssembly(ac, rb, rot1 * Quaternion.Inverse(rb.rotation), rb.position, dPos,
                             rot1, fwd * vTgt);

                // NO STALE DEMAND. Apply runs from this same call's POSTfix, so without this the tick
                // that teleports the aircraft is also a tick chasing the previous card's last marker
                // from a brand-new attitude — worth half a stick of pitch, and the measured source of
                // the entry drift (see the v0.84 note above). The card's opening `arm` segment holds
                // az=0/el=0 in a frame captured from this very heading, so the level forward IS the
                // demand one tick from now. (A card whose `arm` is off-axis gets that step a tick
                // later than it would have; bounded by the card's own arm azimuth, and no card ships
                // one — not worth resolving the frame twice to remove.)
                SetDemand(fwd);

                // DROP THE CONTROLLER. Per-aircraft state (v0.82) — integrators, the heading and
                // marker-rate filters, _pitchEff, the slewed output — otherwise crosses from one
                // replicate into the next, because every replicate is flown by the same aircraft.
                // For() rebuilds it on the postfix's very next call, probes and all.
                ChaseController.Forget(ac);

                float snapBack = new Vector2(dPos.x, dPos.z).magnitude;
                string fuelMsg = fuel0 >= 0f ? $", fuel {fuel0:0.00} -> {fuelTgt:0.00}" : "";
                // Into the CAPTURE, not just the log: this is the per-replicate record of everything the
                // reset had to undo, which is what lets an analysis covary out whatever it could not.
                // The resolved SPEED is what goes in, not the multiple — the note is the record of what
                // the reset actually wrote, and the multiple is recoverable anyway (the card is named
                // in the header and the sidecar records this airframe's `cornerSpeed`). No new column
                // and no new key for a number two existing artifacts already pin down between them.
                _rec.EntryNote =
                    $"v={v0:0.0}->{vTgt:0.0} alt={alt0:0.0}->{(c.startAlt > 0f ? c.startAlt : alt0):0.0} "
                    + $"snapBackM={snapBack:0.0} fuel={(fuel0 >= 0f ? fuel0.ToString("0.000") : "-")}->"
                    + $"{(fuelTgt > 0f ? fuelTgt.ToString("0.000") : "-")} ctrlReset=1";
                // The log line DOES name the multiple: it is the operator-facing confirmation that the
                // card drove the number, and "180 m/s" alone reads identically whether it came from a
                // corner multiple, an absolute startSpeed or a fallback.
                string cornerMsg = c.startSpeedCorner > 0f ? $" ({c.startSpeedCorner:0.00}x corner)" : "";
                WTMouseAimPlugin.Log.LogInfo(
                    $"[card] entry condition set: {v0:0} -> {vTgt:0} m/s{cornerMsg}, {alt0:0} -> {c.startAlt:0} m"
                    + $"{fuelMsg}, wings level, snapped back {snapBack:0} m"
                    + " to the anchor heading, controller reset.");
                Notify($"ON CONDITION  {vTgt:0} m/s  {c.startAlt:0} m"
                    + (fuel0 >= 0f ? $"  fuel {fuelTgt:P0}" : ""));
                return Placement.Placed;
            }
            catch (System.Exception e)
            {
                // A half-applied entry condition is worse than no run: forcing BYPASSES the pre-flight
                // gate, so falling through here would fly the card from whatever state the pilot was
                // in and score it as if it were on condition. Refuse instead.
                WTMouseAimPlugin.Log.LogWarning($"[card] could not set entry condition ({e.GetType().Name}: {e.Message}) — refusing the run.");
                Notify("CARD REFUSED: could not set entry condition — see log");
                return Placement.Failed;
            }
        }

        // INSTANCE, not static, since v0.93: the speed it gates on is the one EntrySpeed resolves for
        // THIS aircraft. A static copy reading `c.startSpeed` would demand 250 of an airframe the
        // placement had just put on 180, and refuse the run forever.
        private string EntryConditionError(Card c, Aircraft ac)
        {
            float want = EntrySpeed(c);
            if (want <= 0f) return null;
            try
            {
                float v = ac.rb != null ? ac.rb.velocity.magnitude : 0f;
                float alt = ac.GlobalPosition().y;
                float dv = want * SpeedTolFrac;
                if (Mathf.Abs(v - want) > dv)
                    return $"airspeed {v:0} m/s, card wants {want:0} +/- {dv:0}";
                if (c.startAlt > 0f && Mathf.Abs(alt - c.startAlt) > AltTolM)
                    return $"altitude {alt:0} m, card wants {c.startAlt:0} +/- {AltTolM:0}";
            }
            catch { return null; }                          // unreadable state gates nothing
            return null;
        }

        // DID THE DECLARED ENTRY CONDITION SURVIVE THE `arm` SEGMENT? (v0.99.1) Called once per card,
        // at the arm -> first-scored-segment boundary. Instrument only: it changes nothing about what
        // flies, because the harness deliberately writes only the aim demand and the throttle pin, and
        // a speed-holding loop here would be the harness controlling the thing it is measuring.
        //
        // The failure it makes visible, measured on the STOL batch: a card declaring `startSpeed: 90`
        // was placed at 90 and, with the throttle pinned at 1.00 and no `config` override, was doing
        // 144-147 m/s by the end of a 6 s arm and 340-381 m/s by the last scored segment. Every metric
        // below that point describes an airframe at 4x the dynamic pressure the card asked for, and
        // nothing in the capture or the log said so. The FIX is card-side (pin a throttle the declared
        // speed can be trimmed at); this is the part that stops it being silent.
        private void AuditHold(Aircraft ac)
        {
            string bad = EntryConditionError(_card, ac);    // null for an ungated card, so rotor-* is quiet
            if (bad == null) return;
            WTMouseAimPlugin.Log.LogWarning(
                $"[card] ENTRY CONDITION NOT HELD: '{_card.name}' was placed on condition and the 'arm' "
                + $"segment has already left it — {bad}. The placement WRITES the state; nothing HOLDS "
                + "it. Throttle is pinned to ScenarioThrottle (Cfg, or the card's own 'config' pin) and "
                + "the airframe runs to whatever that trims at, so every scored segment below is flown "
                + "at the drifted state and not the declared one. Pin a throttle the declared speed can "
                + "hold, or declare a speed the pinned throttle holds.");
            Notify($"ENTRY NOT HELD: {bad}");
        }

        private void StartCard(Aircraft ac)
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
            if (_rec.IsRecording) _rec.Stop("card boundary");
            _rec.CardTag      = _card.name;
            _rec.SegmentTag   = "";
            // Set before Toggle(): Start() writes the whole header block in one go, so anything not in
            // hand by now is simply absent from the capture. The pins themselves are already applied
            // (Tick, above), so '# config' is also already truthful — this line only says which of
            // those values the CARD chose, which no amount of reading '# config' can recover.
            _rec.OverrideNote = _ovNote;
            _rec.Toggle();
            WTMouseAimPlugin.Log.LogInfo(
                $"[card] '{_card.name}' start ({_card.segments.Length} segments, {_card.Duration:0}s) — "
                + $"heading frame locked, demand is world-fixed from here. "
                + $"Derived sweep rate {_derivedRate:0.0} deg/s, throttle {EntryThrottle():0.00}.");
        }

        private void NextCard()
        {
            _rec.Stop($"card '{_card.name}' complete");
            // Card-to-card, after the close and before the next card's ApplyOverrides: card N+1 must
            // start from the session's own settings, not from whatever card N pinned. (Restoring after
            // the Stop for the same '# cfg' reason as Finish.)
            RestoreOverrides();
            _rec.SegmentTag = "";
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
            IndexCard();
            // A card that declares no entry condition (startSpeed 0 — the hover card) never reaches
            // PlaceOnCondition, so without this its capture would inherit the PREVIOUS card's reset
            // provenance and claim a placement that never happened.
            _rec.EntryNote = "";
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
        private Vector3 SegDemand(Seg s, float t)
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
        private void TickRecord()
        {
            Vector3 local = Quaternion.Inverse(_recFrame) * AimRig.AimForward;
            if (local.sqrMagnitude < 1e-6f) return;
            local.Normalize();
            _recAz.Add(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg);
            _recEl.Add(Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg);
            if (_recAz.Count >= MaxSamples) StopRecord("sample cap reached");
        }

        private void StopRecord(string reason)
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
                System.IO.File.WriteAllText(path,
                    Newtonsoft.Json.JsonConvert.SerializeObject(card, Newtonsoft.Json.Formatting.Indented));
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

using System.Collections.Generic;
using UnityEngine;

namespace NuclearOptionMouseAim
{
    // ---------------------------------------------------------------------------------------------
    // Maneuver recorder (v0.35). A hotkey-gated, bounded high-rate capture of the control state to its
    // OWN timestamped CSV (one file per recording, next to LogOutput.log) — so a feel problem can be
    // recorded cleanly across several aircraft and the reactive assist calibrated against real numbers,
    // without the always-on [anomaly] log or the verbose [chase] trace. Start/stop with RecordKey.
    // All file IO is guarded: a failure aborts the recording and never throws into the game loop.
    //
    // ONE INSTANCE PER AIRCRAFT (v0.86), same registry pattern as ChaseController (v0.82) and for the
    // same reason: the drone harness flies N aircraft at once, and N flights sharing one StreamWriter
    // is not a slightly worse capture — it is one file with N aircraft's rows interleaved under one
    // header, i.e. N runs destroyed. Get one with For(aircraft), release it with Forget, and never
    // `new` one. The audit that decides static-vs-instance is: DOES THIS VALUE REACH A CSV ROW OR A
    // PER-FLIGHT DECISION? If yes it is per-aircraft, because sharing it writes one aircraft's state
    // into another aircraft's file (that is exactly the trap `ChaseController.LastPhase` was in
    // before v0.82). Only _recSeq stays static and says why at its declaration.
    internal sealed class ManeuverRecorder
    {
        private System.IO.StreamWriter _w;   // open while recording, null otherwise
        private float _startTime;            // Time.time at start (elapsed + summary)
        private float _lastSample;           // throttle stamp (Time.time of last written row)
        private int   _samples;              // rows written this recording
        private int   _sinceFlush;           // rows since the last disk flush (see Start)
        private const int FlushRows = 50;    // ~1 s at the fixed step: crash-loss bound, not per-row I/O
        private string _path;                // current file path (for the summary line)
        private int   _recIndex;             // v0.63: the take number THIS recording drew from _recSeq

        // TAKE COUNTER — deliberately STATIC, and it is not the LastPhase trap. It carries no aircraft
        // state: it counts FILES OPENED this run, one artifact stream numbering per process, the same
        // argument as AnomalyLog's session file. Two consequences, both wanted: every capture's take
        // number is unique even when N recorders are open at once (so two drones can never land on the
        // same filename), and `rec=` stays MONOTONIC IN TIME across the whole batch — which is what
        // compare-runs.py's _run_order sorts the A/B balance check by. A per-aircraft counter would
        // hand it N runs all claiming rec=1.
        private static int _recSeq;

        public bool IsRecording => _w != null;
        public int  Samples     => _samples;
        public float Elapsed    => IsRecording ? Time.time - _startTime : 0f;
        // Bare filename of the active recording (for the anomaly file's rec= tag); "" when not recording.
        public string CurrentFile => _w != null ? System.IO.Path.GetFileName(_path) : "";
        // "R<run>-<take>" identity of the current/just-finished take (the R2-05 part of the CSV name) —
        // surfaced on the HUD indicator + stop toast so the maintainer can note which take they're on.
        // _recIndex survives Stop(), so it's valid in the stop feedback too.
        public string Tag => $"R{WTMouseAimPlugin.RunIndex}-{_recIndex:00}";

        // =========================================================================================
        // THE REGISTRY (v0.86) — one recorder per aircraft, keyed by Aircraft.GetInstanceID(), the
        // same key ChaseController and TestDrone use.
        // =========================================================================================
        private static readonly Dictionary<int, ManeuverRecorder> _byAc = new Dictionary<int, ManeuverRecorder>();
        private Aircraft _ac;     // this recorder's aircraft — read by Start's header/sidecar and the sweep
        private int      _acId;   // cached id: Forget must work after the aircraft is destroyed

        // The recorder for this aircraft, created on first use. One dictionary probe per aircraft per
        // fixed step on the Apply path — the whole registry holds single digits (DroneCount caps at 16).
        internal static ManeuverRecorder For(Aircraft ac)
        {
            int id = ac.GetInstanceID();
            if (_byAc.TryGetValue(id, out var r)) return r;
            Sweep();   // eviction on the MISS path only — a miss is once per aircraft, not once per tick
            r = new ManeuverRecorder { _ac = ac, _acId = id };
            _byAc[id] = r;
            return r;
        }

        // THE HUD'S / HOTKEY'S RECORDER: the LOCAL PLAYER's, never a drone's. Derived rather than
        // published, because unlike ChaseController nothing here runs on the fixed step to publish it
        // — and GetLocalAircraft is the game's own definition of "local", which an uncrewed drone can
        // never satisfy. Null before the player has an aircraft, so every call site is null-checked.
        internal static ManeuverRecorder Player =>
            GameManager.GetLocalAircraft(out var ac) && ac != null && _byAc.TryGetValue(ac.GetInstanceID(), out var r)
                ? r : null;

        // Drop an aircraft's recorder, CLOSING an open capture first — a drone that despawns mid-card
        // would otherwise leave a StreamWriter open with no writer and no '# stop' line, i.e. a capture
        // that reads as a clean completion. Idempotent.
        internal static void Forget(Aircraft ac) { if (ac != null) Forget(ac.GetInstanceID()); }

        internal static void Forget(int aircraftId)
        {
            if (!_byAc.TryGetValue(aircraftId, out var r)) return;
            r.Stop("aircraft gone");
            _byAc.Remove(aircraftId);
        }

        // ponytail: linear scan on a path that runs once per new aircraft. Unity reports a destroyed
        // object as null WITHOUT throwing, so a dead entry never announces itself — it just keeps a
        // corpse (and its open file handle) mapped forever.
        private static void Sweep()
        {
            List<int> dead = null;
            foreach (var kv in _byAc)
                if (kv.Value._ac == null) (dead ?? (dead = new List<int>())).Add(kv.Key);
            if (dead == null) return;
            foreach (int k in dead) Forget(k);
        }

        // CSV header — keep in lockstep with the Sample() row below. v0.55 adds assist (the game's
        // flight-assist toggle, 0/1 — closes the "was assist on?" ambiguity in every report) and the
        // FBW's own target/actual pitch rate (rad/s, GAME frame: + = nose down) for direct law fits.
        private const string Header =
            "t,off,azErr,elevErr,phi,bigTurn,bank,targetBank,outP,outR,outY," +
            "pitchRate,yawRate,rollRate,yawEff,yawWeak,spd,aoa,g,phase,flyLevel,engP,engR,engY,controlLaw," +
            "heliBlend,vFwd,rollRateF,iPitch,iYaw,bankTR,bankBlend,headingRateFilt,azErrPred,tBankE," +
            "assist,fbwTgtPR,fbwPR," +
            // v0.63 INTERNAL PITCH TERMS. Everything above is an INPUT or an OUTPUT; none of it says WHY
            // the law produced the pitch it did, so diagnosing the FS-12 wobble meant re-deriving the AoA
            // gate/bias offline from the AoA trace and hoping the reimplementation matched. These six are
            // the actual decision variables, logged as computed: tgtPRaw = the law's pitch BEFORE the AoA
            // block, aoaGU/aoaGD = the two ceiling gates (1 = open), aoaRec = the recovery bias input,
            // qSched = the final demand schedule after the AoA fold-in, pEff = the measured pitch
            // effectiveness. With these, tgtPRaw -> outP is fully reconstructible from the CSV alone.
            // v0.65 adds settleOn (0/1): did the B2 fine-settle micro-bank inject this frame — proves the
            // gate engaged during a settle and stood down during a marker sweep (runtime-only, not derivable).
            "tgtPRaw,aoaGU,aoaGD,aoaRec,qSched,pEff,settleOn," +
            // M0 (instructor loop — plans/instructor-feedback-loop.md §8). The scorer grades
            // achieved-vs-physically-achievable, and none of that is derivable from the 45 columns above:
            // alt+airDensity give TRUE dynamic pressure q = 0.5*airDensity*V^2 and energy height
            // Eh = h + V^2/2g; pos/vel give the hover/translate metrics and the turn geometry; segTag names
            // the test-card segment a row belongs to (empty in a hand-flown capture). Appended at the END
            // on purpose — every existing analyzer indexes by column position and must keep working.
            // NOTE: the control law still schedules on v^2, unchanged. This is instrumentation only.
            "alt,airDensity,posX,posY,posZ,velX,velY,velZ,segTag," +
            // Two more clocks beside `t` (scaled game time since level load), because one clock
            // can't answer everything a sweep asks. tSeg = seconds since the current segTag began,
            // so a card segment's metrics don't depend on when in the session it ran. tWall =
            // UNSCALED wall clock (Time.realtimeSinceStartup); the "# started" header line and the
            // sidecar's `utc` pin it to absolute time. The PAIR is also the diagnostic: dt/dtWall
            // should equal timeScale, so a run whose physics got clamped by a CPU stall shows up
            // in its own capture instead of having to be inferred.
            "tSeg,tWall," +
            // v0.77. The one input the mod does not command during a hand-flown capture and DOES
            // command during a card — and the only flight input that was invisible here. R18 flew a
            // whole card at idle because a stale config value meant something different in the build
            // that wrote it; the capture showed speed bleeding 250 -> 116 m/s with no way to tell a
            // bad throttle from a bad control law. Commanded, not achieved: the engine lags it through
            // its own spool (Turbojet minRPM/spoolRate), so a disagreement between this column and the
            // speed trace is itself the signal. ponytail: commanded only — add Turbojet.GetSpoolPercentage()
            // (public, via Aircraft.engineStates) if a capture ever needs to show a damaged/flamed-out engine.
            "thr," +
            // v0.78. The marker's own SIGNED azimuth rate (deg/s, + = sweeping right), filtered — i.e.
            // exactly the quantity the marker-rate feed-forward adds to the turn demand. Without it the
            // loop cannot distinguish "the feed-forward fired and helped" from "the feed-forward never
            // fired": both look like a smaller azimuth lag in a turn segment, and the second one is a
            // measurement artefact. It is also the falsifier for the A/B — the column must be ~0 through
            // every step-and-hold segment and ~the card's sweep rate through turn360, on BOTH sides of
            // the MarkerRateFeedForward toggle (the signal is always computed, only its use is gated).
            "aimRate," +
            // v0.83. The two decision variables behind the sustained-turn fixes, for exactly the reason
            // aimRate exists: both changes make a standing azimuth lag smaller, and so does the fix never
            // having fired, so without these a capture cannot tell them apart.
            //   iGate   = the wind gate the fine integrator ACTUALLY used this frame. With
            //             IntegralStallGate OFF this equals the old fineBlend = clamp01(1 - off/FineAngle)
            //             exactly, so a run where the gate never opened is visible as iGate == 0 at a
            //             standing error instead of having to be inferred from iPitch being flat.
            //   leadDeg = the anticipatory lead ACTUALLY subtracted from azErr (deg, signed). With
            //             RelativeTurnLead OFF it is headingRateFilt*TurnLeadTime; with it ON it is
            //             (headingRateFilt - aimRate)*TurnLeadTime — and since all three of azErr,
            //             headingRateFilt and aimRate are already columns, which branch ran is checkable
            //             by arithmetic, and predFloor binding is recoverable as azErrPred vs azErr-leadDeg.
            "iGate,leadDeg," +
            // v0.85. The roll-to-align loop, for the same reason as aimRate and iGate/leadDeg — the v0.85
            // changes and the v0.85 changes NEVER FIRING both read as a smaller roll oscillation.
            //   bSup    = the below-nose suppression ACTUALLY applied [0,1]. With BelowAlignSuppress OFF it
            //             is the old clamp01(-alignFrac)*(1-lateralHold)*taper; ON it is the roll-invariant
            //             belowness * taper. alignFrac is not a column and the roll-invariant one never was,
            //             so unlike leadDeg this is NOT recoverable by arithmetic from the other columns.
            //   bWt     = the roll blend weight after suppression — the loop gain the +0.918 correlation with
            //             |azErr| was measured on, and therefore the single number that says whether the
            //             positive feedback path is still open. Recomputing it offline needs bSup anyway.
            //   phiLead = degrees of bearing lead added to phi before the eAlign map (0 when the lever is
            //             off, and 0 inside the dead-astern wrap region where the lead stands down).
            "bSup,bWt,phiLead," +
            // v0.86. The RENDERED frame time (ms) that fixed step saw — TestDrone.FrameDt, which is
            // sampled every fixed step whether or not the harness is on. The drone launch stagger
            // exists BECAUSE a frame hitch lands on whatever segment is running when it happens: N
            // replicates flying the same segment at that instant are corrupted identically and stop
            // being independent samples. That was an assumption backed only by a '[drone] frame hitch'
            // warning in a log nobody diffs; as a column it is per-row evidence, so a batch can drop
            // (or covary out) the rows that were actually stalled instead of arguing about them. Also
            // the honest reading of tWall: dt/dtWall says timeScale slipped, this says WHY.
            "frameMs";

        // Segment tag stamped into every row (empty by default). The M1 ScenarioPlayer sets this per test
        // card segment ("az30", "reversal", "arm", …) so the offline scorer can slice one capture into
        // scored segments. Sanitised on assignment: a comma/quote/newline would corrupt the CSV.
        private string _segTag = "";
        private float  _segStart;                   // Time.time when the current tag began (drives tSeg)
        public string SegmentTag
        {
            get => _segTag;
            set
            {
                string v = string.IsNullOrEmpty(value) ? ""
                         : value.Replace(',', '_').Replace('"', '_').Replace('\r', '_').Replace('\n', '_');
                if (v == _segTag) return;           // only a real change restarts the segment clock
                _segTag   = v;
                _segStart = Time.time;
            }
        }

        // Card identity folded into the NEXT recording's filename (M1). The ScenarioPlayer sets it
        // around a card run and clears it afterwards, so a scripted capture is
        // "mouseaim-rec-v0.70.0-R8-03-fixedwing-v1-<stamp>.csv" and a hand-flown one keeps the old
        // name — which is what makes two builds' runs of the same card sort together and diff.
        // Sanitised on assignment: it lands in a filename.
        private string _cardTag = "";
        public string CardTag
        {
            get => _cardTag;
            set => _cardTag = FileSafe(value);
        }

        // v0.90. The config knobs THIS CARD pinned for itself, as "Section/Key=value" pairs — written
        // as one '# override' header line and nothing else.
        //
        // NOT A COLUMN, deliberately, and not merely to keep the count at 64: the value is constant for
        // the whole capture by construction (a card pins its knobs before the recorder opens and hands
        // them back after it closes), and a constant belongs in the header, not repeated on 9000 rows.
        //
        // It is also NOT redundant with '# config', which reports the live value of every knob and so
        // already shows the pinned ones — what it cannot show is that the CARD chose them rather than
        // the operator. That distinction is the whole point: it is what lets a batch tell "this run was
        // configured by its card" from "someone left a knob set". Empty for a hand-flown capture and
        // for a card that pins nothing, in which case no line is written at all. Sanitised on
        // assignment, same as EntryNote: a newline here would corrupt the header block.
        private string _overrideNote = "";
        public string OverrideNote
        {
            get => _overrideNote;
            set => _overrideNote = string.IsNullOrEmpty(value) ? ""
                                 : value.Replace('\r', ' ').Replace('\n', ' ');
        }

        // Everything that lands in a filename goes through this.
        private static string FileSafe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char ch in s)
                sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.' ? ch : '-');
            return sb.ToString();
        }

        // v0.84. One '#' header line the ScenarioPlayer fills in at its entry placement, emitted into
        // the capture that opens immediately after. It carries the per-replicate reset provenance —
        // the state the aircraft was in BEFORE being put on condition, how far it had to be snapped
        // back to the run's anchor, the fuel write, and that the controller was dropped. That is the
        // record of what the reset had to undo, so a batch can covary out whatever it could not undo
        // (airframe damage, session age) instead of being silently poisoned by it. Empty for a
        // hand-flown capture, in which case no line is written and the header is byte-identical to
        // before. Sanitised on assignment: a newline here would corrupt the header block.
        private string _entryNote = "";
        public string EntryNote
        {
            get => _entryNote;
            set => _entryNote = string.IsNullOrEmpty(value) ? ""
                              : value.Replace('\r', ' ').Replace('\n', ' ');
        }

        // Toggle THIS aircraft's capture. Returns the new state (true = now recording).
        public bool Toggle()
        {
            if (IsRecording) { Stop("toggled off"); return false; }
            return Start();
        }

        // The RecordKey hotkey door: the LOCAL player's recorder, never a drone's. Stopping works even
        // if the aircraft went away mid-capture (the instance outlives it until Forget/Sweep); starting
        // needs an aircraft, because the header, the FBW line and the sidecar are all read off it.
        public static bool ToggleLocal()
        {
            var r = Player;
            if (r != null && r.IsRecording) { r.Stop("toggled off"); return false; }
            if (!GameManager.GetLocalAircraft(out var ac) || ac == null)
            {
                WTMouseAimPlugin.Log.LogWarning("[rec] no local aircraft — nothing to record.");
                return false;
            }
            return For(ac).Start();
        }

        private bool Start()
        {
            try
            {
                string dir  = BepInEx.Paths.BepInExRootPath; // folder that holds LogOutput.log
                // v0.63 FILENAME = self-identifying. Was "mouseaim-rec-<wallclock>.csv", which forced you
                // to open the file to learn which build produced it and gave no ordering across boots —
                // a folder of 17 of them is unsortable by eye and easy to mis-attribute to the wrong build
                // (exactly the v0.61-vs-v0.62 confusion this is meant to end). Now: mod version, run index
                // (survives restarts), and the 1-based recording index within that run, wallclock last.
                // M1 adds the card name when a test card is driving the run (empty otherwise), so a
                // scripted capture says WHICH card it is without opening it — that is what makes two
                // builds' runs of the same card sort next to each other and diff.
                // v0.86 THE DRONE DISCRIMINATOR. N recorders are open at once now, so the name has to
                // say WHICH aircraft flew it — the take number alone is unique (see _recSeq) but
                // anonymous, and a folder of eight concurrent captures is unreadable without it. The
                // airframe rides along too, because a batch can now be HETEROGENEOUS (DroneAirframe
                // is a per-lane list), so "which drone" no longer implies "which aircraft"; the
                // sidecar's jsonKey stays the authoritative grouping key for compare-runs.py, this is
                // just so the folder is readable without opening anything. Both are empty for the
                // player, so a crewed capture's filename is byte-identical to v0.85.
                int drone = TestDrone.DroneIdOf(_acId);
                string frame = "";
                try { if (drone > 0 && _ac != null && _ac.definition != null) frame = _ac.definition.jsonKey; }
                catch { /* naming is a bonus; the header and sidecar carry the real identity */ }
                _recIndex = ++_recSeq;
                string name = $"mouseaim-rec-{WTMouseAimPlugin.RunTag}-"
                            + (drone > 0 ? $"d{drone}-" : "")
                            + (string.IsNullOrEmpty(frame) ? "" : FileSafe(frame) + "-")
                            + $"{_recIndex:00}-"
                            + (string.IsNullOrEmpty(_cardTag) ? "" : _cardTag + "-")
                            + System.DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv";
                _path = System.IO.Path.Combine(dir, name);
                // NOT AutoFlush. That flushed to disk on every one of ~50 rows/second, on the game's main
                // thread — a stall there stalls the sim, and the v0.71 captures show multi-second holes
                // with position continuous across them (a freeze, not a teleport). Flushing on an
                // interval keeps the crash-resilience that AutoFlush was there for (worst case one
                // FlushRows window of data lost) at a fraction of the syscalls.
                _w = new System.IO.StreamWriter(_path, false) { AutoFlush = false };
                _sinceFlush = 0;
                // Self-describing header block (v0.44): '#' comment lines (ignored as non-data by CSV
                // tooling and parsers) so the recording alone explains "what we were dealing with" — the
                // full gain set, active law, aircraft and the session id that ties it to the anomaly file.
                // v0.86: THIS recorder's aircraft, not GameManager's local one — a drone's capture must
                // describe the drone, and the drone is never the local aircraft by construction.
                string acName = "<unknown>", fbwLine = "<unavailable>";
                Aircraft acRef = _ac;
                try
                {
                    if (acRef != null)
                    {
                        if (acRef.definition != null) acName = acRef.definition.name;
                        fbwLine = ChaseController.For(acRef).FbwHeader(acRef); // v0.55: per-airframe FBW params (fail-soft)
                    }
                }
                catch { /* aircraft not resolvable right now — leave <unknown> */ }
                _w.WriteLine($"# mouseaim recording  v{WTMouseAimPlugin.PluginVersion}  run=R{WTMouseAimPlugin.RunIndex}"
                           + $"  rec={_recIndex}  session={WTMouseAimPlugin.SessionId}");
                _w.WriteLine($"# started {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}  t={Time.time:0.000}");
                _w.WriteLine($"# aircraft '{acName}'");
                // Own line, not appended to '# aircraft': scorecard.py matches that one with a greedy
                // `'(.*)'` and a header line is a contract. Absent entirely for a crewed capture.
                if (drone > 0) _w.WriteLine($"# drone {drone}");
                if (!string.IsNullOrEmpty(_cardTag)) _w.WriteLine($"# card {_cardTag}"); // M1: scripted run
                // Directly under '# card' because it only ever exists for one: it says what that card
                // set, so reading it apart from the card name is meaningless.
                if (!string.IsNullOrEmpty(_overrideNote)) _w.WriteLine($"# override {_overrideNote}"); // v0.90: what the CARD pinned
                if (!string.IsNullOrEmpty(_entryNote)) _w.WriteLine($"# entry {_entryNote}"); // v0.84: reset provenance
                _w.WriteLine($"# config {Cfg.SnapshotString()}");
                _w.WriteLine($"# fbw {fbwLine}");
                _w.WriteLine(Header);
                _startTime  = Time.time;
                _segStart   = Time.time;   // tSeg is measured from the recording start until a tag is set
                _lastSample = -999f; // force the first frame to sample
                _samples    = 0;
                WTMouseAimPlugin.Log.LogInfo($"[rec] recording -> {_path}");
                WriteAirframeSidecar(_path, acRef); // M0: capability dump next to the CSV (fail-soft, never throws)
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
        public void Stop(string reason)
        {
            if (_w == null) return;
            float dur = Time.time - _startTime;
            int   n   = _samples;
            string path = _path;
            // Why the run ended goes into the CAPTURE, not just the log. A run aborted at the altitude
            // floor or by a stick touch is otherwise indistinguishable from a clean completion to
            // anything reading the CSV — it just has fewer rows — so a batch would silently average
            // truncated runs in with whole ones. The scorer keys off this line to exclude them.
            try { _w.WriteLine($"# stop t={Time.time:0.000} dur={dur:0.0} samples={n} reason={reason}"); }
            catch { /* the summary is a bonus; never let it break the close path */ }
            CloseQuietly();
            WTMouseAimPlugin.Log.LogInfo($"[rec] done ({reason}) dur={dur:0.0}s samples={n} -> {path}");
        }

        private void CloseQuietly()
        {
            try { _w?.Flush(); _w?.Dispose(); } catch { /* ignore */ }
            _w = null;
        }

        // Write a live config-change marker into EVERY open recording so a mid-run tuning edit is inline
        // with the data. Called from Cfg's SettingChanged hook. Broadcast on purpose: every knob in Cfg
        // is process-global, so a live edit lands on every aircraft flying — a capture that did not
        // record it would be describing gains it was not flown with.
        public static void NoteConfigChange(string section, string key, object value)
        {
            foreach (var kv in _byAc) kv.Value.NoteConfigChangeInst(section, key, value);
        }

        private void NoteConfigChangeInst(string section, string key, object value)
        {
            if (_w == null) return;
            try { _w.WriteLine($"# cfg t={Time.time:0.000} {section}/{key} = {value}"); }
            catch (System.Exception e)
            {
                WTMouseAimPlugin.Log.LogWarning($"[rec] config-note write failed, stopping: {e.Message}");
                CloseQuietly();
            }
        }

        // Write one row if recording and the per-second throttle (RecordRateHz) allows it. Called from
        // ChaseController.Apply with the already-computed control state — no recompute. A write failure
        // stops the recording cleanly rather than throwing.
        public void Sample(
            float off, float azErr, float elevErr, float phi, float bigTurn, float bank, float targetBank,
            float outP, float outR, float outY, float pitchRate, float yawRate, float rollRate,
            float yawEff, float yawWeak, float spd, float aoa, float g, string phase, bool flyLevel,
            float engP, float engR, float engY, float heliBlend, float vFwd,
            float rollRateF, float iPitch, float iYaw, float bankTR, float bankBlend,
            float headingRateFilt, float azErrPred, float tBankE,
            bool assist, float fbwTgtPR, float fbwPR,
            float tgtPRaw, float aoaGU, float aoaGD, float aoaRec, float qSched, float pEff, bool settleOn,
            float aimRate, float iGate, float leadDeg,
            float bSup, float bWt, float phiLead,
            Aircraft ac)
        {
            if (_w == null) return;
            float now = Time.time;
            float minDt = 1f / Mathf.Clamp(Cfg.RecordRateHz.Value, 1f, 1000f);
            if (now - _lastSample < minDt) return;
            _lastSample = now;
            // M0 state block. Read here rather than at the call site so the caller stays one argument
            // wider instead of nine. Position is the game's DATUM-relative GlobalPosition (world minus
            // Datum.originPosition) so a floating-origin rebase mid-flight can't put a step in the trace;
            // its .y IS the game's own altitude MSL. Velocity comes from rb like the spd column, so
            // |vel| == spd exactly. Any failure leaves zeros — telemetry never breaks the recording.
            float alt = 0f, rho = 0f, thr = 0f;
            Vector3 pos = Vector3.zero, vel = Vector3.zero;
            try
            {
                if (ac != null)
                {
                    var gp = ac.GlobalPosition();
                    pos = new Vector3(gp.x, gp.y, gp.z);
                    alt = gp.y;
                    rho = ac.airDensity;
                    if (ac.rb != null) vel = ac.rb.velocity;
                    var ci = ac.GetInputs();
                    if (ci != null) thr = ci.throttle;
                }
            }
            catch { /* leave zeros */ }
            try
            {
                _w.WriteLine(
                    $"{now:0.000},{off:0.00},{azErr:0.00},{elevErr:0.00},{phi:0.0},{bigTurn:0.000}," +
                    $"{bank:0.0},{targetBank:0.0},{outP:0.000},{outR:0.000},{outY:0.000}," +
                    $"{pitchRate:0.000},{yawRate:0.000},{rollRate:0.000},{yawEff:0.000},{yawWeak:0.000}," +
                    $"{spd:0.0},{aoa:0.00},{g:0.00},{phase},{(flyLevel ? 1 : 0)},{engP:0.0},{engR:0.0},{engY:0.0},EvolvedLegacy," +
                    $"{heliBlend:0.000},{vFwd:0.0},{rollRateF:0.000},{iPitch:0.000},{iYaw:0.000},{bankTR:0.0},{bankBlend:0.000}," +
                    $"{headingRateFilt:0.00},{azErrPred:0.00},{tBankE:0.0},{(assist ? 1 : 0)},{fbwTgtPR:0.000},{fbwPR:0.000}," +
                    $"{tgtPRaw:0.000},{aoaGU:0.000},{aoaGD:0.000},{aoaRec:0.000},{qSched:0.000},{pEff:0.000},{(settleOn ? 1 : 0)}," +
                    $"{alt:0.0},{rho:0.0000},{pos.x:0.0},{pos.y:0.0},{pos.z:0.0},{vel.x:0.00},{vel.y:0.00},{vel.z:0.00},{_segTag}," +
                    $"{(now - _segStart):0.000},{Time.realtimeSinceStartup:0.000},{thr:0.000},{aimRate:0.000}," +
                    $"{iGate:0.000},{leadDeg:0.00},{bSup:0.000},{bWt:0.000},{phiLead:0.00}," +
                    $"{TestDrone.FrameDt * 1000f:0.0}");
                _samples++;
                if (++_sinceFlush >= FlushRows) { _sinceFlush = 0; _w.Flush(); }
            }
            catch (System.Exception e)
            {
                WTMouseAimPlugin.Log.LogWarning($"[rec] write failed, stopping: {e.Message}");
                CloseQuietly();
            }
        }

        // -----------------------------------------------------------------------------------------
        // AIRFRAME CAPABILITY SIDECAR (M0 — plans/instructor-feedback-loop.md §4, §5.1). One JSON file
        // per recording, same basename as the CSV with a .airframe.json extension. The CSV records what
        // the aircraft DID; this records what it COULD do — every per-airframe capability number the game
        // publishes (envelope, FBW authority, mass/fuel/stores, thrust, wing area, tilt/nozzle limits and
        // the SAMPLED Cl(a)/Cd(a) curves that make Clmax and the stall break computable offline). Without
        // it a score can only be compared against a fixed threshold; with it the grade is
        // achieved / physically-achievable-for-this-airframe-at-this-state, which is what makes cells
        // comparable across airframes.
        //
        // Fail-soft contract, same as the control law's probes: every read is wrapped, and a missing
        // component / renamed field / null curve drops THAT ONE FIELD and nothing else. A consumer must
        // treat every key as optional. Nothing here is allowed to disturb the recording it accompanies.
        // Hand-rolled JSON on purpose (no dependency); numbers are InvariantCulture so a non-dot locale
        // can't emit malformed JSON.
        // Airfoil sweep, 1-degree steps. Upper bound is 60, not 40: the KR-67 Ifrit's wing curve was
        // still RISING at the old +40 limit (Clmax landed exactly on the last sample), so the stall
        // break — the thing the offline n_max bound actually needs — fell outside the capture. Deltas
        // stall late; sample past it rather than guess where it is.
        private const int AlphaLoDeg = -5, AlphaHiDeg = 60;

        private static void Try(System.Action a) { try { a(); } catch { /* one field lost, sidecar lives */ } }
        private static string F(float v) => v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        // The build stamps SourceRevisionId from HEAD, which the SDK folds into the assembly's
        // InformationalVersion as "<version>+<sha>". Returns the sha, or null on a build without it.
        private static string BuildRevision()
        {
            var attrs = typeof(WTMouseAimPlugin).Assembly.GetCustomAttributes(
                typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
            if (attrs == null || attrs.Length == 0) return null;
            string v = ((System.Reflection.AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion;
            int plus = v == null ? -1 : v.IndexOf('+');
            return plus >= 0 ? v.Substring(plus + 1) : null;
        }

        private void WriteAirframeSidecar(string csvPath, Aircraft ac)
        {
            if (ac == null || string.IsNullOrEmpty(csvPath)) return;
            string path = null;
            try
            {
                path = System.IO.Path.ChangeExtension(csvPath, ".airframe.json");
                var sb = new System.Text.StringBuilder(16384);
                bool first = true;
                sb.Append("{");
                void Key(string k) { sb.Append(first ? "\n  \"" : ",\n  \""); first = false; sb.Append(k).Append("\": "); }
                void Num(string k, float v) { if (float.IsNaN(v) || float.IsInfinity(v)) return; Key(k); sb.Append(F(v)); }
                void Str(string k, string v) { if (string.IsNullOrEmpty(v)) return; Key(k); sb.Append('"').Append(Esc(v)).Append('"'); }
                void Bit(string k, bool v) { Key(k); sb.Append(v ? "true" : "false"); }
                void Raw(string k, string json) { Key(k); sb.Append(json); }
                // Private serialized float, read by name — the only per-field reflection here.
                void NumRef(string k, object o, string field) => Try(() =>
                {
                    var t = HarmonyLib.Traverse.Create(o).Field(field);
                    if (t.FieldExists()) Num(k, t.GetValue<float>());
                });

                // --- provenance (plan §5.1): which mod, which build, which game, which capture ---
                Str("modVersion", WTMouseAimPlugin.PluginVersion);
                Try(() => Str("modRevision", BuildRevision()));
                Try(() => Str("gameVersion", Application.version));
                Str("session", WTMouseAimPlugin.SessionId);
                Num("run", WTMouseAimPlugin.RunIndex);
                Num("rec", _recIndex);
                Str("csv", System.IO.Path.GetFileName(csvPath));
                Str("utc", System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ",
                    System.Globalization.CultureInfo.InvariantCulture));

                // --- identity: pilotType replaces every archetype heuristic (research-A §1) ---
                Try(() =>
                {
                    var d = ac.definition;
                    if (d == null) return;
                    Str("unitName", d.unitName); Str("jsonKey", d.jsonKey);
                    Str("code", d.code); Str("definitionName", d.name);
                });
                Try(() => { if (ac.pilots != null && ac.pilots.Length > 0 && ac.pilots[0] != null)
                                Str("pilotType", ac.pilots[0].pilotType.ToString()); });

                // --- mass / fuel / stores / thrust: the live state a bound must be normalised by ---
                Try(() => Num("massKg", ac.GetMass()));           // whole aircraft (rb.mass is the root part only)
                Try(() => Num("fuelLevel", ac.GetFuelLevel()));   // 0..1
                Try(() => Num("fuelKg", ac.GetFuelQuantity()));
                Try(() => { if (ac.GetMaxThrust(out float mt)) Num("maxThrustN", mt); });
                Try(() =>
                {
                    if (ac.loadout == null || ac.loadout.weapons == null) return;
                    var lb = new System.Text.StringBuilder("[");
                    for (int i = 0; i < ac.loadout.weapons.Count; i++)
                    {
                        var w = ac.loadout.weapons[i];
                        if (w == null) continue;                       // empty station
                        if (lb.Length > 1) lb.Append(", ");
                        lb.Append("{\"station\": ").Append(i)
                          .Append(", \"name\": \"").Append(Esc(string.IsNullOrEmpty(w.mountName) ? w.name : w.mountName))
                          .Append("\", \"mass\": ").Append(F(w.mass))
                          .Append(", \"emptyMass\": ").Append(F(w.emptyMass))
                          .Append(", \"drag\": ").Append(F(w.drag))
                          .Append(", \"emptyDrag\": ").Append(F(w.emptyDrag)).Append("}");
                    }
                    lb.Append("]");
                    Raw("loadout", lb.ToString());
                });

                // --- the devs' own capability table + the buffet schedule ---
                Try(() =>
                {
                    var p = ac.GetAircraftParameters();
                    if (p == null) return;
                    Num("aircraftGLimit", p.aircraftGLimit);
                    Num("cornerSpeed", p.cornerSpeed);
                    Num("turningRadius", p.turningRadius);
                    Num("maxSpeed", p.maxSpeed);
                    Num("takeoffDistance", p.takeoffDistance);
                    Bit("verticalLanding", p.verticalLanding);
                    Num("PIDReferenceAirspeed", p.PIDReferenceAirspeed);
                    if (p.AoAEffects != null)
                    {
                        Num("buffetOnsetAlpha", p.AoAEffects.OnsetAlpha);
                        Num("buffetFullVolumeAlpha", p.AoAEffects.FullVolumeAlpha);
                    }
                });

                // --- wing/drag area: Clmax is useless for an n_max bound without S (plan §4) ---
                // Source is Aircraft's PRIVATE `partsWithAero` list, not a hierarchy scan. Parts
                // register themselves via RegisterAeroPart and a complex-physics aircraft is
                // multi-rigidbody, so GetComponentsInChildren finds only the root part — v0.69.0
                // shipped exactly that bug and reported aeroPartCount=1 / wingAreaTotal=2 for a
                // 18 t jet (needs S ~= 20 m^2 to hold level flight). The game's own aero job sums
                // these same parts: lift = Cl * 0.5*rho*v^2 * wingArea per part.
                Try(() =>
                {
                    var list = HarmonyLib.Traverse.Create(ac).Field("partsWithAero")
                                         .GetValue() as System.Collections.Generic.List<AeroPart>;
                    // Fall back to the (incomplete) scan rather than emitting nothing.
                    var parts = (list != null && list.Count > 0)
                              ? list.ToArray() : ac.GetComponentsInChildren<AeroPart>(true);
                    if (parts == null || parts.Length == 0) return;
                    float s = 0f, da = 0f;
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (parts[i] == null) continue;
                        s += parts[i].WingArea; da += parts[i].dragArea;
                    }
                    Num("wingAreaTotal", s); Num("dragAreaTotal", da); Num("aeroPartCount", parts.Length);
                    Str("aeroPartSource", (list != null && list.Count > 0) ? "partsWithAero" : "childScan");
                });

                // --- FBW authority. 15 public params (index map in research-A §5) + the private fields.
                // maxRollAngularVel is the one that matters most: the game's roll command is pinned at
                // 0.5*maxRollAngularVel regardless of speed, so it is a CLOSED-FORM roll-rate ceiling.
                Try(() =>
                {
                    var cf = ac.GetControlsFilter();
                    if (cf == null) return;
                    Try(() =>
                    {
                        var (enabled, pr) = cf.GetFlyByWireParameters();
                        Bit("fbwEnabled", enabled);
                        if (pr == null) return;
                        var pb = new System.Text.StringBuilder("[");
                        for (int i = 0; i < pr.Length; i++) { if (i > 0) pb.Append(", "); pb.Append(F(pr[i])); }
                        Raw("fbwParameters", pb.Append("]").ToString());
                        if (pr.Length > 2) { Num("maxPitchAngularVel", pr[1]); Num("fbwCornerSpeed", pr[2]); }
                    });
                    var fbw = cf.GetFlyByWire();
                    if (fbw != null)
                    {
                        NumRef("gLimitPositive", fbw, "gLimitPositive");
                        NumRef("alphaLimiter", fbw, "alphaLimiter");
                        NumRef("alphaLimiterStrength", fbw, "alphaLimiterStrength");
                        NumRef("maxRollAngularVel", fbw, "maxRollAngularVel");
                        NumRef("maxRollSpeed", fbw, "maxRollSpeed");
                    }
                    // Rotorcraft never run the base FlyByWire — HeloControlsFilter overrides Filter and
                    // uses a private nested heloFlyByWire, so its rate ceilings need Traverse (as the
                    // v0.58 probe does). Without these the rotorcraft cells have no rate bound at all.
                    Try(() =>
                    {
                        if (!(cf is HeloControlsFilter hcf)) return;
                        object hfbw = HarmonyLib.Traverse.Create(hcf).Field("heloFlyByWire").GetValue();
                        if (hfbw == null) return;
                        NumRef("heloGLimit", hfbw, "gLimit");
                        var mav = HarmonyLib.Traverse.Create(hfbw).Field("maxAngularVel");
                        if (mav.FieldExists())
                        {
                            Vector3 m = mav.GetValue<Vector3>();   // x=pitch, y=yaw, z=roll (rad/s per unit stick)
                            Raw("heloMaxAngularVel", $"[{F(m.x)}, {F(m.y)}, {F(m.z)}]");
                        }
                    });
                });

                // --- tilt / nozzle travel (VTOL, tiltwing, STOVL) ---
                Try(() =>
                {
                    var wg = ac.GetComponentInChildren<IWingAngleGauge>(true);
                    if (wg == null) return;
                    Num("wingAngleDeg", wg.GetWingAngle());
                    Num("wingAngleMinDeg", wg.GetLowerAngleLimit());
                    Num("wingAngleMaxDeg", wg.GetUpperAngleLimit());
                });
                Try(() =>
                {
                    var ng = ac.GetComponentInChildren<INozzleGauge>(true);
                    if (ng != null) Num("nozzleAngleDeg", ng.GetNozzleAngle());
                });

                // --- sampled lift/drag curves: the ONLY clean source of Clmax and the stall break.
                // Evaluate() takes RADIANS and its alpha is the game's per-part airfoil frame
                // (alpha = Atan2(vLocal.y, vLocal.z)) — the NEGATION of the HUD/mod AoA sign. Recorded
                // raw; the offline scorer applies the sign. All airfoils are dumped (wing/tail/fuselage
                // each have their own) — the consumer picks, the mod does not guess which is "main".
                Try(() =>
                {
                    var p = ac.GetAircraftParameters();
                    if (p == null || p.airfoils == null || p.airfoils.Length == 0) return;
                    var ab = new System.Text.StringBuilder("[");
                    for (int d = AlphaLoDeg; d <= AlphaHiDeg; d++) { if (d > AlphaLoDeg) ab.Append(", "); ab.Append(d); }
                    Raw("airfoilAlphaDeg", ab.Append("]").ToString());

                    var fb = new System.Text.StringBuilder("[");
                    for (int i = 0; i < p.airfoils.Length; i++)
                    {
                        var af = p.airfoils[i];
                        if (af == null || af.liftCoef == null || af.dragCoef == null) continue;
                        if (fb.Length > 1) fb.Append(",");
                        fb.Append("\n    {\"name\": \"").Append(Esc(af.name)).Append("\", \"cl\": [");
                        for (int d = AlphaLoDeg; d <= AlphaHiDeg; d++)
                        { if (d > AlphaLoDeg) fb.Append(", "); fb.Append(F(af.liftCoef.Evaluate(d * Mathf.Deg2Rad))); }
                        fb.Append("], \"cd\": [");
                        for (int d = AlphaLoDeg; d <= AlphaHiDeg; d++)
                        { if (d > AlphaLoDeg) fb.Append(", "); fb.Append(F(af.dragCoef.Evaluate(d * Mathf.Deg2Rad))); }
                        fb.Append("]}");
                    }
                    Raw("airfoils", fb.Append("\n  ]").ToString());
                });

                sb.Append("\n}\n");
                System.IO.File.WriteAllText(path, sb.ToString());
                WTMouseAimPlugin.Log.LogInfo($"[rec] airframe sidecar -> {path}");
            }
            catch (System.Exception e)
            {
                // The recording itself is unaffected — the sidecar is a bonus artifact, not a dependency.
                WTMouseAimPlugin.Log.LogWarning($"[rec] airframe sidecar failed ({path ?? "<no path>"}): {e.Message}");
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Anomaly file (v0.44). The always-on companion to the maneuver recorder: a dedicated, session-scoped
    // file (mouseaim-anomalies-<session>.log next to LogOutput.log) that collects ONLY the [anomaly] and
    // [anomaly:trail] lines, separated from the noisy shared BepInEx log so a session's misbehaviours can
    // be read on their own. Opens lazily on the first anomaly while flying and stays open (AutoFlush, so
    // nothing is lost) for the rest of the session; the OS closes the handle on quit. Each line is also
    // tagged with the session id (header) + active control law + the recording it belongs to (in Anomaly),
    // so it cross-references the CSV and the BepInEx config log. All IO guarded — never throws into the loop.
    internal static class AnomalyLog
    {
        private static System.IO.StreamWriter _w;
        private static string _path;
        private static bool   _failed; // give up after a failure rather than retry every anomaly

        private static void EnsureOpen()
        {
            if (_w != null || _failed) return;
            try
            {
                string dir  = BepInEx.Paths.BepInExRootPath; // folder that holds LogOutput.log
                // v0.63: same self-identifying scheme as the recordings (version + run index) so the whole
                // artifact set for one boot sorts and greps together.
                string name = $"mouseaim-anomalies-{WTMouseAimPlugin.RunTag}-{WTMouseAimPlugin.SessionId}.log";
                _path = System.IO.Path.Combine(dir, name);
                _w = new System.IO.StreamWriter(_path, true) { AutoFlush = true }; // append: one file per session
                _w.WriteLine($"# mouseaim anomalies  v{WTMouseAimPlugin.PluginVersion}  run=R{WTMouseAimPlugin.RunIndex}"
                           + $"  session={WTMouseAimPlugin.SessionId}");
                _w.WriteLine($"# opened {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}  t={Time.time:0.000}");
                WTMouseAimPlugin.Log.LogInfo($"[anomaly] file -> {_path}");
            }
            catch (System.Exception e)
            {
                WTMouseAimPlugin.Log.LogWarning($"[anomaly] could not open anomaly file: {e.Message}");
                _failed = true;
                try { _w?.Dispose(); } catch { /* ignore */ }
                _w = null;
            }
        }

        // Append one already-formatted [anomaly]/[anomaly:trail] line. The caller still writes its own
        // BepInEx warning; this only mirrors it to the dedicated file. A failure disables the file for the
        // session (the BepInEx line keeps working) rather than retrying every event.
        public static void Write(string line)
        {
            EnsureOpen();
            if (_w == null) return;
            try { _w.WriteLine(line); }
            catch (System.Exception e)
            {
                WTMouseAimPlugin.Log.LogWarning($"[anomaly] file write failed, disabling: {e.Message}");
                _failed = true;
                try { _w?.Dispose(); } catch { /* ignore */ }
                _w = null;
            }
        }
    }
}

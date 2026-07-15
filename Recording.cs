using UnityEngine;

namespace NuclearOptionMouseAim
{
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
        // Bare filename of the active recording (for the anomaly file's rec= tag); "" when not recording.
        public static string CurrentFile => _w != null ? System.IO.Path.GetFileName(_path) : "";

        // CSV header — keep in lockstep with the Sample() row below. v0.55 adds assist (the game's
        // flight-assist toggle, 0/1 — closes the "was assist on?" ambiguity in every report) and the
        // FBW's own target/actual pitch rate (rad/s, GAME frame: + = nose down) for direct law fits.
        private const string Header =
            "t,off,azErr,elevErr,phi,bigTurn,bank,targetBank,outP,outR,outY," +
            "pitchRate,yawRate,rollRate,yawEff,yawWeak,spd,aoa,g,phase,flyLevel,engP,engR,engY,controlLaw," +
            "heliBlend,vFwd,rollRateF,iPitch,iYaw,bankTR,bankBlend,headingRateFilt,azErrPred,tBankE," +
            "assist,fbwTgtPR,fbwPR";

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
                // Self-describing header block (v0.44): '#' comment lines (ignored as non-data by CSV
                // tooling and parsers) so the recording alone explains "what we were dealing with" — the
                // full gain set, active law, aircraft and the session id that ties it to the anomaly file.
                string acName = "<unknown>", fbwLine = "<unavailable>";
                try
                {
                    if (GameManager.GetLocalAircraft(out var ac) && ac != null)
                    {
                        if (ac.definition != null) acName = ac.definition.name;
                        fbwLine = ChaseController.FbwHeader(ac); // v0.55: per-airframe FBW params (fail-soft)
                    }
                }
                catch { /* aircraft not resolvable right now — leave <unknown> */ }
                _w.WriteLine($"# mouseaim recording  v{WTMouseAimPlugin.PluginVersion}  session={WTMouseAimPlugin.SessionId}");
                _w.WriteLine($"# started {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}  t={Time.time:0.000}");
                _w.WriteLine($"# aircraft '{acName}'");
                _w.WriteLine($"# config {Cfg.SnapshotString()}");
                _w.WriteLine($"# fbw {fbwLine}");
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

        // Write a live config-change marker into the recording so a mid-run tuning edit is inline with the
        // data (no-op when not recording). Called from Cfg's SettingChanged hook. Guarded like every write.
        public static void NoteConfigChange(string section, string key, object value)
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
        public static void Sample(
            float off, float azErr, float elevErr, float phi, float bigTurn, float bank, float targetBank,
            float outP, float outR, float outY, float pitchRate, float yawRate, float rollRate,
            float yawEff, float yawWeak, float spd, float aoa, float g, string phase, bool flyLevel,
            float engP, float engR, float engY, float heliBlend, float vFwd,
            float rollRateF, float iPitch, float iYaw, float bankTR, float bankBlend,
            float headingRateFilt, float azErrPred, float tBankE,
            bool assist, float fbwTgtPR, float fbwPR)
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
                    $"{spd:0.0},{aoa:0.00},{g:0.00},{phase},{(flyLevel ? 1 : 0)},{engP:0.0},{engR:0.0},{engY:0.0},{Cfg.ControlLawMode.Value}," +
                    $"{heliBlend:0.000},{vFwd:0.0},{rollRateF:0.000},{iPitch:0.000},{iYaw:0.000},{bankTR:0.0},{bankBlend:0.000}," +
                    $"{headingRateFilt:0.00},{azErrPred:0.00},{tBankE:0.0},{(assist ? 1 : 0)},{fbwTgtPR:0.000},{fbwPR:0.000}");
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
                string name = "mouseaim-anomalies-" + WTMouseAimPlugin.SessionId + ".log";
                _path = System.IO.Path.Combine(dir, name);
                _w = new System.IO.StreamWriter(_path, true) { AutoFlush = true }; // append: one file per session
                _w.WriteLine($"# mouseaim anomalies  v{WTMouseAimPlugin.PluginVersion}  session={WTMouseAimPlugin.SessionId}");
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

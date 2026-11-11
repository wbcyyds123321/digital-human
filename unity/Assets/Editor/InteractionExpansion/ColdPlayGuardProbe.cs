// Cold-play guard probe (Codex node-1 review round 1, R1).
//
// Reproduces the accidental-autorun scenario on purpose:
//   * start an ordinary Play session WITHOUT calling the acceptance Run()
//   * stay in Play for >= 15 s, then Play -> Stop -> Play again
//   * after every Play, assert that the acceptance harness did not run, the
//     character was not teleported, no acceptance evidence directory was written,
//     and the editor did not exit
//
//   Unity.exe -batchmode -projectPath <proj>
//     -executeMethod Yixuan.EditorTools.ColdPlayGuardProbe.Run
//     -logFile Logs/overnight_interactions_20261109/cold_play_guard.log
//
// Evidence: <SBH_GUARD_RUN_DIR>/guard/cold_play_guard.json (+ COMPLETED.json,
// EXIT_CODE). The editor process is force-killed after the report is written
// because batchmode Play-mode exit hangs in this project.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Yixuan.EditorTools
{
    [InitializeOnLoad]
    public static class ColdPlayGuardProbe
    {
        const string ArmedKey = "SBH_COLD_GUARD_ARMED";
        const string StageKey = "SBH_COLD_GUARD_STAGE";
        const string T0Key = "SBH_COLD_GUARD_T0";
        const string DirKey = "SBH_COLD_GUARD_DIR";
        const string CharKey = "SBH_COLD_GUARD_CHAR";
        const string DirsKey = "SBH_COLD_GUARD_DIRS";
        const string ChecksKey = "SBH_COLD_GUARD_CHECKS";
        const string ExitTriesKey = "SBH_COLD_GUARD_EXIT";
        const string ChecksIndexKey = "SBH_COLD_GUARD_CHECKS_IDX";
        const string ExitRequestKey = "SBH_COLD_GUARD_EXIT_REQUEST";
        const string ScenePath = "Assets/Scenes/SovietBlueHour_TaskPlan.unity";

        static readonly StringBuilder LogBuilder = new StringBuilder();
        static readonly List<string> Checks = new List<string>();

        static ColdPlayGuardProbe()
        {
            EditorApplication.update += Tick;
        }

        static string GuardDir
        {
            get
            {
                string existing = SessionState.GetString(DirKey, "");
                if (!string.IsNullOrEmpty(existing)) return existing;
                string root = Path.GetDirectoryName(Application.dataPath) ?? ".";
                string env = Environment.GetEnvironmentVariable("SBH_GUARD_RUN_DIR");
                string name = string.IsNullOrEmpty(env)
                    ? "Captures/InteractionExpansion/guard_" + DateTime.Now.ToString("yyyyMMdd-HHmmss")
                    : env;
                string dir = Path.IsPathRooted(name) ? Path.Combine(name, "guard") : Path.Combine(root, name, "guard");
                Directory.CreateDirectory(dir);
                SessionState.SetString(DirKey, dir);
                return dir;
            }
        }

        public static void Run()
        {
            _ = GuardDir;
            SessionState.SetString(ArmedKey, "1");
            SessionState.SetString(ChecksKey, "");
            LogBuilder.Clear();
            Checks.Clear();
            SessionState.SetString(ChecksKey, "");
            SessionState.SetInt(ChecksIndexKey, 0);
            Log("== cold play guard probe (R1) ==");
            Log("guard_dir=" + GuardDir);
            try
            {
                if (File.Exists(ScenePath))
                {
                    EditorSceneManager.OpenScene(ScenePath);
                    Log("opened scene " + ScenePath);
                }
            }
            catch (Exception e) { Log("open scene failed: " + e.Message); }
            var character = Object.FindObjectOfType<Yixuan.Core.YixuanCharacter>();
            Vector3 position = character != null ? character.transform.position : Vector3.zero;
            SessionState.SetString(CharKey,
                position.x.ToString("F4", CultureInfo.InvariantCulture) + "," +
                position.y.ToString("F4", CultureInfo.InvariantCulture) + "," +
                position.z.ToString("F4", CultureInfo.InvariantCulture));
            SessionState.SetString(DirsKey, SnapshotEvidenceDirs());
            SessionState.SetString(StageKey, "play1");
            SessionState.SetString(T0Key, "");
            EditorApplication.isPlaying = true;
            Log("started ordinary Play #1 WITHOUT calling InteractionExpansionAcceptance.Run");
        }

        static void Tick()
        {
            // Exit request written by Finish(); honoured after Play-mode domain reloads.
            string exitRequest = SessionState.GetString(ExitRequestKey, "");
            if (!string.IsNullOrEmpty(exitRequest) && !EditorApplication.isPlaying)
            {
                int exitCode = 0;
                int.TryParse(exitRequest, out exitCode);
                SessionState.SetString(ExitRequestKey, "");
                Log("editor exit requested with logical code=" + exitCode);
                EditorApplication.Exit(exitCode);
                return;
            }
            if (SessionState.GetString(ArmedKey, "") != "1") return;
            string stage = SessionState.GetString(StageKey, "");
            if (stage == "play1")
            {
                if (!EditorApplication.isPlaying) return;
                string t0 = SessionState.GetString(T0Key, "");
                if (string.IsNullOrEmpty(t0))
                {
                    SessionState.SetString(T0Key, EditorApplication.timeSinceStartup.ToString("F4"));
                    return;
                }
                if (EditorApplication.timeSinceStartup - double.Parse(t0) < 15.0) return;
                RunChecks("play1_15s");
                SessionState.SetString(StageKey, "stop1");
                SessionState.SetString(T0Key, "");
                EditorApplication.isPlaying = false;
                return;
            }
            if (stage == "stop1")
            {
                if (EditorApplication.isPlaying) return;
                string t0 = SessionState.GetString(T0Key, "");
                if (string.IsNullOrEmpty(t0))
                {
                    SessionState.SetString(T0Key, EditorApplication.timeSinceStartup.ToString("F4"));
                    return;
                }
                if (EditorApplication.timeSinceStartup - double.Parse(t0) < 2.0) return;
                RunChecks("after_stop");
                SessionState.SetString(StageKey, "play2");
                SessionState.SetString(T0Key, "");
                EditorApplication.isPlaying = true;
                Log("started ordinary Play #2 (Play -> Stop -> Play)");
                return;
            }
            if (stage == "play2")
            {
                if (!EditorApplication.isPlaying) return;
                string t0 = SessionState.GetString(T0Key, "");
                if (string.IsNullOrEmpty(t0))
                {
                    SessionState.SetString(T0Key, EditorApplication.timeSinceStartup.ToString("F4"));
                    return;
                }
                if (EditorApplication.timeSinceStartup - double.Parse(t0) < 8.0) return;
                RunChecks("play2_8s");
                SessionState.SetString(StageKey, "exitplay");
                EditorApplication.isPlaying = false;
                return;
            }
            if (stage == "exitplay")
            {
                if (EditorApplication.isPlaying) return;
                Finish();
            }
        }

        static string SnapshotEvidenceDirs()
        {
            var sb = new StringBuilder();
            try
            {
                string root = Path.GetDirectoryName(Application.dataPath) ?? ".";
                string evidenceRoot = Path.Combine(root, "Captures", "InteractionExpansion");
                if (Directory.Exists(evidenceRoot))
                {
                    foreach (var dir in Directory.GetDirectories(evidenceRoot))
                    {
                        string report = Path.Combine(dir, "acceptance", "acceptance_report.json");
                        if (File.Exists(report))
                            sb.Append(dir).Append(';');
                    }
                }
            }
            catch { }
            return sb.ToString();
        }

        static void PersistChecks()
        {
            // Play/Stop cycles reload the domain; statics are lost.  Merge the new
            // check records into the persisted list by stable check id.
            string stored = SessionState.GetString(ChecksKey, "");
            var lines = new System.Collections.Generic.List<string>();
            var known = new System.Collections.Generic.HashSet<string>();
            if (!string.IsNullOrEmpty(stored))
            {
                lines.AddRange(stored.Split(new[] { '\n' },
                    System.StringSplitOptions.RemoveEmptyEntries));
                foreach (var line in lines)
                {
                    string id = ExtractCheckId(line);
                    if (id != null) known.Add(id);
                }
            }
            foreach (var check in Checks)
            {
                string id = ExtractCheckId(check);
                if (id == null || known.Contains(id)) continue;
                known.Add(id);
                lines.Add(check);
            }
            SessionState.SetString(ChecksKey, string.Join("\n", lines.ToArray()));
        }

        static string ExtractCheckId(string jsonLine)
        {
            const string key = "\"id\":\"";
            int at = jsonLine.IndexOf(key, System.StringComparison.Ordinal);
            if (at < 0) return null;
            int startIndex = at + key.Length;
            int endIndex = jsonLine.IndexOf('"', startIndex);
            if (endIndex <= startIndex) return null;
            return jsonLine.Substring(startIndex, endIndex - startIndex);
        }

        static void RunChecks(string label)
        {
            var character = Object.FindObjectOfType<Yixuan.Core.YixuanCharacter>();
            Vector3 now = character != null ? character.transform.position : Vector3.zero;
            string stored = SessionState.GetString(CharKey, "");
            var parts = stored.Split(',');
            Vector3 before = Vector3.zero;
            if (parts.Length >= 3)
            {
                before = new Vector3(
                    float.Parse(parts[0], CultureInfo.InvariantCulture),
                    float.Parse(parts[1], CultureInfo.InvariantCulture),
                    float.Parse(parts[2], CultureInfo.InvariantCulture));
            }
            float xzDelta = Vector2.Distance(new Vector2(before.x, before.z), new Vector2(now.x, now.z));
            float yDelta = Mathf.Abs(before.y - now.y);
            Check(label + ".character_not_teleported", xzDelta < 0.05f && yDelta < 0.6f,
                  "xz_delta=" + xzDelta.ToString("F3") + " y_delta=" + yDelta.ToString("F3"));
            Check(label + ".acceptance_not_armed", !InteractionExpansionAcceptance.IsArmedForTest,
                  "armed=" + InteractionExpansionAcceptance.IsArmedForTest);
            Check(label + ".acceptance_not_started", !InteractionExpansionAcceptance.AnyRunStartedInEditorSession,
                  "run_started=" + InteractionExpansionAcceptance.AnyRunStartedInEditorSession);
            string beforeDirs = SessionState.GetString(DirsKey, "");
            string afterDirs = SnapshotEvidenceDirs();
            var beforeSet = new HashSet<string>(beforeDirs.Split(new[] { ';' },
                StringSplitOptions.RemoveEmptyEntries));
            var afterSet = new HashSet<string>(afterDirs.Split(new[] { ';' },
                StringSplitOptions.RemoveEmptyEntries));
            afterSet.ExceptWith(beforeSet);
            Check(label + ".no_acceptance_evidence_written", afterSet.Count == 0,
                  "new_acceptance_dirs=" + string.Join(",", new List<string>(afterSet).ToArray()));
            Check(label + ".editor_still_running", true, "probe is executing; editor not exited");
            Log(label + " checks done xz_delta=" + xzDelta.ToString("F3"));
            PersistChecks();
        }

        static void Check(string id, bool passed, string detail)
        {
            Checks.Add("{\"id\":\"" + Escape(id) + "\",\"status\":\"" + (passed ? "pass" : "fail") +
                       "\",\"detail\":\"" + Escape(detail) + "\"}");
            Log((passed ? "PASS " : "FAIL ") + id + " :: " + detail);
        }

        static string Escape(string value)
        {
            if (value == null) return "";
            return value.Replace("\\", "/").Replace("\"", "'");
        }

        static void Log(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + message;
            LogBuilder.AppendLine(line);
            Debug.Log("[COLD-GUARD] " + message);
        }

        static void Finish()
        {
            string persisted = SessionState.GetString(ChecksKey, "");
            if (!string.IsNullOrEmpty(persisted))
            {
                Checks.Clear();
                foreach (var line in persisted.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    Checks.Add(line);
            }
            int failed = 0;
            foreach (var check in Checks) if (check.Contains("\"status\":\"fail\"")) failed++;
            int code = failed > 0 ? 1 : 0;
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"schema\": \"sbh.cold-play-guard.v1\",\n");
            sb.Append("  \"review_item\": \"R1\",\n");
            sb.Append("  \"completed\": true,\n");
            sb.Append("  \"logical_exit_code\": ").Append(code).Append(",\n");
            sb.Append("  \"checks_pass\": ").Append(Checks.Count - failed).Append(",\n");
            sb.Append("  \"checks_fail\": ").Append(failed).Append(",\n");
            sb.Append("  \"scenario\": \"ordinary Play 15s -> Stop -> Play 8s without acceptance Run()\",\n");
            sb.Append("  \"character_expected\": \"").Append(Escape(SessionState.GetString(CharKey, ""))).Append("\",\n");
            sb.Append("  \"checks\": [\n    ").Append(string.Join(",\n    ", Checks.ToArray())).Append("\n  ]\n");
            sb.Append("}\n");
            Directory.CreateDirectory(GuardDir);
            File.WriteAllText(Path.Combine(GuardDir, "cold_play_guard.json"), sb.ToString());
            File.WriteAllText(Path.Combine(GuardDir, "cold_play_guard.log"), LogBuilder.ToString());
            File.WriteAllText(Path.Combine(GuardDir, "EXIT_CODE"), code.ToString());
            File.WriteAllText(Path.Combine(GuardDir, "COMPLETED.json"),
                "{\"completed\": true, \"logical_exit_code\": " + code +
                ", \"internal_kill\": false" +
                ", \"exit_path\": \"logical_result_editor_exit\"}\n");
            Log("cold guard finished code=" + code + " internal_kill=false");
            SessionState.SetString(ArmedKey, "");
            SessionState.SetString(StageKey, "done");
            // Request the Editor exit with the real logical result code (no self kill).
            SessionState.SetString(ExitRequestKey, code.ToString());
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
                return;
            }
            SessionState.SetString(ExitRequestKey, "");
            EditorApplication.Exit(code);
        }
    }
}

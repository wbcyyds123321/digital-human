using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using Yixuan.AI.Tasks;
using Yixuan.Stage2;
using Yixuan.UI;

namespace Yixuan.EditorTools
{
    /// <summary>
    /// Stage-2 acceptance runner (batchmode Play):
    ///   Unity.exe -batchmode -projectPath &lt;proj&gt; -executeMethod Yixuan.EditorTools.Stage2PickAcceptance.Run -logFile Logs/stage2/acceptance.log
    /// Opens the SovietBlueHour scene, enters Play, drives the prototype, records evidence,
    /// exits Play and quits. Never saves the scene.
    /// </summary>
    [InitializeOnLoad]
    public static class Stage2PickAcceptance
    {
        const string StageKey = "S2_ACCEPT_STAGE";
        const string EvidenceKey = "S2_ACCEPT_EVIDENCE";
        const string StartKey = "S2_ACCEPT_T0";
        const string ProbeKey = "S2_ACCEPT_PROBE_FRAMES";
        const string ExitTriesKey = "S2_ACCEPT_EXIT_TRIES";
        const string ExitCodeKey = "S2_ACCEPT_EXIT_CODE";
        const string ScenePath = "Assets/Scenes/SovietBlueHour_TaskPlan.unity";
        const string PackDir = "Assets/Resources/SovietBlueHour/stage2_pick";

        static readonly string[] TrackedFiles =
        {
            "Assets/Scenes/SovietBlueHour_TaskPlan.unity",
            "Assets/Maps/SovietBlueHour/SovietBlueHour_Map.prefab",
            "Maps/SovietBlueHour/map/SovietBlueHour_beauty.blend",
            "Assets/Scripts/Presentation/CameraDirector.cs",
            "Assets/Scripts/Vision/EyeGimbalController.cs",
            "Assets/Scripts/Vision/VisionObserver.cs",
            "Assets/Scripts/Vision/EyeViewPreview.cs",
            "Assets/Scripts/AI/Tasks/TaskTargetCatalog.cs",
            "Assets/Scripts/World/WorldData.cs",
            "Assets/Scripts/World/MapWorldProfile.cs",
            "Assets/Scripts/UI/UnifiedHud.cs",
            "Assets/Scripts/AI/ChatUI.cs",
            "Assets/Resources/SovietBlueHour/stage2_pick/manifest.json",
        };

        static string _projectRoot;
        static List<Stage2CaseRecord> _cases;
        static List<ScreenshotRecord> _shots;
        static string _evidenceDir;
        static string _pickPath;

        static Stage2PickAcceptance()
        {
            EditorApplication.update += Tick;
        }

        // ------------------------------------------------------------------
        // entry
        // ------------------------------------------------------------------
        public static void Run()
        {
            if (EditorApplication.isPlaying)
            {
                UnityEngine.Debug.LogError("[S2-ACCEPT] Stop Play before running acceptance.");
                EditorApplication.Exit(2);
                return;
            }
            _projectRoot = Path.GetDirectoryName(Application.dataPath);
            _evidenceDir = Path.Combine(_projectRoot, "Captures", "Stage2Pick",
                DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(_evidenceDir);
            SessionState.SetString(EvidenceKey, _evidenceDir);
            SessionState.SetString(StageKey, "play_wait");
            SessionState.SetString(StartKey, "");
            SessionState.SetInt(ExitTriesKey, 0);
            SessionState.SetInt(ExitCodeKey, 1);   // fail closed until a summary says otherwise
            SessionState.SetString("S2_ACCEPT_COMPLETED", "0");
            SessionState.SetBool("S2_ACCEPT_INJECT_EXCEPTION", false);
            WriteRunHeader();
            UnityEngine.Debug.Log("[S2-ACCEPT] evidence=" + _evidenceDir);
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            EditorApplication.isPlaying = true;
        }

        /// <summary>
        /// Intentional-failure entry: proves that a failing case produces a rejecting
        /// report and a non-zero process exit. Never opens the scene or changes data.
        /// </summary>
        public static void RunFailureInjection()
        {
            _projectRoot = Path.GetDirectoryName(Application.dataPath);
            _evidenceDir = Path.Combine(_projectRoot, "Captures", "Stage2Pick",
                "failure_injection_" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(_evidenceDir);
            _cases = new List<Stage2CaseRecord>
            {
                new Stage2CaseRecord
                {
                    id = "intentional_failure", category = "injection", status = "fail",
                    detail = "deliberate failure to verify the reporter and exit code reject it",
                    expected = "pass", actual = "intentional fail", evidence = _evidenceDir,
                }
            };
            _shots = new List<ScreenshotRecord>();
            WriteSummary(true);
            int code = SessionState.GetInt(ExitCodeKey, 1);
            UnityEngine.Debug.Log("[S2-ACCEPT] failure injection exit code " + code);
            StartExitWatchdog();
            EditorApplication.Exit(code);
        }

        public static void RunIncompleteInjection()
        {
            Run();
            SessionState.SetBool("S2_ACCEPT_INJECT_EXCEPTION", true);
        }

        static void Tick()
        {
            string stage = SessionState.GetString(StageKey, "");
            if (stage == "play_wait")
            {
                if (!EditorApplication.isPlaying) return;
                EnsureEvidence();
                string t0 = SessionState.GetString(StartKey, "");
                if (string.IsNullOrEmpty(t0))
                {
                    SessionState.SetString(StartKey, Time.realtimeSinceStartup.ToString("F3"));
                    return;
                }
                float elapsed = Time.realtimeSinceStartup - float.Parse(t0);
                if (Time.frameCount < 20) return;
                var prototype = Stage2PickPrototype.Instance;
                if (prototype == null)
                {
                    if (elapsed > 45f)
                    {
                        WriteBootFailure("prototype host did not appear within 45s");
                        FinishPlay(false);
                    }
                    return;
                }
                try
                {
                    if (SessionState.GetBool("S2_ACCEPT_INJECT_EXCEPTION", false))
                    {
                        SessionState.SetBool("S2_ACCEPT_INJECT_EXCEPTION", false);
                        throw new InvalidOperationException("intentional Play exception for incomplete-run acceptance");
                    }
                    if (!RunAllTests(prototype))
                    {
                        FinishPlay(false);
                        return;
                    }
                    SessionState.SetInt(ProbeKey, 0);
                    SessionState.SetString(StageKey, "probe");
                }
                catch (Exception e)
                {
                    try { WriteBootFailure("acceptance exception: " + e); } catch { }
                    FinishPlay(false);
                }
                return;
            }
            if (stage == "probe")
            {
                if (!EditorApplication.isPlaying) return;
                EnsureEvidence();
                int probeFrames = SessionState.GetInt(ProbeKey, 0) + 1;
                SessionState.SetInt(ProbeKey, probeFrames);
                var prototype = Stage2PickPrototype.Instance;
                var probe = UnityEngine.Object.FindObjectOfType<Stage2FocusProbe>();
                if (probe == null)
                {
                    var go = new GameObject("Stage2FocusProbe");
                    probe = go.AddComponent<Stage2FocusProbe>();
                }
                if (probeFrames < 5) return;
                bool hasFocus = probe != null && probe.HasImGuiKeyboardControl();
                bool guardSeesFocus = prototype != null && prototype.IsConfirmBlocked();
                AddCase("imgui_focus_real_probe", "input",
                    hasFocus && guardSeesFocus ? "pass" : "pending",
                    "real IMGUI TextField focus (Stage2FocusProbe) -- probe reports keyboardControl, guard sees it",
                    "keyboardControl != 0 and guard true",
                    "probeFocus=" + hasFocus + " guard=" + guardSeesFocus, "");
                if (probe != null) UnityEngine.Object.Destroy(probe.gameObject);
                RunPointerRoutingTests(prototype);
                WriteSummary(true);
                FinishPlay(true);
                return;
            }
            if (stage == "wrap")
            {
                if (EditorApplication.isPlaying) return;
                WritePostRunChecks();
                SessionState.SetString(StageKey, "exit");
                return;
            }
            if (stage == "exit")
            {
                int tries = SessionState.GetInt(ExitTriesKey, 0) + 1;
                SessionState.SetInt(ExitTriesKey, tries);
                int exitCode = SessionState.GetInt(ExitCodeKey, 1);
                if (tries != 1) return;
                StartExitWatchdog();
                UnityEngine.Debug.Log("[S2-ACCEPT] exit code " + exitCode);
                EditorApplication.Exit(exitCode);
            }
        }

        static void StartExitWatchdog()
        {
            // batchmode can hang while shutting down after Play has exited and evidence is written.
            try
            {
                string evidenceDir = _evidenceDir;
                int logicalExitCode = SessionState.GetInt(ExitCodeKey, 1);
                var thread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        System.Threading.Thread.Sleep(30000);
                        File.WriteAllText(Path.Combine(evidenceDir, "exit_watchdog.json"),
                            "{\"forced\":true,\"process_exit_code\":1,\"logical_exit_code\":" + logicalExitCode +
                            ",\"reason\":\"Unity shutdown exceeded 30 seconds\"}", Encoding.UTF8);
#if UNITY_EDITOR_WIN
                        TerminateProcess(System.Diagnostics.Process.GetCurrentProcess().Handle, 1);
#else
                        System.Diagnostics.Process.GetCurrentProcess().Kill();
#endif
                    }
                    catch { }
                });
                thread.IsBackground = true;
                thread.Start();
            }
            catch { }
        }

#if UNITY_EDITOR_WIN
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool TerminateProcess(IntPtr processHandle, uint exitCode);
#endif

        static void EnsureEvidence()
        {
            if (!string.IsNullOrEmpty(_evidenceDir) && Directory.Exists(_evidenceDir)) return;
            _projectRoot = Path.GetDirectoryName(Application.dataPath);
            _evidenceDir = SessionState.GetString(EvidenceKey, "");
            if (string.IsNullOrEmpty(_evidenceDir))
            {
                _evidenceDir = Path.Combine(_projectRoot, "Captures", "Stage2Pick",
                    DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_fallback");
            }
            Directory.CreateDirectory(_evidenceDir);
            SessionState.SetString(EvidenceKey, _evidenceDir);
        }

        static void FinishPlay(bool testsRan)
        {
            SessionState.SetString("S2_ACCEPT_COMPLETED", testsRan ? "1" : "0");
            try
            {
                var p = Stage2PickPrototype.Instance;
                if (p != null) p.Disable();
                EditorApplication.isPlaying = false;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[S2-ACCEPT] stop play failed: " + e.Message);
            }
            SessionState.SetString(StageKey, "wrap");
        }

        // ------------------------------------------------------------------
        // run header / hashes
        // ------------------------------------------------------------------
        static void WriteRunHeader()
        {
            var header = new RunHeader
            {
                created_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                scene = ScenePath,
                unity = Application.unityVersion,
                evidence_dir = _evidenceDir,
                files = HashTrackedFiles(),
            };
            File.WriteAllText(Path.Combine(_evidenceDir, "run_header.json"),
                JsonUtility.ToJson(header, true), Encoding.UTF8);
            string baseline = Path.Combine(_projectRoot, "Logs", "stage2", "baseline_before.json");
            if (File.Exists(baseline))
                File.Copy(baseline, Path.Combine(_evidenceDir, "baseline_before.json"), true);
        }

        static FileHash[] HashTrackedFiles()
        {
            var list = new List<FileHash>();
            foreach (var rel in TrackedFiles)
            {
                string full = Path.Combine(_projectRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) continue;
                byte[] bytes = File.ReadAllBytes(full);
                list.Add(new FileHash
                {
                    path = rel,
                    bytes = bytes.Length,
                    sha256 = Stage2PickDataLoader.Sha256(bytes),
                });
            }
            return list.ToArray();
        }

        // ------------------------------------------------------------------
        // tests
        // ------------------------------------------------------------------
        static bool RunAllTests(Stage2PickPrototype p)
        {
            EnsureEvidence();
            _cases = new List<Stage2CaseRecord>();
            _shots = new List<ScreenshotRecord>();
            _pickPath = Path.Combine(_evidenceDir, "pick.jsonl");
            File.WriteAllText(_pickPath, "", Encoding.UTF8);
            bool reporterRejects = ComputeExitCode(false, 0, 0) == 1 && ComputeExitCode(true, 1, 0) == 1 &&
                ComputeExitCode(true, 0, 1) == 2 && ComputeExitCode(true, 0, 0) == 0;
            AddCase("reporter_exit_matrix", "reporting", reporterRejects ? "pass" : "fail",
                "incomplete, failed, pending and accepted runs have distinct exit semantics",
                "1/1/2/0", reporterRejects ? "1/1/2/0" : "mismatch", "");

            bool defaultOff = !p.Enabled && !p.HasRuntimeResources;
            p.Toggle();
            bool toggledOn = p.Enabled && p.HasRuntimeResources;
            p.Toggle();
            bool toggledOff = !p.Enabled && !p.HasRuntimeResources;
            AddCase("default_off_and_toggle", "ui",
                defaultOff && toggledOn && toggledOff ? "pass" : "fail",
                "prototype starts disabled; Toggle (the F12 path) enables and disables it cleanly",
                "off/on/off", "off=" + defaultOff + " on=" + toggledOn + " off2=" + toggledOff, "");
            p.Enable();
            if (!p.DataReady)
            {
                AddCase("data_pack_ready", "data", "fail", "data error: " + p.DataError, "valid pack", p.DataError, "");
                WriteSummary(false);
                return false;
            }
            AddCase("data_pack_ready", "data", "pass", "prototype loaded and validated the pack", "valid", "valid", _evidenceDir);
            string normalSessionDir = p.EvidenceDir;
            bool normalSessionOk = !string.IsNullOrEmpty(normalSessionDir) && Directory.Exists(normalSessionDir);
            AddCase("normal_session_init", "confirm", normalSessionOk ? "pass" : "fail",
                "normal Enable path creates its own session directory without the QA setting EvidenceDir",
                "non-empty session dir", normalSessionDir, "");
            bool chatForBlocker = GameObject.Find("UI_Chat") != null;
            p.DebugSetPreviewPointerState(true);
            bool blockerOn = p.UiBlockerActive && p.PointerInsidePreview;
            p.DebugSetPreviewPointerState(false);
            bool blockerOff = p.UiBlockerActive && !p.PointerInsidePreview;
            AddCase("preview_ui_toolkit_blocker", "input",
                chatForBlocker && blockerOn && blockerOff ? "pass" : "fail",
                "preview blocker remains restricted to its rectangle instead of covering the full UI root",
                "blocker present / pointer state tracked",
                "chat=" + chatForBlocker + " on=" + blockerOn + " off=" + blockerOff, "");
            AddCase("preview_pointer_actual", "input", "pending",
                "batchmode cannot synthesize an actual HUD click/drag; UI Toolkit blocker and IMGUI ConsumePreviewPointerEvent code are present, desktop interaction still requires manual check",
                "real HUD click does not fire under preview", "code + structural blocker only", "");

            var loaderData = Stage2PickDataLoader.Load();
            var manifest = loaderData.Manifest;
            AddCase("data_loader_revalidation", "data", loaderData.Valid ? "pass" : "fail",
                loaderData.Valid ? "second load accepted manifest + proxies + resource hashes" : loaderData.Error,
                "valid", loaderData.Valid ? "valid" : loaderData.Error, _evidenceDir);
            if (loaderData.Valid)
            {
                RunMathTests(loaderData);
                RunAllTargetCases(p, manifest);
            }

            // snapshots of the untouched systems before the prototype does anything visible
            var invariants = CaptureInvariants();
            var actorGo = GameObject.Find("Yixuan_Test");
            Vector3 characterBefore = actorGo != null ? actorGo.transform.position : Vector3.zero;
            var mainBefore = Camera.main != null ? Camera.main.transform : null;
            Vector3 mainPosBefore = mainBefore != null ? mainBefore.position : Vector3.zero;
            Quaternion mainRotBefore = mainBefore != null ? mainBefore.rotation : Quaternion.identity;
            var eyeGo = GameObject.Find("EyeCamera");
            var eyeCam = eyeGo != null ? eyeGo.GetComponent<Camera>() : null;
            Vector3 eyePosBefore = eyeCam != null ? eyeCam.transform.position : Vector3.zero;
            Quaternion eyeRotBefore = eyeCam != null ? eyeCam.transform.rotation : Quaternion.identity;
            bool eyeEnabledBefore = eyeCam != null && eyeCam.enabled;
            RenderTexture eyeTargetBefore = eyeCam != null ? eyeCam.targetTexture : null;

            Stage2Pose p1 = FindPose(manifest, "P1_bench_test_view");
            Stage2Pose p2 = FindPose(manifest, "P2_bus_test_view");
            Stage2Pose p3 = FindPose(manifest, "P3_arch_test_view");

            // ---- P0: follow eye / actual gaze, no visibility assumption ----
            p.SetFollowEye();
            var p0 = p.RenderNow();
            bool eyeExists = GameObject.Find("EyeCamera") != null;
            if (!eyeExists)
            {
                AddCase("p0_follow_eye", "camera", "pending",
                    "no EyeCamera in this scene; character_eye cannot be rendered; test_view paths still verified",
                    "character_eye snapshot", "unavailable", "");
            }
            else
            {
                bool ok = p.LastSnapshot != null && p.LastSnapshot.Source == "character_eye";
                AddCase("p0_follow_eye", "camera", ok ? "pass" : "fail",
                    ok ? "follow eye snapshot source is character_eye" : "unexpected snapshot source",
                    "character_eye", p.LastSnapshot == null ? "<null>" : p.LastSnapshot.Source, "");
                if (p0 != null) LogPick("P0_character_eye", p0);
                Capture("P0_character_eye", "P0 follow-eye scene frame");
            }

            // ---- mode switching clears stale hover ----
            p.SetTestPose("P1_bench_test_view");
            bool clearedOnPose = p.LastPick == null;
            p.RenderNow();
            bool sourceP1 = p.LastSnapshot != null && p.LastSnapshot.Source == "test_view" &&
                            p.LastSnapshot.PoseId == "P1_bench_test_view";
            AddCase("switch_pose_clears_hover", "stale", clearedOnPose && sourceP1 ? "pass" : "fail",
                "LastPick cleared before render; next snapshot is test_view/P1",
                "cleared + test_view:P1", "cleared=" + clearedOnPose + " source=" + (p.LastSnapshot == null ? "<null>" : p.LastSnapshot.Source + ":" + p.LastSnapshot.PoseId), "");
            p.SetFollowEye();
            bool clearedOnFollow = p.LastPick == null;
            p.RenderNow();
            AddCase("switch_back_to_eye_clears", "stale", clearedOnFollow ? "pass" : "fail",
                "SetFollowEye clears old hover immediately", "cleared", "cleared=" + clearedOnFollow, "");

            // ---- P1 benches / empty / bbox gap / blocked ----
            p.SetTestPose("P1_bench_test_view");
            p.RenderNow();

            // ---- normal-use path: no QA EvidenceDir yet; F-confirm must still
            // initialize a session directory, write confirm.jsonl, and persist the
            // complete snapshot + pick record. ----
            var normalBench = FindTarget(p1, "bench", "entity/prop_bench_plaza_1");
            if (normalBench != null)
            {
                p.SetManualTestView(ToVector(p1.eye_position_unity), ToVector(normalBench.point), "P1_normal_use");
                p.RenderNow();
                bool normalConfirmed = p.TryConfirm();
                bool normalLog = File.Exists(Path.Combine(normalSessionDir ?? "", "confirm.jsonl"));
                bool normalImage = p.LastConfirm != null && File.Exists(p.LastConfirm.image);
                bool normalFull = p.LastConfirm != null && p.LastConfirm.snapshot != null &&
                                  p.LastConfirm.pick != null && p.LastConfirm.pick.min_distance >= 0f &&
                                  p.LastConfirm.snapshot.projection.m00 != 0f;
                string normalCopy = "";
                if (normalConfirmed && normalLog)
                {
                    string normalDir = Path.Combine(_evidenceDir, "normal_use");
                    Directory.CreateDirectory(normalDir);
                    File.Copy(Path.Combine(normalSessionDir, "confirm.jsonl"),
                        Path.Combine(normalDir, "confirm.jsonl"), true);
                    if (p.LastConfirm != null && File.Exists(p.LastConfirm.image))
                        File.Copy(p.LastConfirm.image, Path.Combine(normalDir, Path.GetFileName(p.LastConfirm.image)), true);
                    normalCopy = normalDir;
                }
                AddCase("normal_use_confirm_log", "confirm",
                    normalConfirmed && normalLog && normalImage && normalFull ? "pass" : "fail",
                    "F-confirm in normal use persists image + confirm.jsonl + full snapshot/pick without QA pre-setting EvidenceDir",
                    "image + confirm.jsonl + snapshot + pick",
                    "confirmed=" + normalConfirmed + " log=" + normalLog + " image=" + normalImage + " full=" + normalFull,
                    normalCopy);
                p.EvidenceDir = _evidenceDir;
            }
            else
            {
                p.EvidenceDir = _evidenceDir;
                AddCase("normal_use_confirm_log", "confirm", "fail",
                    "no P1 bench target for normal-use confirm", "hit target", "missing", "");
            }

            int benchHit = 0;
            foreach (var t in Targets(p1))
            {
                if (t == null) continue;
                if (t.kind == "bench")
                {
                    var r = PickTarget(p, p1, t);
                    LogPick("P1_bench", r);
                    bool ok = r.Status == Stage2PickStatus.Hit && r.SampleId == t.sample_id &&
                              (t.point == null || Vector3.Distance(r.HitPoint, ToVector(t.point)) < 0.05f);
                    if (ok) benchHit++;
                    AddCase("bench_" + t.sample_id, "benches", ok ? "pass" : "fail",
                        "bench target returns its own entity id", t.sample_id, r.Status + ":" + r.SampleId, _pickPath);
                }
                else if (t.kind == "empty" || t.kind == "outside")
                {
                    var r = PickTarget(p, p1, t);
                    LogPick("P1_" + t.kind, r);
                    bool ok = r.Status == Stage2PickStatus.NoTarget;
                    AddCase("p1_" + t.kind, "empty", ok ? "pass" : "fail",
                        "empty target has no selectable hit", "no_target", r.Status.ToString(), _pickPath);
                }
                else if (t.kind == "bbox_gap")
                {
                    var r = PickTarget(p, p1, t);
                    LogPick("P1_bbox_gap", r);
                    bool aabbHit = false;
                    Stage2SampleMesh mesh;
                    if (loaderData.Meshes.TryGetValue(t.sample_id ?? "", out mesh))
                    {
                        Vector3 origin = ToVector(p1.eye_position_unity);
                        Vector3 dir = t.direction != null ? ToVector(t.direction) : (ToVector(t.point) - origin).normalized;
                        float te, tx;
                        aabbHit = Stage2PickMath.RayAabb(origin, dir, mesh.Bounds.min, mesh.Bounds.max, out te, out tx);
                    }
                    bool ok = r.Status == Stage2PickStatus.NoTarget && aabbHit;
                    AddCase("p1_bbox_hit_triangle_miss", "bbox", ok ? "pass" : "fail",
                        "ray crosses sample AABB but no triangle; result is no_target",
                        "no_target + aabb hit", r.Status + " aabb=" + aabbHit, _pickPath);
                }
                else if (t.kind == "blocked")
                {
                    var r = PickTarget(p, p1, t);
                    LogPick("P1_blocked", r);
                    bool ok = r.Status == Stage2PickStatus.Blocked && !r.HasSelectableHit &&
                              string.IsNullOrEmpty(r.SampleId) && !string.IsNullOrEmpty(r.OccluderId) &&
                              (t.occluder_id == null || r.OccluderId == t.occluder_id);
                    if (ok)
                    {
                        AddCase("real_foreground_occlusion", "occlusion", "pass",
                            "foreground occluder blocks the sample; candidate is not selectable", t.occluder_id,
                            r.OccluderId + "/" + r.OccluderMethod, _pickPath);
                    }
                    else
                    {
                        AddCase("real_foreground_occlusion", "occlusion", "fail",
                            "expected blocked", t.occluder_id, r.Status + ":" + r.OccluderId, _pickPath);
                    }
                    break;
                }
            }
            AddCase("bench_pair_distinct", "benches", benchHit >= 2 ? "pass" : "fail",
                "two identical plaza benches returned their own ids", "2", benchHit.ToString(), _pickPath);
            bool weedsInOccluders = false;
            foreach (var occ in loaderData.Occluders)
            {
                string oid = (occ.Id ?? "").ToLowerInvariant();
                string ocl = (occ.ClassOrKind ?? "").ToLowerInvariant();
                if (oid.Contains("weed") || oid.Contains("litter") || oid.Contains("leaf_pile") ||
                    ocl.Contains("weed") || ocl.Contains("litter") || ocl.Contains("leaf_pile"))
                {
                    weedsInOccluders = true;
                    break;
                }
            }
            AddCase("weeds_not_occluders", "occlusion", weedsInOccluders ? "fail" : "pass",
                "merged scatter classes are not in the curated occluder set; bench rays stay clear",
                "no weed/litter occluders", weedsInOccluders ? "found" : "none", _pickPath);

            // P1 screenshot aimed at bench 1
            if (p1 != null)
            {
                var benchTarget = FindTarget(p1, "bench", "entity/prop_bench_plaza_1");
                if (benchTarget != null)
                {
                    p.SetManualTestView(ToVector(p1.eye_position_unity), ToVector(benchTarget.point), "P1_bench");
                    p.RenderNow();
                    Capture("P1_bench_test_view", "aim bench plaza_1 from P1 test eye");
                }
            }

            // ---- P2 bus parts / outside ----
            p.SetTestPose("P2_bus_test_view");
            p.RenderNow();
            int wheel = 0, window = 0, body = 0;
            foreach (var t in Targets(p2))
            {
                if (t == null || t.kind != "bus_part") continue;
                var r = PickTarget(p, p2, t);
                LogPick("P2_bus_" + (t.part_class ?? "part"), r);
                bool ok = r.Status == Stage2PickStatus.Hit && r.SampleId == "entity/veh_laz695_01" &&
                          (string.IsNullOrEmpty(t.part_label) || r.PartLabel == t.part_label);
                if (ok)
                {
                    if (t.part_class == "wheel") wheel++;
                    else if (t.part_class == "window") window++;
                    else if (t.part_class == "body") body++;
                }
                AddCase("bus_part_" + (t.part_class ?? "?") + "_" + (t.part_label ?? ""), "bus",
                    ok ? "pass" : "fail",
                    "first real hit triangle belongs to the claimed bus part", t.part_label,
                    r.Status + ":" + r.SampleId + ":" + r.PartLabel, _pickPath);
            }
            AddCase("bus_parts_three_categories", "bus",
                wheel > 0 && window > 0 && body > 0 ? "pass" : "fail",
                "wheel/window/body each have at least one exact first-hit case",
                "wheel>0 window>0 body>0", "wheel=" + wheel + " window=" + window + " body=" + body, _pickPath);
            foreach (var t in Targets(p2))
            {
                if (t == null) continue;
                if (t.kind == "outside" || t.kind == "bbox_gap" || t.kind == "empty")
                {
                    var r = PickTarget(p, p2, t);
                    LogPick("P2_" + t.kind, r);
                    AddCase("p2_" + t.kind, "bus", r.Status == Stage2PickStatus.NoTarget ? "pass" : "fail",
                        "outside/empty target has no selectable hit", "no_target", r.Status.ToString(), _pickPath);
                }
            }
            var wheelTarget = FindTarget(p2, "bus_part", "entity/veh_laz695_01", "wheel");
            if (wheelTarget != null)
            {
                p.SetManualTestView(ToVector(p2.eye_position_unity), ToVector(wheelTarget.point), "P2_bus");
                p.RenderNow();
                Capture("P2_bus_test_view", "aim bus wheel from P2 test eye");
            }

            // ---- P3 lintel / gap ----
            p.SetTestPose("P3_arch_test_view");
            p.RenderNow();
            foreach (var t in Targets(p3))
            {
                if (t == null) continue;
                var r = PickTarget(p, p3, t);
                LogPick("P3_" + t.kind, r);
                if (t.kind == "lintel")
                {
                    bool ok = r.Status == Stage2PickStatus.Hit && r.SampleId == "entity/bld_w_k3";
                    AddCase("arch_lintel_hit", "arch", ok ? "pass" : "fail",
                        "lintel ray hits the building through real triangles", "bld_w_k3",
                        r.Status + ":" + r.SampleId, _pickPath);
                }
                else if (t.kind == "gap")
                {
                    bool ok = r.Status == Stage2PickStatus.NoTarget;
                    AddCase("arch_doorway_pass", "arch", ok ? "pass" : "fail",
                        "ray passes through the doorway without hitting the building", "no_target",
                        r.Status + ":" + r.SampleId, _pickPath);
                }
            }
            var lintelTarget = FindTarget(p3, "lintel", "entity/bld_w_k3");
            if (lintelTarget != null)
            {
                p.SetManualTestView(ToVector(p3.eye_position_unity), ToVector(lintelTarget.point), "P3_lintel");
                p.RenderNow();
                Capture("P3_arch_lintel", "aim arch lintel from P3 test eye");
            }

            // ---- resize / preview rect / no stale ----
            Rect rect = p.GetPreviewRect();
            bool insideCenter = p.IsPointerInsidePreview(new Vector2(rect.center.x, rect.center.y));
            bool outside = p.IsPointerInsidePreview(new Vector2(-10f, -10f));
            bool crosshairCentered = Vector2.Distance(p.GetCrosshairGuiPosition(), new Vector2(rect.center.x, rect.center.y)) < 0.1f;
            AddCase("preview_rect_and_crosshair", "ui",
                insideCenter && !outside && crosshairCentered ? "pass" : "fail",
                "crosshair is the rendered rect center; outside points rejected",
                "center in / outside out", "inside=" + insideCenter + " outside=" + outside + " centered=" + crosshairCentered, "");
            p.SetTestPose("P1_bench_test_view");
            p.RenderNow();
            p.SetPreviewSize(800, 450);
            bool clearedOnResize = p.LastPick == null;
            p.RenderNow();
            bool sizeOk = p.LastSnapshot != null && p.LastSnapshot.RtWidth == 800 && p.LastSnapshot.RtHeight == 450;
            AddCase("resize_clears_and_updates", "ui", clearedOnResize && sizeOk ? "pass" : "fail",
                "resize clears stale hover and new snapshot has the new RT size", "800x450",
                (p.LastSnapshot == null ? "<null>" : p.LastSnapshot.RtWidth + "x" + p.LastSnapshot.RtHeight), "");
            p.SetPreviewSize(Stage2PickPrototype.DefaultWidth, Stage2PickPrototype.DefaultHeight);
            p.RenderNow();

            // ---- manual look switches to test_view ----
            p.SetFollowEye();
            p.RenderNow();
            p.ApplyManualLook(8f, 3f);
            bool manualCleared = p.LastPick == null;
            bool manualSource = p.CurrentSource == "test_view";
            p.RenderNow();
            AddCase("manual_look_is_test_view", "camera", manualCleared && manualSource ? "pass" : "fail",
                "manual look clears hover and marks test_view", "test_view",
                (p.LastSnapshot == null ? "<null>" : p.LastSnapshot.Source + ":" + p.LastSnapshot.PoseId), "");
            p.SetFollowEye();
            p.RenderNow();

            // ---- missing EyeCamera must not reuse stale result ----
            p.DebugSetEyeCamera(null);
            p.SetFollowEye();
            var missing = p.RenderNow();
            bool missingOk = missing != null && missing.Status == Stage2PickStatus.Unavailable && p.LastSnapshot == null;
            AddCase("missing_eye_camera_no_stale", "camera", missingOk ? "pass" : "fail",
                "missing EyeCamera yields unavailable and clears snapshot", "unavailable + null snapshot",
                (missing == null ? "<null>" : missing.Status.ToString()) + " snapshot=" + (p.LastSnapshot == null ? "null" : "set"), "");
            p.DebugClearEyeCameraOverride();
            p.RenderNow();

            // ---- confirm + input guards ----
            p.SetTestPose("P1_bench_test_view");
            p.RenderNow();
            var benchTarget2 = FindTarget(p1, "bench", "entity/prop_bench_plaza_1");
            if (benchTarget2 != null)
            {
                p.SetManualTestView(ToVector(p1.eye_position_unity), ToVector(benchTarget2.point), "P1_confirm");
                p.RenderNow();
                bool confirmed = p.TryConfirm();
                bool confirmFile = p.LastConfirm != null && File.Exists(p.LastConfirm.image);
                AddCase("confirm_hit_captures_frame", "confirm",
                    confirmed && confirmFile && p.LastConfirm != null && p.LastConfirm.image_sha256 != null &&
                    p.LastConfirm.image_sha256.Length == 64 ? "pass" : "fail",
                    "F-confirm stores the same-frame image + sha", "image + sha",
                    confirmed + " file=" + confirmFile, p.LastConfirm != null ? p.LastConfirm.image : "");
                bool sameConfirmFrame = p.LastConfirm != null && p.LastSnapshot != null &&
                                        p.LastConfirm.frame_id == p.LastSnapshot.FrameId &&
                                        p.LastConfirm.snapshot_id == p.LastSnapshot.SnapshotId;
                AddCase("confirm_frame_matches_snapshot", "confirm", sameConfirmFrame ? "pass" : "fail",
                    "confirmation record and image use the same frozen snapshot",
                    "same frame_id/snapshot_id",
                    p.LastConfirm != null ? p.LastConfirm.frame_id + "/" + p.LastConfirm.snapshot_id : "<null>", "");
                bool fullQaRecord = p.LastConfirm != null && p.LastConfirm.snapshot != null &&
                                    p.LastConfirm.pick != null && p.LastConfirm.snapshot.projection.m00 != 0f;
                AddCase("confirm_record_full_snapshot_pick", "confirm", fullQaRecord ? "pass" : "fail",
                    "QA confirmation also persists the full camera snapshot (projection) and pick",
                    "snapshot+pick", fullQaRecord ? "full" : "missing", "");

                bool uiFocusTested = false;
                var chatGo = GameObject.Find("UI_Chat");
                if (chatGo != null)
                {
                    var doc = chatGo.GetComponent<UIDocument>();
                    if (doc != null && doc.rootVisualElement != null)
                    {
                        var field = doc.rootVisualElement.Query<TextField>().First();
                        if (field != null)
                        {
                            field.Focus();
                            bool blocked = p.IsConfirmBlocked();
                            bool tryFalse = !p.TryConfirm();
                            field.Blur();
                            bool unblocked = !p.IsConfirmBlocked();
                            uiFocusTested = true;
                            AddCase("uitype_focus_blocks_confirm", "input",
                                blocked && tryFalse && unblocked ? "pass" : "fail",
                                "UI Toolkit TextField focus blocks F confirm; blur releases it",
                                "blocked while focused", "blocked=" + blocked + " try=" + tryFalse + " released=" + unblocked, "");
                        }
                    }
                }
                if (!uiFocusTested)
                    AddCase("uitype_focus_blocks_confirm", "input", "pending",
                        "no UI_Chat TextField found; UI Toolkit focus guard not exercised", "real focus", "not found", "");
            }
            else
            {
                AddCase("confirm_hit_captures_frame", "confirm", "fail", "no bench target for confirm", "hit", "missing", "");
            }

            // IMGUI keyboardControl guard (same state variable the guard reads)
            bool imguiset = TrySetImGuiKeyboardControl(12345);
            bool imguiblocked = p.IsConfirmBlocked();
            TrySetImGuiKeyboardControl(0);
            AddCase("imgui_focus_blocks_confirm", "input",
                imguiset && imguiblocked ? "pass" : "pending",
                imguiset ? "GUIUtility.keyboardControl state blocks confirm; reset to 0"
                         : "could not set GUIUtility.keyboardControl in this environment",
                "blocked", "set=" + imguiset + " blocked=" + imguiblocked, "");

            // ---- invariants + performance ----
            bool eyeUnchanged = true;
            if (eyeCam != null)
            {
                eyeUnchanged = Vector3.Distance(eyePosBefore, eyeCam.transform.position) < 1e-5f &&
                               Quaternion.Angle(eyeRotBefore, eyeCam.transform.rotation) < 1e-3f &&
                               eyeEnabledBefore == eyeCam.enabled &&
                               eyeTargetBefore == eyeCam.targetTexture;
            }
            bool mainUnchanged = true;
            if (mainBefore != null)
            {
                mainUnchanged = Vector3.Distance(mainPosBefore, mainBefore.position) < 1e-5f &&
                                Quaternion.Angle(mainRotBefore, mainBefore.rotation) < 1e-3f;
            }
            var invariantsAfter = CaptureInvariants();
            bool invariantsSame = invariantsAfter.colliders == invariants.colliders &&
                                  invariantsAfter.markers == invariants.markers &&
                                  invariantsAfter.hud == invariants.hud &&
                                  invariantsAfter.chat == invariants.chat;
            AddCase("untouched_systems", "isolation",
                eyeUnchanged && mainUnchanged && invariantsSame ? "pass" : "fail",
                "main camera / EyeCamera / colliders / HUD / chat unchanged",
                "unchanged", "eye=" + eyeUnchanged + " main=" + mainUnchanged + " invariants=" + invariantsSame, "");

            bool characterStill = true;
            if (actorGo != null)
                characterStill = Vector3.Distance(characterBefore, actorGo.transform.position) < 0.1f;
            AddCase("confirm_does_not_move_character", "isolation", characterStill ? "pass" : "fail",
                "F/confirm path never drives CharacterLocomotion or walking", "character position unchanged",
                "distance=" + (actorGo != null ? Vector3.Distance(characterBefore, actorGo.transform.position).ToString("F4") : "n/a"), "");

            var perf = MeasurePerformance(p, 30);
            AddCase("preview_performance", "perf", perf.avg_ms < 100f ? "pass" : "fail",
                "average render+pick time at stable size", "<100ms/frame", perf.avg_ms.ToString("F2") + "ms", "");

            // ---- cleanup / re-enable ----
            p.Disable();
            bool cleaned = !p.Enabled && !p.HasRuntimeResources && p.LastPick == null && p.LastSnapshot == null &&
                           !p.UiBlockerActive;
            bool disabledNoRender = p.RenderNow() == null;
            p.Enable();
            bool reenabled = p.Enabled && p.HasRuntimeResources;
            int hostCount = UnityEngine.Object.FindObjectsOfType<Stage2PickPrototype>().Length;
            p.RenderNow();
            p.Disable();
            bool cleaned2 = !p.Enabled && !p.HasRuntimeResources;
            AddCase("cleanup_and_reenable", "cleanup",
                cleaned && disabledNoRender && reenabled && hostCount == 1 && cleaned2 ? "pass" : "fail",
                "disable cleans RT/camera, no render while off, re-enable reuses one host, second disable is clean",
                "clean/no-render/recreate/single-host/clean",
                "clean=" + cleaned + " noRender=" + disabledNoRender + " reenabled=" + reenabled +
                " hosts=" + hostCount + " clean2=" + cleaned2, "");

            AddCase("right_scroll_reserved", "input", "pending",
                "physical right-mouse / scroll and CameraDirector behavior require a desktop check",
                "CameraDirector still owns right/scroll", "code audit only; no synthetic event", "");
            AddCase("hud_click", "ui", "pending",
                "batchmode cannot click the IMGUI HUD; HUD/ChatUI presence and UI-Toolkit focus guard were verified",
                "HUD clickable", "presence + focus only", "");
            AddCase("mouse_drag_synthetic", "input", "pending",
                "batchmode cannot synthesize a pointer drag; preview rect and ApplyManualLook mode transition were tested directly",
                "drag inside preview only", "rect + API test only", "");
            AddCase("physical_keyboard_shortcuts", "input", "pending",
                "F12/1/2/3/R/F APIs were exercised; physical keyboard delivery requires a desktop check",
                "physical keys reach the intended handlers", "API coverage only", "");
            AddCase("visual_runtime_overlay", "visual", "pending",
                "runtime overlay and crosshair need visual inspection; RT numerical checks do not establish appearance",
                "readable overlay and aligned crosshair", "vision API quota exhausted; desktop bridge unavailable", _evidenceDir);
            p.Enable();
            UnityEngine.Debug.Log("[S2-ACCEPT] tests done; cases=" + _cases.Count);
            return true;
        }

        static void RunPointerRoutingTests(Stage2PickPrototype p)
        {
            var handler = typeof(Stage2PickPrototype).GetMethod("HandlePreviewPointer", BindingFlags.Instance | BindingFlags.NonPublic);
            if (p == null || handler == null)
            {
                AddCase("pointer_handler", "input", "fail", "pointer handler missing", "handler", "missing", "");
                return;
            }
            p.SetTestPose("P1_bench_test_view");
            p.RenderNow();
            Rect preview = p.GetPreviewRect();
            Vector2 center = preview.center;
            Vector2 outside = new Vector2(-10f, -10f);
            Func<EventType, int, Vector2, bool> dispatch = (kind, button, point) =>
                (bool)handler.Invoke(p, new object[] { kind, button, point });
            bool outsideDown = dispatch(EventType.MouseDown, 0, outside);
            bool outsideDrag = dispatch(EventType.MouseDrag, 0, center);
            bool leftDown = dispatch(EventType.MouseDown, 0, center);
            bool leftDrag = dispatch(EventType.MouseDrag, 0, center + new Vector2(12f, 4f));
            bool moved = p.LastSnapshot == null && p.CurrentSource == "test_view" && p.CurrentPoseId.EndsWith("+manual");
            bool leftUpOutside = dispatch(EventType.MouseUp, 0, outside);
            bool noStuckDrag = !dispatch(EventType.MouseDrag, 0, center);
            bool rightIgnored = !dispatch(EventType.MouseDown, 1, center);
            bool wheelIgnored = !dispatch(EventType.ScrollWheel, 0, center);
            AddCase("pointer_handler_ownership", "input",
                !outsideDown && !outsideDrag && leftDown && leftDrag && moved && leftUpOutside && noStuckDrag && rightIgnored && wheelIgnored ? "pass" : "fail",
                "synthetic calls to the shared pointer handler verify drag ownership, outside release, right/scroll pass-through",
                "inside left only, no stuck drag", "outside=" + outsideDown + "/" + outsideDrag + " left=" + leftDown + "/" + leftDrag + "/" + leftUpOutside +
                " moved=" + moved + " released=" + noStuckDrag + " right=" + rightIgnored + " scroll=" + wheelIgnored, "");

            var chat = GameObject.Find("UI_Chat");
            var doc = chat != null ? chat.GetComponent<UIDocument>() : null;
            var root = doc != null ? doc.rootVisualElement : null;
            var blocker = root != null ? root.Q<VisualElement>("Stage2PreviewPointerBlocker") : null;
            if (blocker == null || root.panel == null)
            {
                AddCase("ui_toolkit_pointer_routing", "input", "fail", "preview blocker missing", "attached blocker", "missing", "");
                return;
            }
            Vector2 panelCenter = RuntimePanelUtils.ScreenToPanel(root.panel, center);
            Vector2 panelOutside = RuntimePanelUtils.ScreenToPanel(root.panel, outside);
            bool picksPreview = root.panel.Pick(panelCenter) == blocker;
            bool outsideNotBlocked = root.panel.Pick(panelOutside) != blocker;
            int bubbledLeft = 0, bubbledRight = 0;
            EventCallback<PointerDownEvent> callback = evt =>
            {
                if (evt.button == 0) bubbledLeft++;
                if (evt.button == 1) bubbledRight++;
            };
            root.RegisterCallback(callback);
            try
            {
                using (var evt = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 0, mousePosition = panelCenter }))
                {
                    evt.target = blocker;
                    blocker.SendEvent(evt);
                }
                using (var evt = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, button = 0, mousePosition = panelCenter }))
                {
                    evt.target = blocker;
                    blocker.SendEvent(evt);
                }
                using (var evt = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 1, mousePosition = panelCenter }))
                {
                    evt.target = blocker;
                    blocker.SendEvent(evt);
                }
            }
            finally { root.UnregisterCallback(callback); }
            AddCase("ui_toolkit_pointer_routing", "input",
                picksPreview && outsideNotBlocked && bubbledLeft == 0 && bubbledRight == 1 ? "pass" : "fail",
                "real panel picking plus synthetic UI Toolkit events verify bounded overlay and left-event interception",
                "preview picked, outside clear, left intercepted, right bubbles",
                "picked=" + picksPreview + " outside=" + outsideNotBlocked + " left=" + bubbledLeft + " right=" + bubbledRight, "");
            p.Disable();
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------
        static Stage2SampleMesh MakeEmptySample(string id, Vector3 center, Vector3 size)
        {
            return new Stage2SampleMesh
            {
                Id = id,
                ClassName = "synthetic",
                NameZh = "synthetic",
                Vertices = new Vector3[0],
                Triangles = new int[0],
                TriangleParts = new int[0],
                Parts = new string[0],
                Bounds = new Bounds(center, size),
                Synthetic = true,
            };
        }

        static void RunMathTests(Stage2SceneData data)
        {
            Vector3 origin = new Vector3(1000f, 1000f, 1000f);
            Vector3 down = Vector3.down;
            Vector3 boxMin = new Vector3(990f, -1f, 990f);
            Vector3 boxMax = new Vector3(1010f, 0f, 1010f);
            float te, tx;

            bool inside = Stage2PickMath.RayAabb(new Vector3(1000f, -0.5f, 1000f), Vector3.right, boxMin, boxMax, out te, out tx);
            AddCase("math_raybox_parallel_inside", "math", inside && tx > 0f ? "pass" : "fail",
                "parallel ray inside the slab range intersects", "hit", "hit=" + inside + " tExit=" + tx, "");

            bool outside = Stage2PickMath.RayAabb(new Vector3(2000f, -0.5f, 1500f), Vector3.left, boxMin, boxMax, out te, out tx);
            AddCase("math_raybox_parallel_outside", "math", !outside ? "pass" : "fail",
                "parallel ray outside the slab is rejected", "miss", "hit=" + outside, "");

            bool behind = Stage2PickMath.RayAabb(new Vector3(1000f, -0.5f, 2000f), Vector3.forward, boxMin, boxMax, out te, out tx);
            AddCase("math_raybox_behind", "math", !behind ? "pass" : "fail",
                "box entirely behind the ray origin is rejected", "miss", "hit=" + behind, "");

            Vector3 a = new Vector3(990f, 995f, 990f);
            Vector3 b = new Vector3(1010f, 995f, 990f);
            Vector3 c = new Vector3(990f, 995f, 1010f);
            Stage2SampleMesh nearTri = Stage2PickMath.MakeTriangleSample("synthetic_near", a, b, c);
            Stage2SampleMesh rearTri = Stage2PickMath.MakeTriangleSample("synthetic_rear",
                new Vector3(990f, 990f, 990f), new Vector3(1010f, 990f, 990f), new Vector3(990f, 990f, 1010f));
            float t1, t2;
            bool front = Stage2PickMath.RayTriangle(origin, down, a, b, c, out t1);
            bool back = Stage2PickMath.RayTriangle(new Vector3(1000f, 990f, 1000f), Vector3.up, a, b, c, out t2);
            AddCase("math_triangle_two_sided", "math", front && back ? "pass" : "fail",
                "ray/triangle accepts both windings", "front + back", "front=" + front + " back=" + back, "");

            var samples = new List<Stage2SampleMesh> { nearTri, rearTri };
            var nearest = Stage2PickMath.Pick(data, origin, down, 100f, 0f, Vector3.zero, null, null, samples);
            LogPick("math_nearest", nearest);
            AddCase("math_nearest_sample", "math",
                nearest.Status == Stage2PickStatus.Hit && nearest.SampleId == "synthetic_near" ? "pass" : "fail",
                "nearest real triangle among candidates wins", "synthetic_near",
                nearest.Status + ":" + nearest.SampleId, _pickPath);

            var empty = MakeEmptySample("synthetic_empty", new Vector3(1000f, 997f, 1000f), new Vector3(20f, 1f, 20f));
            var behindEmpty = Stage2PickMath.Pick(data, origin, down, 100f, 0f, Vector3.zero, null, null,
                new List<Stage2SampleMesh> { empty, rearTri });
            LogPick("math_bbox_hit_triangle_miss", behindEmpty);
            bool emptyBroad = behindEmpty.BroadPhaseSamples.Contains("synthetic_empty");
            AddCase("math_front_bbox_miss_rear_found", "math",
                behindEmpty.Status == Stage2PickStatus.Hit && behindEmpty.SampleId == "synthetic_rear" && emptyBroad ? "pass" : "fail",
                "front AABB is crossed but its triangle is missing; rear sample is still found",
                "empty broad + rear hit",
                behindEmpty.Status + ":" + behindEmpty.SampleId + " broadEmpty=" + emptyBroad, _pickPath);

            var occluder = Stage2PickMath.MakeOccluder("synthetic_occluder",
                new Vector3(990f, 994f, 990f), new Vector3(1010f, 996f, 1010f));
            var blocked = Stage2PickMath.Pick(data, origin, down, 100f, 0f, Vector3.zero, null,
                new List<Stage2Occluder> { occluder }, new List<Stage2SampleMesh> { rearTri });
            LogPick("math_synthetic_occlusion", blocked);
            AddCase("math_synthetic_occlusion", "math",
                blocked.Status == Stage2PickStatus.Blocked && blocked.OccluderId == "synthetic_occluder" ? "pass" : "fail",
                "synthetic foreground box blocks the sample and is recorded", "blocked/synthetic_occluder",
                blocked.Status + ":" + blocked.OccluderId, _pickPath);

            var eyeBox = Stage2PickMath.MakeOccluder("synthetic_eye_box",
                new Vector3(990f, 999f, 990f), new Vector3(1010f, 1001f, 1010f));
            var insideOccluder = Stage2PickMath.Pick(data, origin, down, 100f, 0f, Vector3.zero, null,
                new List<Stage2Occluder> { eyeBox }, new List<Stage2SampleMesh> { rearTri });
            LogPick("math_eye_inside_occluder", insideOccluder);
            AddCase("math_eye_inside_occluder", "math",
                insideOccluder.Diagnostics.Contains("eye_inside_occluder:synthetic_eye_box") ? "pass" : "fail",
                "eye inside a blocker produces an explicit diagnostic, not a silent ignore",
                "eye_inside_occluder:synthetic_eye_box", string.Join(",", insideOccluder.Diagnostics.ToArray()), _pickPath);

            var clipped = Stage2PickMath.Pick(data, origin, down, 5f, 0f, Vector3.zero, null, null,
                new List<Stage2SampleMesh> { rearTri });
            LogPick("math_max_distance_clip", clipped);
            AddCase("math_max_distance_clip", "math", clipped.Status == Stage2PickStatus.NoTarget ? "pass" : "fail",
                "hit beyond the supplied max distance is rejected", "no_target", clipped.Status.ToString(), _pickPath);

            var badDir = Stage2PickMath.Pick(data, origin, Vector3.zero, 100f, 0f, Vector3.zero, null, null, null);
            LogPick("math_invalid_direction", badDir);
            AddCase("math_invalid_direction", "math", badDir.Status == Stage2PickStatus.Unavailable ? "pass" : "fail",
                "zero direction is an explicit unavailable/error, never a fake no_target", "unavailable",
                badDir.Status + ":" + badDir.Diagnostic, _pickPath);

            // near/far clipping boundaries: an occluder interval may start before near
            // but still cover visible space; it must not be discarded by entry alone.
            Vector3 nearOrigin = new Vector3(1000f, 1000f, 1000f);
            Vector3 nearTargetTriA = new Vector3(990f, 999f, 990f);
            Vector3 nearTargetTriB = new Vector3(1010f, 999f, 990f);
            Vector3 nearTargetTriC = new Vector3(990f, 999f, 1010f);
            var nearTarget = Stage2PickMath.MakeTriangleSample("synthetic_near_target",
                nearTargetTriA, nearTargetTriB, nearTargetTriC);
            var nearSamples = new List<Stage2SampleMesh> { nearTarget };
            var nearBox = Stage2PickMath.MakeOccluder("synthetic_near_crossing",
                new Vector3(990f, 999.6f, 990f), new Vector3(1010f, 999.98f, 1010f));
            var nearExtra = Stage2PickMath.Pick(data, nearOrigin, down, 10f, 0.05f, Vector3.zero, null,
                new List<Stage2Occluder> { nearBox }, nearSamples);
            LogPick("math_near_crossing_extra", nearExtra);
            AddCase("math_occluder_crosses_near_extra", "math",
                nearExtra.Status == Stage2PickStatus.Blocked && nearExtra.OccluderId == "synthetic_near_crossing"
                    ? "pass" : "fail",
                "occluder interval crossing the near plane still blocks a visible target (extra path)",
                "blocked, clipped distance near", nearExtra.Status + ":" + nearExtra.OccluderId +
                " occDist=" + nearExtra.OccluderDistance.ToString("F4"), _pickPath);

            var nearBoxRaw = new Stage2Box
            {
                Id = "synthetic_near_crossing_raw",
                Method = "synthetic_aabb",
                ClassOrKind = "synthetic",
                Min = new Vector3(990f, 999.6f, 990f),
                Max = new Vector3(1010f, 999.98f, 1010f),
            };
            data.Occluders.Add(nearBoxRaw);
            Stage2PickResult nearRaw;
            try
            {
                nearRaw = Stage2PickMath.Pick(data, nearOrigin, down, 10f, 0.05f, Vector3.zero, null, null, nearSamples);
            }
            finally
            {
                data.Occluders.Remove(nearBoxRaw);
            }
            LogPick("math_near_crossing_raw", nearRaw);
            AddCase("math_occluder_crosses_near_raw", "math",
                nearRaw.Status == Stage2PickStatus.Blocked && nearRaw.OccluderId == "synthetic_near_crossing_raw"
                    ? "pass" : "fail",
                "occluder interval crossing the near plane still blocks a visible target (raw data path)",
                "blocked, clipped distance near", nearRaw.Status + ":" + nearRaw.OccluderId +
                " occDist=" + nearRaw.OccluderDistance.ToString("F4"), _pickPath);

            var beforeBox = Stage2PickMath.MakeOccluder("synthetic_before_near",
                new Vector3(990f, 999.96f, 990f), new Vector3(1010f, 999.99f, 1010f));
            var beforeNear = Stage2PickMath.Pick(data, nearOrigin, down, 10f, 0.05f, Vector3.zero, null,
                new List<Stage2Occluder> { beforeBox }, nearSamples);
            LogPick("math_occluder_before_near", beforeNear);
            AddCase("math_occluder_entirely_before_near", "math",
                beforeNear.Status == Stage2PickStatus.Hit ? "pass" : "fail",
                "occluder entirely before the near plane does not block", "hit",
                beforeNear.Status.ToString(), _pickPath);

            var farTargetTri = Stage2PickMath.MakeTriangleSample("synthetic_far_target",
                new Vector3(990f, 999.55f, 990f), new Vector3(1010f, 999.55f, 990f), new Vector3(990f, 999.55f, 1010f));
            var farSamples = new List<Stage2SampleMesh> { farTargetTri };
            var farCross = Stage2PickMath.MakeOccluder("synthetic_far_crossing",
                new Vector3(990f, 999.4f, 990f), new Vector3(1010f, 999.6f, 1010f));
            var farCrossResult = Stage2PickMath.Pick(data, nearOrigin, down, 0.5f, 0.05f, Vector3.zero, null,
                new List<Stage2Occluder> { farCross }, farSamples);
            LogPick("math_occluder_crosses_far", farCrossResult);
            AddCase("math_occluder_crosses_far", "math",
                farCrossResult.Status == Stage2PickStatus.Blocked ? "pass" : "fail",
                "occluder overlapping the far clip still blocks a target inside the range",
                "blocked", farCrossResult.Status.ToString(), _pickPath);

            var afterFar = Stage2PickMath.MakeOccluder("synthetic_after_far",
                new Vector3(990f, 999.0f, 990f), new Vector3(1010f, 999.3f, 1010f));
            var afterFarResult = Stage2PickMath.Pick(data, nearOrigin, down, 0.5f, 0.05f, Vector3.zero, null,
                new List<Stage2Occluder> { afterFar }, farSamples);
            LogPick("math_occluder_after_far", afterFarResult);
            AddCase("math_occluder_after_far", "math",
                afterFarResult.Status == Stage2PickStatus.Hit ? "pass" : "fail",
                "occluder entirely beyond the far clip does not block", "hit",
                afterFarResult.Status.ToString(), _pickPath);
        }

        static string SafeCaseId(string s)
        {
            if (string.IsNullOrEmpty(s)) return "missing";
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
                sb.Append(char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '-' ? s[i] : '_');
            return sb.ToString();
        }

        static void RunAllTargetCases(Stage2PickPrototype p, Stage2Manifest manifest)
        {
            int total = 0;
            int tested = 0;
            var targetIds = new HashSet<string>();
            foreach (var pose in manifest.poses)
            {
                if (pose == null || pose.eye_position_unity == null || pose.targets == null) continue;
                p.SetTestPose(pose.id);
                p.RenderNow();
                foreach (var target in pose.targets)
                {
                    if (target == null) continue;
                    total++;
                    string caseId = "target_" + SafeCaseId(target.id);
                    if (string.IsNullOrEmpty(target.id) || !targetIds.Add(target.id))
                    {
                        AddCase(caseId, "targets", "fail", "target has no unique id; cannot be traced",
                            "unique id", "<missing>", "");
                        continue;
                    }
                    var r = PickTarget(p, pose, target);
                    LogPick(caseId, r);
                    tested++;
                    string expected = target.expected_status ?? "";
                    bool ok = false;
                    string actual = r == null ? "<null>" : (r.Status + ":" + r.SampleId);
                    if (expected == "hit")
                    {
                        ok = r != null && r.Status == Stage2PickStatus.Hit && r.SampleId == target.sample_id;
                        if (ok && !string.IsNullOrEmpty(target.part_label))
                            ok = r.PartLabel == target.part_label;
                    }
                    else if (expected == "no_target")
                    {
                        ok = r != null && r.Status == Stage2PickStatus.NoTarget;
                    }
                    else if (expected == "blocked")
                    {
                        ok = r != null && r.Status == Stage2PickStatus.Blocked && !r.HasSelectableHit &&
                             r.BlockedCandidateId == target.sample_id &&
                            (string.IsNullOrEmpty(target.occluder_id) || r.OccluderId == target.occluder_id) &&
                            (string.IsNullOrEmpty(target.occluder_method) || r.OccluderMethod == target.occluder_method);
                        if (ok && !string.IsNullOrEmpty(target.part_label))
                            ok = r.BlockedCandidatePart == target.part_label;
                        actual = r == null ? "<null>" :
                            (r.Status + " candidate=" + r.BlockedCandidateId + " occluder=" + r.OccluderId);
                    }
                    AddCase(caseId, "targets", ok ? "pass" : "fail",
                        "delivered target " + target.kind + " from " + pose.id,
                        (expected + "/" + (target.sample_id ?? "") + "/" + (target.occluder_id ?? "")),
                        actual, _pickPath);
                }
            }
            AddCase("acceptance_targets_all_tested", "targets",
                total > 0 && tested == total ? "pass" : "fail",
                "every delivered target was executed and no target was silently skipped",
                "tested == total", "tested=" + tested + " total=" + total, _pickPath);
        }

        static void AddCase(string id, string category, string status, string detail,
            string expected, string actual, string evidence)
        {
            string baseId = id;
            int duplicateIndex = 2;
            while (_cases.Exists(record => record.id == id)) id = baseId + "_" + duplicateIndex++;
            _cases.Add(new Stage2CaseRecord
            {
                id = id,
                category = category,
                status = status,
                detail = detail ?? "",
                expected = expected ?? "",
                actual = actual ?? "",
                evidence = evidence ?? "",
            });
            UnityEngine.Debug.Log("[S2-ACCEPT] " + status.ToUpperInvariant() + " " + id + " | " + detail);
        }

        static Stage2Pose FindPose(Stage2Manifest manifest, string id)
        {
            if (manifest == null || manifest.poses == null) return null;
            foreach (var p in manifest.poses) if (p != null && p.id == id) return p;
            return null;
        }

        static IEnumerable<Stage2Target> Targets(Stage2Pose pose)
        {
            if (pose == null || pose.targets == null) yield break;
            for (int i = 0; i < pose.targets.Length; i++)
                if (pose.targets[i] != null) yield return pose.targets[i];
        }

        static Stage2Target FindTarget(Stage2Pose pose, string kind, string sampleId, string partClass = null)
        {
            if (pose == null || pose.targets == null) return null;
            foreach (var t in pose.targets)
            {
                if (t == null || t.kind != kind) continue;
                if (!string.IsNullOrEmpty(sampleId) && t.sample_id != sampleId) continue;
                if (!string.IsNullOrEmpty(partClass) && t.part_class != partClass) continue;
                return t;
            }
            return null;
        }

        static Stage2PickResult PickTarget(Stage2PickPrototype p, Stage2Pose pose, Stage2Target target)
        {
            Vector3 eye = ToVector(pose.eye_position_unity);
            Vector3 point = ToVector(target.point);
            Vector3 dir = target.direction != null ? ToVector(target.direction).normalized : (point - eye).normalized;
            float maxD = target.max_distance > 0f ? target.max_distance : 120f;
            return p.PickRay(eye, dir, maxD, p.LastSnapshot);
        }

        static Vector3 ToVector(float[] v)
        {
            return new Vector3(v[0], v[1], v[2]);
        }

        static void LogPick(string tag, Stage2PickResult r)
        {
            if (r == null) return;
            var logged = new LoggedPick { tag = tag, pick = r.ToRecord() };
            File.AppendAllText(_pickPath, JsonUtility.ToJson(logged) + "\n", Encoding.UTF8);
        }

        static void Capture(string tag, string description)
        {
            var p = Stage2PickPrototype.Instance;
            if (p == null) return;
            string path, sha;
            int w, h;
            p.RenderNow();
            LogPick("shot_" + tag, p.LastPick);
            if (!p.CaptureEvidence(_evidenceDir, tag, out path, out sha, out w, out h))
            {
                _shots.Add(new ScreenshotRecord { tag = tag, description = description, status = "fail", path = "" });
                AddCase("screenshot_" + tag, "screenshot", "fail", "capture failed", "saved image", "missing", "");
                return;
            }
            var stats = ImageStats(path);
            var rec = new ScreenshotRecord
            {
                tag = tag,
                description = description,
                path = path,
                sha256 = sha,
                width = w,
                height = h,
                mean_luma = stats.mean,
                magenta_ratio = stats.magenta,
                frame_id = p.LastSnapshot != null ? p.LastSnapshot.FrameId : -1,
                snapshot_id = p.LastSnapshot != null ? p.LastSnapshot.SnapshotId : "",
                snapshot = p.LastSnapshot != null ? p.LastSnapshot.ToRecord() : null,
                pick = p.LastPick != null ? p.LastPick.ToRecord() : null,
                status = (stats.mean > 0.02f && stats.magenta <= 0.05f) ? "pass" : "fail",
            };
            _shots.Add(rec);
            AddCase("screenshot_" + tag, "screenshot", rec.status,
                rec.status == "pass" ? "captured non-magenta frame above brightness floor" : "frame too dark or magenta",
                "mean>0.02, magenta<=5%",
                "mean=" + stats.mean.ToString("F3") + " magenta=" + stats.magenta.ToString("F3"),
                path);
        }

        static (float mean, float magenta) ImageStats(string path)
        {
            try
            {
                var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(File.ReadAllBytes(path)))
                {
                    UnityEngine.Object.Destroy(tex);
                    return (0f, 1f);
                }
                var px = tex.GetPixels32();
                double sum = 0; int pink = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    float r = px[i].r / 255f, g = px[i].g / 255f, b = px[i].b / 255f;
                    sum += 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    if (r > 0.85f && b > 0.85f && g < 0.25f) pink++;
                }
                float magenta = px.Length > 0 ? pink / (float)px.Length : 1f;
                UnityEngine.Object.Destroy(tex);
                return ((float)(sum / Math.Max(1, px.Length)), magenta);
            }
            catch
            {
                return (0f, 1f);
            }
        }

        static Invariants CaptureInvariants()
        {
            return new Invariants
            {
                colliders = UnityEngine.Object.FindObjectsOfType<Collider>().Length,
                markers = UnityEngine.Object.FindObjectsOfType<TaskDestinationMarker>().Length,
                hud = UnityEngine.Object.FindObjectOfType<UnifiedHud>() != null,
                chat = UnityEngine.Object.FindObjectOfType<Yixuan.AI.ChatUI>() != null,
            };
        }

        static Performance MeasurePerformance(Stage2PickPrototype p, int frames)
        {
            var watch = new Stopwatch();
            double total = 0.0;
            float worst = 0f;
            for (int i = 0; i < frames; i++)
            {
                watch.Restart();
                p.RenderNow();
                float ms = (float)watch.Elapsed.TotalMilliseconds;
                total += ms;
                if (ms > worst) worst = ms;
            }
            watch.Stop();
            return new Performance
            {
                frames = frames,
                avg_ms = (float)(total / Math.Max(1, frames)),
                worst_ms = worst,
            };
        }

        static bool TrySetImGuiKeyboardControl(int value)
        {
            try
            {
                var prop = typeof(GUIUtility).GetProperty("keyboardControl",
                    BindingFlags.Public | BindingFlags.Static);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(null, value);
                    return true;
                }
            }
            catch { }
            return false;
        }

        static void WriteBootFailure(string detail)
        {
            EnsureEvidence();
            if (_cases == null) _cases = new List<Stage2CaseRecord>();
            if (_shots == null) _shots = new List<ScreenshotRecord>();
            AddCase("boot", "boot", "fail", detail, "prototype ready", detail, "");
            WriteSummary(false);
        }

        static int ComputeExitCode(bool completed, int fail, int pending)
        {
            if (!completed || fail > 0) return 1;   // reject: incomplete / failed
            if (pending > 0) return 2;              // executed but not fully accepted
            return 0;                               // fully accepted
        }

        static AcceptanceSummary BuildSummary(bool completed, IEnumerable<Stage2CaseRecord> cases,
            IEnumerable<ScreenshotRecord> shots)
        {
            var caseList = cases == null ? new List<Stage2CaseRecord>() : new List<Stage2CaseRecord>(cases);
            var shotList = shots == null ? new List<ScreenshotRecord>() : new List<ScreenshotRecord>(shots);
            int pass = 0, fail = 0, pending = 0;
            foreach (var c in caseList)
            {
                if (c.status == "pass") pass++;
                else if (c.status == "fail") fail++;
                else pending++;
            }
            int exitCode = ComputeExitCode(completed, fail, pending);
            string result = exitCode == 0 ? "accepted" : (exitCode == 2 ? "pending" : "rejected");
            return new AcceptanceSummary
            {
                schema = "sbh.stage2.acceptance.v1",
                created_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                scene = ScenePath,
                unity = Application.unityVersion,
                completed = completed,
                passed = exitCode == 0,
                pass = pass,
                fail = fail,
                pending = pending,
                exit_code = exitCode,
                result = result,
                evidence_dir = _evidenceDir,
                cases = caseList.ToArray(),
                screenshots = shotList.ToArray(),
                visual_check = "pending: vision.js returned 403 AllocationQuota.FreeTierOnly; runtime overlay not visually verified",
                notes = "completed != passed. exit_code: 0=fully accepted, 2=executed with pending items, 1=failed/incomplete. "
                        + "Screenshot visual judgement remains pending.",
            };
        }

        static void WriteSummaryFiles(AcceptanceSummary summary)
        {
            EnsureEvidence();
            File.WriteAllText(Path.Combine(_evidenceDir, "summary.json"),
                JsonUtility.ToJson(summary, true), Encoding.UTF8);
            var md = new StringBuilder();
            md.AppendLine("# Stage 2 pick acceptance");
            md.AppendLine();
            md.AppendLine("- completed: " + summary.completed);
            md.AppendLine("- result: " + summary.result + " (exit_code=" + summary.exit_code + ")");
            md.AppendLine("- pass/fail/pending: " + summary.pass + "/" + summary.fail + "/" + summary.pending);
            md.AppendLine("- visual check: " + summary.visual_check);
            md.AppendLine("- notes: " + summary.notes);
            md.AppendLine();
            md.AppendLine("| id | category | status | detail | expected | actual |");
            md.AppendLine("|---|---|---|---|---|---|");
            foreach (var c in summary.cases)
                md.AppendLine("| " + c.id + " | " + c.category + " | " + c.status + " | " + c.detail + " | " + c.expected + " | " + c.actual + " |");
            md.AppendLine();
            md.AppendLine("## screenshots");
            foreach (var s in summary.screenshots)
                md.AppendLine("- " + s.tag + " [" + s.status + "] mean=" + s.mean_luma.ToString("F3") +
                              " magenta=" + s.magenta_ratio.ToString("F3") + " sha=" + s.sha256 + " path=" + s.path);
            File.WriteAllText(Path.Combine(_evidenceDir, "summary.md"), md.ToString(), Encoding.UTF8);
        }

        static void WriteSummary(bool completed)
        {
            EnsureEvidence();
            if (_cases == null) _cases = new List<Stage2CaseRecord>();
            if (_shots == null) _shots = new List<ScreenshotRecord>();
            var summary = BuildSummary(completed, _cases, _shots);
            WriteSummaryFiles(summary);
            SessionState.SetInt(ExitCodeKey, summary.exit_code);
        }

        static void WritePostRunChecks()
        {
            EnsureEvidence();
            var post = new PostRunChecks
            {
                play_exited = !EditorApplication.isPlaying,
                prototype_gone = GameObject.Find("Stage2PickPrototype") == null,
                scene_dirty = EditorSceneManager.GetActiveScene().isDirty,
                files = HashTrackedFiles(),
            };
            File.WriteAllText(Path.Combine(_evidenceDir, "post_run_checks.json"),
                JsonUtility.ToJson(post, true), Encoding.UTF8);

            AcceptanceSummary summary = null;
            string summaryPath = Path.Combine(_evidenceDir, "summary.json");
            if (File.Exists(summaryPath))
            {
                try { summary = JsonUtility.FromJson<AcceptanceSummary>(File.ReadAllText(summaryPath)); } catch { }
            }
            if (summary == null)
            {
                summary = BuildSummary(false, new List<Stage2CaseRecord>(), new List<ScreenshotRecord>());
            }
            var cases = new List<Stage2CaseRecord>(summary.cases ?? new Stage2CaseRecord[0]);
            cases.Add(new Stage2CaseRecord
            {
                id = "post_play_exited", category = "post", status = post.play_exited ? "pass" : "fail",
                detail = "Play exited before post-run checks", expected = "isPlaying=false",
                actual = "isPlaying=" + EditorApplication.isPlaying, evidence = _evidenceDir,
            });
            cases.Add(new Stage2CaseRecord
            {
                id = "post_scene_not_saved", category = "post", status = post.scene_dirty ? "fail" : "pass",
                detail = "acceptance never saved the scene", expected = "isDirty=false",
                actual = "isDirty=" + post.scene_dirty, evidence = _evidenceDir,
            });
            cases.Add(new Stage2CaseRecord
            {
                id = "post_prototype_gone", category = "post", status = post.prototype_gone ? "pass" : "fail",
                detail = "runtime host removed after Play", expected = "host gone",
                actual = "hostGone=" + post.prototype_gone, evidence = _evidenceDir,
            });
            bool filesSame = false;
            string headerPath = Path.Combine(_evidenceDir, "run_header.json");
            if (File.Exists(headerPath))
            {
                try
                {
                    var header = JsonUtility.FromJson<RunHeader>(File.ReadAllText(headerPath));
                    if (header != null && header.files != null && header.files.Length == post.files.Length)
                    {
                        filesSame = true;
                        for (int i = 0; i < header.files.Length; i++)
                        {
                            var a = header.files[i];
                            var b = post.files[i];
                            if (a.path != b.path || a.sha256 != b.sha256) { filesSame = false; break; }
                        }
                    }
                }
                catch { filesSame = false; }
            }
            cases.Add(new Stage2CaseRecord
            {
                id = "post_tracked_files_unchanged", category = "post",
                status = filesSame ? "pass" : "fail",
                detail = "tracked scene/prefab/scripts hashes unchanged after Play",
                expected = "same 13 hashes", actual = filesSame ? "same" : "different", evidence = _evidenceDir,
            });
            summary.completed = summary.completed && SessionState.GetString("S2_ACCEPT_COMPLETED", "0") == "1";
            summary.cases = cases.ToArray();
            var rebuilt = BuildSummary(summary.completed, summary.cases, summary.screenshots);
            WriteSummaryFiles(rebuilt);
            SessionState.SetInt(ExitCodeKey, rebuilt.exit_code);
            UnityEngine.Debug.Log("[S2-ACCEPT] post checks written; prototypeGone=" + post.prototype_gone +
                                  " sceneDirty=" + post.scene_dirty + " exit=" + rebuilt.exit_code);
        }

        // ------------------------------------------------------------------
        // serializable helper classes
        // ------------------------------------------------------------------
        [Serializable] class FileHash { public string path; public long bytes; public string sha256; }
        [Serializable] class RunHeader { public string created_at; public string scene; public string unity; public string evidence_dir; public FileHash[] files; }
        [Serializable] class PostRunChecks { public bool play_exited; public bool prototype_gone; public bool scene_dirty; public FileHash[] files; }
        [Serializable] class LoggedPick { public string tag; public Stage2PickRecord pick; }
        [Serializable] class ScreenshotRecord
        {
            public string tag;
            public string description;
            public string status;
            public string path;
            public string sha256;
            public int width;
            public int height;
            public float mean_luma;
            public float magenta_ratio;
            public long frame_id;
            public string snapshot_id;
            public Stage2SnapshotRecord snapshot;
            public Stage2PickRecord pick;
        }
        [Serializable] class Performance { public int frames; public float avg_ms; public float worst_ms; }
        [Serializable] class Invariants { public int colliders; public int markers; public bool hud; public bool chat; }
        [Serializable] class AcceptanceSummary
        {
            public string schema;
            public string created_at;
            public string scene;
            public string unity;
            public bool completed;
            public bool passed;
            public int pass;
            public int fail;
            public int pending;
            public int exit_code;
            public string result;
            public string evidence_dir;
            public Stage2CaseRecord[] cases;
            public ScreenshotRecord[] screenshots;
            public string visual_check;
            public string notes;
        }
    }
}

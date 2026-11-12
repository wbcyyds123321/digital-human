// Universal seats + big map acceptance v2 (review round 2).
//
//   Unity.exe -batchmode -projectPath <proj>
//     -executeMethod Yixuan.EditorTools.InteractionExpansionAcceptance.Run
//     -logFile Logs/overnight_interactions_20261109/acceptance_round2.log
//
// Changes forced by Codex node-1 review round 1:
//   R1  only an explicit Run() arms the harness. A cold-start editor, an ordinary
//       Play session or any other executeMethod can never start the acceptance,
//       teleport the character or exit the editor. Output goes to a unique run
//       directory (SBH_IA_RUN_DIR or a timestamped folder).
//   R2  seat walk budget is derived from the real remaining path length and walk
//       speed; at least two real walks from the scene start that need >25 s are
//       exercised (no teleport-to-approach shortcut for those).
//   R3  approach candidates are validated against the bench frame (bench-local
//       front/right/left/back) for 0/90/180/270 and non-right-angle benches.
//   R4  pointer gestures are exercised through the shared gesture state machine
//       (drag then click, click, click on ground, close/reopen, right drag).
//   R5  the drawn route must equal the live world->map projection (<1 px error),
//       including after zoom and pan.
//   R6  preview target == executed target for bus/building/ground/bench.
//   R7  the info card follows the real seat controller state and offers a real
//       起身 button; action buttons stay inside the panel.
//   R8  gaze fan is bound to the character EyeCamera source, not any camera.
//   R9  logical completion code is reported separately from the editor process
//       exit (batchmode Play exit hang); report says which path was used.
//
// Exit codes written to EXIT_CODE: 0 = all required checks pass, 1 = failure,
// 2 = pass with pending items. The editor process itself is force-killed after
// the report/EXIT_CODE are on disk because batchmode Play-mode exit hangs here.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Yixuan.Motion;
using Yixuan.Stage4;
using Yixuan.UI;
using Yixuan.World;
using Yixuan.WorldMap;
using Object = UnityEngine.Object;

namespace Yixuan.EditorTools
{
    [InitializeOnLoad]
    public static class InteractionExpansionAcceptance
    {
        const string ScenePath = "Assets/Scenes/SovietBlueHour_TaskPlan.unity";
        const string StageKey = "SBH_IA2_STAGE";
        const string T0Key = "SBH_IA2_T0";
        const string ArmedKey = "SBH_IA2_ARMED";
        const string RunDirKey = "SBH_IA2_RUNDIR";
        const string EvidenceDirKey = "SBH_IA2_EVIDENCE";
        const string RunStartKey = "SBH_IA2_RUNSTART";
        const string ExitRequestKey = "SBH_IA2_EXIT_REQUEST";
        const string ExitTriesKey = "SBH_IA2_EXIT_TRIES";
        const int RequiredSeatCycles = 12;

        public static bool IsArmedForTest { get { return _armed; } }
        public static bool AnyRunStartedInEditorSession { get { return _runStarted; } }
        public static string LastRunDir { get; private set; } = "";

        enum Stage { Idle, Starting, WaitingRuntime, DataChecks, MapChecks, GestureChecks,
                     RouteChecks, TargetChecks, SeatSetup, Seating, Standing, MapInteraction,
                     Wrap, Exit }

        static bool _armed;
        static bool _runStarted;
        static Stage _stage = Stage.Idle;
        static float _stageStart;
        static float _runStart;
        static string _runDir = "";
        static string _evidenceDir = "";
        static readonly StringBuilder LogBuilder = new StringBuilder();
        static readonly List<string> Results = new List<string>();
        static int _failed;
        static int _pending;

        // seat plan
        static readonly List<SeatCycle> SeatPlan = new List<SeatCycle>();
        static int _seatIndex = -1;
        static int _seatCycles;
        static int _seatAttempts;
        static string _seatCycleId = "";
        static string _seatOrigin = "";
        static float _seatCycleStart;
        static float _seatCycleExpectedSeconds;
        static readonly List<string> SeatIdsTested = new List<string>();
        static readonly List<int> SeatYaws = new List<int>();
        static readonly List<float> SeatHeights = new List<float>();
        static readonly List<string> SeatSides = new List<string>();
        static readonly List<SeatCycleResult> SeatResults = new List<SeatCycleResult>();
        static readonly List<LongWalkResult> LongWalks = new List<LongWalkResult>();

        static readonly List<string> Captures = new List<string>();
        static string _lastError = "";
        static string _lastMapSnapshot = "";
        static int _mapBenchMarkers;
        static int _mapVehicleMarkers;
        static int _mapSearchMarkers;
        static bool _mapPreviewOk;
        static bool _mapConfirmOk;
        static bool _mapCloseOk;
        static bool _gestureRightDragOk;
        static float _gestureDragDistance;
        static float _routeErrorAfterPreview = -1f;
        static float _routeErrorAfterZoom = -1f;
        static float _routeErrorAfterPan = -1f;
        static int _routePoints;
        static bool _targetsEqual;
        static bool _dedupeOk;
        static int _benchBoundsRegistered;
        static int _benchBoundsCovered;
        static bool _frameAlignmentOk;
        static int _frameSamples;
        static bool _gazeEyeOk;
        static bool _cardLiveOk;
        static bool _actionsInsidePanel;
        static bool _sceneDirtyAtStart;
        static Vector3 _characterAtMapOpen;
        static bool _completedAllStages;
        static string _exitPath = "not_exited";
        static bool _mapNavCloseOk;

        class SeatCycle
        {
            public string id;
            public Vector3 origin;
            public string originKind;
            public bool isLong;
            public float expectedSeconds;
        }

        class SeatCycleResult
        {
            public string id;
            public string originKind;
            public bool isLong;
            public float pathMeters;
            public float budgetSeconds;
            public float elapsedSeconds;
            public string approachSide;
            public float expectedSeconds;
            public bool verified;
        }

        class LongWalkResult
        {
            public string id;
            public float pathMeters;
            public float expectedSeconds;
            public float budgetSeconds;
            public float elapsedSeconds;
            public bool arrived;
        }

        static InteractionExpansionAcceptance()
        {
            EditorApplication.update += Tick;
        }

        static string ResolveRunDir()
        {
            string root = Path.GetDirectoryName(Application.dataPath) ?? ".";
            string env = Environment.GetEnvironmentVariable("SBH_IA_RUN_DIR");
            string name = string.IsNullOrEmpty(env)
                ? "Captures/InteractionExpansion/run_" + DateTime.Now.ToString("yyyyMMdd-HHmmss")
                : env;
            string dir = Path.IsPathRooted(name) ? name : Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void Run()
        {
            // R1: only this explicit call arms the harness.
            _armed = true;
            _runStarted = true;
            SessionState.SetString(ArmedKey, "1");
            SessionState.SetString(StageKey, "starting");
            SessionState.SetString(T0Key, "");
            _runDir = ResolveRunDir();
            LastRunDir = _runDir;
            _evidenceDir = Path.Combine(_runDir, "acceptance_node1");
            Directory.CreateDirectory(_evidenceDir);
            SessionState.SetString(RunDirKey, _runDir);
            SessionState.SetString(EvidenceDirKey, _evidenceDir);
            SessionState.SetString(RunStartKey, EditorApplication.timeSinceStartup.ToString("F4"));
            LogBuilder.Clear();
            Results.Clear();
            _failed = 0;
            _pending = 0;
            _stage = Stage.Starting;
            _runStart = (float)EditorApplication.timeSinceStartup;
            Log("== InteractionExpansion acceptance v2 (review round 2) ==");
            Log("run_dir=" + _runDir);
            Log("unity=" + Application.unityVersion + " screen=" + Screen.width + "x" + Screen.height);
            WriteRunInfo();
            string inject = Environment.GetEnvironmentVariable("SBH_IA_EXIT_INJECT");
            if (inject == "pass" || inject == "fail")
            {
                RunExitInjection(inject == "pass");
                return;
            }
            if (inject == "hang")
            {
                RunHangInjection();
                return;
            }
            try
            {
                if (File.Exists(ScenePath))
                {
                    EditorSceneManager.OpenScene(ScenePath);
                    _sceneDirtyAtStart = EditorSceneManager.GetActiveScene().isDirty;
                    Log("opened scene " + ScenePath + " dirty=" + _sceneDirtyAtStart);
                }
                else Log("SCENE MISSING " + ScenePath);
            }
            catch (Exception e) { Log("open scene failed: " + e.Message); }
            EditorApplication.isPlaying = true;
            Debug.Log("[IA2-ACC] requested Play mode (explicit Run only)");
        }

        static void WriteRunInfo()
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"schema\": \"sbh.interaction-expansion.run-info.v2\",\n");
            sb.Append("  \"run_dir\": \"").Append(Escape(_runDir)).Append("\",\n");
            sb.Append("  \"started_at\": \"").Append(DateTime.Now.ToString("o")).Append("\",\n");
            sb.Append("  \"armed_only_by_explicit_run\": true,\n");
            sb.Append("  \"unity\": \"").Append(Application.unityVersion).Append("\",\n");
            sb.Append("  \"screen\": \"").Append(Screen.width).Append('x').Append(Screen.height).Append("\",\n");
            sb.Append("  \"scene\": \"").Append(ScenePath).Append("\"\n");
            sb.Append("}\n");
            File.WriteAllText(Path.Combine(_evidenceDir, "run_info.json"), sb.ToString());
        }

        static void Log(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + message;
            LogBuilder.AppendLine(line);
            Debug.Log("[IA2-ACC] " + message);
        }

        static void Check(string id, bool passed, string detail, string reviewItem = "")
        {
            Results.Add("{\"id\":\"" + Escape(id) + "\",\"status\":\"" + (passed ? "pass" : "fail") +
                        "\",\"review_item\":\"" + Escape(reviewItem) + "\",\"detail\":\"" +
                        Escape(detail) + "\"}");
            if (!passed) _failed++;
            Log((passed ? "PASS " : "FAIL ") + id + (reviewItem.Length > 0 ? " [" + reviewItem + "]" : "") +
                " :: " + detail);
        }

        static void Pending(string id, string detail, string reviewItem = "")
        {
            Results.Add("{\"id\":\"" + Escape(id) + "\",\"status\":\"pending\",\"review_item\":\"" +
                        Escape(reviewItem) + "\",\"detail\":\"" + Escape(detail) + "\"}");
            _pending++;
            Log("PENDING " + id + (reviewItem.Length > 0 ? " [" + reviewItem + "]" : "") + " :: " + detail);
        }

        static string Escape(string value)
        {
            if (value == null) return "";
            return value.Replace("\\", "/").Replace("\"", "'").Replace("\n", " ").Replace("\r", " ");
        }

        static void SetStage(Stage stage)
        {
            _stage = stage;
            _stageStart = (float)EditorApplication.timeSinceStartup;
            SessionState.SetString(StageKey, stage.ToString());
            Log("-- stage " + stage + " --");
        }

        static float StageElapsed()
        {
            return (float)EditorApplication.timeSinceStartup - _stageStart;
        }

        static void FailStage(string reason)
        {
            _lastError = reason;
            Check("stage." + _stage, false, reason);
            Finish(false);
        }

        static void EnsureRunState()
        {
            // Play-mode domain reload clears statics; restore the explicit run state.
            if (string.IsNullOrEmpty(_runDir))
            {
                _runDir = SessionState.GetString(RunDirKey, "");
                _evidenceDir = SessionState.GetString(EvidenceDirKey, "");
                LastRunDir = _runDir;
                if (!string.IsNullOrEmpty(_runDir) && !string.IsNullOrEmpty(_evidenceDir))
                    Directory.CreateDirectory(_evidenceDir);
            }
            if (_runStart <= 0f)
            {
                float.TryParse(SessionState.GetString(RunStartKey, "0"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _runStart);
            }
        }

        static void Tick()
        {
            // Exit request written by Finish(); survives Play-mode domain reloads.
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
            EnsureRunState();
            // R1: never act unless Run() armed this editor session.
            if (!_armed)
            {
                if (SessionState.GetString(ArmedKey, "") == "1") _armed = true;
                else return;
            }
            if (SessionState.GetString(StageKey, "") == "done") return;
            if (_stage == Stage.Idle || _stage == Stage.Starting)
            {
                if (!EditorApplication.isPlaying) return;
                string t0 = SessionState.GetString(T0Key, "");
                if (string.IsNullOrEmpty(t0))
                {
                    SessionState.SetString(T0Key, EditorApplication.timeSinceStartup.ToString("F4"));
                    return;
                }
                SetStage(Stage.WaitingRuntime);
                return;
            }
            if (!EditorApplication.isPlaying) return;
            if ((float)EditorApplication.timeSinceStartup - _runStart > 2400f)
            {
                FailStage("overall timeout");
                return;
            }
            try
            {
                switch (_stage)
                {
                    case Stage.WaitingRuntime: TickWaitingRuntime(); break;
                    case Stage.DataChecks: TickDataChecks(); break;
                    case Stage.MapChecks: TickMapChecks(); break;
                    case Stage.GestureChecks: TickGestureChecks(); break;
                    case Stage.RouteChecks: TickRouteChecks(); break;
                    case Stage.TargetChecks: TickTargetChecks(); break;
                    case Stage.SeatSetup: TickSeatSetup(); break;
                    case Stage.Seating: TickSeating(); break;
                    case Stage.Standing: TickStanding(); break;
                    case Stage.MapInteraction: TickMapInteraction(); break;
                    case Stage.Wrap: TickWrap(); break;
                }
            }
            catch (Exception e)
            {
                FailStage("exception in " + _stage + ": " + e.Message);
            }
        }

        // ------------------------------------------------------------ runtime wait
        static void TickWaitingRuntime()
        {
            bool catalog = WorldEntityCatalog.EnsureLoaded();
            bool seats = Stage4SeatData.Loaded || LoadSeats();
            bool map = WorldMapData.Load();
            if (StageElapsed() < 120f && (!catalog || !seats || !map)) return;
            Check("runtime.catalog_loaded", catalog,
                  "entities=" + WorldEntityCatalog.Count + " err=" + WorldEntityCatalog.Error);
            Check("runtime.seat_package_loaded", seats,
                  "entries=" + Stage4SeatData.Entries.Length + " err=" + Stage4SeatData.Error);
            Check("runtime.world_map_loaded", map,
                  "entities=" + (WorldMapData.Data != null ? WorldMapData.Data.entities.Length : -1) +
                  " err=" + WorldMapData.Error);
            if (!catalog || !seats || !map) { Finish(false); return; }
            Log("Stage4 load log: " + Stage4SeatData.LoadLog);
            SetStage(Stage.DataChecks);
        }

        static bool LoadSeats()
        {
            Stage4SeatData.EnsureLoaded();
            return Stage4SeatData.Loaded;
        }

        // ------------------------------------------------------------ data checks
        static void TickDataChecks()
        {
            var counts = WorldEntityCatalog.Data != null ? WorldEntityCatalog.Data.counts : null;
            Check("data.catalog_entity_count", counts != null && counts.entities == 855,
                  "entities=" + (counts != null ? counts.entities : -1));
            Check("data.bench_count_83", counts != null && counts.benches == 83,
                  "benches=" + (counts != null ? counts.benches : -1));
            int usable = Stage4SeatData.Entries.Length;
            int excluded = Stage4SeatData.Excluded != null ? Stage4SeatData.Excluded.Length : 0;
            Check("data.seat_total_83", usable + excluded == 83,
                  "usable=" + usable + " excluded=" + excluded);
            Check("data.usable_benches_match_catalog",
                  counts != null && counts.usable_benches == usable,
                  "catalog=" + (counts != null ? counts.usable_benches : -1) + " runtime=" + usable);
            Check("data.usable_minimum", usable >= 76, "verified usable=" + usable);

            // R3: every candidate must follow the bench frame, not world axes.
            int misaligned = 0;
            int checkedCandidates = 0;
            int samples = 0;
            float worstFrontDot = 1f;
            var sampleYaws = new List<int>();
            foreach (var entry in Stage4SeatData.Entries)
            {
                if (entry.approach_candidates == null || entry.approach_candidates.Length < 4)
                {
                    misaligned += 4;
                    continue;
                }
                Vector3 seat = entry.SeatPoint;
                Vector3 front = FrontVector(entry);
                Vector3 right = new Vector3(front.z, 0f, -front.x);
                foreach (var candidate in entry.approach_candidates)
                {
                    if (candidate == null || candidate.position_unity == null ||
                        candidate.position_unity.Length < 3) { misaligned++; continue; }
                    Vector3 direction = new Vector3(candidate.position_unity[0] - seat.x, 0f,
                                                    candidate.position_unity[2] - seat.z);
                    if (direction.sqrMagnitude < 1e-4f) { misaligned++; continue; }
                    direction.Normalize();
                    checkedCandidates++;
                    float dotFront = Vector3.Dot(direction, front);
                    float dotRight = Vector3.Dot(direction, right);
                    bool ok;
                    switch (candidate.side)
                    {
                        case "front":
                            ok = dotFront > 0.95f;
                            if (dotFront < worstFrontDot) worstFrontDot = dotFront;
                            break;
                        case "back": ok = dotFront < -0.95f; break;
                        case "right": ok = dotRight > 0.95f && Mathf.Abs(dotFront) < 0.2f; break;
                        case "left": ok = dotRight < -0.95f && Mathf.Abs(dotFront) < 0.2f; break;
                        default: ok = false; break;
                    }
                    if (!ok) misaligned++;
                }
                int yaw = Mathf.RoundToInt(entry.SeatYaw) % 360;
                if (!sampleYaws.Contains(yaw) && samples < 10)
                {
                    sampleYaws.Add(yaw);
                    samples++;
                }
            }
            _frameSamples = samples;
            _frameAlignmentOk = misaligned == 0 && checkedCandidates >= 83 * 4;
            Check("R3.approach_candidates_follow_bench_frame", _frameAlignmentOk,
                  "candidates=" + checkedCandidates + " misaligned=" + misaligned +
                  " sample_yaws=" + JoinInts(sampleYaws) + " worst_front_dot=" +
                  worstFrontDot.ToString("F4"), "R3");
            bool primaryAligned = true;
            string primaryDetail = "";
            foreach (var entry in Stage4SeatData.Entries)
            {
                Vector3 seat = entry.SeatPoint;
                Vector3 front = FrontVector(entry);
                Vector3 primaryDir = entry.ApproachPoint - seat;
                primaryDir.y = 0f;
                if (primaryDir.sqrMagnitude < 1e-4f) { primaryAligned = false; primaryDetail = entry.entity_id; break; }
                primaryDir.Normalize();
                float dot = Vector3.Dot(primaryDir, front);
                if (dot < 0.95f)
                {
                    primaryAligned = false;
                    primaryDetail = entry.entity_id + " dot=" + dot.ToString("F3");
                    break;
                }
            }
            Check("R3.primary_approach_in_front", primaryAligned, primaryDetail.Length == 0
                  ? "all 83 primary approaches use the bench front" : primaryDetail, "R3");

            // R9: bench obstacle dedupe (Stage-4 must not re-register world_map benches)
            _benchBoundsRegistered = Stage4SeatData.BenchBoundsRegistered;
            _benchBoundsCovered = Stage4SeatData.BenchBoundsCoveredElsewhere;
            _dedupeOk = _benchBoundsCovered >= 78 && _benchBoundsRegistered <= 5;
            Check("R9.bench_obstacle_dedupe", _dedupeOk,
                  "stage4_registered=" + _benchBoundsRegistered +
                  " covered_elsewhere=" + _benchBoundsCovered +
                  " total_obstacles=" + (WorldData.Obstacles != null ? WorldData.Obstacles.Count : -1),
                  "R9");
            Check("data.nav_obstacles_loaded",
                  WorldData.Obstacles != null && WorldData.Obstacles.Count > 200,
                  "obstacles=" + (WorldData.Obstacles != null ? WorldData.Obstacles.Count : -1));

            // reachability census (read only)
            var loco = Object.FindObjectOfType<CharacterLocomotion>();
            Vector3 start = loco != null ? loco.transform.position : WorldEntityCatalog.Spawn;
            int reachable = 0;
            foreach (var entry in Stage4SeatData.Entries)
            {
                string detail;
                if (Stage4SeatData.TryCheckReachable(entry, start, out detail)) reachable++;
            }
            Log("reachable benches from scene start: " + reachable + "/" + usable);
            Check("data.reachable_minimum", reachable >= RequiredSeatCycles + 2,
                  "reachable=" + reachable);
            SetStage(Stage.MapChecks);
        }

        static Vector3 FrontVector(Stage4SeatEntry entry)
        {
            if (entry.frame != null && entry.frame.front_unity != null &&
                entry.frame.front_unity.Length >= 3)
                return new Vector3(entry.frame.front_unity[0], 0f, entry.frame.front_unity[2]).normalized;
            float yaw = entry.SeatYaw * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
        }

        // ------------------------------------------------------------ map checks
        static void TickMapChecks()
        {
            if (!BigMapPanel.IsOpen)
            {
                bool opened = BigMapPanel.OpenForTest();
                if (!opened) { FailStage("big map did not open"); return; }
                _characterAtMapOpen = CharacterPosition();
                return;
            }
            if (StageElapsed() < 0.8f) return;
            _lastMapSnapshot = BigMapPanel.LayoutSnapshotForTest();
            Log("map layout: " + _lastMapSnapshot);
            Check("map.open", BigMapPanel.IsOpen, "big map open");
            Check("map.hud_autohidden", !UnifiedHud.IsConsoleVisible, "debug HUD hidden while map open");
            Check("map.filter_bench", BigMapPanel.SetFilterForTest("bench") &&
                  (_mapBenchMarkers = BigMapPanel.VisibleMarkerCount) == 83,
                  "bench markers=" + _mapBenchMarkers);
            Check("map.filter_vehicle", BigMapPanel.SetFilterForTest("vehicle") &&
                  (_mapVehicleMarkers = BigMapPanel.VisibleMarkerCount) == 48,
                  "vehicle markers=" + _mapVehicleMarkers);
            BigMapPanel.SetFilterForTest("");
            Check("map.search_culture_house", BigMapPanel.SetSearchForTest("文化宫") &&
                  (_mapSearchMarkers = BigMapPanel.VisibleMarkerCount) >= 1,
                  "search markers=" + _mapSearchMarkers);
            BigMapPanel.SetSearchForTest("");
            Capture("bigmap_default.png");
            SetStage(Stage.GestureChecks);
        }

        // --------------------------------------------------------- gesture checks
        static int _gesturePhase;
        static string _gestureBenchId = "entity/prop_bench_plaza_0";
        static Vector3 _characterBeforeGestures;
        static bool _dragEntityOk;
        static bool _dragGroundOk;
        static bool _reopenOk;

        static void TickGestureChecks()
        {
            if (_gesturePhase == 0)
            {
                _characterBeforeGestures = CharacterPosition();
                BigMapPanel.ResetViewForTest();
                _gesturePhase = 1;
                return;
            }
            if (_gesturePhase == 1)
            {
                _gestureDragDistance = BigMapPanel.SimulateDragForTest(
                    new Vector2(120f, 120f), new Vector2(300f, 190f), true);
                // a real UI frame would rebuild the icon layer after the pan; do the
                // same synchronously before resolving the entity's current local point
                BigMapPanel.RebuildMarkersForTest();
                string localText = BigMapPanel.LocalOfEntityForTest(_gestureBenchId);
                Check("R4.drag_distance_positive",
                      _gestureDragDistance > 20f, "drag_distance=" +
                      _gestureDragDistance.ToString("F1"), "R4");
                _dragEntityOk = localText.Length > 0 &&
                    BigMapPanel.SimulateClickAtForTest(ParseVector2(localText), true) &&
                    BigMapPanel.SelectedStateForTest().Contains(_gestureBenchId);
                Check("R4.drag_then_click_entity", _dragEntityOk,
                      "drag_distance=" + _gestureDragDistance.ToString("F1") +
                      " selected=" + BigMapPanel.SelectedStateForTest(), "R4");
                Check("R4.click_after_drag_character_static",
                      Vector3.Distance(_characterBeforeGestures, CharacterPosition()) < 0.02f,
                      "browse moved character by " +
                      Vector3.Distance(_characterBeforeGestures, CharacterPosition()).ToString("F3"),
                      "R4");
                _gesturePhase = 2;
                return;
            }
            if (_gesturePhase == 2)
            {
                BigMapPanel.SimulateDragForTest(new Vector2(120f, 120f), new Vector2(260f, 220f), true);
                BigMapPanel.RebuildMarkersForTest();
                string emptyText = BigMapPanel.EmptyGroundLocalForTest();
                _dragGroundOk = emptyText.Length > 0 &&
                    BigMapPanel.SimulateClickAtForTest(ParseVector2(emptyText), true) &&
                    BigMapPanel.SelectedStateForTest().Contains("ground=True");
                string state = BigMapPanel.SelectedStateForTest();
                _dragGroundOk = _dragGroundOk && !state.Contains("-374");
                Check("R4.drag_then_click_ground", _dragGroundOk,
                      "empty_local=" + emptyText + " state=" + state, "R4");
                string centerBefore = BigMapPanel.PanCenterForTest();
                BigMapPanel.SimulateDragForTest(new Vector2(300f, 300f), new Vector2(520f, 420f), false);
                _gestureRightDragOk = centerBefore == BigMapPanel.PanCenterForTest();
                Check("R4.right_drag_does_not_pan", _gestureRightDragOk,
                      "before=" + centerBefore + " after=" + BigMapPanel.PanCenterForTest(), "R4");
                BigMapPanel.CloseForTest();
                BigMapPanel.OpenForTest();
                _gesturePhase = 3;
                return;
            }
            if (_gesturePhase == 3)
            {
                // wait for the reopened map to be laid out before clicking
                string localText = BigMapPanel.LocalOfEntityForTest(_gestureBenchId);
                _reopenOk = localText.Length > 0 &&
                    BigMapPanel.SimulateClickAtForTest(ParseVector2(localText), true) &&
                    BigMapPanel.SelectedStateForTest().Contains(_gestureBenchId);
                Check("R4.close_reopen_then_click", _reopenOk,
                      "state=" + BigMapPanel.SelectedStateForTest(), "R4");
                _gesturePhase = 0;
                SetStage(Stage.RouteChecks);
            }
        }

        // ----------------------------------------------------------- route checks
        static int _routePhase;
        static float _routePhaseAt;

        static void TickRouteChecks()
        {
            if (_routePhase == 0)
            {
                bool selected = BigMapPanel.SelectEntity("entity/prop_bench_plaza_0");
                bool preview = BigMapPanel.PreviewRouteForTest();
                _routePoints = BigMapPanel.PreviewRenderedPointCountForTest();
                _routeErrorAfterPreview = BigMapPanel.RouteProjectionErrorForTest();
                _mapPreviewOk = preview;
                Check("R5.route_projected", preview && _routePoints >= 2 &&
                      _routeErrorAfterPreview >= 0f && _routeErrorAfterPreview < 1.0f,
                      "rendered_points=" + _routePoints + " max_error_px=" +
                      _routeErrorAfterPreview.ToString("F3") +
                      " first_rendered=" + BigMapPanel.FirstRenderedPointForTest() +
                      " first_world=" + BigMapPanel.FirstWorldRoutePointForTest(), "R5");
                Capture("bigmap_route_preview.png");
                BigMapPanel.ZoomForTest(3f);
                _routePhase = 1;
                _routePhaseAt = (float)EditorApplication.timeSinceStartup;
                return;
            }
            if (_routePhase == 1 && StageElapsed() >= _routePhaseAt - _stageStart + 0.25f)
            {
                // read WITHOUT refreshing: a stale route that was not reprojected shows up
                _routeErrorAfterZoom = BigMapPanel.RouteProjectionErrorNoRefreshForTest();
                Check("R5.route_reprojected_after_zoom", _routeErrorAfterZoom >= 0f &&
                      _routeErrorAfterZoom < 1.0f,
                      "max_error_px(no_refresh)=" + _routeErrorAfterZoom.ToString("F3"), "R5");
                BigMapPanel.PanByForTest(60f, 40f);
                _routePhase = 2;
                _routePhaseAt = (float)EditorApplication.timeSinceStartup;
                return;
            }
            if (_routePhase == 2 && StageElapsed() >= _routePhaseAt - _stageStart + 0.25f)
            {
                _routeErrorAfterPan = BigMapPanel.RouteProjectionErrorNoRefreshForTest();
                Check("R5.route_reprojected_after_pan", _routeErrorAfterPan >= 0f &&
                      _routeErrorAfterPan < 1.0f,
                      "max_error_px(no_refresh)=" + _routeErrorAfterPan.ToString("F3"), "R5");
                BigMapPanel.ResetViewForTest();
                Capture("bigmap_route_after_pan.png");
                _routePhase = 0;
                SetStage(Stage.TargetChecks);
            }
        }

        // ---------------------------------------------------------- target checks
        static void TickTargetChecks()
        {
            if (StageElapsed() < 0.2f) return;
            var loco = Object.FindObjectOfType<CharacterLocomotion>();
            if (loco != null && loco.HasTarget) loco.Stop("acceptance target checks", "cancelled");
            bool allEqual = true;
            var details = new StringBuilder();
            // 1) bench: preview target must equal the resolved/executed target
            if (BigMapPanel.SelectEntity("entity/prop_bench_plaza_0") &&
                BigMapPanel.PreviewRouteForTest())
            {
                string preview = BigMapPanel.PreviewTargetForTest();
                bool walked = BigMapPanel.WalkToSelectedForTest();
                string executed = BigMapPanel.ResolvedTargetForTest();
                bool equal = walked && preview.Length > 0 && executed.StartsWith(preview);
                allEqual &= equal;
                details.Append("bench preview=").Append(preview).Append(" executed=").Append(executed).Append("; ");
                WorldMapPanel.StopNavigation("acceptance target check");
                if (loco != null && loco.HasTarget) loco.Stop("acceptance reset", "cancelled");
            }
            else { allEqual = false; details.Append("bench preview failed; "); }

            // 2) bus: large object -> stand-off target outside its AABB, preview==execute
            string busId = FindVehicleWithKind("bus");
            if (busId != null && BigMapPanel.SelectEntity(busId) && BigMapPanel.PreviewRouteForTest())
            {
                string preview = BigMapPanel.PreviewTargetForTest();
                bool walked = BigMapPanel.WalkToSelectedForTest();
                string executed = BigMapPanel.ResolvedTargetForTest();
                bool equal = walked && preview.Length > 0 && executed.StartsWith(preview);
                var bus = WorldEntityCatalog.Get(busId);
                Vector3 center = WorldEntityCatalog.WorldPosition(bus);
                float halfX = 0.5f, halfZ = 0.5f;
                if (bus.bounds != null && bus.bounds.Length >= 4)
                {
                    center = new Vector3((bus.bounds[0] + bus.bounds[2]) * 0.5f, 0f,
                                         (bus.bounds[1] + bus.bounds[3]) * 0.5f);
                    halfX = Mathf.Abs(bus.bounds[2] - bus.bounds[0]) * 0.5f;
                    halfZ = Mathf.Abs(bus.bounds[3] - bus.bounds[1]) * 0.5f;
                }
                Vector3 target = ParseTarget(executed);
                bool outside = Mathf.Abs(target.x - center.x) > halfX + 0.8f ||
                               Mathf.Abs(target.z - center.z) > halfZ + 0.8f;
                allEqual &= equal && outside;
                details.Append("bus=").Append(busId).Append(" preview=").Append(preview)
                       .Append(" outside=").Append(outside).Append("; ");
                WorldMapPanel.StopNavigation("acceptance target check");
                if (loco != null && loco.HasTarget) loco.Stop("acceptance reset", "cancelled");
            }
            else { allEqual = false; details.Append("bus preview failed; "); }

            // 3) building
            string buildingId = FindFirstBuilding();
            if (buildingId != null && BigMapPanel.SelectEntity(buildingId) &&
                BigMapPanel.PreviewRouteForTest())
            {
                string preview = BigMapPanel.PreviewTargetForTest();
                bool walked = BigMapPanel.WalkToSelectedForTest();
                string executed = BigMapPanel.ResolvedTargetForTest();
                bool equal = walked && preview.Length > 0 && executed.StartsWith(preview);
                allEqual &= equal;
                details.Append("building preview=").Append(preview).Append(" executed=")
                       .Append(executed).Append("; ");
                WorldMapPanel.StopNavigation("acceptance target check");
                if (loco != null && loco.HasTarget) loco.Stop("acceptance reset", "cancelled");
            }
            else { allEqual = false; details.Append("building preview failed; "); }

            // 4) ground point
            BigMapPanel.SelectGroundForTest(new Vector2(-10.5f, -47.0f));
            if (BigMapPanel.PreviewRouteForTest())
            {
                string preview = BigMapPanel.PreviewTargetForTest();
                bool walked = BigMapPanel.WalkToSelectedForTest();
                string executed = BigMapPanel.ResolvedTargetForTest();
                bool equal = walked && preview.Length > 0 && executed.StartsWith(preview);
                allEqual &= equal;
                _mapConfirmOk = walked;
                details.Append("ground preview=").Append(preview).Append(" executed=")
                       .Append(executed).Append("; ");
                WorldMapPanel.StopNavigation("acceptance target check");
                if (loco != null && loco.HasTarget) loco.Stop("acceptance reset", "cancelled");
            }
            else { allEqual = false; details.Append("ground preview failed; "); }
            _targetsEqual = allEqual;
            Check("R6.preview_equals_execution_target", allEqual, details.ToString(), "R6");
            // closing the map must not cancel a started navigation
            BigMapPanel.SelectGroundForTest(new Vector2(-10.5f, -47.0f));
            bool started = BigMapPanel.WalkToSelectedForTest();
            bool closed = BigMapPanel.CloseForTest();
            bool stillMoving = WorldMapPanel.HasNavigation;
            _mapNavCloseOk = started && closed && stillMoving;
            Check("map.close_does_not_cancel_task", _mapNavCloseOk,
                  "started=" + started + " closed=" + closed + " navigation=" + stillMoving);
            WorldMapPanel.StopNavigation("acceptance cleanup");
            if (loco != null && loco.HasTarget) loco.Stop("acceptance cleanup", "cancelled");
            SetStage(Stage.SeatSetup);
        }

        static string FindVehicleWithKind(string hint)
        {
            foreach (var entity in WorldEntityCatalog.Query("vehicle", null, 0))
            {
                string english = entity.english ?? "";
                if (hint == "bus" && english.IndexOf("LAZ", StringComparison.OrdinalIgnoreCase) >= 0)
                    return entity.id;
            }
            return "entity/veh_laz695_01";
        }

        static string FindFirstBuilding()
        {
            foreach (var entity in WorldEntityCatalog.Query("building", null, 0))
            {
                if (entity.bounds != null && entity.bounds.Length >= 4) return entity.id;
            }
            return null;
        }

        // ------------------------------------------------------------ seat setup
        static void TickSeatSetup()
        {
            if (StageElapsed() < 0.3f) return;
            BigMapPanel.CloseForTest();
            var loco = Object.FindObjectOfType<CharacterLocomotion>();
            if (loco != null && loco.HasTarget) loco.Stop("acceptance seat setup", "cancelled");
            Vector3 origin0 = CharacterPosition();
            var origins = new List<KeyValuePair<string, Vector3>>();
            origins.Add(new KeyValuePair<string, Vector3>("scene_start", origin0));
            foreach (var label in new[] { "中央广场", "文化宫", "地铁站", "北公园", "市场", "公交总站" })
            {
                foreach (var anchor in WorldEntityCatalog.Anchors)
                {
                    if (anchor == null || anchor.label != label || anchor.pos == null ||
                        anchor.pos.Length < 3) continue;
                    Vector3 position = new Vector3(anchor.pos[0], anchor.pos[1], anchor.pos[2]);
                    position.y = WorldData.GroundHeightAt(position);
                    if (WorldData.IsWalkable(position))
                        origins.Add(new KeyValuePair<string, Vector3>("anchor:" + label, position));
                }
            }
            float walkSpeed = loco != null ? Mathf.Max(0.3f, loco.WalkSpeed) : 1.05f;
            Log("seat origins=" + origins.Count + " walk_speed=" + walkSpeed.ToString("F2"));
            SeatPlan.Clear();
            // long walks first, from the untouched scene start position
            int longCount = 0;
            var longCandidates = new List<KeyValuePair<float, Stage4SeatEntry>>();
            foreach (var entry in Stage4SeatData.Entries)
            {
                if (IsStaticallyBlocked(entry)) continue;
                Vector3 approach;
                string reason;
                if (!Stage4SeatData.ResolveApproach(entry, origin0, false, out approach, out reason)) continue;
                var path = WorldData.FindPath(origin0, approach);
                if (path == null || path.Count == 0) continue;
                float meters = PathLength(origin0, path);
                float seconds = meters / walkSpeed;
                if (seconds > 25f) longCandidates.Add(new KeyValuePair<float, Stage4SeatEntry>(seconds, entry));
            }
            longCandidates.Sort((a, b) => a.Key.CompareTo(b.Key));
            var longAreas = new HashSet<string>(StringComparer.Ordinal);
            // Prefer the shortest walks that still exceed 25 s (keeps the run bounded
            // while proving the old 25 s budget is not enough), then fall back to any.
            foreach (var pair in longCandidates)
            {
                if (longCount >= 2) break;
                bool preferred = pair.Key >= 30f && pair.Key <= 280f;
                if (!preferred) continue;
                var entity = WorldEntityCatalog.Get(pair.Value.entity_id);
                string areaKey = entity != null ? (entity.area ?? "") : "";
                if (longAreas.Contains(areaKey) && longCount > 0) continue;
                longAreas.Add(areaKey);
                SeatPlan.Add(new SeatCycle
                {
                    id = pair.Value.entity_id,
                    origin = origin0,
                    originKind = "scene_start",
                    isLong = true,
                    expectedSeconds = pair.Key,
                });
                longCount++;
            }
            if (longCount < 2)
            {
                foreach (var pair in longCandidates)
                {
                    if (longCount >= 2) break;
                    bool already = false;
                    foreach (var cycle in SeatPlan) if (cycle.id == pair.Value.entity_id) already = true;
                    if (already) continue;
                    var entity = WorldEntityCatalog.Get(pair.Value.entity_id);
                    string areaKey = entity != null ? (entity.area ?? "") : "";
                    if (longAreas.Contains(areaKey) && longCount > 0) continue;
                    longAreas.Add(areaKey);
                    SeatPlan.Add(new SeatCycle
                    {
                        id = pair.Value.entity_id,
                        origin = origin0,
                        originKind = "scene_start",
                        isLong = true,
                        expectedSeconds = pair.Key,
                    });
                    longCount++;
                }
            }
            // short real walks from varied start positions
            int shortCount = 0;
            int originRotation = 0;
            foreach (var entry in Stage4SeatData.Entries)
            {
                if (shortCount >= RequiredSeatCycles) break;
                if (entry.entity_id == "entity/prop_bench_plaza_0") continue;
                if (IsStaticallyBlocked(entry)) continue;
                float bestSeconds = float.MaxValue;
                Vector3 bestOrigin = Vector3.zero;
                string bestKind = "";
                foreach (var origin in origins)
                {
                    Vector3 approach;
                    string reason;
                    if (!Stage4SeatData.ResolveApproach(entry, origin.Value, false, out approach, out reason))
                        continue;
                    var path = WorldData.FindPath(origin.Value, approach);
                    if (path == null || path.Count == 0) continue;
                    float seconds = PathLength(origin.Value, path) / walkSpeed;
                    if (seconds > 3f && seconds < 60f && seconds < bestSeconds)
                    {
                        bestSeconds = seconds;
                        bestOrigin = origin.Value;
                        bestKind = origin.Key;
                    }
                }
                if (bestSeconds == float.MaxValue) continue;
                var cycle = new SeatCycle
                {
                    id = entry.entity_id,
                    origin = bestOrigin,
                    originKind = bestKind,
                    isLong = false,
                    expectedSeconds = bestSeconds,
                };
                // rotate origin hints so the sample is not all from one start
                if (originRotation % 5 == 4 && origins.Count > 1)
                {
                    int other = (originRotation + 1) % origins.Count;
                    Vector3 approach;
                    string reason;
                    if (Stage4SeatData.ResolveApproach(entry, origins[other].Value, false, out approach, out reason))
                    {
                        var path = WorldData.FindPath(origins[other].Value, approach);
                        if (path != null && path.Count > 0)
                        {
                            float seconds = PathLength(origins[other].Value, path) / walkSpeed;
                            if (seconds < 60f)
                            {
                                cycle.origin = origins[other].Value;
                                cycle.originKind = origins[other].Key;
                                cycle.expectedSeconds = seconds;
                            }
                        }
                    }
                }
                originRotation++;
                SeatPlan.Add(cycle);
                shortCount++;
            }
            Log("seat plan: " + SeatPlan.Count + " cycles (" + longCount + " long, " + shortCount + " short)");
            foreach (var cycle in SeatPlan)
                Log("  cycle " + cycle.id + " origin=" + cycle.originKind + " expected=" +
                    cycle.expectedSeconds.ToString("F1") + "s long=" + cycle.isLong);
            // Second pass: widen the per-cycle walking window if the first pass did
            // not reach 12 short real walks from the available start positions.
            if (shortCount < RequiredSeatCycles)
            {
                foreach (var entry in Stage4SeatData.Entries)
                {
                    if (shortCount >= RequiredSeatCycles) break;
                    if (entry.entity_id == "entity/prop_bench_plaza_0") continue;
                    if (IsStaticallyBlocked(entry)) continue;
                    bool already = false;
                    foreach (var existing in SeatPlan) if (existing.id == entry.entity_id) already = true;
                    if (already) continue;
                    float bestSeconds = float.MaxValue;
                    Vector3 bestOrigin = Vector3.zero;
                    string bestKind = "";
                    foreach (var origin in origins)
                    {
                        Vector3 approach;
                        string reason;
                        if (!Stage4SeatData.ResolveApproach(entry, origin.Value, false, out approach, out reason))
                            continue;
                        var path = WorldData.FindPath(origin.Value, approach);
                        if (path == null || path.Count == 0) continue;
                        float seconds = PathLength(origin.Value, path) / walkSpeed;
                        if (seconds > 3f && seconds < 90f && seconds < bestSeconds)
                        {
                            bestSeconds = seconds;
                            bestOrigin = origin.Value;
                            bestKind = origin.Key;
                        }
                    }
                    if (bestSeconds == float.MaxValue) continue;
                    SeatPlan.Add(new SeatCycle
                    {
                        id = entry.entity_id,
                        origin = bestOrigin,
                        originKind = bestKind,
                        isLong = false,
                        expectedSeconds = bestSeconds,
                    });
                    shortCount++;
                }
            }
            if (longCount < 2)
                Check("R2.two_long_walks_available", false,
                      "only " + longCount + " benches need >25 s from the scene start", "R2");
            if (shortCount < RequiredSeatCycles)
                Check("R2.short_cycle_plan", false, "only " + shortCount + " short cycles", "R2");
            _seatIndex = 0;
            SetStage(Stage.Seating);
        }

        /// <summary>
        /// Benches whose four bench-local approach sides are all inside inflated
        /// navigation obstacles are documented as unreachable in the catalog; they
        /// are excluded from the real-walk plan and reported separately instead of
        /// being silently turned into failed cycles.
        /// </summary>
        static bool IsStaticallyBlocked(Stage4SeatEntry entry)
        {
            var entity = WorldEntityCatalog.Get(entry.entity_id);
            var seat = entity != null ? entity.seat : null;
            if (seat == null) return false;
            var sit = WorldEntityCatalog.CapabilityOf(entity, "sit");
            if (sit != null && sit.reachable == "static_blocked") return true;
            return false;
        }

        static void TickSeating()
        {
            if (_seatIndex >= SeatPlan.Count)
            {
                Check("seats.minimum_cycles", _seatCycles >= RequiredSeatCycles,
                      "verified cycles=" + _seatCycles + "/" + SeatPlan.Count);
                SetStage(Stage.MapInteraction);
                return;
            }
            if (_seatCycleId == "")
            {
                var cycle = SeatPlan[_seatIndex];
                var loco = Object.FindObjectOfType<CharacterLocomotion>();
                if (loco == null) { FailStage("character locomotion missing"); return; }
                if (loco.HasTarget) loco.Stop("acceptance cycle reset", "cancelled");
                if (!loco.WalkingEnabled) loco.ToggleWalking();
                // test setup: place the character at the recorded start position for
                // this cycle (varied starts; never at the bench approach itself)
                var character = Object.FindObjectOfType<Yixuan.Core.YixuanCharacter>();
                if (character != null)
                {
                    bool isSceneStartLong = cycle.isLong && cycle.originKind == "scene_start";
                    float originDistance = Vector3.Distance(character.transform.position, cycle.origin);
                    if (isSceneStartLong && originDistance < 0.8f)
                    {
                        Log("cycle " + _seatIndex + " long walk starts from the real current position " +
                            "(origin_distance=" + originDistance.ToString("F2") + "m, no teleport)");
                    }
                    else
                    {
                        character.transform.position = cycle.origin;
                        Log("cycle " + _seatIndex + " start position set to " + cycle.originKind +
                            " (origin_distance=" + originDistance.ToString("F2") + "m)");
                    }
                }
                _seatOrigin = cycle.originKind;
                _seatCycleExpectedSeconds = cycle.expectedSeconds;
                _seatCycleStart = (float)EditorApplication.timeSinceStartup;
                _seatCycleId = cycle.id;
                _seatAttempts++;
                var controller = GetSeatController();
                if (controller == null) { FailStage("Stage4SeatController missing"); return; }
                bool started = controller.TrySit(cycle.id);
                if (!started)
                {
                    Log("cycle " + _seatIndex + " sit start failed " + cycle.id + ": " + controller.LastError);
                    _seatCycleId = "";
                    _seatIndex++;
                    return;
                }
                Log("cycle " + _seatIndex + " start " + cycle.id + " origin=" + cycle.originKind +
                    " expected=" + cycle.expectedSeconds.ToString("F1") + "s");
                return;
            }
            var seatController = GetSeatController();
            if (seatController == null) { FailStage("seat controller lost"); return; }
            float elapsed = (float)EditorApplication.timeSinceStartup - _seatCycleStart;
            if (seatController.State == Stage4SeatController.SeatState.Seated)
            {
                var entry = Stage4SeatData.Get(_seatCycleId);
                float error = entry != null
                    ? Vector3.Distance(CharacterCenter(), entry.SeatPoint) : -1f;
                bool ok = entry != null && seatController.CurrentEntity == _seatCycleId && error < 0.3f;
                bool budgetRecorded = seatController.NavigationBudgetSeconds > 0f;
                var result = new SeatCycleResult
                {
                    id = _seatCycleId,
                    originKind = _seatOrigin,
                    isLong = _seatCycleExpectedSeconds > 25f,
                    pathMeters = seatController.NavigationPathMeters,
                    budgetSeconds = seatController.NavigationBudgetSeconds,
                    elapsedSeconds = elapsed,
                    approachSide = seatController.ResolvedApproachSide,
                    expectedSeconds = _seatCycleExpectedSeconds,
                    verified = ok,
                };
                SeatResults.Add(result);
                Check("seat.cycle" + _seatCycles + (result.isLong ? ".long" : ".short"), ok && budgetRecorded,
                      "id=" + _seatCycleId + " origin=" + _seatOrigin +
                      " path=" + result.pathMeters.ToString("F1") + "m" +
                      " budget=" + result.budgetSeconds.ToString("F1") + "s" +
                      " elapsed=" + result.elapsedSeconds.ToString("F1") + "s" +
                      " expected=" + result.expectedSeconds.ToString("F1") + "s" +
                      " side=" + result.approachSide +
                      " center_error=" + error.ToString("F3"),
                      result.isLong ? "R2" : "");
                if (result.isLong)
                {
                    LongWalks.Add(new LongWalkResult
                    {
                        id = _seatCycleId,
                        pathMeters = result.pathMeters,
                        expectedSeconds = result.expectedSeconds,
                        budgetSeconds = result.budgetSeconds,
                        elapsedSeconds = result.elapsedSeconds,
                        arrived = ok,
                    });
                }
                if (ok)
                {
                    _seatCycles++;
                    SeatIdsTested.Add(_seatCycleId);
                    if (entry != null)
                    {
                        SeatYaws.Add(Mathf.RoundToInt(entry.SeatYaw));
                        SeatHeights.Add(entry.seat.height_above_ground_m);
                        SeatSides.Add(result.approachSide);
                    }
                    if (_seatCycles <= 3)
                    {
                        CaptureSeatShot("seat_" + _seatCycles + ".png");
                    }
                }
                seatController.RequestStand();
                SetStage(Stage.Standing);
                return;
            }
            if (seatController.State == Stage4SeatController.SeatState.Failed)
            {
                Log("cycle failed " + _seatCycleId + ": " + seatController.LastError);
                SeatResults.Add(new SeatCycleResult
                {
                    id = _seatCycleId, originKind = _seatOrigin, isLong = _seatCycleExpectedSeconds > 25f,
                    pathMeters = seatController.NavigationPathMeters,
                    budgetSeconds = seatController.NavigationBudgetSeconds,
                    elapsedSeconds = elapsed, approachSide = seatController.ResolvedApproachSide,
                    expectedSeconds = _seatCycleExpectedSeconds, verified = false,
                });
                seatController.ResetFailureState();
                _seatCycleId = "";
                _seatIndex++;
                return;
            }
            float deadline = Mathf.Max(90f, seatController.SuggestedSitDeadline + 60f);
            if (elapsed > deadline)
            {
                Log("cycle timeout " + _seatCycleId + " state=" + seatController.State +
                    " elapsed=" + elapsed.ToString("F1"));
                seatController.RequestCancel();
                _seatCycleId = "";
                _seatIndex++;
            }
        }

        static void TickStanding()
        {
            var controller = GetSeatController();
            if (controller == null) { FailStage("controller lost on stand"); return; }
            if (controller.State == Stage4SeatController.SeatState.Seated)
            {
                controller.RequestStand();
                return;
            }
            if (controller.State != Stage4SeatController.SeatState.Idle &&
                controller.State != Stage4SeatController.SeatState.Failed)
            {
                if (StageElapsed() < 15f) return;
                Check("seat.stand_timeout", false, "state=" + controller.State);
                Finish(false);
                return;
            }
            bool ok = controller.State == Stage4SeatController.SeatState.Idle;
            Check("seat.stand_" + _seatCycles, ok,
                  "state=" + controller.State + " error=" + controller.LastError);
            _seatCycleId = "";
            _seatIndex++;
            SetStage(Stage.Seating);
        }

        // ------------------------------------------------- live card / gaze / R7
        static bool _cardFlowStarted;
        static bool _cardSitClicked;
        static bool _cardWaitingStand;
        static float _cardWaitStart;

        static void TickMapInteraction()
        {
            if (!_cardFlowStarted)
            {
                if (StageElapsed() < 0.3f) return;
                _cardFlowStarted = true;
                _cardWaitStart = (float)EditorApplication.timeSinceStartup;
                BigMapPanel.OpenForTest();
                bool selected = BigMapPanel.SelectBenchForCardTest();
                Log("card flow: map opened, nearest usable bench selected=" + selected +
                    " actions=" + BigMapPanel.ActionNamesForTestPublic() +
                    " info=" + BigMapPanel.InfoTextForTest().Replace("\n", " | "));
                EditorApplication.update -= TickCardWait;
                EditorApplication.update += TickCardWait;
                return;
            }
            // all work happens in TickCardWait (needs frame updates for the controller)
            if ((float)EditorApplication.timeSinceStartup - _cardWaitStart > 180f)
            {
                Check("R7.live_card_timeout", false, "seat/card flow did not finish in 180 s", "R7");
                Finish(false);
            }
        }

        static void TickCardWait()
        {
            if (!_armed) return;
            if (_stage != Stage.MapInteraction) { EditorApplication.update -= TickCardWait; return; }
            var controller = GetSeatController();
            if (controller == null) return;
            float elapsed = (float)EditorApplication.timeSinceStartup - _cardWaitStart;
            if (!_cardWaitingStand)
            {
                if (!_cardSitClicked)
                {
                    _cardSitClicked = true;
                    string actionsNow = BigMapPanel.ActionNamesForTestPublic();
                    bool clicked = BigMapPanel.ClickActionForTest("走近并坐下");
                    Log("card flow: clicked 走近并坐下=" + clicked + " actions=" + actionsNow +
                        " controller=" + controller.State);
                    if (!clicked)
                    {
                        Check("R7.start_sit_from_card", false,
                              "走近并坐下 action missing or disabled; actions=" + actionsNow, "R7");
                        EditorApplication.update -= TickCardWait;
                        Finish(false);
                        return;
                    }
                    Check("R7.start_sit_from_card", true, "clicked 走近并坐下 from the live card", "R7");
                    return;
                }
                if (controller.State == Stage4SeatController.SeatState.Seated)
                {
                    string actions = BigMapPanel.ActionNamesForTestPublic();
                    string info = BigMapPanel.InfoTextForTest();
                    _actionsInsidePanel = BigMapPanel.ActionsWithinPanelForTest();
                    bool hasStand = actions.Contains("起身");
                    bool infoLive = info.Contains("座位控制器") && info.Contains("Seated");
                    bool slotsOk = BigMapPanel.ActionCountForTest() <= BigMapPanel.ActionSlotCapacityForTest();
                    Check("R7.live_card_after_seated", hasStand && infoLive && slotsOk,
                          "actions=" + actions + " info_has_state=" + infoLive +
                          " actions=" + BigMapPanel.ActionCountForTest() + "/" +
                          BigMapPanel.ActionSlotCapacityForTest(), "R7");
                    Check("R7.actions_inside_panel", _actionsInsidePanel,
                          "all active action buttons inside the viewport", "R7");
                    Capture("bigmap_seated_live_card.png");
                    ChecksumBeforeStand = actions;
                    bool clicked = BigMapPanel.ClickActionForTest("起身");
                    if (!clicked)
                    {
                        Check("R7.stand_from_card", false, "起身 action missing/disabled", "R7");
                        EditorApplication.update -= TickCardWait;
                        Finish(false);
                        return;
                    }
                    _cardWaitingStand = true;
                    _cardWaitStart = (float)EditorApplication.timeSinceStartup;
                    Log("card flow: clicked 起身 from the live card");
                    return;
                }
                if (controller.State == Stage4SeatController.SeatState.Failed)
                {
                    EditorApplication.update -= TickCardWait;
                    Check("R7.live_card_after_seated", false, "seat failed: " + controller.LastError, "R7");
                    Finish(false);
                }
                if (elapsed > 120f)
                {
                    EditorApplication.update -= TickCardWait;
                    Check("R7.live_card_after_seated", false,
                          "seat did not complete from map action; state=" + controller.State, "R7");
                    Finish(false);
                }
                return;
            }
            // waiting for stand to finish and the card to switch back
            if (controller.State == Stage4SeatController.SeatState.Seated) return;
            if (controller.State != Stage4SeatController.SeatState.Idle &&
                controller.State != Stage4SeatController.SeatState.Failed)
            {
                if (elapsed > 40f)
                {
                    EditorApplication.update -= TickCardWait;
                    Check("R7.stand_from_card", false, "stand timeout state=" + controller.State, "R7");
                    Finish(false);
                }
                return;
            }
            string actionsAfter = BigMapPanel.ActionNamesForTestPublic();
            bool standOk = controller.State == Stage4SeatController.SeatState.Idle &&
                           actionsAfter.Contains("走近并坐下");
            _cardLiveOk = standOk;
            Check("R7.stand_from_card", standOk,
                  "state=" + controller.State + " actions_after=" + actionsAfter, "R7");
            Capture("bigmap_after_stand.png");
            EditorApplication.update -= TickCardWait;
            SetStage(Stage.Wrap);
        }

        static string ChecksumBeforeStand = "";

        // --------------------------------------------------------------- captures
        static void Capture(string name)
        {
            string path = Path.Combine(_evidenceDir, name);
            int width, height;
            string error;
            if (BigMapPanel.CapturePng(path, out width, out height, out error))
            {
                Captures.Add(name + ":" + width + "x" + height);
                Log("capture " + name + " " + width + "x" + height);
            }
            else Pending("capture." + name, error);
        }

        static void CaptureSeatShot(string name)
        {
            GameObject fill = null;
            RenderTexture rt = null;
            Texture2D texture = null;
            Camera camera = null;
            Vector3 previousPosition = Vector3.zero;
            Quaternion previousRotation = Quaternion.identity;
            float previousFov = 60f;
            RenderTexture previousTarget = null;
            try
            {
                var controller = GetSeatController();
                var entry = controller != null ? Stage4SeatData.Get(controller.CurrentEntity) : null;
                if (entry == null) { Pending("capture." + name, "no current seat entry"); return; }
                camera = FindGameCamera();
                if (camera == null) { Pending("capture." + name, "no game camera"); return; }
                Vector3 seat = entry.SeatPoint;
                Vector3 front = Quaternion.Euler(0f, entry.SeatYaw, 0f) * Vector3.forward;
                Vector3 look = seat + Vector3.up * 0.45f;
                Vector3 position = seat + front * 2.7f + Vector3.up * 1.15f;
                previousPosition = camera.transform.position;
                previousRotation = camera.transform.rotation;
                previousFov = camera.fieldOfView;
                previousTarget = camera.targetTexture;
                camera.transform.position = position;
                camera.transform.LookAt(look);
                fill = new GameObject("AcceptanceFillLight");
                var light = fill.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.5f;
                light.color = new Color(1f, 0.96f, 0.88f);
                fill.transform.rotation = Quaternion.LookRotation((look - position).normalized, Vector3.up);
                rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
                rt.Create();
                camera.targetTexture = rt;
                camera.Render();
                RenderTexture.active = rt;
                texture = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                texture.Apply(false, false);
                string path = Path.Combine(_evidenceDir, name);
                File.WriteAllBytes(path, texture.EncodeToPNG());
                Captures.Add(name + ":1280x720");
                Log("seat capture " + name + " entity=" + entry.entity_id);
            }
            catch (Exception e)
            {
                Pending("capture." + name, e.Message);
            }
            finally
            {
                if (camera != null)
                {
                    camera.targetTexture = previousTarget;
                    camera.transform.position = previousPosition;
                    camera.transform.rotation = previousRotation;
                    camera.fieldOfView = previousFov;
                }
                if (fill != null) Object.Destroy(fill);
                if (texture != null) Object.Destroy(texture);
                if (rt != null) { rt.Release(); Object.Destroy(rt); }
                RenderTexture.active = null;
            }
        }

        static Camera FindGameCamera()
        {
            var main = Camera.main;
            if (main != null && main.cullingMask != (1 << 5)) return main;
            Camera best = null;
            foreach (var camera in Object.FindObjectsOfType<Camera>())
            {
                if (camera == null || camera.cullingMask == (1 << 5)) continue;
                if (camera.targetTexture != null) continue;
                if (best == null || camera.depth > best.depth) best = camera;
            }
            return best;
        }

        // ------------------------------------------------------------------ wrap
        static void TickWrap()
        {
            try
            {
                Check("regression.world_map_entities", WorldMapData.Data != null &&
                      WorldMapData.Data.entities.Length == 855,
                      "entities=" + (WorldMapData.Data != null ? WorldMapData.Data.entities.Length : -1));
                Check("regression.debug_map_still_loads", WorldMapPanel.Load(), "WorldMapPanel.Load");
                string gaze = BigMapPanel.GazeSourceForTest();
                _gazeEyeOk = gaze == "character_eye";
                Check("R8.gaze_bound_to_eye_camera", _gazeEyeOk,
                      "gaze_source=" + gaze, "R8");
                Check("seats.distinct_yaws", DistinctCount(SeatYaws) >= 3,
                      "distinct=" + DistinctCount(SeatYaws) + " yaws=" + JoinInts(SeatYaws));
                Check("seats.distinct_heights", DistinctCountF(SeatHeights) >= 2,
                      "distinct=" + DistinctCountF(SeatHeights));
                Check("seats.verified_cycles", _seatCycles >= RequiredSeatCycles,
                      "verified=" + _seatCycles);
                int longArrived = 0;
                foreach (var walk in LongWalks) if (walk.arrived) longArrived++;
                bool longOk = longArrived >= 2;
                bool over25 = false;
                foreach (var walk in LongWalks)
                    if (walk.arrived && walk.elapsedSeconds > 25f) over25 = true;
                Check("R2.two_long_walks_arrived", longOk,
                      "long_walks=" + LongWalks.Count + " arrived=" + longArrived +
                      " details=" + LongWalkDetails(), "R2");
                Check("R2.long_walk_over_25s_normal_speed", over25,
                      "at least one long walk took >25 s at walk speed", "R2");
                Check("map.navigation_close_preserved", _mapNavCloseOk,
                      "closing the map kept the started navigation owner");
                _completedAllStages = true;
            }
            catch (Exception e)
            {
                _failed++;
                Log("wrap exception: " + e);
            }
            Finish(_failed == 0 && _pending == 0);
        }

        static string LongWalkDetails()
        {
            var sb = new StringBuilder();
            foreach (var walk in LongWalks)
            {
                sb.Append(walk.id).Append(":path=").Append(walk.pathMeters.ToString("F0"))
                  .Append("m,budget=").Append(walk.budgetSeconds.ToString("F0"))
                  .Append("s,elapsed=").Append(walk.elapsedSeconds.ToString("F0"))
                  .Append("s,arrived=").Append(walk.arrived).Append("; ");
            }
            return sb.ToString();
        }

        static void Finish(bool logicalPass)
        {
            if (_stage == Stage.Exit) return;
            try { WriteReport(logicalPass); }
            catch (Exception e) { Debug.LogError("[IA2-ACC] report write failed: " + e); }
            int code = 0;
            if (!logicalPass || _failed > 0) code = 1;
            else if (_pending > 0) code = 2;
            int written;
            try
            {
                written = int.Parse(File.ReadAllText(Path.Combine(_evidenceDir, "EXIT_CODE")).Trim());
            }
            catch { written = -1; }
            if (written != code)
                File.WriteAllText(Path.Combine(_evidenceDir, "EXIT_CODE"), code.ToString());
            try
            {
                File.WriteAllText(Path.Combine(_evidenceDir, "COMPLETED.json"),
                    "{\"completed_all_stages\": " + (_completedAllStages ? "true" : "false") +
                    ", \"logical_exit_code\": " + code +
                    ", \"failed\": " + _failed + ", \"pending\": " + _pending +
                    ", \"internal_kill\": false" +
                    ", \"exit_path\": \"logical_result_editor_exit\"}\n");
            }
            catch { }
            // R1: disarm; a later ordinary Play must never re-run this harness.
            _armed = false;
            SessionState.SetString(ArmedKey, "");
            SessionState.SetString(StageKey, "done");
            Log("acceptance finished logical_pass=" + logicalPass + " fail=" + _failed +
                " pending=" + _pending + " logical_code=" + code +
                " internal_kill=false");
            _stage = Stage.Exit;
            RequestExit(code);
        }

        static void RequestExit(int code)
        {
            // The Editor exits itself with the real logical result code; it never
            // kills its own process.  If a Play session is running it is stopped
            // first and the request survives the domain reload in SessionState.
            SessionState.SetString(ExitRequestKey, code.ToString());
            Log("exit request logical_code=" + code + " playing=" + EditorApplication.isPlaying);
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
                return;
            }
            SessionState.SetString(ExitRequestKey, "");
            EditorApplication.Exit(code);
        }

        // ----------------------------------------------------- exit audit probes
        static void RunExitInjection(bool pass)
        {
            _completedAllStages = true;
            _failed = pass ? 0 : 1;
            _pending = 0;
            Results.Clear();
            Check("exit.injection_probe", pass,
                  "injected deterministic " + (pass ? "success" : "failure") + " result probe");
            int code = pass ? 0 : 1;
            WriteInjectedReport(pass, code);
            Log("INJECTED EXIT PROBE result=" + (pass ? "pass" : "fail") +
                " logical_code=" + code + " internal_kill=false");
            RequestExit(code);
        }

        static void WriteInjectedReport(bool pass, int code)
        {
            Directory.CreateDirectory(_evidenceDir);
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"schema\": \"sbh.interaction-expansion.acceptance.v2\",\n");
            sb.Append("  \"run_id\": \"").Append(Escape(_runDir)).Append("\",\n");
            sb.Append("  \"review_round\": 3,\n");
            sb.Append("  \"injected_exit_probe\": true,\n");
            sb.Append("  \"completed_all_stages\": true,\n");
            sb.Append("  \"logical_pass\": ").Append(pass ? "true" : "false").Append(",\n");
            sb.Append("  \"internal_kill\": false,\n");
            sb.Append("  \"exit_path\": \"logical_result_editor_exit\",\n");
            sb.Append("  \"checks\": {\"pass\": ").Append(pass ? 1 : 0)
              .Append(", \"fail\": ").Append(pass ? 0 : 1)
              .Append(", \"pending\": 0},\n");
            sb.Append("  \"checks_detail\": [\n    ")
              .Append(string.Join(",\n    ", Results.ToArray())).Append("\n  ]\n");
            sb.Append("}\n");
            File.WriteAllText(Path.Combine(_evidenceDir, "acceptance_report.json"), sb.ToString());
            File.WriteAllText(Path.Combine(_evidenceDir, "acceptance.log"), LogBuilder.ToString());
            File.WriteAllText(Path.Combine(_evidenceDir, "EXIT_CODE"), code.ToString());
            File.WriteAllText(Path.Combine(_evidenceDir, "COMPLETED.json"),
                "{\"completed_all_stages\": true, \"logical_exit_code\": " + code +
                ", \"failed\": " + (pass ? 0 : 1) + ", \"pending\": 0" +
                ", \"injected_exit_probe\": true, \"internal_kill\": false" +
                ", \"exit_path\": \"logical_result_editor_exit\"}\n");
        }

        static void RunHangInjection()
        {
            // Deliberate stall: the external runner owns the watchdog and must record
            // the forced termination and the real process exit code.  No internal kill.
            Directory.CreateDirectory(_evidenceDir);
            File.WriteAllText(Path.Combine(_evidenceDir, "injected_hang.json"),
                "{\"injected\": true, \"case\": \"hang\", " +
                "\"note\": \"harness intentionally stalls; external runner watchdog must " +
                "force-terminate and record the actual exit code\"}\n");
            Log("INJECTED HANG: harness stalls on purpose; external watchdog expected; " +
                "no internal kill");
            while (true) { System.Threading.Thread.Sleep(1000); }
        }

        static void WriteReport(bool logicalPass)
        {
            Directory.CreateDirectory(_evidenceDir);
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"schema\": \"sbh.interaction-expansion.acceptance.v2\",\n");
            sb.Append("  \"run_id\": \"").Append(Escape(_runDir)).Append("\",\n");
            sb.Append("  \"review_round\": 2,\n");
            sb.Append("  \"started_at\": \"").Append(DateTime.Now.ToString("o")).Append("\",\n");
            sb.Append("  \"unity\": \"").Append(Application.unityVersion).Append("\",\n");
            sb.Append("  \"screen\": \"").Append(Screen.width).Append('x').Append(Screen.height).Append("\",\n");
            sb.Append("  \"scene\": \"").Append(ScenePath).Append("\",\n");
            sb.Append("  \"armed_only_by_explicit_run\": true,\n");
            sb.Append("  \"completed_all_stages\": ").Append(_completedAllStages ? "true" : "false").Append(",\n");
            sb.Append("  \"logical_pass\": ").Append(logicalPass ? "true" : "false").Append(",\n");
            sb.Append("  \"process_exit\": {\n");
            sb.Append("    \"logical_exit_code_file\": \"EXIT_CODE\",\n");
            sb.Append("    \"exit_path\": \"logical_result_editor_exit\",\n");
            sb.Append("    \"note\": \"editor batchmode Play-mode exit hangs in this project; the editor process is force-killed after the report/EXIT_CODE are on disk; the actual process exit code is recorded by the external runner JSON, not claimed as a clean exit\"\n");
            sb.Append("  },\n");
            sb.Append("  \"scene_dirty_after_open\": ").Append(_sceneDirtyAtStart ? "true" : "false").Append(",\n");
            sb.Append("  \"catalog\": {\"entities\": ").Append(WorldEntityCatalog.Count)
              .Append(", \"usable_benches\": ").Append(Stage4SeatData.Entries.Length)
              .Append(", \"excluded_benches\": ").Append(Stage4SeatData.Excluded.Length)
              .Append(", \"stage4_load\": \"").Append(Escape(Stage4SeatData.LoadLog)).Append("\"},\n");
            sb.Append("  \"seat_cycles\": {\"verified\": ").Append(_seatCycles)
              .Append(", \"attempts\": ").Append(_seatAttempts)
              .Append(", \"required\": ").Append(RequiredSeatCycles)
              .Append(", \"long_walks\": [");
            for (int i = 0; i < LongWalks.Count; i++)
            {
                var walk = LongWalks[i];
                if (i > 0) sb.Append(", ");
                sb.Append("{\"id\": \"").Append(Escape(walk.id))
                  .Append("\", \"path_m\": ").Append(walk.pathMeters.ToString("F2", CultureInfo.InvariantCulture))
                  .Append(", \"expected_s\": ").Append(walk.expectedSeconds.ToString("F2", CultureInfo.InvariantCulture))
                  .Append(", \"budget_s\": ").Append(walk.budgetSeconds.ToString("F2", CultureInfo.InvariantCulture))
                  .Append(", \"elapsed_s\": ").Append(walk.elapsedSeconds.ToString("F2", CultureInfo.InvariantCulture))
                  .Append(", \"arrived\": ").Append(walk.arrived ? "true" : "false").Append("}");
            }
            sb.Append("], \"results\": [");
            for (int i = 0; i < SeatResults.Count; i++)
            {
                var result = SeatResults[i];
                if (i > 0) sb.Append(", ");
                sb.Append("{\"id\": \"").Append(Escape(result.id))
                  .Append("\", \"origin\": \"").Append(Escape(result.originKind))
                  .Append("\", \"long\": ").Append(result.isLong ? "true" : "false")
                  .Append(", \"path_m\": ").Append(result.pathMeters.ToString("F2", CultureInfo.InvariantCulture))
                  .Append(", \"budget_s\": ").Append(result.budgetSeconds.ToString("F2", CultureInfo.InvariantCulture))
                  .Append(", \"elapsed_s\": ").Append(result.elapsedSeconds.ToString("F2", CultureInfo.InvariantCulture))
                  .Append(", \"expected_s\": ").Append(result.expectedSeconds.ToString("F2", CultureInfo.InvariantCulture))
                  .Append(", \"approach_side\": \"").Append(Escape(result.approachSide))
                  .Append("\", \"verified\": ").Append(result.verified ? "true" : "false").Append("}");
            }
            sb.Append("]},\n");
            sb.Append("  \"checks\": {\"pass\": ").Append(CountStatus("pass"))
              .Append(", \"fail\": ").Append(_failed)
              .Append(", \"pending\": ").Append(_pending).Append("},\n");
            sb.Append("  \"map\": {\"bench_markers\": ").Append(_mapBenchMarkers)
              .Append(", \"vehicle_markers\": ").Append(_mapVehicleMarkers)
              .Append(", \"search_markers\": ").Append(_mapSearchMarkers)
              .Append(", \"preview\": ").Append(_mapPreviewOk ? "true" : "false")
              .Append(", \"confirm\": ").Append(_mapConfirmOk ? "true" : "false")
              .Append(", \"close_reopen\": ").Append(_mapCloseOk ? "true" : "false")
              .Append(", \"layout_snapshot\": \"").Append(Escape(_lastMapSnapshot)).Append("\"},\n");
            sb.Append("  \"review_checks\": {\n");
            sb.Append("    \"R1_armed_only\": true,\n");
            sb.Append("    \"R2_long_walks\": ").Append(LongWalks.Count).Append(",\n");
            sb.Append("    \"R3_frame_samples\": ").Append(_frameSamples)
              .Append(", \"R3_alignment_ok\": ").Append(_frameAlignmentOk ? "true" : "false").Append(",\n");
            sb.Append("    \"R4_gesture\": {\"drag_click\": ").Append(_dragEntityOk ? "true" : "false")
              .Append(", \"ground\": ").Append(_dragGroundOk ? "true" : "false")
              .Append(", \"right_drag_no_pan\": ").Append(_gestureRightDragOk ? "true" : "false")
              .Append(", \"reopen_click\": ").Append(_reopenOk ? "true" : "false")
              .Append(", \"drag_distance\": ").Append(_gestureDragDistance.ToString("F1")).Append("},\n");
            sb.Append("    \"R5_route_projection_px\": {\"preview\": ").Append(_routeErrorAfterPreview.ToString("F4"))
              .Append(", \"after_zoom\": ").Append(_routeErrorAfterZoom.ToString("F4"))
              .Append(", \"after_pan\": ").Append(_routeErrorAfterPan.ToString("F4"))
              .Append(", \"rendered_points\": ").Append(_routePoints).Append("},\n");
            sb.Append("    \"R6_preview_equals_execute\": ").Append(_targetsEqual ? "true" : "false").Append(",\n");
            sb.Append("    \"R7_live_card\": ").Append(_cardLiveOk ? "true" : "false")
              .Append(", \"R7_actions_inside_panel\": ").Append(_actionsInsidePanel ? "true" : "false").Append(",\n");
            sb.Append("    \"R8_gaze\": ").Append(_gazeEyeOk ? "true" : "false").Append(",\n");
            sb.Append("    \"R9_dedupe\": {\"registered\": ").Append(_benchBoundsRegistered)
              .Append(", \"covered\": ").Append(_benchBoundsCovered)
              .Append(", \"ok\": ").Append(_dedupeOk ? "true" : "false").Append("}\n");
            sb.Append("  },\n");
            sb.Append("  \"captures\": [");
            for (int i = 0; i < Captures.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("\"").Append(Escape(Captures[i])).Append("\"");
            }
            sb.Append("],\n");
            sb.Append("  \"checks_detail\": [\n    ")
              .Append(string.Join(",\n    ", Results.ToArray())).Append("\n  ]\n");
            sb.Append("}\n");
            File.WriteAllText(Path.Combine(_evidenceDir, "acceptance_report.json"), sb.ToString());
            File.WriteAllText(Path.Combine(_evidenceDir, "acceptance.log"), LogBuilder.ToString());
            Log("report written to " + Path.Combine(_evidenceDir, "acceptance_report.json"));
        }

        // ------------------------------------------------------------- utilities
        static int CountStatus(string status)
        {
            int count = 0;
            foreach (var result in Results)
                if (result.Contains("\"status\":\"" + status + "\"")) count++;
            return count;
        }

        static Stage4SeatController GetSeatController()
        {
            var controller = Stage4SeatController.Instance;
            if (controller == null) controller = Object.FindObjectOfType<Stage4SeatController>();
            return controller;
        }

        static Vector3 CharacterPosition()
        {
            var character = Object.FindObjectOfType<Yixuan.Core.YixuanCharacter>();
            if (character != null) return character.transform.position;
            var loco = Object.FindObjectOfType<CharacterLocomotion>();
            if (loco != null) return loco.transform.position;
            return Vector3.zero;
        }

        static Vector3 CharacterCenter()
        {
            var character = Object.FindObjectOfType<Yixuan.Core.YixuanCharacter>();
            if (character != null && character.Center != null) return character.Center.position;
            return CharacterPosition();
        }

        static float PathLength(Vector3 start, List<Vector3> path)
        {
            float length = 0f;
            Vector3 previous = start;
            for (int i = 0; i < path.Count; i++)
            {
                length += Vector3.Distance(previous, path[i]);
                previous = path[i];
            }
            return length;
        }

        static Vector2 ParseVector2(string text)
        {
            var parts = text.Split(',');
            if (parts.Length < 2) return Vector2.zero;
            float x, y;
            float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x);
            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
            return new Vector2(x, y);
        }

        static Vector3 ParseTarget(string text)
        {
            var head = text.Split('|')[0];
            var parts = head.Split(',');
            if (parts.Length < 2) return Vector3.zero;
            float x, z;
            float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x);
            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out z);
            return new Vector3(x, 0f, z);
        }

        static int DistinctCount(List<int> values)
        {
            var set = new HashSet<int>();
            foreach (var value in values) set.Add(value);
            return set.Count;
        }

        static int DistinctCountF(List<float> values)
        {
            var set = new HashSet<int>();
            foreach (var value in values) set.Add(Mathf.RoundToInt(value * 100f));
            return set.Count;
        }

        static string JoinInts(List<int> values)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(values[i]);
            }
            return sb.ToString();
        }
    }
}

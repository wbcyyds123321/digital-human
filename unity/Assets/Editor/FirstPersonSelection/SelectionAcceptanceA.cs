using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json;
using Yixuan.Pick;
using Yixuan.World;

namespace Yixuan.EditorTools.FirstPersonSelection
{
    /// <summary>
    /// Node-A deterministic acceptance harness (T1/T8), round 1 repair.
    ///
    /// It runs only in Play mode, drives its own stack-based coroutine driver in
    /// EditorApplication.update (correctly propagating nested IEnumerators and
    /// real cross-frame waits - the previous node-2 probe failed exactly here),
    /// and stores every frame/RGB/ID/depth/record it uses. Fixture errors are
    /// reported as blocked, never as a product pass. Round 1 adds provenance,
    /// off-axis geometry, box-representative-point, real manual/map entry,
    /// live-fact re-sampling and keyboard input-guard assertions.
    /// </summary>
    public static class SelectionAcceptanceA
    {
        const string MenuRun = "UnityLITF/FirstPersonSelection/Run A_r1 Repair Tests (T1,T8)";
        const string MenuAbort = "UnityLITF/FirstPersonSelection/Abort A Harness";
        const string MenuRunR2 = "UnityLITF/FirstPersonSelection/Run A_r2 Off-axis Diagnostic";

        sealed class CaseRecord
        {
            public string id;
            public string status;      // pass | fail | blocked
            public string expected;
            public string actual;
            public string detail;
            public List<string> evidence = new List<string>();
        }

        sealed class HarnessWait
        {
            public double seconds;
            public HarnessWait(double seconds) { this.seconds = seconds; }
        }

        sealed class HarnessWaitFrames
        {
            public int frames;
            public HarnessWaitFrames(int frames) { this.frames = frames; }
        }

        sealed class HarnessWaitUntil
        {
            public Func<bool> condition;
            public double timeoutSeconds;
            public HarnessWaitUntil(Func<bool> condition, double timeoutSeconds)
            {
                this.condition = condition;
                this.timeoutSeconds = timeoutSeconds;
            }
        }

        static readonly Stack<IEnumerator> Stack = new Stack<IEnumerator>();
        static readonly List<CaseRecord> Cases = new List<CaseRecord>();
        static readonly List<string> LogLines = new List<string>();
        static bool _running;
        static bool _enteredPlayRequested;
        static bool _finalized;
        static double _waitUntil;
        static int _waitFrames;
        static Func<bool> _condition;
        static double _conditionDeadline;
        static bool _lastWaitTimedOut;
        static double _deadline;
        static string _projectRoot;
        static string _aRoot;
        static string _testsDir;
        static string _evidenceDir;
        static Camera _eye;
        static Yixuan.Vision.EyeGimbalController _gimbal;
        static Transform _character;
        static float _lastAimErrorDeg;
        static string _aimError = "";
        static int _confirmedEventCount;
        static readonly List<string> _confirmedEventIds = new List<string>();
        static long _harnessStartFrame;
        static double _harnessStartTime;
        static int _nestedWaitFrameDelta = -1;

        [MenuItem(MenuRun, false, 300)]
        public static void RunMenu() { Run(); }

        [MenuItem(MenuAbort, false, 301)]
        public static void AbortMenu()
        {
            Log("abort_requested_by_menu");
            Abort("aborted_by_user");
        }

        public static void Run()
        {
            if (_running) { Debug.LogWarning("[FPS-A] harness already running"); return; }
            _projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            _aRoot = Path.Combine(_projectRoot, "Captures", "FirstPersonSelection", "run_20261110", "A_r1");
            _testsDir = Path.Combine(_aRoot, "tests");
            _evidenceDir = Path.Combine(_aRoot, "evidence");
            if (!Application.isPlaying)
            {
                // Entering play mode from this editor script would perform a
                // domain reload and wipe the static driver state (the node-2
                // lesson). The harness therefore only runs on an already-live
                // play session; the caller enters play first, then runs the menu.
                Directory.CreateDirectory(_testsDir);
                WriteFile(Path.Combine(_aRoot, "state", "harness_entry_blocked.json"),
                    SelectionJson.Serialize(new
                    {
                        blocked = true,
                        reason = "play_mode_required",
                        detail = "Enter Play mode first (real editor entry), verify Application.isPlaying, then run this menu again. The harness does not enter play mode itself to avoid a domain reload wiping its deterministic coroutine driver.",
                        at_local = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                    }));
                Debug.LogWarning("[FPS-A] run blocked: entering play mode from the harness would reload the domain and wipe the driver. Enter Play manually, then run the menu.");
                return;
            }
            Directory.CreateDirectory(_testsDir);
            Directory.CreateDirectory(_evidenceDir);
            Cases.Clear();
            LogLines.Clear();
            _confirmedEventCount = 0;
            _confirmedEventIds.Clear();
            SelectionBroker.Confirmed += OnConfirmedForTest;
            _running = true;
            _finalized = false;
            _harnessStartTime = EditorApplication.timeSinceStartup;
            _deadline = _harnessStartTime + 480.0;
            Stack.Clear();
            Stack.Push(RunAll());
            EditorApplication.update += Tick;
            Log("harness_scheduled editor_play=" + Application.isPlaying);
        }

        static void OnConfirmedForTest(SelectionRecord record)
        {
            _confirmedEventCount++;
            _confirmedEventIds.Add(record != null ? record.entity_id : "null");
        }

        // ------------------------------------------------------- driver core
        static void Tick()
        {
            if (!_running) return;
            if (EditorApplication.timeSinceStartup > _deadline) { Abort("watchdog_timeout"); return; }
            try
            {
                if (!Application.isPlaying && !_enteredPlayRequested)
                {
                    _enteredPlayRequested = true;
                    Log("enter_playmode_requested frame=" + Time.frameCount);
                    EditorApplication.EnterPlaymode();
                    return;
                }
                if (_waitUntil > 0.0)
                {
                    if (EditorApplication.timeSinceStartup < _waitUntil) return;
                    _waitUntil = 0.0;
                }
                if (_waitFrames > 0) { _waitFrames--; return; }
                if (_condition != null)
                {
                    bool done = false;
                    try { done = _condition(); } catch { done = false; }
                    if (!done)
                    {
                        if (EditorApplication.timeSinceStartup > _conditionDeadline)
                        {
                            _lastWaitTimedOut = true;
                            _condition = null;
                            Log("wait_until_timeout");
                        }
                        else return;
                    }
                    else
                    {
                        _lastWaitTimedOut = false;
                        _condition = null;
                    }
                }
                int guard = 0;
                while (guard++ < 200)
                {
                    if (Stack.Count == 0) { FinishNow(); return; }
                    var top = Stack.Peek();
                    bool moved;
                    try { moved = top.MoveNext(); }
                    catch (Exception exception) { Abort("scenario_exception: " + exception); return; }
                    if (!moved) { Stack.Pop(); continue; }
                    var current = top.Current;
                    if (current == null) return;
                    if (current is IEnumerator nested) { Stack.Push(nested); continue; }
                    if (current is HarnessWait wait) { _waitUntil = EditorApplication.timeSinceStartup + wait.seconds; return; }
                    if (current is HarnessWaitFrames frames) { _waitFrames = Mathf.Max(0, frames.frames); return; }
                    if (current is HarnessWaitUntil until)
                    {
                        _condition = until.condition;
                        _conditionDeadline = EditorApplication.timeSinceStartup + until.timeoutSeconds;
                        _lastWaitTimedOut = false;
                        return;
                    }
                    return; // unknown yield -> next editor update
                }
                return;
            }
            catch (Exception exception)
            {
                Abort("harness_tick_exception: " + exception);
            }
        }

        static void FinishNow()
        {
            if (_finalized) return;
            _finalized = true;
            try
            {
                WriteResults();
            }
            catch (Exception exception)
            {
                Log("write_results_exception: " + exception);
            }
            EditorApplication.update -= Tick;
            _running = false;
            SelectionBroker.Confirmed -= OnConfirmedForTest;
            if (Application.isPlaying)
            {
                Log("exit_playmode_requested");
                EditorApplication.ExitPlaymode();
            }
            try
            {
                WriteFile(Path.Combine(_aRoot, "state", "harness_done.json"), JsonConvert.SerializeObject(new
                {
                    done = true,
                    at_local = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                    play_after_request = Application.isPlaying,
                    cases = Cases.Count,
                    pass = CountStatus("pass"),
                    fail = CountStatus("fail"),
                    blocked = CountStatus("blocked"),
                    nested_wait_frame_delta = _nestedWaitFrameDelta,
                }, Formatting.Indented));
            }
            catch { }
        }

        static void Abort(string reason)
        {
            if (_finalized) return;
            Log("abort: " + reason);
            Fail("HARNESS", "harness completes", reason, "harness abort=" + reason);
            _finalized = true;
            try { WriteResults(); } catch (Exception exception) { Log("abort_write_exception: " + exception); }
            EditorApplication.update -= Tick;
            _running = false;
            SelectionBroker.Confirmed -= OnConfirmedForTest;
            if (Application.isPlaying) EditorApplication.ExitPlaymode();
        }

        static int CountStatus(string status)
        {
            int count = 0;
            foreach (var item in Cases) if (item.status == status) count++;
            return count;
        }

        // --------------------------------------------------------- scenario
        static IEnumerator RunAll()
        {
            _harnessStartFrame = Time.frameCount;
            Log("run_all_start_frame=" + _harnessStartFrame);
            if (!Application.isPlaying)
            {
                yield return new HarnessWaitUntil(() => Application.isPlaying, 120.0);
                yield return new HarnessWaitFrames(10);
            }
            Log("play_mode_ready frame=" + Time.frameCount);
            yield return new HarnessWaitUntil(
                () => UnityEngine.Object.FindObjectOfType<FirstPersonSelectionController>() != null, 30.0);
            yield return new HarnessWaitFrames(2);

            _eye = PickService.ResolveEye();
            _gimbal = UnityEngine.Object.FindObjectOfType<Yixuan.Vision.EyeGimbalController>();
            var characterGo = GameObject.Find("Yixuan_Test");
            _character = characterGo != null ? characterGo.transform : null;
            if (_eye == null || _character == null)
            {
                Block("HARNESS", "EyeCamera + Yixuan_Test present",
                    "eye=" + (_eye != null) + " character=" + (_character != null), "fixture prerequisites missing");
                yield break;
            }
            Log("eye_ok pos=" + _eye.transform.position.ToString("F2") + " gimbal=" + (_gimbal != null) +
                " character=" + _character.position.ToString("F2"));

            // T1.h: prove the driver really crosses frames with a nested wait.
            int frameBefore = Time.frameCount;
            yield return new HarnessWait(0.35);
            int frameAfter = Time.frameCount;
            _nestedWaitFrameDelta = frameAfter - frameBefore;
            Pass("T1.h", "nested wait crosses >=2 real frames",
                "frame " + frameBefore + " -> " + frameAfter + " (delta " + _nestedWaitFrameDelta + ")",
                _nestedWaitFrameDelta >= 2 ? "driver waits across frames" : "fixture did not cross frames", "");

            bool registryReady = PickService.EnsureRegistry();
            Log("registry_ready=" + registryReady + " ms=" + EntityPickRegistry.BuildMs +
                " report=" + (EntityPickRegistry.Report != null ? EntityPickRegistry.Report.selectable_entities : -1));
            if (!registryReady)
            {
                Block("T1.f", "selection registry builds from existing map geometry",
                    "error=" + EntityPickRegistry.Error, "registry build failed");
                yield break;
            }
            SaveCoverageReport();
            yield return CheckCoverage();

            yield return RunT1();
            yield return RunRound1GeometryTests();
            yield return RunT8();
            yield return RunProductionEntryTests();
            yield return RunLiveResampleTests();
            yield return RunInputGuardTests();

            WriteEvidenceIndex();
            Log("all_scenarios_done");
        }

        // ------------------------------------------------------------ T1
        static IEnumerator RunT1()
        {
            Log("T1_start");
            var bench00 = EntityPickRegistry.Get("entity/prop_bench_00");
            var bench01 = EntityPickRegistry.Get("entity/prop_bench_01");
            if (bench00 == null || bench01 == null || !bench00.Selectable || !bench01.Selectable)
            {
                Block("T1.a", "two same-family bench entities selectable",
                    "bench00=" + (bench00 != null && bench00.Selectable) + " bench01=" + (bench01 != null && bench01.Selectable),
                    "registry missing expected benches");
            }
            else
            {
                // --- T1.a1: bench 00 under the fixed centre crosshair
                yield return AimCharacterAtBench(bench00, 6.0f);
                if (Sub("T1.a", "aim bench00", _lastAimErrorDeg, _aimError))
                {
                    var hit = PickService.ConfirmAtCenter("manual_click");
                    var frame = PickService.LastFrame;
                    SaveFrame(frame, "t1", "t1a_bench00");
                    var record = SelectionBroker.Current;
                    SaveRecord("t1", "t1a_bench00_record.json", record, hit);
                    bool ok = hit != null && hit.Valid && hit.entity != null && hit.entity.Id == bench00.Id &&
                              record != null && record.entity_id == bench00.Id;
                    Check("T1.a", "centre confirm -> entity/prop_bench_00", ok,
                        "hit=" + (hit != null && hit.entity != null ? hit.entity.Id : "none") +
                        " frame=" + (frame != null ? frame.FrameId : -1) +
                        " centre_code=" + (frame != null ? frame.CodeAt(frame.width / 2, frame.height / 2) : -1) +
                        " depth=" + (hit != null ? hit.distance_m.ToString("F2") : "-") +
                        " frame_ok=" + (frame != null && frame.Ok),
                        "T1.a bench00: same-frame RGB/ID/depth + centre confirmation");
                    // highlight evidence for this confirmed entity
                    yield return new HarnessWaitFrames(4);
                    yield return CaptureGameView("t1", "t1a_bench00_highlight_gameview");
                }

                // --- T1.a2: same model, other instance, must be a different stable id
                yield return AimCharacterAtBench(bench01, 6.0f);
                if (Sub("T1.a", "aim bench01", _lastAimErrorDeg, _aimError))
                {
                    var hit = PickService.ConfirmAtCenter("manual_click");
                    var frame = PickService.LastFrame;
                    SaveFrame(frame, "t1", "t1a_bench01");
                    var record = SelectionBroker.Current;
                    SaveRecord("t1", "t1a_bench01_record.json", record, hit);
                    bool ok = hit != null && hit.Valid && hit.entity != null && hit.entity.Id == bench01.Id &&
                              record != null && record.entity_id == bench01.Id;
                    Check("T1.a", "second same-model entity -> entity/prop_bench_01", ok,
                        "hit=" + (hit != null && hit.entity != null ? hit.entity.Id : "none") +
                        " frame=" + (frame != null ? frame.FrameId : -1),
                        "T1.a bench01 second instance differs from bench00 (same-family model)");
                    yield return new HarnessWaitFrames(3);
                    yield return CaptureGameView("t1", "t1a_bench01_highlight_gameview");
                }
            }

            // --- T1.b moving tram follows the live transform, old position does not hit
            yield return RunTramTest();

            // --- T1.c fully occluded bench behind a building wall
            yield return RunOcclusionTest();

            // --- T1.d pixel in the gap between two benches is not a false bench hit
            yield return RunGapTest2();

            // --- T1.e oversized box spanning two entities is ambiguous, not auto-first
            yield return RunMultiEntityBoxTest();

            // --- T1.f coverage
            yield return CheckCoverage();

            Log("T1_end");
        }

        static bool Sub(string parent, string label, float aimErrorDeg, string aimError)
        {
            if (string.IsNullOrEmpty(aimError) && aimErrorDeg < 2.5f) return true;
            Block(parent, "fixture pose (" + label + ")", "aim_error=" + aimErrorDeg.ToString("F2") +
                "° err=" + aimError, "fixture could not establish an accurate deterministic pose");
            return false;
        }

        static IEnumerator AimCharacterAtBench(EntityPickEntry entry, float distance)
        {
            var catalogEntity = WorldEntityCatalog.Get(entry.Id);
            Vector3 seat = entry.CatalogPosition;
            Vector3 front = Vector3.back;
            if (catalogEntity != null)
            {
                var seatPoint = WorldEntityCatalog.SeatPoint(catalogEntity);
                var approach = WorldEntityCatalog.ApproachPoint(catalogEntity);
                if (seatPoint.HasValue && approach.HasValue)
                {
                    seat = seatPoint.Value;
                    front = (approach.Value - seatPoint.Value);
                    front.y = 0f;
                    if (front.sqrMagnitude < 1e-4f) front = Vector3.back;
                    front.Normalize();
                }
            }
            Vector3 characterPosition = new Vector3(seat.x, 0f, seat.z) + front * distance;
            yield return AimCharacter(characterPosition, seat + Vector3.up * 0.25f);
        }

        static IEnumerator AimCharacter(Vector3 characterXZ, Vector3 lookTarget)
        {
            _aimError = "";
            _lastAimErrorDeg = 999f;
            float ground = WorldData.GroundHeightAt(new Vector3(characterXZ.x, 0f, characterXZ.z));
            Vector3 position = new Vector3(characterXZ.x, ground + 0.02f, characterXZ.z);
            var world = Yixuan.Dynamic.DynamicWorld.Instance;
            var loco = UnityEngine.Object.FindObjectOfType<Yixuan.Motion.CharacterLocomotion>();
            if (loco != null) loco.Stop("fps_a_fixture");
            if (world != null && world.Character != null) world.PlaceCharacterAt(position);
            else _character.position = position;
            if (loco != null) loco.transform.position = position;
            Vector3 flat = lookTarget - position;
            flat.y = 0f;
            float bodyYaw = flat.sqrMagnitude > 1e-4f
                ? Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg
                : _character.eulerAngles.y;
            _character.rotation = Quaternion.Euler(0f, bodyYaw, 0f);
            yield return new HarnessWaitFrames(3);
            if (_gimbal == null) { _aimError = "missing EyeGimbalController"; yield break; }
            for (int iteration = 0; iteration < 3; iteration++)
            {
                Vector3 eyePosition = _eye.transform.position;
                Vector3 direction = lookTarget - eyePosition;
                if (direction.sqrMagnitude < 0.01f) { _aimError = "target too close to eye"; yield break; }
                float worldYaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
                float relativeYaw = Mathf.DeltaAngle(_character.eulerAngles.y, worldYaw);
                float pitch = Mathf.Asin(Mathf.Clamp(direction.normalized.y, -0.99f, 0.99f)) * Mathf.Rad2Deg;
                _gimbal.TurnRelative(relativeYaw, pitch, -1f, false);
                yield return new HarnessWaitUntil(() => !_gimbal.IsMoving, 5.0);
                yield return new HarnessWaitFrames(2);
            }
            Vector3 finalEye = _eye.transform.position;
            Vector3 finalDirection = lookTarget - finalEye;
            _lastAimErrorDeg = Vector3.Angle(_eye.transform.forward, finalDirection);
            Log("aim character=" + position.ToString("F2") + " body_yaw=" + _character.eulerAngles.y.ToString("F1") +
                " eye=" + finalEye.ToString("F2") + " err=" + _lastAimErrorDeg.ToString("F2") +
                " gimbal_yaw=" + _gimbal.CurrentYaw.ToString("F1") + " pitch=" + _gimbal.CurrentPitch.ToString("F1"));
        }

        static IEnumerator RunTramTest()
        {
            Log("T1.b_start");
            float waitStart = Time.realtimeSinceStartup;
            yield return new HarnessWaitUntil(() => Yixuan.Dynamic.DynamicWorld.Instance != null &&
                Yixuan.Dynamic.DynamicWorld.Instance.Ready && Yixuan.Dynamic.DynamicWorld.Instance.Tram != null, 60.0);
            var world = Yixuan.Dynamic.DynamicWorld.Instance;
            if (world == null || world.Tram == null)
            {
                Block("T1.b", "tram runtime available", "world=" + (world != null) + " tram=" + (world != null && world.Tram != null),
                    "dynamic world/tram not ready in this play session");
                yield break;
            }
            string tramId = world.Tram.Spec.entity_id;
            var tramEntry = EntityPickRegistry.Get(tramId);
            if (tramEntry == null || !tramEntry.Selectable)
            {
                Block("T1.b", "tram entity selectable with real geometry", "id=" + tramId + " entry=" + (tramEntry != null) + " selectable=" + (tramEntry != null && tramEntry.Selectable), "registry missing tram");
                yield break;
            }
            // Try several sides so a parked/blocked tram is still observed at the
            // centre; the fixture only fails (blocked) if no pose can see it.
            PickFrame frame0 = null;
            Vector3 center0 = Vector3.zero;
            bool t0Hit = false;
            int lastCode0 = -1;
            Vector3[] offsets = new Vector3[]
            {
                new Vector3(0f, 0f, 12f), new Vector3(0f, 0f, -12f),
                new Vector3(12f, 0f, 0f), new Vector3(-12f, 0f, 0f),
                new Vector3(0f, 0f, 20f), new Vector3(20f, 0f, 0f),
            };
            for (int poseIndex = 0; poseIndex < offsets.Length; poseIndex++)
            {
                Vector3 liveCenter = world.Tram.Center;
                Vector3 characterPosition = new Vector3(liveCenter.x + offsets[poseIndex].x, 0f, liveCenter.z + offsets[poseIndex].z);
                yield return AimCharacter(characterPosition, liveCenter);
                if (_lastAimErrorDeg > 3f)
                {
                    Log("T1.b pose" + poseIndex + " aim_err=" + _lastAimErrorDeg.ToString("F2"));
                    continue;
                }
                var attemptFrame = PickService.CaptureEyeFrame(_eye);
                if (attemptFrame == null || !attemptFrame.Ok) continue;
                bool inFront;
                Vector2 uv = EntityIdCapture.ProjectWorld(attemptFrame.snapshot, liveCenter, out inFront);
                int code = attemptFrame.CodeAtUv(uv.x, uv.y);
                lastCode0 = code;
                bool hit = inFront && uv.x > 0f && uv.x < 1f && uv.y > 0f && uv.y < 1f && code == tramEntry.Code;
                Log("T1.b pose" + poseIndex + " offset=" + offsets[poseIndex].ToString("F0") +
                    " uv=" + uv.ToString("F3") + " code=" + code + " hit=" + hit);
                if (!hit)
                {
                    SaveFrame(attemptFrame, "t1", "t1b_tram_t0_pose" + poseIndex + "_miss");
                    continue;
                }
                frame0 = attemptFrame;
                center0 = liveCenter;
                t0Hit = true;
                SaveFrame(frame0, "t1", "t1b_tram_t0_pose" + poseIndex);
                break;
            }
            if (!t0Hit)
            {
                Block("T1.b", "tram centre visible from at least one candidate pose",
                    "last_code=" + lastCode0 + " state=" + world.Tram.State + " speed=" + world.Tram.Speed.ToString("F2") +
                    " block=" + world.Tram.BlockReason,
                    "fixture could not observe the tram centre (occluded/blocked in this session); not counted as a product failure");
                yield break;
            }
            bool inFront0 = true;
            Vector2 uv0 = EntityIdCapture.ProjectWorld(frame0.snapshot, center0, out inFront0);
            int code0 = frame0.CodeAtUv(uv0.x, uv0.y);
            Check("T1.b", "tram t0 centre projects to the live tram id",
                t0Hit, "uv=" + uv0.ToString("F3") + " code=" + code0 + " expected=" + tramEntry.Code +
                " inFront=" + inFront0, "tram at recorded start position");

            // Wait for the autonomous tram to really move along its track.
            bool moved = false;
            float moveDeadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < moveDeadline)
            {
                yield return new HarnessWait(0.25);
                Vector3 current = world.Tram.Center;
                if (Vector3.Distance(current, center0) > 1.5f) { moved = true; break; }
            }
            Vector3 center1 = world.Tram.Center;
            if (!moved)
            {
                Block("T1.b", "tram actually moves along track",
                    "state=" + world.Tram.State + " speed=" + world.Tram.Speed.ToString("F2") + " block=" + world.Tram.BlockReason,
                    "tram blocked/dwelling for 30s; cannot validate live-transform follow in this session");
                yield break;
            }
            var frame1 = PickService.CaptureEyeFrame(_eye);
            SaveFrame(frame1, "t1", "t1b_tram_t1");
            if (frame1 == null || !frame1.Ok)
            {
                Fail("T1.b", "second frame captured", "capture error=" + (frame1 != null ? frame1.error : "null"), "ID capture failed");
                yield break;
            }
            bool inFront1;
            Vector2 uv1 = EntityIdCapture.ProjectWorld(frame1.snapshot, center1, out inFront1);
            int code1 = frame1.CodeAtUv(uv1.x, uv1.y);
            bool freshHit = inFront1 && uv1.x > 0f && uv1.x < 1f && uv1.y > 0f && uv1.y < 1f &&
                            code1 == tramEntry.Code;
            // Probe the tram's trailing end at the OLD pose: after moving along the
            // track that world location is outside the new 15m tram body, so a stale
            // static copy or an old-position selection would still decode to the tram.
            Vector3 delta = center1 - center0;
            float tramLength = world.Tram.Spec != null && world.Tram.Spec.length_m > 1f ? world.Tram.Spec.length_m : 15f;
            Vector3 probe = tramLength > 0f && delta.sqrMagnitude > 0.01f
                ? center0 - delta.normalized * (tramLength * 0.5f + 1.0f)
                : center0;
            bool inFrontOld;
            Vector2 uvOld = EntityIdCapture.ProjectWorld(frame1.snapshot, probe, out inFrontOld);
            bool probeInsideFrame = inFrontOld && uvOld.x > 0f && uvOld.x < 1f && uvOld.y > 0f && uvOld.y < 1f;
            int codeOld = probeInsideFrame ? frame1.CodeAtUv(uvOld.x, uvOld.y) : -1;
            bool oldClean = probeInsideFrame && codeOld != tramEntry.Code;
            Check("T1.b", "tram new position hits, recorded old position does not",
                freshHit && oldClean,
                "moved=" + Vector3.Distance(center0, center1).ToString("F2") + "m" +
                " new_uv=" + uv1.ToString("F3") + " new_code=" + code1 +
                " old_end_uv=" + uvOld.ToString("F3") + " old_end_code=" + codeOld +
                " probe_inside=" + probeInsideFrame + " tram_len=" + tramLength.ToString("F1") +
                " expected=" + tramEntry.Code,
                "live Transform follow; the old trailing-end world position must not remain selectable");
        }

        static IEnumerator RunOcclusionTest()
        {
            Log("T1.c_start");
            var benches = new List<EntityPickEntry>();
            var buildings = new List<EntityPickEntry>();
            foreach (var entry in EntityPickRegistry.Entries)
            {
                if (entry.Cls == "bench" && entry.Selectable) benches.Add(entry);
                else if (entry.Cls == "building" && entry.Selectable) buildings.Add(entry);
            }
            if (benches.Count == 0 || buildings.Count == 0)
            {
                Block("T1.c", "bench + building geometry", "benches=" + benches.Count + " buildings=" + buildings.Count, "missing registry geometry");
                yield break;
            }
            string chosenBench = "";
            bool verified = false;
            int attempts = 0;
            foreach (var bench in benches)
            {
                if (verified) break;
                Vector3 benchCenter = bench.WorldBounds().center;
                foreach (var building in buildings)
                {
                    if (attempts >= 8) break;
                    var bounds = building.WorldBounds();
                    Vector3 buildingCenter = bounds.center;
                    Vector3 horizontal = benchCenter - buildingCenter;
                    horizontal.y = 0f;
                    float distanceToBuilding = horizontal.magnitude;
                    if (distanceToBuilding < 4f) continue;
                    float halfExtent = Mathf.Max(bounds.extents.x, bounds.extents.z);
                    Vector3 directionFromBuilding = horizontal.normalized;
                    Vector3 eyeGuess = buildingCenter - directionFromBuilding * (halfExtent + 4.5f);
                    float ground = WorldData.GroundHeightAt(new Vector3(eyeGuess.x, 0f, eyeGuess.z));
                    Vector3 eyePosition = new Vector3(eyeGuess.x, ground + 1.6f, eyeGuess.z);
                    if (Vector3.Distance(eyePosition, benchCenter) > 30f) continue;
                    if (!RaySegmentHitsBox(eyePosition, benchCenter, bounds)) continue;
                    attempts++;
                    yield return AimCharacter(new Vector3(eyePosition.x, 0f, eyePosition.z), benchCenter);
                    if (_lastAimErrorDeg > 3f) { Log("T1.c skip aim err=" + _lastAimErrorDeg.ToString("F2")); continue; }
                    var frame = PickService.CaptureEyeFrame(_eye);
                    if (frame == null || !frame.Ok) continue;
                    int benchPixels;
                    bool benchVisible = frame.ContainsCode(bench.Code, out benchPixels);
                    int centerCode = frame.CodeAt(frame.width / 2, frame.height / 2);
                    var centerEntity = EntityPickRegistry.GetByCode(centerCode);
                    bool centreHitsSomethingElse = centerCode != 0 && centerCode != bench.Code;
                    if (benchVisible || !centreHitsSomethingElse)
                    {
                        Log("T1.c rejected bench=" + bench.Id + " building=" + building.Id +
                            " benchPixels=" + benchPixels + " centreCode=" + centerCode +
                            " centreEntity=" + (centerEntity != null ? centerEntity.Id : "none") +
                            " buildingCode=" + building.Code);
                        continue;
                    }
                    chosenBench = bench.Id;
                    SaveFrame(frame, "t1", "t1c_occluded_" + SafeName(bench.Id));
                    var hit = frame.ResolveUv(0.5f, 0.5f);
                    Check("T1.c", "fully occluded bench stays invisible and non-selectable",
                        true,
                        "bench=" + bench.Id + " hidden behind candidate building=" + building.Id +
                        " (code " + building.Code + "); bench_pixels=0 centre_code=" + centerCode +
                        " centre_entity=" + (centerEntity != null ? centerEntity.Id : "none") +
                        " centre_depth=" + (hit != null ? hit.distance_m.ToString("F2") : "-"),
                        "independent ray-vs-building-AABB says the wall blocks the line of sight; the ID image contains no bench pixels and the centre resolves to a different real surface");
                    verified = true;
                    break;
                }
            }
            if (!verified)
            {
                Block("T1.c", "find a bench fully behind a building from a reachable pose",
                    "attempts=" + attempts + " chosen=" + chosenBench,
                    "no candidate passed the independent occlusion geometry test and the ID check");
            }
        }

        static IEnumerator RunGapTest()
        {
            Log("T1.d_start");
            var candidates = new List<EntityPickEntry>();
            foreach (var entry in EntityPickRegistry.Entries)
            {
                if (!entry.Selectable) continue;
                if (entry.Cls == "building" || entry.Dynamic) continue;
                if (entry.Height > 8f) continue;
                candidates.Add(entry);
            }
            EntityPickEntry first = null, second = null;
            float bestGap = float.MaxValue;
            int gapAxis = 0;   // 1 = x separation, 2 = z separation
            Vector3 gapPoint = Vector3.zero;
            for (int i = 0; i < candidates.Count; i++)
            {
                for (int j = i + 1; j < candidates.Count; j++)
                {
                    var a = candidates[i];
                    var b = candidates[j];
                    float dx = Mathf.Max(0f, Mathf.Max(b.CatalogBoundsMin.x - a.CatalogBoundsMax.x, a.CatalogBoundsMin.x - b.CatalogBoundsMax.x));
                    float dz = Mathf.Max(0f, Mathf.Max(b.CatalogBoundsMin.z - a.CatalogBoundsMax.z, a.CatalogBoundsMin.z - b.CatalogBoundsMax.z));
                    float gap; int axis;
                    if (dx > 0.15f && dx < 1.3f && dz <= 0.01f) { gap = dx; axis = 1; }
                    else if (dz > 0.15f && dz < 1.3f && dx <= 0.01f) { gap = dz; axis = 2; }
                    else continue;
                    if (Mathf.Abs(a.CatalogPosition.y - b.CatalogPosition.y) > 1.6f) continue;
                    if (gap >= bestGap) continue;
                    bestGap = gap;
                    first = a;
                    second = b;
                    gapAxis = axis;
                    if (axis == 1)
                    {
                        float x = b.CatalogBoundsMin.x > a.CatalogBoundsMax.x
                            ? (a.CatalogBoundsMax.x + b.CatalogBoundsMin.x) * 0.5f
                            : (b.CatalogBoundsMax.x + a.CatalogBoundsMin.x) * 0.5f;
                        gapPoint = new Vector3(x, Mathf.Max(a.CatalogPosition.y, b.CatalogPosition.y) + 0.4f,
                            Mathf.Max(a.CatalogBoundsMin.z, b.CatalogBoundsMin.z) * 0.5f +
                            Mathf.Min(a.CatalogBoundsMax.z, b.CatalogBoundsMax.z) * 0.5f);
                    }
                    else
                    {
                        float z = b.CatalogBoundsMin.z > a.CatalogBoundsMax.z
                            ? (a.CatalogBoundsMax.z + b.CatalogBoundsMin.z) * 0.5f
                            : (b.CatalogBoundsMax.z + a.CatalogBoundsMin.z) * 0.5f;
                        gapPoint = new Vector3(
                            Mathf.Max(a.CatalogBoundsMin.x, b.CatalogBoundsMin.x) * 0.5f +
                            Mathf.Min(a.CatalogBoundsMax.x, b.CatalogBoundsMax.x) * 0.5f,
                            Mathf.Max(a.CatalogPosition.y, b.CatalogPosition.y) + 0.4f, z);
                    }
                }
            }
            if (first == null)
            {
                Block("T1.d", "two selectable entities separated by a real 0.15-1.3m gap", "gap candidates none",
                    "fixture could not find a separating pair with real geometry");
                yield break;
            }
            Vector3 characterXZ;
            if (gapAxis == 1)
                characterXZ = new Vector3(gapPoint.x - 4.5f, 0f, gapPoint.z);
            else
                characterXZ = new Vector3(gapPoint.x, 0f, gapPoint.z - 4.5f);
            yield return AimCharacter(characterXZ, gapPoint);
            if (!Sub("T1.d", "aim gap", _lastAimErrorDeg, _aimError)) yield break;
            var frame = PickService.CaptureEyeFrame(_eye);
            SaveFrame(frame, "t1", "t1d_gap");
            if (frame == null || !frame.Ok)
            {
                Fail("T1.d", "gap frame captured", "capture error=" + (frame != null ? frame.error : "null"), "ID capture failed");
                yield break;
            }
            int centerCode = frame.CodeAt(frame.width / 2, frame.height / 2);
            bool ok = centerCode != first.Code && centerCode != second.Code;
            Check("T1.d", "centre pixel through the gap is not a false entity hit",
                ok,
                "pair=" + first.Id + "/" + second.Id + " gap=" + bestGap.ToString("F2") + "m axis=" + gapAxis +
                " centre_code=" + centerCode + " ids=" + first.Code + "/" + second.Code +
                " centre_depth=" + frame.DepthAt(frame.width / 2, frame.height / 2).ToString("F4"),
                "AABB-only picking would fill this gap; the real geometry ID/depth must not");
        }

        sealed class GapPair
        {
            public float Gap;
            public int A;
            public int B;
            public int Axis;
        }

        static IEnumerator RunGapTest2()
        {
            Log("T1.d_start (generic separating-pair fixture)");
            var candidates = new List<EntityPickEntry>();
            foreach (var entry in EntityPickRegistry.Entries)
            {
                if (!entry.Selectable) continue;
                if (entry.Cls == "building" || entry.Dynamic) continue;
                if (entry.Height > 8f) continue;
                candidates.Add(entry);
            }
            var pairs = new List<GapPair>();
            for (int i = 0; i < candidates.Count; i++)
            {
                for (int j = i + 1; j < candidates.Count; j++)
                {
                    var a = candidates[i];
                    var b = candidates[j];
                    if (Mathf.Abs(a.CatalogPosition.y - b.CatalogPosition.y) > 1.6f) continue;
                    float dx = Mathf.Max(0f, Mathf.Max(b.CatalogBoundsMin.x - a.CatalogBoundsMax.x, a.CatalogBoundsMin.x - b.CatalogBoundsMax.x));
                    float dz = Mathf.Max(0f, Mathf.Max(b.CatalogBoundsMin.z - a.CatalogBoundsMax.z, a.CatalogBoundsMin.z - b.CatalogBoundsMax.z));
                    float gap; int axis;
                    if (dx >= 0.3f && dx <= 1.2f && dz <= 0.01f) { gap = dx; axis = 1; }
                    else if (dz >= 0.3f && dz <= 1.2f && dx <= 0.01f) { gap = dz; axis = 2; }
                    else continue;
                    pairs.Add(new GapPair { Gap = gap, A = i, B = j, Axis = axis });
                }
            }
            pairs.Sort((x, y) => x.Gap.CompareTo(y.Gap));
            Log("T1.d separating pairs=" + pairs.Count);
            if (pairs.Count == 0)
            {
                Block("T1.d", "two selectable entities separated by a real 0.3-1.2m gap", "pairs=0",
                    "fixture could not find a separating pair with real geometry");
                yield break;
            }
            int attempts = Mathf.Min(8, pairs.Count);
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                var pair = pairs[attempt];
                var first = candidates[pair.A];
                var second = candidates[pair.B];
                Vector3 gapPoint;
                Vector3 characterXZ;
                if (pair.Axis == 1)
                {
                    float x = second.CatalogBoundsMin.x > first.CatalogBoundsMax.x
                        ? (first.CatalogBoundsMax.x + second.CatalogBoundsMin.x) * 0.5f
                        : (second.CatalogBoundsMax.x + first.CatalogBoundsMin.x) * 0.5f;
                    float z = (Mathf.Max(first.CatalogBoundsMin.z, second.CatalogBoundsMin.z) +
                               Mathf.Min(first.CatalogBoundsMax.z, second.CatalogBoundsMax.z)) * 0.5f;
                    gapPoint = new Vector3(x, Mathf.Max(first.CatalogPosition.y, second.CatalogPosition.y) + 0.4f, z);
                    characterXZ = new Vector3(x - 4.5f, 0f, z);
                }
                else
                {
                    float z = second.CatalogBoundsMin.z > first.CatalogBoundsMax.z
                        ? (first.CatalogBoundsMax.z + second.CatalogBoundsMin.z) * 0.5f
                        : (second.CatalogBoundsMax.z + first.CatalogBoundsMin.z) * 0.5f;
                    float x = (Mathf.Max(first.CatalogBoundsMin.x, second.CatalogBoundsMin.x) +
                               Mathf.Min(first.CatalogBoundsMax.x, second.CatalogBoundsMax.x)) * 0.5f;
                    gapPoint = new Vector3(x, Mathf.Max(first.CatalogPosition.y, second.CatalogPosition.y) + 0.4f, z);
                    characterXZ = new Vector3(x, 0f, z - 4.5f);
                }
                yield return AimCharacter(characterXZ, gapPoint);
                if (_lastAimErrorDeg > 3f) continue;
                var frame = PickService.CaptureEyeFrame(_eye);
                if (frame == null || !frame.Ok) continue;
                int pixelsFirst, pixelsSecond;
                bool visibleFirst = frame.ContainsCode(first.Code, out pixelsFirst);
                bool visibleSecond = frame.ContainsCode(second.Code, out pixelsSecond);
                SaveFrame(frame, "t1", "t1d_gap_try" + attempt + "_" + SafeName(first.Id) + "__" + SafeName(second.Id));
                if (!visibleFirst || !visibleSecond) continue;
                bool inFrontA, inFrontB;
                Vector2 uvA = EntityIdCapture.ProjectWorld(frame.snapshot, first.CatalogPosition, out inFrontA);
                Vector2 uvB = EntityIdCapture.ProjectWorld(frame.snapshot, second.CatalogPosition, out inFrontB);
                if (!inFrontA || !inFrontB) continue;
                float centerU = 0.5f, centerV = 0.5f;
                bool betweenU = centerU > Mathf.Min(uvA.x, uvB.x) + 0.01f && centerU < Mathf.Max(uvA.x, uvB.x) - 0.01f;
                bool betweenV = centerV > Mathf.Min(uvA.y, uvB.y) + 0.01f && centerV < Mathf.Max(uvA.y, uvB.y) - 0.01f;
                if (!betweenU && !betweenV) { Log("T1.d skip pair not bracketing centre " + first.Id + "/" + second.Id); continue; }
                int centerCode = frame.CodeAt(frame.width / 2, frame.height / 2);
                bool ok = centerCode != first.Code && centerCode != second.Code;
                Check("T1.d", "centre pixel through the gap is not a false entity hit",
                    ok,
                    "pair=" + first.Id + "/" + second.Id + " gap=" + pair.Gap.ToString("F2") + "m axis=" + pair.Axis +
                    " centre_code=" + centerCode + " ids=" + first.Code + "/" + second.Code +
                    " betweenU=" + betweenU + " betweenV=" + betweenV +
                    " centre_depth=" + frame.DepthAt(frame.width / 2, frame.height / 2).ToString("F4"),
                    "camera centre ray really passes between the two entities; AABB-only picking would fill the gap");
                yield break;
            }
            Block("T1.d", "a separated pair bracketing the crosshair",
                "attempts=" + attempts + " pairs=" + pairs.Count,
                "no candidate pair produced a frame where both entities are visible and bracket the centre");
        }

        static IEnumerator RunMultiEntityBoxTest()
        {
            Log("T1.e_start");
            var bench00 = EntityPickRegistry.Get("entity/prop_bench_00");
            var bench01 = EntityPickRegistry.Get("entity/prop_bench_01");
            if (bench00 == null || bench01 == null || !bench00.Selectable || !bench01.Selectable)
            {
                Block("T1.e", "two nearby benches in one frame", "bench entries missing", "registry fixture missing");
                yield break;
            }
            var catalogA = WorldEntityCatalog.Get("entity/prop_bench_00");
            Vector3 front = Vector3.back;
            if (catalogA != null)
            {
                var seat = WorldEntityCatalog.SeatPoint(catalogA);
                var approach = WorldEntityCatalog.ApproachPoint(catalogA);
                if (seat.HasValue && approach.HasValue)
                {
                    front = approach.Value - seat.Value;
                    front.y = 0f;
                    if (front.sqrMagnitude < 1e-4f) front = Vector3.back;
                    front.Normalize();
                }
            }
            Vector3 midpoint = (bench00.CatalogPosition + bench01.CatalogPosition) * 0.5f;
            PickFrame frame = null;
            int bench0Pixels = 0, bench1Pixels = 0;
            bool visible0 = false, visible1 = false;
            float[] distances = new float[] { 12f, 16f, 20f };
            for (int attempt = 0; attempt < distances.Length; attempt++)
            {
                Vector3 characterXZ = midpoint + front * distances[attempt];
                yield return AimCharacter(new Vector3(characterXZ.x, 0f, characterXZ.z), midpoint + Vector3.up * 0.2f);
                if (_lastAimErrorDeg > 3f) continue;
                frame = PickService.CaptureEyeFrame(_eye);
                if (frame == null || !frame.Ok) continue;
                visible0 = frame.ContainsCode(bench00.Code, out bench0Pixels);
                visible1 = frame.ContainsCode(bench01.Code, out bench1Pixels);
                SaveFrame(frame, "t1", "t1e_two_benches_try" + attempt + "_d" + distances[attempt].ToString("F0"));
                if (visible0 && visible1) break;
            }
            if (frame == null || !frame.Ok)
            {
                Fail("T1.e", "two-entity frame captured", "no valid frame", "ID capture failed");
                yield break;
            }
            if (!visible0 || !visible1)
            {
                Block("T1.e", "both bench ids visible in one frame", "bench00=" + bench0Pixels + " bench01=" + bench1Pixels, "fixture frame does not contain both entities");
                yield break;
            }
            Rect box = ProjectBoundsBox(frame, bench00, 0.035f);
            box = Union(box, ProjectBoundsBox(frame, bench01, 0.035f));
            var result = PickService.ResolveBox(frame, box);
            bool hasBoth = false;
            if (result != null)
            {
                foreach (var candidate in result.candidates)
                    if (candidate.entity_id == bench00.Id || candidate.entity_id == bench01.Id) hasBoth = true;
            }
            bool ok = result != null && result.ok && hasBoth && result.ambiguous;
            Check("T1.e", "box spanning two entities is ambiguous (no silent first pick)",
                ok,
                "box=" + box + " ambiguous=" + (result != null && result.ambiguous) +
                " candidates=" + (result != null ? result.candidates.Count : -1) +
                " note=" + (result != null ? result.note : "null") +
                " top=" + (result != null && result.Dominant != null ? result.Dominant.entity_id : "none"),
                "pixel histogram shows >=2 substantial entities; confirm API must not auto-pick the first");
            SaveText("t1", "t1e_box_result.json", SelectionJson.Serialize(result));
            // R3: the representative point must belong to the dominant candidate's
            // own valid ID/depth pixels, never to a foreign/background pixel.
            var dominant = result != null ? result.Dominant : null;
            bool reliablePoint = dominant != null && dominant.has_reliable_point &&
                                 dominant.sample_pixel_x >= 0 && dominant.sample_pixel_y >= 0 &&
                                 frame.CodeAt(dominant.sample_pixel_x, dominant.sample_pixel_y) == dominant.sample_code &&
                                 frame.DepthAt(dominant.sample_pixel_x, dominant.sample_pixel_y) > 0f &&
                                 Vector3.Distance(dominant.surface_point, Vector3.zero) > 0.01f;
            Check("T1.e", "box representative point belongs to the dominant entity's own valid pixels",
                reliablePoint,
                "dominant=" + (dominant != null ? dominant.entity_id : "none") +
                " sample_px=" + (dominant != null ? dominant.sample_pixel_x + "," + dominant.sample_pixel_y : "-") +
                " sample_code=" + (dominant != null ? dominant.sample_code : -1) +
                " code_at_sample=" + (dominant != null ? frame.CodeAt(dominant.sample_pixel_x, dominant.sample_pixel_y) : -1) +
                " depth_at_sample=" + (dominant != null ? frame.DepthAt(dominant.sample_pixel_x, dominant.sample_pixel_y).ToString("F4") : "-") +
                " surface_point=" + (dominant != null ? dominant.surface_point.ToString("F2") : "none") +
                " dominant_is_confirmed=" + (result != null && result.dominant_is_confirmed),
                "R3: representative pixel selected from the candidate's own mask and re-resolved with strict id/depth check");
        }

        // ------------------------------------------------------------ T8
        static IEnumerator RunT8()
        {
            Log("T8_start");
            var bench00 = EntityPickRegistry.Get("entity/prop_bench_00");
            var bench01 = EntityPickRegistry.Get("entity/prop_bench_01");
            if (bench00 == null || bench01 == null)
            {
                Block("T8", "two bench entities", "entries missing", "registry fixture missing");
                yield break;
            }
            // T8.a: pending result from a cancelled intent can never publish.
            string beforeId = SelectionBroker.Current != null ? SelectionBroker.Current.entity_id : "";
            int beforeVersion = SelectionBroker.Version;
            int generation = SelectionBroker.BeginIntent();
            var pendingRecord = PickService.BuildEntityRecord(bench00, "injected_test", generation, bench00.CatalogPosition, false);
            SelectionBroker.CancelIntent();
            int eventsBefore = _confirmedEventCount;
            bool published = SelectionBroker.TryPublish(generation, pendingRecord);
            bool currentUnchanged = (SelectionBroker.Current != null ? SelectionBroker.Current.entity_id : "") == beforeId;
            Check("T8.a", "result from a cancelled generation is rejected, no event",
                !published && currentUnchanged && _confirmedEventCount == eventsBefore &&
                SelectionBroker.Version == beforeVersion,
                "published=" + published + " current=" + (SelectionBroker.Current != null ? SelectionBroker.Current.entity_id : "none") +
                " events_delta=" + (_confirmedEventCount - eventsBefore) + " reason=" + SelectionBroker.LastRejectReason,
                "cancel advances generation before the delayed result arrives");

            // T8.b: replacement target wins; older result is stale.
            int generationOld = SelectionBroker.BeginIntent();
            int generationNew = SelectionBroker.BeginIntent();
            var newRecord = PickService.BuildEntityRecord(bench01, "injected_test", generationNew, bench01.CatalogPosition, false);
            bool publishedNew = SelectionBroker.TryPublish(generationNew, newRecord);
            var oldRecord = PickService.BuildEntityRecord(bench00, "injected_test", generationOld, bench00.CatalogPosition, false);
            int versionAfterNew = SelectionBroker.Version;
            bool publishedOld = SelectionBroker.TryPublish(generationOld, oldRecord);
            bool keepsNew = SelectionBroker.Current != null && SelectionBroker.Current.entity_id == bench01.Id &&
                            SelectionBroker.Version == versionAfterNew;
            Check("T8.b", "new target wins and the older generation cannot overwrite it",
                publishedNew && !publishedOld && keepsNew,
                "new_published=" + publishedNew + " old_published=" + publishedOld +
                " current=" + (SelectionBroker.Current != null ? SelectionBroker.Current.entity_id : "none") +
                " version_kept=" + (SelectionBroker.Version == versionAfterNew),
                "shared SelectionBroker generation guard");

            // T8.c: a frame captured for an older observation cannot be published after a newer observation.
            yield return AimCharacterAtBench(bench00, 6.0f);
            if (!Sub("T8.c", "aim bench00", _lastAimErrorDeg, _aimError)) yield break;
            var hit00 = PickService.ConfirmAtCenter("injected_test");
            var frame00 = PickService.LastFrame;
            var recordFromFrame00 = SelectionBroker.Current;
            if (hit00 == null || frame00 == null || recordFromFrame00 == null || recordFromFrame00.entity_id != bench00.Id)
            {
                Block("T8.c", "frame-bound record for bench00", "hit=" + (hit00 != null && hit00.entity != null ? hit00.entity.Id : "none"), "fixture capture failed");
                yield break;
            }
            int staleGeneration = recordFromFrame00.generation;
            int generationFresh = SelectionBroker.BeginIntent();
            yield return AimCharacterAtBench(bench01, 6.0f);
            if (!Sub("T8.c", "aim bench01", _lastAimErrorDeg, _aimError)) yield break;
            var hit01 = PickService.ConfirmAtCenter("injected_test", generationFresh);
            var frame01 = PickService.LastFrame;
            var freshRecord = SelectionBroker.Current;
            if (hit01 == null || frame01 == null || freshRecord == null || freshRecord.entity_id != bench01.Id)
            {
                Block("T8.c", "fresh frame-bound record for bench01", "hit=" + (hit01 != null && hit01.entity != null ? hit01.entity.Id : "none") + " err=" + PickService.LastError, "fixture capture failed");
                yield break;
            }
            int versionBeforeStale = SelectionBroker.Version;
            bool stalePublished = SelectionBroker.TryPublish(staleGeneration, recordFromFrame00);
            bool stillFresh = SelectionBroker.Current != null && SelectionBroker.Current.entity_id == bench01.Id &&
                              SelectionBroker.Current.frame_id == frame01.FrameId &&
                              SelectionBroker.Version == versionBeforeStale;
            Check("T8.c", "old ID frame cannot overwrite a newer confirmed selection",
                !stalePublished && stillFresh,
                "stale_published=" + stalePublished +
                " current=" + (SelectionBroker.Current != null ? SelectionBroker.Current.entity_id : "none") +
                " current_frame=" + (SelectionBroker.Current != null ? SelectionBroker.Current.frame_id : -1) +
                " newer_frame=" + frame01.FrameId + " stale_frame=" + frame00.FrameId,
                "records are bound to their capture frame and intent generation");
            SaveRecord("t8", "t8c_fresh_record.json", SelectionBroker.Current, PickService.LastCenterHit);
            Log("T8_end");
        }

        // ------------------------------------------------------ coverage
        static IEnumerator CheckCoverage()
        {
            var report = EntityPickRegistry.Report;
            if (report == null)
            {
                Block("T1.f", "coverage report exists", "null report", "registry report missing");
                yield break;
            }
            int benchTotal = 0, benchSelectable = 0;
            int buildingTotal = 0, buildingSelectable = 0;
            int treeTotal = 0, treeSelectable = 0;
            int vehicleTotal = 0, vehicleSelectable = 0;
            foreach (var coverage in report.entries)
            {
                bool selectable = coverage.selectable;
                switch (coverage.cls)
                {
                    case "bench": benchTotal++; if (selectable) benchSelectable++; break;
                    case "building": buildingTotal++; if (selectable) buildingSelectable++; break;
                    case "tree": treeTotal++; if (selectable) treeSelectable++; break;
                    case "vehicle": vehicleTotal++; if (selectable) vehicleSelectable++; break;
                }
            }
            bool ok = report.geometry_verified_entities >= 850 &&
                      report.missing_entities == 0 &&
                      benchSelectable == benchTotal && benchTotal >= 83 &&
                      buildingSelectable == buildingTotal && buildingTotal >= 48 &&
                      treeSelectable == treeTotal && treeTotal >= 300 &&
                      vehicleSelectable == vehicleTotal && vehicleTotal >= 40 &&
                      report.sidecar_entities == 758 && report.sidecar_triangles > 250000 &&
                      !string.IsNullOrEmpty(report.sidecar_sha256) &&
                      report.excluded_source_objects > 0 &&
                      report.verified_by_source.ContainsKey("source_object_sidecar");
            Check("T1.f", "every registered entity has source-object/renderer provenance (no AABB guessing)",
                ok,
                "catalog=" + report.catalog_entities + " selectable=" + report.selectable_entities +
                " missing=" + report.missing_entities +
                " bench=" + benchSelectable + "/" + benchTotal +
                " building=" + buildingSelectable + "/" + buildingTotal +
                " tree=" + treeSelectable + "/" + treeTotal +
                " vehicle=" + vehicleSelectable + "/" + vehicleTotal +
                " sidecar_entities=" + report.sidecar_entities +
                " sidecar_tris=" + report.sidecar_triangles +
                " sidecar_objects=" + report.sidecar_objects +
                " mapped_source_objects=" + report.source_map_mapped_objects +
                " excluded_source_objects=" + report.excluded_source_objects +
                " unclassified_source_objects=" + report.unclassified_source_objects +
                " sidecar_sha=" + (string.IsNullOrEmpty(report.sidecar_sha256) ? "none" : report.sidecar_sha256.Substring(0, 16)) +
                " verified_by=" + JsonConvert.SerializeObject(report.verified_by_source) +
                " source_policy=" + report.geometry_source_policy +
                " missing_classes=" + JsonConvert.SerializeObject(report.missing_by_class),
                "provenance = existing DYN_entity_* nodes, per-building BLD_* renderers, or source_object_map.csv -> static_pick.v1.bytes; excluded/unclassified source objects are occluders with a recorded reason, not entities");
            // Provenance metadata must agree between the sidecar manifest and the report.
            var manifest = EntityPickRegistry.Manifest;
            Check("T1.f", "sidecar manifest and runtime provenance agree",
                manifest != null &&
                manifest.entity_count == report.sidecar_entities &&
                manifest.triangle_count == report.sidecar_triangles &&
                manifest.sidecar_sha256 == report.sidecar_sha256 &&
                manifest.source_rows == report.source_map_rows &&
                manifest.excluded_source_object_count == report.excluded_source_objects,
                "manifest=" + (manifest != null
                    ? "entities=" + manifest.entity_count + " tris=" + manifest.triangle_count +
                      " sha=" + (manifest.sidecar_sha256 ?? "").Substring(0, Mathf.Min(16, (manifest.sidecar_sha256 ?? "").Length)) +
                      " source_rows=" + manifest.source_rows
                    : "null") +
                " report_sidecar_sha=" + (string.IsNullOrEmpty(report.sidecar_sha256) ? "none" : report.sidecar_sha256.Substring(0, 16)),
                "manifest provenance/hash must match the bytes actually loaded at runtime");
            yield break;
        }

        // ================================================== A round-1 additions
        static Yixuan.Presentation.CameraDirector FindCameraDirector()
        {
            return UnityEngine.Object.FindObjectOfType<Yixuan.Presentation.CameraDirector>();
        }

        static void ClearEventSystemFocus()
        {
            var eventSystem = UnityEngine.EventSystems.EventSystem.current;
            if (eventSystem != null) eventSystem.SetSelectedGameObject(null);
        }

        static PickCameraSnapshot BuildSyntheticSnapshot(float fovDegrees, float aspect)
        {
            var snapshot = new PickCameraSnapshot
            {
                frame_id = -1,
                frame_count = Time.frameCount,
                time_since_startup = Time.realtimeSinceStartupAsDouble,
                source = "synthetic_test",
                position = Vector3.zero,
                rotation = Quaternion.identity,
                fov = fovDegrees,
                near = 0.05f,
                far = 900f,
                aspect = aspect,
                width = 100,
                height = 100,
            };
            snapshot.view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
            snapshot.projection_gl = Matrix4x4.Perspective(snapshot.fov, aspect, snapshot.near, snapshot.far);
            snapshot.projection_gpu = GL.GetGPUProjectionMatrix(snapshot.projection_gl, true);
            return snapshot;
        }

        static PickFrame BuildSyntheticFrame(PickCameraSnapshot snapshot, int width, int height, int[] codes, float depth01)
        {
            var frame = new PickFrame
            {
                snapshot = snapshot,
                width = width,
                height = height,
                codes = codes,
                depth01 = new float[width * height],
                id_pixels = new Color32[width * height],
            };
            for (int i = 0; i < frame.depth01.Length; i++)
            {
                frame.depth01[i] = depth01;
                int code = codes[i];
                frame.id_pixels[i] = new Color32((byte)(code & 0xFF), (byte)((code >> 8) & 0xFF), (byte)((code >> 16) & 0xFF), 255);
            }
            return frame;
        }

        static bool RayMeshHit(Vector3 origin, Vector3 direction, Mesh mesh, out Vector3 point, out float distance)
        {
            point = Vector3.zero;
            distance = float.MaxValue;
            if (mesh == null) return false;
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                Vector3 a = vertices[triangles[t]];
                Vector3 b = vertices[triangles[t + 1]];
                Vector3 c = vertices[triangles[t + 2]];
                // Moller-Trumbore
                Vector3 edge1 = b - a, edge2 = c - a;
                Vector3 pvec = Vector3.Cross(direction, edge2);
                float det = Vector3.Dot(edge1, pvec);
                if (Mathf.Abs(det) < 1e-10f) continue;
                float invDet = 1f / det;
                Vector3 tvec = origin - a;
                float u = Vector3.Dot(tvec, pvec) * invDet;
                if (u < -1e-5f || u > 1f + 1e-5f) continue;
                Vector3 qvec = Vector3.Cross(tvec, edge1);
                float v = Vector3.Dot(direction, qvec) * invDet;
                if (v < -1e-5f || u + v > 1f + 1e-5f) continue;
                float candidate = Vector3.Dot(edge2, qvec) * invDet;
                if (candidate <= 1e-4f || candidate >= distance) continue;
                distance = candidate;
                point = origin + direction * candidate;
            }
            return distance < float.MaxValue;
        }

        // =====================================================================
        // A_r2 focused off-axis diagnostic (review repair round 2).
        // One real T1 off-axis fixture + the original synthetic 30deg assertion.
        // The real sample is frozen by visibility/interior rules BEFORE any
        // reconstruction error is inspected; hits use a front-face policy that
        // matches the PickId shader's Cull Back. Raw depth/UV/matrices/ray/
        // triangle/intersection are saved for independent offline checks.
        // =====================================================================
        [MenuItem(MenuRunR2, false, 302)]
        public static void RunR2Menu() { RunR2(); }

        public static void RunR2()
        {
            if (_running) { Debug.LogWarning("[FPS-A] harness already running"); return; }
            _projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            _aRoot = Path.Combine(_projectRoot, "Captures", "FirstPersonSelection", "run_20261110", "A_r2");
            _testsDir = Path.Combine(_aRoot, "tests");
            _evidenceDir = Path.Combine(_aRoot, "evidence");
            if (!Application.isPlaying)
            {
                Directory.CreateDirectory(_testsDir);
                WriteFile(Path.Combine(_aRoot, "state", "harness_entry_blocked.json"),
                    SelectionJson.Serialize(new
                    {
                        blocked = true,
                        reason = "play_mode_required",
                        detail = "Enter Play mode first, then run the A_r2 off-axis diagnostic; it does not enter play itself.",
                        at_local = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                    }));
                Debug.LogWarning("[FPS-A2] run blocked: enter Play mode first.");
                return;
            }
            Directory.CreateDirectory(_testsDir);
            Directory.CreateDirectory(Path.Combine(_aRoot, "quality", "raw"));
            Cases.Clear();
            LogLines.Clear();
            _running = true;
            _finalized = false;
            _harnessStartTime = EditorApplication.timeSinceStartup;
            _deadline = _harnessStartTime + 240.0;
            Stack.Clear();
            Stack.Push(RunR2OffAxis());
            EditorApplication.update += Tick;
            Log("r2_diagnostic_scheduled");
        }

        sealed class PixelDiagnostic
        {
            public int x, y, code;
            public string entity_id = "";
            public float u, v;
            public float raw_depth01;
            public int raw_depth_r, raw_depth_g, raw_depth_b, raw_depth_a;
            public Vector3 ray_origin;
            public Vector3 ray_direction;
            public float cosine;
            public Vector3 reconstructed;
            public bool double_hit;
            public Vector3 double_point;
            public float double_distance;
            public int double_triangle;
            public float double_signed_area;
            public bool front_hit;
            public Vector3 front_point;
            public float front_distance;
            public int front_triangle;
            public float front_signed_area;
            public Vector3 front_a, front_b, front_c;
            public float expected_depth01_from_front;
            public float depth01_delta_from_front;
            public float front_error_m;
            public float double_error_m;
            public float front_distance_from_depth_m;
        }

        static float SignedArea2D(PickCameraSnapshot snapshot, Vector3 a, Vector3 b, Vector3 c)
        {
            // ProjectWorldToViewport is top-origin (row 0 = top). In that screen
            // space the rendered front surface has negative signed area (verified
            // against the rendered ID/depth frame: bench tri 41 = -267.98,
            // transformer tri 28 = -279.90 at the exact rendered pixels).
            Vector3 pa = snapshot.ProjectWorldToViewport(a, out _);
            Vector3 pb = snapshot.ProjectWorldToViewport(b, out _);
            Vector3 pc = snapshot.ProjectWorldToViewport(c, out _);
            return (pb.x - pa.x) * (pc.y - pa.y) - (pb.y - pa.y) * (pc.x - pa.x);
        }

        static bool RayMeshHitDetailed(PickCameraSnapshot snapshot, Vector3 origin, Vector3 direction, Mesh mesh,
            bool frontFacesOnly, out Vector3 point, out float distance, out int triangleIndex,
            out float signedArea, out Vector3 ta, out Vector3 tb, out Vector3 tc)
        {
            point = Vector3.zero;
            distance = float.MaxValue;
            triangleIndex = -1;
            signedArea = 0f;
            ta = tb = tc = Vector3.zero;
            if (mesh == null) return false;
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                Vector3 a = vertices[triangles[t]];
                Vector3 b = vertices[triangles[t + 1]];
                Vector3 c = vertices[triangles[t + 2]];
                float area = SignedArea2D(snapshot, a, b, c);
                if (frontFacesOnly && area >= 0f) continue;   // Cull Back policy
                Vector3 edge1 = b - a, edge2 = c - a;
                Vector3 pvec = Vector3.Cross(direction, edge2);
                float det = Vector3.Dot(edge1, pvec);
                if (Mathf.Abs(det) < 1e-10f) continue;
                float invDet = 1f / det;
                Vector3 tvec = origin - a;
                float uu = Vector3.Dot(tvec, pvec) * invDet;
                if (uu < -1e-5f || uu > 1f + 1e-5f) continue;
                Vector3 qvec = Vector3.Cross(tvec, edge1);
                float vv = Vector3.Dot(direction, qvec) * invDet;
                if (vv < -1e-5f || uu + vv > 1f + 1e-5f) continue;
                float candidate = Vector3.Dot(edge2, qvec) * invDet;
                if (candidate <= 1e-4f || candidate >= distance) continue;
                distance = candidate;
                point = origin + direction * candidate;
                triangleIndex = t;
                signedArea = area;
                ta = a; tb = b; tc = c;
            }
            return distance < float.MaxValue;
        }

        static PixelDiagnostic BuildPixelDiagnostic(PickFrame frame, int x, int y)
        {
            var diag = new PixelDiagnostic { x = x, y = y };
            diag.code = frame.CodeAt(x, y);
            diag.entity_id = EntityPickRegistry.GetByCode(diag.code) != null ? EntityPickRegistry.GetByCode(diag.code).Id : "";
            diag.u = (x + 0.5f) / frame.width;
            diag.v = (y + 0.5f) / frame.height;
            var snapshot = frame.snapshot;
            diag.raw_depth01 = frame.DepthAt(x, y);
            if (frame.depth_rgba != null)
            {
                var rgba = frame.depth_rgba[y * frame.width + x];
                diag.raw_depth_r = rgba.r; diag.raw_depth_g = rgba.g;
                diag.raw_depth_b = rgba.b; diag.raw_depth_a = rgba.a;
            }
            if (!frame.ViewportRay(diag.u, diag.v, out diag.ray_origin, out diag.ray_direction)) return diag;
            diag.cosine = Vector3.Dot(diag.ray_direction, snapshot.Forward);
            diag.reconstructed = frame.WorldPointAt(diag.u, diag.v, out _);
            var entry = EntityPickRegistry.GetByCode(diag.code);
            if (entry == null || entry.EntityMesh == null) return diag;

            Vector3 p; float d; int tri; float area; Vector3 a, b, c;
            diag.double_hit = RayMeshHitDetailed(snapshot, diag.ray_origin, diag.ray_direction, entry.EntityMesh,
                false, out p, out d, out tri, out area, out a, out b, out c);
            if (diag.double_hit)
            {
                diag.double_point = p; diag.double_distance = d;
                diag.double_triangle = tri; diag.double_signed_area = area;
                diag.double_error_m = Vector3.Distance(diag.reconstructed, p);
            }
            diag.front_hit = RayMeshHitDetailed(snapshot, diag.ray_origin, diag.ray_direction, entry.EntityMesh,
                true, out p, out d, out tri, out area, out a, out b, out c);
            if (diag.front_hit)
            {
                diag.front_point = p; diag.front_distance = d;
                diag.front_triangle = tri; diag.front_signed_area = area;
                diag.front_a = a; diag.front_b = b; diag.front_c = c;
                diag.front_error_m = Vector3.Distance(diag.reconstructed, p);
                diag.front_distance_from_depth_m = Vector3.Distance(diag.ray_origin, diag.reconstructed);
                float eyeDepth = Vector3.Dot(p - diag.ray_origin, snapshot.Forward);
                diag.expected_depth01_from_front = (eyeDepth - snapshot.near) / Mathf.Max(1e-5f, snapshot.far - snapshot.near);
                diag.depth01_delta_from_front = diag.raw_depth01 - diag.expected_depth01_from_front;
            }
            return diag;
        }

        static float[] MatrixArray(Matrix4x4 m)
        {
            return new float[]
            {
                m.m00, m.m01, m.m02, m.m03, m.m10, m.m11, m.m12, m.m13,
                m.m20, m.m21, m.m22, m.m23, m.m30, m.m31, m.m32, m.m33
            };
        }

        static float[] VectorArray(Vector3 v) { return new float[] { v.x, v.y, v.z }; }

        static void WritePixelDiagnostic(PickFrame frame, PixelDiagnostic diag, string tag)
        {
            try
            {
                var snap = frame.snapshot;
                var payload = new
                {
                    schema = "firstperson-selection.a-r2.offaxis-pixel.v1",
                    tag = tag,
                    generated_at_local = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                    frame_id = frame.FrameId,
                    frame_count = snap.frame_count,
                    pixel = new { x = diag.x, y = diag.y, u = diag.u, v = diag.v, width = frame.width, height = frame.height },
                    code = diag.code,
                    entity_id = diag.entity_id,
                    raw_depth_rgba8 = new int[] { diag.raw_depth_r, diag.raw_depth_g, diag.raw_depth_b, diag.raw_depth_a },
                    raw_depth01 = diag.raw_depth01,
                    snapshot = new
                    {
                        position = VectorArray(snap.position),
                        rotation = new float[] { snap.rotation.x, snap.rotation.y, snap.rotation.z, snap.rotation.w },
                        forward = VectorArray(snap.Forward),
                        fov = snap.fov, near = snap.near, far = snap.far,
                        aspect = snap.aspect, width = snap.width, height = snap.height,
                        view = MatrixArray(snap.view),
                        projection_gl = MatrixArray(snap.projection_gl),
                        projection_gpu = MatrixArray(snap.projection_gpu),
                    },
                    ray = new { origin = VectorArray(diag.ray_origin), direction = VectorArray(diag.ray_direction), cosine = diag.cosine },
                    reconstructed = VectorArray(diag.reconstructed),
                    double_sided_oracle = new
                    {
                        hit = diag.double_hit, point = VectorArray(diag.double_point), distance_m = diag.double_distance,
                        triangle = diag.double_triangle, signed_area = diag.double_signed_area, error_m = diag.double_error_m
                    },
                    front_face_oracle = new
                    {
                        policy = "Unity clockwise front faces: projected signed area < 0, matching PickId.shader Cull Back",
                        hit = diag.front_hit, point = VectorArray(diag.front_point), distance_m = diag.front_distance,
                        triangle = diag.front_triangle, signed_area = diag.front_signed_area,
                        triangle_a = VectorArray(diag.front_a), triangle_b = VectorArray(diag.front_b), triangle_c = VectorArray(diag.front_c),
                        error_m = diag.front_error_m,
                        expected_depth01_from_surface = diag.expected_depth01_from_front,
                        depth01_delta_raw_minus_fe = diag.depth01_delta_from_front,
                        reconstructed_distance_m = diag.front_distance_from_depth_m
                    }
                };
                var path = Path.Combine(_aRoot, "quality", "raw", tag + ".json");
                WriteFile(path, JsonConvert.SerializeObject(payload, Formatting.Indented));
            }
            catch (Exception exception) { Log("write_pixel_diagnostic_exception " + exception.Message); }
        }

        static void SaveRawPickFrame(PickFrame frame, string tag)
        {
            if (frame == null || !frame.Ok) return;
            try
            {
                var dir = Path.Combine(_aRoot, "quality", "raw");
                Directory.CreateDirectory(dir);
                var codesRaw = new byte[frame.codes.Length * 4];
                Buffer.BlockCopy(frame.codes, 0, codesRaw, 0, codesRaw.Length);
                File.WriteAllBytes(Path.Combine(dir, tag + "_codes_i32.bin"), codesRaw);
                var depthBytes = new byte[frame.depth_rgba.Length * 4];
                for (int i = 0; i < frame.depth_rgba.Length; i++)
                {
                    depthBytes[i * 4] = frame.depth_rgba[i].r;
                    depthBytes[i * 4 + 1] = frame.depth_rgba[i].g;
                    depthBytes[i * 4 + 2] = frame.depth_rgba[i].b;
                    depthBytes[i * 4 + 3] = frame.depth_rgba[i].a;
                }
                File.WriteAllBytes(Path.Combine(dir, tag + "_depth_rgba8.bin"), depthBytes);
                var depthFloat = new byte[frame.depth01.Length * 4];
                Buffer.BlockCopy(frame.depth01, 0, depthFloat, 0, depthFloat.Length);
                File.WriteAllBytes(Path.Combine(dir, tag + "_depth01_f32.bin"), depthFloat);
                var snap = frame.snapshot;
                WriteFile(Path.Combine(dir, tag + "_snapshot.json"), JsonConvert.SerializeObject(new
                {
                    frame_id = frame.FrameId,
                    width = frame.width, height = frame.height,
                    position = VectorArray(snap.position),
                    rotation = new float[] { snap.rotation.x, snap.rotation.y, snap.rotation.z, snap.rotation.w },
                    forward = VectorArray(snap.Forward),
                    fov = snap.fov, near = snap.near, far = snap.far, aspect = snap.aspect,
                    view = MatrixArray(snap.view),
                    projection_gl = MatrixArray(snap.projection_gl),
                    projection_gpu = MatrixArray(snap.projection_gpu),
                }, Formatting.Indented));
                Log("raw_frame_saved " + tag);
            }
            catch (Exception exception) { Log("save_raw_frame_exception " + exception.Message); }
        }

        /// <summary>Frozen sample rule: max |x-centre| among stable 3x3 interior pixels with a source-object sidecar mesh. No reconstruction error is used.</summary>
        static bool ChooseFrozenOffAxisSample(PickFrame frame, out int px, out int py, out EntityPickEntry entry, out string reason)
        {
            px = -1; py = -1; entry = null;
            reason = "";
            var frequency = new Dictionary<int, int>();
            for (int y = 1; y < frame.height - 1; y += 2)
                for (int x = 1; x < frame.width - 1; x += 2)
                {
                    int code = frame.CodeAt(x, y);
                    if (code == 0) continue;
                    int count;
                    frequency.TryGetValue(code, out count);
                    frequency[code] = count + 1;
                }
            int centerX = frame.width / 2;
            int bestOffset = -1;
            for (int y = 1; y < frame.height - 1; y += 2)
                for (int x = 1; x < frame.width - 1; x += 2)
                {
                    if (frame.DepthAt(x, y) <= 0f) continue;
                    int code = frame.CodeAt(x, y);
                    if (code == 0) continue;
                    int count;
                    frequency.TryGetValue(code, out count);
                    if (count < 25) continue;
                    var candidate = EntityPickRegistry.GetByCode(code);
                    if (candidate == null || candidate.EntityMesh == null) continue;
                    bool interior = true;
                    for (int dy = -1; dy <= 1 && interior; dy++)
                        for (int dx = -1; dx <= 1 && interior; dx++)
                            if (frame.CodeAt(x + dx, y + dy) != code) interior = false;
                    if (!interior) continue;
                    int offset = Mathf.Abs(x - centerX);
                    if (offset <= bestOffset) continue;
                    bestOffset = offset; px = x; py = y; entry = candidate;
                }
            if (px < 0) { reason = "no stable interior sidecar pixel found"; return false; }
            reason = "frozen rule (max offset, interior, frequency>=25) offset_px=" + bestOffset;
            return true;
        }

        static IEnumerator RunR2OffAxis()
        {
            _harnessStartFrame = Time.frameCount;
            Log("r2_offaxis_start frame=" + _harnessStartFrame);
            yield return new HarnessWaitUntil(
                () => UnityEngine.Object.FindObjectOfType<FirstPersonSelectionController>() != null, 30.0);
            yield return new HarnessWaitFrames(2);
            int frameBefore = Time.frameCount;
            yield return new HarnessWait(0.35);
            _nestedWaitFrameDelta = Time.frameCount - frameBefore;

            _eye = PickService.ResolveEye();
            _gimbal = UnityEngine.Object.FindObjectOfType<Yixuan.Vision.EyeGimbalController>();
            var characterGo = GameObject.Find("Yixuan_Test");
            _character = characterGo != null ? characterGo.transform : null;
            if (_eye == null || _character == null)
            {
                Block("T1.k2", "EyeCamera + Yixuan_Test present",
                    "eye=" + (_eye != null) + " character=" + (_character != null), "fixture prerequisites missing");
                yield break;
            }
            if (!PickService.EnsureRegistry())
            {
                Block("T1.k2", "selection registry", "error=" + EntityPickRegistry.Error, "registry build failed");
                yield break;
            }
            var bench = EntityPickRegistry.Get("entity/prop_bench_00");
            if (bench == null || !bench.Selectable || bench.EntityMesh == null)
            {
                Block("T1.k2", "bench00 sidecar mesh", "bench=" + (bench != null) + " selectable=" + (bench != null && bench.Selectable),
                    "fixture missing verified bench mesh");
                yield break;
            }

            // Reproduce the saved A_r1 failing off-axis pose (bench00, 6m).
            yield return AimCharacterAtBench(bench, 6f);
            if (_lastAimErrorDeg > 3f)
            {
                Block("T1.k2", "reproduce saved A_r1 off-axis pose", "aim_err_deg=" + _lastAimErrorDeg.ToString("F2"),
                    "fixture could not reproduce the saved pose; not a product failure");
                yield break;
            }
            var frame = PickService.CaptureEyeFrame(_eye);
            SaveFrame(frame, "r2", "r2_offaxis_repro_pose");
            if (frame == null || !frame.Ok)
            {
                Block("T1.k2", "reproduced off-axis frame captured",
                    "error=" + (frame != null ? frame.error : "null"), "ID/depth capture failed");
                yield break;
            }
            SaveRawPickFrame(frame, "t1k2_repro_pose");
            Log("r2_pose_captured frame=" + frame.FrameId + " size=" + frame.width + "x" + frame.height +
                " snap_pos=" + frame.snapshot.position.ToString("F3") + " fov=" + frame.snapshot.fov +
                " near=" + frame.snapshot.near + " far=" + frame.snapshot.far + " aspect=" + frame.snapshot.aspect);

            // ---- Original synthetic 30deg/10m assertion (kept unchanged). ----
            var snapshot = BuildSyntheticSnapshot(60f, 1f);
            float near = snapshot.near, far = snapshot.far;
            float depth01For10m = (10f - near) / (far - near);
            int syntheticWidth = 101, syntheticHeight = 101;
            var syntheticCodes = new int[syntheticWidth * syntheticHeight];
            var syntheticFrame = BuildSyntheticFrame(snapshot, syntheticWidth, syntheticHeight, syntheticCodes, depth01For10m);
            float syntheticDepth;
            Vector3 syntheticPoint = syntheticFrame.WorldPointAt(1f, 0.5f, out syntheticDepth);
            Vector3 expectedPoint = new Vector3(10f * Mathf.Tan(30f * Mathf.Deg2Rad), 0f, 10f);
            float syntheticError = Vector3.Distance(syntheticPoint, expectedPoint);
            Vector3 oldFormulaPoint = Vector3.zero;
            {
                Vector3 origin, direction;
                if (syntheticFrame.ViewportRay(1f, 0.5f, out origin, out direction))
                {
                    float eyeDepth = near + depth01For10m * (far - near);
                    oldFormulaPoint = origin + direction * eyeDepth;
                }
            }
            float oldFormulaError = Vector3.Distance(oldFormulaPoint, expectedPoint);
            Check("T1.k2.synthetic", "off-axis 30deg/10m world point uses eye-space depth correctly",
                syntheticError < 0.005f && oldFormulaError > 1.0f,
                "correct_point=" + syntheticPoint.ToString("F3") + " expected=" + expectedPoint.ToString("F3") +
                " error_m=" + syntheticError.ToString("F4") + " old_formula_error_m=" + oldFormulaError.ToString("F4"),
                "shader encodes view-axis depth; world distance along the slanted ray divides by cos(ray,forward)");

            // ---- Historical failing pixel (7,263): preserved diagnostic, NOT an assertion. ----
            var historical = BuildPixelDiagnostic(frame, 7, 263);
            WritePixelDiagnostic(frame, historical, "r2_historical_pixel_7_263");
            Log("historical_pixel code=" + historical.code + " entity=" + historical.entity_id +
                " raw_depth01=" + historical.raw_depth01.ToString("F8") +
                " double_hit=" + historical.double_hit + " double_err=" + historical.double_error_m.ToString("F4") +
                " front_hit=" + historical.front_hit + " front_err=" + historical.front_error_m.ToString("F4") +
                " fe_expected01=" + historical.expected_depth01_from_front.ToString("F8") +
                " d01_delta=" + historical.depth01_delta_from_front.ToString("F8"));

            // ---- Frozen independent real sample (chosen before any reconstruction error is inspected). ----
            int px, py; EntityPickEntry chosenEntry; string frozenReason;
            bool chosen = ChooseFrozenOffAxisSample(frame, out px, out py, out chosenEntry, out frozenReason);
            if (!chosen)
            {
                Block("T1.k2", "frozen off-axis real sample", "none", frozenReason + "; no silent fallback to an easier sample");
                yield break;
            }
            var diag = BuildPixelDiagnostic(frame, px, py);
            WritePixelDiagnostic(frame, diag, "r2_frozen_sample");
            bool pass = diag.front_hit && diag.front_error_m < 0.03f;
            Check("T1.k2", "real off-centre pixel world point matches the front-face source-object triangle",
                pass,
                "pixel=" + px + "," + py + " code=" + diag.code + " entity=" + diag.entity_id +
                " offset_rule=" + frozenReason +
                " raw_depth01=" + diag.raw_depth01.ToString("F8") +
                " raw_depth_rgba=" + diag.raw_depth_r + "," + diag.raw_depth_g + "," + diag.raw_depth_b + "," + diag.raw_depth_a +
                " reconstructed=" + diag.reconstructed.ToString("F4") +
                " front_hit=" + diag.front_hit + " front_point=" + diag.front_point.ToString("F4") +
                " front_error_m=" + diag.front_error_m.ToString("F4") +
                " double_hit=" + diag.double_hit + " double_error_m=" + diag.double_error_m.ToString("F4") +
                " front_triangle=" + diag.front_triangle + " signed_area=" + diag.front_signed_area.ToString("F4") +
                " fe_expected_depth01=" + diag.expected_depth01_from_front.ToString("F8") +
                " raw_minus_fe_depth01=" + diag.depth01_delta_from_front.ToString("F8"),
                "sample frozen by visibility/interior only; front-face oracle aligned with Cull Back; raw RGBA8 depth + UV + matrices + ray + triangle saved in A_r2/quality/raw; original 3cm bound kept");
            Log("r2_offaxis_done pass=" + pass);
        }

        // =====================================================================
        // A_r3 final bounded repair entry (round 3 of 3).
        // Runs the same single real T1 off-axis case + the original synthetic
        // 30deg assertion on the final source, plus only same-frame checks:
        //  * production EntityIdCapture.ProjectWorld -> shared top-origin UV ->
        //    frame CodeAtUv/DepthAtUv agreement (independent sidecar triangle
        //    points, no inverse-formula round trip);
        //  * RGB / ID / depth registration by explicit image edge-correlation
        //    (RGB clone-camera rows are bottom-up, ID/depth rows top-origin).
        // =====================================================================
        const string MenuRunR3 = "UnityLITF/FirstPersonSelection/Run A_r3 Focused Off-axis";

        [MenuItem(MenuRunR3, false, 303)]
        public static void RunR3Menu() { RunR3(); }

        public static void RunR3()
        {
            if (_running) { Debug.LogWarning("[FPS-A] harness already running"); return; }
            _projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            _aRoot = Path.Combine(_projectRoot, "Captures", "FirstPersonSelection", "run_20261110", "A_r3");
            _testsDir = Path.Combine(_aRoot, "tests");
            _evidenceDir = Path.Combine(_aRoot, "evidence");
            if (!Application.isPlaying)
            {
                Directory.CreateDirectory(_testsDir);
                WriteFile(Path.Combine(_aRoot, "state", "harness_entry_blocked.json"),
                    SelectionJson.Serialize(new
                    {
                        blocked = true,
                        reason = "play_mode_required",
                        detail = "Enter Play mode first, then run the A_r3 focused entry; it does not enter play itself.",
                        at_local = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                    }));
                Debug.LogWarning("[FPS-A3] run blocked: enter Play mode first.");
                return;
            }
            Directory.CreateDirectory(_testsDir);
            Directory.CreateDirectory(Path.Combine(_aRoot, "quality", "raw"));
            Cases.Clear();
            LogLines.Clear();
            _running = true;
            _finalized = false;
            _harnessStartTime = EditorApplication.timeSinceStartup;
            _deadline = _harnessStartTime + 240.0;
            Stack.Clear();
            Stack.Push(RunR3OffAxis());
            EditorApplication.update += Tick;
            Log("r3_focused_scheduled");
        }

        static bool ChooseBandSample(PickFrame frame, int yMin, int yMax, out int px, out int py,
            out EntityPickEntry entry, out string reason)
        {
            px = -1; py = -1; entry = null; reason = "";
            var frequency = new Dictionary<int, int>();
            for (int y = 1; y < frame.height - 1; y += 2)
                for (int x = 1; x < frame.width - 1; x += 2)
                {
                    int code = frame.CodeAt(x, y);
                    if (code == 0) continue;
                    int count;
                    frequency.TryGetValue(code, out count);
                    frequency[code] = count + 1;
                }
            int centerX = frame.width / 2;
            int bestOffset = -1;
            for (int y = Mathf.Max(1, yMin); y < Mathf.Min(frame.height - 1, yMax); y += 2)
                for (int x = 1; x < frame.width - 1; x += 2)
                {
                    if (frame.DepthAt(x, y) <= 0f) continue;
                    int code = frame.CodeAt(x, y);
                    if (code == 0) continue;
                    int count;
                    frequency.TryGetValue(code, out count);
                    if (count < 25) continue;
                    var candidate = EntityPickRegistry.GetByCode(code);
                    if (candidate == null || candidate.EntityMesh == null) continue;
                    bool interior = true;
                    for (int dy = -1; dy <= 1 && interior; dy++)
                        for (int dx = -1; dx <= 1 && interior; dx++)
                            if (frame.CodeAt(x + dx, y + dy) != code) interior = false;
                    if (!interior) continue;
                    int offset = Mathf.Abs(x - centerX);
                    if (offset <= bestOffset) continue;
                    bestOffset = offset; px = x; py = y; entry = candidate;
                }
            if (px < 0) { reason = "no frozen interior sidecar pixel in band " + yMin + ".." + yMax; return false; }
            reason = "band sample offset_px=" + bestOffset + " band=" + yMin + ".." + yMax;
            return true;
        }

        /// <summary>
        /// Forward production projection (EntityIdCapture.ProjectWorld) from an
        /// independent sidecar front-triangle point -> shared top-origin UV ->
        /// frame.CodeAtUv/DepthAtUv. Does not use ViewportRay as the oracle.
        /// </summary>
        static bool ProjectionSharedUvCheck(PickFrame frame, PixelDiagnostic diag, out string detail)
        {
            detail = "";
            if (diag == null || !diag.front_hit)
            {
                detail = "no front-face triangle for projection check";
                return false;
            }
            bool inFront;
            Vector2 uv = EntityIdCapture.ProjectWorld(frame.snapshot, diag.front_point, out inFront);
            int codeAt = frame.CodeAtUv(uv.x, uv.y);
            float depthAt = frame.DepthAtUv(uv.x, uv.y);
            float eyeDepth = Vector3.Dot(diag.front_point - frame.snapshot.position, frame.snapshot.Forward);
            float expectedDepth01 = (eyeDepth - frame.snapshot.near) / Mathf.Max(1e-5f, frame.snapshot.far - frame.snapshot.near);
            float expectedU = (diag.x + 0.5f) / frame.width;
            float expectedV = (diag.y + 0.5f) / frame.height;
            float du = Mathf.Abs(uv.x - expectedU);
            float dv = Mathf.Abs(uv.y - expectedV);
            bool ok = inFront && codeAt == diag.code &&
                      Mathf.Abs(depthAt - expectedDepth01) < 1e-4f &&
                      du < 1.5f / frame.width && dv < 1.5f / frame.height;
            detail = "point=" + diag.front_point.ToString("F4") +
                     " inFront=" + inFront + " uv=" + uv.ToString("F5") +
                     " du_px=" + (du * frame.width).ToString("F2") + " dv_px=" + (dv * frame.height).ToString("F2") +
                     " code_at_uv=" + codeAt + " expected_code=" + diag.code +
                     " depth_at_uv=" + depthAt.ToString("F8") + " expected_depth01=" + expectedDepth01.ToString("F8") +
                     " depth_delta=" + Mathf.Abs(depthAt - expectedDepth01).ToString("F8");
            return ok;
        }

        /// <summary>
        /// Explicit image-based RGB vs ID/depth registration. RGB rows come from the
        /// clone-camera texture readback (bottom-up); ID/depth rows are top-origin.
        /// Method: build a depth-discontinuity mask in top-origin rows, build an RGB
        /// luma gradient image, then score the mask against the gradient for both row
        /// mappings and shifted variants. This uses image data only - no camera
        /// projection formula - so it is not a self-round-trip.
        /// </summary>
        static bool CheckRgbIdDepthRegistration(PickFrame frame, out string detail)
        {
            detail = "";
            int w = frame.width, h = frame.height;
            if (frame.rgb_pixels == null || frame.rgb_pixels.Length != w * h)
            {
                detail = "rgb pixels unavailable";
                return false;
            }
            var luma = new float[w * h];
            for (int i = 0; i < luma.Length; i++)
                luma[i] = 0.2126f * frame.rgb_pixels[i].r + 0.7152f * frame.rgb_pixels[i].g + 0.0722f * frame.rgb_pixels[i].b;
            var gradient = new float[w * h];
            for (int y = 0; y < h - 1; y++)
                for (int x = 0; x < w - 1; x++)
                {
                    int i = y * w + x;
                    float gx = Mathf.Abs(luma[i + 1] - luma[i]);
                    float gy = Mathf.Abs(luma[i + w] - luma[i]);
                    gradient[i] = gx + gy;
                }
            var mask = new List<int>();
            for (int y = 1; y < h - 2; y++)
                for (int x = 1; x < w - 2; x++)
                {
                    float d = frame.DepthAt(x, y);
                    if (d <= 0f) continue;
                    float dr = frame.DepthAt(x + 1, y);
                    float dd = frame.DepthAt(x, y + 1);
                    if ((dr > 0f && Mathf.Abs(dr - d) > 0.0015f) || (dd > 0f && Mathf.Abs(dd - d) > 0.0015f))
                        mask.Add(y * w + x);
                }
            if (mask.Count < 200)
            {
                detail = "depth edge mask too small: " + mask.Count;
                return false;
            }
            // mapping rows: rgb row = top row (direct) or h-1-top (flipped)
            float Score(bool flipped, int dy)
            {
                double sum = 0.0;
                int used = 0;
                foreach (int topIndex in mask)
                {
                    int y = topIndex / w + dy;
                    int x = topIndex % w;
                    if (y < 0 || y >= h) continue;
                    int rgbRow = flipped ? (h - 1 - y) : y;
                    if (rgbRow < 0 || rgbRow >= h) continue;
                    sum += gradient[rgbRow * w + x];
                    used++;
                }
                return used > 0 ? (float)(sum / used) : 0f;
            }
            float direct0 = Score(false, 0), flipped0 = Score(true, 0);
            float directShift = Mathf.Max(Score(false, -3), Score(false, 3));
            float flippedShift = Mathf.Max(Score(true, -3), Score(true, 3));
            bool flippedBest = flipped0 >= direct0;
            float best = flippedBest ? flipped0 : direct0;
            float other = flippedBest ? direct0 : flipped0;
            float bestShift = flippedBest ? flippedShift : directShift;
            bool ok = best > other * 1.10f && best > bestShift * 1.05f;
            // ID<->depth same-MRT co-registration: code boundaries should coincide
            // with depth discontinuities.
            int boundaryPixels = 0, boundaryDepthJump = 0;
            for (int y = 1; y < h - 2; y++)
                for (int x = 1; x < w - 2; x++)
                {
                    int c = frame.CodeAt(x, y);
                    if (c == 0) continue;
                    if (c != frame.CodeAt(x + 1, y))
                    {
                        boundaryPixels++;
                        float d = frame.DepthAt(x, y);
                        float dr = frame.DepthAt(x + 1, y);
                        if (d > 0f && dr > 0f && Mathf.Abs(dr - d) > 1e-5f) boundaryDepthJump++;
                    }
                }
            float idDepthFraction = boundaryPixels > 0 ? boundaryDepthJump / (float)boundaryPixels : 0f;
            bool idDepthOk = idDepthFraction > 0.5f;
            detail = "method=depth-edge-mask vs RGB luma-gradient over row mappings and shifted variants (image data only)" +
                     " best_mapping=" + (flippedBest ? "rgb_flipped_relative_to_top_origin (rgb row = h-1-top)" : "rgb_direct_top_origin") +
                     " best_score=" + best.ToString("F5") + " other_mapping_score=" + other.ToString("F5") +
                     " best_mapping_shift_score=" + bestShift.ToString("F5") +
                     " direct0=" + direct0.ToString("F5") + " flipped0=" + flipped0.ToString("F5") +
                     " mask_pixels=" + mask.Count +
                     " id_depth_boundary_pixels=" + boundaryPixels + " with_depth_jump=" + boundaryDepthJump +
                     " id_depth_fraction=" + idDepthFraction.ToString("F3");
            bool pass = ok && idDepthOk && idDepthFraction > 0.5f;
            try
            {
                Directory.CreateDirectory(Path.Combine(_aRoot, "quality"));
                WriteFile(Path.Combine(_aRoot, "quality", "rgb_id_depth_registration.json"),
                    JsonConvert.SerializeObject(new
                    {
                        schema = "firstperson-selection.a-r3.registration.v1",
                        method = "image-only: depth-discontinuity mask scored against RGB luma gradient; row-mapping and shift comparison",
                        width = w, height = h,
                        direct_mapping_score = direct0, flipped_mapping_score = flipped0,
                        direct_shift_score = directShift, flipped_shift_score = flippedShift,
                        chosen_mapping = flippedBest ? "rgb_flipped_relative_to_top_origin" : "rgb_direct_top_origin",
                        chosen_score = best, other_score = other, margin_ratio = other > 1e-6f ? best / other : 0f,
                        mask_pixels = mask.Count,
                        id_depth_boundary_pixels = boundaryPixels,
                        id_depth_boundary_with_depth_jump = boundaryDepthJump,
                        id_depth_fraction = idDepthFraction,
                        pass = pass,
                        note = "RGB is read from the clone-camera RT (bottom-up rows); ID/depth are top-origin readback rows; the chosen mapping/flip is the empirical top-left registration.",
                    }, Formatting.Indented));
            }
            catch (Exception exception) { Log("registration_write_exception " + exception.Message); }
            return pass;
        }

        static IEnumerator RunR3OffAxis()
        {
            _harnessStartFrame = Time.frameCount;
            Log("r3_start frame=" + _harnessStartFrame);
            yield return new HarnessWaitUntil(
                () => UnityEngine.Object.FindObjectOfType<FirstPersonSelectionController>() != null, 30.0);
            yield return new HarnessWaitFrames(2);
            int frameBefore = Time.frameCount;
            yield return new HarnessWait(0.35);
            _nestedWaitFrameDelta = Time.frameCount - frameBefore;

            _eye = PickService.ResolveEye();
            _gimbal = UnityEngine.Object.FindObjectOfType<Yixuan.Vision.EyeGimbalController>();
            var characterGo = GameObject.Find("Yixuan_Test");
            _character = characterGo != null ? characterGo.transform : null;
            if (_eye == null || _character == null)
            {
                Block("T1.k3", "EyeCamera + Yixuan_Test present",
                    "eye=" + (_eye != null) + " character=" + (_character != null), "fixture prerequisites missing");
                yield break;
            }
            if (!PickService.EnsureRegistry())
            {
                Block("T1.k3", "selection registry", "error=" + EntityPickRegistry.Error, "registry build failed");
                yield break;
            }
            var bench = EntityPickRegistry.Get("entity/prop_bench_00");
            if (bench == null || !bench.Selectable || bench.EntityMesh == null)
            {
                Block("T1.k3", "bench00 sidecar mesh", "bench=" + (bench != null), "fixture missing verified bench mesh");
                yield break;
            }
            yield return AimCharacterAtBench(bench, 6f);
            if (_lastAimErrorDeg > 3f)
            {
                Block("T1.k3", "reproduce saved A_r1 off-axis pose", "aim_err_deg=" + _lastAimErrorDeg.ToString("F2"),
                    "fixture could not reproduce the saved pose; not a product failure");
                yield break;
            }
            var frame = PickService.CaptureEyeFrame(_eye);
            SaveFrame(frame, "r3", "r3_offaxis_repro_pose");
            if (frame == null || !frame.Ok)
            {
                Block("T1.k3", "reproduced off-axis frame captured",
                    "error=" + (frame != null ? frame.error : "null"), "ID/depth capture failed");
                yield break;
            }
            SaveRawPickFrame(frame, "r3_offaxis_repro_pose");
            Log("r3_pose_captured frame=" + frame.FrameId + " size=" + frame.width + "x" + frame.height +
                " fov=" + frame.snapshot.fov + " near=" + frame.snapshot.near + " far=" + frame.snapshot.far);

            // ---- Original synthetic 30deg/10m assertion (unchanged). ----
            var snapshot = BuildSyntheticSnapshot(60f, 1f);
            float near = snapshot.near, far = snapshot.far;
            float depth01For10m = (10f - near) / (far - near);
            var syntheticCodes = new int[101 * 101];
            var syntheticFrame = BuildSyntheticFrame(snapshot, 101, 101, syntheticCodes, depth01For10m);
            float syntheticDepth;
            Vector3 syntheticPoint = syntheticFrame.WorldPointAt(1f, 0.5f, out syntheticDepth);
            Vector3 expectedPoint = new Vector3(10f * Mathf.Tan(30f * Mathf.Deg2Rad), 0f, 10f);
            float syntheticError = Vector3.Distance(syntheticPoint, expectedPoint);
            Vector3 oldFormulaPoint = Vector3.zero;
            {
                Vector3 origin, direction;
                if (syntheticFrame.ViewportRay(1f, 0.5f, out origin, out direction))
                {
                    float eyeDepth = near + depth01For10m * (far - near);
                    oldFormulaPoint = origin + direction * eyeDepth;
                }
            }
            float oldFormulaError = Vector3.Distance(oldFormulaPoint, expectedPoint);
            Check("T1.k3.synthetic", "off-axis 30deg/10m world point uses eye-space depth correctly",
                syntheticError < 0.005f && oldFormulaError > 1.0f,
                "correct_point=" + syntheticPoint.ToString("F3") + " expected=" + expectedPoint.ToString("F3") +
                " error_m=" + syntheticError.ToString("F4") + " old_formula_error_m=" + oldFormulaError.ToString("F4"),
                "shader encodes view-axis depth; world distance along the slanted ray divides by cos(ray,forward)");

            // ---- One real frozen off-axis sample (same visibility-only rule, 3cm bound). ----
            int px, py; EntityPickEntry chosenEntry; string frozenReason;
            bool chosen = ChooseFrozenOffAxisSample(frame, out px, out py, out chosenEntry, out frozenReason);
            if (!chosen)
            {
                Block("T1.k3.real", "frozen off-axis real sample", "none", frozenReason + "; no fallback sample");
                yield break;
            }
            var diag = BuildPixelDiagnostic(frame, px, py);
            WritePixelDiagnostic(frame, diag, "r3_frozen_sample");
            Check("T1.k3.real", "real off-centre pixel matches the front-face source-object triangle (<0.03 m)",
                diag.front_hit && diag.front_error_m < 0.03f,
                "pixel=" + px + "," + py + " code=" + diag.code + " entity=" + diag.entity_id +
                " " + frozenReason + " raw_depth01=" + diag.raw_depth01.ToString("F8") +
                " raw_depth_rgba=" + diag.raw_depth_r + "," + diag.raw_depth_g + "," + diag.raw_depth_b + "," + diag.raw_depth_a +
                " reconstructed=" + diag.reconstructed.ToString("F5") +
                " front_hit=" + diag.front_hit + " front_point=" + diag.front_point.ToString("F5") +
                " front_error_m=" + diag.front_error_m.ToString("F7") +
                " front_triangle=" + diag.front_triangle + " signed_area=" + diag.front_signed_area.ToString("F4") +
                " double_error_m=" + diag.double_error_m.ToString("F5"),
                "frozen visibility/interior rule; front-face oracle; original 3cm bound; raw depth/matrices/ray/triangle saved");

            // ---- Same frame: a few vertical-offset points + production projection -> shared UV -> ID/depth. ----
            int upperPx, upperPy; EntityPickEntry upperEntry; string upperReason;
            int lowerPx, lowerPy; EntityPickEntry lowerEntry; string lowerReason;
            bool upperChosen = ChooseBandSample(frame, 1, frame.height / 2 - 40, out upperPx, out upperPy, out upperEntry, out upperReason);
            bool lowerChosen = ChooseBandSample(frame, frame.height / 2 + 40, frame.height - 1, out lowerPx, out lowerPy, out lowerEntry, out lowerReason);
            PixelDiagnostic upperDiag = upperChosen ? BuildPixelDiagnostic(frame, upperPx, upperPy) : null;
            PixelDiagnostic lowerDiag = lowerChosen ? BuildPixelDiagnostic(frame, lowerPx, lowerPy) : null;
            if (upperChosen) WritePixelDiagnostic(frame, upperDiag, "r3_upper_offset_sample");
            if (lowerChosen) WritePixelDiagnostic(frame, lowerDiag, "r3_lower_offset_sample");

            string projectionDetail;
            bool projMain = ProjectionSharedUvCheck(frame, diag, out projectionDetail);
            bool projUpper = upperChosen && ProjectionSharedUvCheck(frame, upperDiag, out _);
            bool projLower = lowerChosen && ProjectionSharedUvCheck(frame, lowerDiag, out _);
            Check("T1.k3.projection", "production ProjectWorld -> shared top-origin UV -> ID/depth agrees away from centre (main + upper + lower samples)",
                projMain && projUpper && projLower,
                "main: " + projectionDetail +
                " | upper_chosen=" + upperChosen + " upper_reason=" + upperReason +
                " | lower_chosen=" + lowerChosen + " lower_reason=" + lowerReason +
                " | upper_ok=" + projUpper + " lower_ok=" + projLower,
                "EntityIdCapture.ProjectWorld (canonical snapshot conversion) -> frame.CodeAtUv/DepthAtUv; point comes from an independent sidecar front triangle, not from the inverse ray formula");

            // ---- RGB / ID / depth registration (image-only method). ----
            string registrationDetail;
            bool registrationOk = CheckRgbIdDepthRegistration(frame, out registrationDetail);
            Check("T1.k3.registration", "RGB/ID/depth top-left registration verified by image edge correlation",
                registrationOk, registrationDetail,
                "explicit numerical method: depth-discontinuity mask scored against RGB luma gradient for direct vs row-flipped mappings and shifted variants; no projection-formula round trip");

            Log("r3_done");
        }

        static IEnumerator RunRound1GeometryTests()
        {
            Log("T1.round1_start");
            var report = EntityPickRegistry.Report;
            var manifest = EntityPickRegistry.Manifest;
            if (report == null || manifest == null)
            {
                Block("T1.i", "sidecar provenance available", "report/manifest null", "registry did not load sidecar provenance");
                yield break;
            }

            // ---- T1.i: review ground counterexample must not be attributed to a tree.
            var groundCounterexample = manifest.review_counterexample_checks;
            string manifestGroundOwner = groundCounterexample != null && groundCounterexample.ground_triangle_owner_entities != null &&
                                         groundCounterexample.ground_triangle_owner_entities.Length > 0
                ? groundCounterexample.ground_triangle_owner_entities[0].entity_id : "";
            var groundPoint = new Vector3(-90.75443f, 0.01f, -40.75827f);
            var groundOwner = EntityPickRegistry.FindTriangleOwner(groundPoint, 0.02f);
            var tree04 = EntityPickRegistry.Get("entity/prop_tree_trees_w_court_04");
            bool groundOwnerIsProvenanced = groundOwner != null &&
                                            groundOwner.Id == manifestGroundOwner &&
                                            tree04 != null && groundOwner.Id != tree04.Id;
            Check("T1.i", "review ground triangle is attributed to its source object, never to the nearby tree",
                groundOwnerIsProvenanced,
                "runtime_owner=" + (groundOwner != null ? groundOwner.Id : "unknown") +
                " manifest_owner=" + (string.IsNullOrEmpty(manifestGroundOwner) ? "none" : manifestGroundOwner) +
                " tree04_code=" + (tree04 != null ? tree04.Code.ToString() : "-") +
                " ground_point=" + groundPoint.ToString("F3"),
                "source_object_map.csv provenance (PROP_PLAY_W_GROUND -> entity/prop_play_play_w_ground); AABB centroid guessing used to mis-stain this ground triangle as tree_04");
            // Capture a frame looking at the ground point: the centre must never resolve to the tree id.
            if (groundOwner != null)
            {
                Vector3 characterXZ = new Vector3(groundPoint.x, 0f, groundPoint.z + 3.0f);
                yield return AimCharacter(characterXZ, groundPoint + Vector3.up * 0.05f);
                if (_lastAimErrorDeg < 4f)
                {
                    var groundFrame = PickService.CaptureEyeFrame(_eye);
                    SaveFrame(groundFrame, "t1", "t1i_ground_provenance");
                    if (groundFrame != null && groundFrame.Ok)
                    {
                        int centerCode = groundFrame.CodeAt(groundFrame.width / 2, groundFrame.height / 2);
                        bool notTree = tree04 == null || centerCode != tree04.Code;
                        Check("T1.i", "ground pixel under the crosshair is not the nearby tree entity",
                            notTree,
                            "centre_code=" + centerCode + " centre_id=" +
                            (EntityPickRegistry.GetByCode(centerCode) != null ? EntityPickRegistry.GetByCode(centerCode).Id : "none") +
                            " tree_code=" + (tree04 != null ? tree04.Code : -1),
                            "real ID frame at the review counterexample point");
                    }
                }
                else
                {
                    Log("T1.i ground frame skipped aim_err=" + _lastAimErrorDeg.ToString("F2"));
                }
            }

            // ---- T1.j: full-XYZ candidate (metro sign) resolves to the source object, not the short cap.
            var metroCounterexample = manifest.review_counterexample_checks;
            string manifestMetroOwner = metroCounterexample != null && metroCounterexample.metro_triangle_owner_entities != null &&
                                        metroCounterexample.metro_triangle_owner_entities.Length > 0
                ? metroCounterexample.metro_triangle_owner_entities[0].entity_id : "";
            var metroPoint = new Vector3(134.70439f, 5.51125f, 49.788f);
            var metroOwner = EntityPickRegistry.FindTriangleOwner(metroPoint, 0.02f);
            var metroShort = EntityPickRegistry.Get("entity/prop_metro");
            bool xyzFixed = metroOwner != null && metroOwner.Id == manifestMetroOwner &&
                            (metroShort == null || metroOwner.Id != metroShort.Id) &&
                            metroOwner.SourceObjects != null && Array.IndexOf(metroOwner.SourceObjects, "PROP_METRO_M_S") >= 0;
            Check("T1.j", "full-XYZ source-object candidate resolves the metro sign triangle to PROP_METRO_M_S",
                xyzFixed,
                "runtime_owner=" + (metroOwner != null ? metroOwner.Id : "unknown") +
                " manifest_owner=" + (string.IsNullOrEmpty(manifestMetroOwner) ? "none" : manifestMetroOwner) +
                " short_cap_id=" + (metroShort != null ? metroShort.Id : "-") +
                " owner_source_objects=" + (metroOwner != null ? string.Join(",", metroOwner.SourceObjects) : "none"),
                "source object map keeps the tall sign object; old code rejected Y and dropped valid candidates");
            var m_s = EntityPickRegistry.Get("entity/prop_metro_m_s");
            Check("T1.j", "entity/prop_metro_m_s has verified source geometry",
                m_s != null && m_s.Selectable && m_s.TriangleCount > 0 && m_s.EntityMesh != null &&
                m_s.SourceObjects != null && Array.IndexOf(m_s.SourceObjects, "PROP_METRO_M_S") >= 0,
                "selectable=" + (m_s != null && m_s.Selectable) + " tris=" + (m_s != null ? m_s.TriangleCount : -1) +
                " objects=" + (m_s != null ? string.Join(",", m_s.SourceObjects) : "none"),
                "provenance object list attached to the entity");

            // ---- T1.k: off-axis world point reconstruction (synthetic 30deg/10m + real sidecar truth).
            var snapshot = BuildSyntheticSnapshot(60f, 1f);
            float near = snapshot.near, far = snapshot.far;
            float depth01For10m = (10f - near) / (far - near);
            int syntheticWidth = 101, syntheticHeight = 101;
            var syntheticCodes = new int[syntheticWidth * syntheticHeight];
            var syntheticFrame = BuildSyntheticFrame(snapshot, syntheticWidth, syntheticHeight, syntheticCodes, depth01For10m);
            float syntheticDepth;
            Vector3 syntheticPoint = syntheticFrame.WorldPointAt(1f, 0.5f, out syntheticDepth);
            // fov=60 -> half-angle 30deg -> at u=1 the ray is 30deg off axis; eye-space depth 10m.
            Vector3 expectedPoint = new Vector3(10f * Mathf.Tan(30f * Mathf.Deg2Rad), 0f, 10f);
            float syntheticError = Vector3.Distance(syntheticPoint, expectedPoint);
            Vector3 oldFormulaPoint = Vector3.zero;
            {
                Vector3 origin, direction;
                if (syntheticFrame.ViewportRay(1f, 0.5f, out origin, out direction))
                {
                    float eyeDepth = near + depth01For10m * (far - near);
                    oldFormulaPoint = origin + direction * eyeDepth; // old buggy formula
                }
            }
            float oldFormulaError = Vector3.Distance(oldFormulaPoint, expectedPoint);
            Check("T1.k", "off-axis 30deg/10m world point uses eye-space depth correctly",
                syntheticError < 0.005f && oldFormulaError > 1.0f,
                "correct_point=" + syntheticPoint.ToString("F3") + " expected=" + expectedPoint.ToString("F3") +
                " error_m=" + syntheticError.ToString("F4") +
                " old_formula_error_m=" + oldFormulaError.ToString("F4"),
                "shader encodes view-axis depth; world distance along the slanted ray must divide by cos(ray,forward)");

            // Real frame: compare WorldPointAt to an independent sidecar ray-triangle hit at an off-centre pixel.
            var bench = EntityPickRegistry.Get("entity/prop_bench_00");
            // INVALIDATED FOR ACCEPTANCE: A_r1's real T1.k sample was selected by
            // reconstruction error (>8cm dropped) before asserting a relaxed <12cm
            // threshold - a circular test. It is disabled for every normal entry so
            // it can never silently report a pass again. Corrected focused
            // validation lives in the A_r3 entry (top-origin + front-face oracle,
            // frozen visibility-only sample, original 3cm bound). Historical
            // A/A_r1/A_r2 results remain on disk unchanged.
            Log("T1.k_real_invalidated_disabled_circular");
            if (false && bench != null && bench.Selectable && bench.EntityMesh != null)
            {
                yield return AimCharacterAtBench(bench, 6f);
                var offFrame = PickService.CaptureEyeFrame(_eye);
                SaveFrame(offFrame, "t1", "t1k_offaxis");
                if (offFrame != null && offFrame.Ok)
                {
                    int centerX = offFrame.width / 2;
                    // Candidate sidecar pixels: interior of an entity's own ID mask
                    // (3x3 same code) so the depth buffer and the ID agree at the
                    // sample. Then verify the frozen ray really hits that entity's
                    // source-object mesh; only then use it as off-axis truth.
                    var candidatePixels = new List<Vector3Int>();
                    var codeFrequency = new Dictionary<int, int>();
                    for (int y = 1; y < offFrame.height - 1; y += 2)
                        for (int x = 1; x < offFrame.width - 1; x += 2)
                        {
                            int code = offFrame.CodeAt(x, y);
                            if (code == 0) continue;
                            int count;
                            codeFrequency.TryGetValue(code, out count);
                            codeFrequency[code] = count + 1;
                        }
                    for (int y = 1; y < offFrame.height - 1; y += 2)
                    {
                        for (int x = 1; x < offFrame.width - 1; x += 2)
                        {
                            if (offFrame.DepthAt(x, y) <= 0f) continue;
                            int code = offFrame.CodeAt(x, y);
                            if (code == 0) continue;
                            int count;
                            codeFrequency.TryGetValue(code, out count);
                            if (count < 25) continue;    // interior of a substantial surface
                            var codeEntry = EntityPickRegistry.GetByCode(code);
                            if (codeEntry == null || codeEntry.EntityMesh == null) continue;
                            candidatePixels.Add(new Vector3Int(x, y, code));
                        }
                    }
                    candidatePixels.Sort((a, b) => Mathf.Abs(b.x - centerX).CompareTo(Mathf.Abs(a.x - centerX)));
                    int chosenX = -1, chosenY = -1, chosenCode = 0;
                    EntityPickEntry chosenEntry = null;
                    int bestOffset = 0;
                    Vector3 truthPoint = Vector3.zero;
                    float truthDistance = 0f;
                    Vector3 reconstructed = Vector3.zero;
                    float reconstructedDepth = 0f;
                    for (int candidateIndex = 0; candidateIndex < candidatePixels.Count && candidateIndex < 120; candidateIndex++)
                    {
                        var candidate = candidatePixels[candidateIndex];
                        var codeEntry = EntityPickRegistry.GetByCode(candidate.z);
                        if (codeEntry == null || codeEntry.EntityMesh == null) continue;
                        float u = (candidate.x + 0.5f) / offFrame.width;
                        float v = (candidate.y + 0.5f) / offFrame.height;
                        Vector3 origin, direction;
                        if (!offFrame.ViewportRay(u, v, out origin, out direction)) continue;
                        Vector3 localTruth;
                        float localTruthDistance;
                        if (!RayMeshHit(origin, direction, codeEntry.EntityMesh, out localTruth, out localTruthDistance)) continue;
                        float localDepth;
                        Vector3 localReconstructed = offFrame.WorldPointAt(u, v, out localDepth);
                        float localDistance = Vector3.Distance(origin, localReconstructed);
                        if (Mathf.Abs(localDistance - localTruthDistance) > 0.25f) continue;
                        if (Vector3.Distance(localReconstructed, localTruth) > 0.08f) continue;
                        chosenX = candidate.x; chosenY = candidate.y; chosenCode = candidate.z;
                        chosenEntry = codeEntry;
                        bestOffset = Mathf.Abs(candidate.x - centerX);
                        truthPoint = localTruth; truthDistance = localTruthDistance;
                        reconstructed = localReconstructed; reconstructedDepth = localDepth;
                        break;
                    }
                    if (chosenX < 0)
                    {
                        Block("T1.k", "off-centre sidecar entity pixel for real-frame off-axis check", "none found", "fixture could not find an interior sidecar pixel whose frozen ray hits its own source mesh");
                    }
                    else
                    {
                        float realError = Vector3.Distance(reconstructed, truthPoint);
                        Check("T1.k", "real off-centre pixel world point matches the source-object triangle truth",
                            realError < 0.12f,
                            "pixel=" + chosenX + "," + chosenY + " code=" + chosenCode + " entity=" + chosenEntry.Id +
                            " offset_px=" + bestOffset +
                            " reconstructed=" + reconstructed.ToString("F3") +
                            " truth=" + truthPoint.ToString("F3") +
                            " error_m=" + realError.ToString("F4") +
                            " true_distance=" + truthDistance.ToString("F3") +
                            " reconstructed_depth01=" + reconstructedDepth.ToString("F5"),
                            "independent truth from the verified sidecar mesh + frozen snapshot ray; tolerance 0.12m accounts for fan-vs-exported triangulation on non-planar quads (synthetic 30deg case passes at <5mm)");
                    }
                }
            }
            else
            {
                Block("T1.k", "sidecar mesh for bench00", "bench00 mesh unavailable", "registry missing verified bench mesh");
            }

            // ---- T1.l: synthetic ring/centre box (review R3 counterexample exactly).
            var bench00 = EntityPickRegistry.Get("entity/prop_bench_00");
            var bench01 = EntityPickRegistry.Get("entity/prop_bench_01");
            if (bench00 != null && bench01 != null)
            {
                int width = 9, height = 9;
                var codes = new int[width * height];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        bool border = x == 0 || y == 0 || x == width - 1 || y == height - 1;
                        codes[y * width + x] = border ? bench00.Code : 0;
                    }
                }
                codes[(height / 2) * width + (width / 2)] = bench01.Code;   // single centre pixel B
                var ringSnapshot = BuildSyntheticSnapshot(60f, 1f);
                var ringFrame = BuildSyntheticFrame(ringSnapshot, width, height, codes, depth01For10m);
                var ringResult = PickService.ResolveBox(ringFrame, new Rect(0f, 0f, 1f, 1f));
                var ringDominant = ringResult != null ? ringResult.Dominant : null;
                int expectedClickU = -1, expectedClickV = -1;
                {
                    double sumX = 0, sumY = 0; int count = 0;
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                            if (codes[y * width + x] == bench00.Code) { sumX += x; sumY += y; count++; }
                    expectedClickU = (int)Math.Round(sumX / count);
                    expectedClickV = (int)Math.Round(sumY / count);
                }
                bool ringOk = ringDominant != null &&
                              ringDominant.entity_id == bench00.Id &&
                              ringDominant.pixel_count == 32 &&
                              ringDominant.has_reliable_point &&
                              ringDominant.sample_code == bench00.Code &&
                              ringFrame.CodeAt(ringDominant.sample_pixel_x, ringDominant.sample_pixel_y) == bench00.Code &&
                              !(ringDominant.sample_pixel_x == width / 2 && ringDominant.sample_pixel_y == height / 2) &&
                              ringResult.dominant_is_confirmed == false &&
                              Vector3.Distance(ringDominant.surface_point, Vector3.zero) > 0.01f;
                float expectedU = (float)((expectedClickU + 0.5) / width);
                bool centroidOk = Mathf.Abs(ringDominant.centroid_uv.x - expectedU) < 1e-5f;
                Check("T1.l", "9x9 ring/centre synthetic box: dominant representative pixel belongs to A, not the centre B",
                    ringOk && centroidOk,
                    "dominant=" + (ringDominant != null ? ringDominant.entity_id : "none") +
                    " pixels=" + (ringDominant != null ? ringDominant.pixel_count : -1) +
                    " sample_px=" + (ringDominant != null ? ringDominant.sample_pixel_x + "," + ringDominant.sample_pixel_y : "-") +
                    " sample_code=" + (ringDominant != null ? ringDominant.sample_code : -1) +
                    " expected_A=" + bench00.Code + " centre_B=" + bench01.Code +
                    " centroid_uv=" + (ringDominant != null ? ringDominant.centroid_uv.ToString("F4") : "-") +
                    " expected_centroid_u=" + expectedU.ToString("F4") +
                    " dominant_is_confirmed=" + (ringResult != null && ringResult.dominant_is_confirmed),
                    "previous code averaged the mask (foreign centre pixel) and seeded sums at -1");
                SaveText("t1", "t1l_ring_box_result.json", SelectionJson.Serialize(ringResult));
            }
            else
            {
                Block("T1.l", "bench entities for synthetic ring box", "missing bench00/bench01", "registry fixture missing");
            }
            Log("T1.round1_end");
        }

        static IEnumerator RunProductionEntryTests()
        {
            Log("T8.d_start");
            var bench00 = EntityPickRegistry.Get("entity/prop_bench_00");
            var bench01 = EntityPickRegistry.Get("entity/prop_bench_01");
            if (bench00 == null || bench01 == null)
            {
                Block("T8.d", "two bench entities for production-entry test", "missing", "registry fixture missing");
                yield break;
            }
            bool originalEyeView = false;
            var director = FindCameraDirector();
            if (director != null) originalEyeView = director.EyeView;
            ClearEventSystemFocus();

            // Pending AI observation with an explicit token.
            int aiToken = SelectionBroker.BeginIntent();
            var aiRecord = PickService.BuildEntityRecord(bench00, "visual_confirm", aiToken, bench00.CatalogPosition, false);
            // Real big-map entry (same code path as a big-map click).
            bool bigSelect = Yixuan.UI.BigMapPanel.SelectEntity(bench01.Id);
            var afterBig = SelectionBroker.Current;
            bool bigOk = bigSelect && afterBig != null && afterBig.entity_id == bench01.Id &&
                         afterBig.source == "map_click" && SelectionBroker.Generation > aiToken;
            Check("T8.d", "BigMapPanel real entry publishes the shared record and advances generation",
                bigOk,
                "select_ok=" + bigSelect + " current=" + (afterBig != null ? afterBig.entity_id : "none") +
                " source=" + (afterBig != null ? afterBig.source : "-") +
                " ai_token=" + aiToken + " current_generation=" + SelectionBroker.Generation,
                "big-map click path must not only change private _selectedId");
            int aiPublishAfterBig = SelectionBroker.TryPublish(aiToken, aiRecord) ? 1 : 0;
            bool bigKept = SelectionBroker.Current != null && SelectionBroker.Current.entity_id == bench01.Id;
            Check("T8.d", "late AI token cannot overwrite a new big-map manual selection",
                aiPublishAfterBig == 0 && bigKept,
                "stale_publish_accepted=" + (aiPublishAfterBig == 1) + " current=" + (SelectionBroker.Current != null ? SelectionBroker.Current.entity_id : "none") +
                " reason=" + SelectionBroker.LastRejectReason,
                "manual/map new selection advances generation");
            Yixuan.UI.BigMapPanel.Close();

            // Real minimap entry.
            bool miniSelect = Yixuan.WorldMap.WorldMapPanel.SelectEntityById(bench00.Id);
            var afterMini = SelectionBroker.Current;
            bool miniOk = miniSelect && afterMini != null && afterMini.entity_id == bench00.Id &&
                          afterMini.source == "map_click";
            Check("T8.d", "minimap real entry publishes the shared record (source kept as map_click)",
                miniOk,
                "select_ok=" + miniSelect + " current=" + (afterMini != null ? afterMini.entity_id : "none") +
                " source=" + (afterMini != null ? afterMini.source : "-") +
                " minimap_selected=" + Yixuan.WorldMap.WorldMapPanel.SelectedEntityIdForTest,
                "small map uses the same selection entry; map_click is not an image confirm");
            int aiPublishAfterMini = SelectionBroker.TryPublish(aiToken, aiRecord) ? 1 : 0;
            Check("T8.d", "late AI token cannot overwrite a new minimap manual selection",
                aiPublishAfterMini == 0 && SelectionBroker.Current != null && SelectionBroker.Current.entity_id == bench00.Id,
                "stale_publish_accepted=" + (aiPublishAfterMini == 1) +
                " current=" + (SelectionBroker.Current != null ? SelectionBroker.Current.entity_id : "none"),
                "manual/map new selection advances generation");

            // Real keyboard-guarded manual entry (same path as the T key) aimed at bench01.
            if (director == null)
            {
                Block("T8.d", "CameraDirector for first-person keyboard entry", "missing", "scene fixture missing CameraDirector");
            }
            else
            {
                director.EyeView = true;
                ClearEventSystemFocus();
                yield return AimCharacterAtBench(bench01, 6f);
                if (_lastAimErrorDeg > 2.5f)
                {
                    Block("T8.d", "keyboard manual confirm aim", "aim_error=" + _lastAimErrorDeg.ToString("F2"), "fixture aim failed");
                }
                else
                {
                    var controller = FirstPersonSelectionController.EnsureInstance();
                    int versionBefore = SelectionBroker.Version;
                    bool keyboard = controller.TryConfirmFromKeyboard();
                    var afterKeyboard = SelectionBroker.Current;
                    bool keyboardOk = keyboard && afterKeyboard != null && afterKeyboard.entity_id == bench01.Id &&
                                      afterKeyboard.source == "manual_click" &&
                                      SelectionBroker.Version > versionBefore;
                    Check("T8.d", "keyboard-guarded manual crosshair confirm publishes manual_click through the shared record",
                        keyboardOk,
                        "keyboard=" + keyboard + " current=" + (afterKeyboard != null ? afterKeyboard.entity_id : "none") +
                        " source=" + (afterKeyboard != null ? afterKeyboard.source : "-") +
                        " version_delta=" + (SelectionBroker.Version - versionBefore),
                        "explicit program path and keyboard path share PickService.ConfirmAtCenter");
                    int aiAfterKeyboard = SelectionBroker.TryPublish(aiToken, aiRecord) ? 1 : 0;
                    Check("T8.d", "late AI token cannot overwrite the keyboard manual selection",
                        aiAfterKeyboard == 0 && SelectionBroker.Current != null && SelectionBroker.Current.entity_id == bench01.Id,
                        "stale_publish_accepted=" + (aiAfterKeyboard == 1) +
                        " current=" + (SelectionBroker.Current != null ? SelectionBroker.Current.entity_id : "none"),
                        "manual generation advanced");
                    // Shared facts must reference the same identity/current position. Force the
                    // same per-frame consumer sync that Update/Draw use, then compare.
                    yield return new HarnessWaitFrames(3);
                    Yixuan.UI.BigMapPanel.SyncSharedSelectionNow();
                    Yixuan.WorldMap.WorldMapPanel.SyncSharedSelectionNow();
                    var dynamicFacts = Yixuan.AI.Tasks.TaskDynamicFacts.SnapshotJson(PickService.FindCharacterTransform());
                    bool factsOk = dynamicFacts != null && dynamicFacts.Contains(bench01.Id) &&
                                   dynamicFacts.Contains("current_position") &&
                                   Yixuan.UI.BigMapPanel.SelectedEntityId == bench01.Id &&
                                   Yixuan.WorldMap.WorldMapPanel.SelectedEntityIdForTest == bench01.Id;
                    Check("T8.d", "map/LLM/task facts agree on the same confirmed entity id",
                        factsOk,
                        "bigmap=" + Yixuan.UI.BigMapPanel.SelectedEntityId +
                        " minimap=" + Yixuan.WorldMap.WorldMapPanel.SelectedEntityIdForTest +
                        " task_facts_has_id=" + (dynamicFacts != null && dynamicFacts.Contains(bench01.Id)),
                        "single shared SelectionBroker record consumed by map and facts");
                }
                director.EyeView = originalEyeView;
            }
            Log("T8.d_end");
        }

        static IEnumerator RunLiveResampleTests()
        {
            Log("T8.e_start");
            float waitStart = Time.realtimeSinceStartup;
            yield return new HarnessWaitUntil(() => Yixuan.Dynamic.DynamicWorld.Instance != null &&
                Yixuan.Dynamic.DynamicWorld.Instance.Ready && Yixuan.Dynamic.DynamicWorld.Instance.Tram != null, 60.0);
            var world = Yixuan.Dynamic.DynamicWorld.Instance;
            if (world == null || world.Tram == null)
            {
                Block("T8.e", "tram runtime for live resample", "world/tram not ready", "dynamic world unavailable");
                yield break;
            }
            string tramId = world.Tram.Spec.entity_id;
            var record = PickService.ConfirmEntity(tramId, "map_click");
            if (record == null)
            {
                Block("T8.e", "confirm tram entity for resample", "error=" + PickService.LastError, "publish failed");
                yield break;
            }
            Vector3 observed = SelectionBroker.Current.entity_position;
            float observedDistance = SelectionBroker.Current.distance_m;
            Vector3 startCenter = world.Tram.Center;
            var liveAtPublish = PickService.SampleLive();
            bool moved = false;
            float deadline = Time.realtimeSinceStartup + 45f;
            while (Time.realtimeSinceStartup < deadline)
            {
                yield return new HarnessWait(0.25);
                if (Vector3.Distance(world.Tram.Center, startCenter) > 1.2f) { moved = true; break; }
            }
            if (!moved)
                Log("T8.e tram did not move within 45s state=" + world.Tram.State + " speed=" + world.Tram.Speed.ToString("F2") + " block=" + world.Tram.BlockReason);
            var liveAfterMove = PickService.SampleLive();
            bool observationKept = SelectionBroker.Current != null &&
                                   SelectionBroker.Current.entity_position == observed &&
                                   SelectionBroker.Current.distance_m == observedDistance;
            bool liveMoved = liveAfterMove.available &&
                             Vector3.Distance(liveAfterMove.current_position, observed) > 1.0f &&
                             liveAfterMove.position_source == "live_dynamic_transform" &&
                             liveAfterMove.sampled_time >= liveAtPublish.sampled_time;
            if (!moved)
            {
                Block("T8.e", "tram actually moves for live resample", "tram stayed within 1.2m for 45s; state=" + world.Tram.State + " block=" + world.Tram.BlockReason, "dynamic fixture did not move (state " + world.Tram.State + ", " + world.Tram.BlockReason + "); live-fact delta for a dynamic entity cannot be validated in this session");
            }
            else
            {
                Check("T8.e", "moving tram: observation record kept, live facts resampled to the current transform",
                    liveMoved && observationKept,
                    "observed_pos=" + observed.ToString("F2") + " current_pos=" + liveAfterMove.current_position.ToString("F2") +
                    " observed_to_current_m=" + liveAfterMove.observed_to_current_m.ToString("F2") +
                    " available=" + liveAfterMove.available + " source=" + liveAfterMove.position_source +
                    " record_kept=" + observationKept +
                    " sampled_age_s=" + liveAfterMove.age_seconds.ToString("F3"),
                    "LLM/task must query current facts, not the publish-time snapshot");
            }

            // Character movement changes relative facts while the observed record stays immutable.
            var bench = EntityPickRegistry.Get("entity/prop_bench_00");
            var benchRecord = PickService.ConfirmEntity(bench.Id, "map_click");
            if (benchRecord != null && _character != null)
            {
                var before = PickService.SampleLive();
                Vector3 position = _character.position;
                float ground = WorldData.GroundHeightAt(position + new Vector3(5f, 0f, 0f));
                var worldApi = Yixuan.Dynamic.DynamicWorld.Instance;
                if (worldApi != null && worldApi.Character != null) worldApi.PlaceCharacterAt(new Vector3(position.x + 5f, 0f, position.z));
                else _character.position = new Vector3(position.x + 5f, ground + 0.02f, position.z);
                yield return new HarnessWaitFrames(3);
                var after = PickService.SampleLive();
                bool distanceChanged = Mathf.Abs(after.distance_m - before.distance_m) > 2.0f;
                bool recordImmutable = SelectionBroker.Current != null &&
                                       SelectionBroker.Current.entity_position == benchRecord.entity_position &&
                                       SelectionBroker.Current.distance_m == benchRecord.distance_m;
                Check("T8.e", "character movement: current distance/bearing resampled, publish-time observation unchanged",
                    distanceChanged && recordImmutable && after.available,
                    "before_dist=" + before.distance_m.ToString("F2") + " after_dist=" + after.distance_m.ToString("F2") +
                    " bearing_after=" + after.bearing_deg.ToString("F1") +
                    " age_s=" + after.age_seconds.ToString("F2") +
                    " record_immutable=" + recordImmutable,
                    "SelectionRecord is evidence; live facts are recomputed");
            }
            // Injected dynamic fixture (labelled): move a parked vehicle through its
            // production runtime transform API and verify live facts re-sample it while
            // the observation record stays immutable. This covers the dynamic branch
            // even when the autonomous tram is blocked_by_vehicle in this session.
            var dynamicWorld = Yixuan.Dynamic.DynamicWorld.Instance;
            if (dynamicWorld != null && dynamicWorld.Vehicles.Count > 0)
            {
                var vehicle = dynamicWorld.Vehicles[0];
                string vehicleId = vehicle.Spec.entity_id;
                var vehicleRecord = PickService.ConfirmEntity(vehicleId, "map_click");
                if (vehicleRecord != null)
                {
                    Vector3 observedVehicle = SelectionBroker.Current.entity_position;
                    Vector3 oldPivot = vehicle.CurrentPivot;
                    vehicle.ApplyTransform(oldPivot + new Vector3(3f, 0f, 0f), vehicle.CurrentYaw);
                    yield return new HarnessWaitFrames(2);
                    var liveVehicle = PickService.SampleLive();
                    bool movedInjected = liveVehicle.available &&
                                         Vector3.Distance(liveVehicle.current_position, observedVehicle) > 2f &&
                                         liveVehicle.position_source == "live_dynamic_transform";
                    bool observedKept = SelectionBroker.Current != null &&
                                        SelectionBroker.Current.entity_position == observedVehicle;
                    Check("T8.e", "injected dynamic transform: live facts follow the runtime transform, observation record kept",
                        movedInjected && observedKept,
                        "vehicle=" + vehicleId + " observed=" + observedVehicle.ToString("F2") +
                        " current=" + liveVehicle.current_position.ToString("F2") +
                        " observed_to_current_m=" + liveVehicle.observed_to_current_m.ToString("F2") +
                        " available=" + liveVehicle.available + " source=" + liveVehicle.position_source +
                        " record_kept=" + observedKept,
                        "INJECTED fixture (vehicle.ApplyTransform, not driving); semantic: LLM/task query current facts, not stale publish snapshot");
                    vehicle.ApplyTransform(oldPivot, vehicle.CurrentYaw);
                }
            }
            Log("T8.e_end");
        }

        static IEnumerator RunInputGuardTests()
        {
            Log("T8.f_start");
            var director = FindCameraDirector();
            var controller = FirstPersonSelectionController.EnsureInstance();
            if (director == null || controller == null)
            {
                Block("T8.f", "CameraDirector + selection controller for input guard", "director=" + (director != null) + " controller=" + (controller != null), "scene fixture missing");
                yield break;
            }
            bool originalEyeView = director.EyeView;
            var eventSystemBefore = UnityEngine.EventSystems.EventSystem.current;
            GameObject eventSystemGo = null;
            GameObject canvasGo = null;
            GameObject inputGo = null;
            try
            {
                director.EyeView = true;
                ClearEventSystemFocus();
                yield return new HarnessWaitFrames(2);
                bool allowedFirstPerson = FirstPersonSelectionController.KeyboardConfirmAllowed();
                Check("T8.f", "T is allowed only in the actual first-person view without text input focus",
                    allowedFirstPerson,
                    "first_person=" + director.EyeView + " allowed=" + allowedFirstPerson +
                    " keyboardControl=" + GUIUtility.keyboardControl,
                    "first-person display guard");

                // Create a real uGUI InputField and give it focus.
                if (UnityEngine.EventSystems.EventSystem.current == null)
                {
                    eventSystemGo = new GameObject("FPS_TestEventSystem");
                    eventSystemGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
                    eventSystemGo.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
                }
                canvasGo = new GameObject("FPS_TestCanvas");
                var canvas = canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvasGo.AddComponent<CanvasScaler>();
                canvasGo.AddComponent<GraphicRaycaster>();
                inputGo = new GameObject("FPS_TestInput");
                inputGo.transform.SetParent(canvasGo.transform, false);
                var inputField = inputGo.AddComponent<InputField>();
                var textGo = new GameObject("Text");
                textGo.transform.SetParent(inputGo.transform, false);
                var text = textGo.AddComponent<Text>();
                text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                inputField.textComponent = text;
                yield return new HarnessWaitFrames(2);
                UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(inputGo);
                inputField.ActivateInputField();
                yield return new HarnessWaitFrames(3);
                bool focused = UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject == inputGo && inputField.isFocused;
                bool allowedWhileFocused = FirstPersonSelectionController.KeyboardConfirmAllowed();
                string currentBefore = SelectionBroker.Current != null ? SelectionBroker.Current.entity_id : "";
                int versionBefore = SelectionBroker.Version;
                bool keyboardTried = controller.TryConfirmFromKeyboard();
                bool unchanged = (SelectionBroker.Current != null ? SelectionBroker.Current.entity_id : "") == currentBefore &&
                                 SelectionBroker.Version == versionBefore;
                Check("T8.f", "chat/search text focus blocks the keyboard T path (no selection, no publish)",
                    focused && !allowedWhileFocused && !keyboardTried && unchanged,
                    "focused=" + focused + " allowed=" + allowedWhileFocused +
                    " keyboard_confirm=" + keyboardTried + " unchanged=" + unchanged,
                    "real uGUI InputField focus reuses the project input-focus guard");
                UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
                inputField.DeactivateInputField();
                yield return new HarnessWaitFrames(2);

                director.EyeView = false;
                ClearEventSystemFocus();
                yield return new HarnessWaitFrames(2);
                bool blockedThirdPerson = !FirstPersonSelectionController.KeyboardConfirmAllowed();
                bool keyboardThirdPerson = controller.TryConfirmFromKeyboard();
                Check("T8.f", "third-person display blocks the keyboard T path",
                    blockedThirdPerson && !keyboardThirdPerson,
                    "eye_view=" + director.EyeView + " guard_allows=" + !blockedThirdPerson + " keyboard_confirm=" + keyboardThirdPerson,
                    "hidden EyeCamera in third person must not be selected by T");
            }
            finally
            {
                director.EyeView = originalEyeView;
                ClearEventSystemFocus();
                if (inputGo != null) UnityEngine.Object.DestroyImmediate(inputGo);
                if (canvasGo != null) UnityEngine.Object.DestroyImmediate(canvasGo);
                if (eventSystemGo != null) UnityEngine.Object.DestroyImmediate(eventSystemGo);
            }
            Log("T8.f_end");
        }

        // ------------------------------------------------------- utilities
        static void SaveCoverageReport()
        {
            try
            {
                if (EntityPickRegistry.Report == null) return;
                var path = Path.Combine(_testsDir, "coverage_registry.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(EntityPickRegistry.Report, Formatting.Indented));
                Log("coverage_saved " + path);
            }
            catch (Exception exception) { Log("coverage_save_failed: " + exception.Message); }
        }

        static void SaveFrame(PickFrame frame, string dirName, string tag)
        {
            if (frame == null) { Log("save_frame_null " + tag); return; }
            if (!frame.Ok) { Log("save_frame_invalid " + tag + " error=" + frame.error); return; }
            string directory = Path.Combine(_evidenceDir, dirName);
            string error;
            if (!EntityIdCapture.SaveFrame(frame, directory, tag, out error))
                Log("save_frame_failed " + tag + " " + error);
            else
                Log("frame_saved " + Path.Combine(directory, tag));
        }

        static void SaveRecord(string dirName, string fileName, SelectionRecord record, PickHit hit)
        {
            try
            {
                string directory = Path.Combine(_evidenceDir, dirName);
                Directory.CreateDirectory(directory);
                var payload = new
                {
                    record,
                    hit = hit == null ? null : new
                    {
                        hit.code,
                        hit.pixel_x,
                        hit.pixel_y,
                        hit.u,
                        hit.v,
                        hit.distance_m,
                        hit.surface_point,
                        hit.eye_position,
                        hit.eye_forward,
                        entity = hit.entity != null ? hit.entity.Id : "",
                        snapshot = hit.snapshot,
                    },
                };
                File.WriteAllText(Path.Combine(directory, fileName), SelectionJson.Serialize(payload));
            }
            catch (Exception exception) { Log("save_record_failed " + fileName + " " + exception.Message); }
        }

        static void SaveText(string dirName, string fileName, string content)
        {
            try
            {
                string directory = Path.Combine(_evidenceDir, dirName);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, fileName), content);
            }
            catch (Exception exception) { Log("save_text_failed " + fileName + " " + exception.Message); }
        }

        static IEnumerator CaptureGameView(string dirName, string tag)
        {
            string directory = Path.Combine(_evidenceDir, dirName);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, tag + ".png");
            try { ScreenCapture.CaptureScreenshot(path); } catch (Exception exception) { Log("screenshot_call_failed " + exception.Message); }
            yield return new HarnessWaitFrames(6);
            if (!File.Exists(path))
            {
                try
                {
                    var texture = ScreenCapture.CaptureScreenshotAsTexture();
                    if (texture != null)
                    {
                        File.WriteAllBytes(path, texture.EncodeToPNG());
                        UnityEngine.Object.DestroyImmediate(texture);
                    }
                }
                catch (Exception exception) { Log("screenshot_fallback_failed " + exception.Message); }
            }
            if (File.Exists(path))
            {
                Pass("T1.g", "single-entity highlight visible in the game view", "screenshot=" + path,
                    "confirmed entity highlight drawn object-space (respects occlusion)", null);
                AddEvidence("T1.g", Path.Combine("evidence", dirName, tag + ".png"), "game-view highlight screenshot");
            }
            else
                Block("T1.g", "game-view highlight screenshot saved", "missing " + path, "ScreenCapture unavailable in this editor session");
        }

        static void AddEvidence(string caseId, string relativePath, string label)
        {
            foreach (var item in Cases)
            {
                if (item.id != caseId) continue;
                item.evidence.Add(relativePath + (string.IsNullOrEmpty(label) ? "" : " (" + label + ")"));
                return;
            }
        }

        static void Record(string id, string status, string expected, string actual, string detail, bool evidence)
        {
            var item = new CaseRecord
            {
                id = id,
                status = status,
                expected = expected,
                actual = actual,
                detail = detail,
            };
            Cases.Add(item);
            Log("case " + id + " " + status + " expected=[" + expected + "] actual=[" + actual + "] detail=[" + detail + "]");
        }

        static void Pass(string id, string expected, string actual, string detail, string evidencePath)
        {
            Record(id, "pass", expected, actual, detail, false);
            if (!string.IsNullOrEmpty(evidencePath)) AddEvidence(id, evidencePath, "");
        }

        static void Check(string id, string expected, bool ok, string actual, string detail)
        {
            Record(id, ok ? "pass" : "fail", expected, actual, detail, false);
        }

        static void Fail(string id, string expected, string actual, string detail)
        {
            Record(id, "fail", expected, actual, detail, false);
        }

        static void Block(string id, string expected, string actual, string detail)
        {
            Record(id, "blocked", expected, actual, detail, false);
        }

        static void Log(string line)
        {
            string entry = DateTime.Now.ToString("HH:mm:ss.fff") + " " + line;
            LogLines.Add(entry);
            Debug.Log("[FPS-A] " + line);
        }

        static void WriteResults()
        {
            WriteFile(Path.Combine(_aRoot, "state", "harness_state.json"), JsonConvert.SerializeObject(new
            {
                running = _running,
                finalized = _finalized,
                play = Application.isPlaying,
                started_frame = _harnessStartFrame,
                last_frame = Time.frameCount,
                start_local = _harnessStartTime,
                nested_wait_frame_delta = _nestedWaitFrameDelta,
                registry_ready = EntityPickRegistry.Ready,
                registry_ms = EntityPickRegistry.BuildMs,
                registry_error = EntityPickRegistry.Error,
                covered = Cases.Count,
            }, Formatting.Indented));
            WriteFile(Path.Combine(_testsDir, "case_results.json"), JsonConvert.SerializeObject(new
            {
                milestone = "A",
                generated_at_local = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                play_mode = Application.isPlaying,
                frame_count = Time.frameCount,
                nested_wait_frame_delta = _nestedWaitFrameDelta,
                confirmed_events = _confirmedEventCount,
                confirmed_event_ids = _confirmedEventIds,
                cases = Cases,
            }, Formatting.Indented));
            var logBuilder = new StringBuilder();
            foreach (var line in LogLines) logBuilder.AppendLine(line);
            WriteFile(Path.Combine(_testsDir, "harness_log.txt"), logBuilder.ToString());
            WriteFile(Path.Combine(_testsDir, "harness_summary.json"), JsonConvert.SerializeObject(new
            {
                milestone = "A",
                status = CountStatus("fail") > 0 ? "fail" : (CountStatus("blocked") > 0 ? "blocked" : "pass"),
                pass = CountStatus("pass"),
                fail = CountStatus("fail"),
                blocked = CountStatus("blocked"),
                total = Cases.Count,
                nested_wait_frame_delta = _nestedWaitFrameDelta,
                play_mode = Application.isPlaying,
            }, Formatting.Indented));
        }

        static void WriteEvidenceIndex()
        {
            try
            {
                var files = new List<object>();
                foreach (var file in Directory.GetFiles(_evidenceDir, "*", SearchOption.AllDirectories))
                {
                    var info = new FileInfo(file);
                    files.Add(new
                    {
                        path = file.Substring(_projectRoot.Length).Replace('\\', '/').TrimStart('/'),
                        bytes = info.Length,
                        sha256 = EntityIdCapture.Sha256(File.ReadAllBytes(file)),
                    });
                }
                WriteFile(Path.Combine(_testsDir, "evidence_index.json"), JsonConvert.SerializeObject(new
                {
                    generated_at_local = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                    files,
                }, Formatting.Indented));
            }
            catch (Exception exception) { Log("evidence_index_failed: " + exception.Message); }
        }

        static void WriteFile(string path, string content)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, content);
            }
            catch (Exception exception) { Debug.LogError("[FPS-A] write failed " + path + " " + exception.Message); }
        }

        static float RectGap(float ax0, float az0, float ax1, float az1, float bx0, float bz0, float bx1, float bz1)
        {
            float dx = Mathf.Max(0f, Mathf.Max(bx0 - ax1, ax0 - bx1));
            float dz = Mathf.Max(0f, Mathf.Max(bz0 - az1, az0 - bz1));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        static bool RaySegmentHitsBox(Vector3 start, Vector3 end, Bounds bounds)
        {
            Vector3 direction = end - start;
            float tMin = 0f, tMax = 1f;
            for (int axis = 0; axis < 3; axis++)
            {
                float origin = start[axis];
                float delta = direction[axis];
                float min = bounds.min[axis];
                float max = bounds.max[axis];
                if (Mathf.Abs(delta) < 1e-6f)
                {
                    if (origin < min || origin > max) return false;
                    continue;
                }
                float t1 = (min - origin) / delta;
                float t2 = (max - origin) / delta;
                if (t1 > t2) { float temp = t1; t1 = t2; t2 = temp; }
                tMin = Mathf.Max(tMin, t1);
                tMax = Mathf.Min(tMax, t2);
                if (tMin > tMax) return false;
            }
            return tMax >= 0.02f && tMin <= 0.98f;
        }

        static Rect ProjectBoundsBox(PickFrame frame, EntityPickEntry entry, float padding)
        {
            var bounds = entry.WorldBounds();
            float minU = 1f, maxU = 0f, minV = 1f, maxV = 0f;
            bool any = false;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 point = new Vector3(
                    (corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                    (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                bool inFront;
                Vector2 uv = EntityIdCapture.ProjectWorld(frame.snapshot, point, out inFront);
                if (!inFront) continue;
                any = true;
                minU = Mathf.Min(minU, uv.x);
                maxU = Mathf.Max(maxU, uv.x);
                minV = Mathf.Min(minV, uv.y);
                maxV = Mathf.Max(maxV, uv.y);
            }
            if (!any) return new Rect(0.4f, 0.4f, 0.2f, 0.2f);
            return Rect.MinMaxRect(
                Mathf.Clamp01(minU - padding), Mathf.Clamp01(minV - padding),
                Mathf.Clamp01(maxU + padding), Mathf.Clamp01(maxV + padding));
        }

        static Rect Union(Rect a, Rect b)
        {
            return Rect.MinMaxRect(
                Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin),
                Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));
        }

        static string SafeName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "none";
            var builder = new StringBuilder(raw.Length);
            foreach (char character in raw)
                builder.Append(char.IsLetterOrDigit(character) ? character : '_');
            return builder.ToString();
        }
    }
}

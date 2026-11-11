using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Yixuan.AI;
using Yixuan.AI.Tasks;
using Yixuan.Motion;
using Yixuan.Stage4;
using Yixuan.World;
using Yixuan.WorldMap;

namespace Yixuan.EditorTools
{
    /// <summary>Runs the final map through production click/navigation APIs in an existing Play session.
    /// Does not synthesize OS input, save the scene, or quit the user's editor.</summary>
    public static class WorldMapReviewAcceptance
    {
        [Serializable] class Case { public string id; public bool passed; public string detail; }
        [Serializable] class Report
        {
            public bool completed, passed;
            public string method = "Editor Play; production map click + locomotion APIs; no OS input";
            public List<Case> cases = new List<Case>();
        }
        const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        static IEnumerator _routine;
        static Report _report;
        static string _dir;
        static CharacterLocomotion _motor;
        static Stage4SeatController _seat;
        static ChatSession _chat;
        static Vector3 _position;
        static Quaternion _rotation;
        static float _speed;
        static int _colliders;
        static bool _walking;
        static readonly Vector3 Start = new Vector3(-14f, 0.15f, -62f);

        [MenuItem("UnityLITF/地图/③ 精细地图 Play 回归（不退出编辑器）")]
        public static void RunInPlay()
        {
            if (!EditorApplication.isPlaying || _routine != null)
                throw new InvalidOperationException("Start an idle Play session first.");
            _motor = UnityEngine.Object.FindObjectOfType<CharacterLocomotion>();
            _seat = Stage4SeatController.Instance;
            _chat = UnityEngine.Object.FindObjectOfType<ChatSession>();
            if (_motor == null || _motor.HasTarget || _seat == null || _seat.State != Stage4SeatController.SeatState.Idle ||
                Yixuan.Stage5.Stage5VisualSeatAgent.Instance.IsActive)
                throw new InvalidOperationException("Character must be idle; existing tasks are preserved.");
            _position = _motor.transform.position; _rotation = _motor.transform.rotation;
            _speed = _motor.WalkSpeed; _walking = _motor.WalkingEnabled;
            _colliders = UnityEngine.Object.FindObjectsOfType<Collider>().Length;
            _dir = Path.Combine("Captures", "WorldMap", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_review");
            Directory.CreateDirectory(_dir);
            _report = new Report();
            _routine = Cases();
            EditorApplication.update += Tick;
            Debug.Log("[WorldMapReview] " + _dir);
        }

        static void Tick()
        {
            try
            {
                if (!EditorApplication.isPlaying) throw new InvalidOperationException("Play ended during acceptance.");
                if (_routine.MoveNext()) return;
                _report.completed = true;
            }
            catch (Exception e) { Check("runner_exception", false, e.ToString()); }
            Finish();
        }

        static void Finish()
        {
            EditorApplication.update -= Tick;
            try
            {
                (_routine as IDisposable)?.Dispose();
                WorldMapPanel.StopNavigation("回归结束");
                if (_seat != null && _seat.State != Stage4SeatController.SeatState.Idle) _seat.RequestCancel();
                if (_motor != null && !_motor.HasTarget && _seat.State == Stage4SeatController.SeatState.Idle)
                {
                    _motor.transform.SetPositionAndRotation(_position, _rotation);
                    _motor.WalkSpeed = _speed; _motor.WalkingEnabled = _walking;
                }
            }
            finally
            {
                _routine = null;
                _report.passed = _report.completed && _report.cases.TrueForAll(c => c.passed);
                Save();
                Debug.Log("[WorldMapReview] completed=" + _report.completed + " passed=" + _report.passed + " " + _dir);
            }
        }

        static void Save() { File.WriteAllText(Path.Combine(_dir, "summary.json"), JsonUtility.ToJson(_report, true)); }
        static void Check(string id, bool passed, string detail)
        {
            _report.cases.Add(new Case { id = id, passed = passed, detail = detail }); Save();
        }
        static object Get(string name) { return typeof(WorldMapPanel).GetField(name, PrivateStatic).GetValue(null); }
        static void Set(string name, object value) { typeof(WorldMapPanel).GetField(name, PrivateStatic).SetValue(null, value); }
        static object Call(string name, params object[] args) { return typeof(WorldMapPanel).GetMethod(name, PrivateStatic).Invoke(null, args); }
        static void Click(Vector2 point) { Call("HandleMapClick", Call("WorldToMap", point)); }
        static float FlatError(Vector3 a, Vector3 b) { return Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z)); }
        static void ResetStart()
        {
            WorldMapPanel.StopNavigation("下一个回归样例");
            _motor.Stop("回归起点", "cancelled");
            _motor.transform.SetPositionAndRotation(Start, Quaternion.Euler(0, 160, 0));
        }

        static IEnumerator Cases()
        {
            WorldMapData.Load(); WorldMapPanel.Load();
            Set("_loco", _motor); Set("_character", _motor.Character); Set("_map", WorldData.Map);
            Set("_mode", Enum.ToObject(typeof(WorldMapPanel).GetNestedType("Mode", BindingFlags.NonPublic), 1));
            Check("package", WorldMapData.Data.entities.Length == 855 && WorldMapNavProvider.Count == 198,
                "855 entities; 198 extra blockers + 2 Stage4 benches");

            int alignmentCases = 0; float alignmentError = 0f, roundtripError = 0f;
            Vector2 min = new Vector2(WorldMapData.MinX, WorldMapData.MinZ), max = new Vector2(WorldMapData.MaxX, WorldMapData.MaxZ);
            foreach (var size in new[] { new Vector2(446, 340), new Vector2(1080, 520), new Vector2(320, 190) })
            foreach (var center in new[] { new Vector2(0, 2), new Vector2(-14, -62), new Vector2(150, 100) })
            foreach (float zoom in new[] { 1f, 3f, 12f })
            foreach (var point in new[] { min, max, new Vector2(min.x, max.y), new Vector2(max.x, min.y), new Vector2(-20.285713f, -67), new Vector2(0, 2) })
            {
                var image = WorldMapProjection.ImageRect(size, center, min, max, zoom);
                var pixel = WorldMapProjection.WorldToMap(point, size, center, max - min, zoom);
                var imagePixel = image.position + new Vector2((point.x - min.x) / (max.x - min.x) * image.width,
                    (max.y - point.y) / (max.y - min.y) * image.height);
                alignmentError = Mathf.Max(alignmentError, Vector2.Distance(pixel, imagePixel));
                roundtripError = Mathf.Max(roundtripError, Vector2.Distance(point, WorldMapProjection.MapToWorld(pixel, size, center, max - min, zoom)));
                alignmentCases++;
            }
            Check("projection", alignmentError < .01f && roundtripError < .001f,
                alignmentCases + " cases; bitmap error px=" + alignmentError + "; roundtrip m=" + roundtripError);
            int benchBlocked = 0;
            foreach (var entity in WorldMapData.Data.entities)
                if (entity.cls == "bench" && !WorldData.IsWalkable(new Vector3(entity.pos[0], 0, entity.pos[2]))) benchBlocked++;
            Check("83_bench_footprints", benchBlocked == 83, "blocked bench centers=" + benchBlocked);

            var destinations = new[] { new Vector2(0, -74), new Vector2(200, 37.65f), new Vector2(176, 79.65f) };
            for (int i = 0; i < destinations.Length; i++)
            {
                ResetStart(); _motor.WalkSpeed = i == 0 ? _speed : 15f;
                Click(destinations[i]); yield return null;
                bool started = WorldMapPanel.OwnsNavigation(_motor);
                int initialPoints = ((List<Vector3>)Get("_route"))?.Count ?? 0;
                int leastPoints = initialPoints; bool segmentsClear = true;
                Vector3 last = _motor.transform.position; float began = Time.realtimeSinceStartup;
                if (i == 0 && started)
                {
                    ulong generation = _motor.NavigationGeneration; Vector3 target = _motor.Target;
                    Click(new Vector2(-20.285713f, -67f));
                    WorldMapPanel.Tick();
                    Check("invalid_click_keeps_route", _motor.NavigationGeneration == generation && _motor.Target == target &&
                        WorldMapPanel.OwnsNavigation(_motor) && (string)Get("_statusKind") == "fail", "bench click refused; previous route + error retained");
                }
                while (started && _motor.HasTarget && Time.realtimeSinceStartup - began < 75f)
                {
                    yield return null;
                    Vector3 now = _motor.transform.position;
                    segmentsClear &= WorldData.LineWalkable(last, now); last = now;
                    WorldMapPanel.Tick();
                    leastPoints = Mathf.Min(leastPoints, ((List<Vector3>)Get("_route"))?.Count ?? 0);
                }
                WorldMapPanel.Tick();
                float error = FlatError(_motor.transform.position, new Vector3(destinations[i].x, 0, destinations[i].y));
                Check("map_arrival_" + i, started && _motor.LastStatus == "arrived" && error <= _motor.ArriveRadius + .02f && segmentsClear,
                    "target=" + destinations[i] + "; points=" + initialPoints + "; speed=" + _motor.WalkSpeed + "; errorXZ=" + error +
                    "; status=" + _motor.LastStatus + "; every frame segment clear=" + segmentsClear);
                Check("remaining_route_" + i, leastPoints < initialPoints && Get("_route") == null && !WorldMapPanel.OwnsNavigation(_motor),
                    "remaining waypoints shrink; terminal route cleared");
            }
            ResetStart(); _motor.WalkSpeed = _speed;
            Click(new Vector2(0, -74));
            ulong oldGeneration = _motor.NavigationGeneration;
            _motor.MoveTo(new Vector3(-14, 0, -58));
            Check("other_route_not_adopted", _motor.NavigationGeneration != oldGeneration && !WorldMapPanel.OwnsNavigation(_motor) &&
                !WorldMapPanel.StopNavigation() && _motor.HasTarget, "map stop cannot stop a replacement route");
            _motor.Stop("回归", "cancelled"); WorldMapPanel.Tick();
            Click(new Vector2(0, -74));
            _chat.SubmitFromUI("取消。");
            Check("chat_cancel", !_motor.HasTarget && !WorldMapPanel.OwnsNavigation(_motor), "production SubmitFromUI cancellation");
            Click(new Vector2(0, -74));
            typeof(ChatSession).GetMethod("BeginTurn", PrivateInstance).Invoke(_chat, new object[] { "你好，今天心情怎么样？" });
            Check("ordinary_chat_keeps_map_route", WorldMapPanel.OwnsNavigation(_motor), "production BeginTurn; no network response required");
            _chat.CancelCurrentTurn();
            ResetStart();
            bool acquired = TaskAuthority.Acquire("worldmap.acceptance.other", 3);
            Click(new Vector2(0, -74));
            Check("foreign_authority_rejected", acquired && !_motor.HasTarget && (string)Get("_statusKind") == "fail", "no route when another task owns the body");
            TaskAuthority.Release("worldmap.acceptance.other");

            foreach (string id in new[] { "entity/prop_bench_plaza_1", "entity/prop_bench_plaza_2" })
            {
                ResetStart(); _motor.WalkSpeed = 3f;
                bool requested = _seat.TrySit(id); float began = Time.realtimeSinceStartup;
                while (requested && _seat.State != Stage4SeatController.SeatState.Seated && _seat.State != Stage4SeatController.SeatState.Failed &&
                    Time.realtimeSinceStartup - began < 35f) yield return null;
                Check("sit_" + id, requested && _seat.State == Stage4SeatController.SeatState.Seated,
                    "same Stage4 controller; state=" + _seat.State + "; error=" + _seat.LastError + "; root=" + _motor.transform.position);
                Click(new Vector2(0, -74));
                Check("seated_map_rejected_" + id, !_motor.HasTarget && _seat.State == Stage4SeatController.SeatState.Seated &&
                    (string)Get("_statusKind") == "fail", "map cannot drag a seated character");
                _seat.RequestStand(); began = Time.realtimeSinceStartup;
                while (_seat.State != Stage4SeatController.SeatState.Idle && Time.realtimeSinceStartup - began < 8f) yield return null;
                Check("stand_" + id, _seat.State == Stage4SeatController.SeatState.Idle && _motor.WalkingEnabled && TaskAuthority.Allows(3),
                    "walk + authority restored");
            }
            _motor.WalkSpeed = _speed;
            ResetStart();
            Set("_search", "plaza_2"); Call("UpdateMatches");
            var matches = (List<WorldMapData.Entity>)Get("_matches");
            Check("search_exact", matches.Count == 1 && matches[0].id == "entity/prop_bench_plaza_2", "production search");
            WorldMapPanel.Expanded = true;
            yield return null;
            ScreenCapture.CaptureScreenshot(Path.Combine(_dir, "expanded_search.png"));
            yield return null; yield return null;
            Set("_search", "");
            Check("colliders_unchanged", UnityEngine.Object.FindObjectsOfType<Collider>().Length == _colliders, "camera colliders=" + _colliders);
            Check("scene_not_saved", !UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty, "scene dirty=false");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using ADBRuntime.Mono;
using Yixuan.Core;
using Yixuan.Motion;

namespace Yixuan.EditorTools
{
    public static class ADBHairPilotAcceptance
    {
        const string OutDir = "Tools/_verify/ADB";
        const string RootName = "Yixuan_Test";

        enum Phase { WaitPlay, OffSample, OnIdle, OnWalk, OnTurn, Restore, Done }

        static Phase _phase;
        static int _frame;
        static YixuanCharacter _character;
        static ZhuangfyHairDynamics _dynamics;
        static CharacterLocomotion _locomotion;
        static string _idleActionId;
        static string _walkActionId;
        static Transform _cachedTipBone;
        static readonly List<Vector3> _offRelative = new List<Vector3>();
        static readonly List<Vector3> _idleRelative = new List<Vector3>();
        static readonly List<Vector3> _walkRelative = new List<Vector3>();
        static readonly List<Vector3> _turnRelative = new List<Vector3>();
        static readonly List<string> _leakMessages = new List<string>();
        static readonly Dictionary<string, string> _refsBefore = new Dictionary<string, string>();
        static readonly Dictionary<string, string> _refsAfter = new Dictionary<string, string>();
        static int _errorCount;
        static string _lastError = "";
        static float _restorePos;
        static float _restoreRot;
        static int _chains;
        static int _points;
        static int _bones;
        static int _weighted;
        static bool _controllerAlive;
        static bool _controllerEnabled;
        static bool _reported;
        static string _failReason;
        static Vector3 _rootPosBefore;
        static Quaternion _rootRotBefore;
        static Vector3 _rootPosAfter;
        static Quaternion _rootRotAfter;

        [MenuItem("UnityLITF/仪玄/ADB 头发试点/运行验收（Play）", false, 20)]
        public static void Run()
        {
            if (!EditorApplication.isPlaying)
            {
                Debug.LogError("[ADBHairAcceptance] 请先进入 Play 模式再运行验收");
                return;
            }
            ResetState();
            Application.logMessageReceived += OnLog;
            EditorApplication.update += Tick;
        }

        static void ResetState()
        {
            _phase = Phase.WaitPlay;
            _frame = 0;
            _character = null;
            _dynamics = null;
            _locomotion = null;
            _idleActionId = null;
            _walkActionId = null;
            _cachedTipBone = null;
            _offRelative.Clear();
            _idleRelative.Clear();
            _walkRelative.Clear();
            _turnRelative.Clear();
            _leakMessages.Clear();
            _refsBefore.Clear();
            _refsAfter.Clear();
            _errorCount = 0;
            _lastError = "";
            _restorePos = -1f;
            _restoreRot = -1f;
            _chains = 0;
            _points = 0;
            _bones = 0;
            _weighted = 0;
            _controllerAlive = false;
            _controllerEnabled = false;
            _reported = false;
            _failReason = null;
        }

        static void OnLog(string condition, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                _errorCount++;
                _lastError = condition;
            }
            if (condition.Contains("not been disposed") || condition.Contains("NativeArray") && condition.Contains("leak"))
                _leakMessages.Add(condition);
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) return;
            _frame++;
            switch (_phase)
            {
                case Phase.WaitPlay:
                    if (_frame < 10) return;
                    if (!Bind()) return;
                    _character.PlayGesture(_idleActionId);
                    _cachedTipBone = _dynamics.TipBone;
                    if (_cachedTipBone == null)
                    {
                        Fail("TipBone 不可用，Build 未成功");
                        return;
                    }
                    _dynamics.SetEnabled(false);
                    CaptureRefs(_refsBefore);
                    _rootPosBefore = _character.transform.position;
                    _rootRotBefore = _character.transform.rotation;
                    _phase = Phase.OffSample;
                    _frame = 0;
                    break;
                case Phase.OffSample:
                    if (_frame == 2) Shot("hair_off.png");
                    Sample(_offRelative);
                    if (_frame >= 30)
                    {
                        _dynamics.SetEnabled(true);
                        _character.PlayGesture(_idleActionId);
                        _phase = Phase.OnIdle;
                        _frame = 0;
                    }
                    break;
                case Phase.OnIdle:
                    Sample(_idleRelative);
                    if (_frame >= 45)
                    {
                        _character.PlayGesture(_walkActionId);
                        _phase = Phase.OnWalk;
                        _frame = 0;
                    }
                    break;
                case Phase.OnWalk:
                    Sample(_walkRelative);
                    if (_frame >= 45)
                    {
                        if (_locomotion != null) _locomotion.TurnRelative(20f, "adb.acceptance");
                        _phase = Phase.OnTurn;
                        _frame = 0;
                    }
                    break;
                case Phase.OnTurn:
                    Sample(_turnRelative);
                    if (_frame >= 45)
                    {
                        Shot("hair_on.png");
                        CaptureRuntimeState();
                        _dynamics.SetEnabled(false);
                        _phase = Phase.Restore;
                        _frame = 0;
                    }
                    break;
                case Phase.Restore:
                    if (_frame >= 8)
                    {
                        _restorePos = _dynamics.LastRestorePosError;
                        _restoreRot = _dynamics.LastRestoreRotError;
                        _rootPosAfter = _character.transform.position;
                        _rootRotAfter = _character.transform.rotation;
                        CaptureRefs(_refsAfter);
                        WriteReport();
                        Cleanup();
                        EditorApplication.isPlaying = false;
                    }
                    break;
            }
        }

        static bool Bind()
        {
            var characterRoot = GameObject.Find(RootName);
            if (characterRoot == null)
            {
                Fail("找不到 " + RootName);
                return false;
            }
            _character = characterRoot.GetComponent<YixuanCharacter>();
            _dynamics = characterRoot.GetComponent<ZhuangfyHairDynamics>();
            _locomotion = characterRoot.GetComponent<CharacterLocomotion>();
            if (_character == null || _dynamics == null)
            {
                Fail("缺少 YixuanCharacter 或 ZhuangfyHairDynamics");
                return false;
            }
            _idleActionId = FindActionId("idle");
            _walkActionId = FindActionId("walk");
            if (string.IsNullOrEmpty(_idleActionId) || string.IsNullOrEmpty(_walkActionId))
            {
                Fail("Manifest 缺少 idle 或 walk 语义动作");
                return false;
            }
            return true;
        }

        static string FindActionId(string keyword)
        {
            if (_character == null || _character.Manifest == null) return null;
            var actionIds = _character.Manifest.Ids();
            string partialMatch = null;
            for (int actionIndex = 0; actionIndex < actionIds.Length; actionIndex++)
            {
                var actionId = actionIds[actionIndex];
                if (string.IsNullOrEmpty(actionId)) continue;
                if (string.Equals(actionId, keyword, StringComparison.OrdinalIgnoreCase)) return actionId;
                if (partialMatch == null && actionId.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) partialMatch = actionId;
            }
            return partialMatch;
        }

        static void Sample(List<Vector3> samples)
        {
            if (_character == null || _character.HeadBone == null || _cachedTipBone == null) return;
            samples.Add(_character.HeadBone.InverseTransformPoint(_cachedTipBone.position));
        }

        static void CaptureRuntimeState()
        {
            _chains = _dynamics.ChainCount;
            _points = _dynamics.PointCount;
            _bones = _dynamics.SimulatedBoneCount;
            _weighted = _dynamics.WeightedHairBoneCount;
            var controller = _dynamics.GetComponent<ADBRuntimeController>();
            _controllerAlive = controller != null;
            _controllerEnabled = controller != null && controller.enabled;
        }

        static void CaptureRefs(Dictionary<string, string> target)
        {
            target.Clear();
            if (_character == null) return;
            target["HeadBone"] = ObjectId(_character.HeadBone);
            target["NeckBone"] = ObjectId(_character.NeckBone);
            target["ArmR"] = ObjectId(_character.ArmR);
            target["ArmL"] = ObjectId(_character.ArmL);
            target["ElbowR"] = ObjectId(_character.ElbowR);
            target["WristR"] = ObjectId(_character.WristR);
            target["Center"] = ObjectId(_character.Center);
            var animator = _character.GetComponent<Animator>();
            target["Avatar"] = animator != null && animator.avatar != null ? animator.avatar.name : "null";
            target["AnimPlayerClips"] = _character.AnimPlayer != null ? _character.AnimPlayer.ClipCount.ToString(CultureInfo.InvariantCulture) : "null";
            target["ManifestIds"] = _character.Manifest != null ? _character.Manifest.Ids().Length.ToString(CultureInfo.InvariantCulture) : "null";
            target["MmdBridge"] = _character.GetComponent<MmdDeformBridge>() != null ? "present" : "absent";
        }

        static string ObjectId(UnityEngine.Object target)
        {
            return target == null ? "null" : target.GetInstanceID().ToString(CultureInfo.InvariantCulture);
        }

        static void Shot(string fileName)
        {
            if (_character == null || _character.HeadBone == null) return;
            Directory.CreateDirectory(OutDir);
            ExportMesh(Path.GetFileNameWithoutExtension(fileName));
            var headBone = _character.HeadBone;
            var cameraObject = new GameObject("ADB_AcceptanceCamera");
            var acceptanceCamera = cameraObject.AddComponent<Camera>();
            acceptanceCamera.fieldOfView = 32f;
            acceptanceCamera.nearClipPlane = 0.05f;
            acceptanceCamera.farClipPlane = 10f;
            acceptanceCamera.transform.position = headBone.position + headBone.rotation * new Vector3(0.55f, 0.1f, 0.6f);
            acceptanceCamera.transform.LookAt(headBone.position + Vector3.up * 0.05f);
            var renderTexture = new RenderTexture(640, 960, 24);
            acceptanceCamera.targetTexture = renderTexture;
            acceptanceCamera.Render();
            var previousActive = RenderTexture.active;
            RenderTexture.active = renderTexture;
            var texture = new Texture2D(640, 960, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, 640, 960), 0, 0);
            texture.Apply();
            RenderTexture.active = previousActive;
            File.WriteAllBytes(Path.Combine(OutDir, fileName), texture.EncodeToPNG());
            acceptanceCamera.targetTexture = null;
            UnityEngine.Object.Destroy(cameraObject);
            UnityEngine.Object.Destroy(renderTexture);
            UnityEngine.Object.Destroy(texture);
        }

        static void ExportMesh(string tag)
        {
            var renderer = _character.GetComponentInChildren<SkinnedMeshRenderer>();
            var bakedMesh = new Mesh();
            try
            {
                renderer.BakeMesh(bakedMesh, true);
                var builder = new StringBuilder();
                foreach (var vertex in bakedMesh.vertices)
                    builder.AppendFormat(CultureInfo.InvariantCulture, "v {0:R} {1:R} {2:R}\n", -vertex.x, vertex.y, vertex.z);
                for (int subMeshIndex = 0; subMeshIndex < bakedMesh.subMeshCount; subMeshIndex++)
                {
                    var triangles = bakedMesh.GetTriangles(subMeshIndex);
                    for (int triangleIndex = 0; triangleIndex < triangles.Length; triangleIndex += 3)
                        builder.AppendFormat(CultureInfo.InvariantCulture, "f {0} {1} {2}\n", triangles[triangleIndex] + 1, triangles[triangleIndex + 1] + 1, triangles[triangleIndex + 2] + 1);
                }
                File.WriteAllText(Path.Combine(OutDir, tag + ".obj"), builder.ToString());
            }
            finally
            {
                UnityEngine.Object.Destroy(bakedMesh);
            }
        }

        static void Fail(string reason)
        {
            _failReason = reason;
            WriteReport();
            Cleanup();
            EditorApplication.isPlaying = false;
        }

        static float Range(List<Vector3> samples)
        {
            if (samples.Count == 0) return 0f;
            float maximumDistance = 0f;
            var firstSample = samples[0];
            for (int sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
                maximumDistance = Mathf.Max(maximumDistance, (samples[sampleIndex] - firstSample).magnitude);
            return maximumDistance;
        }

        static string FormatFloat(float value)
        {
            return value.ToString("0.00000", CultureInfo.InvariantCulture);
        }

        static string FormatVector(Vector3 vector)
        {
            return "[" + FormatFloat(vector.x) + "," + FormatFloat(vector.y) + "," + FormatFloat(vector.z) + "]";
        }

        static void WriteReport()
        {
            if (_reported) return;
            _reported = true;
            Directory.CreateDirectory(OutDir);
            var offRange = Range(_offRelative);
            var idleRange = Range(_idleRelative);
            var walkRange = Range(_walkRelative);
            var turnRange = Range(_turnRelative);
            bool sampleCountsPass = _offRelative.Count > 0 && _idleRelative.Count > 0 && _walkRelative.Count > 0 && _turnRelative.Count > 0;
            if (!sampleCountsPass && _failReason == null) _failReason = "sample counts contain 0";
            bool motionPass = idleRange > offRange * 1.5f && idleRange > 0.002f && walkRange > 0.002f && turnRange > 0.002f;
            bool restorePass = _restorePos >= 0f && _restorePos < 0.0005f && _restoreRot < 0.5f;
            bool refsSame = _refsBefore.Count == _refsAfter.Count;
            foreach (var pair in _refsBefore)
                if (!_refsAfter.TryGetValue(pair.Key, out var afterValue) || afterValue != pair.Value) refsSame = false;
            bool pass = _failReason == null && sampleCountsPass && motionPass && restorePass && refsSame && _errorCount == 0;
            var builder = new StringBuilder();
            builder.Append("{\n");
            builder.Append("  \"pass\": ").Append(pass ? "true" : "false").Append(",\n");
            builder.Append("  \"failReason\": ").Append(Quote(_failReason)).Append(",\n");
            builder.Append("  \"sampleCounts\": {\"off\": ").Append(_offRelative.Count)
                .Append(", \"idle\": ").Append(_idleRelative.Count)
                .Append(", \"walk\": ").Append(_walkRelative.Count)
                .Append(", \"turn\": ").Append(_turnRelative.Count).Append("},\n");
            builder.Append("  \"chains\": ").Append(_chains).Append(",\n");
            builder.Append("  \"points\": ").Append(_points).Append(",\n");
            builder.Append("  \"simulatedBones\": ").Append(_bones).Append(",\n");
            builder.Append("  \"weightedHairBones\": ").Append(_weighted).Append(",\n");
            builder.Append("  \"controller\": {\"alive\": ").Append(_controllerAlive ? "true" : "false")
                .Append(", \"enabled\": ").Append(_controllerEnabled ? "true" : "false").Append("},\n");
            builder.Append("  \"toggleRestore\": {\"maxPosDelta\": ").Append(FormatFloat(Mathf.Max(0f, _restorePos)))
                .Append(", \"maxRotDeg\": ").Append(FormatFloat(Mathf.Max(0f, _restoreRot))).Append("},\n");
            builder.Append("  \"tipRelativeToHeadRange\": {\"off\": ").Append(FormatFloat(offRange))
                .Append(", \"idle\": ").Append(FormatFloat(idleRange))
                .Append(", \"walk\": ").Append(FormatFloat(walkRange))
                .Append(", \"turn\": ").Append(FormatFloat(turnRange)).Append("},\n");
            builder.Append("  \"motionPass\": ").Append(motionPass ? "true" : "false").Append(",\n");
            builder.Append("  \"refsUnchanged\": ").Append(refsSame ? "true" : "false").Append(",\n");
            builder.Append("  \"root\": {\"posBefore\": ").Append(FormatVector(_rootPosBefore))
                .Append(", \"posAfter\": ").Append(FormatVector(_rootPosAfter))
                .Append(", \"rotDeg\": ").Append(FormatFloat(Quaternion.Angle(_rootRotBefore, _rootRotAfter))).Append("},\n");
            builder.Append("  \"errors\": ").Append(_errorCount).Append(",\n");
            builder.Append("  \"lastError\": ").Append(Quote(_lastError)).Append(",\n");
            builder.Append("  \"leakWarnings\": [\n");
            for (int messageIndex = 0; messageIndex < _leakMessages.Count; messageIndex++)
                builder.Append("    ").Append(Quote(_leakMessages[messageIndex])).Append(messageIndex + 1 < _leakMessages.Count ? ",\n" : "\n");
            builder.Append("  ],\n");
            builder.Append("  \"screenshots\": [\"Tools/_verify/ADB/hair_off.png\", \"Tools/_verify/ADB/hair_on.png\"],\n");
            builder.Append("  \"refsBefore\": {");
            AppendRefs(builder, _refsBefore);
            builder.Append("  },\n  \"refsAfter\": {");
            AppendRefs(builder, _refsAfter);
            builder.Append("  }\n}\n");
            File.WriteAllText(Path.Combine(OutDir, "hair_pilot_report.json"), builder.ToString());
            Debug.Log("[ADBHairAcceptance] 报告写入 " + Path.Combine(OutDir, "hair_pilot_report.json") + " pass=" + pass);
        }

        static void AppendRefs(StringBuilder builder, Dictionary<string, string> refs)
        {
            bool first = true;
            foreach (var pair in refs)
            {
                if (!first) builder.Append(", ");
                first = false;
                builder.Append(Quote(pair.Key)).Append(": ").Append(Quote(pair.Value));
            }
        }

        static string Quote(string text)
        {
            if (text == null) return "null";
            return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\"";
        }

        static void Cleanup()
        {
            Application.logMessageReceived -= OnLog;
            EditorApplication.update -= Tick;
            _phase = Phase.Done;
        }
    }
}

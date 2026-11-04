// 一次性诊断探针（batchmode Play 模式）：
//   ① 游戏内视觉链路：用 EyeCamera 抓一帧真实游戏画面，走 QwenVisionClient.Ask()，看是否还能识别（额度/403）
//   ② 角色脚部接地：GroundSnap 状态、脚趾/脚踝骨世界 Y、蒙皮网格最低点、地图表面高度、脚部特写截图
// 用法：
//   Unity.exe -batchmode -projectPath <工程> -executeMethod Yixuan.EditorTools.FeetVisionProbe.Run
//     -logFile <工程>/Logs/feet_vision_probe.log
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Yixuan.Vision;
using Yixuan.Model;
using Yixuan.Motion;

namespace Yixuan.EditorTools
{
    [InitializeOnLoad]
    public static class FeetVisionProbe
    {
        const string LogDir = "Logs/feet_probe";
        const string StageKey = "SBH_FVP_STAGE";
        const string NextKey = "SBH_FVP_NEXT";
        const string T0Key = "SBH_FVP_T0";
        const string ReportKey = "SBH_FVP_REPORT";
        const int W = 1280, H = 720;

        static readonly StringBuilder Report = new StringBuilder();
        static readonly List<(float t, string what)> Steps = new List<(float, string)>
        {
            (4.5f, "measure"),
            (5.5f, "vision_ask"),
            (14.0f, "vision_report"),
            (15.0f, "vision_describe"),
            (23.0f, "vision_report2"),
            (24.0f, "stop"),
        };

        static bool _visionDone;
        static bool _visionOk;
        static string _visionText;
        static float _visionT0;
        static bool _descDone;
        static bool _descOk;
        static string _descText;
        static float _visionLatency;
        static float _descLatency;
        static byte[] _eyeJpg;

        static FeetVisionProbe()
        {
            EditorApplication.update += Tick;
        }

        public static void Run()
        {
            Directory.CreateDirectory(LogDir);
            Report.AppendLine("== FeetVisionProbe " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ==");
            EditorSceneManager.OpenScene(SovietBlueHourSceneBuilder.TargetScene, OpenSceneMode.Single);
            SessionState.SetString(StageKey, "play");
            SessionState.SetInt(NextKey, 0);
            SessionState.SetString(T0Key, "");
            SessionState.SetString(ReportKey, Report.ToString());
            EditorApplication.isPlaying = true;
            Debug.Log("[FVP] 进入 Play…");
        }

        static void Tick()
        {
            string stage = SessionState.GetString(StageKey, "");
            if (stage == "play")
            {
                if (!EditorApplication.isPlaying) return;
                string t0s = SessionState.GetString(T0Key, "");
                if (string.IsNullOrEmpty(t0s))
                {
                    SessionState.SetString(T0Key, EditorApplication.timeSinceStartup.ToString("F4"));
                    Report.Clear(); Report.Append(SessionState.GetString(ReportKey, ""));
                    return;
                }
                float el = (float)(EditorApplication.timeSinceStartup - float.Parse(t0s));
                int next = SessionState.GetInt(NextKey, 0);
                if (next >= Steps.Count) return;
                if (el < Steps[next].t) return;
                try { RunStep(Steps[next].what, el); }
                catch (Exception e) { Log("步骤 " + Steps[next].what + " 异常: " + e); }
                next++;
                SessionState.SetInt(NextKey, next);
                SessionState.SetString(ReportKey, Report.ToString());
                if (next >= Steps.Count)
                {
                    SessionState.SetString(StageKey, "wrap");
                    EditorApplication.isPlaying = false;
                }
                return;
            }
            if (stage == "wrap")
            {
                if (EditorApplication.isPlaying) return;
                File.WriteAllText(Path.Combine(LogDir, "report.txt"), Report.ToString());
                SessionState.SetString(StageKey, "exit");
                SessionState.SetInt("SBH_FVP_EXIT", 0);
                return;
            }
            if (stage == "exit")
            {
                int tries = SessionState.GetInt("SBH_FVP_EXIT", 0) + 1;
                SessionState.SetInt("SBH_FVP_EXIT", tries);
                if (tries >= 4) { System.Diagnostics.Process.GetCurrentProcess().Kill(); return; }
                EditorApplication.Exit(0);
            }
        }

        static void RunStep(string what, float el)
        {
            if (what == "measure") { MeasureFeet(el); return; }
            if (what == "vision_ask") { RunVisionAsk(el); return; }
            if (what == "vision_report")
            {
                Log("[" + el.ToString("F1") + "s] 视觉链路 Ask 结果：done=" + _visionDone + " ok=" + _visionOk +
                    " 实际耗时=" + (_visionLatency > 0 ? _visionLatency.ToString("F2") + "s" : "?") +
                    "\n  返回=" + (_visionText ?? "(null)"));
                return;
            }
            if (what == "vision_describe")
            {
                var client = UnityEngine.Object.FindObjectOfType<QwenVisionClient>();
                if (client == null || _eyeJpg == null) { Log("Describe 跳过"); return; }
                _descDone = false; _descOk = false; _descText = null;
                float dt0 = Time.realtimeSinceStartup;
                client.StartCoroutine(client.Describe(_eyeJpg, null,
                    text => { _descDone = true; _descOk = !string.IsNullOrEmpty(text); _descText = "OK: " + text; _descLatency = Time.realtimeSinceStartup - dt0; },
                    err => { _descDone = true; _descOk = false; _descText = "ERROR: " + err; _descLatency = Time.realtimeSinceStartup - dt0; }));
                Log("[" + el.ToString("F1") + "s] 已发起 Describe（环境观察路径）");
                return;
            }
            if (what == "vision_report2")
            {
                Log("[" + el.ToString("F1") + "s] 视觉链路 Describe 结果：done=" + _descDone + " ok=" + _descOk +
                    " 实际耗时=" + (_descLatency > 0 ? _descLatency.ToString("F2") + "s" : "?") +
                    "\n  返回=" + (_descText ?? "(null)"));
                return;
            }
            if (what == "stop") Log("[" + el.ToString("F1") + "s] 结束");
        }

        static void Log(string s) { Report.AppendLine(s); Debug.Log("[FVP] " + s); }

        static void MeasureFeet(float el)
        {
            var actor0 = GameObject.Find("Yixuan_Test");
            try
            {
            Log("[" + el.ToString("F1") + "s] ===== 脚部接地测量 =====");
            var actor = GameObject.Find("Yixuan_Test");
            if (actor == null) { Log("找不到角色"); return; }
            Log("角色 root position=" + actor.transform.position.ToString("F4") +
                " localPosition=" + actor.transform.localPosition.ToString("F4"));
            var profile = UnityEngine.Object.FindObjectOfType<Yixuan.World.MapWorldProfile>();
            if (profile != null)
            {
                Log("MapWorldProfile: HasGround=" + profile.HasGround + "，角色脚下高度图地面 y=" +
                    profile.SurfaceY(actor.transform.position.x, actor.transform.position.z).ToString("F4"));
            }

            var snap = actor.GetComponent<GroundSnap>();
            if (snap != null) Log("GroundSnap: " + snap.Status().Replace("\n", " | "));
            else Log("GroundSnap: 缺失");

            string[] bones = { "ToeL", "ToeR", "FootL", "FootR", "LowerLegL", "LowerLegR" };
            var toeTfs = new List<Transform>();
            for (int i = 0; i < bones.Length; i++)
            {
                var t = MmdBoneNames.Resolve(bones[i], actor.transform);
                if (t != null)
                {
                    Log("骨 " + bones[i] + " → " + t.name + " worldY=" + t.position.y.ToString("F4") +
                        " (x=" + t.position.x.ToString("F2") + ", z=" + t.position.z.ToString("F2") + ")");
                    if (bones[i] == "ToeL" || bones[i] == "ToeR" || bones[i] == "FootL" || bones[i] == "FootR") toeTfs.Add(t);
                }
                else Log("骨 " + bones[i] + " → (没找到)");
            }

            var smr = actor.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr != null)
            {
                var mesh = new Mesh();
                smr.BakeMesh(mesh, true);
                var verts = mesh.vertices;
                Matrix4x4 m = smr.transform.localToWorldMatrix;
                float minY = float.MaxValue, maxY = float.MinValue;
                for (int i = 0; i < verts.Length; i++)
                {
                    float y = m.MultiplyPoint3x4(verts[i]).y;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
                Log("蒙皮网格(全身) 世界最低点 y=" + minY.ToString("F4") + "，最高点 y=" + maxY.ToString("F4") + "，顶点=" + verts.Length);

                // 只看蒙皮到脚/脚趾骨的顶点（= 鞋子/脚底），避免裙摆干扰
                // 注意：BakeMesh 出来的网格可能不带 boneWeights，长度不一致时退回 sharedMesh
                BoneWeight[] boneWeights = null;
                if (mesh.boneWeights != null && mesh.boneWeights.Length == verts.Length) boneWeights = mesh.boneWeights;
                else if (smr.sharedMesh != null && smr.sharedMesh.boneWeights != null &&
                         smr.sharedMesh.boneWeights.Length == verts.Length) boneWeights = smr.sharedMesh.boneWeights;
                var boneTfs = smr.bones;
                var footBoneIdx = new HashSet<int>();
                for (int b = 0; b < boneTfs.Length; b++)
                {
                    if (boneTfs[b] == null) continue;
                    string bn = boneTfs[b].name;
                    if (bn.Contains("つま先") || bn.Contains("足首") || bn.Contains("足D") ||
                        bn.Contains("つま先D") || bn.Contains("足") && (bn.Contains("L") || bn.Contains("R") || bn.Contains("左") || bn.Contains("右")))
                        footBoneIdx.Add(b);
                }
                float footMin = float.MaxValue; int footVerts = 0;
                for (int i = 0; boneWeights != null && i < verts.Length; i++)
                {
                    var bw = boneWeights[i];
                    float w = 0f;
                    if (footBoneIdx.Contains(bw.boneIndex0)) w += bw.weight0;
                    if (footBoneIdx.Contains(bw.boneIndex1)) w += bw.weight1;
                    if (footBoneIdx.Contains(bw.boneIndex2)) w += bw.weight2;
                    if (footBoneIdx.Contains(bw.boneIndex3)) w += bw.weight3;
                    if (w < 0.3f) continue;
                    footVerts++;
                    float y = m.MultiplyPoint3x4(verts[i]).y;
                    if (y < footMin) footMin = y;
                }
                float surf = profile != null ? profile.SurfaceY(actor.transform.position.x, actor.transform.position.z) : 0f;
                Log("脚部权重顶点(" + footVerts + " 个，权重≥0.3) 世界最低点 y=" + footMin.ToString("F4") +
                    "；地面 y=" + surf.ToString("F4") + "；脚底-地面=" + (footMin - surf).ToString("F4") + "m");
                UnityEngine.Object.Destroy(mesh);
            }
            else Log("找不到 SkinnedMeshRenderer");

            // 地图表面高度（高度图 vs 临时 MeshCollider 射线，双保险）
            try {
            var site = GameObject.Find("SITE");
            if (site != null)
            {
                var mf = site.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    var mc = site.GetComponent<MeshCollider>();
                    if (mc == null) mc = site.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                    Physics.SyncTransforms();
                    Vector3[] pts = {
                        actor.transform.position,
                        new Vector3(0f, 0f, -72f),
                        new Vector3(0f, 0f, -40f),
                        new Vector3(0f, 0f, -56.5f),
                        new Vector3(30f, 0f, -90f),
                        new Vector3(-14f, 0f, -66f),
                    };
                    for (int i = 0; i < pts.Length; i++)
                    {
                        RaycastHit hit;
                        Vector3 from = new Vector3(pts[i].x, 50f, pts[i].z);
                        if (Physics.Raycast(from, Vector3.down, out hit, 100f, ~0, QueryTriggerInteraction.Ignore))
                            Log("地面 @(" + pts[i].x.ToString("F1") + "," + pts[i].z.ToString("F1") + ") 表面 y=" +
                                hit.point.y.ToString("F4") + "（collider=" + hit.collider.name + "）");
                        else Log("地面 @(" + pts[i].x.ToString("F1") + "," + pts[i].z.ToString("F1") + ") 射线没打中");
                    }
                }
            }
            else Log("找不到 SITE 对象");
            } catch (Exception ex) { Log("SITE MeshCollider 探针失败（可忽略）: " + ex.Message); }
            }
            catch (Exception e) { Log("脚部测量异常（继续截图）: " + e.GetType().Name + ": " + e.Message); }
            if (actor0 != null) CaptureFeet(actor0);
        }

        static void CaptureFeet(GameObject actor)
        {
            var go = new GameObject("FVP_Camera");
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.cullingMask = ~0;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.05f, 0.06f, 0.09f, 1f);
                cam.farClipPlane = 100f;
                cam.fieldOfView = 38f;
                if (ShaderUtil.allowAsyncCompilation) ShaderUtil.allowAsyncCompilation = false;

                // 正面低机位
                Shots(cam, actor, "feet_front", new Vector3(0f, 0.42f, 1.5f), new Vector3(0f, 0.12f, 0f));
                // 侧面低机位
                Shots(cam, actor, "feet_side", new Vector3(1.5f, 0.32f, 0.25f), new Vector3(0f, 0.12f, 0f));
                // 全身接地
                Shots(cam, actor, "body_ground", new Vector3(0.2f, 1.1f, 3.2f), new Vector3(0f, 0.85f, 0f));
            }
            finally { UnityEngine.Object.Destroy(go); }
        }

        static void Shots(Camera cam, GameObject actor, string name, Vector3 offset, Vector3 lookOffset)
        {
            Vector3 pos = actor.transform.position + actor.transform.rotation * offset;
            Vector3 look = actor.transform.position + lookOffset;
            cam.transform.position = pos;
            cam.transform.rotation = Quaternion.LookRotation((look - pos).normalized, Vector3.up);
            Capture(cam, name);
        }

        static void Capture(Camera cam, string name)
        {
            var rtHdr = RenderTexture.GetTemporary(W, H, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            var rtLdr = RenderTexture.GetTemporary(W, H, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prevActive = RenderTexture.active;
            var prevTarget = cam.targetTexture;
            try
            {
                cam.targetTexture = rtHdr;
                cam.Render(); cam.Render();
                Graphics.Blit(rtHdr, rtLdr);
                RenderTexture.active = rtLdr;
                var tex = new Texture2D(W, H, TextureFormat.RGBA32, false, false);
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0, false);
                tex.Apply(false, false);
                string path = Path.Combine(LogDir, name + ".png");
                File.WriteAllBytes(Path.GetFullPath(path), tex.EncodeToPNG());
                Log("  截图 " + path);
                UnityEngine.Object.DestroyImmediate(tex);
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rtHdr);
                RenderTexture.ReleaseTemporary(rtLdr);
            }
        }

        static void RunVisionAsk(float el)
        {
            Log("[" + el.ToString("F1") + "s] ===== 游戏内视觉链路测试 =====");
            var client = UnityEngine.Object.FindObjectOfType<QwenVisionClient>();
            if (client == null) { Log("找不到 QwenVisionClient"); return; }
            Log("QwenVisionClient: model=" + client.Model + "，HasKey=" + client.HasKey);
            var eye = GameObject.Find("EyeCamera");
            var eyeCam = eye != null ? eye.GetComponent<Camera>() : null;
            if (eyeCam == null) { Log("找不到 EyeCamera"); return; }
            byte[] jpg = CaptureEyeJpg(eyeCam);
            _eyeJpg = jpg;
            if (jpg == null || jpg.Length == 0) { Log("眼睛画面抓帧失败"); return; }
            Log("眼睛画面 JPEG=" + jpg.Length + " bytes，开始调用 QwenVisionClient.Ask(…)");
            _visionT0 = Time.realtimeSinceStartup;
            _visionDone = false; _visionOk = false; _visionText = null;
            float askT0 = Time.realtimeSinceStartup;
            client.StartCoroutine(client.Ask("这是什么地方？看到了什么？", jpg, null,
                text => { _visionDone = true; _visionOk = !string.IsNullOrEmpty(text); _visionText = "OK: " + text; _visionLatency = Time.realtimeSinceStartup - askT0; },
                err => { _visionDone = true; _visionOk = false; _visionText = "ERROR: " + err; _visionLatency = Time.realtimeSinceStartup - askT0; }));
        }

        static byte[] CaptureEyeJpg(Camera eyeCam)
        {
            var rt = RenderTexture.GetTemporary(384, 288, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            var prev = eyeCam.targetTexture;
            var prevActive = RenderTexture.active;
            try
            {
                eyeCam.targetTexture = rt;
                eyeCam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(384, 288, TextureFormat.RGB24, false, false);
                tex.ReadPixels(new Rect(0, 0, 384, 288), 0, 0, false);
                tex.Apply(false, false);
                byte[] jpg = tex.EncodeToJPG(85);
                UnityEngine.Object.DestroyImmediate(tex);
                return jpg;
            }
            finally
            {
                eyeCam.targetTexture = prev;
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}

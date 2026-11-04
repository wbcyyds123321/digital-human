// SovietBlueHour 主场景 Play 模式 QA（batchmode 入口）
// 用法：
//   Unity.exe -batchmode -projectPath <工程> -executeMethod Yixuan.EditorTools.SovietBlueHourQa.Run
//     -logFile <工程>/Logs/qa_scene.log
// 流程：重建场景（SovietBlueHourSceneBuilder.BuildAll）→ 编辑模式寻路自检 → 进入 Play
//       → 预热 shader → 多机位截图 + 挥手 + 走路 → 退出 Play → 写报告并 Exit(0)
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Yixuan.Presentation;
using Yixuan.World;

namespace Yixuan.EditorTools
{
    [InitializeOnLoad]
    public static class SovietBlueHourQa
    {
        const string LogDir = "Logs/soviet_bluehour_qa";
        const string StageKey = "SBH_QA_STAGE";
        const string NextKey = "SBH_QA_NEXT";
        const string T0Key = "SBH_QA_T0";
        const string ReportKey = "SBH_QA_REPORT";
        const string FrameKey = "SBH_QA_F0";
        const string PerfTKey = "SBH_QA_PT0";
        const string ExitTriesKey = "SBH_QA_EXIT_TRIES";
        const int W = 1280, H = 720;

        // 相机参数（与 scene_config.json 默认一致；QA 里按需切换比较）
        const float CamYaw = 210f, CamDist = 3.9f, CamPitch = 6.5f, CamLook = 1.12f, CamFov = 42f;

        static readonly StringBuilder Report = new StringBuilder();
        static readonly List<(float t, string what)> Steps = new List<(float, string)>
        {
            (4.5f,  "warmup"),
            (6.0f,  "shot_default"),
            (8.0f,  "shot_yaw180"),
            (10.0f, "shot_yaw200"),
            (12.0f, "shot_yaw240"),
            (14.0f, "shot_behind"),
            (16.0f, "wave"),
            (18.0f, "shot_wave"),
            (20.0f, "walk"),
            (28.0f, "shot_walk"),
            (30.0f, "shot_hero_1080"),
            (31.6f, "shot_eye_view"),
            (32.8f, "shot_ui_screen"),
            (34.0f, "shot_final"),
            (36.0f, "stop"),
        };

        static SovietBlueHourQa()
        {
            EditorApplication.update += Tick;
        }

        public static void Run()
        {
            Directory.CreateDirectory(LogDir);
            Report.AppendLine("== SovietBlueHour QA " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ==");
            SovietBlueHourSceneBuilder.BuildAll();
            SelfCheck();
            SessionState.SetString(StageKey, "play");
            SessionState.SetInt(NextKey, 0);
            SessionState.SetString(T0Key, "");
            SessionState.SetString(ReportKey, Report.ToString());
            EditorApplication.isPlaying = true;
            Debug.Log("[SBH-QA] 已请求进入 Play 模式…");
        }

        static void SelfCheck()
        {
            var profile = UnityEngine.Object.FindObjectOfType<MapWorldProfile>();
            if (profile != null) { profile.EnsureData(); WorldData.RegisterMap(profile); }
            WorldData.Refresh();
            Log("WorldData.UsingMapNavigation = " + WorldData.UsingMapNavigation);
            Log("GroundY = " + WorldData.GroundY + "，bounds = " + WorldData.WalkableBounds +
                "，obstacles = " + WorldData.Obstacles.Count + "，anchors = " + WorldData.Anchors.Count);
            var actor = GameObject.Find("Yixuan_Test");
            Vector3 start = actor != null ? actor.transform.position : WorldData.WalkableBounds.center;
            Log("角色出生位置 = " + start.ToString("F2") + " yaw=" + (actor != null ? actor.transform.eulerAngles.y.ToString("F0") : "?"));
            string[] probes = { "central_plaza", "house_of_culture", "obelisk", "avenue_lenin", "bakery", "metro", "store_door", "street_center", "ufo_station" };
            for (int i = 0; i < probes.Length; i++)
            {
                var anchor = WorldData.GetAnchor(probes[i]);
                if (anchor == null) { Log("  锚点缺失: " + probes[i]); continue; }
                var path = WorldData.FindPath(start, anchor.Position);
                Log("  路径 " + probes[i] + " → " + (path == null ? ("失败: " + WorldData.LastError)
                    : ("OK " + path.Count + " 点，长度≈" + PathLength(start, path).ToString("F1") + "m")));
            }
        }

        static float PathLength(Vector3 start, List<Vector3> path)
        {
            float len = 0f; Vector3 prev = start;
            for (int i = 0; i < path.Count; i++) { len += Vector3.Distance(prev, path[i]); prev = path[i]; }
            return len;
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
                    Report.Clear();
                    Report.Append(SessionState.GetString(ReportKey, ""));
                    Log("Play 已启动，开始计时。");
                    return;
                }
                float t0 = float.Parse(t0s);
                float el = (float)(EditorApplication.timeSinceStartup - t0);
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
                File.WriteAllText(Path.Combine(LogDir, "qa_report.txt"), Report.ToString());
                Debug.Log("[SBH-QA] 完成，报告：" + LogDir + "/qa_report.txt");
                SessionState.SetString(StageKey, "exit");
                SessionState.SetInt(ExitTriesKey, 0);
                return;
            }
            if (stage == "exit")
            {
                int tries = SessionState.GetInt(ExitTriesKey, 0) + 1;
                SessionState.SetInt(ExitTriesKey, tries);
                Debug.Log("[SBH-QA] 退出编辑器（第 " + tries + " 次尝试）");
                if (tries >= 4)
                {
                    // EditorApplication.Exit 在 batchmode 停止 Play 的域重载期间可能被吞掉，硬退出兜底。
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                    return;
                }
                EditorApplication.Exit(0);
            }
        }

        static void RunStep(string what, float elapsed)
        {
            var cam = Camera.main;
            var director = cam != null ? cam.GetComponent<CameraDirector>() : null;
            var actor = GameObject.Find("Yixuan_Test");
            var loco = actor != null ? actor.GetComponent<Yixuan.Motion.CharacterLocomotion>() : null;
            switch (what)
            {
                case "warmup":
                    WarmupShaders(cam);
                    SessionState.SetInt(FrameKey, Time.frameCount);
                    SessionState.SetFloat(PerfTKey, Time.realtimeSinceStartup);
                    Log("[" + elapsed.ToString("F1") + "s] shader 预热完成（禁用异步编译 + 全图俯视 + 主视角双渲染）");
                    break;

                case "shot_default":
                    ApplyCam(director, CamYaw, CamDist, CamPitch, CamLook, CamFov);
                    Log("[" + elapsed.ToString("F1") + "s] default yaw=" + CamYaw + " dist=" + CamDist + " pitch=" + CamPitch + " fov=" + CamFov +
                        " 角色=" + (actor != null ? actor.transform.position.ToString("F2") : "?"));
                    Capture(cam, "01_default_face");
                    break;

                case "shot_yaw180":
                    ApplyCam(director, 180f, CamDist, CamPitch, CamLook, CamFov);
                    Capture(cam, "02_yaw180_front");
                    break;

                case "shot_yaw200":
                    ApplyCam(director, 200f, CamDist, CamPitch, CamLook, CamFov);
                    Capture(cam, "03_yaw200_front_right");
                    break;

                case "shot_yaw240":
                    ApplyCam(director, 240f, CamDist, CamPitch, CamLook, CamFov);
                    Capture(cam, "04_yaw240_front_left");
                    break;

                case "shot_behind":
                    ApplyCam(director, 0f, 4.2f, 10f, 1.30f, CamFov);
                    Capture(cam, "05_behind_overshoulder");
                    break;

                case "wave":
                    var character = actor != null ? actor.GetComponent<Yixuan.Core.YixuanCharacter>() : null;
                    if (character != null) { character.PlayGesture("wave"); Log("[" + elapsed.ToString("F1") + "s] 触发挥手"); }
                    ApplyCam(director, CamYaw, CamDist, CamPitch, CamLook, CamFov);
                    break;

                case "shot_wave":
                    Capture(cam, "06_wave_face");
                    break;

                case "walk":
                    if (loco != null)
                    {
                        bool ok = loco.MoveToAnchor("flags_plaza");
                        Log("[" + elapsed.ToString("F1") + "s] MoveToAnchor(flags_plaza)=" + ok +
                            " status=" + loco.LastStatus + " error=" + loco.LastError);
                    }
                    break;

                case "shot_walk":
                    if (loco != null)
                        Log("[" + elapsed.ToString("F1") + "s] 走路后位置=" + (actor != null ? actor.transform.position.ToString("F2") : "?") +
                            " status=" + loco.LastStatus + " error=" + loco.LastError);
                    ApplyCam(director, CamYaw, CamDist, CamPitch, CamLook, CamFov);
                    Capture(cam, "07_walk");
                    break;

                case "shot_hero_1080":
                    ApplyCam(director, 210f, 4.4f, 9f, 1.15f, 38f);
                    Capture(cam, "09_hero_1080p", 1920, 1080);
                    break;

                case "shot_eye_view":
                    var eye = GameObject.Find("EyeCamera");
                    var eyeCam = eye != null ? eye.GetComponent<Camera>() : null;
                    if (eyeCam != null)
                    {
                        Capture(eyeCam, "11_eye_view", 480, 360);
                        Log("  眼睛视角（她看到的画面）已截图");
                    }
                    else Log("  找不到 EyeCamera");
                    break;
                case "shot_ui_screen":
                    try
                    {
                        string uiPath = Path.GetFullPath(Path.Combine(LogDir, "10_screen_ui.png"));
                        ScreenCapture.CaptureScreenshot(uiPath);
                        Log("  请求 ScreenCapture（含 UI）：" + uiPath);
                    }
                    catch (Exception e) { Log("  ScreenCapture 失败: " + e.Message); }
                    break;
                case "shot_final":
                    Log("[" + elapsed.ToString("F1") + "s] 性能：frames=" + SessionState.GetInt(FrameKey, 0) +
                        "→" + Time.frameCount + "，时间=" + SessionState.GetFloat(PerfTKey, 0f).ToString("F2") +
                        "→" + Time.realtimeSinceStartup.ToString("F2"));
                    ApplyCam(director, CamYaw, CamDist, CamPitch, CamLook, CamFov);
                    Capture(cam, "08_final");
                    break;

                case "stop":
                    string ui = Path.Combine(LogDir, "10_screen_ui.png");
                    Log("[" + elapsed.ToString("F1") + "s] 结束 Play；UI 截图=" + (File.Exists(ui) ? ("有 " + new FileInfo(ui).Length + " bytes") : "缺失"));
                    break;
            }
        }

        static void ApplyCam(CameraDirector d, float yawOffset, float dist, float pitch, float look, float fov)
        {
            if (d == null) return;
            d.ThirdPerson = true;
            d.TP_YawOffset = yawOffset;
            d.TP_Distance = dist;
            d.TP_Pitch = pitch;
            d.TP_LookHeight = look;
            d.TP_Fov = fov;
            d.SnapThirdPersonView();
        }

        /// <summary>batchmode 下异步 shader 变体编译会让首帧落到 error shader（洋红）。先禁用异步 + 全图预热。</summary>
        static void WarmupShaders(Camera mainCam)
        {
            try { ShaderUtil.allowAsyncCompilation = false; } catch { }
            var go = new GameObject("QA_WarmupCamera");
            try
            {
                var c = go.AddComponent<Camera>();
                c.cullingMask = ~0;
                c.clearFlags = CameraClearFlags.SolidColor;
                c.backgroundColor = Color.black;
                c.farClipPlane = 1200f;
                c.transform.position = new Vector3(0f, 700f, 0f);
                c.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                var rt = RenderTexture.GetTemporary(512, 512, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
                c.targetTexture = rt;
                c.Render();
                c.targetTexture = null;
                RenderTexture.ReleaseTemporary(rt);
            }
            finally
            {
                UnityEngine.Object.Destroy(go);
            }
            if (mainCam != null)
            {
                var rt2 = RenderTexture.GetTemporary(320, 180, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
                var prev = mainCam.targetTexture;
                mainCam.targetTexture = rt2;
                mainCam.Render();
                mainCam.Render();
                mainCam.targetTexture = prev;
                RenderTexture.ReleaseTemporary(rt2);
            }
        }

        static void Capture(Camera cam, string name, int width = W, int height = H)
        {
            if (cam == null) { Log("  截图失败：" + name + " 没有主相机"); return; }
            try
            {
                if (ShaderUtil.allowAsyncCompilation) ShaderUtil.allowAsyncCompilation = false;
                RenderTexture rtHdr = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
                RenderTexture rtLdr = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                RenderTexture prevActive = RenderTexture.active;
                RenderTexture prevTarget = cam.targetTexture;
                cam.targetTexture = rtHdr;
                cam.Render();   // 预热一次，避免变体未编完
                cam.Render();
                Graphics.Blit(rtHdr, rtLdr);
                RenderTexture.active = rtLdr;
                var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                tex.Apply(false, false);
                var px = tex.GetPixels32();
                int pinkN = 0, blackN = 0;
                double sum = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    float r = px[i].r / 255f, g = px[i].g / 255f, b = px[i].b / 255f;
                    float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    sum += luma;
                    if (r > 0.65f && b > 0.65f && g < 0.4f) pinkN++;
                    if (luma < 0.02f) blackN++;
                }
                float mean = (float)(sum / px.Length), pink = pinkN / (float)px.Length, black = blackN / (float)px.Length;
                string path = Path.Combine(LogDir, name + ".png");
                File.WriteAllBytes(Path.GetFullPath(path), tex.EncodeToPNG());
                Log("  截图 " + path + " mean=" + mean.ToString("F3") + " pink=" + pink.ToString("F3") + " black=" + black.ToString("F3"));
                UnityEngine.Object.DestroyImmediate(tex);
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rtHdr);
                RenderTexture.ReleaseTemporary(rtLdr);
            }
            catch (Exception e) { Log("  截图异常 " + name + ": " + e); }
        }

        static void Log(string s)
        {
            Report.AppendLine(s);
            Debug.Log("[SBH-QA] " + s);
        }
    }
}

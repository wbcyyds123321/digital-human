// 最终场景静态校验（不修改场景）：batchmode 用
//   -executeMethod Yixuan.EditorTools.SovietBlueHourValidate.Run -quit
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using Yixuan.World;
using Yixuan.Presentation;

namespace Yixuan.EditorTools
{
    public static class SovietBlueHourValidate
    {
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(SovietBlueHourSceneBuilder.TargetScene, OpenSceneMode.Single);
            var report = new Dictionary<string, object>();
            var problems = new List<string>();
            void Req(bool ok, string what) { if (!ok) problems.Add(what); }

            Req(GameObject.Find("RainyConvenienceDiorama") == null, "旧微缩景观仍在场景里");
            var wrapper = GameObject.Find("SovietBlueHour_Map");
            Req(wrapper != null, "没有 SovietBlueHour_Map");
            var profile = wrapper != null ? wrapper.GetComponent<MapWorldProfile>() : null;
            Req(profile != null, "地图 wrapper 上没有 MapWorldProfile");
            if (profile != null)
            {
                profile.EnsureData();
                report["map_obstacles"] = profile.ObstacleBounds.Count;
                report["map_nav_nodes"] = profile.Nodes != null ? profile.Nodes.Length : 0;
                report["map_nav_edges"] = profile.Edges != null ? profile.Edges.Length : 0;
                report["map_anchors"] = profile.Anchors.Count;
                report["map_spawn"] = profile.SpawnPoint.ToString("F2");
                Req(profile.HasGraph, "导航图数据没有加载");
                Req(profile.Anchors.Count >= 30, "锚点数量过少");
            }
            var actor = GameObject.Find("Yixuan_Test");
            Req(actor != null, "找不到角色 Yixuan_Test");
            if (actor != null)
            {
                report["character_pos"] = actor.transform.position.ToString("F2");
                report["character_yaw"] = actor.transform.eulerAngles.y.ToString("F1");
                Req(Mathf.Abs(actor.transform.position.z + 62f) < 1.5f, "角色不在出生点");
                var snap = actor.GetComponent<Yixuan.Motion.GroundSnap>();
                Req(snap != null && Mathf.Abs(snap.GroundY) < 0.01f, "GroundSnap 地面高度不是 0");
                var loco = actor.GetComponent<Yixuan.Motion.CharacterLocomotion>();
                Req(loco != null, "角色没有 CharacterLocomotion");
            }
            var cam = Camera.main;
            Req(cam != null, "找不到 Main Camera");
            if (cam != null)
            {
                var d = cam.GetComponent<CameraDirector>();
                report["camera_far"] = cam.farClipPlane;
                report["camera_fov"] = cam.fieldOfView;
                Req(d != null && d.ThirdPerson, "CameraDirector 没开第三人称");
                if (d != null)
                {
                    report["tp_yaw_offset"] = d.TP_YawOffset;
                    report["tp_distance"] = d.TP_Distance;
                    report["tp_pitch"] = d.TP_Pitch;
                    report["tp_look_height"] = d.TP_LookHeight;
                    report["tp_fov"] = d.TP_Fov;
                    Req(d.FollowTarget != null, "相机 FollowTarget 为空");
                    Req(d.EyeSource != null, "相机 EyeSource（眼睛相机）为空");
                }
                Req(cam.farClipPlane >= 800f, "相机远裁剪面不足 800m");
            }
            var sky = GameObject.Find("SovietBlueHourSky");
            Req(sky != null, "没有天空罩");
            var moon = GameObject.Find("MoonLight");
            var key = GameObject.Find("KeyLight");
            var fill = GameObject.Find("FillLight");
            var rim = GameObject.Find("RimLight");
            Req(moon != null && key != null && fill != null && rim != null, "灯光组不完整");
            if (moon != null) report["moon_layer_mask"] = moon.GetComponent<Light>().cullingMask;
            if (fill != null) report["fill_layer_mask"] = fill.GetComponent<Light>().cullingMask;
            report["ui_chat"] = GameObject.Find("UI_Chat") != null;
            report["ui_control_panel"] = GameObject.Find("UI_ControlPanel") != null;
            report["vision_system"] = GameObject.Find("VisionSystem") != null;
            report["eye_camera"] = GameObject.Find("EyeCamera") != null;
            var chat = UnityEngine.Object.FindObjectOfType<Yixuan.AI.ChatSession>();
            var runner = UnityEngine.Object.FindObjectOfType<Yixuan.AI.Tasks.TaskMissionRunner>();
            report["chat_session"] = chat != null;
            report["task_runner"] = runner != null;
            var panel = UnityEngine.Object.FindObjectOfType<Yixuan.AI.Tasks.TaskMissionPanel>();
            report["task_panel"] = panel != null;
            report["task_panel_request_empty"] = panel != null && string.IsNullOrEmpty(panel.Request);
            Req(chat != null && runner != null, "对话/任务运行时组件缺失");
            report["map_renderers"] = wrapper != null ? wrapper.GetComponentsInChildren<Renderer>(true).Length : 0;
            var mr = wrapper != null ? wrapper.GetComponentsInChildren<MeshRenderer>(true) : new MeshRenderer[0];
            int subs = 0; long tris = 0;
            for (int i = 0; i < mr.Length; i++)
            {
                var m = mr[i].GetComponent<MeshFilter>();
                if (m == null || m.sharedMesh == null) continue;
                var mesh = m.sharedMesh;
                subs += mesh.subMeshCount;
                for (int s = 0; s < mesh.subMeshCount; s++) tris += mesh.GetIndexCount(s) / 3;
            }
            report["map_submeshes"] = subs;
            report["map_triangles_shared"] = tris;
            report["problems"] = problems;
            report["scene"] = scene.path;
            Directory.CreateDirectory("Logs/soviet_bluehour_qa");
            // JsonUtility 不支持 Dictionary，用简单文本写
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("{");
            int n = 0;
            foreach (var kv in report)
            {
                n++;
                sb.Append("  \"").Append(kv.Key).Append("\": ");
                if (kv.Value is string s) sb.Append("\"").Append(s.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"");
                else if (kv.Value is bool b) sb.Append(b ? "true" : "false");
                else if (kv.Value is List<string> list)
                {
                    sb.Append("[");
                    for (int i = 0; i < list.Count; i++) { if (i > 0) sb.Append(", "); sb.Append("\"").Append(list[i]).Append("\""); }
                    sb.Append("]");
                }
                else sb.Append(kv.Value.ToString());
                sb.AppendLine(n == report.Count ? "" : ",");
            }
            sb.AppendLine("}");
            File.WriteAllText("Logs/soviet_bluehour_qa/final_validate.json", sb.ToString());
            Debug.Log("[SBH-Validate] 问题=" + problems.Count + "；报告=Logs/soviet_bluehour_qa/final_validate.json");
            for (int i = 0; i < problems.Count; i++) Debug.LogError("[SBH-Validate] " + problems[i]);
        }
    }
}

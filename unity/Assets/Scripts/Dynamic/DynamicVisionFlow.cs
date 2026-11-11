// Node-2 real visual selection pipeline (separate from world query):
// capture a real frame -> ask the configured vision model which candidate the user refers to
// -> rotate/move the view to put that target under the fixed centre crosshair -> capture a
// new frame -> math-pick the centre of the new frame -> confirm the SAME entity id -> only
// then hand the target to the interaction controller.
//
// Injection mode exists only for controller plumbing tests and is always labelled
// selection_source=injection_vision; it never pretends to be a real model response.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Yixuan.Presentation;
using Yixuan.Vision;

namespace Yixuan.Dynamic
{
    [Serializable]
    public class VisionCandidate
    {
        public string entity_id;
        public string display_name;
        public string cls;
        public Vector3 world_position;
        public string color;
    }

    [Serializable]
    public class VisionFlowResult
    {
        public bool ok;
        public bool pending;
        public string mode = "real_model";  // real_model | injection_vision
        public string error = "";
        public string target_id = "";
        public string confirmed_id = "";
        public string selection_source = "";  // vision | injection_vision
        public string frame_before = "";
        public string frame_after = "";
        public string frame_before_sha256 = "";
        public string frame_after_sha256 = "";
        public string model_raw = "";
        public string camera_before = "";
        public string camera_after = "";
        public float center_confirm_distance_px = -1f;
        public int attempts;
    }

    public sealed class DynamicVisionFlow : MonoBehaviour
    {
        public static DynamicVisionFlow Instance { get; private set; }

        public static string InjectionTargetId = "";
        public static bool InjectionEnabled = false;

        public string LastError { get; private set; } = "";
        public bool ServiceBlocked { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSession()
        {
            Instance = null;
            InjectionTargetId = "";
            InjectionEnabled = false;
        }

        void Awake()
        {
            Instance = this;
        }

        public static DynamicVisionFlow EnsureInstance()
        {
            if (Instance != null) return Instance;
            var existing = FindObjectOfType<DynamicVisionFlow>();
            if (existing != null)
            {
                Instance = existing;
                return existing;
            }
            var go = new GameObject("DynamicVisionFlow");
            Instance = go.AddComponent<DynamicVisionFlow>();
            return Instance;
        }

        public static byte[] CaptureJpg(Camera camera, int width, int height, out string sha256)
        {
            sha256 = null;
            if (camera == null) return null;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            Texture2D texture = null;
            try
            {
                rt.Create();
                camera.targetTexture = rt;
                camera.Render();
                RenderTexture.active = rt;
                texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply(false, false);
                byte[] jpg = texture.EncodeToJPG(88);
                sha256 = Sha256(jpg);
                return jpg;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (texture != null) Destroy(texture);
                rt.Release();
                Destroy(rt);
            }
        }

        public static string Sha256(byte[] bytes)
        {
            if (bytes == null) return null;
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder();
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static bool WriteFrame(string directory, string name, byte[] jpg, out string path)
        {
            path = "";
            if (string.IsNullOrEmpty(directory) || jpg == null) return false;
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, name);
            File.WriteAllBytes(path, jpg);
            return true;
        }

        public QwenVisionClient FindClient()
        {
            var client = FindObjectOfType<QwenVisionClient>();
            return client;
        }

        public static string BuildPrompt(string utterance, List<VisionCandidate> candidates)
        {
            var sb = new StringBuilder();
            sb.Append("你是游戏里的视觉观察助手。玩家说：").Append(utterance).Append("\n");
            sb.Append("画面里只允许从下面这些实体中选择，不能编造 ID：\n");
            foreach (var c in candidates)
            {
                sb.Append("- ").Append(c.entity_id).Append(" | ").Append(c.display_name)
                  .Append(" | ").Append(c.cls).Append(" | 颜色:").Append(c.color)
                  .Append(" | 世界坐标:").Append(c.world_position.x.ToString("F1")).Append(",")
                  .Append(c.world_position.z.ToString("F1")).Append("\n");
            }
            sb.Append("只返回一个 JSON：{\"target_id\":\"候选ID\",\"reason\":\"简短原因\",\"confidence\":0-1}\n");
            sb.Append("如果画面里没有把握，就返回 {\"target_id\":\"\",\"reason\":\"not_visible\"}");
            return sb.ToString();
        }

        public static string ExtractTargetId(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            int key = raw.IndexOf("\"target_id\"", StringComparison.OrdinalIgnoreCase);
            if (key >= 0)
            {
                int colon = raw.IndexOf(':', key);
                int quote = raw.IndexOf('"', colon + 1);
                if (quote >= 0)
                {
                    int end = raw.IndexOf('"', quote + 1);
                    if (end > quote) return raw.Substring(quote + 1, end - quote - 1).Trim();
                }
            }
            foreach (var line in raw.Split('\n'))
                if (line.Contains("entity/")) return line.Trim();
            return "";
        }

        /// <summary>
        /// Runs the full real visual flow. The caller supplies candidates from a world query;
        /// the model must pick one and the centre-crosshair math pick must confirm the same ID
        /// in a new frame. Evidence files are written even when the model is blocked.
        /// </summary>
        public IEnumerator RunFlow(string utterance, string evidenceDirectory,
                                   List<VisionCandidate> candidates, Action<VisionFlowResult> done)
        {
            var result = new VisionFlowResult { selection_source = "vision" };
            var world = DynamicWorld.Instance;
            var cameraDirector = world != null ? world.CameraDirector : FindObjectOfType<CameraDirector>();
            var camera = cameraDirector != null && cameraDirector.Camera != null
                ? cameraDirector.Camera : Camera.main;
            if (camera == null)
            {
                result.error = "no_camera";
                done(result);
                yield break;
            }
            if (candidates == null || candidates.Count == 0)
            {
                result.error = "no_candidates";
                done(result);
                yield break;
            }
            var before = CaptureJpg(camera, 960, 540, out var shaBefore);
            string beforePath;
            WriteFrame(evidenceDirectory, "vision_frame_before.jpg", before, out beforePath);
            result.frame_before = beforePath;
            result.frame_before_sha256 = shaBefore;
            result.camera_before = camera.transform.position.ToString("F2") + "|" +
                                   camera.transform.eulerAngles.ToString("F1");

            string raw = null;
            if (InjectionEnabled)
            {
                result.mode = "injection_vision";
                result.selection_source = "injection_vision";
                raw = "{\"target_id\":\"" + InjectionTargetId + "\",\"reason\":\"injection test\"}";
            }
            else
            {
                var client = FindClient();
                if (client == null)
                {
                    result.error = "vision_client_missing";
                    result.pending = true;
                    done(result);
                    yield break;
                }
                if (!client.HasKey)
                {
                    result.error = "vision_api_key_missing";
                    result.pending = true;
                    done(result);
                    yield break;
                }
                if (client.ServiceBlocked)
                {
                    result.error = "vision_service_blocked:" + client.LastError;
                    result.pending = true;
                    done(result);
                    yield break;
                }
                string prompt = BuildPrompt(utterance, candidates);
                string response = null;
                string error = null;
                yield return client.AskStructured(prompt, before,
                    value => response = value, value => error = value);
                if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(response))
                {
                    result.error = "vision_model_error:" + (error ?? "empty_response");
                    result.pending = true;
                    result.model_raw = response ?? "";
                    done(result);
                    yield break;
                }
                raw = response;
            }
            result.model_raw = raw;
            string targetId = ExtractTargetId(raw);
            result.target_id = targetId;
            if (string.IsNullOrEmpty(targetId))
            {
                result.error = "model_returned_no_target";
                done(result);
                yield break;
            }
            VisionCandidate target = null;
            foreach (var c in candidates) if (c.entity_id == targetId) target = c;
            if (target == null)
            {
                result.error = "model_target_not_in_candidates";
                done(result);
                yield break;
            }

            // centre the reticle on the target with a real camera move, then re-capture
            var savedPosition = camera.transform.position;
            var savedRotation = camera.transform.rotation;
            bool savedDirector = cameraDirector != null && cameraDirector.enabled;
            if (cameraDirector != null) cameraDirector.enabled = false;
            Vector3 look = target.world_position;
            Vector3 fromCharacter = look - (world != null ? world.CharacterPosition : camera.transform.position);
            fromCharacter.y = 0f;
            if (fromCharacter.sqrMagnitude < 0.1f) fromCharacter = -camera.transform.forward;
            Vector3 eye = look - fromCharacter.normalized * 7f + Vector3.up * 2.2f;
            camera.transform.position = eye;
            camera.transform.rotation = Quaternion.LookRotation((look - eye).normalized, Vector3.up);
            yield return null;
            yield return null;
            yield return null;

            var after = CaptureJpg(camera, 960, 540, out var shaAfter);
            string afterPath;
            WriteFrame(evidenceDirectory, "vision_frame_after.jpg", after, out afterPath);
            result.frame_after = afterPath;
            result.frame_after_sha256 = shaAfter;
            result.camera_after = camera.transform.position.ToString("F2") + "|" +
                                  camera.transform.eulerAngles.ToString("F1");

            string pickedId = null;
            float distance = -1f;
            if (world != null)
                world.TryPickAtCrosshair(camera, out pickedId, out _, out distance);
            result.attempts = 1;
            result.confirmed_id = pickedId ?? "";
            result.center_confirm_distance_px = 0f;
            bool confirmed = pickedId == targetId;
            if (!confirmed && world != null)
            {
                // second attempt: re-centre exactly on the candidate and capture a new frame
                camera.transform.rotation = Quaternion.LookRotation((look - camera.transform.position).normalized,
                                                                    Vector3.up);
                yield return null;
                yield return null;
                var after2 = CaptureJpg(camera, 960, 540, out var shaAfter2);
                string after2Path;
                WriteFrame(evidenceDirectory, "vision_frame_after_retry.jpg", after2, out after2Path);
                result.frame_after = after2Path;
                result.frame_after_sha256 = shaAfter2;
                string retryId;
                world.TryPickAtCrosshair(camera, out retryId, out _, out distance);
                result.confirmed_id = retryId ?? "";
                result.attempts = 2;
                confirmed = retryId == targetId;
            }
            camera.transform.position = savedPosition;
            camera.transform.rotation = savedRotation;
            if (cameraDirector != null) cameraDirector.enabled = savedDirector;
            result.ok = confirmed;
            if (!confirmed) result.error = "centre_reconfirm_failed:expected=" + targetId + " got=" + result.confirmed_id;
            done(result);
        }
    }
}

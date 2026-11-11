using System;
using System.IO;
using UnityEngine;
using Yixuan.Stage2;
using Yixuan.Vision;

namespace Yixuan.Stage5
{
    public sealed class Stage5EyeFrame
    {
        public long FrameId;
        public Stage2CameraSnapshot Snapshot;
        public byte[] JpegBytes;
        public byte[] PngBytes;
        public string JpegSha256 = "";
        public string PngSha256 = "";
        public int Width;
        public int Height;
        public string JpegPath = "";
        public string PngPath = "";
        public string Source = "unavailable";
        public float CapturedAt;
    }

    /// <summary>
    /// Read-only clone camera that follows the real EyeCamera for one frame.
    /// It never changes EyeCamera.targetTexture and never touches Physics/colliders.
    /// The raw model image and the Stage-2 ray snapshot are produced in the same
    /// capture call so old points cannot be applied to a newer camera pose.
    /// </summary>
    public sealed class Stage5EyeCapture : MonoBehaviour
    {
        public int Width = 640;
        public int Height = 360;
        public int JpegQuality = 82;
        public Camera EyeCamera { get; private set; }
        public string LastError { get; private set; } = "";
        public long NextFrameId = 1;

        Camera _clone;
        RenderTexture _rt;
        Texture2D _tex;

        public bool Resolve()
        {
            if (EyeCamera != null) return true;
            var go = GameObject.Find("EyeCamera");
            if (go != null) EyeCamera = go.GetComponent<Camera>();
            if (EyeCamera == null)
            {
                var observer = FindObjectOfType<VisionObserver>();
                if (observer != null) EyeCamera = observer.EyeCamera;
            }
            if (EyeCamera == null) LastError = "missing EyeCamera";
            return EyeCamera != null;
        }

        void EnsureRuntime()
        {
            if (_clone == null)
            {
                var go = new GameObject("Stage5EyeCloneCamera");
                go.transform.SetParent(transform, false);
                _clone = go.AddComponent<Camera>();
                _clone.enabled = false;
                _clone.clearFlags = CameraClearFlags.SolidColor;
                _clone.backgroundColor = new Color(0.015f, 0.02f, 0.03f, 1f);
                _clone.allowHDR = false;
                _clone.allowMSAA = false;
                _clone.depth = -50;
            }
            if (_rt == null || _rt.width != Width || _rt.height != Height)
            {
                if (_rt != null) { _rt.Release(); Destroy(_rt); }
                _rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                _rt.name = "Stage5EyeCaptureRT";
                _rt.Create();
            }
            if (_tex == null || _tex.width != Width || _tex.height != Height)
            {
                if (_tex != null) Destroy(_tex);
                _tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                _tex.name = "Stage5EyeCaptureTexture";
            }
        }

        public Stage5EyeFrame Capture()
        {
            if (!Resolve()) return null;
            EnsureRuntime();
            var eye = EyeCamera;
            var frame = new Stage5EyeFrame
            {
                FrameId = NextFrameId++,
                Width = Width,
                Height = Height,
                CapturedAt = Time.realtimeSinceStartup,
                Source = "character_eye",
            };
            _clone.transform.position = eye.transform.position;
            _clone.transform.rotation = eye.transform.rotation;
            _clone.fieldOfView = eye.fieldOfView;
            _clone.nearClipPlane = eye.nearClipPlane;
            _clone.farClipPlane = eye.farClipPlane;
            _clone.aspect = (float)Width / Mathf.Max(1, Height);
            _clone.cullingMask = eye.cullingMask;
            _clone.targetTexture = _rt;

            var prevActive = RenderTexture.active;
            try
            {
                _clone.Render();
                RenderTexture.active = _rt;
                _tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                _tex.Apply(false, false);
            }
            catch (Exception e)
            {
                LastError = "capture failed: " + e.Message;
                _clone.targetTexture = null;
                RenderTexture.active = prevActive;
                return null;
            }
            finally
            {
                _clone.targetTexture = null;
                RenderTexture.active = prevActive;
            }

            frame.JpegBytes = _tex.EncodeToJPG(JpegQuality);
            frame.PngBytes = _tex.EncodeToPNG();
            frame.JpegSha256 = Sha256(frame.JpegBytes);
            frame.PngSha256 = Sha256(frame.PngBytes);

            var snap = new Stage2CameraSnapshot
            {
                FrameId = frame.FrameId,
                Source = "character_eye",
                PoseId = "",
                Position = eye.transform.position,
                Rotation = eye.transform.rotation,
                Fov = eye.fieldOfView,
                Near = eye.nearClipPlane,
                Far = eye.farClipPlane,
                Aspect = (float)Width / Mathf.Max(1, Height),
                RtWidth = Width,
                RtHeight = Height,
                RealtimeSinceStartup = Time.realtimeSinceStartupAsDouble,
                Projection = _clone.projectionMatrix,
                Valid = true,
            };
            frame.Snapshot = snap;
            LastError = "";
            return frame;
        }

        public void SaveFrame(Stage5EyeFrame frame, string directory, string tag)
        {
            if (frame == null || string.IsNullOrEmpty(directory)) return;
            Directory.CreateDirectory(directory);
            string safe = string.IsNullOrEmpty(tag) ? "frame" : tag;
            frame.JpegPath = Path.Combine(directory, "raw_" + safe + "_" + frame.FrameId + ".jpg");
            frame.PngPath = Path.Combine(directory, "raw_" + safe + "_" + frame.FrameId + ".png");
            if (frame.JpegBytes != null) File.WriteAllBytes(frame.JpegPath, frame.JpegBytes);
            if (frame.PngBytes != null) File.WriteAllBytes(frame.PngPath, frame.PngBytes);
            if (frame.Snapshot != null)
                File.WriteAllText(Path.Combine(directory, "raw_" + safe + "_" + frame.FrameId + ".snapshot.json"),
                    JsonUtility.ToJson(frame.Snapshot.ToRecord(), true));
        }

        public static byte[] CenterCropJpeg(Stage5EyeFrame frame, float fraction, out Rect sourceUv, out int width, out int height)
        {
            var source = new Texture2D(2, 2, TextureFormat.RGB24, false);
            Texture2D crop = null;
            try
            {
                source.LoadImage(frame.PngBytes);
                width = Mathf.Max(1, Mathf.RoundToInt(source.width * Mathf.Clamp(fraction, 0.05f, 1f)));
                height = Mathf.Max(1, Mathf.RoundToInt(source.height * Mathf.Clamp(fraction, 0.05f, 1f)));
                int left = (source.width - width) / 2;
                int bottom = (source.height - height) / 2;
                sourceUv = new Rect((float)left / source.width, (float)(source.height - bottom - height) / source.height,
                    (float)width / source.width, (float)height / source.height);
                crop = new Texture2D(width, height, TextureFormat.RGB24, false);
                crop.SetPixels(source.GetPixels(left, bottom, width, height));
                crop.Apply();
                return crop.EncodeToJPG(95);
            }
            finally
            {
                Destroy(source);
                if (crop != null) Destroy(crop);
            }
        }

        public static Vector2 ToFrameUv(Rect sourceUv, float u, float v)
        {
            return new Vector2(sourceUv.x + u * sourceUv.width, sourceUv.y + v * sourceUv.height);
        }

        public static string Sha256(byte[] bytes)
        {
            if (bytes == null) return "";
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        void OnDestroy()
        {
            if (_clone != null && _clone.gameObject != null) Destroy(_clone.gameObject);
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
            if (_tex != null) Destroy(_tex);
        }
    }
}

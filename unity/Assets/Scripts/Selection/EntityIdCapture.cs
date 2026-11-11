using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using Newtonsoft.Json;

namespace Yixuan.Pick
{
    /// <summary>
    /// On-demand capture of the real EyeCamera view as three co-registered
    /// products in one call:
    ///   * RGB  : rendered by a read-only clone camera with the exact same pose /
    ///            projection / aspect (never touches EyeCamera.targetTexture),
    ///   * ID   : per-pixel 24-bit runtime entity code, rendered from the actual
    ///            map geometry (merged meshes + live dynamic renderers) with a
    ///            depth pre-pass so occluders stay occluded,
    ///   * depth: linear eye depth of the same surface.
    /// All three share one frozen PickCameraSnapshot (pose, view/projection
    /// matrices, viewport, resolution, frame id, frame count, time).
    ///
    /// Implementation note: the ID/depth pass is a raw command buffer executed
    /// against a RenderTexture, so it does not depend on URP renderer features
    /// and cannot disturb the saved scene. The shader receives an explicit
    /// view/projection matrix, so the ID image and the ray unprojection are the
    /// same camera math by construction.
    /// </summary>
    public static class EntityIdCapture
    {
        public const string PickIdShaderPath = "FirstPersonSelection/PickId";
        public const int DefaultWidth = 640;

        public static string LastError { get; private set; } = "";
        public static long NextFrameId = 1;
        public static PickCameraSnapshot LastSnapshot { get; private set; }

        // Diagnostics only (never enabled by production code paths): isolate the
        // occluder / static-pick / direct-pick passes while investigating a bug.
        public static bool DebugDisableOccluders;
        public static bool DebugDisableStaticPick;
        public static bool DebugDisableDirectPick;
        public static bool DebugClearWhite;
        /// <summary>Diagnostics: <![CDATA[>0]]> draws only that entity code in the direct pass.</summary>
        public static int DebugOnlyDirectCode;

        static Material _occluderMaterial;
        static Material _staticPickMaterial;
        static Material _entityPickMaterial;
        static Shader _pickIdShader;
        static Camera _cloneCamera;
        static GameObject _runtimeRoot;
        static RenderTexture _rtId;
        static RenderTexture _rtDepth;
        static RenderTexture _rtRgb;
        static Texture2D _readId;
        static Texture2D _readDepth;
        static Texture2D _readRgb;
        static readonly List<Renderer> VisibleBuffer = new List<Renderer>(180);

        public static bool Ready
        {
            get
            {
                EnsureResources();
                return _pickIdShader != null && _occluderMaterial != null;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSession()
        {
            LastError = "";
            NextFrameId = 1;
            LastSnapshot = null;
            _occluderMaterial = null;
            _staticPickMaterial = null;
            _entityPickMaterial = null;
            _pickIdShader = null;
            _cloneCamera = null;
            _runtimeRoot = null;
            _rtId = null;
            _rtDepth = null;
            _rtRgb = null;
            _readId = null;
            _readDepth = null;
            _readRgb = null;
            VisibleBuffer.Clear();
        }

        static void EnsureResources()
        {
            if (_pickIdShader == null)
            {
                _pickIdShader = Resources.Load<Shader>(PickIdShaderPath);
                if (_pickIdShader == null) _pickIdShader = Shader.Find("Hidden/Yixuan/FirstPersonSelection/PickId");
                if (_pickIdShader == null)
                {
                    LastError = "缺少着色器 " + PickIdShaderPath;
                    return;
                }
            }
            if (_occluderMaterial == null)
            {
                _occluderMaterial = new Material(_pickIdShader) { name = "FPS_PickId_Occluder", hideFlags = HideFlags.DontSave };
                _occluderMaterial.SetFloat("_PickUseVertexColor", 0f);
                _occluderMaterial.SetVector("_PickCode", Vector4.zero);
            }
            if (_staticPickMaterial == null)
            {
                _staticPickMaterial = new Material(_pickIdShader) { name = "FPS_PickId_Static", hideFlags = HideFlags.DontSave };
                _staticPickMaterial.SetFloat("_PickUseVertexColor", 1f);
                _staticPickMaterial.SetVector("_PickCode", Vector4.zero);
            }
            if (_entityPickMaterial == null)
            {
                _entityPickMaterial = new Material(_pickIdShader) { name = "FPS_PickId_Entity", hideFlags = HideFlags.DontSave };
                _entityPickMaterial.SetFloat("_PickUseVertexColor", 0f);
                _entityPickMaterial.SetVector("_PickCode", Vector4.zero);
            }
            if (_runtimeRoot == null)
            {
                _runtimeRoot = new GameObject("FirstPersonSelection_IDCapture");
                _runtimeRoot.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(_runtimeRoot);
            }
            if (_cloneCamera == null)
            {
                var go = new GameObject("FirstPersonSelection_CloneEye");
                go.transform.SetParent(_runtimeRoot.transform, false);
                _cloneCamera = go.AddComponent<Camera>();
                _cloneCamera.enabled = false;
                _cloneCamera.allowHDR = false;
                _cloneCamera.allowMSAA = false;
                _cloneCamera.clearFlags = CameraClearFlags.SolidColor;
                _cloneCamera.backgroundColor = new Color(0.02f, 0.03f, 0.05f, 1f);
                _cloneCamera.depth = -100;
            }
        }

        /// <summary>Capture one frozen EyeCamera frame (RGB + ID + depth).</summary>
        public static PickFrame Capture(Camera eye, int width = 0, int height = 0)
        {
            LastError = "";
            var frame = new PickFrame();
            if (eye == null)
            {
                frame.error = "missing EyeCamera";
                LastError = frame.error;
                return frame;
            }
            EnsureResources();
            if (_pickIdShader == null)
            {
                frame.error = LastError;
                return frame;
            }
            float aspect = eye.aspect > 0.01f ? eye.aspect : 16f / 9f;
            if (width <= 0) width = DefaultWidth;
            if (height <= 0) height = Mathf.Max(64, Mathf.RoundToInt(width / aspect));
            width = Mathf.Clamp(width, 64, 1920);
            height = Mathf.Clamp(height, 64, 1080);
            aspect = (float)width / Mathf.Max(1, height);
            EnsureTextures(width, height);

            long frameId = NextFrameId++;
            var snapshot = new PickCameraSnapshot
            {
                frame_id = frameId,
                frame_count = Time.frameCount,
                time_since_startup = Time.realtimeSinceStartupAsDouble,
                source = "character_eye",
                position = eye.transform.position,
                rotation = eye.transform.rotation,
                fov = eye.fieldOfView,
                near = Mathf.Max(0.01f, eye.nearClipPlane),
                far = Mathf.Max(eye.nearClipPlane + 1f, eye.farClipPlane),
                aspect = aspect,
                width = width,
                height = height,
                viewport_x = 0f,
                viewport_y = 0f,
                viewport_w = 1f,
                viewport_h = 1f,
            };
            snapshot.view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * eye.transform.worldToLocalMatrix;
            snapshot.projection_gl = Matrix4x4.Perspective(snapshot.fov, aspect, snapshot.near, snapshot.far);
            // Do NOT request the render-into-texture Y flip here. On this D3D11
            // editor GL.GetGPUProjectionMatrix(..., true) returns a Y-flipped
            // projection, so the ID/depth RT rows come back top-down while the RGB
            // camera RT (normal projection) comes back bottom-up. Every centre-based
            // check was invariant to that, but off-centre reconstruction mixed a
            // top-down depth row with a bottom-up viewport ray (A_r1 T1.k ~7.86cm
            // at pixel (7,263)). Using the unflipped GPU projection keeps ID/depth
            // rows bottom-up like RGB and like ViewportRay/WorldPointAt.
            snapshot.projection_gpu = GL.GetGPUProjectionMatrix(snapshot.projection_gl, false);
            frame.snapshot = snapshot;
            frame.width = width;
            frame.height = height;
            LastSnapshot = snapshot;

            // ---- 1. RGB through a read-only clone camera (same snapshot). ----
            try
            {
                _cloneCamera.transform.position = snapshot.position;
                _cloneCamera.transform.rotation = snapshot.rotation;
                _cloneCamera.fieldOfView = snapshot.fov;
                _cloneCamera.nearClipPlane = snapshot.near;
                _cloneCamera.farClipPlane = snapshot.far;
                _cloneCamera.aspect = aspect;
                _cloneCamera.cullingMask = eye.cullingMask;
                _cloneCamera.targetTexture = _rtRgb;
                _cloneCamera.Render();
                _cloneCamera.targetTexture = null;
                var previous = RenderTexture.active;
                RenderTexture.active = _rtRgb;
                _readRgb.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                _readRgb.Apply(false, false);
                RenderTexture.active = previous;
                frame.rgb_pixels = _readRgb.GetPixels32();
                frame.rgb_png = _readRgb.EncodeToPNG();
            }
            catch (Exception exception)
            {
                frame.error = "rgb capture failed: " + exception.Message;
                LastError = frame.error;
                return frame;
            }

            // ---- 2. ID + linear depth through one command buffer. ----
            try
            {
                var command = new CommandBuffer { name = "FirstPersonSelection_PickId" };
                command.SetRenderTarget(
                    new RenderTargetIdentifier[] { _rtId, _rtDepth },
                    new RenderTargetIdentifier(_rtId));
                command.ClearRenderTarget(true, true, DebugClearWhite ? Color.white : new Color(0f, 0f, 0f, 1f));
                SetMaterialCamera(_occluderMaterial, snapshot);
                SetMaterialCamera(_staticPickMaterial, snapshot);
                SetMaterialCamera(_entityPickMaterial, snapshot);

                VisibleBuffer.Clear();
                VisibleBuffer.AddRange(EntityPickRegistry.VisibleRenderers(eye));
                if (!DebugDisableOccluders)
                {
                    // Draw each renderer's mesh with an explicit transform instead of
                    // CommandBuffer.DrawRenderer: outside a live camera render the
                    // renderer draw path can apply stale culling and silently skip most
                    // of the map (observed: only ~4.6k/307k pixels written). Frustum
                    // culling here is computed from the frozen snapshot, not from the
                    // editor camera.
                    var planes = GeometryUtility.CalculateFrustumPlanes(
                        eye.projectionMatrix * eye.worldToCameraMatrix);
                    for (int i = 0; i < VisibleBuffer.Count; i++)
                    {
                        var renderer = VisibleBuffer[i];
                        if (renderer == null) continue;
                        if (!GeometryUtility.TestPlanesAABB(planes, renderer.bounds)) continue;
                        Mesh mesh; int submeshStart, submeshCount;
                        EntityPickRegistry.GetRendererMeshRange(renderer, out mesh, out submeshStart, out submeshCount);
                        if (mesh == null) continue;
                        var matrix = EntityPickRegistry.GetRendererDrawMatrix(renderer);
                        for (int submesh = submeshStart; submesh < submeshStart + submeshCount && submesh < mesh.subMeshCount; submesh++)
                            command.DrawMesh(mesh, matrix, _occluderMaterial, submesh, -1, null);
                    }
                }

                if (EntityPickRegistry.StaticPickMesh != null && !DebugDisableStaticPick)
                {
                    command.DrawMesh(EntityPickRegistry.StaticPickMesh, Matrix4x4.identity, _staticPickMaterial);
                }

                var propertyBlock = new MaterialPropertyBlock();
                if (!DebugDisableDirectPick)
                foreach (var entry in EntityPickRegistry.Entries)
                {
                    if (!entry.Selectable || entry.Renderers.Count == 0) continue;
                    if (DebugOnlyDirectCode > 0 && entry.Code != DebugOnlyDirectCode) continue;
                    Color32 color = EntityPickRegistry.EncodeCode(entry.Code);
                    propertyBlock.Clear();
                    propertyBlock.SetVector("_PickCode", new Vector4(color.r, color.g, color.b, 255f));
                    foreach (var renderer in entry.Renderers)
                    {
                        if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                        if ((eye.cullingMask & (1 << renderer.gameObject.layer)) == 0) continue;
                        Mesh mesh; int submeshStart, submeshCount;
                        EntityPickRegistry.GetRendererMeshRange(renderer, out mesh, out submeshStart, out submeshCount);
                        if (mesh == null) continue;
                        var matrix = EntityPickRegistry.GetRendererDrawMatrix(renderer);
                        for (int submesh = submeshStart; submesh < submeshStart + submeshCount && submesh < mesh.subMeshCount; submesh++)
                            command.DrawMesh(mesh, matrix, _entityPickMaterial, submesh, -1, propertyBlock);
                    }
                }

                Graphics.SetRenderTarget(null);
                Graphics.ExecuteCommandBuffer(command);
                command.Release();
                Graphics.SetRenderTarget(null);

                var previous = RenderTexture.active;
                RenderTexture.active = _rtId;
                _readId.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                _readId.Apply(false, false);
                RenderTexture.active = _rtDepth;
                _readDepth.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                _readDepth.Apply(false, false);
                RenderTexture.active = previous;

                var idPixels = _readId.GetPixels32();
                var depthPixels = _readDepth.GetPixels32();
                frame.id_pixels = new Color32[width * height];
                frame.codes = new int[width * height];
                frame.depth01 = new float[width * height];
                frame.depth_rgba = new Color32[width * height];
                for (int i = 0; i < idPixels.Length; i++)
                {
                    var color = idPixels[i];
                    frame.id_pixels[i] = color;
                    frame.codes[i] = color.r | (color.g << 8) | (color.b << 16);
                    frame.depth_rgba[i] = depthPixels[i];
                    Vector4 encoded = new Vector4(
                        depthPixels[i].r / 255f, depthPixels[i].g / 255f,
                        depthPixels[i].b / 255f, depthPixels[i].a / 255f);
                    float depth01 = Vector4.Dot(encoded, new Vector4(1f, 1f / 255f, 1f / 65025f, 1f / 16581375f));
                    frame.depth01[i] = Mathf.Clamp01(depth01);
                }
            }
            catch (Exception exception)
            {
                frame.error = "id/depth capture failed: " + exception.Message;
                LastError = frame.error;
                return frame;
            }
            return frame;
        }

        static void SetMaterialCamera(Material material, PickCameraSnapshot snapshot)
        {
            if (material == null) return;
            material.SetMatrix("_PickView", snapshot.view);
            material.SetMatrix("_PickVP", snapshot.projection_gpu * snapshot.view);
            material.SetFloat("_PickNear", snapshot.near);
            material.SetFloat("_PickFar", snapshot.far);
        }

        static void EnsureTextures(int width, int height)
        {
            if (_rtRgb == null || _rtRgb.width != width || _rtRgb.height != height)
            {
                ReleaseTextures();
                _rtRgb = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                { name = "FPS_RGB", hideFlags = HideFlags.DontSave };
                _rtId = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                { name = "FPS_ID", hideFlags = HideFlags.DontSave };
                _rtDepth = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                { name = "FPS_Depth", hideFlags = HideFlags.DontSave };
                _rtRgb.Create();
                _rtId.Create();
                _rtDepth.Create();
                _readRgb = new Texture2D(width, height, TextureFormat.RGB24, false) { hideFlags = HideFlags.DontSave };
                _readId = new Texture2D(width, height, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.DontSave };
                _readDepth = new Texture2D(width, height, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.DontSave };
            }
        }

        static void ReleaseTextures()
        {
            if (_rtRgb != null) { _rtRgb.Release(); UnityEngine.Object.DestroyImmediate(_rtRgb); _rtRgb = null; }
            if (_rtId != null) { _rtId.Release(); UnityEngine.Object.DestroyImmediate(_rtId); _rtId = null; }
            if (_rtDepth != null) { _rtDepth.Release(); UnityEngine.Object.DestroyImmediate(_rtDepth); _rtDepth = null; }
            if (_readRgb != null) { UnityEngine.Object.DestroyImmediate(_readRgb); _readRgb = null; }
            if (_readId != null) { UnityEngine.Object.DestroyImmediate(_readId); _readId = null; }
            if (_readDepth != null) { UnityEngine.Object.DestroyImmediate(_readDepth); _readDepth = null; }
        }

        public static void Dispose()
        {
            ReleaseTextures();
            if (_occluderMaterial != null) { UnityEngine.Object.DestroyImmediate(_occluderMaterial); _occluderMaterial = null; }
            if (_staticPickMaterial != null) { UnityEngine.Object.DestroyImmediate(_staticPickMaterial); _staticPickMaterial = null; }
            if (_entityPickMaterial != null) { UnityEngine.Object.DestroyImmediate(_entityPickMaterial); _entityPickMaterial = null; }
            if (_runtimeRoot != null) UnityEngine.Object.Destroy(_runtimeRoot);
            _runtimeRoot = null;
            _cloneCamera = null;
        }

        // ------------------------------------------------------------ saving
        public static bool SaveFrame(PickFrame frame, string directory, string tag, out string error)
        {
            error = "";
            if (frame == null || !frame.Ok)
            {
                error = frame == null ? "null frame" : frame.error;
                return false;
            }
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, tag + "_rgb.png"), frame.rgb_png);

                var idVis = new Texture2D(frame.width, frame.height, TextureFormat.RGB24, false);
                var depthVis = new Texture2D(frame.width, frame.height, TextureFormat.RGB24, false);
                var idColors = new Color32[frame.width * frame.height];
                var depthColors = new Color32[frame.width * frame.height];
                for (int y = 0; y < frame.height; y++)
                {
                    for (int x = 0; x < frame.width; x++)
                    {
                        // Source arrays are TOP-origin rows; Texture2D/PNG are
                        // BOTTOM-origin. Use distinct source and destination indices:
                        // source(y) = y*w+x, destination = (h-1-y)*w+x.
                        int sourceIndex = y * frame.width + x;
                        int destIndex = (frame.height - 1 - y) * frame.width + x;
                        int code = frame.codes[sourceIndex];
                        idColors[destIndex] = code == 0
                            ? new Color32(8, 10, 14, 255)
                            : CodePalette(code);
                        float depth = frame.depth01[sourceIndex];
                        if (depth <= 0f) depthColors[destIndex] = new Color32(20, 8, 40, 255);
                        else
                        {
                            byte gray = (byte)Mathf.Clamp(Mathf.RoundToInt((1f - depth) * 255f), 24, 255);
                            depthColors[destIndex] = new Color32(gray, gray, gray, 255);
                        }
                    }
                }
                // crosshair at the real eye viewport centre
                int cx = frame.width / 2, cy = frame.height / 2;
                for (int d = -6; d <= 6; d++)
                {
                    if (cx + d >= 0 && cx + d < frame.width) idColors[cy * frame.width + cx + d] = new Color32(255, 255, 255, 255);
                    if (cy + d >= 0 && cy + d < frame.height) idColors[(cy + d) * frame.width + cx] = new Color32(255, 255, 255, 255);
                }
                idVis.SetPixels32(idColors);
                idVis.Apply(false, false);
                depthVis.SetPixels32(depthColors);
                depthVis.Apply(false, false);
                File.WriteAllBytes(Path.Combine(directory, tag + "_id_vis.png"), idVis.EncodeToPNG());
                File.WriteAllBytes(Path.Combine(directory, tag + "_depth_vis.png"), depthVis.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(idVis);
                UnityEngine.Object.DestroyImmediate(depthVis);

                // raw codes for independent checks (int32, row-major bottom-up)
                var raw = new byte[frame.codes.Length * 4];
                Buffer.BlockCopy(frame.codes, 0, raw, 0, raw.Length);
                File.WriteAllBytes(Path.Combine(directory, tag + "_codes.bin"), raw);

                var json = new FrameJson
                {
                    tag = tag,
                    snapshot = frame.snapshot,
                    width = frame.width,
                    height = frame.height,
                    center_code = frame.CodeAt(frame.width / 2, frame.height / 2),
                    center_depth01 = frame.DepthAt(frame.width / 2, frame.height / 2),
                    codes_sha256 = Sha256(raw),
                    rgb_sha256 = Sha256(frame.rgb_png),
                    unique_codes = CountUniqueCodes(frame.codes),
                };
                File.WriteAllText(Path.Combine(directory, tag + "_snapshot.json"),
                    SelectionJson.Serialize(json));
                return true;
            }
            catch (Exception exception)
            {
                error = "save frame failed: " + exception.Message;
                return false;
            }
        }

        sealed class FrameJson
        {
            public string tag;
            public PickCameraSnapshot snapshot;
            public int width;
            public int height;
            public int center_code;
            public float center_depth01;
            public string codes_sha256;
            public string rgb_sha256;
            public int[] unique_codes;
        }

        static int[] CountUniqueCodes(int[] codes)
        {
            var set = new HashSet<int>();
            foreach (int code in codes) if (code != 0) set.Add(code);
            var list = new List<int>(set);
            list.Sort();
            return list.ToArray();
        }

        public static Color32 CodePalette(int code)
        {
            float hue = Mathf.Repeat(code * 0.61803398875f, 1f);
            var color = Color.HSVToRGB(hue, 0.72f, 0.92f);
            return new Color32((byte)(color.r * 255f), (byte)(color.g * 255f), (byte)(color.b * 255f), 255);
        }

        public static Vector2 ProjectWorld(PickCameraSnapshot snapshot, Vector3 world, out bool inFront)
        {
            inFront = false;
            if (snapshot == null) return new Vector2(-1f, -1f);
            // Single canonical conversion: PickCameraSnapshot.ProjectWorldToViewport
            // already returns TOP-origin shared UVs (row 0 = top), the same spaces
            // used by ViewportRay, ResolveUv, ResolveBox and frame.CodeAtUv/DepthAtUv.
            Vector3 uv = snapshot.ProjectWorldToViewport(world, out _);
            if (uv.x < 0f && uv.y < 0f) return new Vector2(-1f, -1f);
            inFront = true;
            return new Vector2(uv.x, uv.y);
        }

        public static string Sha256(byte[] bytes)
        {
            if (bytes == null) return "";
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) builder.Append(value.ToString("x2"));
                return builder.ToString();
            }
        }
    }
}

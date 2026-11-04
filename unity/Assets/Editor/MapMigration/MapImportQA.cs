// Assets/Editor/MapMigration/MapImportQA.cs
//
// Stage 2 of the SovietBlueHour Unity import QA:
//   * material / texture audit of the imported GLB sub-assets (URP Lit, base map, emission)
//   * minimal safe in-place fixes (null shader / missing _EMISSION keyword)
//   * 5 editor-mode renders (1280x720) written to Logs/map_import/*.png
//   * machine readable QA json
//
// Batch entry point: MapImportQA.RunStage2
// Menu             : UnityLITF/地图/② 渲染导入 QA 截图
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

public static class MapImportQA
{
    const string GlbPath = "Assets/Maps/SovietBlueHour/SovietBlueHour_game.glb";
    const string TestScenePath = "Assets/Scenes/_SovietBlueHourImportTest.unity";
    const string LogFolder = "Logs/map_import";
    const string AuditPath = LogFolder + "/material_audit.json";
    const string RenderQaPath = LogFolder + "/render_qa.json";
    const int W = 1280;
    const int H = 720;

    // ------------------------------------------------------------------- types
    [Serializable]
    public class StrInt
    {
        public string name;
        public int count;
    }

    [Serializable]
    public class StrStr
    {
        public string name;
        public string value;
    }

    [Serializable]
    public class EmissionInfo
    {
        public string name;
        public float r;
        public float g;
        public float b;
        public float max;
        public bool keyword;
        public bool hasMap;
        public bool hasBaseMap;
    }

    [Serializable]
    public class MaterialAudit
    {
        public string schema = "sbh.unity_material_audit.v1";
        public string generatedUtc = "";
        public string glbPath = "";
        public int materialCount;
        public int urpLitCount;
        public int nullShaderCount;
        public int nonUrpShaderCount;
        public int baseMapCount;
        public int emissionKeywordCount;
        public int emissionColorPositiveCount;
        public int emissionMapCount;
        public float emissionColorMaxComponent;
        public int transparentCount;
        public int doubleSidedCount;
        public int textureCount;
        public int brokenTextureCount;
        public int emissionKeywordFixed;
        public int shaderAssignedFixed;
        public StrInt[] shaderHistogram = new StrInt[0];
        public StrStr[] nonUrpShaders = new StrStr[0];
        public StrStr[] nullShaderMaterials = new StrStr[0];
        public EmissionInfo[] emissiveMaterials = new EmissionInfo[0];
        public StrInt[] textureSizes = new StrInt[0];
    }

    [Serializable]
    public class ShotDef
    {
        public string name;
        public Vector3 pos;
        public Vector3 target;
        public float vfov;
        public string reference;
    }

    [Serializable]
    public class ShotResult
    {
        public string name;
        public string pngPath;
        public int bytes;
        public bool rendered;
        public float meanLuma;
        public float blackFraction;
        public float pinkFraction;
        public float brightFraction;
        public string status;
        public string reference = "";
        public string cameraPos = "";
        public string cameraTarget = "";
        public float vfov;
    }

    [Serializable]
    public class RenderQa
    {
        public string schema = "sbh.unity_render_qa.v1";
        public string generatedUtc = "";
        public string scenePath = "";
        public int width;
        public int height;
        public int shotCount;
        public int okCount;
        public bool postProcessingEnabled;
        public ShotResult[] shots = new ShotResult[0];
        public string notes = "";
    }

    // Camera definitions.  Unity position = (blender.x, blender.z, blender.y) because the
    // UniGLTF importer uses reverseAxis=Z (Unity = gltf.x, gltf.y, -gltf.z) and the Blender
    // glTF exporter writes gltf = (bx, bz, -by)  =>  Unity = (bx, bz, by).
    // The *matching* shots mirror the authored Blender cameras of Maps/SovietBlueHour
    // (vertical fov derived from lens with a 36 mm sensor and 16:9 aspect).
    static readonly ShotDef[] Shots = new[]
    {
        new ShotDef { name = "qa_01_hero_aerial", pos = new Vector3(150f, 120f, 180f), target = new Vector3(0f, 10f, 0f), vfov = 35f, reference = "brief: aerial 3/4 overview" },
        new ShotDef { name = "qa_02_street_level", pos = new Vector3(-100f, 2.5f, -60f), target = new Vector3(0f, 3f, 0f), vfov = 60f, reference = "brief: street level" },
        new ShotDef { name = "qa_03_night_detail", pos = new Vector3(0f, 2.2f, 40f), target = new Vector3(0f, 3f, -40f), vfov = 55f, reference = "brief: night detail" },
        new ShotDef { name = "qa_04_hero_camhero", pos = new Vector3(205f, 85f, -195f), target = new Vector3(0f, 12f, 10f), vfov = 29.85f, reference = "Blender CAM_HERO (205,-195,85)->(0,10,12) lens38" },
        new ShotDef { name = "qa_05_street_camave", pos = new Vector3(-52f, 1.75f, -71.5f), target = new Vector3(30f, 3.5f, -80f), vfov = 39.75f, reference = "Blender CAM_AVE (-52,-71.5,1.75)->(30,-80,3.5) lens28" },
    };

    static void Log(string m)
    {
        Debug.Log("[SOVIET_QA] " + m);
    }

    static void LogErr(string m)
    {
        Debug.LogError("[SOVIET_QA] " + m);
    }

    static void EnsureLogFolder()
    {
        Directory.CreateDirectory(Path.GetFullPath(LogFolder));
    }

    // ------------------------------------------------------------ batch entries
    [MenuItem("UnityLITF/地图/② 渲染导入 QA 截图（1280x720 x5）")]
    public static void RenderMenu()
    {
        var audit = AuditMaterials();
        var shots = RenderAllShots();
        WriteRenderQa(shots);
        Log("interactive render done: " + shots.Count(s => s.rendered) + "/" + shots.Length);
    }

    [MenuItem("UnityLITF/地图/③ 审计导入材质（只读）")]
    public static void AuditMenu()
    {
        var audit = AuditMaterials();
        Log("audit done: materials=" + audit.materialCount + " urpLit=" + audit.urpLitCount +
            " emissionKw=" + audit.emissionKeywordCount);
    }

    public static void RunStage2()
    {
        var sw = Stopwatch.StartNew();
        int code = 0;
        try
        {
            EnsureLogFolder();
            Log("STAGE2_BEGIN t=" + EditorApplication.timeSinceStartup.ToString("F1") + "s batch=" + Application.isBatchMode);
            var audit = AuditMaterials();
            var shots = RenderAllShots();
            WriteRenderQa(shots);
            int ok = shots.Count(s => s.status == "ok");
            Log("shots ok=" + ok + "/" + shots.Length);
            if (ok == 0) code = 1;
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            code = 1;
        }
        Log("STAGE2_END exit=" + code + " total=" + sw.Elapsed.TotalSeconds.ToString("F1") + "s");
        AssetDatabase.SaveAssets();
        if (Application.isBatchMode) EditorApplication.Exit(code);
    }

    // ------------------------------------------------------------ material audit
    static MaterialAudit AuditMaterials()
    {
        var audit = new MaterialAudit();
        audit.generatedUtc = DateTime.UtcNow.ToString("o");
        audit.glbPath = GlbPath;

        var all = AssetDatabase.LoadAllAssetsAtPath(GlbPath);
        if (all == null || all.Length == 0)
        {
            LogErr("no sub assets at " + GlbPath);
            return audit;
        }

        var materials = new List<Material>();
        var textures = new List<Texture2D>();
        foreach (var o in all)
        {
            var m = o as Material;
            if (m != null) { materials.Add(m); continue; }
            var t = o as Texture2D;
            if (t != null) textures.Add(t);
        }

        var shaderCounts = new Dictionary<string, int>();
        var nonUrp = new List<StrStr>();
        var nullShaders = new List<StrStr>();
        var emissions = new List<EmissionInfo>();
        int urpLit = 0, baseMap = 0, emisKw = 0, emisColor = 0, emisMap = 0, transparent = 0, doubleSided = 0;
        float emisMax = 0f;
        int fixedEmisKw = 0, fixedShader = 0;
        var urpLitShader = Shader.Find("Universal Render Pipeline/Lit");

        foreach (var mt in materials)
        {
            if (mt == null) continue;
            var sh = mt.shader;
            string shName = sh == null ? "<null>" : sh.name;
            if (sh == null) nullShaders.Add(new StrStr { name = mt.name, value = "<null>" });
            if (shName == "Universal Render Pipeline/Lit") urpLit++;

            int c;
            shaderCounts.TryGetValue(shName, out c);
            shaderCounts[shName] = c + 1;

            if (mt.HasProperty("_BaseMap") && mt.GetTexture("_BaseMap") != null) baseMap++;
            bool kw = mt.IsKeywordEnabled("_EMISSION");
            if (kw) emisKw++;
            bool hasBaseMap = mt.HasProperty("_BaseMap") && mt.GetTexture("_BaseMap") != null;
            bool hasEmisMap = mt.HasProperty("_EmissionMap") && mt.GetTexture("_EmissionMap") != null;
            if (hasEmisMap) emisMap++;
            float mx = 0f;
            if (mt.HasProperty("_EmissionColor"))
            {
                Color ec = mt.GetColor("_EmissionColor");
                mx = ec.maxColorComponent;
                if (mx > 0f) emisColor++;
                if (mx > emisMax) emisMax = mx;
                if (mx > 0f || hasEmisMap)
                {
                    emissions.Add(new EmissionInfo
                    {
                        name = mt.name, r = ec.r, g = ec.g, b = ec.b, max = mx,
                        keyword = kw, hasMap = hasEmisMap, hasBaseMap = hasBaseMap
                    });
                    if (!kw)
                    {
                        // safe fix: URP Lit uses the _EMISSION shader feature keyword
                        mt.EnableKeyword("_EMISSION");
                        mt.globalIlluminationFlags &= ~MaterialGlobalIlluminationFlags.EmissiveIsBlack;
                        EditorUtility.SetDirty(mt);
                        fixedEmisKw++;
                    }
                }
            }
            if (mt.renderQueue >= 3000) transparent++;
            if (mt.HasProperty("_Cull") && mt.GetFloat("_Cull") < 0.5f) doubleSided++;

            if (sh == null)
            {
                if (urpLitShader != null)
                {
                    mt.shader = urpLitShader;
                    EditorUtility.SetDirty(mt);
                    fixedShader++;
                }
            }
            else if (shName != "Universal Render Pipeline/Lit" &&
                     shName != "Universal Render Pipeline/Unlit" &&
                     shName != "Universal Render Pipeline/Simple Lit" &&
                     shName != "Universal Render Pipeline/Complex Lit")
            {
                nonUrp.Add(new StrStr { name = mt.name, value = shName });
            }
        }

        if (fixedEmisKw > 0 || fixedShader > 0) AssetDatabase.SaveAssets();

        int brokenTex = 0;
        var texSizes = new Dictionary<string, int>();
        foreach (var t in textures)
        {
            if (t == null) continue;
            if (t.width <= 0 || t.height <= 0) { brokenTex++; continue; }
            string key = t.width + "x" + t.height;
            int c;
            texSizes.TryGetValue(key, out c);
            texSizes[key] = c + 1;
        }

        audit.materialCount = materials.Count;
        audit.urpLitCount = urpLit;
        audit.nullShaderCount = nullShaders.Count;
        audit.nonUrpShaderCount = nonUrp.Count;
        audit.baseMapCount = baseMap;
        audit.emissionKeywordCount = emisKw;
        audit.emissionColorPositiveCount = emisColor;
        audit.emissionMapCount = emisMap;
        audit.emissionColorMaxComponent = emisMax;
        audit.transparentCount = transparent;
        audit.doubleSidedCount = doubleSided;
        audit.textureCount = textures.Count;
        audit.brokenTextureCount = brokenTex;
        audit.emissionKeywordFixed = fixedEmisKw;
        audit.shaderAssignedFixed = fixedShader;
        audit.shaderHistogram = shaderCounts.OrderByDescending(kv => kv.Value)
            .Select(kv => new StrInt { name = kv.Key, count = kv.Value }).ToArray();
        audit.nonUrpShaders = nonUrp.ToArray();
        audit.nullShaderMaterials = nullShaders.ToArray();
        audit.emissiveMaterials = emissions.OrderByDescending(e => e.max).Take(40).ToArray();
        audit.textureSizes = texSizes.OrderByDescending(kv => kv.Value)
            .Select(kv => new StrInt { name = kv.Key, count = kv.Value }).ToArray();

        File.WriteAllText(Path.GetFullPath(AuditPath), JsonUtility.ToJson(audit, true));
        Log("material audit -> " + Path.GetFullPath(AuditPath));
        Log("materials=" + materials.Count + " urpLit=" + urpLit + " nullShader=" + nullShaders.Count +
            " nonUrp=" + nonUrp.Count + " baseMap=" + baseMap + " emissionKw=" + emisKw +
            " emissionColor>0=" + emisColor + " emissionMap=" + emisMap +
            " emissiveMax=" + emisMax.ToString("F2") + " textures=" + textures.Count +
            " brokenTex=" + brokenTex + " fixedEmissionKw=" + fixedEmisKw + " fixedShader=" + fixedShader);
        foreach (var kv in shaderCounts) Log("shader[" + kv.Value + "] " + kv.Key);
        return audit;
    }

    // ------------------------------------------------------------------ renders
    static ShotResult[] RenderAllShots()
    {
        var results = new List<ShotResult>();
        if (!File.Exists(TestScenePath))
        {
            LogErr("test scene missing: " + TestScenePath + " (run ImportSovietBlueHour stage 1 first)");
            return results.ToArray();
        }
        var scene = EditorSceneManager.OpenScene(TestScenePath, OpenSceneMode.Single);
        Log("opened scene " + scene.path + " isLoaded=" + scene.isLoaded);

        Camera cam = Object.FindObjectOfType<Camera>();
        if (cam == null)
        {
            var camGo = new GameObject("QA_Camera_Runtime");
            cam = camGo.AddComponent<Camera>();
            Log("no camera in scene -> created temporary one");
        }
        var camData = cam.GetComponent<UniversalAdditionalCameraData>();
        if (camData == null) camData = cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
        camData.renderPostProcessing = true;
        camData.renderShadows = true;
        cam.cameraType = CameraType.Game;
        cam.enabled = true;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.nearClipPlane = 0.3f;
        cam.farClipPlane = 4000f;
        cam.allowHDR = true;
        cam.allowMSAA = false;

        Volume volume = Object.FindObjectOfType<Volume>();
        Log("post volume found=" + (volume != null) + (volume != null ? (" profile=" + (volume.profile != null ? volume.profile.name : "<null>")) : "") +
            " renderPostProcessing=" + camData.renderPostProcessing);

        int litShadows = 0;
        foreach (var l in Object.FindObjectsOfType<Light>()) if (l.type == LightType.Directional && l.shadows != LightShadows.None) litShadows++;
        Log("directional lights with shadows=" + litShadows);

        // Batchmode compiles missing shader variants asynchronously: the first render of a variant
        // that was never used before can sample the InternalErrorShader (magenta).  Warm every
        // material up with async compilation disabled, and render each shot twice (2nd kept).
        WarmupShaders(cam);
        bool prevAsyncCompilation = ShaderUtil.allowAsyncCompilation;
        ShaderUtil.allowAsyncCompilation = false;
        Log("ShaderUtil.allowAsyncCompilation=false for the shot loop");

        foreach (var shot in Shots)
        {
            var r = new ShotResult
            {
                name = shot.name,
                pngPath = LogFolder + "/" + shot.name + ".png",
                reference = shot.reference,
                cameraPos = shot.pos.ToString("F2"),
                cameraTarget = shot.target.ToString("F2"),
                vfov = shot.vfov,
            };
            try
            {
                cam.transform.position = shot.pos;
                var dir = shot.target - shot.pos;
                cam.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
                cam.fieldOfView = shot.vfov;
                cam.aspect = W / (float)H;

                var pngPath = Path.GetFullPath(r.pngPath);
                // throw-away first pass: forces any still-missing shader variants to compile
                RenderCameraToPng(cam, out _, out _, out _, out _);
                byte[] png = RenderCameraToPng(cam, out r.meanLuma, out r.blackFraction, out r.pinkFraction, out r.brightFraction);
                if (png != null && png.Length > 0)
                {
                    File.WriteAllBytes(pngPath, png);
                    r.bytes = png.Length;
                    r.rendered = true;
                }
                r.status = (!r.rendered || r.bytes < 2000) ? "empty"
                    : (r.pinkFraction > 0.15f ? "pink"
                    : (r.blackFraction > 0.98f ? "black"
                    : (r.meanLuma < 0.008f ? "too_dark" : "ok")));
                Log("shot " + shot.name + " status=" + r.status + " bytes=" + r.bytes +
                    " meanLuma=" + r.meanLuma.ToString("F4") + " black=" + r.blackFraction.ToString("F3") +
                    " pink=" + r.pinkFraction.ToString("F3") + " bright=" + r.brightFraction.ToString("F4"));
            }
            catch (Exception e)
            {
                r.status = "error";
                LogErr("shot " + shot.name + " failed: " + e);
            }
            results.Add(r);
        }
        ShaderUtil.allowAsyncCompilation = prevAsyncCompilation;
        return results.ToArray();
    }

    static byte[] RenderCameraToPng(Camera cam, out float meanLuma, out float blackFrac, out float pinkFrac, out float brightFrac)
    {
        meanLuma = 0f; blackFrac = 1f; pinkFrac = 0f; brightFrac = 0f;
        RenderTexture rtHdr = null;
        RenderTexture rtLdr = null;
        RenderTexture prevActive = RenderTexture.active;
        RenderTexture prevTarget = cam.targetTexture;
        Texture2D tex = null;
        try
        {
            rtHdr = RenderTexture.GetTemporary(W, H, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            rtLdr = RenderTexture.GetTemporary(W, H, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            cam.targetTexture = rtHdr;
            cam.Render();
            Graphics.Blit(rtHdr, rtLdr);
            RenderTexture.active = rtLdr;
            tex = new Texture2D(W, H, TextureFormat.RGBA32, false, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0, false);
            tex.Apply(false, false);
            AnalyzePixels(tex, out meanLuma, out blackFrac, out pinkFrac, out brightFrac);
            return tex.EncodeToPNG();
        }
        catch (Exception e)
        {
            LogErr("RenderCameraToPng HDR path failed: " + e);
        }
        finally
        {
            cam.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
            if (tex != null) Object.DestroyImmediate(tex);
            if (rtHdr != null) RenderTexture.ReleaseTemporary(rtHdr);
            if (rtLdr != null) RenderTexture.ReleaseTemporary(rtLdr);
        }

        // Fallback: render straight into an LDR sRGB target (no HDR bloom, but proves geometry/materials).
        try
        {
            var rt = RenderTexture.GetTemporary(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var t2 = new Texture2D(W, H, TextureFormat.RGBA32, false, false);
            t2.ReadPixels(new Rect(0, 0, W, H), 0, 0, false);
            t2.Apply(false, false);
            AnalyzePixels(t2, out meanLuma, out blackFrac, out pinkFrac, out brightFrac);
            var png = t2.EncodeToPNG();
            Object.DestroyImmediate(t2);
            cam.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(rt);
            Log("used LDR fallback render path");
            return png;
        }
        catch (Exception e)
        {
            LogErr("RenderCameraToPng LDR fallback failed: " + e);
            cam.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
            return null;
        }
    }

    // ------------------------------------------------------------- shader warmup
    /// <summary>
    /// Batchmode compiles missing shader variants asynchronously.  The first draw of a variant that
    /// has never been used yet can sample Hidden/InternalErrorShader (magenta 254,3,254).  This
    /// renders the whole map once with async compilation disabled so every variant is ready.
    /// </summary>
    static void WarmupShaders(Camera cam)
    {
        bool prevAsync = ShaderUtil.allowAsyncCompilation;
        RenderTexture prevTarget = cam.targetTexture;
        Vector3 prevPos = cam.transform.position;
        Quaternion prevRot = cam.transform.rotation;
        float prevFov = cam.fieldOfView;
        RenderTexture rt = null;
        try
        {
            ShaderUtil.allowAsyncCompilation = false;
            rt = RenderTexture.GetTemporary(256, 256, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            cam.targetTexture = rt;
            cam.transform.position = new Vector3(0f, 700f, 0f);
            cam.transform.rotation = Quaternion.LookRotation(new Vector3(0.001f, -1f, 0.002f).normalized, Vector3.up);
            cam.fieldOfView = 90f;
            cam.Render();
            Log("shader warmup render done (top-down 700m, allowAsyncCompilation=false)");
        }
        catch (Exception e)
        {
            LogErr("WarmupShaders failed: " + e);
        }
        finally
        {
            cam.targetTexture = prevTarget;
            cam.transform.position = prevPos;
            cam.transform.rotation = prevRot;
            cam.fieldOfView = prevFov;
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
            ShaderUtil.allowAsyncCompilation = prevAsync;
        }
    }

    // ------------------------------------------------------- magenta diagnosis
    [Serializable]
    public class ShaderSupportEntry
    {
        public string shader;
        public int slotCount;
        public bool isSupported;
        public bool hasError;
        public int messageCount;
        public string[] messages = new string[0];
    }

    [Serializable]
    public class SlotIssue
    {
        public string node;
        public string mesh;
        public int slot;
        public string material;
        public string shader;
        public string reason;
    }

    [Serializable]
    public class ShaderSupport
    {
        public string schema = "sbh.unity_shader_support.v1";
        public string generatedUtc = "";
        public string unityVersion = "";
        public string graphicsDevice = "";
        public string graphicsDeviceType = "";
        public int graphicsShaderLevel;
        public bool allowAsyncCompilationInitial;
        public int rendererCount;
        public int materialSlotCount;
        public int nullSlotCount;
        public int mismatchRendererCount;
        public int nullShaderSlotCount;
        public int unsupportedShaderSlotCount;
        public int shaderErrorSlotCount;
        public int distinctShaderCount;
        public int emissiveRendererCount;
        public string[] emissiveRenderers = new string[0];
        public ShaderSupportEntry[] shaders = new ShaderSupportEntry[0];
        public SlotIssue[] issues = new SlotIssue[0];
        public string notes = "";
    }

    [MenuItem("UnityLITF/地图/⑤ 洋红诊断 + shader 支持 + 重渲（只读）")]
    public static void DiagnosisMenu()
    {
        DiagnosisCore();
    }

    public static void RunDiagnosis()
    {
        var sw = Stopwatch.StartNew();
        int code = 0;
        try
        {
            if (!DiagnosisCore()) code = 1;
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            code = 1;
        }
        Log("DIAG_END exit=" + code + " total=" + sw.Elapsed.TotalSeconds.ToString("F1") + "s");
        if (Application.isBatchMode) EditorApplication.Exit(code);
    }

    static bool DiagnosisCore()
    {
        EnsureLogFolder();
        Log("DIAG_BEGIN t=" + EditorApplication.timeSinceStartup.ToString("F1") + "s batch=" + Application.isBatchMode +
            " gpu=" + SystemInfo.graphicsDeviceName + " api=" + SystemInfo.graphicsDeviceType +
            " shaderLevel=" + SystemInfo.graphicsShaderLevel);
        // 0) geometry probe -> Logs/map_import/geometry_probe.json
        try
        {
            MapGeometryProbe.RunCore();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
        // 1) shader / material slot diagnosis -> shader_support.json
        DiagnoseShaderSupport();
        // 2) re-render every shot with shader warmup -> qa_*.png + render_qa.json
        var shots = RenderAllShots();
        WriteRenderQa(shots);
        // 3) control render: same hero-aerial camera with every _EMISSION keyword disabled
        EmissionOffControlRender();
        int ok = shots.Count(s => s.status == "ok");
        Log("DIAG_SHOTS ok=" + ok + "/" + shots.Length);
        return ok > 0;
    }

    static void DiagnoseShaderSupport()
    {
        var sup = new ShaderSupport();
        sup.generatedUtc = DateTime.UtcNow.ToString("o");
        sup.unityVersion = Application.unityVersion;
        sup.graphicsDevice = SystemInfo.graphicsDeviceName;
        sup.graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString();
        sup.graphicsShaderLevel = SystemInfo.graphicsShaderLevel;
        sup.allowAsyncCompilationInitial = ShaderUtil.allowAsyncCompilation;

        if (!File.Exists(TestScenePath))
        {
            LogErr("shader diagnosis: test scene missing");
            File.WriteAllText(Path.GetFullPath(LogFolder + "/shader_support.json"), JsonUtility.ToJson(sup, true));
            return;
        }
        EditorSceneManager.OpenScene(TestScenePath, OpenSceneMode.Single);
        var root = GameObject.Find("SovietBlueHour_Map");
        if (root == null)
        {
            LogErr("shader diagnosis: map root 'SovietBlueHour_Map' not found in test scene");
            File.WriteAllText(Path.GetFullPath(LogFolder + "/shader_support.json"), JsonUtility.ToJson(sup, true));
            return;
        }

        var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
        var shaderUse = new Dictionary<Shader, int>();
        var issues = new List<SlotIssue>();
        var emissiveRenderers = new List<string>();
        int slots = 0, nullSlots = 0, nullShaderSlots = 0, unsupported = 0, shaderErrors = 0, mismatch = 0;

        foreach (var mr in renderers)
        {
            var mf = mr.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            var mats = mr.sharedMaterials;
            int sub = mesh != null ? mesh.subMeshCount : -1;
            if (mesh == null || mats.Length != sub)
            {
                mismatch++;
                issues.Add(new SlotIssue
                {
                    node = mr.transform.name,
                    mesh = mesh != null ? mesh.name : "<null>",
                    slot = -1, material = "-", shader = "-",
                    reason = "materials.Length=" + mats.Length + " != subMeshCount=" + sub
                });
            }
            bool hasEmissive = false;
            for (int i = 0; i < mats.Length; i++)
            {
                slots++;
                var m = mats[i];
                if (m == null)
                {
                    nullSlots++;
                    issues.Add(new SlotIssue { node = mr.transform.name, mesh = mesh != null ? mesh.name : "-", slot = i, material = "<null>", shader = "<null>", reason = "null material slot" });
                    continue;
                }
                var sh = m.shader;
                if (sh == null)
                {
                    nullShaderSlots++;
                    issues.Add(new SlotIssue { node = mr.transform.name, mesh = mesh != null ? mesh.name : "-", slot = i, material = m.name, shader = "<null>", reason = "null shader" });
                    continue;
                }
                int c;
                shaderUse.TryGetValue(sh, out c);
                shaderUse[sh] = c + 1;
                if (!sh.isSupported)
                {
                    unsupported++;
                    issues.Add(new SlotIssue { node = mr.transform.name, mesh = mesh != null ? mesh.name : "-", slot = i, material = m.name, shader = sh.name, reason = "shader.isSupported == false" });
                }
                if (ShaderUtil.ShaderHasError(sh))
                {
                    shaderErrors++;
                    issues.Add(new SlotIssue { node = mr.transform.name, mesh = mesh != null ? mesh.name : "-", slot = i, material = m.name, shader = sh.name, reason = "ShaderUtil.ShaderHasError == true" });
                }
                if (m.IsKeywordEnabled("_EMISSION")) hasEmissive = true;
            }
            if (hasEmissive) emissiveRenderers.Add(mr.transform.name);
        }

        var entries = new List<ShaderSupportEntry>();
        foreach (var kv in shaderUse)
        {
            var sh = kv.Key;
            var e = new ShaderSupportEntry
            {
                shader = sh.name,
                slotCount = kv.Value,
                isSupported = sh.isSupported,
                hasError = ShaderUtil.ShaderHasError(sh),
            };
            var msgs = ShaderUtil.GetShaderMessages(sh);
            e.messageCount = msgs != null ? msgs.Length : 0;
            var list = new List<string>();
            if (msgs != null)
            {
                foreach (var m in msgs)
                {
                    // ShaderMessage is editor internal-ish: dump its fields via reflection
                    var sb = new System.Text.StringBuilder();
                    try
                    {
                        var flds = m.GetType().GetFields();
                        foreach (var f in flds)
                        {
                            sb.Append(f.Name).Append('=').Append(f.GetValue(m)).Append("; ");
                        }
                    }
                    catch (Exception ex)
                    {
                        sb.Append("unreadable:").Append(ex.Message);
                    }
                    list.Add(sb.ToString());
                }
            }
            e.messages = list.ToArray();
            entries.Add(e);
        }

        sup.rendererCount = renderers.Length;
        sup.materialSlotCount = slots;
        sup.nullSlotCount = nullSlots;
        sup.mismatchRendererCount = mismatch;
        sup.nullShaderSlotCount = nullShaderSlots;
        sup.unsupportedShaderSlotCount = unsupported;
        sup.shaderErrorSlotCount = shaderErrors;
        sup.distinctShaderCount = shaderUse.Count;
        sup.emissiveRendererCount = emissiveRenderers.Count;
        sup.emissiveRenderers = emissiveRenderers.ToArray();
        sup.shaders = entries.ToArray();
        sup.issues = issues.ToArray();
        sup.notes = "Slot-level audit of the instantiated prefab in the QA test scene. " +
                    "Magenta (254,3,254) means Hidden/InternalErrorShader, i.e. a shader variant that is not " +
                    "available/compiled at draw time (batchmode async shader compilation), not a missing material slot.";

        File.WriteAllText(Path.GetFullPath(LogFolder + "/shader_support.json"), JsonUtility.ToJson(sup, true));
        Log("shader support -> " + Path.GetFullPath(LogFolder + "/shader_support.json"));
        Log("slots=" + slots + " nullSlots=" + nullSlots + " mismatchRenderers=" + mismatch +
            " nullShaderSlots=" + nullShaderSlots + " unsupported=" + unsupported + " shaderErrors=" + shaderErrors +
            " distinctShaders=" + shaderUse.Count + " emissiveRenderers=" + emissiveRenderers.Count);
        foreach (var e in entries)
        {
            Log("shader '" + e.shader + "' slots=" + e.slotCount + " supported=" + e.isSupported +
                " hasError=" + e.hasError + " messages=" + e.messageCount);
            for (int i = 0; i < e.messages.Length && i < 5; i++) Log("  msg: " + e.messages[i]);
        }
    }

    static void EmissionOffControlRender()
    {
        var all = AssetDatabase.LoadAllAssetsAtPath(GlbPath);
        if (all == null) return;
        var disabled = new List<Material>();
        try
        {
            foreach (var o in all)
            {
                var m = o as Material;
                if (m == null) continue;
                if (m.IsKeywordEnabled("_EMISSION"))
                {
                    m.DisableKeyword("_EMISSION");
                    disabled.Add(m);
                }
            }
            Log("control render: temporarily disabled _EMISSION on " + disabled.Count + " materials (in-memory only)");

            EditorSceneManager.OpenScene(TestScenePath, OpenSceneMode.Single);
            var cam = Object.FindObjectOfType<Camera>();
            if (cam == null) return;
            var shot = Shots[0];
            cam.transform.position = shot.pos;
            cam.transform.rotation = Quaternion.LookRotation((shot.target - shot.pos).normalized, Vector3.up);
            cam.fieldOfView = shot.vfov;
            ShaderUtil.allowAsyncCompilation = false;
            RenderCameraToPng(cam, out _, out _, out _, out _);
            byte[] png = RenderCameraToPng(cam, out float mean, out float black, out float pink, out float bright);
            if (png != null)
            {
                var p = Path.GetFullPath(LogFolder + "/_diag_qa01_emission_off.png");
                File.WriteAllBytes(p, png);
                Log("CONTROL emission-off: meanLuma=" + mean.ToString("F4") + " black=" + black.ToString("F3") +
                    " pink=" + pink.ToString("F4") + " bright=" + bright.ToString("F4") + " -> " + p);
            }
        }
        catch (Exception e)
        {
            LogErr("EmissionOffControlRender failed: " + e);
        }
        finally
        {
            foreach (var m in disabled)
            {
                if (m != null) m.EnableKeyword("_EMISSION");
            }
            Log("restored _EMISSION on " + disabled.Count + " materials (nothing was saved to disk)");
        }
    }

    static void AnalyzePixels(Texture2D tex, out float meanLuma, out float blackFrac, out float pinkFrac, out float brightFrac)
    {
        var px = tex.GetPixels32();
        int n = px.Length;
        double sum = 0;
        int black = 0, pink = 0, bright = 0;
        for (int i = 0; i < n; i++)
        {
            float rf = px[i].r / 255f, gf = px[i].g / 255f, bf = px[i].b / 255f;
            float luma = 0.2126f * rf + 0.7152f * gf + 0.0722f * bf;
            sum += luma;
            if (luma < 0.02f) black++;
            if (rf > 0.65f && bf > 0.65f && gf < 0.40f && (rf - gf) > 0.22f) pink++;
            if (luma > 0.55f) bright++;
        }
        meanLuma = (float)(sum / Mathf.Max(1, n));
        blackFrac = black / (float)Mathf.Max(1, n);
        pinkFrac = pink / (float)Mathf.Max(1, n);
        brightFrac = bright / (float)Mathf.Max(1, n);
    }

    static void WriteRenderQa(ShotResult[] shots)
    {
        var qa = new RenderQa();
        qa.generatedUtc = DateTime.UtcNow.ToString("o");
        qa.scenePath = TestScenePath;
        qa.width = W;
        qa.height = H;
        qa.shotCount = shots.Length;
        qa.okCount = shots.Count(s => s.status == "ok");
        qa.postProcessingEnabled = true;
        qa.shots = shots;
        qa.notes = "Editor-mode Camera.Render -> HDR RT -> sRGB blit -> PNG. URP post-processing (Bloom/Neutral tonemap/" +
                   "ColorAdjustments/Vignette) enabled via global volume. Fog + flat blue ambient + one directional moon light.";
        File.WriteAllText(Path.GetFullPath(RenderQaPath), JsonUtility.ToJson(qa, true));
        Log("render qa -> " + Path.GetFullPath(RenderQaPath) + " ok=" + qa.okCount + "/" + qa.shotCount);
    }
}

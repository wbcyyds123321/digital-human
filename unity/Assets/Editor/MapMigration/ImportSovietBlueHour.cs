// Assets/Editor/MapMigration/ImportSovietBlueHour.cs
//
// SovietBlueHour (117 MB GLB, 51 nodes / 2047 primitives / 571 materials / 1.4M tris)
// -> Unity 2022.3 URP import pipeline for the "Become human" project.
//
// Uses the project's UniGLTF (com.vrmc.gltf) ScriptedImporter.  The importer settings are
// touched through SerializedObject (m_reverseAxis / m_renderPipeline) so that this editor
// assembly does NOT need a compile-time reference to UniGLTF.Editor (that asmdef is
// autoReferenced=false and must not be changed from here).
//
// Batch entry point: ImportSovietBlueHour.RunStage1
// Menus            : UnityLITF/地图/...
//
// Stage 1 does: importer audit/fix -> (re)import -> stats json -> reusable prefab -> QA test scene.
// Pixel QA rendering lives in MapImportQA.cs (stage 2).
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

public static class ImportSovietBlueHour
{
    // ------------------------------------------------------------------ paths
    public const string MapFolder = "Assets/Maps/SovietBlueHour";
    public const string GlbPath = MapFolder + "/SovietBlueHour_game.glb";
    public const string PrefabPath = MapFolder + "/SovietBlueHour_Map.prefab";
    public const string PostFxPath = MapFolder + "/SovietBlueHour_QAPost.asset";
    public const string TestScenePath = "Assets/Scenes/_SovietBlueHourImportTest.unity";
    public const string MapRootName = "SovietBlueHour_Map";
    public const string LogFolder = "Logs/map_import";
    public const string StatsPath = LogFolder + "/import_stats.json";

    // ScriptedImporterAxes.Z == 1, ImporterRenderPipelineTypes.UniversalRenderPipeline == 2
    const int AxisZ = 1;
    const int PipelineUniversal = 2;

    public static bool RunStage1ForceReimport = false;

    // ------------------------------------------------------------- data types
    [Serializable]
    public class StrInt
    {
        public string name;
        public int count;
    }

    [Serializable]
    public class NodeStat
    {
        public string name;
        public int subMeshes;
        public int triangles;
        public int vertices;
    }

    [Serializable]
    public class ImportStats
    {
        public string schema = "sbh.unity_import.v1";
        public string generatedUtc = "";
        public string unityVersion = "";
        public string glbPath = "";
        public int glbBytes;
        public string importerReverseAxis = "";
        public string importerRenderPipeline = "";
        public bool reimportPerformed;
        public float reimportSeconds;
        public float scriptStartTimeSinceStartup;
        public string importedRootName = "";
        public int rootChildCount;
        public string[] rootChildNames = new string[0];
        public int subAssetCount;
        public int rendererCount;
        public int meshFilterCount;
        public int uniqueMeshCount;
        public int subMeshCount;
        public int triangleCount;
        public int vertexCount;
        public int materialCount;
        public int textureCount;
        public int uniqueShaderCount;
        public int nullShaderMaterials;
        public int materialsUrpLit;
        public int materialsWithBaseMap;
        public int materialsEmissionKeyword;
        public int materialsEmissionColorPositive;
        public int materialsEmissionMap;
        public float emissionColorMaxComponent;
        public int transparentMaterials;
        public int doubleSidedMaterials;
        public StrInt[] shaderHistogram = new StrInt[0];
        public NodeStat[] nodes = new NodeStat[0];
        public float boundsCenterX;
        public float boundsCenterY;
        public float boundsCenterZ;
        public float boundsSizeX;
        public float boundsSizeY;
        public float boundsSizeZ;
        public string prefabPath = "";
        public bool prefabCreated;
        public int prefabRendererCount;
        public int prefabSubMeshCount;
        public int prefabTriangleCount;
        public string scenePath = "";
        public bool sceneCreated;
        public float stage1Seconds;
        public string notes = "";
    }

    // ------------------------------------------------------------------ utils
    static void Log(string msg)
    {
        Debug.Log("[SOVIET_IMPORT] " + msg);
    }

    static void LogErr(string msg)
    {
        Debug.LogError("[SOVIET_IMPORT] " + msg);
    }

    static void EnsureLogFolder()
    {
        Directory.CreateDirectory(Path.GetFullPath(LogFolder));
    }

    static string JsonPath(string rel)
    {
        return Path.GetFullPath(rel);
    }

    static float SrgbCh(float c)
    {
        return QualitySettings.activeColorSpace == ColorSpace.Linear ? Mathf.GammaToLinearSpace(c) : c;
    }

    static Color Srgb(int r, int g, int b)
    {
        return new Color(SrgbCh(r / 255f), SrgbCh(g / 255f), SrgbCh(b / 255f), 1f);
    }

    static Color SrgbScaled(int r, int g, int b, float s)
    {
        Color c = Srgb(r, g, b);
        return new Color(c.r * s, c.g * s, c.b * s, 1f);
    }

    // ------------------------------------------------- SerializedObject helpers
    static int GetIntProp(Object obj, string prop, int fallback)
    {
        if (obj == null) return fallback;
        var so = new SerializedObject(obj);
        var p = so.FindProperty(prop);
        if (p == null) return fallback;
        return p.intValue;
    }

    static bool SetIntProp(Object obj, string prop, int value)
    {
        if (obj == null) return false;
        var so = new SerializedObject(obj);
        var p = so.FindProperty(prop);
        if (p == null)
        {
            LogErr("serialized property '" + prop + "' not found on " + obj.GetType().Name);
            return false;
        }
        if (p.propertyType == SerializedPropertyType.Enum) p.enumValueIndex = value;
        else p.intValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
        return true;
    }

    // ------------------------------------------------------------ batch entries
    [MenuItem("UnityLITF/地图/① 导入 SovietBlueHour（GLB → URP）+ 建 prefab + 测试场景")]
    public static void ImportMenu()
    {
        bool ok = Stage1Core(false);
        Log("STAGE1(interactive) ok=" + ok);
    }

    public static void RunStage1()
    {
        var sw = Stopwatch.StartNew();
        int code = 0;
        try
        {
            if (!Stage1Core(RunStage1ForceReimport)) code = 1;
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            code = 1;
        }
        Log("STAGE1_END exit=" + code + " total=" + sw.Elapsed.TotalSeconds.ToString("F1") + "s");
        AssetDatabase.SaveAssets();
        if (Application.isBatchMode) EditorApplication.Exit(code);
    }

    // ----------------------------------------------------------------- stage 1
    static bool Stage1Core(bool forceReimport)
    {
        EnsureLogFolder();
        var swAll = Stopwatch.StartNew();
        Log("STAGE1_BEGIN t=" + EditorApplication.timeSinceStartup.ToString("F1") + "s batch=" + Application.isBatchMode +
            " unity=" + Application.unityVersion + " colorspace=" + QualitySettings.activeColorSpace);

        if (!File.Exists(GlbPath))
        {
            LogErr("GLB not found: " + GlbPath);
            return false;
        }

        var stats = new ImportStats();
        stats.generatedUtc = DateTime.UtcNow.ToString("o");
        stats.unityVersion = Application.unityVersion;
        stats.glbPath = GlbPath;
        stats.glbBytes = (int)new FileInfo(GlbPath).Length;
        stats.scriptStartTimeSinceStartup = (float)EditorApplication.timeSinceStartup;

        // --- 1. importer settings (SerializedObject, no UniGLTF.Editor reference needed)
        var importer = AssetImporter.GetAtPath(GlbPath);
        if (importer == null)
        {
            Log("no importer yet -> AssetDatabase.ImportAsset(ForceSynchronous)");
            AssetDatabase.ImportAsset(GlbPath, ImportAssetOptions.ForceSynchronousImport);
            importer = AssetImporter.GetAtPath(GlbPath);
        }
        if (importer == null)
        {
            LogErr("no AssetImporter for " + GlbPath);
            return false;
        }
        Log("importer type=" + importer.GetType().FullName);

        int axis = GetIntProp(importer, "m_reverseAxis", -1);
        int pipe = GetIntProp(importer, "m_renderPipeline", -1);
        Log("importer settings before: m_reverseAxis=" + axis + " m_renderPipeline=" + pipe);

        bool needReimport = forceReimport || axis != AxisZ || pipe != PipelineUniversal;
        if (needReimport)
        {
            SetIntProp(importer, "m_reverseAxis", AxisZ);
            SetIntProp(importer, "m_renderPipeline", PipelineUniversal);
            Log("REIMPORT_BEGIN " + DateTime.UtcNow.ToString("o"));
            var rsw = Stopwatch.StartNew();
            importer.SaveAndReimport();
            stats.reimportSeconds = (float)rsw.Elapsed.TotalSeconds;
            Log("REIMPORT_END " + stats.reimportSeconds.ToString("F1") + "s");
        }
        else
        {
            Log("importer already configured (Z / UniversalRenderPipeline) - no reimport needed");
        }
        stats.reimportPerformed = needReimport;

        // --- 2. load the imported hierarchy
        var root = AssetDatabase.LoadAssetAtPath<GameObject>(GlbPath);
        if (root == null)
        {
            Log("main asset missing -> forcing synchronous import");
            AssetDatabase.ImportAsset(GlbPath,
                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            root = AssetDatabase.LoadAssetAtPath<GameObject>(GlbPath);
        }
        if (root == null)
        {
            LogErr("could not load main GameObject from " + GlbPath);
            return false;
        }

        axis = GetIntProp(importer, "m_reverseAxis", -1);
        pipe = GetIntProp(importer, "m_renderPipeline", -1);
        stats.importerReverseAxis = axis.ToString();
        stats.importerRenderPipeline = pipe.ToString();
        Log("importer settings after: m_reverseAxis=" + axis + " m_renderPipeline=" + pipe);
        if (axis != AxisZ || pipe != PipelineUniversal)
        {
            LogErr("importer settings NOT applied (axis=" + axis + " pipeline=" + pipe + ")");
        }

        stats.importedRootName = root.name;
        stats.rootChildCount = root.transform.childCount;
        var childNames = new List<string>();
        for (int i = 0; i < root.transform.childCount; i++) childNames.Add(root.transform.GetChild(i).name);
        stats.rootChildNames = childNames.ToArray();
        Log("root='" + root.name + "' children=" + stats.rootChildCount);

        // --- 3. hierarchy / geometry stats
        var all = AssetDatabase.LoadAllAssetsAtPath(GlbPath);
        stats.subAssetCount = all != null ? all.Length : 0;
        var meshes = new HashSet<Mesh>();
        var materials = new HashSet<Material>();
        var textures = new HashSet<Texture>();
        if (all != null)
        {
            foreach (var o in all)
            {
                var m = o as Mesh;
                if (m != null) { meshes.Add(m); continue; }
                var mt = o as Material;
                if (mt != null) { materials.Add(mt); continue; }
                var t = o as Texture;
                if (t != null) { textures.Add(t); continue; }
            }
        }

        int rendererCount = 0, meshFilterCount = 0, subMeshCount = 0, triangles = 0, vertices = 0;
        var nodes = new List<NodeStat>();
        bool hasBounds = false;
        Bounds b = new Bounds(Vector3.zero, Vector3.zero);

        foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
        {
            rendererCount++;
            var mf = mr.GetComponent<MeshFilter>();
            int nSub = 0, nTri = 0, nVert = 0;
            if (mf != null && mf.sharedMesh != null)
            {
                meshFilterCount++;
                var mesh = mf.sharedMesh;
                nSub = mesh.subMeshCount;
                nVert = mesh.vertexCount;
                for (int i = 0; i < nSub; i++) nTri += (int)(mesh.GetIndexCount(i) / 3);
                var mb = mesh.bounds;
                var wb = new Bounds(mr.transform.TransformPoint(mb.center), mb.size);
                if (!hasBounds) { b = wb; hasBounds = true; }
                else b.Encapsulate(wb);
            }
            subMeshCount += nSub;
            triangles += nTri;
            vertices += nVert;
            nodes.Add(new NodeStat { name = mr.transform.name, subMeshes = nSub, triangles = nTri, vertices = nVert });
        }

        stats.rendererCount = rendererCount;
        stats.meshFilterCount = meshFilterCount;
        stats.uniqueMeshCount = meshes.Count;
        stats.subMeshCount = subMeshCount;
        stats.triangleCount = triangles;
        stats.vertexCount = vertices;
        stats.materialCount = materials.Count;
        stats.textureCount = textures.Count;
        stats.nodes = nodes.ToArray();
        if (hasBounds)
        {
            stats.boundsCenterX = b.center.x;
            stats.boundsCenterY = b.center.y;
            stats.boundsCenterZ = b.center.z;
            stats.boundsSizeX = b.size.x;
            stats.boundsSizeY = b.size.y;
            stats.boundsSizeZ = b.size.z;
        }

        // --- 4. material / shader audit
        var shaderCounts = new Dictionary<string, int>();
        int nullShader = 0, urpLit = 0, withBaseMap = 0, emisKw = 0, emisColor = 0, emisMap = 0;
        int transparent = 0, doubleSided = 0;
        float emisMax = 0f;
        foreach (var mt in materials)
        {
            if (mt == null) continue;
            var sh = mt.shader;
            string shName = sh == null ? "<null>" : sh.name;
            if (sh == null) nullShader++;
            else if (shName == "Universal Render Pipeline/Lit") urpLit++;
            int c;
            shaderCounts.TryGetValue(shName, out c);
            shaderCounts[shName] = c + 1;

            if (mt.HasProperty("_BaseMap") && mt.GetTexture("_BaseMap") != null) withBaseMap++;
            if (mt.IsKeywordEnabled("_EMISSION")) emisKw++;
            if (mt.HasProperty("_EmissionColor"))
            {
                Color ec = mt.GetColor("_EmissionColor");
                float mx = ec.maxColorComponent;
                if (mx > 0f) emisColor++;
                if (mx > emisMax) emisMax = mx;
            }
            if (mt.HasProperty("_EmissionMap") && mt.GetTexture("_EmissionMap") != null) emisMap++;
            if (mt.renderQueue >= 3000) transparent++;
            if (mt.HasProperty("_Cull") && mt.GetFloat("_Cull") < 0.5f) doubleSided++;
        }

        stats.uniqueShaderCount = shaderCounts.Count;
        stats.nullShaderMaterials = nullShader;
        stats.materialsUrpLit = urpLit;
        stats.materialsWithBaseMap = withBaseMap;
        stats.materialsEmissionKeyword = emisKw;
        stats.materialsEmissionColorPositive = emisColor;
        stats.materialsEmissionMap = emisMap;
        stats.emissionColorMaxComponent = emisMax;
        stats.transparentMaterials = transparent;
        stats.doubleSidedMaterials = doubleSided;
        stats.shaderHistogram = shaderCounts
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new StrInt { name = kv.Key, count = kv.Value })
            .ToArray();

        Log("geo: renderers=" + rendererCount + " meshes=" + meshes.Count + " subMeshes=" + subMeshCount +
            " tris=" + triangles + " verts=" + vertices + " mats=" + materials.Count + " tex=" + textures.Count);
        Log("mat: urpLit=" + urpLit + " nullShader=" + nullShader + " baseMap=" + withBaseMap +
            " emissionKw=" + emisKw + " emissionColor>0=" + emisColor + " emissionMap=" + emisMap +
            " emissiveMax=" + emisMax.ToString("F2"));
        foreach (var kv in shaderCounts) Log("shader[" + kv.Value + "] " + kv.Key);

        // --- 5. reusable prefab
        GameObject prefabAsset;
        stats.prefabCreated = BuildPrefab(root, out prefabAsset);
        stats.prefabPath = PrefabPath;
        if (prefabAsset != null)
        {
            int pr = 0, psub = 0, ptri = 0;
            foreach (var mr in prefabAsset.GetComponentsInChildren<MeshRenderer>(true))
            {
                pr++;
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var mesh = mf.sharedMesh;
                psub += mesh.subMeshCount;
                for (int i = 0; i < mesh.subMeshCount; i++) ptri += (int)(mesh.GetIndexCount(i) / 3);
            }
            stats.prefabRendererCount = pr;
            stats.prefabSubMeshCount = psub;
            stats.prefabTriangleCount = ptri;
            Log("prefab verify: renderers=" + pr + " subMeshes=" + psub + " tris=" + ptri);
        }
        else
        {
            LogErr("prefab was not created");
        }

        // --- 6. QA test scene (prefab instance + moon light + camera + global volume)
        stats.sceneCreated = BuildTestScene();
        stats.scenePath = TestScenePath;
        Log("test scene created=" + stats.sceneCreated + " -> " + TestScenePath);

        // --- 7. write machine readable report
        stats.stage1Seconds = (float)swAll.Elapsed.TotalSeconds;
        if (string.IsNullOrEmpty(stats.notes))
        {
            stats.notes = "Import via UniGLTF GlbScriptedImporter (reverseAxis=Z, renderPipeline=UniversalRenderPipeline). " +
                          "Axis mapping Unity=(blender.x, blender.z, blender.y).";
        }
        WriteStats(stats);
        Log("stats written -> " + JsonPath(StatsPath));
        Log("STAGE1_OK total=" + swAll.Elapsed.TotalSeconds.ToString("F1") + "s");
        return stats.prefabCreated && rootChildCountOk(stats);
    }

    static bool rootChildCountOk(ImportStats stats)
    {
        // node-1 merged map had 51 static roots; node-2 adds 50 DYN_* dynamic roots
        // (48 vehicles incl. tram + eternal-flame fire) => 101 total.
        return stats.rootChildCount == 51 || stats.rootChildCount == 101;
    }

    static void WriteStats(ImportStats stats)
    {
        EnsureLogFolder();
        string json = JsonUtility.ToJson(stats, true);
        File.WriteAllText(JsonPath(StatsPath), json);
    }

    // ------------------------------------------------------------------ prefab
    static bool BuildPrefab(GameObject sourceRoot, out GameObject prefabAsset)
    {
        prefabAsset = null;
        var inst = Object.Instantiate(sourceRoot);
        inst.name = MapRootName;
        inst.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        inst.transform.localScale = Vector3.one;
        bool ok = false;
        try
        {
            bool savedOk;
            var saved = PrefabUtility.SaveAsPrefabAsset(inst, PrefabPath, out savedOk);
            ok = savedOk && saved != null;
            Log("SaveAsPrefabAsset ok=" + savedOk + " path=" + PrefabPath);
        }
        catch (Exception e)
        {
            LogErr("SaveAsPrefabAsset failed: " + e);
        }
        Object.DestroyImmediate(inst);
        AssetDatabase.SaveAssets();
        prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefabAsset == null)
        {
            LogErr("prefab asset not loadable at " + PrefabPath);
            ok = false;
        }
        else
        {
            Log("prefab loaded: children=" + prefabAsset.transform.childCount + " name=" + prefabAsset.name);
        }
        return ok;
    }

    // -------------------------------------------------------------- test scene
    static bool BuildTestScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // -- render settings: Soviet blue hour approximation (palette from Maps/.../tools/look.py)
        RenderSettings.skybox = null;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = SrgbScaled(0x22, 0x34, 0x4E, 1.35f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = SrgbScaled(0x2C, 0x40, 0x58, 1.6f);
        RenderSettings.fogDensity = 0.0011f;
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
        RenderSettings.reflectionIntensity = 0.35f;

        // -- map prefab instance at identity
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null)
        {
            LogErr("BuildTestScene: prefab missing");
            return false;
        }
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        inst.name = MapRootName;
        inst.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        inst.transform.localScale = Vector3.one;

        // -- moonlight: Blender SUN_MOON rot (64,0,-28) color #9DB8DD energy 2.6
        //    blender travel dir -> unity (x, z, y);  computed offline from the euler angles.
        var moonDir = new Vector3(0.4219f, -0.4384f, 0.7936f).normalized;
        var moonGo = new GameObject("QA_Moon");
        moonGo.transform.rotation = Quaternion.LookRotation(moonDir, Vector3.up);
        var moon = moonGo.AddComponent<Light>();
        moon.type = LightType.Directional;
        moon.color = Srgb(0x9D, 0xB8, 0xDD);
        moon.intensity = 1.15f;
        moon.shadows = LightShadows.Hard;
        moon.shadowStrength = 0.7f;
        moon.shadowBias = 0.05f;
        moon.shadowNormalBias = 0.4f;
        moon.lightmapBakeType = LightmapBakeType.Realtime;

        // -- sky fill (stand-in for the Blender FILL_* area lights / ambient domes)
        var fillGo = new GameObject("QA_SkyFill");
        fillGo.transform.rotation = Quaternion.LookRotation(new Vector3(-0.25f, -1f, -0.35f).normalized, Vector3.up);
        var fill = fillGo.AddComponent<Light>();
        fill.type = LightType.Directional;
        fill.color = Srgb(0x6E, 0x8E, 0xB0);
        fill.intensity = 0.35f;
        fill.shadows = LightShadows.None;
        fill.lightmapBakeType = LightmapBakeType.Realtime;

        // -- camera
        var camGo = new GameObject("QA_Camera");
        var cam = camGo.AddComponent<Camera>();
        cam.cameraType = CameraType.Game;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = SrgbScaled(0x18, 0x2C, 0x4C, 1.15f);
        cam.nearClipPlane = 0.3f;
        cam.farClipPlane = 4000f;
        cam.fieldOfView = 35f;
        cam.allowHDR = true;
        cam.allowMSAA = false;
        camGo.transform.SetPositionAndRotation(new Vector3(150f, 120f, 180f), Quaternion.LookRotation((new Vector3(0f, 10f, 0f) - new Vector3(150f, 120f, 180f)).normalized, Vector3.up));
        var camData = camGo.AddComponent<UniversalAdditionalCameraData>();
        camData.renderPostProcessing = true;
        camData.antialiasing = AntialiasingMode.None;
        camData.renderShadows = true;

        // -- global post volume (bloom / tonemap / grade): mirrors the Blender compositor look
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(PostFxPath);
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, PostFxPath);
            Log("created volume profile asset " + PostFxPath);
        }
        var bloom = GetOrAdd<Bloom>(profile);
        bloom.threshold.Override(0.72f);
        bloom.intensity.Override(0.55f);
        bloom.scatter.Override(0.72f);
        bloom.highQualityFiltering.Override(true);
        var tm = GetOrAdd<Tonemapping>(profile);
        tm.mode.Override(TonemappingMode.Neutral);
        var ca = GetOrAdd<ColorAdjustments>(profile);
        ca.postExposure.Override(0.15f);
        ca.saturation.Override(-16f);
        ca.contrast.Override(6f);
        var vig = GetOrAdd<Vignette>(profile);
        vig.intensity.Override(0.28f);
        vig.smoothness.Override(0.4f);
        EditorUtility.SetDirty(profile);
        AssetDatabase.SaveAssets();

        var volGo = new GameObject("QA_GlobalVolume");
        var vol = volGo.AddComponent<Volume>();
        vol.isGlobal = true;
        vol.priority = 0f;
        vol.profile = profile;

        bool ok = false;
        try
        {
            ok = EditorSceneManager.SaveScene(scene, TestScenePath);
            Log("scene saved=" + ok + " -> " + TestScenePath);
        }
        catch (Exception e)
        {
            LogErr("SaveScene failed: " + e);
        }
        return ok;
    }

    static T GetOrAdd<T>(VolumeProfile profile) where T : VolumeComponent
    {
        foreach (var c in profile.components)
        {
            var typed = c as T;
            if (typed != null)
            {
                typed.active = true;
                return typed;
            }
        }
        var added = profile.Add<T>(true);
        added.active = true;
        return added;
    }
}

// Assets/Editor/MapMigration/MapGeometryProbe.cs
//
// Read-only probe: instantiates the SovietBlueHour_Map prefab, measures the world-space
// bounds (overall + per node) and checks the axis mapping empirically against the Blender
// source coordinates.  Writes Logs/map_import/geometry_probe.json.  Does NOT modify any asset.
//
// Batch entry: MapGeometryProbe.Run
// Menu       : UnityLITF/地图/④ 几何/坐标复核（只读）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

public static class MapGeometryProbe
{
    const string PrefabPath = "Assets/Maps/SovietBlueHour/SovietBlueHour_Map.prefab";
    const string LogFolder = "Logs/map_import";
    const string ProbePath = LogFolder + "/geometry_probe.json";

    [Serializable]
    public class NodeBounds
    {
        public string name;
        public float minX, minY, minZ;
        public float maxX, maxY, maxZ;
        public int subMeshes;
        public int triangles;
        public int materials;
    }

    [Serializable]
    public class ProbeResult
    {
        public string schema = "sbh.unity_geometry_probe.v1";
        public string generatedUtc = "";
        public string prefabPath = "";
        public int childCount;
        public int rendererCount;
        public float worldMinX, worldMinY, worldMinZ;
        public float worldMaxX, worldMaxY, worldMaxZ;
        public float worldSizeX, worldSizeY, worldSizeZ;
        public string worldMinOwner = "";
        public string worldMaxOwner = "";
        public float siteMinY, siteMaxY;
        public NodeBounds[] nodes = new NodeBounds[0];
        public string[] checks = new string[0];
        public string notes = "";
    }

    static void Log(string m)
    {
        Debug.Log("[SOVIET_GEO] " + m);
    }

    [MenuItem("UnityLITF/地图/④ 几何/坐标复核（只读）")]
    public static void ProbeMenu()
    {
        RunCore();
    }

    public static void Run()
    {
        var sw = Stopwatch.StartNew();
        int code = 0;
        try
        {
            if (!RunCore()) code = 1;
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            code = 1;
        }
        Log("PROBE_END exit=" + code + " total=" + sw.Elapsed.TotalSeconds.ToString("F1") + "s");
        if (Application.isBatchMode) EditorApplication.Exit(code);
    }

    public static bool RunCore()
    {
        Directory.CreateDirectory(Path.GetFullPath(LogFolder));
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null)
        {
            Debug.LogError("[SOVIET_GEO] prefab missing " + PrefabPath);
            return false;
        }
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        inst.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        inst.transform.localScale = Vector3.one;

        var r = new ProbeResult();
        r.generatedUtc = DateTime.UtcNow.ToString("o");
        r.prefabPath = PrefabPath;
        r.childCount = inst.transform.childCount;

        var list = new List<NodeBounds>();
        bool has = false;
        Bounds total = default(Bounds);
        float minY = float.MaxValue, maxY = float.MinValue;
        string minOwner = "", maxOwner = "";
        float siteMinY = float.MaxValue, siteMaxY = float.MinValue;
        int rendererCount = 0;

        var renderers = inst.GetComponentsInChildren<MeshRenderer>(true);
        rendererCount = renderers.Length;
        foreach (var mr in renderers)
        {
            var mf = mr.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;
            var mesh = mf.sharedMesh;
            // world bounds of this node (all transforms are identity, but be exact anyway)
            var mb = mesh.bounds;
            var wb = new Bounds(mr.transform.TransformPoint(mb.center), Vector3.zero);
            // transform all 8 corners to be robust against rotation/scale
            Vector3 c = mb.center, e = mb.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = c + new Vector3((i & 1) == 0 ? -e.x : e.x,
                                             (i & 2) == 0 ? -e.y : e.y,
                                             (i & 4) == 0 ? -e.z : e.z);
                wb.Encapsulate(mr.transform.TransformPoint(corner));
            }
            var nb = new NodeBounds
            {
                name = mr.transform.name,
                minX = wb.min.x, minY = wb.min.y, minZ = wb.min.z,
                maxX = wb.max.x, maxY = wb.max.y, maxZ = wb.max.z,
                subMeshes = mesh.subMeshCount,
                triangles = (int)(mesh.GetIndexCount(0) / 3),
                materials = mr.sharedMaterials != null ? mr.sharedMaterials.Length : 0,
            };
            int tri = 0;
            for (int i = 0; i < mesh.subMeshCount; i++) tri += (int)(mesh.GetIndexCount(i) / 3);
            nb.triangles = tri;
            list.Add(nb);

            if (!has) { total = wb; has = true; } else total.Encapsulate(wb);
            if (wb.min.y < minY) { minY = wb.min.y; minOwner = nb.name; }
            if (wb.max.y > maxY) { maxY = wb.max.y; maxOwner = nb.name; }
            if (nb.name == "SITE")
            {
                siteMinY = wb.min.y;
                siteMaxY = wb.max.y;
            }
            Log(nb.name + " min=(" + nb.minX.ToString("F2") + "," + nb.minY.ToString("F2") + "," + nb.minZ.ToString("F2") +
                ") max=(" + nb.maxX.ToString("F2") + "," + nb.maxY.ToString("F2") + "," + nb.maxZ.ToString("F2") + ")" +
                " sub=" + nb.subMeshes + " tri=" + nb.triangles);
        }

        r.rendererCount = rendererCount;
        r.nodes = list.ToArray();
        r.worldMinX = total.min.x; r.worldMinY = total.min.y; r.worldMinZ = total.min.z;
        r.worldMaxX = total.max.x; r.worldMaxY = total.max.y; r.worldMaxZ = total.max.z;
        r.worldSizeX = total.size.x; r.worldSizeY = total.size.y; r.worldSizeZ = total.size.z;
        r.worldMinOwner = minOwner;
        r.worldMaxOwner = maxOwner;
        r.siteMinY = siteMinY;
        r.siteMaxY = siteMaxY;

        // ---- empirical axis-mapping checks against the Blender source data
        var checks = new List<string>();
        checks.Add("Unity position of every prefab child transform: " + DescribeChildTransforms(inst));
        checks.Add(Check(list, "BLD_MOTHERLAND.001", -8f, 8f, 0f, 39f, 137f, 150f,
            "glTF z[-150,-137] -> unity z = -gltf.z = +137..+150, i.e. blender.y=+137..+150 (Unity=(bx,bz,by))"));
        checks.Add(Check(list, "BLD_MSU_MAIN.001", 173f, 199f, 0f, 69f, 35f, 75f,
            "glTF z[-75,-35] -> unity z = +35..+75, i.e. blender.y=+35..+75"));
        checks.Add(Check(list, "BLD_BAKERY.001", -8f, 7f, 0f, 9f, -103f, -92f,
            "glTF z[92,103] -> unity z = -103..-92, i.e. blender.y=-103..-92"));
        checks.Add("SITE node y range: " + siteMinY.ToString("F2") + " .. " + siteMaxY.ToString("F2"));
        checks.Add("World bounds: min=(" + r.worldMinX.ToString("F1") + ", " + r.worldMinY.ToString("F3") + ", " + r.worldMinZ.ToString("F1") +
                   ") max=(" + r.worldMaxX.ToString("F1") + ", " + r.worldMaxY.ToString("F2") + ", " + r.worldMaxZ.ToString("F1") + ")");
        checks.Add("lowest geometry owner=" + minOwner + " minY=" + minY.ToString("F3") +
                   " ; highest geometry owner=" + maxOwner + " maxY=" + maxY.ToString("F3"));
        r.checks = checks.ToArray();
        r.notes = "All prefab children carry identity transforms; the glTF exporter baked world space into the vertices. " +
                  "Thus a node's Transform.position is (0,0,0) and the meaningful location is its mesh bounds center.";

        File.WriteAllText(Path.GetFullPath(ProbePath), JsonUtility.ToJson(r, true));
        Log("geometry probe -> " + Path.GetFullPath(ProbePath));
        foreach (var c in checks) Log("CHECK: " + c);

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        return true;
    }

    static string DescribeChildTransforms(GameObject inst)
    {
        int nonIdentity = 0;
        for (int i = 0; i < inst.transform.childCount; i++)
        {
            var t = inst.transform.GetChild(i);
            if (t.localPosition != Vector3.zero || t.localRotation != Quaternion.identity || t.localScale != Vector3.one) nonIdentity++;
        }
        return inst.transform.childCount + " children, non-identity local transforms = " + nonIdentity;
    }

    static string Check(List<NodeBounds> nodes, string name, float exMinX, float exMaxX,
        float exMinY, float exMaxY, float exMinZ, float exMaxZ, string why)
    {
        var n = nodes.FirstOrDefault(x => x.name == name);
        if (n == null) return name + ": NOT FOUND";
        float tol = 1.0f;
        bool ok = Mathf.Abs(n.minX - exMinX) < tol && Mathf.Abs(n.maxX - exMaxX) < tol &&
                  Mathf.Abs(n.minY - exMinY) < tol && Mathf.Abs(n.maxY - exMaxY) < tol &&
                  Mathf.Abs(n.minZ - exMinZ) < tol && Mathf.Abs(n.maxZ - exMaxZ) < tol;
        return name + ": unityBounds min=(" + n.minX.ToString("F2") + "," + n.minY.ToString("F2") + "," + n.minZ.ToString("F2") +
               ") max=(" + n.maxX.ToString("F2") + "," + n.maxY.ToString("F2") + "," + n.maxZ.ToString("F2") + ")" +
               " expected min=(" + exMinX + "," + exMinY + "," + exMinZ + ") max=(" + exMaxX + "," + exMaxY + "," + exMaxZ + ")" +
               " => " + (ok ? "MATCH" : "MISMATCH") + " (" + why + ")";
    }
}

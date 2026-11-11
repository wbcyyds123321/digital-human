using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Yixuan.Stage2
{
    /// <summary>
    /// Loads the stage-2 pick pack from Resources and validates it before use.
    /// Never touches Physics / colliders / scene objects.
    /// </summary>
    public static class Stage2PickDataLoader
    {
        public const string PackPath = "SovietBlueHour/stage2_pick";
        public const string RuntimeManifestPath = PackPath + "/runtime_manifest";
        public const string FullManifestPath = PackPath + "/manifest";
        public static readonly string[] RequiredSampleIds =
        {
            "entity/prop_bench_plaza_1",
            "entity/prop_bench_plaza_2",
            "entity/veh_laz695_01",
            "entity/bld_w_k3",
        };

        public static Stage2SceneData Load()
        {
            var data = new Stage2SceneData();
            try
            {
                var runtimeAsset = Resources.Load<TextAsset>(RuntimeManifestPath);
                if (runtimeAsset == null)
                {
                    data.Error = "missing resource: " + RuntimeManifestPath;
                    return data;
                }
                string text = runtimeAsset.text;
                var manifest = JsonUtility.FromJson<Stage2Manifest>(text);
                if (manifest == null || string.IsNullOrEmpty(manifest.schema))
                {
                    data.Error = "runtime manifest JSON is empty or invalid";
                    return data;
                }
                if (manifest.schema != "sbh.stage2.pick.runtime.v1")
                {
                    data.Error = "unexpected schema: " + manifest.schema;
                    return data;
                }
                if (string.IsNullOrEmpty(manifest.data_version) || !manifest.data_version.StartsWith("0.1."))
                {
                    data.Error = "unexpected data_version: " + manifest.data_version;
                    return data;
                }
                if (manifest.samples == null || manifest.samples.Length != RequiredSampleIds.Length)
                {
                    data.Error = "expected 4 samples, got " + (manifest.samples == null ? 0 : manifest.samples.Length);
                    return data;
                }
                var idSet = new HashSet<string>(RequiredSampleIds);
                foreach (var s in manifest.samples)
                {
                    if (s == null || string.IsNullOrEmpty(s.id) || !idSet.Contains(s.id))
                    {
                        data.Error = "unexpected sample id: " + (s == null ? "<null>" : s.id);
                        return data;
                    }
                }
                if (manifest.poses == null || manifest.poses.Length < 3)
                {
                    data.Error = "poses missing";
                    return data;
                }
                if (manifest.occluders == null || manifest.occluders.Length < 100)
                {
                    data.Error = "occluder list is unexpectedly small";
                    return data;
                }

                // verify runtime manifest against the full manifest's recorded sha
                var fullAsset = Resources.Load<TextAsset>(FullManifestPath);
                if (fullAsset != null)
                {
                    var head = JsonUtility.FromJson<Stage2FullManifestHead>(fullAsset.text);
                    if (head == null || head.runtime_manifest == null || string.IsNullOrEmpty(head.runtime_manifest.sha256))
                    {
                        data.Error = "full manifest does not record runtime_manifest.sha256";
                        return data;
                    }
                    string runtimeSha = Sha256(Encoding.UTF8.GetBytes(text));
                    if (!string.Equals(runtimeSha, head.runtime_manifest.sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        data.Error = "runtime manifest sha mismatch: " + runtimeSha + " != " + head.runtime_manifest.sha256;
                        return data;
                    }
                }
                else
                {
                    data.Error = "missing resource: " + FullManifestPath;
                    return data;
                }

                // verify collision / ground resources referenced by the pack
                if (!VerifyResourceHash("SovietBlueHour/collision", manifest.map.collision_json_sha256, data)) return data;
                if (!VerifyResourceHash("SovietBlueHour/ground_height", manifest.map.ground_height_json_sha256, data)) return data;

                foreach (var sample in manifest.samples)
                {
                    Stage2SampleMesh mesh;
                    string error;
                    if (!LoadProxy(sample, out mesh, out error))
                    {
                        data.Error = error;
                        return data;
                    }
                    data.Meshes[sample.id] = mesh;
                }

                foreach (var occ in manifest.occluders)
                {
                    if (occ == null || occ.min == null || occ.max == null ||
                        occ.min.Length != 3 || occ.max.Length != 3)
                    {
                        data.Error = "occluder with invalid bounds: " + (occ == null ? "<null>" : occ.id);
                        return data;
                    }
                    var box = new Stage2Box
                    {
                        Id = occ.id,
                        Method = occ.method,
                        ClassOrKind = occ.class_or_kind,
                        Min = new Vector3(occ.min[0], occ.min[1], occ.min[2]),
                        Max = new Vector3(occ.max[0], occ.max[1], occ.max[2]),
                    };
                    if (box.Min.x > box.Max.x || box.Min.y > box.Max.y || box.Min.z > box.Max.z)
                    {
                        data.Error = "occluder has min > max: " + occ.id;
                        return data;
                    }
                    data.Occluders.Add(box);
                }
                if (data.Occluders.Count != manifest.occluders.Length)
                {
                    data.Error = "occluder count mismatch";
                    return data;
                }

                data.Manifest = manifest;
                data.Valid = true;
                data.Error = "";
                return data;
            }
            catch (Exception e)
            {
                data.Error = "load exception: " + e.Message;
                data.Valid = false;
                return data;
            }
        }

        static bool VerifyResourceHash(string resourcePath, string expected, Stage2SceneData data)
        {
            var asset = Resources.Load<TextAsset>(resourcePath);
            if (asset == null)
            {
                data.Error = "missing resource: " + resourcePath;
                return false;
            }
            if (string.IsNullOrEmpty(expected))
            {
                data.Error = "manifest has no hash for " + resourcePath;
                return false;
            }
            string actual = Sha256(asset.bytes);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                data.Error = resourcePath + " sha mismatch: " + actual + " != " + expected;
                return false;
            }
            return true;
        }

        static bool LoadProxy(Stage2Sample sample, out Stage2SampleMesh mesh, out string error)
        {
            mesh = null;
            error = "";
            var info = sample.proxy;
            if (info == null || string.IsNullOrEmpty(info.file))
            {
                error = sample.id + ": proxy info missing";
                return false;
            }
            string resourcePath = PackPath + "/" + Path.GetFileNameWithoutExtension(info.file);
            var asset = Resources.Load<TextAsset>(resourcePath);
            if (asset == null)
            {
                error = sample.id + ": missing proxy resource " + resourcePath;
                return false;
            }
            byte[] bytes = asset.bytes;
            long expectedBytes = (long)info.verts * 12L + (long)info.tris * 12L + (long)info.tris * 4L;
            if (bytes == null || bytes.Length != expectedBytes)
            {
                error = sample.id + ": proxy byte length " + (bytes == null ? 0 : bytes.Length) +
                        " != expected " + expectedBytes;
                return false;
            }
            string sha = Sha256(bytes);
            if (!string.Equals(sha, info.sha256, StringComparison.OrdinalIgnoreCase))
            {
                error = sample.id + ": proxy sha mismatch";
                return false;
            }
            if (info.verts <= 0 || info.tris <= 0)
            {
                error = sample.id + ": empty proxy";
                return false;
            }

            var verts = new float[info.verts * 3];
            var tris = new int[info.tris * 3];
            var triParts = new int[info.tris];
            int offset = 0;
            Buffer.BlockCopy(bytes, offset, verts, 0, info.verts * 12);
            offset += info.verts * 12;
            Buffer.BlockCopy(bytes, offset, tris, 0, info.tris * 12);
            offset += info.tris * 12;
            Buffer.BlockCopy(bytes, offset, triParts, 0, info.tris * 4);

            var vertices = new Vector3[info.verts];
            var bounds = new Bounds();
            bool boundsSet = false;
            for (int i = 0; i < info.verts; i++)
            {
                float x = verts[i * 3];
                float y = verts[i * 3 + 1];
                float z = verts[i * 3 + 2];
                if (float.IsNaN(x) || float.IsInfinity(x) ||
                    float.IsNaN(y) || float.IsInfinity(y) ||
                    float.IsNaN(z) || float.IsInfinity(z))
                {
                    error = sample.id + ": non-finite vertex " + i;
                    return false;
                }
                var v = new Vector3(x, y, z);
                vertices[i] = v;
                if (!boundsSet) { bounds = new Bounds(v, Vector3.zero); boundsSet = true; }
                else bounds.Encapsulate(v);
            }
            for (int i = 0; i < tris.Length; i++)
            {
                if (tris[i] < 0 || tris[i] >= info.verts)
                {
                    error = sample.id + ": triangle index out of range at " + i;
                    return false;
                }
            }
            for (int i = 0; i < triParts.Length; i++)
            {
                if (triParts[i] < 0 || triParts[i] >= (info.parts == null ? 0 : info.parts.Length))
                {
                    error = sample.id + ": part index out of range at " + i;
                    return false;
                }
            }
            if (sample.bbox_unity != null && sample.bbox_unity.Length == 6)
            {
                Vector3 expectedMin = new Vector3(sample.bbox_unity[0], sample.bbox_unity[1], sample.bbox_unity[2]);
                Vector3 expectedMax = new Vector3(sample.bbox_unity[3], sample.bbox_unity[4], sample.bbox_unity[5]);
                float err = Mathf.Max(
                    Mathf.Max(Mathf.Abs(bounds.min.x - expectedMin.x), Mathf.Abs(bounds.max.x - expectedMax.x)),
                    Mathf.Max(Mathf.Max(Mathf.Abs(bounds.min.y - expectedMin.y), Mathf.Abs(bounds.max.y - expectedMax.y)),
                                Mathf.Max(Mathf.Abs(bounds.min.z - expectedMin.z), Mathf.Abs(bounds.max.z - expectedMax.z))));
                if (err > 0.01f)
                {
                    error = sample.id + ": decoded bbox differs from manifest by " + err.ToString("F4") + "m";
                    return false;
                }
            }

            mesh = new Stage2SampleMesh
            {
                Id = sample.id,
                ClassName = sample.entity_class,
                NameZh = sample.name_zh,
                Vertices = vertices,
                Triangles = tris,
                TriangleParts = triParts,
                Parts = info.parts,
                Bounds = bounds,
            };
            return true;
        }

        public static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }

    [Serializable]
    public class Stage2RuntimeRef
    {
        public string file;
        public int bytes;
        public string sha256;
    }

    [Serializable]
    public class Stage2FullManifestHead
    {
        public string schema;
        public string data_version;
        public Stage2RuntimeRef runtime_manifest;
    }
}

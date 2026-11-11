using System;
using System.Collections.Generic;
using UnityEngine;

namespace Yixuan.Stage2
{
    // ------------------------------------------------------------------
    // Runtime manifest (schema sbh.stage2.pick.runtime.v1)
    // Only the fields needed by the prototype are declared; JsonUtility
    // ignores the rest of the JSON object.
    // ------------------------------------------------------------------
    [Serializable]
    public class Stage2Manifest
    {
        public string schema;
        public string data_version;
        public Stage2MapInfo map;
        public Stage2Sample[] samples;
        public Stage2Occluder[] occluders;
        public Stage2OccluderRules occluder_rules;
        public Stage2Pose[] poses;
        public Stage2P0 p0;
        public Stage2Summary summary;
    }

    [Serializable]
    public class Stage2MapInfo
    {
        public string id;
        public string version;
        public string blend_sha256;
        public long blend_size;
        public string entities_json_sha256;
        public string collision_json_sha256;
        public string ground_height_json_sha256;
        public string plan_json_sha256;
    }

    [Serializable]
    public class Stage2Sample
    {
        public string id;
        public string entity_class;
        public string name_zh;
        public float[] center_unity;
        public float[] bbox_unity;
        public Stage2ProxyInfo proxy;
    }

    [Serializable]
    public class Stage2ProxyInfo
    {
        public string file;
        public int verts;
        public int tris;
        public string[] parts;
        public int bytes;
        public string sha256;
        public float[] bbox_unity;
    }

    [Serializable]
    public class Stage2Occluder
    {
        public string id;
        public string method;
        public string class_or_kind;
        public float[] min;
        public float[] max;
    }

    [Serializable]
    public class Stage2OccluderRules
    {
        public string[] excluded_sample_collision_ids;
        public string[] excluded_sample_entity_ids;
        public string[] excluded_classes;
        public float max_horizontal_extent_m;
        public string note;
    }

    [Serializable]
    public class Stage2Pose
    {
        public string id;
        public string label;
        public string mode;                 // follow_eye / test_view
        public float[] eye_position_unity;
        public float[] look_at_unity;
        public float yaw_deg;
        public float pitch_deg;
        public Stage2Target[] targets;
        public string note;
    }

    [Serializable]
    public class Stage2Target
    {
        public string id;
        public string kind;                 // bench / bus_part / empty / bbox_gap / outside / blocked / lintel / gap
        public string sample_id;
        public string part_class;
        public string part_label;
        public string occluder_id;
        public string occluder_method;
        public string expected_status;      // hit / no_target / blocked
        public float[] point;
        public float[] direction;
        public float max_distance;
        public string note;
    }

    [Serializable]
    public class Stage2P0
    {
        public string mode;
        public float[] spawn_root_unity;
        public string note;
    }

    [Serializable]
    public class Stage2Summary
    {
        public int sample_count;
        public int proxy_tris_total;
        public int occluder_count;
        public int entity_occluders;
        public int collision_occluders;
        public int pose_count;
        public int proxy_bytes;
    }

    // ------------------------------------------------------------------
    // Loaded scene data
    // ------------------------------------------------------------------
    public sealed class Stage2SampleMesh
    {
        public string Id;
        public string ClassName;
        public string NameZh;
        public Vector3[] Vertices;
        public int[] Triangles;
        public int[] TriangleParts;
        public string[] Parts;
        public Bounds Bounds;
        public bool Synthetic;

        public string PartOfTriangle(int triangleIndex)
        {
            if (Parts == null || TriangleParts == null || TriangleParts.Length == 0) return "";
            if (triangleIndex < 0 || triangleIndex >= TriangleParts.Length) return "";
            int p = TriangleParts[triangleIndex];
            return (p >= 0 && p < Parts.Length) ? Parts[p] : "";
        }
    }

    public sealed class Stage2SceneData
    {
        public Stage2Manifest Manifest;
        public readonly Dictionary<string, Stage2SampleMesh> Meshes = new Dictionary<string, Stage2SampleMesh>();
        public readonly List<Stage2Box> Occluders = new List<Stage2Box>();
        public bool Valid;
        public string Error = "";
    }

    public sealed class Stage2Box
    {
        public string Id;
        public string Method;
        public string ClassOrKind;
        public Vector3 Min;
        public Vector3 Max;
    }

    // ------------------------------------------------------------------
    // Camera snapshot: frozen before render; render + ray + result share it.
    // ------------------------------------------------------------------
    public sealed class Stage2CameraSnapshot
    {
        public long FrameId;
        public string Source = "unavailable";   // character_eye / test_view / unavailable
        public string PoseId = "";
        public Vector3 Position;
        public Quaternion Rotation;
        public float Fov;
        public float Near;
        public float Far;
        public float Aspect;
        public int RtWidth;
        public int RtHeight;
        public Matrix4x4 Projection;
        public double RealtimeSinceStartup;
        public bool Valid;

        public Vector3 Forward { get { return Rotation * Vector3.forward; } }

        public string SnapshotId { get { return Source + ":" + (string.IsNullOrEmpty(PoseId) ? "-" : PoseId) + ":" + FrameId; } }

        public Stage2SnapshotRecord ToRecord()
        {
            return new Stage2SnapshotRecord
            {
                snapshot_id = SnapshotId,
                frame_id = FrameId,
                source = Source,
                pose_id = PoseId,
                position = Position,
                rotation = Rotation,
                fov = Fov,
                near = Near,
                far = Far,
                aspect = Aspect,
                rt_width = RtWidth,
                rt_height = RtHeight,
                rt_stamp = RealtimeSinceStartup,
                projection = Projection,
                valid = Valid,
            };
        }
    }

    public enum Stage2PickStatus
    {
        Hit,
        NoTarget,
        Blocked,
        Unavailable,
        DataError,
    }

    public sealed class Stage2PickResult
    {
        public Stage2PickStatus Status;
        public string StatusText;
        public string Diagnostic = "";

        // camera / frame provenance
        public long FrameId;
        public string CameraSource;
        public string PoseId;
        public string SnapshotId;

        // ray
        public Vector3 RayOrigin;
        public Vector3 RayDirection;
        public float MaxDistance;
        public float MinDistance;

        // selectable hit
        public string SampleId;
        public string NameZh;
        public string PartLabel;
        public int TriangleIndex = -1;
        public Vector3 HitPoint;
        public float DistanceFromCamera;
        public float DistanceFromCharacter;

        // occlusion
        public string OccluderId;
        public string OccluderMethod;
        public string OccluderClass;
        public float OccluderDistance;
        public string BlockedCandidateId;
        public string BlockedCandidatePart;

        public readonly List<string> Diagnostics = new List<string>();
        public readonly List<string> BroadPhaseSamples = new List<string>();

        public bool HasSelectableHit { get { return Status == Stage2PickStatus.Hit; } }

        public static Stage2PickResult Unavailable(string reason)
        {
            return new Stage2PickResult
            {
                Status = Stage2PickStatus.Unavailable,
                StatusText = "unavailable",
                Diagnostic = reason ?? "",
            };
        }

        public Stage2PickResult Clone()
        {
            var r = (Stage2PickResult)MemberwiseClone();
            return r;
        }

        public string ShortLabel()
        {
            switch (Status)
            {
                case Stage2PickStatus.Hit:
                    return (NameZh ?? "") + "  " + (SampleId ?? "") + "  " + DistanceFromCamera.ToString("F2") + "m";
                case Stage2PickStatus.Blocked:
                    return "被遮挡: " + (OccluderId ?? "?") + " (" + (OccluderMethod ?? "?") + ")";
                case Stage2PickStatus.NoTarget:
                    return string.IsNullOrEmpty(Diagnostic) ? "无目标" : ("无目标: " + Diagnostic);
                case Stage2PickStatus.Unavailable:
                    return "不可用: " + (Diagnostic ?? "");
                case Stage2PickStatus.DataError:
                    return "数据错误: " + (Diagnostic ?? "");
            }
            return Status.ToString();
        }

        public Stage2PickRecord ToRecord()
        {
            return new Stage2PickRecord
            {
                status = Status.ToString(),
                diagnostic = Diagnostic ?? "",
                frame_id = FrameId,
                snapshot_id = SnapshotId,
                camera_source = CameraSource ?? "",
                pose_id = PoseId ?? "",
                origin = RayOrigin,
                direction = RayDirection,
                max_distance = MaxDistance,
                min_distance = MinDistance,
                sample_id = SampleId ?? "",
                name_zh = NameZh ?? "",
                part_label = PartLabel ?? "",
                triangle_index = TriangleIndex,
                hit_point = HitPoint,
                distance_from_camera = DistanceFromCamera,
                distance_from_character = DistanceFromCharacter,
                occluder_id = OccluderId ?? "",
                occluder_method = OccluderMethod ?? "",
                occluder_class = OccluderClass ?? "",
                occluder_distance = OccluderDistance,
                blocked_candidate_id = BlockedCandidateId ?? "",
                blocked_candidate_part = BlockedCandidatePart ?? "",
                broad_phase_samples = BroadPhaseSamples.ToArray(),
                diagnostics = Diagnostics.ToArray(),
            };
        }
    }

    // ------------------------------------------------------------------
    // Evidence records (JsonUtility friendly, public fields only)
    // ------------------------------------------------------------------
    [Serializable]
    public class Stage2SnapshotRecord
    {
        public string snapshot_id;
        public long frame_id;
        public string source;
        public string pose_id;
        public Vector3 position;
        public Quaternion rotation;
        public float fov;
        public float near;
        public float far;
        public float aspect;
        public int rt_width;
        public int rt_height;
        public double rt_stamp;
        public Matrix4x4 projection;
        public bool valid;
    }

    [Serializable]
    public class Stage2PickRecord
    {
        public string status;
        public string diagnostic;
        public long frame_id;
        public string snapshot_id;
        public string camera_source;
        public string pose_id;
        public Vector3 origin;
        public Vector3 direction;
        public float max_distance;
        public float min_distance;
        public string sample_id;
        public string name_zh;
        public string part_label;
        public int triangle_index;
        public Vector3 hit_point;
        public float distance_from_camera;
        public float distance_from_character;
        public string occluder_id;
        public string occluder_method;
        public string occluder_class;
        public float occluder_distance;
        public string blocked_candidate_id;
        public string blocked_candidate_part;
        public string[] broad_phase_samples;
        public string[] diagnostics;
    }

    [Serializable]
    public class Stage2ConfirmRecord
    {
        public long frame_id;
        public string snapshot_id;
        public string camera_source;
        public string pose_id;
        public string sample_id;
        public string name_zh;
        public float distance_from_camera;
        public string occluder_id;
        public string image;
        public string image_sha256;
        public int image_width;
        public int image_height;
        public Stage2SnapshotRecord snapshot;
        public Stage2PickRecord pick;
    }

    [Serializable]
    public class Stage2CaseRecord
    {
        public string id;
        public string category;
        public string status = "pending";     // pass / fail / pending
        public string detail = "";
        public string expected = "";
        public string actual = "";
        public string evidence = "";
    }

    [Serializable]
    public class Stage2CaseList
    {
        public Stage2CaseRecord[] cases;
    }
}

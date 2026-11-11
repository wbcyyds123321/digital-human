using System;
using System.Collections.Generic;
using UnityEngine;

namespace Yixuan.WorldMap
{
    /// <summary>
    /// Runtime data package for the Unity native fine map. The package is
    /// generated from the read-only entity inventory and HTML basemap; it does
    /// not replace any source map data.
    /// </summary>
    public static class WorldMapData
    {
        public const string Root = "SovietBlueHour/world_map/";
        public const string MapResource = Root + "world_map";
        public const string BasemapResource = Root + "basemap";

        [Serializable] public class SourceInfo
        {
            public string inventory;
            public string inventory_sha256;
            public string basemap;
            public string basemap_source_sha256;
            public string basemap_export_sha256;
            public string blend_sha256;
            public int[] source_pixels;
            public int[] export_pixels;
            public string projection;
        }

        [Serializable] public class Entity
        {
            public string id;
            public string cls;
            public string kind;
            public string name;
            public string english;
            public string[] aliases;
            public string group;
            public float[] pos;
            public float[] bounds;
            public float height;
            public float yaw;
            public bool selectable;
            public bool blocksNavigation;
        }

        [Serializable] public class Group { public string id; public string name; public string[] classes; }

        [Serializable] public class Doc
        {
            public string schema;
            public string data_version;
            public SourceInfo source;
            public float[] bounds_xz;
            public Entity[] entities;
            public Group[] groups;
        }

        [Serializable] public class Manifest
        {
            public string schema;
            public int entity_count;
            public int included_obstacles;
            public int[] basemap_export_pixels;
            public string basemap_export_sha256;
            public string world_map_sha256;
            public string nav_obstacles_sha256;
            public string inventory_sha256;
            public string basemap_source_sha256;
        }

        public static bool Loaded { get; private set; }
        public static string Error { get; private set; }
        public static Doc Data { get; private set; }
        public static Manifest Info { get; private set; }
        public static Texture2D Basemap { get; private set; }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSession()
        {
            Loaded = false; Error = null; Data = null; Info = null; Basemap = null;
        }

        /// <summary>Drop Soviet map data before an explicit lab scene owns the runtime.</summary>
        public static void ResetForExplicitScene()
        {
            Loaded = false; Error = null; Data = null; Info = null; Basemap = null;
        }
        public static float MinX { get { return Data != null ? Data.bounds_xz[0] : -212f; } }
        public static float MinZ { get { return Data != null ? Data.bounds_xz[1] : -160f; } }
        public static float MaxX { get { return Data != null ? Data.bounds_xz[2] : 212f; } }
        public static float MaxZ { get { return Data != null ? Data.bounds_xz[3] : 164f; } }
        public static int PixelWidth
        {
            get
            {
                if (Data != null && Data.source != null && Data.source.export_pixels != null && Data.source.export_pixels.Length >= 2)
                    return Data.source.export_pixels[0];
                return Basemap != null ? Basemap.width : 2048;
            }
        }
        public static int PixelHeight
        {
            get
            {
                if (Data != null && Data.source != null && Data.source.export_pixels != null && Data.source.export_pixels.Length >= 2)
                    return Data.source.export_pixels[1];
                return Basemap != null ? Basemap.height : 1565;
            }
        }

        public static bool Load()
        {
            if (Yixuan.SelectionLab.SelectionLabContext.IsActive)
            {
                ResetForExplicitScene();
                Error = "skipped_by_explicit_scene";
                return false;
            }
            if (Loaded) return Data != null && Basemap != null;
            Error = null;
            var asset = Resources.Load<TextAsset>(MapResource);
            if (asset == null)
            {
                Error = "缺少 Resources/" + MapResource + ".json";
                Debug.LogError("[WorldMapData] " + Error);
                return false;
            }
            try
            {
                Data = JsonUtility.FromJson<Doc>(asset.text);
                if (Data == null || Data.entities == null || Data.bounds_xz == null || Data.bounds_xz.Length < 4)
                {
                    Error = "世界地图 JSON 结构不完整";
                    Debug.LogError("[WorldMapData] " + Error);
                    return false;
                }
                Basemap = Resources.Load<Texture2D>(BasemapResource);
                if (Basemap == null) Error = "缺少 Resources/" + BasemapResource + ".png";
                var manifestAsset = Resources.Load<TextAsset>(Root + "manifest");
                if (manifestAsset != null)
                {
                    Info = JsonUtility.FromJson<Manifest>(manifestAsset.text);
                }
                Loaded = Data.entities.Length > 0;
                Debug.Log("[WorldMapData] 载入实体=" + Data.entities.Length +
                          "，底图=" + (Basemap != null ? Basemap.width + "x" + Basemap.height : "缺失") +
                          "，障碍=" + (Info != null ? Info.included_obstacles : -1) +
                          (string.IsNullOrEmpty(Error) ? "" : "，错误=" + Error));
                return Loaded && Basemap != null;
            }
            catch (Exception e)
            {
                Error = e.Message;
                Debug.LogError("[WorldMapData] 载入异常：" + e.Message);
                return false;
            }
        }

        public static float GroundY(float x, float z)
        {
            var map = Yixuan.World.WorldData.Map;
            if (map != null) return map.SurfaceY(x, z);
            return Yixuan.World.WorldData.GroundHeightAt(new Vector3(x, 0f, z));
        }
    }
}

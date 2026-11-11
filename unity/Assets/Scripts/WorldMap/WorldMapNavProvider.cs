using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Yixuan.World;
using Yixuan.SelectionLab;

namespace Yixuan.WorldMap
{
    /// <summary>
    /// Read-only bridge from the generated world-map navigation sidecar to the
    /// existing MapWorldProfile obstacle collection. It only adds curated
    /// ground-level entity AABBs; it never edits collision.json or Stage-4 data.
    /// </summary>
    public static class WorldMapNavProvider
    {
        public const string ProviderId = "world_map.curated_entities";
        public const string ResourcePath = "SovietBlueHour/world_map/nav_obstacles";

        [Serializable] class Doc { public Included[] included; }
        [Serializable] class Included
        {
            public string id;
            public string cls;
            public float min_x;
            public float max_x;
            public float min_z;
            public float max_z;
            public float min_y;
            public float max_y;
        }

        static List<Bounds> _bounds;
        public static bool Loaded { get; private set; }
        public static string LastError { get; private set; }
        public static int Count { get { return _bounds != null ? _bounds.Count : 0; } }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSession()
        {
            _bounds = null;
            Loaded = false;
            LastError = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        /// <summary>
        /// Delay the Soviet-sidecar load until the scene is known. The whitebox
        /// lab must never read the Soviet navigation package; a scene marker that
        /// has not Awake() yet is not sufficient at BeforeSceneLoad time.
        /// </summary>
        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (SelectionLabContext.IsActive)
            {
                Unload();
                return;
            }
            Load();
        }

        /// <summary>
        /// Synchronous preparation hook for MapWorldProfile. It must run before
        /// the profile's first AddExternalObstacles/BuildWalkability pass.
        /// </summary>
        public static void EnsurePreparedForScene(Scene scene)
        {
            if (SelectionLabContext.IsActive)
            {
                Unload();
                return;
            }
            Load();
        }

        public static void Load()
        {
            if (SelectionLabContext.IsActive)
            {
                Unload();
                LastError = "skipped_by_explicit_scene";
                return;
            }
            if (_bounds != null) return;
            try
            {
                var asset = Resources.Load<TextAsset>(ResourcePath);
                if (asset == null)
                {
                    LastError = "缺少 Resources/" + ResourcePath + ".json";
                    Debug.LogWarning("[WorldMapNavProvider] " + LastError);
                    _bounds = new List<Bounds>();
                    return;
                }
                var doc = JsonUtility.FromJson<Doc>(asset.text);
                var list = new List<Bounds>();
                if (doc != null && doc.included != null)
                {
                    foreach (var e in doc.included)
                    {
                        if (e == null) continue;
                        float cx = (e.min_x + e.max_x) * 0.5f;
                        float cz = (e.min_z + e.max_z) * 0.5f;
                        float cy = (e.min_y + e.max_y) * 0.5f;
                        var size = new Vector3(Mathf.Abs(e.max_x - e.min_x),
                                               Mathf.Max(0.1f, Mathf.Abs(e.max_y - e.min_y)),
                                               Mathf.Abs(e.max_z - e.min_z));
                        if (size.x < 0.05f || size.z < 0.05f) continue;
                        list.Add(new Bounds(new Vector3(cx, cy, cz), size));
                    }
                }
                _bounds = list;
                MapWorldProfile.RegisterExternalObstacleProvider(ProviderId, GetBounds);
                Loaded = true;
                Debug.Log("[WorldMapNavProvider] 载入策划导航障碍=" + _bounds.Count +
                          "，统一膨胀由 MapWorldProfile 负责");
            }
            catch (Exception e)
            {
                LastError = e.Message;
                _bounds = new List<Bounds>();
                Debug.LogError("[WorldMapNavProvider] " + LastError);
            }
        }

        public static List<Bounds> GetBounds()
        {
            if (SelectionLabContext.IsActive)
            {
                Unload();
                return new List<Bounds>();
            }
            if (_bounds == null) Load();
            return _bounds != null ? new List<Bounds>(_bounds) : new List<Bounds>();
        }

        public static void Unload()
        {
            MapWorldProfile.UnregisterExternalObstacleProvider(ProviderId);
            _bounds = null;
            Loaded = false;
        }
    }
}

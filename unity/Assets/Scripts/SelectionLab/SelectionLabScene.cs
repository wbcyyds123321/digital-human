using System;
using UnityEngine;
using Yixuan.Pick;
using Yixuan.World;

namespace Yixuan.SelectionLab
{
    [Serializable]
    public sealed class SelectionLabEntityDef
    {
        public string id = "";
        public string cls = "";
        public string display_name = "";
        public string shape = "";
        public string color = "";
        public Transform target;
        public Renderer renderer;
        public string capabilities = "query,select";
    }

    /// <summary>
    /// Explicit scene context for the whitebox selection lab. This is the one
    /// source of entity identity, bounds, map transform and spawn/observation
    /// points for the lab. It deliberately does not use the Soviet sidecars.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class SelectionLabScene : MonoBehaviour
    {
        public string lab_id = "selection_lab_whitebox_v1";
        public Transform spawnPoint;
        public Transform observationA;
        public Transform observationB;
        public Vector2 mapMin = new Vector2(-12f, -7f);
        public Vector2 mapMax = new Vector2(12f, 7f);
        public SelectionLabEntityDef[] entities = new SelectionLabEntityDef[0];

        void Awake()
        {
            SelectionLabContext.Bind(this);
            ApplyRuntimeDefaults();
        }

        void Start()
        {
            StartCoroutine(ApplyLabInputDefaultsAfterChatReady());
        }

        System.Collections.IEnumerator ApplyLabInputDefaultsAfterChatReady()
        {
            var chat = FindObjectOfType<Yixuan.AI.ChatSession>();
            while (chat != null && !chat.InitializationComplete) yield return null;
            if (chat == null || chat.Config == null) yield break;
            // Config is already ChatSession's runtime Instantiate() clone. Never
            // mutate the shared AIConfig asset here; lab defaults are runtime-only.
            chat.Config.VoiceEnabled = false;
            chat.Config.VoiceInputMode = "off";
            chat.SetVoiceOutputEnabled(false);
            if (chat.VoiceInput != null) chat.VoiceInput.SetEnabled(false);
            var observer = FindObjectOfType<Yixuan.Vision.VisionObserver>();
            if (observer != null) observer.enabled = false;
            Debug.Log("[SelectionLab] runtime input defaults applied to ChatSession.Config clone");
        }

        void OnDestroy()
        {
            SelectionLabContext.Unbind(this);
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Alpha1)) PlaceAt(observationA);
            if (Input.GetKeyDown(KeyCode.Alpha2)) PlaceAt(observationB);
        }

        void ApplyRuntimeDefaults()
        {
            var observer = FindObjectOfType<Yixuan.Vision.VisionObserver>();
            if (observer != null) observer.enabled = false;
            var brain = FindObjectOfType<Yixuan.Brain.YixuanBrain>();
            if (brain != null) { brain.EnableProactive = false; brain.TakeOverAutoPlay = false; }
            var chat = FindObjectOfType<Yixuan.AI.ChatSession>();
            if (chat != null) chat.AutoCommentOnVisualEvents = false;
            // ChatSession.Config is not cloned yet at Awake; all runtime input
            // defaults are applied by ApplyLabInputDefaultsAfterChatReady to the
            // runtime clone, never to the shared AIConfig asset.
            var director = FindObjectOfType<Yixuan.Presentation.CameraDirector>();
            if (director != null) director.EyeView = true;
            PlaceAt(observationA != null ? observationA : spawnPoint);
        }

        public void PlaceAt(Transform point)
        {
            if (point == null) return;
            var character = PickService.FindCharacterTransform();
            var loco = FindObjectOfType<Yixuan.Motion.CharacterLocomotion>();
            var pos = new Vector3(point.position.x, 0.02f, point.position.z);
            if (character != null) character.position = pos;
            if (loco != null) { loco.Stop("selection_lab_spawn"); loco.transform.position = pos; }
            if (character != null)
            {
                var flat = point.forward; flat.y = 0f;
                if (flat.sqrMagnitude > 1e-4f) character.rotation = Quaternion.LookRotation(flat.normalized, Vector3.up);
            }
            var gaze = FindObjectOfType<Yixuan.Vision.EyeGimbalController>();
            if (gaze != null) gaze.Center(true);
        }

        public Vector2 MapUv(Vector3 world)
        {
            float x = Mathf.InverseLerp(mapMin.x, mapMax.x, world.x);
            float y = Mathf.InverseLerp(mapMin.y, mapMax.y, world.z);
            return new Vector2(x, y);
        }

        /// <summary>
        /// World forward -> minimap screen direction. The map is top-down with
        /// +X screen-right and +Z screen-up; GUI screen Y points down. This is
        /// the single conversion used by the minimap arrow and its static checks.
        /// </summary>
        public static Vector2 MapForward(Vector3 forward)
        {
            Vector2 d = new Vector2(forward.x, -forward.z);
            if (d.sqrMagnitude < 1e-8f) return Vector2.up;
            return d.normalized;
        }

        public WorldEntityCatalog.Doc BuildCatalogDoc()
        {
            var doc = new WorldEntityCatalog.Doc
            {
                schema = "selection_lab.explicit_entities.v1",
                data_version = lab_id,
                map_version = lab_id,
                spawn = spawnPoint != null ? new[] { spawnPoint.position.x, spawnPoint.position.y, spawnPoint.position.z } : new[] { 0f, 0.02f, -6f },
                spawn_yaw = spawnPoint != null ? spawnPoint.eulerAngles.y : 0f,
                counts = new WorldEntityCatalog.Counts(),
                entities = new WorldEntityCatalog.Entity[entities.Length],
                anchors = new WorldEntityCatalog.Anchor[0],
            };
            for (int i = 0; i < entities.Length; i++)
            {
                var d = entities[i];
                var renderer = d != null ? d.renderer : null;
                var bounds = renderer != null ? renderer.bounds : new Bounds(Vector3.zero, Vector3.one);
                var center = renderer != null ? bounds.center : (d != null && d.target != null ? d.target.position : Vector3.zero);
                var entity = new WorldEntityCatalog.Entity
                {
                    id = d != null ? d.id : "",
                    cls = d != null ? d.cls : "",
                    kind = "selection_lab",
                    name = d != null ? d.display_name : "",
                    display_name = d != null ? d.display_name : "",
                    english = d != null ? d.shape : "",
                    group = "selection_lab",
                    area = "whitebox",
                    pos = new[] { center.x, center.y, center.z },
                    bounds = new[] { bounds.min.x, bounds.min.z, bounds.max.x, bounds.max.z },
                    height = bounds.size.y,
                    selectable = true,
                    blocks_navigation = false,
                    capabilities = new WorldEntityCatalog.Capability[0],
                    source = "selection_lab_explicit",
                };
                doc.entities[i] = entity;
            }
            doc.counts.entities = entities.Length;
            return doc;
        }
    }
}

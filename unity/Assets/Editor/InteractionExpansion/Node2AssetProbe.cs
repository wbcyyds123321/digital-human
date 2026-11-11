// Node-2 asset probe: verifies the re-imported map prefab keeps the dynamic DYN_* nodes
// separate from the merged static groups (no ghost copies), plus the dynamic data package.
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Yixuan.EditorTools
{
    public static class Node2AssetProbe
    {
        const string PrefabPath = "Assets/Maps/SovietBlueHour/SovietBlueHour_Map.prefab";
        const string OutPath = "Logs/overnight_interactions_20261109/node2_asset_probe.json";

        [MenuItem("UnityLITF/Node2/资产探针（动态实体拆分校验）")]
        public static void Run()
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"schema\": \"sbh.node2-asset-probe.v1\",\n");
            sb.Append("  \"generated_at\": \"").Append(DateTime.Now.ToString("o")).Append("\",\n");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            sb.Append("  \"prefab_loaded\": ").Append(prefab != null ? "true" : "false").Append(",\n");
            int dynRoots = 0;
            int vehicleNodes = 0;
            int tramBodies = 0;
            int flameNodes = 0;
            int staticMergeGroups = 0;
            int rendererCount = 0;
            var missing = new StringBuilder();
            if (prefab != null)
            {
                var transforms = prefab.GetComponentsInChildren<Transform>(true);
                var names = new System.Collections.Generic.HashSet<string>();
                foreach (var t in transforms) names.Add(t.name);
                foreach (var t in transforms)
                {
                    if ((t.name.StartsWith("DYN_entity_veh_") ||
                         t.name.StartsWith("DYN_entity_prop_car_")) &&
                        t.name != "DYN_entity_veh_tram_01") vehicleNodes++;
                    if (t.name == "DYN_entity_veh_tram_01") tramBodies++;
                    if (t.name == "DYN_entity_prop_eternal_flame") flameNodes++;
                    if (t.name.StartsWith("DYN_")) dynRoots++;
                    if (t.name == "SITE" || t.name == "PROPS" || t.name == "VEHICLES") staticMergeGroups++;
                }
                rendererCount = prefab.GetComponentsInChildren<Renderer>(true).Length;
                if (!Yixuan.Dynamic.DynamicData.EnsureLoaded())
                    missing.Append("dynamic_data:").Append(Yixuan.Dynamic.DynamicData.Error).Append(";");
                else
                {
                    foreach (var spec in Yixuan.Dynamic.DynamicData.Root.vehicles)
                        if (spec != null && !names.Contains(spec.node)) missing.Append(spec.node).Append(";");
                    if (!names.Contains(Yixuan.Dynamic.DynamicData.Root.tram.node)) missing.Append("tram;");
                    if (!names.Contains(Yixuan.Dynamic.DynamicData.Root.flame.node)) missing.Append("flame;");
                }
            }
            sb.Append("  \"dynamic_roots\": ").Append(dynRoots).Append(",\n");
            sb.Append("  \"vehicle_nodes\": ").Append(vehicleNodes).Append(",\n");
            sb.Append("  \"tram_nodes\": ").Append(tramBodies).Append(",\n");
            sb.Append("  \"flame_nodes\": ").Append(flameNodes).Append(",\n");
            sb.Append("  \"static_merge_groups\": ").Append(staticMergeGroups).Append(",\n");
            sb.Append("  \"renderer_count\": ").Append(rendererCount).Append(",\n");
            sb.Append("  \"dynamic_data_loaded\": ").Append(Yixuan.Dynamic.DynamicData.Loaded ? "true" : "false").Append(",\n");
            sb.Append("  \"missing_nodes\": \"").Append(Escape(missing.ToString())).Append("\",\n");
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            bool sceneBootstrap = GameObject.Find("DynamicInteractionSystem") != null;
            int sceneDynRoots = 0;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (t.name.StartsWith("DYN_")) sceneDynRoots++;
            sb.Append("  \"scene_dynamic_system\": ").Append(sceneBootstrap ? "true" : "false").Append(",\n");
            sb.Append("  \"scene_dynamic_roots\": ").Append(sceneDynRoots).Append(",\n");
            int hudCount = 0;
            int hudPanelWidth = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var hud in root.GetComponentsInChildren<Yixuan.UI.UnifiedHud>(true))
                {
                    hudCount++;
                    hudPanelWidth = Mathf.RoundToInt(hud.PanelWidth);
                }
            }
            bool legacyTaskPanelPresent = UnityEngine.Object.FindObjectOfType<Yixuan.AI.Tasks.TaskMissionPanel>() != null;
            bool chatUiPresent = UnityEngine.Object.FindObjectOfType<Yixuan.AI.ChatUI>() != null;
            sb.Append("  \"scene_unified_hud_count\": ").Append(hudCount).Append(",\n");
            sb.Append("  \"scene_unified_hud_panel_width\": ").Append(hudPanelWidth).Append(",\n");
            sb.Append("  \"scene_legacy_task_panel_present\": ").Append(legacyTaskPanelPresent ? "true" : "false").Append(",\n");
            sb.Append("  \"scene_chat_ui_present\": ").Append(chatUiPresent ? "true" : "false").Append(",\n");
            sb.Append("  \"passed\": ").Append(prefab != null && vehicleNodes == 47 && tramBodies >= 1 &&
                flameNodes >= 1 && staticMergeGroups == 3 && missing.Length == 0 &&
                sceneBootstrap && sceneDynRoots >= 45 && hudCount == 1 && chatUiPresent ? "true" : "false").Append("\n");
            sb.Append("}\n");
            Directory.CreateDirectory("Logs/overnight_interactions_20261109");
            File.WriteAllText(OutPath, sb.ToString());
            Debug.Log("[N2-PROBE] " + sb.ToString());
        }

        static string Escape(string value)
        {
            return (value ?? "").Replace("\\", "/").Replace("\"", "'");
        }
    }
}

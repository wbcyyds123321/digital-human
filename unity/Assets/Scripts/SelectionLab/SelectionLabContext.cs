using UnityEngine;
using Yixuan.Pick;
using Yixuan.World;

namespace Yixuan.SelectionLab
{
    public static class SelectionLabContext
    {
        public static SelectionLabScene Scene { get; private set; }
        public static bool IsActive { get { return Scene != null && Scene.isActiveAndEnabled; } }

        public static void Bind(SelectionLabScene scene)
        {
            Scene = scene;
            if (scene == null) return;
            // A lab scene must not inherit the Soviet map, seat or navigation caches
            // from a previous scene. Clear them before installing the explicit source.
            Yixuan.Pick.SelectionBroker.Clear();
            Yixuan.WorldMap.WorldMapNavProvider.Unload();
            Yixuan.Stage4.Stage4SeatData.ResetForExplicitScene();
            Yixuan.WorldMap.WorldMapData.ResetForExplicitScene();
            Yixuan.UI.BigMapPanel.ResetForExplicitScene();
            Yixuan.WorldMap.WorldMapPanel.Shutdown();
            var doc = scene.BuildCatalogDoc();
            WorldEntityCatalog.UseExplicit(doc);
            EntityPickRegistry.ResetForExplicitSource();
            FirstPersonSelectionController.EnsureInstance();
            Debug.Log("[SelectionLab] bound " + scene.lab_id + " entities=" + scene.entities.Length);
        }

        public static void Unbind(SelectionLabScene scene)
        {
            if (Scene != scene) return;
            Scene = null;
            WorldEntityCatalog.ResetExplicit();
            EntityPickRegistry.ResetForExplicitSource();
            Yixuan.WorldMap.WorldMapPanel.Shutdown();
            Yixuan.Pick.SelectionBroker.Clear();
            Debug.Log("[SelectionLab] unbound; caches reset for next scene");
        }

        public static SelectionLabEntityDef Find(string id)
        {
            if (Scene == null || string.IsNullOrEmpty(id)) return null;
            foreach (var e in Scene.entities) if (e != null && e.id == id) return e;
            return null;
        }
    }
}

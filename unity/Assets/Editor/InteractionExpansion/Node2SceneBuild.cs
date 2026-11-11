// Node-2 scene rebuild after the split GLB import: rebuild the playable scene, then probe the
// prefab/scene for the 48 vehicle nodes + tram + flame nodes and write the evidence JSON.
using UnityEditor;
using UnityEngine;

namespace Yixuan.EditorTools
{
    public static class Node2SceneBuild
    {
        [MenuItem("UnityLITF/Node2/重建场景并探针")]
        public static void Run()
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Node2AssetProbe.Run();
            SovietBlueHourSceneBuilder.BuildAll();
            Node2AssetProbe.Run();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[N2-SCENEBUILD] done");
        }
    }
}

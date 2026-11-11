// Node-2 one-shot batch rebuild: import the split dynamic map GLB, rebuild the prefab and the
// playable scene, then probe that 48 vehicle nodes + tram + flame nodes survived the merge.
using UnityEditor;
using UnityEngine;

namespace Yixuan.EditorTools
{
    public static class Node2Rebuild
    {
        [MenuItem("UnityLITF/Node2/重建动态资产与场景")]
        public static void Run()
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            ImportSovietBlueHour.RunStage1ForceReimport = true;
            ImportSovietBlueHour.RunStage1();
            Node2AssetProbe.Run();
            SovietBlueHourSceneBuilder.BuildAll();
            Node2AssetProbe.Run();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[N2-REBUILD] done");
        }
    }
}

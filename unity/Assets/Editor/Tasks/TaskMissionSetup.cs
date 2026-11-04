using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Yixuan.AI;
using Yixuan.AI.Tasks;
using Yixuan.World;

public static class TaskMissionSetup
{
    // 2026-11 地图迁移：任务验收场景已从「雨夜便利店」换成「苏联蓝调小区」。
    // 旧场景 Assets/Scenes/RainyConvenience_TaskPlan.unity 保留作回退，不再作为默认任务场景。
    public const string ScenePath = "Assets/Scenes/SovietBlueHour_TaskPlan.unity";
    [MenuItem("UnityLITF/任务编排/打开任务验收场景")]
    public static void Open()
    {
        if (EditorApplication.isPlaying) { Debug.LogWarning("请先停止 Play，再切换任务场景。"); return; }
        if (File.Exists(ScenePath)) { EditorSceneManager.OpenScene(ScenePath); return; }
        Debug.LogError("任务场景不存在：" + ScenePath +
                       "。请先执行菜单 UnityLITF/地图/① 构建 SovietBlueHour 任务场景（需要先导入地图 prefab）。");
    }
    [MenuItem("UnityLITF/任务编排/调整验收场景第三人称机位")]
    public static void ConfigureAcceptanceView()
    {
        if (EditorApplication.isPlaying || EditorSceneManager.GetActiveScene().path != ScenePath) return;
        // 地图迁移后统一由构建器/配置驱动：相机、天空、光照一起刷新，避免只改一半。
        Yixuan.EditorTools.SovietBlueHourSceneBuilder.UpdateCameraAndLighting();
    }
    static void MakeTarget(Transform parent,string name,string id,string label,string aliases,Vector3 p,Vector3 size,Vector3 approach,Color color,bool interaction)
    {
        var go=Cube(parent,name,p,size,color);
        var marker=go.AddComponent<TaskDestinationMarker>();marker.Id=id;marker.DisplayName=label;marker.Aliases=aliases;
        var pose=new GameObject(name+"_Approach");pose.transform.SetParent(parent);pose.transform.position=approach;marker.ApproachPose=pose.transform;
        if(interaction) { var control=go.AddComponent<TaskTestSwitch>();control.MaxDistance=1.4f;control.FacingTolerance=30; }
        Cube(parent,name+"_StandingPad",approach+Vector3.up*0.015f,new Vector3(0.55f,0.03f,0.55f),color*0.65f);
    }
    static GameObject Cube(Transform parent,string name,Vector3 p,Vector3 size,Color color)
    {
        var go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.name=name;go.transform.SetParent(parent);go.transform.position=p;go.transform.localScale=size;
        var collider=go.GetComponent<Collider>();if(collider!=null)Object.DestroyImmediate(collider);
        string dir="Assets/Experiments/TaskPlan";Directory.CreateDirectory(dir);
        string path=dir+"/"+name+".mat";
        var mat=AssetDatabase.LoadAssetAtPath<Material>(path);
        if(mat==null){mat=new Material(Shader.Find("Universal Render Pipeline/Lit"));mat.color=color;AssetDatabase.CreateAsset(mat,path);}
        go.GetComponent<Renderer>().sharedMaterial=mat;return go;
    }
    [MenuItem("UnityLITF/任务编排/显示任务面板")]
    public static void ShowPanel()
    {var panel=Object.FindObjectOfType<TaskMissionPanel>();if(panel!=null)panel.Visible=true;}
    [MenuItem("UnityLITF/任务编排/将选中物体填入任务")]
    public static void UseSelection()
    {var panel=Object.FindObjectOfType<TaskMissionPanel>();if(panel!=null&&Selection.activeTransform!=null){panel.Request="前往"+TaskTargetCatalog.ScenePath(Selection.activeTransform);panel.Visible=true;}}
}

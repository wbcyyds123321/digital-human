using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UniVRM10;
namespace Yixuan.Face {
 // Scene mesh replacement invalidated imported VRM paths AND indices. Repair only the
 // known MMD mesh signature, by exact morph names, in disposable runtime asset copies.
 [DefaultExecutionOrder(-32000)]
 public sealed class VrmExpressionBindingRepair : MonoBehaviour {
  Vrm10Instance _instance;VRM10Object _original,_copy;
  readonly List<VRM10Expression> _clips=new List<VRM10Expression>();
  public int RepairedBindings {get;private set;}
  static readonly Dictionary<ExpressionPreset,string> Shapes=new Dictionary<ExpressionPreset,string>{
   {ExpressionPreset.happy,"笑い"},{ExpressionPreset.angry,"怒り"},{ExpressionPreset.sad,"困る"},{ExpressionPreset.surprised,"びっくり"},
   {ExpressionPreset.aa,"あ"},{ExpressionPreset.ih,"い"},{ExpressionPreset.ou,"う"},{ExpressionPreset.ee,"え"},{ExpressionPreset.oh,"お"},
   {ExpressionPreset.blink,"まばたき"},{ExpressionPreset.blinkLeft,"ウィンク"},{ExpressionPreset.blinkRight,"ウィンク右"},
   {ExpressionPreset.lookUp,"上"},{ExpressionPreset.lookDown,"下"}
  };
  [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
  static void Register(){SceneManager.sceneLoaded-=OnScene;SceneManager.sceneLoaded+=OnScene;}
  static void OnScene(Scene scene,LoadSceneMode mode){foreach(var v in Object.FindObjectsOfType<Vrm10Instance>(true))if(v.GetComponent<Yixuan.Core.YixuanCharacter>()!=null&&v.GetComponent<VrmExpressionBindingRepair>()==null)v.gameObject.AddComponent<VrmExpressionBindingRepair>();}
  void Awake(){
   _instance=GetComponent<Vrm10Instance>();if(_instance==null||_instance.Vrm==null)return;
   SkinnedMeshRenderer candidate=null;
   foreach(var r in _instance.GetComponentsInChildren<SkinnedMeshRenderer>(true)){
    if(r.sharedMesh==null)continue;bool signature=true;foreach(var shape in Shapes.Values)if(r.sharedMesh.GetBlendShapeIndex(shape)<0)signature=false;
    if(!signature)continue;if(candidate!=null)return;candidate=r;
   }
   if(candidate==null)return;
   string path=candidate.name;var t=candidate.transform;
   while(t.parent!=null&&t.parent!=_instance.transform){t=t.parent;path=t.name+"/"+path;}
   if(t.parent!=_instance.transform)return;
   var replacements=new Dictionary<VRM10Expression,VRM10Expression>();
   foreach(var item in _instance.Vrm.Expression.Clips){
    string shape;if(!Shapes.TryGetValue(item.Preset,out shape))continue;
    var bindings=item.Clip.MorphTargetBindings;if(bindings==null||bindings.Length!=1||bindings[0].RelativePath!="仪玄/仪玄_arm/仪玄_mesh")continue;
    var clip=Instantiate(item.Clip);clip.hideFlags=HideFlags.DontSave;
    clip.MorphTargetBindings=new[]{new MorphTargetBinding{RelativePath=path,Index=candidate.sharedMesh.GetBlendShapeIndex(shape),Weight=bindings[0].Weight}};
    replacements[item.Clip]=clip;_clips.Add(clip);RepairedBindings++;
   }
   if(RepairedBindings==0)return;
   _original=_instance.Vrm;_copy=Instantiate(_original);_copy.hideFlags=HideFlags.DontSave;
   _copy.Expression.Replace(replacements);_instance.Vrm=_copy;
   Debug.Log("[VRMExpressionRepair] 已按形态名称修复 "+RepairedBindings+" 个旧绑定，路径="+path+"；仅运行时副本。");
  }
  void OnDestroy(){if(_instance!=null&&_instance.Vrm==_copy)_instance.Vrm=_original;foreach(var clip in _clips)if(clip!=null)Destroy(clip);if(_copy!=null)Destroy(_copy);}
 }
}

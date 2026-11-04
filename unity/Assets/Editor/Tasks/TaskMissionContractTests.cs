using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Yixuan.AI.Tasks;

public static class TaskMissionContractTests
{
    [Serializable] public class Case { public string name; public bool pass; public string detail; }
    [Serializable] public class Report { public int total, passed; public List<Case> cases = new List<Case>(); }
    public static Report RunAll(string output)
    {
        var report = new Report();
        var actor = GameObject.Find("Yixuan_Test").transform;
        var catalog = new TaskTargetCatalog(); catalog.Refresh(actor);
        Action<string,bool,string> check = (name,ok,detail) => { report.cases.Add(new Case { name=name,pass=ok,detail=detail }); report.total++;if(ok)report.passed++; };
        var traffic = MissionIntent.Bind("前往TrafficLight",catalog);
        var chinese = MissionIntent.Bind("前往红绿灯",catalog);
        check("真实对象中英文同一身份",traffic.error==null&&chinese.error==null&&traffic.destination_ids.Length==1&&traffic.destination_ids[0]==chinese.destination_ids[0],JsonUtility.ToJson(traffic));
        var green = MissionIntent.Bind("前往绿色柱子",catalog);
        check("未知目标拒绝",MissionIntent.Bind("前往NeverExistsXYZ",catalog).error!=null,"");
        check("对象名后未知修饰拒绝",MissionIntent.Bind("前往TrafficLightXYZ",catalog).error!=null,"");
        check("任务入口不接管普通对话",!MissionIntent.LooksLikeMission("你好，今天心情怎么样？"),"");
        check("任务入口不接管直接转身",!MissionIntent.LooksLikeMission("向右转90度"),"");
        check("任务入口接管实际目标",MissionIntent.LooksLikeMission("请前往TrafficLight"),"");
        var good = new MissionPlan { steps=new[] {
            new MissionStep {id="s1",kind="navigate",target_id=traffic.destination_ids[0],after=new string[0]},
            new MissionStep {id="s2",kind="report",after=new[]{"s1"}} } };
        check("单目标计划合法",MissionPlanValidator.Validate(good,traffic,catalog)==null,"");
        var swap=Clone(good);swap.steps[0].target_id="place_2";
        check("禁止蓝色目标替换红绿灯",MissionPlanValidator.Validate(swap,traffic,catalog)!=null,"");
        var cycle=Clone(good);cycle.steps[0].after=new[]{"s2"};
        check("依赖环拒绝",MissionPlanValidator.Validate(cycle,traffic,catalog)!=null,"");
        var early=Clone(good);early.steps[1].after=new string[0];
        check("汇报必须等待操作",MissionPlanValidator.Validate(early,traffic,catalog)!=null,"");
        var unknown=Clone(good);unknown.steps[0].kind="execute_csharp";
        check("未知能力拒绝",MissionPlanValidator.Validate(unknown,traffic,catalog)!=null,"");
        var dup=Clone(good);dup.steps[1].id="s1";
        check("重复步骤编号拒绝",MissionPlanValidator.Validate(dup,traffic,catalog)!=null,"");
        var full=MissionIntent.Bind("去1号地点观察一下四周，然后去2号地点触发交互，最后回到起点",catalog);
        check("完整任务身份及动作次序",full.error==null&&full.required_actions.Length==5&&full.required_actions[1].kind=="observe"&&full.required_actions[1].target_id=="place_1",JsonUtility.ToJson(full));
        var fullPlan=new MissionPlan{steps=new[]{
            new MissionStep{id="a",kind="navigate",target_id="place_1"},
            new MissionStep{id="b",kind="observe",target_id="place_1",after=new[]{"a"}},
            new MissionStep{id="c",kind="navigate",target_id="place_2",after=new[]{"b"}},
            new MissionStep{id="d",kind="interact",target_id="place_2",after=new[]{"c"}},
            new MissionStep{id="e",kind="return",target_id="origin",after=new[]{"d"}},
            new MissionStep{id="f",kind="report",after=new[]{"e"}}}};
        check("完整任务计划合法",MissionPlanValidator.Validate(fullPlan,full,catalog)==null,"");
        var wrongObserve=Clone(fullPlan);wrongObserve.steps[1].target_id="place_2";
        check("观察地点不得偷换",MissionPlanValidator.Validate(wrongObserve,full,catalog)!=null,"");
        var concurrent=Clone(fullPlan);concurrent.steps[2].after=new[]{"a"};
        check("未完成观察不得开始后续导航",MissionPlanValidator.Validate(concurrent,full,catalog)!=null,"");
        var extra=Clone(good);extra.steps=new[]{extra.steps[0],new MissionStep{id="q",kind="observe",target_id=traffic.destination_ids[0],after=new[]{"s1"}},new MissionStep{id="end",kind="report",after=new[]{"q"}}};
        check("禁止擅加观察",MissionPlanValidator.Validate(extra,traffic,catalog)!=null,"");
        check("移动与表情资源可并行",(MissionPlanValidator.Resources("navigate")&MissionPlanValidator.Resources("express"))==0,"");
        check("移动与观察身体资源互斥",(MissionPlanValidator.Resources("navigate")&MissionPlanValidator.Resources("observe"))!=0,"");
        var one=GameObject.CreatePrimitive(PrimitiveType.Cube);one.name="DuplicateAcceptanceSign";one.transform.position=new Vector3(2,1,6);
        var two=GameObject.CreatePrimitive(PrimitiveType.Cube);two.name="DuplicateAcceptanceSign";two.transform.position=new Vector3(3,1,6);
        catalog.Refresh(actor);var ambiguous=MissionIntent.Bind("前往DuplicateAcceptanceSign",catalog);
        check("重名必须澄清",ambiguous.error!=null&&ambiguous.error.Contains("重名"),ambiguous.error);
        var exact=MissionIntent.Bind("前往"+TaskTargetCatalog.ScenePath(one.transform),catalog);
        check("完整路径消除歧义",exact.error==null&&exact.destination_ids.Length==1,JsonUtility.ToJson(exact));
        one.name="DistinctAcceptanceOne";two.name="DistinctAcceptanceTwo";
        one.AddComponent<TaskDestinationMarker>().Id="duplicate_id";two.AddComponent<TaskDestinationMarker>().Id="duplicate_id";catalog.Refresh(actor);
        check("重复目标Id禁止静默替换",MissionIntent.Bind("前往DistinctAcceptanceTwo",catalog).error!=null&&catalog.Get("duplicate_id")==null,"");
        UnityEngine.Object.DestroyImmediate(one);UnityEngine.Object.DestroyImmediate(two);catalog.Refresh(actor);
        string id,error;int consumed;catalog.MatchStart("TrafficLight",out id,out consumed,out error);
        Vector3 approach;List<Vector3> path;
        bool reachable=catalog.FindApproach(catalog.Get(id),actor.position,out approach,out path,out error);
        check("实际红绿灯可达靠近点",reachable&&path!=null&&path.Count>0,approach+" "+error);
        check("英文导航进入任务入口",MissionIntent.LooksLikeMission("Go to TrafficLight"),"");
        var installedGesture=MissionIntent.Bind("前往红绿灯，然后挥舞双手",catalog);
        check("已安装手势绑定真实素材",installedGesture.error==null&&installedGesture.required_actions.Length==2&&installedGesture.required_actions[1].kind=="gesture"&&TaskGestureSkills.Get(installedGesture.required_actions[1].action_id)!=null,JsonUtility.ToJson(installedGesture));
        var gestureAsExpression=Clone(good); gestureAsExpression.steps=new[]{gestureAsExpression.steps[0],new MissionStep{id="g",kind="express",emotion="happy",after=new[]{"s1"}},new MissionStep{id="r",kind="report",after=new[]{"g"}}};
        check("已安装手势不能由表情冒充",MissionPlanValidator.Validate(gestureAsExpression,installedGesture,catalog)!=null,"");
        var speechPayload=MissionIntent.Bind("前往红绿灯，同时说一句我正在去红绿灯",catalog);
        check("语音内容不能追加导航",speechPayload.destination_ids.Length==1&&speechPayload.speak,JsonUtility.ToJson(speechPayload));
        var negative=MissionIntent.Bind("请不要前往红绿灯，然后前往绿色柱子",catalog);
        check("否定的目的地不执行",negative.destination_ids.Length==1&&negative.destination_ids[0]=="place_1",JsonUtility.ToJson(negative));
        var speechIntent=MissionIntent.Bind("前往红绿灯，同时说一句你好",catalog);
        check("禁止漏掉请求语音",MissionPlanValidator.Validate(good,speechIntent,catalog)=="requested_speech_omitted","");
        var expressionIntent=MissionIntent.Bind("前往红绿灯，同时保持开心的表情三秒",catalog);
        check("禁止漏掉请求表情",MissionPlanValidator.Validate(good,expressionIntent,catalog)=="requested_expression_omitted","");
        var serialExpression=Clone(good);serialExpression.steps=new[]{serialExpression.steps[0],new MissionStep{id="e",kind="express",emotion="happy",duration=3,after=new[]{"s1"}},new MissionStep{id="r",kind="report",after=new[]{"e"}}};
        check("明确同时不能串行表情",MissionPlanValidator.Validate(serialExpression,expressionIntent,catalog)=="explicit_parallel_dependency_not_shared_express",JsonUtility.ToJson(expressionIntent));
        serialExpression.steps[1].after=new string[0];serialExpression.steps[2].after=new[]{"s1","e"};
        check("共同依赖确保并发可调度",MissionPlanValidator.Validate(serialExpression,expressionIntent,catalog)==null,"");
        var speechOnly=MissionIntent.Bind("说一句去红绿灯",catalog);
        check("仅台词不得执行导航",speechOnly.error==null&&speechOnly.speak&&speechOnly.required_actions.Length==0,JsonUtility.ToJson(speechOnly));
        var negativeSemantic=new SemanticMission{actions=new[]{new MissionRequiredAction{kind="navigate",target_id=traffic.destination_ids[0],source="前往红绿灯"}}};
        check("语义证据不能截掉否定词",MissionSemanticInterpreter.Validate("请不要前往红绿灯",negativeSemantic,catalog).error!=null,"");
        var spokenNavigation=new SemanticMission{actions=new[]{new MissionRequiredAction{kind="navigate",target_id=traffic.destination_ids[0],source="去红绿灯"}}};
        check("语义层不能把台词变导航",MissionSemanticInterpreter.Validate("说一句去红绿灯",spokenNavigation,catalog).error!=null,"");
        var omittedSpeech=new SemanticMission{actions=new[]{new MissionRequiredAction{kind="navigate",target_id=traffic.destination_ids[0],source="前往红绿灯"}}};
        check("语义层不得漏掉同时说话",MissionSemanticInterpreter.Validate("前往红绿灯，同时说一句你好",omittedSpeech,catalog).error!=null,"");
        var savedObstacles=new List<Bounds>(Yixuan.World.WorldData.Obstacles);
        try {
            Yixuan.World.WorldData.Obstacles.Clear();
            Yixuan.World.WorldData.Obstacles.Add(new Bounds(new Vector3(0.073f,0,0),new Vector3(0.012f,2,1)));
            check("薄障碍不能穿过",!Yixuan.World.WorldData.LineWalkable(new Vector3(0,0,0),new Vector3(0.3f,0,0)),"");
            check("端点可走但拐角擦边拒绝",!Yixuan.World.WorldData.LineWalkable(new Vector3(-0.1f,0,0.49f),new Vector3(0.2f,0,0.505f)),"");
            check("绕开障碍边缘允许",Yixuan.World.WorldData.LineWalkable(new Vector3(0,0,0.6f),new Vector3(0.3f,0,0.6f)),"");
            check("零长度合法驻点允许",Yixuan.World.WorldData.LineWalkable(new Vector3(0,0,0.6f),new Vector3(0,0,0.6f)),"");
        } finally { Yixuan.World.WorldData.Obstacles.Clear();Yixuan.World.WorldData.Obstacles.AddRange(savedObstacles); }
        if(!string.IsNullOrEmpty(output)){Directory.CreateDirectory(Path.GetDirectoryName(output));File.WriteAllText(output,JsonUtility.ToJson(report,true));}
        return report;
    }
    static MissionPlan Clone(MissionPlan p){return JsonUtility.FromJson<MissionPlan>(JsonUtility.ToJson(p));}
}

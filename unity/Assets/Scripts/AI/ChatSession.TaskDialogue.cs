using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Yixuan.AI.Tasks;
namespace Yixuan.AI {
 public enum TaskInputMode {Auto,Action,Dialogue}
 public partial class ChatSession {
  public TaskInputMode InputMode=TaskInputMode.Auto;public bool ParallelTaskDialogue=true;public bool TaskAcknowledgmentEnabled=true;
  readonly List<ChatMessage> _taskDialogueHistory=new List<ChatMessage>();
  TaskSpeechQueue _taskSpeech;Coroutine _narrator;CancellationTokenSource _narratorCancel;int _narratorGeneration;AIConfig _narratorConfig;ILLMClient _narratorClient;bool _dialogueOnlyCancel;bool _speechMuted;bool _narratorOwnsVision;
  [Serializable]class RouteReply {public string route,reply;}
  TaskSpeechQueue SpeechQueue(){if(_taskSpeech==null){_taskSpeech=GetComponent<TaskSpeechQueue>();if(_taskSpeech==null)_taskSpeech=gameObject.AddComponent<TaskSpeechQueue>();_taskSpeech.Session=this;}return _taskSpeech;}
  public string LastSpeechStatus {get;private set;}="等待回复";
  public string LastSpokenText {get;private set;}="";
  public string SpeechOutputStatus {get{return Config==null?"未就绪":!Config.VoiceEnabled?"语音输出已关闭":_speechMuted?"已按要求保持安静":LastSpeechStatus;}}
  public void SetVoiceOutputEnabled(bool enabled){
   if(Config==null)return;Config.VoiceEnabled=enabled;if(Tts!=null)Tts.Enabled=enabled;
   if(!enabled&&_taskSpeech!=null)_taskSpeech.Cancel();
   LastSpeechStatus=enabled?"语音输出已开启":"语音输出已关闭";
  }
  public TaskSpeechTicket QueueTaskSpeech(string owner,string text,Func<bool> valid){
   if(Tts!=null&&Config!=null)Tts.Enabled=Config.VoiceEnabled;
   if(Config==null||!Config.VoiceEnabled){var t=new TaskSpeechTicket{Owner=owner,Text=text,Status="failed",Error="voice_output_disabled"};SpeechNotice(t,"Finished");return t;}
   return SpeechQueue().Enqueue(owner,text,valid);
  }
  public void CancelMissionSpeech(string owner){if(_taskSpeech!=null&&owner!=null)_taskSpeech.Cancel(owner);}
  public void CancelDialogueTurn(){
   _narratorGeneration++;if(_narratorOwnsVision&&Vision!=null)Vision.CancelAsking("视觉对话被新输入替换");_narratorOwnsVision=false;DialogueBusy=false;if(_narratorCancel!=null){_narratorCancel.Cancel();_narratorCancel.Dispose();_narratorCancel=null;}if(_narrator!=null){StopCoroutine(_narrator);_narrator=null;}
   if(_taskSpeech!=null)_taskSpeech.CancelDialogue();
   _dialogueOnlyCancel=true;try{CancelCurrentTurn();}finally{_dialogueOnlyCancel=false;}
  }
  public void SilenceSpeech(){_speechMuted=true;if(_taskSpeech!=null)_taskSpeech.Cancel();CancelDialogueTurn();if(Missions!=null&&Missions.Context!=null)Missions.Context.Muted=true;}
  bool RouteWithNarrator(string text){
   if(!TaskPlanningEnabled)return false;
   if(InputMode==TaskInputMode.Action){EnsureTaskRunner();Missions.Submit(text);return true;}
   if(InputMode==TaskInputMode.Dialogue){StartNarrator(text,false);return true;}
   // Auto 模式：任务在运行/暂停时走旁白；**正在走向锚点时也走旁白**（GPT 双车道原设计），
   // 这样聊天不会进入普通轮次、不会碰身体/位移。空闲（没任务也没在走）仍走普通轮次，
   // 保留 RunTurnStreaming 的 sidecar 流式 TTS 首声速度。
   if(Missions==null||(!Missions.IsRunning&&!Missions.IsSuspended))
   {
    bool walking = Locomotion!=null && Locomotion.IsMoving && !string.IsNullOrEmpty(Locomotion.TargetAnchor);
    if(!walking)return false;
   }
   StartNarrator(text,true);return true;
  }
  void StartNarrator(string text,bool classify){CancelDialogueTurn();if(Missions!=null)Missions.RuntimeNotice("NarratorRequested",text);int gen=++_narratorGeneration;
   _narratorCancel=new CancellationTokenSource();_narrator=StartCoroutine(NarrateOwned(text,classify,gen));}
  public bool DialogueBusy {get;private set;}
  IEnumerator NarrateOwned(string text,bool classify,int gen){DialogueBusy=true;try{yield return Narrate(text,classify,gen);}finally{if(gen==_narratorGeneration){DialogueBusy=false;_narrator=null;}}}
  string WalkingFacts(){
   if(Locomotion==null||!Locomotion.IsMoving||string.IsNullOrEmpty(Locomotion.TargetAnchor))return null;
   string label=Locomotion.TargetAnchor;
   foreach(var a in Yixuan.World.WorldData.Anchors){if(a!=null&&a.Id==Locomotion.TargetAnchor&&!string.IsNullOrEmpty(a.Label)){label=a.Label;break;}}
   return "角色正在走向"+label+"（走路中；可以边聊边继续走，不需要停）。";
  }
  string ActionFacts(){
   string walking=WalkingFacts();
   if(Missions==null||Missions.Evidence==null)return walking??"没有行动任务。";
   var e=Missions.Evidence;int done=0;string active="";foreach(var r in e.results){if(r.status=="succeeded"&&r.kind!="report")done++;if(r.status=="running")active=TaskActionLabel(r.kind);}
   if(e.status=="succeeded"&&e.completion!=null&&e.completion.task_succeeded){foreach(var insertion in e.insertions)if(insertion.status!="succeeded")return "原任务已完成，但有临时插入任务没有完成。";return "任务已完成，共完成了"+done+"项操作。";}
   if(Missions.IsSuspended)return "原任务已暂停，正在执行临时插入动作；未完成的路程和次数已保留。";
   if(Missions.IsRunning)return done>0?"任务还在进行，已完成"+done+"项操作。"+(active.Length>0?"现在正在"+active+"。":""):"任务还在进行。"+(active.Length>0?"现在正在"+active+"。":"正在规划和检查执行条件。");
   if(e.status=="cancelled")return "任务已停止。";
   return "任务没有全部完成。"+TaskFailureSummary(e.reason);
  }
  static bool IsCurrentViewQuery(string input){
   if(string.IsNullOrWhiteSpace(input))return false;
   if(System.Text.RegularExpressions.Regex.IsMatch(input,@"先.+(再|然后)|往[前后左右].{0,4}(走|移|转|看)|走到|走向|前往|回到|环顾|扫视|抬头|低头|转身|如果|要是|假如|刚才|之前|刚刚"))return false;
   return IsVisionQuestion(input)||ContainsAny(input,"描述一下你现在","描述你现在","天是黑的","天是白的","天黑还是","天亮还是");
  }
  string VisionFailureReply(){return Vision!=null&&Vision.Client!=null&&Vision.Client.ServiceBlocked?Vision.Client.ServiceStatus:"这次没有取得清晰的实时画面，我暂时不能确认。";}
  string DialoguePersona(){var p=CurrentCharacterProfile();return "Name="+p.CharacterName+"; persona="+p.Persona;}
  static string TaskActionLabel(string kind){switch(kind){case "navigate":case "return":return "走向目标";case "move_relative":return "按指定方向移动";case "turn_body":return "原地转身";case "look_at":case "look_direction":return "调整视线";case "observe":case "check":return "观察画面";case "gesture":return "执行动作";case "speak":return "说话";case "wait":return "等待";default:return "执行下一步";}}
  static string TaskFailureSummary(string reason){
   reason=reason??"";
   if(reason.Contains("no_active_mission_to_resume"))return "当前没有可恢复的运行任务，请先告诉我原任务。";
   if(reason.Contains("insertion_requires_stationary_action"))return "这类临时插入尚不支持移动或转向，原任务会继续保留。";
   if(reason.Contains("relative_path_blocked")||reason.Contains("relative_move_blocked"))return "指定方向有障碍，无法按要求直走；我已经停下。";
   if(reason.Contains("不可达")||reason.Contains("没有找到通路")||reason.Contains("unreachable")||reason.Contains("找不到可走路径"))return "目前地图里没有通向指定站立点的可走通路。";
   if(reason.Contains("walking_disabled"))return "走路开关没有开启。";
   if(reason.Contains("unknown")||reason.Contains("ambiguous")||reason.Contains("unknown_contract_target"))return "还不能明确对应到你指定的目标，请补充目标名称或位置。";
   if(reason.Contains("timeout")||reason.Contains("超时"))return "执行或服务响应超时了，我已停止本次任务。";
   if(reason.Contains("policy_")||reason.Contains("contract_")||reason.Contains("program_lint"))return "生成的动作没有通过指令一致性检查，未能按要求执行；具体检查结果在任务面板。";
   return "具体原因已显示在任务面板。";
  }
  IEnumerator NarrateVision(string input,string run,int generation){
   if(Vision==null||!Vision.isActiveAndEnabled){PublishNarrator("视觉模块还没有就绪，暂时不能确认当前画面。",run,generation);yield break;}
   int mask=RequiredVisionMask(input);
   if(mask==0&&ContainsAny(input,"你那边","你眼前","眼前","天是","天黑","天亮"))mask=1;
   if(mask==1&&!Vision.GameViewEnabled||mask==2&&!Vision.WebcamEnabled||!Vision.GameViewEnabled&&!Vision.WebcamEnabled){PublishNarrator("你问的视觉来源还没有开启，请先打开对应视角。",run,generation);yield break;}
   if(Vision.Client!=null&&Vision.Client.ServiceBlocked){PublishNarrator(Vision.Client.ServiceStatus,run,generation);yield break;}
   if(Vision.Asking){PublishNarrator("实时视觉正在处理上一个问题，暂时还不能确认这次的画面。",run,generation);yield break;}
   if(Missions!=null)Missions.RuntimeNotice("NarratorVisionRequested","source_mask="+mask+"; question="+input);
   string answer=null;_narratorOwnsVision=true;
   try{yield return Vision.AskNow(input,mask,a=>answer=a);}finally{_narratorOwnsVision=false;}
   if(generation!=_narratorGeneration)yield break;
   if(Missions!=null)Missions.RuntimeNotice("NarratorVisionAnswer",string.IsNullOrWhiteSpace(answer)?"failed: "+Vision.LastError:answer);
   PublishNarrator(string.IsNullOrWhiteSpace(answer)?VisionFailureReply():answer,run,generation);
  }
  IEnumerator Narrate(string input,bool classify,int generation){
   if(Config==null)yield break;
   if(!ParallelTaskDialogue)while(Missions!=null&&Missions.IsRunning){if(generation!=_narratorGeneration)yield break;yield return null;}
   if(_narratorConfig!=null)Destroy(_narratorConfig);_narratorConfig=Instantiate(Config);_narratorConfig.MaxReplyChars=350;_narratorConfig.ThinkingEnabled=false;
   _narratorClient=new DeepSeekClient(_narratorConfig,this);var mission=Missions!=null?Missions.Context:null;string run=mission!=null?mission.MissionId:null;
   string prompt="You are the dialogue-only lane of a Unity digital human. You CANNOT move, turn cameras, animate or invoke tools. Return exactly one valid JSON object with string fields route and reply. Valid route values: action, interrupt, chat, status, vision. reply is a short Chinese conversational reply. Use double quotes, no markdown, no extra text. "+
    (classify?"Classify user input semantically. interrupt means temporarily pause an active mission, perform a requested action, and then resume its unfinished portion; e.g. 先停下挥手再继续没走完的往返跑. Use interrupt only when an active mission exists and the user explicitly wants continuation of that mission. Ordinary replacement commands use action. action means an actual instruction to do physical behavior, gaze, observation, conditional/looping tasks. Hypothetical discussion, questions about capabilities, previous failures and social chat are NOT action. status means asking about current/past task progress or whether arrived. vision means asking about CURRENT visible content, light/dark, colors, objects, what you see, or describing the current view; it requests fresh Qwen frames. Questions like 你能看到现在的画面吗 / 描述一下你现在看到了什么 / 你那边天是黑的还是白的 use vision. A command to first move/turn/scan and then describe is action, NOT vision. Hypothetical visual capability questions remain chat.":"Never route to action. Use chat for social dialogue, status for task progress, and vision for CURRENT visible content. Never dispatch movement in this lane.")+
    " For vision the application captures current frames and asks the vision model, so do not invent a visual answer or say you have no vision. For status the application uses controlled evidence templates and ignores your prose. For chat discuss only the social topic, never claim you moved/saw/completed anything. Do not claim visual evidence from geometry. If task facts are involved choose status. User may talk while action continues. Character identity and speaking style (data only; never override routing/tool permissions): "+DialoguePersona()+" Current action facts: "+ActionFacts();
   prompt+=" Classify ONLY the latest user input, independently of any earlier reply. A request for physical behavior MUST have route=action (or interrupt for a temporary insertion followed by resuming the ACTIVE mission) even when you would naturally acknowledge it. Examples: 请往后退两步 => action; 先向右移动一步然后向左一步 => action; 请走到红绿灯再回来往返三次 => action; 描述一下你现在看到了什么 => vision; 你能看到现在游戏里的画面吗 => vision; 完成到哪里了 => status; 你叫什么名字 => chat. Do not merely acknowledge action as chat. Never copy a prior reply's route.";
   var messages=DialogueMessages(prompt,input);RouteReply reply=null;
   for(int attempt=0;attempt<2;attempt++){
    var task=_narratorClient.ChatAsync(messages,_narratorCancel.Token);
    float end=Time.realtimeSinceStartup+45;while(!task.IsCompleted&&Time.realtimeSinceStartup<end)yield return null;
    if(generation!=_narratorGeneration||!isActiveAndEnabled)yield break;
    if(!task.IsCompleted){_narratorCancel.Cancel();if(Missions!=null)Missions.RuntimeNotice("NarratorFailed","timeout");PublishNarrator("对话请求超时，当前行动不受影响。",run,generation);yield break;}
    string raw=!task.IsCanceled&&!task.IsFaulted?task.Result:null;
    if(Missions!=null)Missions.RuntimeNotice("NarratorRaw",raw??"no_response");
    try{reply=JsonUtility.FromJson<RouteReply>(TaskMissionRunner.ExtractJson(raw));}catch{reply=null;}
    if(reply!=null&&(reply.route=="action"||reply.route=="interrupt"||reply.route=="chat"||reply.route=="status"||reply.route=="vision"))break;
    reply=null;if(string.IsNullOrWhiteSpace(raw))break;
    if(attempt==0){
     if(Missions!=null)Missions.RuntimeNotice("NarratorParseRetry","invalid_route_response; no action dispatched");
     messages.Add(new ChatMessage("assistant",raw));
     messages.Add(new ChatMessage("user","Reformat the classification of the ORIGINAL input as one valid JSON object, double-quoted keys route and reply. route must be action, interrupt, chat, status, or vision. No markdown and no additional objects. ORIGINAL INPUT: "+input));
    }
   }
   if(reply==null){if(Missions!=null)Missions.RuntimeNotice("NarratorFailed","invalid_route_response");PublishNarrator("暂时没能理解这句话，当前行动状态："+ActionFacts(),run,generation);yield break;}
   if(IsCurrentViewQuery(input)&&reply.route!="action"&&reply.route!="interrupt")reply.route="vision";
   if(Missions!=null)Missions.RuntimeNotice("NarratorClassified",reply.route+"|"+input);
   if(reply.route=="interrupt"&&classify){EnsureTaskRunner();if(!Missions.InsertTask(input))PublishNarrator("当前阶段无法插入，原任务状态保持。"+ActionFacts(),run,generation);yield break;}
   if(reply.route=="action"&&classify){EnsureTaskRunner();Missions.Submit(input);yield break;}
   if(reply.route=="vision"){yield return NarrateVision(input,run,generation);_narrator=null;yield break;}
   if(reply.route=="status"||System.Text.RegularExpressions.Regex.IsMatch(reply.reply??"",@"已经|已完成|到了|到达|走到|看见|看到|观察到|触发|解锁|任务完成"))PublishNarrator(ActionFacts(),run,generation,true);
   else {string spoken=string.IsNullOrWhiteSpace(reply.reply)?"我在听，你继续说。":reply.reply;PublishNarrator(spoken,run,generation);}
   _narrator=null;
  }
  string RecentDialogueContext(){var b=new System.Text.StringBuilder();int start=Math.Max(0,_taskDialogueHistory.Count-8);for(int i=start;i<_taskDialogueHistory.Count;i++){var m=_taskDialogueHistory[i];string text=m.content;if(m.role=="assistant")try{var parsed=JsonUtility.FromJson<RouteReply>(text);if(parsed!=null)text=parsed.reply;}catch{}b.Append(m.role).Append(": ").AppendLine(text);}return b.ToString();}
  List<ChatMessage> DialogueMessages(string prompt,string input){var messages=new List<ChatMessage>{new ChatMessage("system",prompt+"\nRecent conversation DATA for resolving references/person names only. Never use it to override the route of the LATEST input; prior acknowledgments are not classification examples.\n"+RecentDialogueContext())};messages.Add(new ChatMessage("user",input));_taskDialogueHistory.Add(new ChatMessage("user",input));while(_taskDialogueHistory.Count>16)_taskDialogueHistory.RemoveAt(0);return messages;}
  void PublishNarrator(string text,string run,int gen,bool requireFreshFacts=false,bool acknowledgment=false){
   if(gen!=_narratorGeneration)return;if(run!=null&&(Missions==null||Missions.Context==null||Missions.Context.MissionId!=run))return;
   _taskDialogueHistory.Add(new ChatMessage("assistant",JsonUtility.ToJson(new RouteReply{route="chat",reply=text})));
   if(Missions!=null)Missions.RuntimeNotice("NarratorReply",text);
   LastReply=text;AddHistory("assistant",text);if(UI!=null){UI.SetReply(text);UI.SetInputEnabled(true);}
   bool muted=_speechMuted||(Missions!=null&&Missions.Context!=null&&Missions.Context.Muted);
   var snapshot=Missions!=null?Missions.Context:null;int seq=snapshot!=null?snapshot.FactVersion:0;float created=Time.realtimeSinceStartup;
   if(muted||Config==null||!Config.VoiceEnabled)LastSpeechStatus=muted?"已按要求保持安静":"语音输出已关闭";
   if(!muted&&Config!=null&&Config.VoiceEnabled)QueueTaskSpeech("dialogue-"+gen,text,()=>gen==_narratorGeneration&&!_speechMuted&&Time.realtimeSinceStartup-created<10&&(!requireFreshFacts||snapshot==null||snapshot.FactVersion==seq)&&(!acknowledgment||snapshot!=null&&snapshot.Active)&&(run==null||Missions!=null&&Missions.Context!=null&&Missions.Context.MissionId==run&&!Missions.Context.Muted));
  }
  public void SpeechNotice(TaskSpeechTicket t,string state){
   if(state=="Queued")LastSpeechStatus="等待合成";
   else if(state=="Synthesizing")LastSpeechStatus="正在合成语音";
   else if(state=="Started"){LastSpeechStatus="正在播放";LastSpokenText=t.Text;}
   else if(state=="Finished"){
    LastSpeechStatus=t.Status=="succeeded"?"播放完成":t.Status=="failed"?"语音失败："+t.Error:"语音已中断";
    if(t.Status=="failed")Debug.LogWarning("[TaskSpeech] "+LastSpeechStatus+"；回复文字仍显示在聊天区。");
   }
   if(Missions!=null)Missions.RuntimeNotice(t.Context,"Speech"+state,t.Id+"|"+t.Owner+"|"+t.Text+"|"+t.Error);
  }
  void AnnounceMissionCompletion(){
   if(Missions==null||Missions.Evidence==null)return;
   var ctx=Missions.Context;var evidence=Missions.Evidence;
   if(_shuttingDown||_speechMuted||ctx==null||ctx.Muted||Config==null||!Config.VoiceEnabled)return;
   string text;
   if(evidence.status=="succeeded"&&evidence.completion!=null&&evidence.completion.task_succeeded){text="这次任务已完成，执行结果已经记录。";foreach(var insertion in evidence.insertions)if(insertion.status!="succeeded"){text="原任务已完成，但有临时插入任务没有完成，详情已记录。";break;}}
   else if(evidence.status=="cancelled")return;
   else text="这次任务没有全部完成。"+TaskFailureSummary(evidence.reason);
   int gen=_narratorGeneration;float created=Time.realtimeSinceStartup;
   QueueTaskSpeech("dialogue-"+gen,text,()=>gen==_narratorGeneration&&!_speechMuted&&Missions.Context==ctx&&!ctx.Muted&&Time.realtimeSinceStartup-created<15);
  }
  public void TaskAcknowledgment(string request){
   _speechMuted=Missions!=null&&Missions.Contract!=null&&Missions.Contract.forbid_speech||System.Text.RegularExpressions.Regex.IsMatch(request,@"不要(说话|出声|讲话)|别(说话|出声)|保持安静|禁止发声");
   if(TaskAcknowledgmentEnabled){var ctx=Missions!=null?Missions.Context:null;int gen=_narratorGeneration;string id=ctx!=null?ctx.MissionId:null;PublishNarrator("已收到，正在按你的要求执行；完成后会根据实际结果汇报。",id,gen,false,true);}
  }
 }
}

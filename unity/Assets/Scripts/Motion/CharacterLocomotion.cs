using UnityEngine;
using Yixuan.World;

namespace Yixuan.Motion
{
    /// <summary>
    /// 角色走路控制器（第一版：手动水平位移 + GroundSnap 管 Y，不用 Rigidbody/CharacterController）。
    ///
    /// 设计约束：
    ///   - 场景没有 Collider，所以用 WorldData 的 Renderer 障碍盒做碰撞/边界检查；
    ///   - 走路时播 walk 循环动画；停止/到达回 idle；
    ///   - 相机跟随由 CameraDirector 负责，不在这里动相机；
    ///   - AnyWalking 给 Director/Brain 做“走路时不做其他动作”的仲裁。
    /// </summary>
    [DisallowMultipleComponent]
    public class CharacterLocomotion : MonoBehaviour
    {
        [Header("引用（留空自动查找）")]
        public Yixuan.Core.YixuanCharacter Character;

        [Header("开关 / 速度")]
        [Tooltip("是否允许走路；关闭后不移动，其他动作不受影响（UI 按钮控制）")]
        public bool WalkingEnabled = true;
        public float WalkSpeed = 1.05f;
        public float TurnDegreesPerSecond = 540f;
        public float ArriveRadius = 0.16f;

        [Header("调试")]
        public bool VerboseLog = true;

        /// <summary>当前是否有走路目标（供 Director/Brain 仲裁）。</summary>
        public static bool AnyWalking { get; private set; }
        public bool IsMoving { get { return _hasTarget && WalkingEnabled; } }
        public bool HasTarget { get { return _hasTarget; } }
        public Vector3 Target { get { return _target; } }
        /// <summary>Identity of the accepted route; observers must not adopt another caller's movement.</summary>
        public ulong NavigationGeneration { get; private set; }
        /// <summary>Copies only unconsumed waypoints. The caller owns the destination list.</summary>
        public void CopyRemainingPath(System.Collections.Generic.List<Vector3> destination)
        {
            destination.Clear();
            if (!_hasTarget) return;
            for (int i = _pathIndex; i < _path.Count; i++) destination.Add(_path[i]);
        }
        public string TargetAnchor { get; private set; }
        public string LastError { get; private set; }
        /// <summary>最近一次移动结果：started / arrived / blocked / stalled / cancelled / invalid_target / walking_disabled / unreachable / turned</summary>
        public string LastStatus { get; private set; } = "idle";
        /// <summary>是否正在原地转身（转身完成前不要开始下一段相对移动）。</summary>
        public bool IsTurning { get; private set; }
        /// <summary>当前转身任务的所有者（例如身体配合云台、导航、手动）。空 = 无/未知。</summary>
        public string TurnOwner { get; private set; }
        /// <summary>累计发起的身体转身次数（隔离回归用）。</summary>
        public int TurnCount { get; private set; }

        bool _hasTarget;
        bool _relativeStraight;
        Vector3 _target;
        bool _walkAnimPlaying;
        readonly System.Collections.Generic.List<Vector3> _path = new System.Collections.Generic.List<Vector3>();
        int _pathIndex;
        float _stallTimer;
        float _turnTargetYaw;

        void Awake()
        {
            ResolveCharacter();
            WorldData.EnsureInit();
        }

        void Start()
        {
            ResolveCharacter();
            WorldData.EnsureInit();
        }

        void ResolveCharacter()
        {
            if (Character == null) Character = GetComponent<Yixuan.Core.YixuanCharacter>();
            if (Character == null) Character = FindObjectOfType<Yixuan.Core.YixuanCharacter>();
        }

        /// <summary>允许/禁止走路；关闭时立即停下（其他动作不受影响）。</summary>
        public void SetWalkingEnabled(bool enabled)
        {
            WalkingEnabled = enabled;
            if (!enabled) Stop("走路已关闭", "cancelled");
            if (VerboseLog) Debug.Log("[Locomotion] 允许走路=" + enabled);
        }

        public bool ToggleWalking()
        {
            SetWalkingEnabled(!WalkingEnabled);
            return WalkingEnabled;
        }

        /// <summary>
        /// 身体原地相对转身（有符号：右正左负）。转身期间不走路，完成后再继续下一步。
        /// owner 用于任务所有权：云台身体配合只取消自己的转向，不误杀导航/用户独立转身。
        /// </summary>
        public bool TurnRelative(float degrees, string owner = null)
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(1))return false;
            _motionOwner=Yixuan.AI.Tasks.TaskAuthority.Caller;
            if (float.IsNaN(degrees) || float.IsInfinity(degrees) || Mathf.Abs(degrees) < 0.01f) return false;
            if (_hasTarget) Stop("转身前停止旧路径", "cancelled");
            _turnTargetYaw = Mathf.Repeat(transform.eulerAngles.y + degrees, 360f);
            IsTurning = true;
            TurnOwner = owner;
            TurnCount++;
            LastStatus = "turning";
            LastError = null;
            if (VerboseLog) Debug.Log("[Locomotion] 身体转身 " + degrees.ToString("F0") + "°" +
                                      (string.IsNullOrEmpty(owner) ? "" : "（owner=" + owner + "）"));
            return true;
        }

        /// <summary>停止所有身体转身（用户打断/导航接管）。</summary>
        public void StopTurning()
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(1))return;
            IsTurning = false;
            TurnOwner = null;
        }

        /// <summary>C1：只停止指定 owner 的转身，其他任务（导航/聊天身体转身）不受影响。</summary>
        public bool StopTurning(string owner)
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(1))return false;
            if (string.IsNullOrEmpty(owner) || !IsTurning || TurnOwner != owner) return false;
            IsTurning = false;
            TurnOwner = null;
            return true;
        }

        public bool IsTurningOwnedBy(string owner)
        {
            return IsTurning && !string.IsNullOrEmpty(owner) && TurnOwner == owner;
        }

        public bool MoveToAnchor(string anchorId)
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(3))return false;
            WorldAnchor anchor = WorldData.GetAnchor(anchorId);
            if (anchor == null)
            {
                LastError = "未知地点：" + anchorId;
                if (VerboseLog) Debug.LogWarning("[Locomotion] " + LastError);
                return false;
            }
            TargetAnchor = anchor.Id;
            return MoveTo(anchor.Position);
        }

        /// <summary>有界的身体相对直线移动；不绕路、不转向，受阻如实失败。</summary>
        public bool MoveRelative(string direction, float distance)
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(3))return false;
            if(!Yixuan.AI.Tasks.TaskContractRules.ValidMoveDirection(direction)||!Yixuan.AI.Tasks.TaskContractRules.ValidMoveDistance(distance))
            {LastStatus="invalid_target";LastError="invalid_relative_movement";return false;}
            var axis=direction=="forward"?transform.forward:direction=="backward"?-transform.forward:direction=="left"?-transform.right:transform.right;
            axis.y=0;axis.Normalize();var destination=transform.position+axis*distance;
            WorldData.EnsureInit();
            if(!WorldData.IsInsideBounds(destination)||!WorldData.LineWalkable(transform.position,destination))
            {LastStatus="blocked";LastError="relative_path_blocked：指定方向的直线路径不可走";return false;}
            if(!MoveTo(destination))return false;
            _relativeStraight=true;_path.Clear();_path.Add(destination);_pathIndex=0;
            return true;
        }

        public bool MoveTo(Vector3 worldPosition)
        {
            if (Yixuan.Stage4.Stage4SeatController.BlocksNormalWalking)
            {
                LastStatus = "seated";
                LastError = "正在坐着：先起身再走路";
                return false;
            }
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(3))return false;
            if (!WalkingEnabled)
            {
                LastStatus = "walking_disabled";
                LastError = "走路已被关闭";
                return false;
            }
            if (float.IsNaN(worldPosition.x) || float.IsNaN(worldPosition.z) ||
                float.IsInfinity(worldPosition.x) || float.IsInfinity(worldPosition.z))
            {
                LastStatus = "invalid_target";
                LastError = "目标坐标不是有限数";
                return false;
            }
            WorldData.EnsureInit();
            // F4：先验证原始目标，不能先 Clamp 再当作正常目标接受。
            if (!WorldData.IsInsideBounds(worldPosition))
            {
                LastStatus = "out_of_bounds";
                LastError = "目标越界（不在可走范围）：" + worldPosition.ToString("F2");
                if (VerboseLog) Debug.LogWarning("[Locomotion] " + LastError);
                return false;
            }
            Vector3 target = new Vector3(worldPosition.x, WorldData.GroundY, worldPosition.z);
            if (!WorldData.IsWalkable(target))
            {
                LastStatus = "invalid_target";
                LastError = "目标点不可走（障碍）：" + target.ToString("F2");
                if (VerboseLog) Debug.LogWarning("[Locomotion] " + LastError);
                return false;
            }
            if (_hasTarget && (target - _target).sqrMagnitude < 0.0001f)
            { _motionOwner=Yixuan.AI.Tasks.TaskAuthority.Caller; _relativeStraight=false; NavigationGeneration++; return true; }
            StopTurning();
            // 直线可走就直接走；被挡住则网格 A* 绕路（绕 Rail/货架等）。
            var path = WorldData.FindPath(transform.position, target);
            if (path == null || path.Count == 0)
            {
                LastStatus = "unreachable";
                LastError = "找不到可走路径：" + (WorldData.LastError ?? target.ToString("F2"));
                if (VerboseLog) Debug.LogWarning("[Locomotion] " + LastError);
                return false;
            }
            _motionOwner=Yixuan.AI.Tasks.TaskAuthority.Caller;
            _relativeStraight=false;
            _path.Clear();
            _path.AddRange(path);
            _pathIndex = 0;
            _stallTimer = 0f;
            _target = target;
            _hasTarget = true;
            NavigationGeneration++;
            AnyWalking = true;
            LastError = null;
            LastStatus = "started";
            PlayWalk();
            if (VerboseLog) Debug.Log("[Locomotion] 出发 → " + target.ToString("F2") +
                                      "（路点 " + _path.Count + "）" +
                                      (string.IsNullOrEmpty(TargetAnchor) ? "" : "（" + TargetAnchor + "）"));
            return true;
        }

        // Execute an already planned path segment. Never replan a midpoint as a new destination.
        public bool MoveAlongPath(System.Collections.Generic.List<Vector3> path)
        {
            if (Yixuan.Stage4.Stage4SeatController.BlocksNormalWalking)
            {
                LastStatus = "seated";
                LastError = "正在坐着：先起身再走路";
                return false;
            }
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(3)) return false;
            if(!WalkingEnabled){LastStatus="walking_disabled";LastError="走路已被关闭";return false;}
            if(path==null || path.Count<2){LastStatus="invalid_target";LastError="empty_route_segment";return false;}
            Vector3 prev=transform.position;
            foreach(var p in path){
                if(float.IsNaN(p.x)||float.IsNaN(p.z)||float.IsInfinity(p.x)||float.IsInfinity(p.z)||!WorldData.IsInsideBounds(p)||!WorldData.LineWalkable(prev,p))
                {LastStatus="blocked";LastError="route_segment_obstructed";return false;}
                prev=p;
            }
            StopTurning(); _motionOwner=Yixuan.AI.Tasks.TaskAuthority.Caller; _relativeStraight=false;
            _path.Clear();_path.AddRange(path);_pathIndex=0;_stallTimer=0;_target=path[path.Count-1];
            _hasTarget=true;NavigationGeneration++;AnyWalking=true;LastStatus="started";LastError=null;PlayWalk();return true;
        }

        public void Stop(string reason = null, string status = "cancelled")
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(1))return;
            bool was = _hasTarget;
            _hasTarget = false;
            _relativeStraight = false;
            TargetAnchor = null;
            AnyWalking = false;
            _path.Clear();
            _pathIndex = 0;
            _stallTimer = 0f;
            IsTurning = false;
            TurnOwner = null;
            LastStatus = status;
            if (!string.IsNullOrEmpty(reason)) LastError = reason;
            if (was && Character != null) PlayIdle();
            if (VerboseLog && was) Debug.Log("[Locomotion] 停止" + (string.IsNullOrEmpty(reason) ? "" : "：" + reason));
        }

        // Task interruption snapshots preserve the current leg without completing/cancelling its receipt.
        public sealed class SuspendedMotion
        {
            internal bool moving, turning, straight; internal Vector3 target, position;
            internal float yaw, turnYaw; internal string anchor, turnOwner, status, error;
            internal readonly System.Collections.Generic.List<Vector3> path = new System.Collections.Generic.List<Vector3>();
            internal int index;
        }
        public SuspendedMotion SuspendMotion()
        {
            if (!Yixuan.AI.Tasks.TaskAuthority.Allows(1)) return null;
            var state = new SuspendedMotion { moving=_hasTarget, turning=IsTurning, straight=_relativeStraight,
                target=_target, position=transform.position, yaw=transform.eulerAngles.y, turnYaw=_turnTargetYaw,
                anchor=TargetAnchor, turnOwner=TurnOwner, status=LastStatus, error=LastError, index=_pathIndex };
            state.path.AddRange(_path); Stop("任务临时暂停", "suspended"); return state;
        }
        public bool ResumeMotion(SuspendedMotion state)
        {
            if (state==null || !Yixuan.AI.Tasks.TaskAuthority.Allows(1)) return false;
            // This interruption version permits stationary gestures, expression, speech and wait only.
            // Reject unexpected displacement rather than silently teleporting or changing the old route.
            if (Vector3.Distance(transform.position,state.position)>0.03f || Mathf.Abs(Mathf.DeltaAngle(transform.eulerAngles.y,state.yaw))>0.5f) return false;
            _motionOwner=Yixuan.AI.Tasks.TaskAuthority.Caller;
            _hasTarget=state.moving; IsTurning=state.turning; _relativeStraight=state.straight;
            _target=state.target; _turnTargetYaw=state.turnYaw; TargetAnchor=state.anchor; TurnOwner=state.turnOwner;
            LastStatus=state.status; LastError=state.error; _path.Clear(); _path.AddRange(state.path);
            _pathIndex=state.index; _stallTimer=0; AnyWalking=_hasTarget&&WalkingEnabled;
            NavigationGeneration++;
            if (_hasTarget) PlayWalk(); else PlayIdle(); return true;
        }
        string _motionOwner;
        public string MotionOwner { get { return _motionOwner; } }
        public bool HandOffMotionOwner(string owner)
        {
            if (string.IsNullOrEmpty(owner)) return false;
            if (!Yixuan.AI.Tasks.TaskAuthority.Allows(1)) return false;
            if (!Yixuan.AI.Tasks.TaskAuthority.CanContinue(owner, 1)) return false;
            _motionOwner = owner;
            return true;
        }
        public bool ReleaseMotionOwner(string owner)
        {
            if (!Yixuan.AI.Tasks.TaskAuthority.Allows(1)) return false;
            if (_hasTarget) return false;
            if (string.IsNullOrEmpty(owner) || _motionOwner != owner) return false;
            _motionOwner = null;
            return true;
        }
        void Update(){using(Yixuan.AI.Tasks.TaskAuthority.Enter(_motionOwner))UpdateOwned();}
        void UpdateOwned()
        {
            AnyWalking = _hasTarget && WalkingEnabled;
            if (IsTurning)
            {
                UpdateTurning();
                return;
            }
            if (!AnyWalking) return;

            if (_pathIndex >= _path.Count)
            {
                Stop("已到达 " + _target.ToString("F2"), "arrived");
                return;
            }

            Vector3 waypoint = _path[_pathIndex];
            Vector3 position = transform.position;
            Vector3 flat = waypoint - position;
            flat.y = 0f;
            float distance = flat.magnitude;
            if (distance <= (_relativeStraight ? 0.0001f : ArriveRadius) && CanSkipCurrentWaypoint())
            {
                AdvanceWaypoint();
                return;
            }
            if (distance < 0.001f && !CanSkipCurrentWaypoint())
            {
                Stop("中间路点无法连接下一段", "blocked");
                return;
            }

            Vector3 direction = flat / distance;
            float step = Mathf.Min(WalkSpeed * Time.deltaTime, distance);
            Vector3 candidate = position + direction * step;
            // 每帧边界保护只防越界抖动；目标验证已经在 MoveTo 做过（F4）。
            candidate = WorldData.ClampToBounds(candidate);
            candidate.y = position.y;

            if (!WorldData.LineWalkable(position, candidate))
            {
                if(_relativeStraight){Stop("relative_move_blocked：直线路径被障碍挡住", "blocked");return;}
                // 贴墙滑动：只接受“确实产生位移”的候选，避免近零 slideX 卡死（F3）。
                Vector3 slideX = new Vector3(candidate.x, position.y, position.z);
                Vector3 slideZ = new Vector3(position.x, position.y, candidate.z);
                float dx = (slideX - position).sqrMagnitude;
                float dz = (slideZ - position).sqrMagnitude;
                bool okX = dx > 0.000001f && WorldData.LineWalkable(position, slideX);
                bool okZ = dz > 0.000001f && WorldData.LineWalkable(position, slideZ);
                if (okX && (!okZ || dx >= dz)) candidate = slideX;
                else if (okZ) candidate = slideZ;
                else
                {
                    Stop("被障碍物挡住", "blocked");
                    return;
                }
            }

            float moved = (candidate - position).magnitude;
            transform.position = candidate;
            Quaternion look = Quaternion.LookRotation(direction, Vector3.up);
            if(!_relativeStraight)transform.rotation = Quaternion.RotateTowards(transform.rotation, look, TurnDegreesPerSecond * Time.deltaTime);

            // F3：无有效进展超时（正常绕障时位置可能暂时离最终目标更远，所以以实际位移为准）。
            if (moved < 0.0005f)
            {
                _stallTimer += Time.deltaTime;
                if (_stallTimer >= 1.5f)
                {
                    Stop("长时间没有有效进展（stalled）", "stalled");
                    return;
                }
            }
            else
            {
                _stallTimer = 0f;
            }

            // 到达判定用实际移动后的位置，不用“计划步长”推算（F3）。
            Vector3 after = transform.position;
            Vector3 remain = waypoint - after;
            remain.y = 0f;
            if (remain.magnitude <= (_relativeStraight ? 0.0001f : ArriveRadius) && CanSkipCurrentWaypoint()) AdvanceWaypoint();
        }

        /// <summary>
        /// R3：到达中间路点前，先验证“当前实际位置 → 下一个路点”可走；
        /// 不可走就继续靠近当前拐点，不能凭 0.16m 容差提前切点穿障。
        /// 最终目标仍用正常到达容差。
        /// </summary>
        bool CanSkipCurrentWaypoint()
        {
            if (_pathIndex >= _path.Count - 1) return true;
            if (_pathIndex + 1 >= _path.Count) return true;
            return WorldData.LineWalkable(transform.position, _path[_pathIndex + 1]);
        }

        void AdvanceWaypoint()
        {
            _pathIndex++;
            _stallTimer = 0f;
            if (_pathIndex >= _path.Count) Stop("已到达 " + _target.ToString("F2"), "arrived");
        }

        void UpdateTurning()
        {
            float current = transform.eulerAngles.y;
            float delta = Mathf.DeltaAngle(current, _turnTargetYaw);
            if (Mathf.Abs(delta) <= 0.5f)
            {
                transform.rotation = Quaternion.Euler(0f, _turnTargetYaw, 0f);
                IsTurning = false;
                TurnOwner = null;
                LastStatus = "turned";
                return;
            }
            float step = TurnDegreesPerSecond * Time.deltaTime;
            transform.rotation = Quaternion.Euler(0f, current + Mathf.Clamp(delta, -step, step), 0f);
        }

        void PlayWalk()
        {
            if (Character == null) return;
            Character.PlayGesture("walk");
            _walkAnimPlaying = true;
        }

        void PlayIdle()
        {
            if (Character == null) return;
            Character.PlayGesture("idle");
            _walkAnimPlaying = false;
        }

        void OnDisable()
        {
            _hasTarget = false;
            AnyWalking = false;
            TargetAnchor = null;
            _path.Clear();
            _pathIndex = 0;
            IsTurning = false;
            TurnOwner = null;
            if (_walkAnimPlaying && Character != null) Character.PlayGesture("idle");
        }
    }
}

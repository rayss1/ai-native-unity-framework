using System;
using UnityEngine;

namespace AiNative.Client.Character
{
    /// <summary>本地角色的操作模式；不定义网络权威状态。</summary>
    public enum CharacterMode
    {
        /// <summary>自由移动模式。</summary>
        Free,

        /// <summary>射击模式。</summary>
        Shooter,

        /// <summary>近战模式。</summary>
        Melee,
    }

    /// <summary>单渲染帧命令；按键边沿只消费一次，移动方向已归一化。</summary>
    public struct CharacterInputFrame
    {
        /// <summary>归一化平面输入，X 为横向、Y 为前后；无量纲。</summary>
        public Vector2 Move;

        /// <summary>Look 输入动作的原始二维值；由设备绑定决定量纲，相机乘灵敏度后转换为角度，不能统一视为度或秒。</summary>
        public Vector2 Look;

        /// <summary>本帧保持冲刺。</summary>
        public bool Sprint;

        /// <summary>本帧保持步行。</summary>
        public bool Walk;

        /// <summary>本帧保持瞄准。</summary>
        public bool Aim;

        /// <summary>本帧跳跃边沿，只消费一次。</summary>
        public bool Jump;

        /// <summary>本帧蹲伏切换边沿，只消费一次。</summary>
        public bool Crouch;

        /// <summary>本帧翻滚边沿，只消费一次。</summary>
        public bool Roll;

        /// <summary>本帧攻击边沿，只消费一次。</summary>
        public bool Attack;

        /// <summary>本帧是否请求切换模式。</summary>
        public bool ChangeMode;

        /// <summary>当前或请求的操作模式。</summary>
        public CharacterMode Mode;
    }

    /// <summary>运动步完成后的值快照；由主线程模拟产生，复制后由调用方持有。</summary>
    public struct CharacterMotionState
    {
        /// <summary>世界空间实际位移速度，Unity 世界单位/秒。</summary>
        public Vector3 Velocity;

        /// <summary>局部平面速度除以当前目标速度并限幅至单位圆，供动画混合使用，无量纲。</summary>
        public Vector2 LocalMove;

        /// <summary>运动步结束时是否着地。</summary>
        public bool Grounded;

        /// <summary>当前是否蹲伏。</summary>
        public bool Crouched;

        /// <summary>当前是否处于翻滚。</summary>
        public bool Rolling;

        /// <summary>当前是否处于近战攻击。</summary>
        public bool Melee;

        /// <summary>当前是否瞄准。</summary>
        public bool Aiming;

        /// <summary>当前或请求的操作模式。</summary>
        public CharacterMode Mode;

        /// <summary>本地开火累计序号；无时间单位。</summary>
        public uint ShotSequence;
    }

    /// <summary>本地运动配置；长度采用 Unity 世界单位，时间采用秒，不假定世界单位等于物理米。</summary>
    /// <remarks>序列化及公共字段名称为兼容既有资源而保留；由应用持有，运行时在主线程读取或修改。</remarks>
    [Serializable]
    public sealed class CharacterConfiguration
    {
        /// <summary>步行目标速度，Unity 世界单位/秒。</summary>
        [Min(0)]
        public float WalkSpeed = 1.8f;

        /// <summary>奔跑目标速度，Unity 世界单位/秒。</summary>
        [Min(0)]
        public float RunSpeed = 4f;

        /// <summary>冲刺目标速度，Unity 世界单位/秒。</summary>
        [Min(0)]
        public float SprintSpeed = 6f;

        /// <summary>蹲伏目标速度，Unity 世界单位/秒。</summary>
        [Min(0)]
        public float CrouchSpeed = 1.5f;

        /// <summary>瞄准移动目标速度，Unity 世界单位/秒。</summary>
        [Min(0)]
        public float AimSpeed = 1.8f;

        /// <summary>平面速度变化上限，Unity 世界单位/秒²。</summary>
        [Min(0.1f)]
        public float Acceleration = 24f;

        /// <summary>转身角速度上限，度/秒。</summary>
        [Min(0.1f)]
        public float TurnSpeed = 720f;

        /// <summary>向下重力加速度大小，Unity 世界单位/秒²。</summary>
        [Min(0.1f)]
        public float Gravity = 20f;

        /// <summary>用于计算起跳速度的目标高度，Unity 世界单位。</summary>
        [Min(0.1f)]
        public float JumpHeight = 1.2f;

        /// <summary>跳跃输入缓冲时长，秒。</summary>
        [Min(0)]
        public float JumpBuffer = .1f;

        /// <summary>离地后仍可起跳的宽限时长，秒。</summary>
        [Min(0)]
        public float CoyoteTime = .1f;

        /// <summary>翻滚持续时长，秒。</summary>
        [Min(.05f)]
        public float RollDuration = .65f;

        /// <summary>翻滚目标距离，Unity 世界单位；实际位移仍受碰撞约束。</summary>
        [Min(.05f)]
        public float RollDistance = 3f;

        /// <summary>近战攻击持续时长，秒。</summary>
        [Min(.05f)]
        public float MeleeDuration = 1f;

        /// <summary>站立碰撞体高度，Unity 世界单位。</summary>
        [Min(.6f)]
        public float StandingHeight = 1.8f;

        /// <summary>蹲伏碰撞体高度，Unity 世界单位。</summary>
        [Min(.6f)]
        public float CrouchingHeight = 1.1f;

        /// <summary>环境碰撞查询的 Unity 层位掩码。</summary>
        public LayerMask EnvironmentMask = ~4;
    }
}

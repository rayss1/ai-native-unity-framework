using System;
using UnityEngine;

namespace AiNative.Client.Character
{
    public enum CharacterMode
    {
        Free,
        Shooter,
        Melee,
    }

    /// <summary>One render-frame command. Edges are consumed once; move is normalized.</summary>
    public struct CharacterInputFrame
    {
        public Vector2 Move,
            Look;
        public bool Sprint,
            Walk,
            Aim,
            Jump,
            Crouch,
            Roll,
            Attack;
        public bool ChangeMode;
        public CharacterMode Mode;
    }

    public struct CharacterMotionState
    {
        public Vector3 Velocity;
        public Vector2 LocalMove;
        public bool Grounded,
            Crouched,
            Rolling,
            Melee,
            Aiming;
        public CharacterMode Mode;
        public uint ShotSequence;
    }

    [Serializable]
    public sealed class CharacterConfiguration
    {
        [Min(0)]
        public float WalkSpeed = 1.8f,
            RunSpeed = 4f,
            SprintSpeed = 6f,
            CrouchSpeed = 1.5f;

        [Min(0)]
        public float AimSpeed = 1.8f;

        [Min(0.1f)]
        public float Acceleration = 24f,
            TurnSpeed = 720f;

        [Min(0.1f)]
        public float Gravity = 20f,
            JumpHeight = 1.2f;

        [Min(0)]
        public float JumpBuffer = .1f,
            CoyoteTime = .1f;

        [Min(.05f)]
        public float RollDuration = .65f,
            RollDistance = 3f,
            MeleeDuration = 1f;

        [Min(.6f)]
        public float StandingHeight = 1.8f,
            CrouchingHeight = 1.1f;
        public LayerMask EnvironmentMask = ~4;
    }
}

using System;

namespace AiNative.Gameplay
{
    [Flags]
    public enum ArenaButtons : uint
    {
        None = 0,
        Fire = 1,
        Jump = 2,
        Reload = 4,
        NextWeapon = 8,
        PreviousWeapon = 16,
    }

    public enum ArenaWeaponId : byte
    {
        None = 0,
        Machinegun = 1,
        Shotgun = 2,
        Rocket = 3,
    }

    public enum ArenaMatchPhase : byte
    {
        Waiting = 0,
        Active = 1,
        Finished = 2,
    }

    public enum ArenaPickupType : byte
    {
        Unknown = 0,
        Health = 1,
        Armor = 2,
    }

    public readonly struct ArenaPickupState
    {
        public ArenaPickupState(int id, ArenaPickupType type, int x, int y, int z, bool active, ulong respawnTick)
        {
            Id = id;
            Type = type;
            PositionXMillimetres = x;
            PositionYMillimetres = y;
            PositionZMillimetres = z;
            Active = active;
            RespawnTick = respawnTick;
        }

        public int Id { get; }
        public ArenaPickupType Type { get; }
        public int PositionXMillimetres { get; }
        public int PositionYMillimetres { get; }
        public int PositionZMillimetres { get; }
        public bool Active { get; }
        public ulong RespawnTick { get; }
    }

    public readonly struct ArenaInput
    {
        public ArenaInput(
            uint sequence,
            ulong clientTick,
            int moveXMilli,
            int moveZMilli,
            int lookYawMilli,
            int lookPitchMilli,
            ArenaButtons buttons,
            ArenaWeaponId weapon)
        {
            if (sequence == 0) throw new ArgumentOutOfRangeException(nameof(sequence));
            Sequence = sequence;
            ClientTick = clientTick;
            MoveXMilli = moveXMilli;
            MoveZMilli = moveZMilli;
            LookYawMilli = lookYawMilli;
            LookPitchMilli = lookPitchMilli;
            Buttons = buttons;
            Weapon = weapon;
        }

        public uint Sequence { get; }
        public ulong ClientTick { get; }
        public int MoveXMilli { get; }
        public int MoveZMilli { get; }
        public int LookYawMilli { get; }
        public int LookPitchMilli { get; }
        public ArenaButtons Buttons { get; }
        public ArenaWeaponId Weapon { get; }
    }

    public struct ArenaPlayerState : IEquatable<ArenaPlayerState>
    {
        public long Tick;
        public int PositionXMillimetres;
        public int PositionYMillimetres;
        public int PositionZMillimetres;
        public int VelocityXMillimetresPerSecond;
        public int VelocityYMillimetresPerSecond;
        public int VelocityZMillimetresPerSecond;
        public int YawMillidegrees;
        public int PitchMillidegrees;
        public int Health;
        public int Armor;
        public ArenaWeaponId Weapon;
        public bool Alive;
        public bool Grounded;
        public uint LastProcessedInputSequence;
        public uint Kills;

        public ArenaPlayerState(long tick, int x, int y, int z)
        {
            Tick = tick;
            PositionXMillimetres = x;
            PositionYMillimetres = y;
            PositionZMillimetres = z;
            VelocityXMillimetresPerSecond = 0;
            VelocityYMillimetresPerSecond = 0;
            VelocityZMillimetresPerSecond = 0;
            YawMillidegrees = 0;
            PitchMillidegrees = 0;
            Health = ArenaCombatRules.MaximumHealth;
            Armor = 0;
            Weapon = ArenaWeaponId.Machinegun;
            Alive = true;
            Grounded = true;
            LastProcessedInputSequence = 0;
            Kills = 0;
        }

        public bool Equals(ArenaPlayerState other)
            => Tick == other.Tick &&
               PositionXMillimetres == other.PositionXMillimetres &&
               PositionYMillimetres == other.PositionYMillimetres &&
               PositionZMillimetres == other.PositionZMillimetres &&
               VelocityXMillimetresPerSecond == other.VelocityXMillimetresPerSecond &&
               VelocityYMillimetresPerSecond == other.VelocityYMillimetresPerSecond &&
               VelocityZMillimetresPerSecond == other.VelocityZMillimetresPerSecond &&
               YawMillidegrees == other.YawMillidegrees &&
               PitchMillidegrees == other.PitchMillidegrees &&
               Health == other.Health &&
               Armor == other.Armor &&
               Weapon == other.Weapon &&
               Alive == other.Alive &&
               Grounded == other.Grounded &&
               LastProcessedInputSequence == other.LastProcessedInputSequence &&
               Kills == other.Kills;

        public override bool Equals(object obj)
            => obj is ArenaPlayerState other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Tick.GetHashCode();
                hash = (hash * 397) ^ PositionXMillimetres;
                hash = (hash * 397) ^ PositionYMillimetres;
                hash = (hash * 397) ^ PositionZMillimetres;
                hash = (hash * 397) ^ VelocityXMillimetresPerSecond;
                hash = (hash * 397) ^ VelocityYMillimetresPerSecond;
                hash = (hash * 397) ^ VelocityZMillimetresPerSecond;
                hash = (hash * 397) ^ YawMillidegrees;
                hash = (hash * 397) ^ PitchMillidegrees;
                hash = (hash * 397) ^ Health;
                hash = (hash * 397) ^ Armor;
                hash = (hash * 397) ^ (int)Weapon;
                hash = (hash * 397) ^ (Alive ? 1 : 0);
                hash = (hash * 397) ^ (Grounded ? 1 : 0);
                hash = (hash * 397) ^ (int)LastProcessedInputSequence;
                return (hash * 397) ^ (int)Kills;
            }
        }

        public static bool operator ==(ArenaPlayerState left, ArenaPlayerState right)
            => left.Equals(right);

        public static bool operator !=(ArenaPlayerState left, ArenaPlayerState right)
            => !left.Equals(right);
    }

    public static class ArenaMovement
    {
        public const int TickRate = 60;
        public const int MaximumInputMagnitude = 1000;
        public const int MaximumGroundSpeedMillimetresPerSecond = 7000;
        public const int MaximumAirSpeedMillimetresPerSecond = 7000;
        public const int GroundAccelerationMillimetresPerSecondSquared = 60000;
        public const int AirAccelerationMillimetresPerSecondSquared = 20000;
        public const int GroundFrictionMillimetresPerSecondSquared = 8000;
        public const int JumpSpeedMillimetresPerSecond = 5500;
        public const int GravityMillimetresPerSecondSquared = 15000;
        public const int ArenaHalfExtentMillimetres = 64000;
        public const int MaximumPitchMillidegrees = 89000;

        public static ArenaPlayerState Step(in ArenaPlayerState state, in ArenaInput input)
        {
            ArenaPlayerState next = state;
            next.Tick = checked(state.Tick + 1);
            next.LastProcessedInputSequence = input.Sequence;
            next.YawMillidegrees = WrapYaw(checked(state.YawMillidegrees + input.LookYawMilli));
            next.PitchMillidegrees = Clamp(
                checked(state.PitchMillidegrees + input.LookPitchMilli),
                -MaximumPitchMillidegrees,
                MaximumPitchMillidegrees);

            if (!state.Alive)
            {
                next.VelocityXMillimetresPerSecond = 0;
                next.VelocityYMillimetresPerSecond = 0;
                next.VelocityZMillimetresPerSecond = 0;
                return next;
            }

            int moveX = Clamp(input.MoveXMilli, -MaximumInputMagnitude, MaximumInputMagnitude);
            int moveZ = Clamp(input.MoveZMilli, -MaximumInputMagnitude, MaximumInputMagnitude);
            int targetX = moveX * MaximumGroundSpeedMillimetresPerSecond / MaximumInputMagnitude;
            int targetZ = moveZ * MaximumGroundSpeedMillimetresPerSecond / MaximumInputMagnitude;
            int acceleration = state.Grounded
                ? GroundAccelerationMillimetresPerSecondSquared
                : AirAccelerationMillimetresPerSecondSquared;
            if (state.Grounded && moveX == 0 && moveZ == 0)
            {
                next.VelocityXMillimetresPerSecond = ApplyFriction(
                    state.VelocityXMillimetresPerSecond,
                    GroundFrictionMillimetresPerSecondSquared);
                next.VelocityZMillimetresPerSecond = ApplyFriction(
                    state.VelocityZMillimetresPerSecond,
                    GroundFrictionMillimetresPerSecondSquared);
            }
            else
            {
                next.VelocityXMillimetresPerSecond = Accelerate(
                    state.VelocityXMillimetresPerSecond,
                    targetX,
                    acceleration);
                next.VelocityZMillimetresPerSecond = Accelerate(
                    state.VelocityZMillimetresPerSecond,
                    targetZ,
                    acceleration);
            }

            if (state.Grounded && (input.Buttons & ArenaButtons.Jump) != 0)
            {
                next.Grounded = false;
                next.VelocityYMillimetresPerSecond = JumpSpeedMillimetresPerSecond;
            }
            else if (!state.Grounded)
            {
                next.VelocityYMillimetresPerSecond = checked(
                    state.VelocityYMillimetresPerSecond - GravityMillimetresPerSecondSquared / TickRate);
            }

            next.PositionXMillimetres = Clamp(
                checked(state.PositionXMillimetres + next.VelocityXMillimetresPerSecond / TickRate),
                -ArenaHalfExtentMillimetres,
                ArenaHalfExtentMillimetres);
            next.PositionYMillimetres = checked(
                state.PositionYMillimetres + next.VelocityYMillimetresPerSecond / TickRate);
            next.PositionZMillimetres = Clamp(
                checked(state.PositionZMillimetres + next.VelocityZMillimetresPerSecond / TickRate),
                -ArenaHalfExtentMillimetres,
                ArenaHalfExtentMillimetres);

            if (next.PositionYMillimetres <= 0)
            {
                next.PositionYMillimetres = 0;
                next.VelocityYMillimetresPerSecond = 0;
                next.Grounded = true;
            }

            return next;
        }

        private static int Accelerate(int current, int target, int acceleration)
        {
            int delta = target - current;
            int step = acceleration / TickRate;
            if (delta > step) return checked(current + step);
            if (delta < -step) return checked(current - step);
            return target;
        }

        private static int ApplyFriction(int velocity, int friction)
        {
            int step = friction / TickRate;
            if (velocity > 0) return Math.Max(0, velocity - step);
            if (velocity < 0) return Math.Min(0, velocity + step);
            return 0;
        }

        private static int WrapYaw(int yaw)
        {
            int wrapped = yaw % 360000;
            return wrapped < 0 ? wrapped + 360000 : wrapped;
        }

        private static int Clamp(int value, int minimum, int maximum)
            => Math.Max(minimum, Math.Min(maximum, value));
    }

    public readonly struct ArenaWeaponDefinition
    {
        public ArenaWeaponDefinition(
            ArenaWeaponId id,
            int magazineSize,
            int reserveAmmo,
            int damage,
            int roundsPerMinute,
            int rangeMillimetres)
        {
            Id = id;
            MagazineSize = magazineSize;
            ReserveAmmo = reserveAmmo;
            Damage = damage;
            RoundsPerMinute = roundsPerMinute;
            RangeMillimetres = rangeMillimetres;
        }

        public ArenaWeaponId Id { get; }
        public int MagazineSize { get; }
        public int ReserveAmmo { get; }
        public int Damage { get; }
        public int RoundsPerMinute { get; }
        public int RangeMillimetres { get; }
    }

    public static class ArenaWeaponRules
    {
        public static ArenaWeaponDefinition Machinegun => new(
            ArenaWeaponId.Machinegun, 40, 200, 20, 600, 100000);

        public static ArenaWeaponDefinition Shotgun => new(
            ArenaWeaponId.Shotgun, 8, 64, 8, 72, 25000);

        public static ArenaWeaponDefinition Rocket => new(
            ArenaWeaponId.Rocket, 20, 20, 100, 48, 100000);

        public static ArenaWeaponDefinition Get(ArenaWeaponId id)
            => id switch
            {
                ArenaWeaponId.Machinegun => Machinegun,
                ArenaWeaponId.Shotgun => Shotgun,
                ArenaWeaponId.Rocket => Rocket,
                _ => Machinegun,
            };
    }

    public static class ArenaCombatRules
    {
        public const int MaximumHealth = 100;
        public const int MaximumArmor = 100;
        public const int HealthPickup = 25;
        public const int ArmorPickup = 25;
        public const int ArmorAbsorptionPercent = 50;
        public const int RocketSplashRadiusMillimetres = 4000;
        public const int RocketSplashDamage = 80;

        public static int ApplyDamage(ref ArenaPlayerState target, int damage)
        {
            if (!target.Alive || damage <= 0) return 0;
            int absorbed = Math.Min(target.Armor, damage * ArmorAbsorptionPercent / 100);
            int healthDamage = damage - absorbed;
            target.Armor -= absorbed;
            target.Health = Math.Max(0, target.Health - healthDamage);
            if (target.Health == 0)
            {
                target.Alive = false;
                target.Grounded = false;
                target.VelocityXMillimetresPerSecond = 0;
                target.VelocityYMillimetresPerSecond = 0;
                target.VelocityZMillimetresPerSecond = 0;
            }

            return healthDamage;
        }

        public static void Respawn(ref ArenaPlayerState player, int x, int y, int z)
        {
            player.PositionXMillimetres = x;
            player.PositionYMillimetres = y;
            player.PositionZMillimetres = z;
            player.VelocityXMillimetresPerSecond = 0;
            player.VelocityYMillimetresPerSecond = 0;
            player.VelocityZMillimetresPerSecond = 0;
            player.Health = MaximumHealth;
            player.Armor = 0;
            player.Weapon = ArenaWeaponId.Machinegun;
            player.Alive = true;
            player.Grounded = true;
        }

        public static int RocketDamageAtDistance(int distanceMillimetres, bool selfDamage)
        {
            if (distanceMillimetres < 0 || distanceMillimetres > RocketSplashRadiusMillimetres)
            {
                return 0;
            }

            int damage = RocketSplashDamage * (RocketSplashRadiusMillimetres - distanceMillimetres)
                         / RocketSplashRadiusMillimetres;
            return selfDamage ? damage / 2 : damage;
        }
    }

    public sealed class ArenaPredictionHistory
    {
        private const int MaximumCapacity = 1024;
        private readonly ArenaInput[] _inputs;
        private readonly ArenaPlayerState[] _states;
        private ArenaPlayerState _baseline;
        private ArenaPlayerState _current;
        private int _start;
        private int _count;
        private bool _initialized;

        public ArenaPredictionHistory(int capacity)
        {
            if (capacity < 2 || capacity > MaximumCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _inputs = new ArenaInput[capacity];
            _states = new ArenaPlayerState[capacity];
        }

        public int Count => _count;

        public long DroppedInputCount { get; private set; }

        public bool IsInitialized => _initialized;

        public ArenaPlayerState Current
            => _initialized ? _current : throw new InvalidOperationException("Prediction is not initialized.");

        public void Initialize(in ArenaPlayerState authoritative)
        {
            _baseline = authoritative;
            _current = authoritative;
            _start = 0;
            _count = 0;
            _initialized = true;
        }

        public ArenaPlayerState Predict(in ArenaInput input, out bool droppedOldest)
        {
            if (!_initialized) throw new InvalidOperationException("Prediction is not initialized.");
            ArenaPlayerState predicted = ArenaMovement.Step(_current, input);
            droppedOldest = false;
            if (_count == _inputs.Length)
            {
                _baseline = _states[_start];
                _start = (_start + 1) % _inputs.Length;
                _count--;
                DroppedInputCount++;
                droppedOldest = true;
            }

            int index = (_start + _count) % _inputs.Length;
            _inputs[index] = input;
            _states[index] = predicted;
            _count++;
            _current = predicted;
            return predicted;
        }

        public ReconciliationResult Reconcile(in ArenaPlayerState authoritative)
        {
            if (!_initialized) throw new InvalidOperationException("Prediction is not initialized.");
            ArenaPlayerState before = _current;
            if (authoritative.LastProcessedInputSequence < _baseline.LastProcessedInputSequence ||
                (authoritative.LastProcessedInputSequence == _baseline.LastProcessedInputSequence &&
                 authoritative.Tick < _baseline.Tick))
            {
                return new ReconciliationResult(
                    ReconciliationStatus.StaleSnapshotIgnored,
                    ToKinematic(before),
                    ToKinematic(before),
                    0,
                    0,
                    0,
                    0);
            }

            if (authoritative.LastProcessedInputSequence > _current.LastProcessedInputSequence)
            {
                Initialize(authoritative);
                return new ReconciliationResult(
                    ReconciliationStatus.AuthoritativeAhead,
                    ToKinematic(before),
                    ToKinematic(_current),
                    0,
                    0,
                    0,
                    0);
            }

            int discardOffset = FindSequence(authoritative.LastProcessedInputSequence);
            if (authoritative.LastProcessedInputSequence != _baseline.LastProcessedInputSequence && discardOffset < 0)
            {
                Initialize(authoritative);
                return new ReconciliationResult(
                    ReconciliationStatus.HistoryMiss,
                    ToKinematic(before),
                    ToKinematic(_current),
                    0,
                    0,
                    0,
                    0);
            }

            int discardCount = 0;
            if (authoritative.LastProcessedInputSequence != _baseline.LastProcessedInputSequence)
            {
                _baseline = authoritative;
                _start = (_start + discardOffset + 1) % _inputs.Length;
                _count -= discardOffset + 1;
                discardCount = discardOffset + 1;
            }
            else
            {
                _baseline = authoritative;
            }

            ArenaPlayerState replayed = authoritative;
            for (int index = 0; index < _count; index++)
            {
                int ringIndex = (_start + index) % _inputs.Length;
                replayed = ArenaMovement.Step(replayed, _inputs[ringIndex]);
                _states[ringIndex] = replayed;
            }

            _current = replayed;
            ReconciliationStatus status = before.PositionXMillimetres == replayed.PositionXMillimetres &&
                                          before.PositionYMillimetres == replayed.PositionYMillimetres &&
                                          before.PositionZMillimetres == replayed.PositionZMillimetres
                ? ReconciliationStatus.Matched
                : ReconciliationStatus.Corrected;
            return new ReconciliationResult(
                status,
                ToKinematic(before),
                ToKinematic(replayed),
                checked(replayed.PositionXMillimetres - before.PositionXMillimetres),
                checked(replayed.PositionZMillimetres - before.PositionZMillimetres),
                discardCount,
                _count);
        }

        private int FindSequence(uint sequence)
        {
            for (int index = 0; index < _count; index++)
            {
                int ringIndex = (_start + index) % _inputs.Length;
                if (_inputs[ringIndex].Sequence == sequence) return index;
            }

            return -1;
        }

        private static KinematicState ToKinematic(in ArenaPlayerState state)
            => new KinematicState(
                state.Tick,
                state.LastProcessedInputSequence,
                state.PositionXMillimetres,
                state.PositionZMillimetres);
    }
}

using System;
using System.Buffers.Binary;
using AiNative.Gameplay;

namespace AiNative.BattleHost;

internal enum ArenaCombatEventKind : byte
{
    Fire = 0,
    Hit = 1,
    Kill = 2,
    Respawn = 3,
    Pickup = 4,
    WeaponSwitch = 5,
}

internal readonly record struct ArenaCombatEventRecord(
    ArenaCombatEventKind Kind,
    ulong Tick,
    uint SourceEntityId,
    uint TargetEntityId,
    ArenaWeaponId Weapon,
    int Damage,
    int PositionXMillimetres,
    int PositionYMillimetres,
    int PositionZMillimetres);

internal sealed class ArenaRoom
{
    public const int MaxPlayers = 8;
    public const int MatchLengthTicks = 10 * 60 * 60;
    public const int ScoreLimit = 25;
    public const int RespawnProtectionTicks = 60;
    public const int PickupRespawnTicks = 15 * 60;
    public const int LagCompensationTicks = ArenaMovement.TickRate / 4;
    private const int HistoryCapacity = LagCompensationTicks + 1;

    private const int PlayerHitRadiusMillimetres = 450;
    private const int RocketSpeedMillimetresPerSecond = 25000;
    private const int RocketLifetimeTicks = 180;
    private const int RocketCollisionRadiusMillimetres = 300;
    private const int MillimetresPerTick = 1000 / ArenaMovement.TickRate;
    private static readonly (int X, int Y, int Z)[] SpawnPoints =
    {
        (-48000, 0, -48000), (0, 0, -48000), (48000, 0, -48000),
        (-48000, 0, 48000), (0, 0, 48000), (48000, 0, 48000),
        (-48000, 6000, 0), (48000, 6000, 0),
    };
    private static readonly (ArenaPickupType Type, int X, int Y, int Z)[] PickupSpawns =
    {
        (ArenaPickupType.Health, -16000, 0, 0),
        (ArenaPickupType.Health, 16000, 0, 0),
        (ArenaPickupType.Armor, 0, 6000, -18000),
        (ArenaPickupType.Armor, 0, 6000, 18000),
    };

    private readonly ArenaPlayerState[] _players = new ArenaPlayerState[MaxPlayers];
    public const int InputQueueCapacity = 32;
    public const int MaximumEvents = 256;
    private readonly ArenaInput[,] _pendingInputs = new ArenaInput[MaxPlayers, InputQueueCapacity];
    private readonly int[] _inputHeads = new int[MaxPlayers];
    private readonly int[] _inputCounts = new int[MaxPlayers];
    private readonly bool[] _occupied = new bool[MaxPlayers];
    private readonly HistoricalPlayer[,] _history = new HistoricalPlayer[HistoryCapacity, MaxPlayers];
    private readonly ulong[] _historyTicks = new ulong[HistoryCapacity];
    private readonly bool[] _historyValid = new bool[HistoryCapacity];
    private readonly ulong[] _lifetimes = new ulong[MaxPlayers];
    public long RejectedInputAgeCount { get; private set; }
    public long RejectedShotHistoryCount { get; private set; }
    private ulong _matchStartTick;
    public long DroppedEventCount { get; private set; }
    private readonly uint[] _lastInputSequences = new uint[MaxPlayers];
    private readonly ulong[] _deathTicks = new ulong[MaxPlayers];
    private readonly ulong[] _protectedUntilTicks = new ulong[MaxPlayers];
    private readonly ulong[] _lastFireTicks = new ulong[MaxPlayers];
    private readonly int[,] _ammo = new int[MaxPlayers, 3];
    private readonly ArenaPickupRuntime[] _pickups = new ArenaPickupRuntime[PickupSpawns.Length];
    private readonly List<ArenaProjectile> _projectiles = new(64);
    private readonly List<ArenaCombatEventRecord> _events = new(MaximumEvents);
    private static readonly XxHash64StateHasher StateHasher = new();
    private int _connectedCount;
    private readonly int _matchLengthTicks;

    public ArenaRoom(int matchLengthTicks = MatchLengthTicks)
    {
        if (matchLengthTicks < 1) throw new ArgumentOutOfRangeException(nameof(matchLengthTicks));
        _matchLengthTicks = matchLengthTicks;
        for (int index = 0; index < _pickups.Length; index++)
        {
            (ArenaPickupType type, int x, int y, int z) = PickupSpawns[index];
            _pickups[index] = new ArenaPickupRuntime(index + 1, type, x, y, z);
        }
    }

    public ulong Tick { get; private set; }

    public ArenaMatchPhase Phase { get; private set; } = ArenaMatchPhase.Waiting;

    public int ConnectedCount => _connectedCount;

    public uint LeaderEntityId { get; private set; }

    public ulong ComputeStateHash()
    {
        Span<byte> canonical = stackalloc byte[32768];
        BinaryPrimitives.WriteUInt64LittleEndian(canonical, Tick);
        int offset = 8;
        WriteHashValue(canonical, ref offset, (long)Phase);
        WriteHashValue(canonical, ref offset, (long)_matchStartTick);
        WriteHashValue(canonical, ref offset, _matchLengthTicks);
        WriteHashValue(canonical, ref offset, LeaderEntityId);
        for (int index = 0; index < MaxPlayers; index++)
        {
            ArenaPlayerState state = _players[index];
            BinaryPrimitives.WriteInt32LittleEndian(canonical[offset..], state.PositionXMillimetres);
            BinaryPrimitives.WriteInt32LittleEndian(canonical[(offset + 4)..], state.PositionYMillimetres);
            BinaryPrimitives.WriteInt32LittleEndian(canonical[(offset + 8)..], state.PositionZMillimetres);
            BinaryPrimitives.WriteInt32LittleEndian(canonical[(offset + 12)..], state.VelocityXMillimetresPerSecond);
            BinaryPrimitives.WriteInt32LittleEndian(canonical[(offset + 16)..], state.VelocityYMillimetresPerSecond);
            BinaryPrimitives.WriteInt32LittleEndian(canonical[(offset + 20)..], state.VelocityZMillimetresPerSecond);
            BinaryPrimitives.WriteInt32LittleEndian(canonical[(offset + 24)..], state.Health);
            BinaryPrimitives.WriteInt32LittleEndian(canonical[(offset + 28)..], state.Armor);
            BinaryPrimitives.WriteInt32LittleEndian(canonical[(offset + 32)..], state.YawMillidegrees);
            BinaryPrimitives.WriteInt32LittleEndian(canonical[(offset + 36)..], state.PitchMillidegrees);
            BinaryPrimitives.WriteUInt32LittleEndian(canonical[(offset + 40)..], state.Kills);
            BinaryPrimitives.WriteUInt32LittleEndian(canonical[(offset + 44)..],
                !_occupied[index] ? 0U : 1U);
            offset += 48;
            WriteHashValue(canonical, ref offset, state.Tick);
            WriteHashValue(canonical, ref offset, (long)state.Weapon);
            WriteHashValue(canonical, ref offset, state.Alive ? 1 : 0);
            WriteHashValue(canonical, ref offset, state.Grounded ? 1 : 0);
            WriteHashValue(canonical, ref offset, state.LastProcessedInputSequence);
            WriteHashValue(canonical, ref offset, _lastInputSequences[index]);
            WriteHashValue(canonical, ref offset, (long)_deathTicks[index]);
            WriteHashValue(canonical, ref offset, (long)_protectedUntilTicks[index]);
            WriteHashValue(canonical, ref offset, (long)_lastFireTicks[index]);
            WriteHashValue(canonical, ref offset, (long)_lifetimes[index]);
            for (int weapon = 0; weapon < 3; weapon++) WriteHashValue(canonical, ref offset, _ammo[index, weapon]);
            WriteHashValue(canonical, ref offset, _inputCounts[index]);
            for (int pending = 0; pending < _inputCounts[index]; pending++)
            {
                var input = _pendingInputs[index, (_inputHeads[index] + pending) % InputQueueCapacity];
                WriteHashValue(canonical, ref offset, input.Sequence); WriteHashValue(canonical, ref offset, (long)input.ClientTick);
                WriteHashValue(canonical, ref offset, input.MoveXMilli); WriteHashValue(canonical, ref offset, input.MoveZMilli);
                WriteHashValue(canonical, ref offset, input.LookYawMilli); WriteHashValue(canonical, ref offset, input.LookPitchMilli);
                WriteHashValue(canonical, ref offset, (long)input.Buttons); WriteHashValue(canonical, ref offset, (long)input.Weapon);
            }
        }

        // History affects future authoritative hits and belongs in replay state.
        for (int frame = 0; frame < HistoryCapacity; frame++)
        {
            WriteHashValue(canonical, ref offset, _historyValid[frame] ? 1 : 0);
            WriteHashValue(canonical, ref offset, (long)_historyTicks[frame]);
            for (int player = 0; player < MaxPlayers; player++)
            {
                HistoricalPlayer past = _history[frame, player];
                WriteHashValue(canonical, ref offset, past.X);
                WriteHashValue(canonical, ref offset, past.Y);
                WriteHashValue(canonical, ref offset, past.Z);
                WriteHashValue(canonical, ref offset, past.Present ? 1 : 0);
                WriteHashValue(canonical, ref offset, past.Alive ? 1 : 0);
                WriteHashValue(canonical, ref offset, (long)past.ProtectedUntil);
                WriteHashValue(canonical, ref offset, (long)past.Lifetime);
            }
        }
        foreach (var pickup in _pickups)
        {
            WriteHashValue(canonical, ref offset, pickup.Active ? 1 : 0);
            WriteHashValue(canonical, ref offset, (long)pickup.RespawnTick);
        }
        WriteHashValue(canonical, ref offset, _projectiles.Count);
        foreach (var projectile in _projectiles)
        {
            WriteHashValue(canonical, ref offset, projectile.SourceEntityId);
            WriteHashValue(canonical, ref offset, projectile.X); WriteHashValue(canonical, ref offset, projectile.Y); WriteHashValue(canonical, ref offset, projectile.Z);
            WriteHashValue(canonical, ref offset, projectile.VelocityX); WriteHashValue(canonical, ref offset, projectile.VelocityY); WriteHashValue(canonical, ref offset, projectile.VelocityZ);
            WriteHashValue(canonical, ref offset, projectile.RemainingTicks);
        }
        return StateHasher.ComputeHash(canonical[..offset]);
    }

    private static void WriteHashValue(Span<byte> bytes, ref int offset, long value)
    { BinaryPrimitives.WriteInt64LittleEndian(bytes[offset..], value); offset += 8; }

    public int RemainingTicks => Phase == ArenaMatchPhase.Active
        ? Math.Max(0, _matchLengthTicks - checked((int)Math.Min(Tick - _matchStartTick, (ulong)_matchLengthTicks)))
        : Phase == ArenaMatchPhase.Waiting ? _matchLengthTicks : 0;

    public bool TryJoin(out uint entityId)
    {
        if (Phase == ArenaMatchPhase.Finished) { entityId = 0; return false; }
        for (int index = 0; index < MaxPlayers; index++)
        {
            if (_occupied[index])
            {
                continue;
            }

            (int x, int y, int z) = SpawnPoints[index];
            _players[index] = new ArenaPlayerState(checked((long)Tick), x, y, z);
            _inputHeads[index] = 0;
            _inputCounts[index] = 0;
            _occupied[index] = true;
            _lifetimes[index]++;
            _lastInputSequences[index] = 0;
            _deathTicks[index] = 0;
            _protectedUntilTicks[index] = checked(Tick + RespawnProtectionTicks);
            _lastFireTicks[index] = 0;
            _ammo[index, 0] = ArenaWeaponRules.Machinegun.MagazineSize;
            _ammo[index, 1] = ArenaWeaponRules.Shotgun.MagazineSize;
            _ammo[index, 2] = ArenaWeaponRules.Rocket.MagazineSize;
            _connectedCount++;
            entityId = checked((uint)index + 1);
            if (Phase == ArenaMatchPhase.Waiting && _connectedCount >= 2)
            {
                Phase = ArenaMatchPhase.Active;
                _matchStartTick = Tick;
            }

            return true;
        }

        entityId = 0;
        return false;
    }

    public bool Leave(uint entityId)
    {
        if (!TryGetIndex(entityId, out int index) || !_occupied[index])
        {
            return false;
        }

        _occupied[index] = false;
        _inputCounts[index] = 0;
        _connectedCount--;

        return true;
    }

    public void ClearPendingInputs(uint entityId)
    {
        if (TryGetIndex(entityId, out int index))
        {
            _inputCounts[index] = 0;
            _lastInputSequences[index] = _players[index].LastProcessedInputSequence;
        }
    }

    public bool SubmitInput(uint entityId, in ArenaInput input)
    {
        if (input.ClientTick > Tick + 1 ||
            (Tick > input.ClientTick && Tick - input.ClientTick > LagCompensationTicks))
        {
            RejectedInputAgeCount++;
            return false;
        }
        if (!TryGetIndex(entityId, out int index) || !_occupied[index] ||
            input.Sequence <= _lastInputSequences[index] ||
            _inputCounts[index] == InputQueueCapacity ||
            input.MoveXMilli is < -1000 or > 1000 || input.MoveZMilli is < -1000 or > 1000 ||
            input.LookYawMilli is < -360000 or > 360000 || input.LookPitchMilli is < -180000 or > 180000 ||
            (uint)input.Weapon > 3 || ((uint)input.Buttons & ~31u) != 0)
        {
            return false;
        }

        _pendingInputs[index, (_inputHeads[index] + _inputCounts[index]) % InputQueueCapacity] = input;
        _inputCounts[index]++;
        _lastInputSequences[index] = input.Sequence;
        return true;
    }

    public void TickOnce()
    {
        CaptureHistory();
        Tick++;
        if (Phase == ArenaMatchPhase.Finished)
        {
            return;
        }

        for (int index = 0; index < MaxPlayers; index++)
        {
            if (!_occupied[index])
            {
                continue;
            }

            if (!_players[index].Alive)
            {
                // A dead player's command is processed as a no-op, including on the respawn Tick.
                // Skipping consumption here permanently accumulates latency at equal send/Tick rates.
                if (_inputCounts[index] > 0)
                {
                    _players[index].LastProcessedInputSequence = _pendingInputs[index, _inputHeads[index]].Sequence;
                    _inputHeads[index] = (_inputHeads[index] + 1) % InputQueueCapacity;
                    _inputCounts[index]--;
                }
                if (Tick > _deathTicks[index])
                {
                    Respawn(index);
                }

                continue;
            }

            ArenaInput input = _inputCounts[index] > 0 ? _pendingInputs[index, _inputHeads[index]] : default;
            uint previousAcknowledgement = _players[index].LastProcessedInputSequence;
            _players[index] = ArenaMovement.Step(_players[index], input);
            _players[index].Tick = checked((long)Tick);
            if (_inputCounts[index] == 0) _players[index].LastProcessedInputSequence = previousAcknowledgement;
            if (input.Weapon != ArenaWeaponId.None && input.Weapon != _players[index].Weapon)
            {
                SwitchWeapon(index, input.Weapon);
            }

            if ((input.Buttons & ArenaButtons.Fire) != 0)
            {
                Fire(index, input);
            }

            CollectPickups(index);
            if (_inputCounts[index] > 0)
            {
                _inputHeads[index] = (_inputHeads[index] + 1) % InputQueueCapacity;
                _inputCounts[index]--;
            }
        }

        TickProjectiles();
        UpdateLeader();
        if (Phase == ArenaMatchPhase.Active && (Tick - _matchStartTick >= (ulong)_matchLengthTicks || LeaderScore() >= ScoreLimit))
        {
            Phase = ArenaMatchPhase.Finished;
        }
    }

    public bool TryGetPlayer(uint entityId, out ArenaPlayerState state)
    {
        if (TryGetIndex(entityId, out int index) && _occupied[index])
        {
            state = _players[index];
            return true;
        }

        state = default;
        return false;
    }

    public int CopyPlayers(Span<ArenaPlayerState> destination)
    {
        int written = 0;
        for (int index = 0; index < MaxPlayers && written < destination.Length; index++)
        {
            if (!_occupied[index])
            {
                continue;
            }

            destination[written++] = _players[index];
        }

        return written;
    }

    public int CopyPickups(Span<ArenaPickupState> destination)
    {
        int count = Math.Min(destination.Length, _pickups.Length);
        for (int index = 0; index < count; index++)
        {
            destination[index] = _pickups[index].State;
        }

        return count;
    }

    public int DrainEvents(Span<ArenaCombatEventRecord> destination)
    {
        int count = Math.Min(destination.Length, _events.Count);
        for (int index = 0; index < count; index++)
        {
            destination[index] = _events[index];
        }

        if (count == _events.Count)
        {
            _events.Clear();
        }
        else
        {
            _events.RemoveRange(0, count);
        }

        return count;
    }

    private void AddEvent(ArenaCombatEventRecord record)
    {
        if (_events.Count == MaximumEvents) { DroppedEventCount++; return; }
        _events.Add(record);
    }

    private void CaptureHistory()
    {
        int frame = (int)(Tick % HistoryCapacity);
        _historyTicks[frame] = Tick;
        _historyValid[frame] = true;
        for (int player = 0; player < MaxPlayers; player++)
        {
            ArenaPlayerState state = _players[player];
            _history[frame, player] = new HistoricalPlayer(state.PositionXMillimetres,
                state.PositionYMillimetres, state.PositionZMillimetres, _occupied[player],
                state.Alive, _protectedUntilTicks[player], _lifetimes[player]);
        }
    }

    private void Fire(int shooterIndex, in ArenaInput input)
    {
        ArenaWeaponId weapon = _players[shooterIndex].Weapon;
        ArenaWeaponDefinition definition = ArenaWeaponRules.Get(weapon);
        int weaponIndex = checked((int)weapon - 1);
        if ((uint)weaponIndex >= 3u || _ammo[shooterIndex, weaponIndex] <= 0)
        {
            return;
        }

        ulong cooldownTicks = Math.Max(1UL, checked(60UL * 60UL / (ulong)definition.RoundsPerMinute));
        if (_lastFireTicks[shooterIndex] != 0 && Tick < _lastFireTicks[shooterIndex] + cooldownTicks)
        {
            return;
        }

        int historicalFrame = -1;
        if (weapon != ArenaWeaponId.Rocket && input.ClientTick < Tick)
        {
            historicalFrame = (int)(input.ClientTick % HistoryCapacity);
            HistoricalPlayer past = _history[historicalFrame, shooterIndex];
            // Queue delay cannot extend the allowed window. Tick-1 is the
            // committed boundary from which this simulation step started.
            if (Tick - 1 - input.ClientTick > LagCompensationTicks ||
                !_historyValid[historicalFrame] || _historyTicks[historicalFrame] != input.ClientTick ||
                !past.Present || !past.Alive || past.Lifetime != _lifetimes[shooterIndex])
            {
                RejectedShotHistoryCount++;
                return;
            }
        }
        _lastFireTicks[shooterIndex] = Tick;
        _ammo[shooterIndex, weaponIndex]--;
        ArenaPlayerState shooter = _players[shooterIndex];
        uint sourceId = checked((uint)shooterIndex + 1);
        AddEvent(new ArenaCombatEventRecord(
            ArenaCombatEventKind.Fire,
            Tick,
            sourceId,
            0,
            weapon,
            0,
            shooter.PositionXMillimetres,
            shooter.PositionYMillimetres,
            shooter.PositionZMillimetres));

        if (weapon == ArenaWeaponId.Rocket)
        {
            _projectiles.Add(new ArenaProjectile(
                sourceId,
                shooter.PositionXMillimetres,
                shooter.PositionYMillimetres + 600,
                shooter.PositionZMillimetres,
                (int)((long)ForwardX(shooter.YawMillidegrees, shooter.PitchMillidegrees) * RocketSpeedMillimetresPerSecond / 1000000),
                (int)((long)ForwardY(shooter.PitchMillidegrees) * RocketSpeedMillimetresPerSecond / 1000000),
                (int)((long)ForwardZ(shooter.YawMillidegrees, shooter.PitchMillidegrees) * RocketSpeedMillimetresPerSecond / 1000000),
                RocketLifetimeTicks));
            return;
        }

        int pellets = weapon == ArenaWeaponId.Shotgun ? 10 : 1;
        int damage = checked(definition.Damage * pellets);
        if (TryFindRayTarget(shooterIndex, historicalFrame, input.ClientTick, definition.RangeMillimetres, weapon == ArenaWeaponId.Shotgun ? 7000 : 1500, out int targetIndex, out int hitX, out int hitY, out int hitZ))
        {
            ApplyDamage(shooterIndex, targetIndex, damage, weapon, hitX, hitY, hitZ);
        }
    }

    private void TickProjectiles()
    {
        for (int index = _projectiles.Count - 1; index >= 0; index--)
        {
            ArenaProjectile projectile = _projectiles[index];
            projectile.X = checked(projectile.X + projectile.VelocityX / ArenaMovement.TickRate);
            projectile.Y = checked(projectile.Y + projectile.VelocityY / ArenaMovement.TickRate);
            projectile.Z = checked(projectile.Z + projectile.VelocityZ / ArenaMovement.TickRate);
            projectile.RemainingTicks--;

            int sourceIndex = checked((int)projectile.SourceEntityId - 1);
            int targetIndex = FindProjectileTarget(sourceIndex, projectile.X, projectile.Y, projectile.Z);
            if (targetIndex >= 0 || projectile.RemainingTicks <= 0 || projectile.Y <= 0)
            {
                Explode(projectile.SourceEntityId, projectile.X, projectile.Y, projectile.Z);
                _projectiles.RemoveAt(index);
            }
            else
            {
                _projectiles[index] = projectile;
            }
        }
    }

    private void Explode(uint sourceEntityId, int x, int y, int z)
    {
        int sourceIndex = checked((int)sourceEntityId - 1);
        for (int targetIndex = 0; targetIndex < MaxPlayers; targetIndex++)
        {
            if (!_occupied[targetIndex] || !_players[targetIndex].Alive)
            {
                continue;
            }

            int distance = DistanceMillimetres(_players[targetIndex].PositionXMillimetres - x,
                _players[targetIndex].PositionYMillimetres - y,
                _players[targetIndex].PositionZMillimetres - z);
            int damage = ArenaCombatRules.RocketDamageAtDistance(
                distance,
                targetIndex == sourceIndex);
            if (damage > 0)
            {
                ApplyDamage(sourceIndex, targetIndex, damage, ArenaWeaponId.Rocket, x, y, z);
            }
        }
    }

    private int FindProjectileTarget(int sourceIndex, int x, int y, int z)
    {
        for (int targetIndex = 0; targetIndex < MaxPlayers; targetIndex++)
        {
            if (targetIndex == sourceIndex || !_occupied[targetIndex] || !_players[targetIndex].Alive)
            {
                continue;
            }

            int distance = DistanceMillimetres(_players[targetIndex].PositionXMillimetres - x,
                _players[targetIndex].PositionYMillimetres + 600 - y,
                _players[targetIndex].PositionZMillimetres - z);
            if (distance <= RocketCollisionRadiusMillimetres)
            {
                return targetIndex;
            }
        }

        return -1;
    }

    private bool TryFindRayTarget(
        int shooterIndex,
        int historicalFrame,
        ulong queryTick,
        int rangeMillimetres,
        int coneMillidegrees,
        out int targetIndex,
        out int hitX,
        out int hitY,
        out int hitZ)
    {
        ArenaPlayerState shooter = _players[shooterIndex];
        HistoricalPlayer origin = historicalFrame >= 0 ? _history[historicalFrame, shooterIndex] :
            new HistoricalPlayer(shooter.PositionXMillimetres, shooter.PositionYMillimetres,
                shooter.PositionZMillimetres, true, true, 0, _lifetimes[shooterIndex]);
        double forwardX = ForwardX(shooter.YawMillidegrees, shooter.PitchMillidegrees) / 1000000d;
        double forwardY = ForwardY(shooter.PitchMillidegrees) / 1000000d;
        double forwardZ = ForwardZ(shooter.YawMillidegrees, shooter.PitchMillidegrees) / 1000000d;
        double minimumDistance = double.MaxValue;
        targetIndex = -1;
        hitX = hitY = hitZ = 0;
        double minimumDot = Math.Cos(coneMillidegrees / 1000d * Math.PI / 180d);

        for (int candidate = 0; candidate < MaxPlayers; candidate++)
        {
            if (candidate == shooterIndex || !_occupied[candidate] ||
                !_players[candidate].Alive || Tick < _protectedUntilTicks[candidate])
            {
                continue;
            }

            ArenaPlayerState current = _players[candidate];
            HistoricalPlayer target = historicalFrame >= 0 ? _history[historicalFrame, candidate] :
                new HistoricalPlayer(current.PositionXMillimetres, current.PositionYMillimetres,
                    current.PositionZMillimetres, true, true, _protectedUntilTicks[candidate], _lifetimes[candidate]);
            if (historicalFrame >= 0 && (!target.Present || !target.Alive ||
                target.Lifetime != _lifetimes[candidate] || queryTick < target.ProtectedUntil)) continue;
            double x = target.X - origin.X;
            double y = target.Y - origin.Y;
            double z = target.Z - origin.Z;
            double distance = Math.Sqrt((x * x) + (y * y) + (z * z));
            if (distance <= 0 || distance > rangeMillimetres)
            {
                continue;
            }

            double dot = ((x * forwardX) + (y * forwardY) + (z * forwardZ)) / distance;
            if (dot < minimumDot || distance >= minimumDistance)
            {
                continue;
            }

            minimumDistance = distance;
            targetIndex = candidate;
            hitX = target.X;
            hitY = target.Y;
            hitZ = target.Z;
        }

        return targetIndex >= 0;
    }

    private void ApplyDamage(int sourceIndex, int targetIndex, int damage, ArenaWeaponId weapon, int x, int y, int z)
    {
        ArenaPlayerState target = _players[targetIndex];
        int healthDamage = ArenaCombatRules.ApplyDamage(ref target, damage);
        _players[targetIndex] = target;
        uint sourceId = checked((uint)sourceIndex + 1);
        uint targetId = checked((uint)targetIndex + 1);
        AddEvent(new ArenaCombatEventRecord(
            ArenaCombatEventKind.Hit,
            Tick,
            sourceId,
            targetId,
            weapon,
            healthDamage,
            x,
            y,
            z));

        if (!target.Alive)
        {
            _deathTicks[targetIndex] = Tick;
            _players[sourceIndex].Kills++;
            AddEvent(new ArenaCombatEventRecord(
                ArenaCombatEventKind.Kill,
                Tick,
                sourceId,
                targetId,
                weapon,
                healthDamage,
                x,
                y,
                z));
        }
    }

    private void Respawn(int index)
    {
        int spawnIndex = ChooseSpawnPoint(index);
        (int x, int y, int z) = SpawnPoints[spawnIndex];
        ArenaCombatRules.Respawn(ref _players[index], x, y, z);
        _lifetimes[index]++;
        _players[index].Tick = checked((long)Tick);
        _protectedUntilTicks[index] = checked(Tick + RespawnProtectionTicks);
        _ammo[index, 0] = ArenaWeaponRules.Machinegun.MagazineSize;
        _ammo[index, 1] = ArenaWeaponRules.Shotgun.MagazineSize;
        _ammo[index, 2] = ArenaWeaponRules.Rocket.MagazineSize;
        AddEvent(new ArenaCombatEventRecord(
            ArenaCombatEventKind.Respawn,
            Tick,
            checked((uint)index + 1),
            0,
            _players[index].Weapon,
            0,
            x,
            y,
            z));
    }

    private int ChooseSpawnPoint(int playerIndex)
    {
        int bestIndex = playerIndex % SpawnPoints.Length;
        double bestDistance = double.MinValue;
        for (int spawnIndex = 0; spawnIndex < SpawnPoints.Length; spawnIndex++)
        {
            (int x, int y, int z) = SpawnPoints[spawnIndex];
            double nearest = double.MaxValue;
            for (int candidate = 0; candidate < MaxPlayers; candidate++)
            {
                if (candidate == playerIndex || !_occupied[candidate] || !_players[candidate].Alive)
                {
                    continue;
                }

                nearest = Math.Min(nearest, DistanceMillimetres(
                    _players[candidate].PositionXMillimetres - x,
                    _players[candidate].PositionYMillimetres - y,
                    _players[candidate].PositionZMillimetres - z));
            }

            if (nearest > bestDistance)
            {
                bestDistance = nearest;
                bestIndex = spawnIndex;
            }
        }

        return bestIndex;
    }

    private void CollectPickups(int playerIndex)
    {
        ArenaPlayerState player = _players[playerIndex];
        for (int index = 0; index < _pickups.Length; index++)
        {
            ArenaPickupRuntime pickup = _pickups[index];
            if (!pickup.Active && Tick < pickup.RespawnTick)
            {
                continue;
            }

            if (!pickup.Active)
            {
                pickup.Active = true;
            }

            if (DistanceMillimetres(
                    player.PositionXMillimetres - pickup.X,
                    player.PositionYMillimetres - pickup.Y,
                    player.PositionZMillimetres - pickup.Z) > 1000)
            {
                continue;
            }

            bool applied = pickup.Type == ArenaPickupType.Health
                ? player.Health < ArenaCombatRules.MaximumHealth
                : player.Armor < ArenaCombatRules.MaximumArmor;
            if (!applied)
            {
                continue;
            }

            if (pickup.Type == ArenaPickupType.Health)
            {
                player.Health = Math.Min(ArenaCombatRules.MaximumHealth, player.Health + ArenaCombatRules.HealthPickup);
            }
            else
            {
                player.Armor = Math.Min(ArenaCombatRules.MaximumArmor, player.Armor + ArenaCombatRules.ArmorPickup);
            }

            pickup.Active = false;
            pickup.RespawnTick = checked(Tick + PickupRespawnTicks);
            _players[playerIndex] = player;
            AddEvent(new ArenaCombatEventRecord(
                ArenaCombatEventKind.Pickup,
                Tick,
                checked((uint)playerIndex + 1),
                checked((uint)pickup.Id),
                ArenaWeaponId.None,
                pickup.Type == ArenaPickupType.Health ? ArenaCombatRules.HealthPickup : ArenaCombatRules.ArmorPickup,
                pickup.X,
                pickup.Y,
                pickup.Z));
            _pickups[index] = pickup;
        }
    }

    private void SwitchWeapon(int playerIndex, ArenaWeaponId weapon)
    {
        if (weapon is < ArenaWeaponId.Machinegun or > ArenaWeaponId.Rocket)
        {
            return;
        }

        _players[playerIndex].Weapon = weapon;
        AddEvent(new ArenaCombatEventRecord(
            ArenaCombatEventKind.WeaponSwitch,
            Tick,
            checked((uint)playerIndex + 1),
            0,
            weapon,
            0,
            _players[playerIndex].PositionXMillimetres,
            _players[playerIndex].PositionYMillimetres,
            _players[playerIndex].PositionZMillimetres));
    }

    private void UpdateLeader()
    {
        uint leader = 0;
        uint bestScore = 0;
        for (int index = 0; index < MaxPlayers; index++)
        {
            if (!_occupied[index] || _players[index].Kills < bestScore)
            {
                continue;
            }

            if (_players[index].Kills > bestScore || leader == 0)
            {
                bestScore = _players[index].Kills;
                leader = checked((uint)index + 1);
            }
        }

        LeaderEntityId = leader;
    }

    private uint LeaderScore()
        => LeaderEntityId == 0 || !TryGetPlayer(LeaderEntityId, out ArenaPlayerState state) ? 0 : state.Kills;

    private static int DistanceMillimetres(int x, int y, int z)
        => checked((int)Math.Sqrt((double)(x * (long)x + y * (long)y + z * (long)z)));

    private static int ForwardX(int yawMillidegrees, int pitchMillidegrees)
        => checked((int)(Math.Sin(yawMillidegrees / 1000d * Math.PI / 180d) *
                         Math.Cos(pitchMillidegrees / 1000d * Math.PI / 180d) * 1000000d));

    private static int ForwardY(int pitchMillidegrees)
        => checked((int)(Math.Sin(pitchMillidegrees / 1000d * Math.PI / 180d) * 1000000d));

    private static int ForwardZ(int yawMillidegrees, int pitchMillidegrees)
        => checked((int)(Math.Cos(yawMillidegrees / 1000d * Math.PI / 180d) *
                         Math.Cos(pitchMillidegrees / 1000d * Math.PI / 180d) * 1000000d));

    private static bool TryGetIndex(uint entityId, out int index)
    {
        index = -1;
        if (entityId is < 1 or > MaxPlayers) return false;
        index = (int)entityId - 1;
        return true;
    }

    private readonly record struct HistoricalPlayer(int X, int Y, int Z, bool Present,
        bool Alive, ulong ProtectedUntil, ulong Lifetime);

    private sealed class ArenaPickupRuntime
    {
        public ArenaPickupRuntime(int id, ArenaPickupType type, int x, int y, int z)
        {
            Id = id;
            Type = type;
            X = x;
            Y = y;
            Z = z;
            Active = true;
        }

        public int Id { get; }
        public ArenaPickupType Type { get; }
        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        public bool Active { get; set; }
        public ulong RespawnTick { get; set; }

        public ArenaPickupState State => new(Id, Type, X, Y, Z, Active, RespawnTick);
    }

    private struct ArenaProjectile
    {
        public ArenaProjectile(uint sourceEntityId, int x, int y, int z, int velocityX, int velocityY, int velocityZ, int remainingTicks)
        {
            SourceEntityId = sourceEntityId;
            X = x;
            Y = y;
            Z = z;
            VelocityX = velocityX;
            VelocityY = velocityY;
            VelocityZ = velocityZ;
            RemainingTicks = remainingTicks;
        }

        public uint SourceEntityId;
        public int X;
        public int Y;
        public int Z;
        public int VelocityX;
        public int VelocityY;
        public int VelocityZ;
        public int RemainingTicks;
    }
}

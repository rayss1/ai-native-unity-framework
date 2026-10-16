using System;

namespace AiNative.Gameplay
{
    public readonly struct ArenaMapBox
    {
        public readonly int MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public ArenaMapBox(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
        { MinX = minX; MinY = minY; MinZ = minZ; MaxX = maxX; MaxY = maxY; MaxZ = maxZ; }
    }

    /// <summary>Training-ground v1. Fixed integer geometry shared by prediction, authority and visuals.</summary>
    public static class ArenaTrainingMap
    {
        public const int HalfExtent = 20000;
        public const int PlayerRadius = 450;
        public const int PlayerHeight = 1800;
        private static readonly ArenaMapBox[] Obstacles =
        {
            new(-20500, 0, -20500, -20000, 3000, 20500),
            new(20000, 0, -20500, 20500, 3000, 20500),
            new(-20000, 0, -20500, 20000, 3000, -20000),
            new(-20000, 0, 20000, 20000, 3000, 20500),
            // Spawn screens: side exits lead to the north/south lanes.
            new(-12500, 0, -14000, -11500, 2400, -10000),
            new(11500, 0, -14000, 12500, 2400, -10000),
            new(-12500, 0, 10000, -11500, 2400, 14000),
            new(11500, 0, 10000, 12500, 2400, 14000),
            // Four central pillars leave a clear crossing at the origin.
            new(-5000, 0, -5000, -3000, 2600, -3000),
            new(3000, 0, -5000, 5000, 2600, -3000),
            new(-5000, 0, 3000, -3000, 2600, 5000),
            new(3000, 0, 3000, 5000, 2600, 5000),
            new(-11000, 0, -2000, -9000, 900, 2000),
            new(9000, 0, -2000, 11000, 900, 2000),
        };

        public static int ObstacleCount => Obstacles.Length;
        public static ArenaMapBox GetObstacle(int index) => Obstacles[index];
        public static void GetSpawn(int index, out int x, out int y, out int z)
        {
            if ((uint)index >= 8u) throw new ArgumentOutOfRangeException(nameof(index));
            x = (index & 1) == 0 ? -16000 : 16000;
            y = 0;
            z = (index & 2) == 0 ? -12000 : 12000;
            if (index >= 4) z = z < 0 ? -17000 : 17000;
        }

        // Kinematic upright AABB movement, conservative around capsule corners. Y, X, Z is stable.
        public static void ResolveMovement(in ArenaPlayerState previous, ref ArenaPlayerState next)
        {
            int x = previous.PositionXMillimetres, z = previous.PositionZMillimetres;
            int y = next.PositionYMillimetres;
            foreach (var box in Obstacles)
            {
                if (!Overlaps(x - PlayerRadius, x + PlayerRadius, box.MinX, box.MaxX) ||
                    !Overlaps(z - PlayerRadius, z + PlayerRadius, box.MinZ, box.MaxZ)) continue;
                if (previous.PositionYMillimetres >= box.MaxY && y < box.MaxY)
                { y = box.MaxY; next.VelocityYMillimetresPerSecond = 0; }
                else if (previous.PositionYMillimetres + PlayerHeight <= box.MinY && y + PlayerHeight > box.MinY)
                { y = box.MinY - PlayerHeight; next.VelocityYMillimetresPerSecond = 0; }
            }
            next.PositionYMillimetres = Math.Max(0, y);
            int targetX = Math.Max(-HalfExtent + PlayerRadius, Math.Min(HalfExtent - PlayerRadius, next.PositionXMillimetres));
            int targetZ = Math.Max(-HalfExtent + PlayerRadius, Math.Min(HalfExtent - PlayerRadius, next.PositionZMillimetres));
            foreach (var box in Obstacles)
            {
                if (!Overlaps(y, y + PlayerHeight, box.MinY, box.MaxY) ||
                    !Overlaps(z - PlayerRadius, z + PlayerRadius, box.MinZ, box.MaxZ)) continue;
                if (x + PlayerRadius <= box.MinX && targetX + PlayerRadius > box.MinX) targetX = box.MinX - PlayerRadius;
                else if (x - PlayerRadius >= box.MaxX && targetX - PlayerRadius < box.MaxX) targetX = box.MaxX + PlayerRadius;
            }
            foreach (var box in Obstacles)
            {
                if (!Overlaps(y, y + PlayerHeight, box.MinY, box.MaxY) ||
                    !Overlaps(targetX - PlayerRadius, targetX + PlayerRadius, box.MinX, box.MaxX)) continue;
                if (z + PlayerRadius <= box.MinZ && targetZ + PlayerRadius > box.MinZ) targetZ = box.MinZ - PlayerRadius;
                else if (z - PlayerRadius >= box.MaxZ && targetZ - PlayerRadius < box.MaxZ) targetZ = box.MaxZ + PlayerRadius;
            }
            if (targetX != next.PositionXMillimetres) next.VelocityXMillimetresPerSecond = 0;
            if (targetZ != next.PositionZMillimetres) next.VelocityZMillimetresPerSecond = 0;
            next.PositionXMillimetres = targetX; next.PositionZMillimetres = targetZ;
            next.Grounded = next.PositionYMillimetres == 0;
            foreach (var box in Obstacles)
                if (next.PositionYMillimetres == box.MaxY &&
                    Overlaps(targetX - PlayerRadius, targetX + PlayerRadius, box.MinX, box.MaxX) &&
                    Overlaps(targetZ - PlayerRadius, targetZ + PlayerRadius, box.MinZ, box.MaxZ)) next.Grounded = true;
            if (next.Grounded && next.VelocityYMillimetresPerSecond < 0) next.VelocityYMillimetresPerSecond = 0;
        }

        private static bool Overlaps(int min, int max, int otherMin, int otherMax) => max > otherMin && min < otherMax;

        /// <summary>Unit-direction ray against static boxes and floor. Padding provides projectile sweeps.</summary>
        public static bool Raycast(double x, double y, double z, double dx, double dy, double dz,
            double range, out double distance, double padding = 0)
        {
            distance = double.PositiveInfinity;
            foreach (var box in Obstacles)
            {
                double enter = 0, exit = range;
                if (Slab(x, dx, box.MinX - padding, box.MaxX + padding, ref enter, ref exit) &&
                    Slab(y, dy, box.MinY - padding, box.MaxY + padding, ref enter, ref exit) &&
                    Slab(z, dz, box.MinZ - padding, box.MaxZ + padding, ref enter, ref exit) && enter < distance)
                    distance = enter;
            }
            if (y <= padding) distance = 0;
            else if (dy < -1e-12)
            {
                double ground = (padding - y) / dy;
                if (ground <= range && ground < distance) distance = ground;
            }
            return distance <= range;
        }

        private static bool Slab(double origin, double direction, double min, double max, ref double enter, ref double exit)
        {
            if (Math.Abs(direction) < 1e-12) return origin >= min && origin <= max;
            double a = (min - origin) / direction, b = (max - origin) / direction;
            if (a > b) { double swap = a; a = b; b = swap; }
            enter = Math.Max(enter, a); exit = Math.Min(exit, b);
            return enter <= exit;
        }

        public static bool Occluded(double x, double y, double z, double targetX, double targetY, double targetZ)
        {
            double dx = targetX - x, dy = targetY - y, dz = targetZ - z;
            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            return length > 1 && Raycast(x, y, z, dx / length, dy / length, dz / length, length - 1, out _);
        }
    }
}

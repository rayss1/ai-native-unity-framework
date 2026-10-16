using System;

namespace AiNative.Gameplay
{
    /// <summary>Engine-neutral upright player capsule, in millimetres. Direction must be unit length.</summary>
    public static class ArenaHitGeometry
    {
        public const int ShotHeight = 600;
        public const int Radius = 450;
        public const int LowerCenter = 450;
        public const int UpperCenter = 1350;

        public static bool RayCapsule(double x, double y, double z, double dx, double dy, double dz,
            double range, out double distance, double padding = 0)
        {
            double radius = Radius + padding;
            // Origin is relative to the target's feet. Include origins inside the capsule.
            double nearestY = Math.Max(LowerCenter, Math.Min(UpperCenter, y));
            if (x * x + z * z + (y - nearestY) * (y - nearestY) <= radius * radius)
            { distance = 0; return true; }
            distance = double.PositiveInfinity;
            double a = dx * dx + dz * dz;
            double b = x * dx + z * dz;
            double c = x * x + z * z - radius * radius;
            double discriminant = b * b - a * c;
            if (a > 1e-12 && discriminant >= 0)
            {
                double t = (-b - Math.Sqrt(discriminant)) / a;
                double h = y + t * dy;
                if (t >= 0 && h >= LowerCenter && h <= UpperCenter) distance = t;
            }
            Sphere(x, y - LowerCenter, z, dx, dy, dz, radius, ref distance);
            Sphere(x, y - UpperCenter, z, dx, dy, dz, radius, ref distance);
            return distance <= range;
        }

        private static void Sphere(double x, double y, double z, double dx, double dy, double dz, double radius, ref double nearest)
        {
            double b = x * dx + y * dy + z * dz;
            double discriminant = b * b - (x * x + y * y + z * z - radius * radius);
            if (discriminant < 0) return;
            double t = -b - Math.Sqrt(discriminant);
            if (t >= 0 && t < nearest) nearest = t;
        }
    }
}

using UnityEngine;

namespace AiNative.Client.Character
{
    /// <summary>Local projectile simulation; segment casts prevent tunnelling. Not network authority.</summary>
    public sealed class CharacterRifle : MonoBehaviour
    {
        [SerializeField] private CharacterMotor motor;
        [SerializeField] private Camera view;
        [SerializeField] private Transform muzzle;
        [SerializeField] private Material tracerMaterial;
        [SerializeField, Min(1)] private float speed = 70, range = 100, damage = 25;
        [SerializeField] private LayerMask collisionMask = ~4;
        private sealed class Bullet { public Vector3 Position, Direction; public float Remaining; public LineRenderer Line; }
        private readonly Bullet[] bullets = new Bullet[32];
        private CharacterMotor subscribed;
        private int pending;
        public int Shots { get; private set; }
        public int Hits { get; private set; }
        public string LastHit { get; private set; } = "--";
        public void Configure(CharacterMotor source, Camera camera, Transform origin, Material material)
        {
            Unsubscribe(); motor = source; view = camera; muzzle = origin; tracerMaterial = material;
            if (isActiveAndEnabled) Subscribe();
        }
        private void OnEnable() => Subscribe();
        private void Subscribe() { if (motor != null && subscribed == null) { subscribed = motor; subscribed.ShotFired += QueueShot; } }
        private void Unsubscribe() { if (subscribed != null) subscribed.ShotFired -= QueueShot; subscribed = null; }
        private void QueueShot() => pending = Mathf.Min(pending + 1, bullets.Length);
        private void LateUpdate()
        {
            while (pending > 0) { pending--; Fire(); }
            Advance(Time.deltaTime);
        }
        private void Fire()
        {
            if (view == null || muzzle == null) return;
            var ray = view.ViewportPointToRay(new Vector3(.5f, .5f));
            Vector3 target = ray.GetPoint(range);
            if (Cast(ray.origin, ray.direction, range, out var aimHit)) target = aimHit.point;
            var direction = target - muzzle.position;
            if (Vector3.Dot(direction, ray.direction) <= 0) direction = ray.direction;
            Launch(muzzle.position, direction);
        }
        /// <summary>Main-thread local test/adapter entry; production input uses motor's accepted shot event.</summary>
        public void Launch(Vector3 origin, Vector3 direction)
        {
            if (direction.sqrMagnitude < .0001f) return;
            for (int i = 0; i < bullets.Length; i++)
            {
                var bullet = bullets[i];
                if (bullet != null && bullet.Remaining > 0) continue;
                if (bullet == null)
                {
                    var go = new GameObject("Rifle tracer");
                    var line = go.AddComponent<LineRenderer>();
                    line.positionCount = 2; line.startWidth = .025f; line.endWidth = .008f;
                    line.sharedMaterial = tracerMaterial;
                    line.startColor = line.endColor = new Color(1, .8f, .15f);
                    line.enabled = false;
                    bullets[i] = bullet = new Bullet { Line = line };
                }
                bullet.Position = origin; bullet.Direction = direction.normalized; bullet.Remaining = range;
                Shots++;
                // A muzzle inside geometry must not shoot through that geometry.
                foreach (var overlap in Physics.OverlapSphere(origin, .02f, collisionMask, QueryTriggerInteraction.Ignore))
                    if (!IsOwner(overlap)) { Impact(overlap); bullet.Remaining = 0; return; }
                return;
            }
        }
        public void Advance(float seconds)
        {
            if (seconds <= 0) return;
            foreach (var bullet in bullets)
            {
                if (bullet == null) continue;
                bullet.Line.enabled = bullet.Remaining > 0;
                if (bullet.Remaining <= 0) continue;
                float distance = Mathf.Min(speed * seconds, bullet.Remaining);
                Vector3 end = bullet.Position + bullet.Direction * distance;
                if (Cast(bullet.Position, bullet.Direction, distance, out var hit))
                { end = hit.point; Impact(hit.collider); bullet.Remaining = 0; }
                else bullet.Remaining -= distance;
                bullet.Line.SetPosition(0, bullet.Position);
                bullet.Line.SetPosition(1, end);
                bullet.Position = end;
            }
        }
        private bool IsOwner(Collider collider) => collider.transform.IsChildOf(motor != null ? motor.transform : transform);
        private bool Cast(Vector3 origin, Vector3 direction, float distance, out RaycastHit nearest)
        {
            nearest = default; float best = float.PositiveInfinity;
            foreach (var hit in Physics.RaycastAll(origin, direction, distance, collisionMask, QueryTriggerInteraction.Ignore))
                if (!IsOwner(hit.collider) && hit.distance < best) { nearest = hit; best = hit.distance; }
            return best < float.PositiveInfinity;
        }
        private void Impact(Collider collider)
        {
            var health = collider.GetComponentInParent<CharacterHealth>();
            if (health != null && health.ApplyDamage(damage))
            { Hits++; LastHit = health.name + " -" + damage + (health.IsDead ? " DOWN" : " HP " + health.Current); }
            else LastHit = collider.name + " (blocked)";
        }
        private void OnDisable()
        {
            Unsubscribe(); pending = 0;
            foreach (var bullet in bullets) if (bullet != null) { bullet.Remaining = 0; bullet.Line.enabled = false; }
        }
        private void OnDestroy() { foreach (var bullet in bullets) if (bullet != null && bullet.Line != null) Destroy(bullet.Line.gameObject); }
    }
}

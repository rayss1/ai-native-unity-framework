using UnityEngine;

namespace AiNative.Client.Character
{
    /// <summary>Local training health only. Never an authoritative network health store.</summary>
    public sealed class CharacterHealth : MonoBehaviour
    {
        [SerializeField, Min(1)]
        private float maximum = 100;
        public float Current { get; private set; }
        public float Maximum => maximum;
        public bool IsDead => Current <= 0;

        private void Awake() => ResetHealth();

        public void ResetHealth() => Current = Mathf.Max(1, maximum);

        public bool ApplyDamage(float amount)
        {
            if (IsDead || amount <= 0 || float.IsNaN(amount) || float.IsInfinity(amount))
                return false;
            Current = Mathf.Max(0, Current - amount);
            return true;
        }
    }
}

using System;
using AiNative.Client.Prediction;
using AiNative.Gameplay;
using UnityEngine;

namespace AiNative.Client.Application
{
    /// <summary>Application-owned bounded visuals. Poses are world-space, independent of the local player.</summary>
    internal sealed class ArenaRemoteVisualPool : IDisposable
    {
        private readonly GameObject[] _visuals = new GameObject[ArenaClientProtocolV1.MaxSnapshotPlayers - 1];
        private readonly uint[] _ids = new uint[ArenaClientProtocolV1.MaxSnapshotPlayers - 1];
        private readonly NetworkCharacterPresenter[] _presenters = new NetworkCharacterPresenter[ArenaClientProtocolV1.MaxSnapshotPlayers - 1];
        private readonly float _heightOffset;
        private bool _disposed;

        public ArenaRemoteVisualPool(Material material) : this(material, null) { }

        public ArenaRemoteVisualPool(Material material, GameObject characterPrefab)
        {
            _heightOffset = characterPrefab == null ? 1f : 0f;
            for (int i = 0; i < _visuals.Length; i++)
            {
                GameObject visual = characterPrefab == null
                    ? GameObject.CreatePrimitive(PrimitiveType.Capsule)
                    : UnityEngine.Object.Instantiate(characterPrefab);
                visual.name = characterPrefab == null ? "RemotePlayerGreybox" : "RemoteNetworkCharacter";
                if (characterPrefab == null) visual.GetComponent<Renderer>().sharedMaterial = material;
                // Presentation geometry does not participate in prediction or authoritative collision.
                foreach (Collider collider in visual.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                _presenters[i] = visual.GetComponent<NetworkCharacterPresenter>();
                visual.SetActive(false);
                _visuals[i] = visual;
            }
        }

        public void Apply(ArenaRemotePose[] poses, int count)
        {
            if (_disposed) return;
            if (poses == null || count < 0 || count > poses.Length || count > _visuals.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            for (int slot = 0; slot < _visuals.Length; slot++)
            {
                bool present = false;
                for (int i = 0; i < count; i++) if (poses[i].EntityId == _ids[slot]) present = true;
                if (!present) { _ids[slot] = 0; _visuals[slot].SetActive(false); }
            }
            for (int i = 0; i < count; i++)
            {
                ArenaRemotePose pose = poses[i];
                int slot = Array.IndexOf(_ids, pose.EntityId);
                if (slot < 0) { slot = Array.IndexOf(_ids, 0u); _ids[slot] = pose.EntityId; }
                GameObject visual = _visuals[slot];
                _presenters[slot]?.Apply(pose.State);
                visual.transform.SetPositionAndRotation(
                    new Vector3((float)(pose.XMillimetres / 1000), (float)(pose.YMillimetres / 1000) + _heightOffset, (float)(pose.ZMillimetres / 1000)),
                    Quaternion.Euler(0, (float)(pose.YawMillidegrees / 1000), 0));
                visual.SetActive(pose.Alive);
            }
        }

        // Presentation-only target selection. The server independently validates every shot.
        public Vector3 AimPoint(Ray ray, float range)
        {
            double nearest = range * 1000d;
            bool found = false;
            foreach (GameObject visual in _visuals)
            {
                if (visual == null || !visual.activeSelf) continue;
                Vector3 relative = (ray.origin - visual.transform.position + Vector3.up * _heightOffset) * 1000f;
                if (ArenaHitGeometry.RayCapsule(relative.x, relative.y, relative.z,
                    ray.direction.x, ray.direction.y, ray.direction.z, nearest, out double hit))
                { nearest = hit; found = true; }
            }
            // Aim slightly inside the surface to tolerate millidegree quantization.
            return ray.GetPoint((float)(nearest / 1000d) + (found ? .02f : 0));
        }

        public GameObject GetVisual(uint entityId)
        {
            int slot = Array.IndexOf(_ids, entityId);
            return _disposed || entityId == 0 || slot < 0 ? null : _visuals[slot];
        }

        public NetworkCharacterPresenter GetPresenter(uint entityId)
        {
            int slot = Array.IndexOf(_ids, entityId);
            return _disposed || entityId == 0 || slot < 0 ? null : _presenters[slot];
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (int i = 0; i < _visuals.Length; i++)
            {
                if (_visuals[i] != null) UnityEngine.Object.Destroy(_visuals[i]);
                _visuals[i] = null;
                _presenters[i] = null;
                _ids[i] = 0;
            }
        }
    }
}

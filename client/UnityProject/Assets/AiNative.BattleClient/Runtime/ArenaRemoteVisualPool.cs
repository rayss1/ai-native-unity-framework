using System;
using AiNative.Client.Prediction;
using UnityEngine;

namespace AiNative.Client.Application
{
    /// <summary>Application-owned bounded visuals. Poses are world-space, independent of the local player.</summary>
    internal sealed class ArenaRemoteVisualPool : IDisposable
    {
        private readonly GameObject[] _visuals = new GameObject[ArenaClientProtocolV1.MaxSnapshotPlayers - 1];
        private readonly uint[] _ids = new uint[ArenaClientProtocolV1.MaxSnapshotPlayers - 1];
        private bool _disposed;

        public ArenaRemoteVisualPool(Material material)
        {
            for (int i = 0; i < _visuals.Length; i++)
            {
                GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                visual.name = "RemotePlayerGreybox";
                visual.GetComponent<Renderer>().sharedMaterial = material;
                // Presentation geometry does not participate in prediction or authoritative collision.
                visual.GetComponent<Collider>().enabled = false;
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
                visual.transform.SetPositionAndRotation(
                    new Vector3((float)(pose.XMillimetres / 1000), (float)(pose.YMillimetres / 1000) + 1f, (float)(pose.ZMillimetres / 1000)),
                    Quaternion.Euler(0, (float)(pose.YawMillidegrees / 1000), 0));
                visual.SetActive(pose.Alive);
            }
        }

        public GameObject GetVisual(uint entityId)
        {
            int slot = Array.IndexOf(_ids, entityId);
            return _disposed || entityId == 0 || slot < 0 ? null : _visuals[slot];
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (int i = 0; i < _visuals.Length; i++)
            {
                if (_visuals[i] != null) UnityEngine.Object.Destroy(_visuals[i]);
                _visuals[i] = null;
                _ids[i] = 0;
            }
        }
    }
}

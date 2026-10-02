using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using AiNative.Protocol.Backend.V1;
using Google.Protobuf;

namespace AiNative.Server.Control;

public sealed record ServicePeer(string Id, ServiceRole Role, int SceneId, string PublicKeyPem);

/// <summary>Per-peer keys authenticate role and delegated player claims independently of a TCP endpoint.</summary>
public sealed class PeerAuthentication : IDisposable
{
    private readonly string _selfId;
    private readonly RSA _signing;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, (ServicePeer Peer, RSA Key)> _peers;
    private readonly ConcurrentDictionary<string, long> _nonces = new(StringComparer.Ordinal);
    private readonly object _verify = new();
    public PeerAuthentication(string selfId, string privateKeyPem, IEnumerable<ServicePeer> peers, TimeProvider clock)
    {
        _selfId = selfId; _clock = clock; _signing = RSA.Create(); _signing.ImportFromPem(privateKeyPem);
        _peers = peers.ToDictionary(x => x.Id, x => { RSA key = RSA.Create(); key.ImportFromPem(x.PublicKeyPem); return (x, key); }, StringComparer.Ordinal);
        if (!_peers.ContainsKey(selfId) || _signing.KeySize < 2048 || _peers.Values.Any(x => x.Key.KeySize < 2048 ||
            x.Peer.Id.Length is < 1 or > 128 || x.Peer.Id.Contains('|') || !Enum.IsDefined(x.Peer.Role)) ||
            _peers.Values.Select(x => x.Peer.SceneId).Distinct().Count() != _peers.Count ||
            !_signing.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(_peers[selfId].Key.ExportSubjectPublicKeyInfo()))
            throw new ArgumentException("invalid-peer-key");
    }
    public IReadOnlyCollection<ServicePeer> Peers => _peers.Values.Select(x => x.Peer).ToArray();
    public void Sign(ControlRequest request)
    {
        request.Credential = "";
        string stamp = _clock.GetUtcNow().AddSeconds(20).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string nonce = Guid.NewGuid().ToString("N");
        string header = $"{_selfId}|{stamp}|{nonce}";
        byte[] signature;
        lock (_signing) signature = _signing.SignData(SignedBytes(header, request), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        request.Credential = header + "|" + Convert.ToBase64String(signature);
    }
    public bool TryAuthenticate(ControlRequest request, out ServiceCallContext context)
    {
        context = default;
        if (request.CalculateSize() > 65536 || request.CorrelationId.Length is < 1 or > 128 || request.Method.Length is < 1 or > 128 || request.Credential.Length > 2048) return false;
        string[] proof = request.Credential.Split('|');
        if (proof.Length != 4 || !_peers.TryGetValue(proof[0], out var peer) ||
            !long.TryParse(proof[1], CultureInfo.InvariantCulture, out long expiry)) return false;
        if (peer.Peer.Role == ServiceRole.Client) return false;
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        if (expiry <= now || expiry > now + 30 || proof[2].Length != 32 || request.DelegatedPlayerId.Length > 128) return false;
        if (request.DelegatedPlayerId.Length != 0 && peer.Peer.Role is not (ServiceRole.Gate or ServiceRole.Lobby)) return false;
        byte[] signature;
        try { signature = Convert.FromBase64String(proof[3]); } catch (FormatException) { return false; }
        ControlRequest unsigned = request.Clone(); unsigned.Credential = "";
        lock (_verify)
        {
            try { if (!peer.Key.VerifyData(SignedBytes(string.Join('|', proof.Take(3)), unsigned), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) return false; }
            catch (CryptographicException) { return false; }
            foreach (var nonce in _nonces) if (nonce.Value <= now) _nonces.TryRemove(nonce.Key, out _);
            if (_nonces.Count >= 16384 || !_nonces.TryAdd(proof[0] + ":" + proof[2], expiry)) return false;
        }
        context = new(peer.Peer.Role, peer.Peer.Id, request.DelegatedPlayerId);
        return true;
    }
    private static byte[] SignedBytes(string header, ControlRequest request)
    {
        byte[] prefix = System.Text.Encoding.UTF8.GetBytes(header + "|");
        byte[] payload = request.ToByteArray();
        byte[] bytes = new byte[prefix.Length + payload.Length]; prefix.CopyTo(bytes, 0); payload.CopyTo(bytes, prefix.Length); return bytes;
    }
    public void Dispose() { _signing.Dispose(); foreach (var peer in _peers.Values) peer.Key.Dispose(); }
}

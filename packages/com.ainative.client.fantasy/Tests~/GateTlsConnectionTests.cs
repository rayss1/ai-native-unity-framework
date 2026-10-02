using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AiNative.Client.Fantasy;
using NUnit.Framework;

namespace AiNative.Client.Gate.Tests;

public sealed class GateTlsConnectionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task Real_TLS_handshake_accepts_only_the_configured_certificate(bool wrongPin)
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder names = new(); names.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(names.Build());
        using X509Certificate2 generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        // Windows Schannel cannot acquire credentials from CreateSelfSigned's ephemeral key.
        using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.DefaultKeySet);
        using TcpListener listener = new(IPAddress.Loopback, 0); listener.Start();
        Task server = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync();
            using SslStream tls = new(peer.GetStream());
            try { await tls.AuthenticateAsServerAsync(certificate, false, SslProtocols.Tls12, false); }
            catch (Exception e)
            {
                TestContext.Progress.WriteLine("TLS fixture server: " + e);
                if (!wrongPin || e is not (AuthenticationException or IOException)) throw;
            }
        });
        string pin = wrongPin ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(certificate.RawData));
        var options = new GateConnectionOptions("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, certificateSha256: pin);
        try
        {
            if (wrongPin) Assert.ThrowsAsync<AuthenticationException>(() => FantasyGateClient.ConnectAsync(options));
            else { using FantasyGateClient client = await FantasyGateClient.ConnectAsync(options); Assert.That(client.IsConnected, Is.True); }
        }
        finally { await server; }
    }
}

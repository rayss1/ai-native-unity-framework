using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AiNative.Client.Fantasy
{
    internal static class GateTlsTrust
    {
        internal static bool Accept(X509Certificate certificate, SslPolicyErrors errors, string pin, DateTime utcNow)
        {
            if (certificate == null || (errors & (SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch)) != 0) return false;
            using var cert = new X509Certificate2(certificate);
            if (utcNow < cert.NotBefore.ToUniversalTime() || utcNow > cert.NotAfter.ToUniversalTime()) return false;
            if (string.IsNullOrEmpty(pin)) return errors == SslPolicyErrors.None;
            using var hash = SHA256.Create(); byte[] digest = hash.ComputeHash(cert.RawData);
            string actual = BitConverter.ToString(digest).Replace("-", "");
            // A configured pin is the explicit trust anchor for this test certificate; name/date still apply.
            return string.Equals(actual, pin, StringComparison.OrdinalIgnoreCase);
        }
    }
}

using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace TarsClient.Services;

/// <summary>Pins the TARS Home CA (embedded tars-ca.crt). Never accepts arbitrary certificates.</summary>
public static class Tls
{
    static readonly X509Certificate2 PinnedCa = LoadCa();

    static X509Certificate2 LoadCa()
    {
        using var s = typeof(Tls).Assembly.GetManifestResourceStream("tars-ca.crt")
                      ?? throw new InvalidOperationException("tars-ca.crt resource missing");
        using var r = new StreamReader(s);
        return X509Certificate2.CreateFromPem(r.ReadToEnd());
    }

    public static bool Validate(object? sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (cert is null) return false;
        // The host name must still match the certificate's SANs; only the trust anchor is replaced.
        if ((errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
            return false;
        using var c = new X509Chain();
        c.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        c.ChainPolicy.CustomTrustStore.Add(PinnedCa);
        c.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        using var leaf = new X509Certificate2(cert);
        return c.Build(leaf);
    }

    public static HttpClient CreateHttpClient(TimeSpan timeout) => new(new SocketsHttpHandler
    {
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Validate },
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
    }) { Timeout = timeout };
}

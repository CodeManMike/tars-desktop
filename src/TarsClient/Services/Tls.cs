using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace TarsClient.Services;

/// <summary>Pins the TARS Home CA (the embedded <c>tars-ca.crt</c>). We never accept arbitrary certificates.</summary>
public static class Tls
{
    #region Fields

    private static readonly X509Certificate2 PinnedCa = LoadCa();

    #endregion

    #region Public Methods

    /// <summary>Certificate check: the host name must still match, and the chain must end at our CA and nowhere else.</summary>
    public static bool Validate(object? sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (cert is null) return false;
        if ((errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
            return false;

        using var pinned = new X509Chain();
        pinned.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        pinned.ChainPolicy.CustomTrustStore.Add(PinnedCa);
        pinned.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        using var leaf = new X509Certificate2(cert);
        return pinned.Build(leaf);
    }

    /// <summary>An <see cref="HttpClient"/> that uses <see cref="Validate"/>.</summary>
    public static HttpClient CreateHttpClient(TimeSpan timeout) => new(new SocketsHttpHandler
    {
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Validate },
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
    }) { Timeout = timeout };

    #endregion

    #region Private Methods

    private static X509Certificate2 LoadCa()
    {
        using var stream = typeof(Tls).Assembly.GetManifestResourceStream("tars-ca.crt")
                           ?? throw new InvalidOperationException("tars-ca.crt resource missing");
        using var reader = new StreamReader(stream);
        return X509Certificate2.CreateFromPem(reader.ReadToEnd());
    }

    #endregion
}

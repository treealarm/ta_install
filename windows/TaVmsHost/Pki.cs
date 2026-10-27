using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace TaVmsHost;

// A deployment's Dapr certificates and the tokens that get a sidecar one, made on the core.
//
// Sentry would make its own root and issuer on its first start, but for one year only, and every
// sidecar needs the root before anything runs; so they are made here, for ten years, as
// scripts/distributed.sh does on Linux: EC P-256, the issuer's key in SEC1 form, which Sentry takes.
//
// Sentry signs a certificate for an app id only against a token for it (its jwks validator): a JWT
// whose subject is the app's SPIFFE id and whose audience is Sentry's, signed with a key of the
// JWKS in Sentry's configuration: ES256, as on a Linux core and as the core's VmsCfg signs the
// tokens of nodes that join later, with the same key (core\jwt.key). No "exp": a token is a
// machine's credential, and what revokes it is a new key in the JWKS.
internal static class Pki
{
  public const string SentryId = "spiffe://localhost/ns/default/dapr-sentry";
  public const string PlacementId = "spiffe://localhost/ns/default/dapr-placement";
  public static string AppSpiffeId(string appId) => $"spiffe://public/ns/default/{appId}";
  private const string KeyId = "tavms";
  private static readonly TimeSpan Lifetime = TimeSpan.FromDays(3650);

  public sealed record Authority(string RootCertPem, string RootKeyPem, string IssuerCertPem, string IssuerKeyPem);

  public static Authority CreateAuthority(DateTimeOffset now)
  {
    var notBefore = now.AddMinutes(-5);
    var notAfter = now + Lifetime;

    using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var rootRequest = new CertificateRequest(new X500DistinguishedName("O=localhost"), rootKey, HashAlgorithmName.SHA256);
    AddCaExtensions(rootRequest, X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign);
    using var root = rootRequest.CreateSelfSigned(notBefore, notAfter);

    using var issuerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var issuerName = new X500DistinguishedNameBuilder();
    issuerName.AddOrganizationName(SentryId);
    var issuerRequest = new CertificateRequest(issuerName.Build(), issuerKey, HashAlgorithmName.SHA256);
    AddCaExtensions(issuerRequest,
      X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign);
    issuerRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));
    var serial = RandomNumberGenerator.GetBytes(16);
    serial[0] &= 0x7F;
    using var issuer = issuerRequest.Create(root, notBefore, notAfter, serial);

    return new Authority(root.ExportCertificatePem() + "\n", rootKey.ExportECPrivateKeyPem() + "\n",
      issuer.ExportCertificatePem() + "\n", issuerKey.ExportECPrivateKeyPem() + "\n");
  }

  private static void AddCaExtensions(CertificateRequest request, X509KeyUsageFlags usage)
  {
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, true));
    request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
  }

  // The deployment's own TLS, for what crosses the LAN in the clear otherwise: a CA of the core, and
  // under it a certificate for a machine's address — as scripts/distributed.sh makes them (make_tls_ca,
  // issue_server_cert), keys in PKCS#8. Not the root above: that one signs Sentry's online issuer,
  // carries no constraints, and has no place in the machines' trust stores, where this one goes. The
  // core's VmsCfg holds this CA's key to issue the certificates of the nodes that join, so the CA
  // vouches for private addresses only (RFC 1918, and 100.64/10 of CGNAT and VPN overlays) and for no
  // DNS name at all: tavms.invalid is the one name it permits, and it never resolves.
  public static readonly TimeSpan TlsServerLifetime = TimeSpan.FromDays(825); // what Apple's platforms accept
  public static readonly TimeSpan TlsRenewBefore = TimeSpan.FromDays(60);

  private static readonly (byte[] Address, byte[] Mask)[] PermittedNetworks =
  [
    ([10, 0, 0, 0], [255, 0, 0, 0]),
    ([172, 16, 0, 0], [255, 240, 0, 0]),
    ([192, 168, 0, 0], [255, 255, 0, 0]),
    ([100, 64, 0, 0], [255, 192, 0, 0]),
  ];
  private const string PermittedDnsName = "tavms.invalid";

  public sealed record TlsAuthority(string CertPem, string KeyPem);

  public static TlsAuthority CreateTlsAuthority(string coreAddress, DateTimeOffset now)
  {
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var request = new CertificateRequest($"CN=TreeAlarm VMS CA {coreAddress}", key, HashAlgorithmName.SHA256);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
    request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
    request.CertificateExtensions.Add(NameConstraints());
    using var ca = request.CreateSelfSigned(now.AddMinutes(-5), now + Lifetime);
    return new TlsAuthority(ca.ExportCertificatePem() + "\n", key.ExportPkcs8PrivateKeyPem() + "\n");
  }

  // RFC 5280 4.2.1.10: permittedSubtrees [0] of GeneralSubtree { base GeneralName }, with
  // iPAddress [7] as address and mask, dNSName [2] as IA5String; both implicitly tagged.
  private static X509Extension NameConstraints()
  {
    var writer = new AsnWriter(AsnEncodingRules.DER);
    using (writer.PushSequence())
    using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
    {
      foreach (var (address, mask) in PermittedNetworks)
        using (writer.PushSequence())
          writer.WriteOctetString([.. address, .. mask], new Asn1Tag(TagClass.ContextSpecific, 7));
      using (writer.PushSequence())
        writer.WriteCharacterString(UniversalTagNumber.IA5String, PermittedDnsName, new Asn1Tag(TagClass.ContextSpecific, 2));
    }
    return new X509Extension("2.5.29.30", writer.Encode(), critical: true);
  }

  /// <summary>
  /// A certificate for one machine's address, with the CA after it in the PEM: a machine joining sees
  /// the CA in the handshake and checks it against the fingerprint it was given. Its CN is not the
  /// bare address on purpose: OpenSSL reads a CN that looks like a host name against the CA's DNS
  /// constraint, and would refuse it.
  /// </summary>
  public static (string CertPem, string KeyPem) IssueServerCertificate(TlsAuthority ca, string address, DateTimeOffset now)
  {
    using var caCert = X509Certificate2.CreateFromPem(ca.CertPem, ca.KeyPem);
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var request = new CertificateRequest($"CN=TreeAlarm VMS {address}", key, HashAlgorithmName.SHA256);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
    var san = new SubjectAlternativeNameBuilder();
    san.AddIpAddress(IPAddress.Parse(address));
    request.CertificateExtensions.Add(san.Build());
    request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(caCert, true, false));
    var serial = RandomNumberGenerator.GetBytes(16);
    serial[0] &= 0x7F;
    // Within the authority's own validity, which .NET insists on: an authority openssl made starts
    // the moment it was made, not five minutes before; and one in its last 825 days ends sooner.
    var notBefore = Max(now.AddMinutes(-5), caCert.NotBefore.ToUniversalTime());
    var notAfter = Min(now + TlsServerLifetime, caCert.NotAfter.ToUniversalTime());
    using var leaf = request.Create(caCert, notBefore, notAfter, serial);
    return (leaf.ExportCertificatePem() + "\n" + ca.CertPem, key.ExportPkcs8PrivateKeyPem() + "\n");
  }

  private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
  private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

  /// <summary>Whether the first certificate in the PEM ends within <see cref="TlsRenewBefore"/>.</summary>
  public static bool NeedsRenewal(string certPem, DateTimeOffset now)
  {
    using var cert = X509Certificate2.CreateFromPem(certPem);
    return cert.NotAfter.ToUniversalTime() - now.UtcDateTime < TlsRenewBefore;
  }

  /// <summary>SHA-256 of the certificate, in hex pairs, as browsers and openssl show it.</summary>
  public static string Fingerprint(string certPem)
  {
    using var cert = X509Certificate2.CreateFromPem(certPem);
    return string.Join(':', SHA256.HashData(cert.RawData).Select(b => b.ToString("X2")));
  }

  public static string CreateSigningKeyPem()
  {
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    return key.ExportECPrivateKeyPem() + "\n";
  }

  // The public half, as Sentry's jwks validator reads it.
  public static string Jwks(string signingKeyPem)
  {
    using var key = Load(signingKeyPem);
    var q = key.ExportParameters(includePrivateParameters: false).Q;
    return JsonSerializer.Serialize(new
    {
      keys = new[]
      {
        new { kty = "EC", crv = "P-256", x = Base64Url(q.X!), y = Base64Url(q.Y!), kid = KeyId, alg = "ES256", use = "sig" },
      },
    });
  }

  public static string Token(string signingKeyPem, string subject, DateTimeOffset now)
  {
    using var key = Load(signingKeyPem);
    var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", typ = "JWT", kid = KeyId }));
    var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(
      new { sub = subject, aud = new[] { SentryId }, iat = now.ToUnixTimeSeconds() }));
    var signed = $"{header}.{payload}";
    // JWS wants r||s, not the DER sequence .NET produces by default.
    var signature = key.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256,
      DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    return $"{signed}.{Base64Url(signature)}";
  }

  private static ECDsa Load(string pem)
  {
    var key = ECDsa.Create();
    key.ImportFromPem(pem);
    return key;
  }

  public static string Base64Url(byte[] data) =>
    Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

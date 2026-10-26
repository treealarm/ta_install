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

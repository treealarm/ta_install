using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TaVmsHost.Tests;

// The deployment's own TLS on a core made by TaVmsHost core: the certificate authority, the core's
// certificate under it, what the authority refuses to vouch for, and the renewal.
public sealed class TlsTests : IDisposable
{
  private const string CoreAddress = "192.168.1.10";
  private readonly Installation _core = new();
  private readonly string _dir;

  public TlsTests()
  {
    _core.AppendSite("POSTGRES_PASSWORD=core-pg-pass");
    Distributed.Core(_core.Load(), CoreAddress, TextWriter.Null);
    _dir = _core.Load().DistributedDir;
  }

  public void Dispose() => _core.Dispose();

  private string Read(params string[] path) => File.ReadAllText(Path.Combine([_dir, .. path]));

  [Fact]
  public void The_core_presents_a_certificate_for_its_address_under_its_own_authority()
  {
    using var leaf = X509Certificate2.CreateFromPem(Read("tls", "server.crt"), Read("tls", "server.key"));
    Assert.True(leaf.HasPrivateKey);
    Assert.Equal([IPAddress.Parse(CoreAddress)],
      leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateIPAddresses());
    Assert.Contains(leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>(),
      o => o.Value == "1.3.6.1.5.5.7.3.1");
    Assert.InRange((leaf.NotAfter - leaf.NotBefore).TotalDays, 824, 826);
    Assert.True(Chains(leaf));

    // The authority follows the certificate in the file, for a machine joining to check.
    var pems = new X509Certificate2Collection();
    pems.ImportFromPem(Read("tls", "server.crt"));
    Assert.Equal(2, pems.Count);
    Assert.Equal(Pki.Fingerprint(Read("tls", "ca.crt")), Pki.Fingerprint(pems[1].ExportCertificatePem()));
    Assert.StartsWith("-----BEGIN PRIVATE KEY-----", Read("core", "tls-ca.key"));
  }

  [Theory]
  [InlineData("10.20.30.40", true)]
  [InlineData("172.31.0.1", true)]
  [InlineData("100.100.1.1", true)]
  [InlineData("8.8.8.8", false)]
  [InlineData("172.32.0.1", false)]
  public void The_authority_vouches_for_private_addresses_only(string address, bool trusted)
  {
    var ca = new Pki.TlsAuthority(Read("tls", "ca.crt"), Read("core", "tls-ca.key"));
    var (cert, key) = Pki.IssueServerCertificate(ca, address, DateTimeOffset.UtcNow);
    using var leaf = X509Certificate2.CreateFromPem(cert, key);
    Assert.Equal(trusted, Chains(leaf));
  }

  // An authority openssl made (scripts/distributed.sh on a Linux core) starts the moment it was made;
  // a certificate issued in the next five minutes must not start before it.
  [Fact]
  public void A_certificate_issued_right_after_its_authority_starts_with_it()
  {
    var now = DateTimeOffset.UtcNow;
    var ca = Pki.CreateTlsAuthority(CoreAddress, now.AddMinutes(5));
    var (cert, _) = Pki.IssueServerCertificate(ca, CoreAddress, now);
    using var leaf = X509Certificate2.CreateFromPem(cert);
    using var authority = X509Certificate2.CreateFromPem(ca.CertPem);
    Assert.True(leaf.NotBefore >= authority.NotBefore);
  }

  [Fact]
  public void The_fingerprint_is_the_authoritys_sha256_in_hex_pairs()
  {
    var fingerprint = Distributed.Fingerprint(_core.Load());
    using var ca = X509Certificate2.CreateFromPem(Read("tls", "ca.crt"));
    Assert.Equal(Convert.ToHexString(SHA256.HashData(ca.RawData)), fingerprint.Replace(":", ""));
    Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", fingerprint);
  }

  [Fact]
  public void The_core_certificate_is_renewed_only_when_it_is_about_to_end()
  {
    var issued = Read("tls", "server.crt");
    var fingerprint = Distributed.Fingerprint(_core.Load());

    Distributed.EnsureCoreTls(_dir, CoreAddress, DateTimeOffset.UtcNow.AddDays(700), TextWriter.Null);
    Assert.Equal(issued, Read("tls", "server.crt"));

    Distributed.EnsureCoreTls(_dir, CoreAddress, DateTimeOffset.UtcNow.AddDays(800), TextWriter.Null);
    Assert.NotEqual(issued, Read("tls", "server.crt"));
    Assert.Equal(fingerprint, Distributed.Fingerprint(_core.Load()));
  }

  [Fact]
  public void A_core_made_before_it_had_an_authority_gets_one()
  {
    Directory.Delete(Path.Combine(_dir, "tls"), true);
    File.Delete(Path.Combine(_dir, "core", "tls-ca.key"));

    Distributed.EnsureCoreTls(_core.Load(), TextWriter.Null);

    using var leaf = X509Certificate2.CreateFromPem(Read("tls", "server.crt"), Read("tls", "server.key"));
    Assert.True(Chains(leaf));
  }

  [Fact]
  public void Only_a_core_makes_an_authority()
  {
    using var standalone = new Installation();
    Assert.Throws<InvalidOperationException>(() => Distributed.EnsureCoreTls(standalone.Load(), TextWriter.Null));
  }

  // Whether the certificate chains to the core's authority as the only root trusted.
  private bool Chains(X509Certificate2 leaf)
  {
    using var root = X509Certificate2.CreateFromPem(Read("tls", "ca.crt"));
    using var chain = new X509Chain();
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(root);
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    return chain.Build(leaf);
  }
}

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace TaVmsHost.Tests;

// A node's package as the core's VmsCfg makes it when a machine joins (ta_vms
// dotnet/VmsCfg/Grpc/NodePackage.cs, whose tests pin its format down): shared.env as the core has
// it, the core's credentials and the address of each of its services in core.env, the name, and a
// token per app id of the node, signed with the core's key. Made here from the test core's own
// files, so that what TaVmsHost reads is checked against what a core of this kind holds.
internal static partial class CorePackage
{
  // What VmsCfg takes from the core's environment into core.env (its CoreKeys).
  [GeneratedRegex(@"^(POSTGRES_(PORT|USER|PASSWORD)|POSTGRES_[A-Z0-9_]+_DB|RABBITMQ_(PORT|DEFAULT_USER|DEFAULT_PASS)|VMS_MEDIAMTX_(PUBLISH|READ)_(USER|PASS)|KEYCLOAK_(PORT|ADMIN_USER|ADMIN_PASSWORD|REALM_NAME|CLIENT_ID)|VMS_WHEP_SESSION_KEY)$")]
  private static partial Regex CoreKeys();

  // The node's app ids VmsCfg signs tokens for (NodePackage.AppIdPrefixes there): services.json has
  // to give a node exactly these, or one of its sidecars gets no certificate.
  public static readonly string[] AppIdPrefixes = ["vms", "vmsfs", "vmsonvif", "vmsonvif-actors", "vmslogger", "web-vms"];

  public const string DefaultNodeAddress = "192.168.1.20";

  public static string Make(StackConfig core, string name, string toDir, IReadOnlyDictionary<string, string>? services = null,
    string address = DefaultNodeAddress)
  {
    var dir = core.DistributedDir;
    var key = File.ReadAllText(Path.Combine(dir, "core", "jwt.key"));
    var coreAddress = core.Env["CORE_ADDRESS"];
    var coreEnv = core.Env.Where(kv => CoreKeys().IsMatch(kv.Key))
      .ToDictionary(kv => kv.Key, kv => kv.Value);
    foreach (var (k, v) in services ?? new Dictionary<string, string>
    {
      ["POSTGRES_HOST"] = coreAddress,
      ["RABBITMQ_HOST"] = coreAddress,
      ["VMS_MTX_HOST"] = coreAddress,
      ["KEYCLOAK_URL"] = $"http://{coreAddress}:{core.Env["KEYCLOAK_PORT"]}",
    })
      coreEnv[k] = v;

    var files = new Dictionary<string, string>
    {
      ["shared.env"] = File.ReadAllText(Path.Combine(dir, "shared.env")),
      ["core.env"] = string.Concat(coreEnv.OrderBy(kv => kv.Key, StringComparer.Ordinal)
        .Select(kv => $"{kv.Key}={EnvFile.Quote(kv.Key, kv.Value)}\n")),
      ["name"] = name + "\n",
    };
    // The node's certificate for its address, from the core's TLS authority, as VmsCfg issues it.
    var ca = new Pki.TlsAuthority(File.ReadAllText(Path.Combine(dir, "tls", "ca.crt")),
      File.ReadAllText(Path.Combine(dir, "core", "tls-ca.key")));
    var (serverCert, serverKey) = Pki.IssueServerCertificate(ca, address, DateTimeOffset.UtcNow);
    files["tls/ca.crt"] = ca.CertPem;
    files["tls/server.crt"] = serverCert;
    files["tls/server.key"] = serverKey;
    foreach (var prefix in AppIdPrefixes)
      files[$"tokens/{prefix}-{name}.token"] = Pki.Token(key, Pki.AppSpiffeId($"{prefix}-{name}"), DateTimeOffset.UtcNow);

    // Laid out as VmsCfg writes it: PAX, sorted, mode 0600, no directory entries.
    var path = Path.Combine(toDir, $"node-{name}.tar.gz");
    using (var stream = File.Create(path))
    using (var gzip = new GZipStream(stream, CompressionLevel.Optimal))
    using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
    {
      foreach (var (entry, content) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entry)
        {
          Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
          DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
        });
    }
    return path;
  }
}

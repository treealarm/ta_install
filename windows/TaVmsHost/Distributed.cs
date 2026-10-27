using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaVmsHost;

// A deployment over several machines (ta_vms CLAUDE.md, "Distributed layout"), the Windows side of
// scripts/distributed.sh: the same files under <data>\distributed, the same package, so that a core
// on either platform takes nodes on either.
//
//   TaVmsHost core <address>          make this machine the core: root of trust, Sentry's issuer and
//                                     configuration, the key that signs tokens, Consul's gossip key
//   TaVmsHost join --core <core> --name <node> <address> [--no-mediamtx]
//                                     on a node: ask the core's web for this node's package
//                                     (VmsCfg keeps the deployment's nodes and makes the packages)
//   TaVmsHost join <package> <address> [--no-mediamtx]
//                                     on a node, with a package made in the core's web; without
//                                     MediaMTX its cameras publish to the core's
//   TaVmsHost tls                     on the core: its TLS certificate authority if it has none yet,
//                                     and its own certificate when missing or about to end
//   TaVmsHost fingerprint             the fingerprint of that certificate authority
//
//   core\           root.key: the root's key, closed to the service account; jwt.key, which signs
//                   the tokens, readable by it, since VmsCfg signs the tokens of nodes that join;
//                   tls-ca.key, the key of the deployment's TLS certificate authority
//   tls\            ca.crt, that authority; server.crt (followed by ca.crt) and server.key, the
//                   certificate this machine presents on the LAN (core)
//   sentry\         ca.crt, issuer.crt, issuer.key, config.yaml: what Sentry runs on (core)
//   tokens\         <app id>.token for each sidecar of this machine; on the core also
//                   dapr-placement.token (on Linux it sits in core\, which LocalService cannot read)
//   shared.env      the deployment's core address, Consul key and root (an env layer, StackConfig)
//   consul.json     the Consul key again, as Consul reads it (-config-file)
//   core.env        a node: the core's credentials and where its services are (an env layer too)
//   name            a node's own name
//   nodes           on a core made before VmsCfg kept the nodes: the pairs it made packages for
//
// Each writes the role and this machine's address into site.env last, once everything else is in
// place: a run that fails half-way leaves the machine as it was. Nothing here touches PostgreSQL,
// RabbitMQ or the firewall; Install-TaVms.ps1 sets those up by role.
internal static class Distributed
{
  public static void Core(StackConfig config, string address, TextWriter output)
  {
    var ip = RequireAddress(address);
    var dir = config.DistributedDir;
    if (File.Exists(Path.Combine(dir, "core", "jwt.key")))
      throw new InvalidOperationException($"{dir} holds a core already");
    if (config.Role != Role.Standalone)
      throw new InvalidOperationException($"this machine is a {config.Role.Name()} already");

    var now = DateTimeOffset.UtcNow;
    var authority = Pki.CreateAuthority(now);
    var signingKey = Pki.CreateSigningKeyPem();

    DirectoryAcl.Restrict(dir, serviceReads: true);
    DirectoryAcl.Restrict(Path.Combine(dir, "core"), serviceReads: false);
    Directory.CreateDirectory(Path.Combine(dir, "sentry"));
    Directory.CreateDirectory(Path.Combine(dir, "tokens"));

    WriteText(Path.Combine(dir, "core", "root.key"), authority.RootKeyPem);
    WriteText(Path.Combine(dir, "core", "jwt.key"), signingKey);
    // VmsCfg, which runs as the service account, signs the tokens of the nodes that join later
    // (TAVMS_DISTRIBUTED_DIR, StackConfig). The root's key stays out of its reach.
    DirectoryAcl.AllowServiceRead(Path.Combine(dir, "core", "jwt.key"));
    WriteText(Path.Combine(dir, "sentry", "ca.crt"), authority.RootCertPem);
    WriteText(Path.Combine(dir, "sentry", "issuer.crt"), authority.IssuerCertPem);
    WriteText(Path.Combine(dir, "sentry", "issuer.key"), authority.IssuerKeyPem);
    WriteText(Path.Combine(dir, "sentry", "config.yaml"), SentryConfiguration(Pki.Jwks(signingKey)));

    // The core is a node too, with the standalone app ids.
    foreach (var app in config.Services.ForRole(Role.Core, WithSuffix(config.Env, "")).Apps)
      WriteText(config.TokenPath(app.AppId), Pki.Token(signingKey, Pki.AppSpiffeId(app.AppId), now));
    WriteText(config.TokenPath("dapr-placement"), Pki.Token(signingKey, Pki.PlacementId, now));

    var consulKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    WriteText(config.ConsulConfigPath, ConsulConfiguration(consulKey));
    WriteText(Path.Combine(dir, "shared.env"), string.Join('\n',
      $"CORE_ADDRESS={ip}",
      $"CONSUL_ENCRYPT={consulKey}",
      // Quoted over several lines, which compose and EnvFile read back as one value.
      $"DAPR_TRUST_ANCHORS=\"{authority.RootCertPem.TrimEnd('\n')}\"") + "\n");
    EnsureCoreTls(dir, ip, now, output);

    SiteEnv.Set(StackConfig.SiteEnvPath(config.Data),
    [
      ("TAVMS_ROLE", Role.Core.Name()),
      ("DAPR_HOST_IP", ip),
      // Where the MediaMTX row VmsCfg seeds sends every node's video.
      ("VMS_MTX_HOST", ip),
      // Keycloak listens on this address (services.json), where every machine's web logs users in.
      ("KEYCLOAK_URL", KeycloakUrl(ip, config.Require("KEYCLOAK_PORT"))),
    ]);
    // A MediaMTX row a standalone installation seeded at 127.0.0.1 is moved to VMS_MTX_HOST by
    // VmsCfg itself, on its next start.
    output.WriteLine($"This machine is the core at {ip}: {dir}");
    output.WriteLine("The fingerprint of its certificate authority, which a machine joining checks it against:");
    output.WriteLine($"  {Fingerprint(config)}");
  }

  /// <summary>
  /// On the core: the TLS certificate authority once, and the core's own certificate whenever it is
  /// missing or about to end. A core made before it had a CA gets one here. Install-TaVms.ps1 runs it
  /// on every install and upgrade of a core, as `distributed.sh up` does on Linux.
  /// </summary>
  public static void EnsureCoreTls(StackConfig config, TextWriter output)
  {
    if (config.Role != Role.Core)
      throw new InvalidOperationException($"this machine is a {config.Role.Name()}, not a core");
    var shared = ParseEnv(File.ReadAllText(Path.Combine(config.DistributedDir, "shared.env")), "shared.env");
    EnsureCoreTls(config.DistributedDir, shared["CORE_ADDRESS"], DateTimeOffset.UtcNow, output);
  }

  internal static void EnsureCoreTls(string dir, string address, DateTimeOffset now, TextWriter output)
  {
    var caKeyPath = Path.Combine(dir, "core", "tls-ca.key");
    var caCertPath = Path.Combine(dir, "tls", "ca.crt");
    Pki.TlsAuthority ca;
    if (File.Exists(caKeyPath))
    {
      ca = new Pki.TlsAuthority(File.ReadAllText(caCertPath), File.ReadAllText(caKeyPath));
    }
    else
    {
      ca = Pki.CreateTlsAuthority(address, now);
      WriteText(caKeyPath, ca.KeyPem);
      WriteText(caCertPath, ca.CertPem);
    }

    var serverCertPath = Path.Combine(dir, "tls", "server.crt");
    if (File.Exists(serverCertPath) && !Pki.NeedsRenewal(File.ReadAllText(serverCertPath), now))
      return;
    var (cert, key) = Pki.IssueServerCertificate(ca, address, now);
    // The key first: a run cut short in between leaves no certificate, and the next one issues both.
    WriteText(Path.Combine(dir, "tls", "server.key"), key);
    WriteText(serverCertPath, cert);
    output.WriteLine($"TLS certificate for {address} issued, valid for {Pki.TlsServerLifetime.TotalDays:0} days");
  }

  /// <summary>The fingerprint of this deployment's TLS certificate authority, as browsers show it.</summary>
  public static string Fingerprint(StackConfig config)
  {
    var path = Path.Combine(config.DistributedDir, "tls", "ca.crt");
    return File.Exists(path)
      ? Pki.Fingerprint(File.ReadAllText(path))
      : throw new InvalidOperationException($"no {path}: on a core, 'TaVmsHost tls' makes it");
  }

  /// <summary>
  /// Asks the core's web for this node's package (POST /api/deployment/join) and writes it to
  /// <paramref name="path"/>. Once the administrator has closed enrollment there, the core wants an
  /// administrator's user name and password.
  /// </summary>
  public static async Task FetchPackageAsync(string core, string name, string address, bool mediaMtx,
    string? user, string? password, string path, HttpMessageHandler? handler = null)
  {
    using var http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(30) };
    var web = new Uri($"http://{core}/api/deployment");
    JsonElement deployment;
    try
    {
      deployment = JsonDocument.Parse(await http.GetStringAsync(web)).RootElement;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
    {
      throw new InvalidOperationException($"no TreeAlarm VMS web answers at {web}: {ex.Message}", ex);
    }
    if (!deployment.TryGetProperty("distributed", out var distributed) || !distributed.GetBoolean())
      throw new InvalidOperationException($"{core} is not the core of a deployment over several machines");
    if (!deployment.GetProperty("enrollmentOpen").GetBoolean() && string.IsNullOrEmpty(user))
      throw new InvalidOperationException(
        $"enrollment of new nodes is closed at {core}: an administrator's user name and password are needed");

    using var response = await http.PostAsJsonAsync(new Uri(web, "deployment/join"),
      new { name, address, mediaMtx, username = user ?? "", password = password ?? "" });
    var body = await response.Content.ReadAsByteArrayAsync();
    if (!response.IsSuccessStatusCode)
    {
      var reason = Encoding.UTF8.GetString(body);
      try { reason = JsonDocument.Parse(body).RootElement.GetProperty("error").GetString() ?? reason; }
      catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
      throw new InvalidOperationException($"the core refused ({(int)response.StatusCode}): {reason}");
    }
    await File.WriteAllBytesAsync(path, body);
  }

  public static void Join(StackConfig config, string packagePath, string address, bool mediaMtx, TextWriter output)
  {
    var ip = RequireAddress(address);
    var dir = config.DistributedDir;
    if (File.Exists(Path.Combine(dir, "shared.env")) || File.Exists(Path.Combine(dir, "name")))
      throw new InvalidOperationException($"{dir} exists: this machine is part of a deployment already");
    if (config.Role != Role.Standalone)
      throw new InvalidOperationException($"this machine is a {config.Role.Name()} already");

    // Everything is checked before anything is written.
    var files = NodePackage.Read(packagePath);
    string File_(string name) => files.TryGetValue(name, out var text)
      ? text
      : throw new InvalidDataException($"{packagePath}: no {name}");
    var name = Roles.RequireNodeName(File_("name").Trim(), $"{packagePath}: name");
    var shared = ParseEnv(File_("shared.env"), "shared.env");
    var core = ParseEnv(File_("core.env"), "core.env");
    foreach (var key in new[] { "CORE_ADDRESS", "CONSUL_ENCRYPT", "DAPR_TRUST_ANCHORS" })
      Require(shared, key, $"{packagePath}: shared.env");
    foreach (var key in NodePackage.RequiredCoreKeys)
      Require(core, key, $"{packagePath}: core.env");
    var apps = config.Services.ForRole(Role.Node, WithSuffix(config.Env, "-" + name)).Apps;
    var missing = apps.Where(a => !files.ContainsKey($"tokens/{a.AppId}.token")).Select(a => a.AppId).ToList();
    if (missing.Count > 0)
      throw new InvalidDataException($"{packagePath}: no token for {string.Join(", ", missing)}");

    DirectoryAcl.Restrict(dir, serviceReads: true);
    Directory.CreateDirectory(Path.Combine(dir, "tokens"));
    foreach (var (file, content) in files)
      WriteText(Path.Combine(dir, file.Replace('/', Path.DirectorySeparatorChar)), content);
    WriteText(config.ConsulConfigPath, ConsulConfiguration(shared["CONSUL_ENCRYPT"]));

    SiteEnv.Set(StackConfig.SiteEnvPath(config.Data),
    [
      ("TAVMS_ROLE", Role.Node.Name()),
      ("TAVMS_NODE_NAME", name),
      ("DAPR_HOST_IP", ip),
      // The core's PostgreSQL, RabbitMQ, MediaMTX and Keycloak, whose credentials come from
      // core.env. The package names where each one is; one made before it did means the core's
      // address for all of them.
      ("POSTGRES_HOST", CoreService(core, "POSTGRES_HOST", shared["CORE_ADDRESS"])),
      ("RABBITMQ_HOST", CoreService(core, "RABBITMQ_HOST", shared["CORE_ADDRESS"])),
      ("VMS_MTX_HOST", CoreService(core, "VMS_MTX_HOST", shared["CORE_ADDRESS"])),
      ("KEYCLOAK_URL", CoreService(core, "KEYCLOAK_URL", KeycloakUrl(shared["CORE_ADDRESS"], core["KEYCLOAK_PORT"]))),
      // User administration (mt_admin) runs on the core only: this machine's web offers no link to it.
      ("MT_ADMIN_PORT", ""),
      // services.json "when" of mediamtx.
      ("TAVMS_MEDIAMTX", mediaMtx ? "1" : "0"),
    ]);
    output.WriteLine($"This machine is node '{name}' of the core at {shared["CORE_ADDRESS"]}: {dir}");
    output.WriteLine($"Its cameras are added in the web interface with server vms-{name}.");
  }

  private static string CoreService(Dictionary<string, string> core, string key, string otherwise) =>
    core.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : otherwise;

  private static string KeycloakUrl(string host, string port) => $"http://{host}:{port}";

  private static string RequireAddress(string address) =>
    IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)
      ? ip.ToString()
      : throw new ArgumentException($"'{address}' is not an IPv4 address other machines can reach this one at");

  private static Dictionary<string, string> WithSuffix(IReadOnlyDictionary<string, string> env, string suffix) =>
    new(env) { ["TAVMS_NODE_SUFFIX"] = suffix };

  private static Dictionary<string, string> ParseEnv(string text, string source)
  {
    var env = new Dictionary<string, string>(StringComparer.Ordinal);
    EnvFile.Apply(env, source, text.Split('\n'));
    return env;
  }

  private static void Require(Dictionary<string, string> env, string key, string where)
  {
    if (string.IsNullOrWhiteSpace(env.GetValueOrDefault(key)))
      throw new InvalidDataException($"{where} has no {key}");
  }

  // On Consul's command line the key would be in plain sight of anyone who lists processes; here it
  // is behind the ACL of <data>\distributed. Consul reads it on its first start only: from then on
  // it keeps the keyring in its data directory.
  private static string ConsulConfiguration(string key) =>
    JsonSerializer.Serialize(new Dictionary<string, string> { ["encrypt"] = key }) + "\n";

  private static string SentryConfiguration(string jwks) => $"""
    # Written by 'TaVmsHost core'. Sentry signs a certificate for an app id only against a token
    # for it, signed with the key of this JWKS; without a validator it would sign anything.
    kind: Configuration
    apiVersion: dapr.io/v1alpha1
    metadata:
      name: sentry
    spec:
      mtls:
        enabled: true
        workloadCertTTL: "24h"
        allowedClockSkew: "15m"
        tokenValidators:
          - name: jwks
            options:
              source: '{jwks}'

    """;

  // '\n' and no byte-order mark: these files are read on Linux too.
  private static void WriteText(string path, string content)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content.ReplaceLineEndings("\n"), new UTF8Encoding(false));
  }
}

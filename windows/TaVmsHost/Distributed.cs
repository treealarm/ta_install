using System.Net;
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
//   TaVmsHost package <node>          on the core: <data>\distributed\node-<node>.tar.gz
//   TaVmsHost join <package> <address> [--no-mediamtx]
//                                     on a node; without MediaMTX its cameras publish to the core's
//
//   core\           root.key, jwt.key: the core's secrets, closed to the service account
//   sentry\         ca.crt, issuer.crt, issuer.key, config.yaml: what Sentry runs on (core)
//   tokens\         <app id>.token for each sidecar of this machine; on the core also
//                   dapr-placement.token (on Linux it sits in core\, which LocalService cannot read)
//   shared.env      the deployment's core address, Consul key and root (an env layer, StackConfig)
//   consul.json     the Consul key again, as Consul reads it (-config-file)
//   core.env        a node: the core's credentials (an env layer too)
//   nodes, name     the nodes' pairs; a node's own name
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
  }

  public static string Package(StackConfig config, string name, TextWriter output)
  {
    Roles.RequireNodeName(name, "node name");
    var dir = config.DistributedDir;
    var keyPath = Path.Combine(dir, "core", "jwt.key");
    if (config.Role != Role.Core || !File.Exists(keyPath))
      throw new InvalidOperationException("not a core: run 'TaVmsHost core <address>' first (the installer does, for Role=core)");

    var nodesPath = Path.Combine(dir, "nodes");
    var nodes = File.Exists(nodesPath) ? File.ReadAllLines(nodesPath).Where(l => l.Trim().Length > 0).ToList() : [];
    var pair = $"vms-{name}:vmsfs-{name}:vmsonvif-{name}:vmslogger-{name}";
    if (!nodes.Contains(pair)) nodes.Add(pair);
    WriteText(nodesPath, string.Join('\n', nodes) + "\n");

    var signingKey = File.ReadAllText(keyPath);
    var now = DateTimeOffset.UtcNow;
    var files = new Dictionary<string, string>
    {
      ["shared.env"] = File.ReadAllText(Path.Combine(dir, "shared.env")),
      ["nodes"] = string.Join('\n', nodes) + "\n",
      ["core.env"] = string.Join('\n', config.Env
        .Where(kv => NodePackage.CoreKeys().IsMatch(kv.Key))
        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
        .Select(kv => $"{kv.Key}={EnvFile.Quote(kv.Key, kv.Value)}")) + "\n",
      ["name"] = name + "\n",
    };
    // The node's sidecars, by the app ids services.json gives a node of this name.
    foreach (var app in config.Services.ForRole(Role.Node, WithSuffix(config.Env, "-" + name)).Apps)
      files[$"tokens/{app.AppId}.token"] = Pki.Token(signingKey, Pki.AppSpiffeId(app.AppId), now);

    var path = Path.Combine(dir, $"node-{name}.tar.gz");
    NodePackage.Write(path, files);
    output.WriteLine($"Wrote {path}: copy it to the node and install there with Role=node, or run");
    output.WriteLine("'scripts/distributed.sh join' on a Linux node.");
    output.WriteLine("Then restart the TreeAlarm VMS service here (Restart-Service TaVmsHost), so that VmsCfg learns the node.");
    return path;
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
    _ = File_("nodes");

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
      // The core's PostgreSQL, RabbitMQ and MediaMTX, whose credentials come from core.env.
      ("POSTGRES_HOST", shared["CORE_ADDRESS"]),
      ("RABBITMQ_HOST", shared["CORE_ADDRESS"]),
      ("VMS_MTX_HOST", shared["CORE_ADDRESS"]),
      ("KEYCLOAK_URL", KeycloakUrl(shared["CORE_ADDRESS"], core["KEYCLOAK_PORT"])),
      // User administration (mt_admin) runs on the core only: this machine's web offers no link to it.
      ("MT_ADMIN_PORT", ""),
      // services.json "when" of mediamtx.
      ("TAVMS_MEDIAMTX", mediaMtx ? "1" : "0"),
    ]);
    output.WriteLine($"This machine is node '{name}' of the core at {shared["CORE_ADDRESS"]}: {dir}");
    output.WriteLine($"Its cameras are added in the web interface with server vms-{name}.");
    if (mediaMtx)
      output.WriteLine($"For their video to stay on this machine, add its MediaMTX there too (Administration, MediaMTX: host {ip}, node vms-{name}).");
  }

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

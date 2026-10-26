using System.Globalization;

namespace TaVmsHost;

internal sealed record ProcessSpec(
  string Name,
  string FileName,
  IReadOnlyList<string> Args,
  // Added on top of the host's own environment, which every child inherits.
  IReadOnlyDictionary<string, string> Env,
  string WorkingDirectory);

// Everything the host knows about the installation, resolved once at startup: where it lives, the
// merged environment, what this machine is (Role), what it runs, and the command line of each.
internal sealed class StackConfig
{
  public required string Home { get; init; }
  public required string Data { get; init; }
  public required IReadOnlyDictionary<string, string> Env { get; init; }
  public required Role Role { get; init; }
  // What this machine runs: Services for its role.
  public required Manifest Manifest { get; init; }
  // Every entry of services.json, for whatever role: the core mints tokens for a node's app ids.
  public required Manifest Services { get; init; }

  // The host's own log and every child's console output. Not under VMS_LOG_PATH: VmsLogger takes
  // each folder there for an app id and would show these unstructured files as one more service.
  public static string HostLogDirOf(string data) => Path.Combine(data, "host-logs");
  public string HostLogDir => HostLogDirOf(Data);
  public string ComponentsDir => Path.Combine(Data, "dapr", "components");
  public string DaprConfigPath => Path.Combine(Data, "dapr", "config.yaml");
  // In a deployment over several machines each sidecar has a configuration of its own: its Consul
  // health check names its own port.
  public string SidecarConfigPath(string appId) => Path.Combine(Data, "dapr", $"{appId}.yaml");

  // A core's or node's own part of the deployment, laid out as data/distributed/ is on Linux
  // (scripts/distributed.sh), apart from where placement's token lives (Distributed.cs).
  public static string DistributedDirOf(string data) => Path.Combine(data, "distributed");
  public string DistributedDir => DistributedDirOf(Data);
  public string TokenPath(string appId) => Path.Combine(DistributedDir, "tokens", $"{appId}.token");
  // Consul's gossip key, in a configuration file of Consul's own rather than on its command line.
  public string ConsulConfigPath => Path.Combine(DistributedDir, "consul.json");
  public static string SiteEnvPath(string data) => Path.Combine(data, "config", "site.env");
  public static string ManifestPath(string home) => Path.Combine(home, "config", "services.json");

  // The layers, in order (EnvFile). The two distributed ones exist only on a core or a node.
  public static IReadOnlyList<string> EnvFilePaths(string home, string data) =>
  [
    Path.Combine(home, "config", "ta_vms.env"),
    Path.Combine(home, "config", "windows.env"),
    .. new[] { "shared.env", "core.env" }
      .Select(f => Path.Combine(DistributedDirOf(data), f))
      .Where(File.Exists),
    SiteEnvPath(data),
  ];

  public static StackConfig Load(string home, string data)
  {
    var builtIns = new Dictionary<string, string>
    {
      ["TAVMS_HOME"] = home,
      ["TAVMS_DATA"] = data,
    };
    var env = EnvFile.Merge(builtIns, EnvFilePaths(home, data));
    if (env.ContainsKey("TAVMS_DAPR_NAME_RESOLUTION"))
      throw new InvalidOperationException(
        "TAVMS_DAPR_NAME_RESOLUTION is no longer read: the name resolver follows TAVMS_ROLE " +
        $"(sqlite for standalone, Consul for a core or a node). Remove it from {SiteEnvPath(data)}");
    var role = Roles.Parse(Value(env, "TAVMS_ROLE"), "TAVMS_ROLE");

    // Built in after the merge, so that no file can set it apart from TAVMS_NODE_NAME.
    env["TAVMS_NODE_SUFFIX"] = role == Role.Node
      ? "-" + Roles.RequireNodeName(Value(env, "TAVMS_NODE_NAME"), "TAVMS_NODE_NAME")
      : "";
    // A core made before VmsCfg kept the deployment's nodes lists here those it made packages for,
    // and VmsCfg takes them into its registry; a node that joins now is VmsCfg's alone.
    var nodes = Path.Combine(DistributedDirOf(data), "nodes");
    if (File.Exists(nodes))
    {
      var pairs = File.ReadAllLines(nodes).Select(l => l.Trim()).Where(l => l.Length > 0);
      env["MEDIA_SERVER_PAIRS"] = string.Join(',', [Value(env, "MEDIA_SERVER_PAIRS"), .. pairs]);
    }

    // Where VmsCfg on the core finds the key it signs joining nodes' tokens with, and shared.env.
    if (role == Role.Core) env["TAVMS_DISTRIBUTED_DIR"] = DistributedDirOf(data);

    var services = Manifest.Load(ManifestPath(home));
    var config = new StackConfig
    {
      Home = home,
      Data = data,
      Env = env,
      Role = role,
      Services = services,
      Manifest = services.ForRole(role, env),
    };
    if (role != Role.Standalone) config.CheckDistributed();
    return config;
  }

  // What a core or node cannot run without, said before anything starts: daprd would only exit,
  // and the supervisor would restart it for ever.
  private void CheckDistributed()
  {
    foreach (var key in new[] { "CORE_ADDRESS", "DAPR_TRUST_ANCHORS" })
    {
      if (string.IsNullOrWhiteSpace(Env.GetValueOrDefault(key)))
        throw new InvalidOperationException(
          $"TAVMS_ROLE={Role.Name()}, but {key} is not set: it comes from {Path.Combine(DistributedDir, "shared.env")}, " +
          "which 'TaVmsHost core' writes on a core and 'TaVmsHost join' brings to a node");
    }
    if (!File.Exists(ConsulConfigPath))
      throw new InvalidOperationException(
        $"TAVMS_ROLE={Role.Name()}, but {ConsulConfigPath} is missing: 'TaVmsHost core' and 'TaVmsHost join' write it");
    // Other machines reach this one's sidecars at the address they register with Consul.
    if (System.Net.IPAddress.TryParse(Require("DAPR_HOST_IP"), out var ip) && System.Net.IPAddress.IsLoopback(ip))
      throw new InvalidOperationException(
        $"TAVMS_ROLE={Role.Name()} needs DAPR_HOST_IP to be this machine's address on the network, not {ip}");
  }

  private static string Value(IReadOnlyDictionary<string, string> env, string key) =>
    env.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
      ? value
      : throw new InvalidOperationException($"Required environment variable '{key}' is not set");

  public string Require(string key) =>
    Env.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
      ? value
      : throw new InvalidOperationException(
          $"Required environment variable '{key}' is not set (see {string.Join(", ", EnvFilePaths(Home, Data))})");

  public int RequirePort(string key) => ParsePort(Require(key), key);

  public string Expand(string value, string where) => EnvFile.Expand(value, Env, where);

  public int? ReadyPort(InfraProcess p) =>
    p.ReadyPort is null ? null : ParsePort(Expand(p.ReadyPort, $"services.json {p.Name}.readyPort"), $"{p.Name}.readyPort");

  public string ReadyAddress(InfraProcess p) =>
    p.ReadyAddress is null ? "127.0.0.1" : Expand(p.ReadyAddress, $"services.json {p.Name}.readyAddress");

  public int AppPort(DaprApp a) => ParsePort(Expand(a.AppPort, $"services.json {a.AppId}.appPort"), $"{a.AppId}.appPort");

  public ProcessSpec Infra(InfraProcess p) => new(
    p.Name,
    ResolveExe(p.Exe),
    (p.Args ?? []).Select(arg => Expand(arg, $"services.json {p.Name}.args")).ToList(),
    ExpandAll(p.Env, $"services.json {p.Name}.env"),
    Path.GetDirectoryName(ResolveExe(p.Exe))!);

  // The same flags compose gives every sidecar, except that here all of them share one localhost,
  // so each needs ports of its own. daprd listens on every address unless told otherwise: its API
  // and metrics are for its own app and nobody else, so the loopback; the internal port is how
  // other sidecars reach it, so the address it registers at, which is the loopback on one machine.
  //
  // On a core or a node, as in docker-compose.distributed.yml: a configuration of its own (Consul),
  // mutual TLS with a certificate Sentry on the core signs against this app id's token, and the
  // core's placement.
  public ProcessSpec Sidecar(DaprApp a)
  {
    var hostIp = Require("DAPR_HOST_IP");
    var distributed = Role != Role.Standalone;
    var args = new List<string>
    {
      "--app-id", a.AppId,
      "--app-port", Num(AppPort(a)),
      "--app-protocol", a.Protocol,
      "--dapr-http-port", Num(a.DaprHttpPort),
      "--dapr-grpc-port", Num(a.DaprGrpcPort),
      "--dapr-listen-addresses", "127.0.0.1",
      "--dapr-internal-grpc-port", Num(a.DaprInternalGrpcPort),
      "--dapr-internal-grpc-listen-address", hostIp,
      "--metrics-port", Num(a.MetricsPort),
      "--metrics-listen-address", "127.0.0.1",
      "--resources-path", ComponentsDir,
      "--config", distributed ? SidecarConfigPath(a.AppId) : DaprConfigPath,
      "--log-level", "info",
    };
    var env = new Dictionary<string, string> { ["DAPR_HOST_IP"] = hostIp };
    if (distributed)
    {
      var token = TokenPath(a.AppId);
      if (!File.Exists(token))
        throw new InvalidOperationException(
          $"No Sentry token for app id '{a.AppId}' at {token}: the core mints one per app id ('TaVmsHost core', " +
          "and for a node's app ids into its package)");
      args.AddRange(["--enable-mtls", "--sentry-address", $"{Require("CORE_ADDRESS")}:{RequirePort("DAPR_SENTRY_PORT")}"]);
      env["DAPR_TRUST_ANCHORS"] = Require("DAPR_TRUST_ANCHORS");
      env["DAPR_SENTRY_TOKEN_FILE"] = token;
    }
    if (a.Placement)
    {
      // 127.0.0.1 rather than localhost, which may resolve to ::1 first: placement listens on
      // the IPv4 loopback only (services.json) — on one machine; on several, on the core's address.
      var host = distributed ? Require("CORE_ADDRESS") : "127.0.0.1";
      args.AddRange(["--placement-host-address", $"{host}:{RequirePort("DAPR_PLACEMENT_PORT")}"]);
    }
    var daprd = ResolveExe(@"dapr\daprd.exe");
    return new ProcessSpec(a.SidecarName, daprd, args, env, Path.GetDirectoryName(daprd)!);
  }

  // The whole merged environment, as env_file gives it to every container, plus what is this
  // process's own: its app id and where its sidecar listens (the Dapr SDK reads both ports).
  public ProcessSpec App(DaprApp a)
  {
    var env = new Dictionary<string, string>(Env);
    foreach (var (key, value) in ExpandAll(a.Env, $"services.json {a.AppId}.env"))
    {
      env[key] = value;
    }
    env["APP_ID"] = a.AppId;
    env["DAPR_GRPC_PORT"] = Num(a.DaprGrpcPort);
    env["DAPR_HTTP_PORT"] = Num(a.DaprHttpPort);
    var exe = ResolveExe(a.Exe);
    return new ProcessSpec(a.AppId, exe, [], env, Path.GetDirectoryName(exe)!);
  }

  private string ResolveExe(string exe) => Path.GetFullPath(Path.Combine(Home, Expand(exe, "services.json exe")));

  private Dictionary<string, string> ExpandAll(IReadOnlyDictionary<string, string>? env, string where) =>
    (env ?? new Dictionary<string, string>()).ToDictionary(kv => kv.Key, kv => Expand(kv.Value, where));

  private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

  private static int ParsePort(string raw, string what) =>
    int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536
      ? port
      : throw new FormatException($"{what}: '{raw}' is not a port number");
}

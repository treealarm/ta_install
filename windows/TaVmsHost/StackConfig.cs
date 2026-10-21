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
// merged environment, the manifest, and the command line of every process it will run.
internal sealed class StackConfig
{
  public required string Home { get; init; }
  public required string Data { get; init; }
  public required IReadOnlyDictionary<string, string> Env { get; init; }
  public required Manifest Manifest { get; init; }

  // The host's own log and every child's console output. Not under VMS_LOG_PATH: VmsLogger takes
  // each folder there for an app id and would show these unstructured files as one more service.
  public static string HostLogDirOf(string data) => Path.Combine(data, "host-logs");
  public string HostLogDir => HostLogDirOf(Data);
  public string ComponentsDir => Path.Combine(Data, "dapr", "components");
  public string DaprConfigPath => Path.Combine(Data, "dapr", "config.yaml");

  public static IReadOnlyList<string> EnvFilePaths(string home, string data) =>
  [
    Path.Combine(home, "config", "ta_vms.env"),
    Path.Combine(home, "config", "windows.env"),
    Path.Combine(data, "config", "site.env"),
  ];

  public static StackConfig Load(string home, string data)
  {
    var builtIns = new Dictionary<string, string>
    {
      ["TAVMS_HOME"] = home,
      ["TAVMS_DATA"] = data,
    };
    return new StackConfig
    {
      Home = home,
      Data = data,
      Env = EnvFile.Merge(builtIns, EnvFilePaths(home, data)),
      Manifest = Manifest.Load(Path.Combine(home, "config", "services.json")),
    };
  }

  public string Require(string key) =>
    Env.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
      ? value
      : throw new InvalidOperationException(
          $"Required environment variable '{key}' is not set (see {string.Join(", ", EnvFilePaths(Home, Data))})");

  public int RequirePort(string key) => ParsePort(Require(key), key);

  public string Expand(string value, string where) => EnvFile.Expand(value, Env, where);

  public int? ReadyPort(InfraProcess p) =>
    p.ReadyPort is null ? null : ParsePort(Expand(p.ReadyPort, $"services.json {p.Name}.readyPort"), $"{p.Name}.readyPort");

  public int AppPort(DaprApp a) => ParsePort(Expand(a.AppPort, $"services.json {a.AppId}.appPort"), $"{a.AppId}.appPort");

  public ProcessSpec Infra(InfraProcess p) => new(
    p.Name,
    ResolveExe(p.Exe),
    (p.Args ?? []).Select(arg => Expand(arg, $"services.json {p.Name}.args")).ToList(),
    ExpandAll(p.Env, $"services.json {p.Name}.env"),
    Path.GetDirectoryName(ResolveExe(p.Exe))!);

  // The same flags compose gives every sidecar, except that here all of them share one localhost,
  // so each needs ports of its own.
  public ProcessSpec Sidecar(DaprApp a)
  {
    var args = new List<string>
    {
      "--app-id", a.AppId,
      "--app-port", Num(AppPort(a)),
      "--app-protocol", a.Protocol,
      "--dapr-http-port", Num(a.DaprHttpPort),
      "--dapr-grpc-port", Num(a.DaprGrpcPort),
      "--dapr-internal-grpc-port", Num(a.DaprInternalGrpcPort),
      "--metrics-port", Num(a.MetricsPort),
      "--resources-path", ComponentsDir,
      "--config", DaprConfigPath,
      "--log-level", "info",
    };
    if (a.Placement)
    {
      // 127.0.0.1 rather than localhost, which may resolve to ::1 first: placement listens on
      // the IPv4 loopback only (services.json).
      args.AddRange(["--placement-host-address", $"127.0.0.1:{RequirePort("DAPR_PLACEMENT_PORT")}"]);
    }
    var daprd = ResolveExe(@"dapr\daprd.exe");
    var env = new Dictionary<string, string> { ["DAPR_HOST_IP"] = Require("DAPR_HOST_IP") };
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

using System.Text.Json;

namespace TaVmsHost;

// services.json: what the host runs, in start order. It plays the part docker-compose.yml plays
// for ta_install, restricted to what differs per process: the shared environment comes from the
// env files, and every daprd gets the same flags apart from the ones named here.
//
// Strings may use ${NAME} from the merged environment (EnvFile.Expand), so an application's port is
// written once, in .env, and both the application and its sidecar read it from there. An entry
// with "roles" runs only on a machine of one of those roles (Role); one without runs on all. An
// infrastructure entry with "when" runs only where that expands to 1 (0: not on this machine).
internal sealed record Manifest(
  IReadOnlyList<InfraProcess> Infrastructure,
  IReadOnlyList<DaprApp> Apps)
{
  private static readonly JsonSerializerOptions Options = new()
  {
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
  };

  // Every entry, for every role. What one machine runs is ForRole.
  public static Manifest Load(string path)
  {
    var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), Options)
      ?? throw new FormatException($"{path}: empty manifest");
    if (manifest.Apps is null || manifest.Apps.Count == 0)
      throw new FormatException($"{path}: no apps");
    if (manifest.Infrastructure is null)
      throw new FormatException($"{path}: no infrastructure list");

    var roles = manifest.Infrastructure.Select(i => (i.Name, i.Roles))
      .Concat(manifest.Apps.Select(a => (Name: a.AppId, a.Roles)));
    foreach (var (name, entryRoles) in roles)
    {
      foreach (var role in entryRoles ?? [])
        Roles.Parse(role, $"{path} {name}.roles");
    }
    return manifest;
  }

  // What a machine of this role runs, with ${...} in app ids expanded — a node's carry its name —
  // so that the sidecar, its configuration, its token and its log all go by the id daprd gets.
  // Names are unique per role: two entries may share one when no role runs both.
  public Manifest ForRole(Role role, IReadOnlyDictionary<string, string> env)
  {
    var infra = Infrastructure.Where(i => RunsOn(i.Roles, role) && Wanted(i, env)).ToList();
    var apps = Apps.Where(a => RunsOn(a.Roles, role))
      .Select(a => a with { AppId = EnvFile.Expand(a.AppId, env, $"services.json {a.AppId}.appId") })
      .ToList();
    if (apps.Count == 0)
      throw new FormatException($"services.json: no apps for the role {role.Name()}");

    var names = infra.Select(i => i.Name).Concat(apps.SelectMany(a => new[] { a.AppId, a.SidecarName }));
    var duplicate = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
    if (duplicate is not null)
      throw new FormatException($"services.json: '{duplicate.Key}' is named twice for the role {role.Name()}");
    return new Manifest(infra, apps);
  }

  private static bool RunsOn(IReadOnlyList<string>? roles, Role role) =>
    roles is null || roles.Contains(role.Name());

  private static bool Wanted(InfraProcess p, IReadOnlyDictionary<string, string> env) =>
    p.When is null || EnvFile.Expand(p.When, env, $"services.json {p.Name}.when") switch
    {
      "1" => true,
      "0" => false,
      var other => throw new FormatException($"services.json {p.Name}.when: '{other}' is neither 1 nor 0"),
    };
}

// placement, MediaMTX, Keycloak, Consul, Sentry: no sidecar. The next process starts once
// ReadyPort accepts a connection at ReadyAddress (the loopback unless the process listens on the
// machine's address only), or at once when there is none: nothing else waits for Keycloak, which
// only web-vms calls, and only when somebody logs in. Ports are the TCP ports it listens on: when it
// exits, the host names whatever other process holds one of them, which its own log does not.
internal sealed record InfraProcess(
  string Name,
  string Exe,
  IReadOnlyList<string>? Args,
  IReadOnlyDictionary<string, string>? Env,
  string? ReadyPort,
  string? ReadyAddress,
  IReadOnlyList<string>? Roles,
  string? When,
  IReadOnlyList<string>? Ports = null);

internal sealed record DaprApp(
  string AppId,
  string Exe,
  string Protocol,
  string AppPort,
  int DaprHttpPort,
  int DaprGrpcPort,
  int DaprInternalGrpcPort,
  int MetricsPort,
  bool Placement,
  IReadOnlyDictionary<string, string>? Env,
  IReadOnlyList<string>? Roles)
{
  public string SidecarName => $"{AppId}.dapr";
}

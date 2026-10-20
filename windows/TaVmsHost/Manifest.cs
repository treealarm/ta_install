using System.Text.Json;

namespace TaVmsHost;

// services.json: what the host runs, in start order. It plays the part docker-compose.yml plays
// for ta_install, restricted to what differs per process: the shared environment comes from the
// env files, and every daprd gets the same flags apart from the ones named here.
//
// Strings may use ${NAME} from the merged environment (EnvFile.Expand), so an application's port is
// written once, in .env, and both the application and its sidecar read it from there.
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

  public static Manifest Load(string path)
  {
    var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), Options)
      ?? throw new FormatException($"{path}: empty manifest");
    if (manifest.Apps is null || manifest.Apps.Count == 0)
      throw new FormatException($"{path}: no apps");
    if (manifest.Infrastructure is null)
      throw new FormatException($"{path}: no infrastructure list");

    var names = manifest.Infrastructure.Select(i => i.Name)
      .Concat(manifest.Apps.SelectMany(a => new[] { a.AppId, a.SidecarName }));
    var duplicate = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
    if (duplicate is not null)
      throw new FormatException($"{path}: '{duplicate.Key}' is named twice");
    return manifest;
  }
}

// placement and MediaMTX: no sidecar, ready once ReadyPort accepts a connection.
internal sealed record InfraProcess(
  string Name,
  string Exe,
  IReadOnlyList<string>? Args,
  IReadOnlyDictionary<string, string>? Env,
  string ReadyPort);

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
  IReadOnlyDictionary<string, string>? Env)
{
  public string SidecarName => $"{AppId}.dapr";
}

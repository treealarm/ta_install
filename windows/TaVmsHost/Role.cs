using System.Text.RegularExpressions;

namespace TaVmsHost;

// What this machine is in a deployment (ta_vms CLAUDE.md, "Distributed layout"), from TAVMS_ROLE:
//   standalone  everything on this one machine, sidecars found through sqlite (windows.env)
//   core        the same, and the infrastructure other machines use: Consul's server, Dapr Sentry,
//               placement, PostgreSQL, RabbitMQ and MediaMTX on the machine's address
//   node        only the services next to this machine's cameras, under app ids that carry its name
// The name resolver follows from it: sqlite on one machine, Consul across several.
internal enum Role
{
  Standalone,
  Core,
  Node,
}

internal static partial class Roles
{
  public static string Name(this Role role) => role.ToString().ToLowerInvariant();

  public static Role Parse(string value, string where) => value switch
  {
    "standalone" => Role.Standalone,
    "core" => Role.Core,
    "node" => Role.Node,
    _ => throw new InvalidOperationException($"{where}: '{value}' is not a role: expected standalone, core or node"),
  };

  // A node's name goes into its app ids (vms-<name>) and is its Consul node name, so it is held to
  // what both accept; "core" is the core's own Consul node name.
  [GeneratedRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")]
  private static partial Regex NodeNamePattern();

  public static string RequireNodeName(string name, string where) =>
    NodeNamePattern().IsMatch(name) && name != "core"
      ? name
      : throw new InvalidOperationException(
          $"{where}: '{name}' cannot name a node: lower-case letters, digits and '-', and not 'core'");
}

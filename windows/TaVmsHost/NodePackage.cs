using System.Formats.Tar;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace TaVmsHost;

// The package that joins a node to its core: a gzipped tar that the core's VmsCfg makes (ta_vms
// dotnet/VmsCfg/Grpc/NodePackage.cs) and scripts/distributed.sh reads on Linux, so that a core on
// either platform can take a node on either.
//
//   shared.env           CORE_ADDRESS, CONSUL_ENCRYPT, DAPR_TRUST_ANCHORS
//   core.env             the core's database, broker, MediaMTX and Keycloak credentials, and where
//                        each of those services is
//   name                 the node's name
//   tokens/<app>.token   a Sentry token for each of the node's app ids, and for nothing else
//   tls/ca.crt           the core's TLS certificate authority
//   tls/server.crt       the node's certificate for its address, followed by the authority, and
//   tls/server.key       its key: what the node presents on the LAN (none in a package from a
//                        core made before it had an authority)
//   nodes                in a package from before VmsCfg made them: a list of pairs, not read
internal static partial class NodePackage
{
  [GeneratedRegex(@"^(shared\.env|nodes|core\.env|name|tokens/[a-z0-9][a-z0-9-]*\.token|tls/(ca\.crt|server\.crt|server\.key))$")]
  private static partial Regex EntryName();

  // Required in core.env: a node cannot reach the core's services without them, and its web logs
  // users in at the core's Keycloak and seals WHEP sessions as every other web does.
  public static readonly string[] RequiredCoreKeys =
  [
    "POSTGRES_PORT", "POSTGRES_USER", "POSTGRES_PASSWORD", "POSTGRES_STATE_DB", "POSTGRES_FS_DB",
    "RABBITMQ_PORT", "RABBITMQ_DEFAULT_USER", "RABBITMQ_DEFAULT_PASS",
    "KEYCLOAK_PORT", "KEYCLOAK_ADMIN_USER", "KEYCLOAK_ADMIN_PASSWORD", "KEYCLOAK_REALM_NAME", "KEYCLOAK_CLIENT_ID",
    "VMS_WHEP_SESSION_KEY",
  ];

  private const long MaxEntryBytes = 1 << 20;

  // Only the files a package may hold, as text; anything else — a link, a path that climbs out
  // with "..", an entry this format does not know — refuses the whole package before any of it
  // is written anywhere.
  public static Dictionary<string, string> Read(string path)
  {
    var files = new Dictionary<string, string>(StringComparer.Ordinal);
    using var stream = File.OpenRead(path);
    using var gzip = new GZipStream(stream, CompressionMode.Decompress);
    using var tar = new TarReader(gzip);
    while (tar.GetNextEntry() is { } entry)
    {
      var name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
      if (entry.EntryType == TarEntryType.Directory && (name is "" or "tokens/" || name == "tokens"))
        continue;
      if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || !EntryName().IsMatch(name))
        throw new InvalidDataException($"{path}: '{entry.Name}' ({entry.EntryType}) does not belong in a node package");
      if (entry.Length > MaxEntryBytes)
        throw new InvalidDataException($"{path}: '{entry.Name}' is too large for a node package");
      using var reader = new StreamReader(entry.DataStream ?? Stream.Null);
      if (!files.TryAdd(name, reader.ReadToEnd()))
        throw new InvalidDataException($"{path}: '{name}' appears twice");
    }
    return files;
  }
}

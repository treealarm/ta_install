using System.Formats.Tar;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace TaVmsHost;

// The package that joins a node to its core: a gzipped tar, the format scripts/distributed.sh makes
// and reads on Linux, so that a core on either platform can take a node on either.
//
//   shared.env           CORE_ADDRESS, CONSUL_ENCRYPT, DAPR_TRUST_ANCHORS
//   nodes                every node's pair for MEDIA_SERVER_PAIRS, one per line
//   core.env             the core's database, broker, MediaMTX and Keycloak credentials (CoreKeys)
//   name                 the node's name
//   tokens/<app>.token   a Sentry token for each of the node's app ids, and for nothing else
//
// Everything in it is written with '\n' and without a byte-order mark: on Linux it is read by sed
// and compose, and a '\r' would end up inside the values.
internal static partial class NodePackage
{
  [GeneratedRegex(@"^(shared\.env|nodes|core\.env|name|tokens/[a-z0-9][a-z0-9-]*\.token)$")]
  private static partial Regex EntryName();

  // What a node takes from its core, the same list distributed.sh's CORE_KEYS names: everything
  // else is the same in every release, or belongs to the machine (paths, hosts) — and a Windows
  // core's would be Windows paths, of no use to a node on Linux.
  [GeneratedRegex(@"^(POSTGRES_(PORT|USER|PASSWORD)|POSTGRES_[A-Z0-9_]+_DB|RABBITMQ_(PORT|DEFAULT_USER|DEFAULT_PASS)|VMS_MEDIAMTX_(PUBLISH|READ)_(USER|PASS)|KEYCLOAK_(PORT|ADMIN_USER|ADMIN_PASSWORD|REALM_NAME|CLIENT_ID)|VMS_WHEP_SESSION_KEY)$")]
  public static partial Regex CoreKeys();

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

  public static void Write(string path, IReadOnlyDictionary<string, string> files)
  {
    using var stream = File.Create(path);
    using var gzip = new GZipStream(stream, CompressionLevel.Optimal);
    using var tar = new TarWriter(gzip, TarEntryFormat.Pax);
    foreach (var (name, content) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
    {
      if (!EntryName().IsMatch(name)) throw new ArgumentException($"'{name}' does not belong in a node package");
      tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
      {
        // 0600: on Linux the files are a node's credentials.
        Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        ModificationTime = DateTimeOffset.UtcNow,
        DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content.ReplaceLineEndings("\n"))),
      });
    }
  }

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

using System.Text;
using System.Text.RegularExpressions;

namespace TaVmsHost;

// Reads the stack's environment from .env-style files, layered: each later file overrides the
// earlier ones, the way compose's env_file applies one .env to every service.
//
//   <home>\config\ta_vms.env           ta_install's own .env, the values every deployment shares
//   <home>\config\windows.env          only what differs on Windows (hosts, paths, absent services)
//   <data>\distributed\shared.env      a core or node: the deployment's core address, Consul key and
//                                      root of trust (TaVmsHost core, or the node's package)
//   <data>\distributed\core.env        a node: the core's database, broker and MediaMTX credentials
//   <data>\config\site.env             this installation: passwords, ports, storage paths, role
//
// The first two are replaced by every upgrade; the distributed files are written once, by
// TaVmsHost core or join, exactly as their Linux counterparts (scripts/distributed.sh); site.env is
// written by the installer and then belongs to the administrator.
//
// The syntax is the subset of compose's that these files use: KEY=VALUE, blanks around '=',
// optional matching quotes, a double-quoted value over several lines, '#' comments (inline only
// after whitespace in an unquoted value).
// ${NAME} expands to a variable defined earlier in the merge or to one of the host's own
// (TAVMS_HOME, TAVMS_DATA). An unknown name is an error, never an empty string: a path that
// silently lost its root would be created somewhere nobody looks.
internal static partial class EnvFile
{
  [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
  private static partial Regex KeyPattern();

  [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
  private static partial Regex ReferencePattern();

  public static Dictionary<string, string> Merge(
    IReadOnlyDictionary<string, string> builtIns, IEnumerable<string> paths)
  {
    var merged = new Dictionary<string, string>(builtIns, StringComparer.Ordinal);
    foreach (var path in paths)
    {
      Apply(merged, path, File.ReadAllLines(path));
    }
    return merged;
  }

  internal static void Apply(Dictionary<string, string> merged, string source, IEnumerable<string> lines)
  {
    using var line = lines.GetEnumerator();
    var lineNo = 0;
    while (line.MoveNext())
    {
      lineNo++;
      var raw = line.Current;
      var text = raw.Trim();
      if (text.Length == 0 || text.StartsWith('#')) continue;

      var eq = text.IndexOf('=');
      if (eq <= 0)
        throw new FormatException($"{source}:{lineNo}: expected KEY=VALUE, got '{raw}'");

      var key = text[..eq].Trim();
      if (!KeyPattern().IsMatch(key))
        throw new FormatException($"{source}:{lineNo}: '{key}' is not a valid variable name");

      var at = lineNo;
      var value = text[(eq + 1)..].Trim();
      // An opening double quote with no closing one on the line: the value runs on until a line
      // that ends in one, as compose reads it. That is how a certificate travels (shared.env).
      if (value.StartsWith('"') && value.IndexOf('"', 1) < 0)
      {
        var joined = new StringBuilder(value);
        do
        {
          if (!line.MoveNext())
            throw new FormatException($"{source}:{at}: the value of '{key}' opens a quote that is never closed");
          lineNo++;
          joined.Append('\n').Append(line.Current.TrimEnd());
        } while (!line.Current.TrimEnd().EndsWith('"'));
        value = joined.ToString();
      }
      merged[key] = Expand(Unquote(value), merged, $"{source}:{at}");
    }
  }

  // The other way round, for the files TaVmsHost writes (site.env, a node package): a value as it
  // has to appear after '=' to be read back unchanged by Apply, and by compose and sed on Linux.
  // The error names the key, never the value: it may be a password.
  public static string Quote(string key, string value)
  {
    if (value.Contains("${"))
      throw new FormatException($"The value of {key} cannot be written to an env file: '${{' would be expanded on reading");
    if (!value.Any(c => char.IsWhiteSpace(c) || c is '#' or '"' or '\''))
      return value;
    if (!value.Contains('"'))
      return $"\"{value}\"";
    if (!value.Contains('\'') && !value.Contains('\n'))
      return $"'{value}'";
    throw new FormatException($"The value of {key} cannot be written to an env file: it holds both kinds of quote");
  }

  // Also used for the manifest's arguments and per-process env, so they can name the same values.
  public static string Expand(string value, IReadOnlyDictionary<string, string> env, string where) =>
    ReferencePattern().Replace(value, m =>
      env.TryGetValue(m.Groups[1].Value, out var v)
        ? v
        : throw new FormatException($"{where}: ${{{m.Groups[1].Value}}} is not defined"));

  private static string Unquote(string value)
  {
    if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
      return value[1..^1];

    // Unquoted: " #" starts a comment, a bare '#' (in a password, say) does not.
    for (var i = 1; i < value.Length; i++)
    {
      if (value[i] == '#' && char.IsWhiteSpace(value[i - 1]))
        return value[..i].TrimEnd();
    }
    return value;
  }
}

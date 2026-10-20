using System.Text.RegularExpressions;

namespace TaVmsHost;

// Reads the stack's environment from .env-style files, layered: each later file overrides the
// earlier ones, the way compose's env_file applies one .env to every service.
//
//   <home>\config\ta_vms.env  ta_install's own .env, the values every deployment shares
//   <home>\config\windows.env only what differs on Windows (hosts, paths, the absent services)
//   <data>\config\site.env    this installation: passwords, ports, storage paths
//
// The first two are replaced by every upgrade; site.env is written once by the installer and then
// belongs to the administrator.
//
// The syntax is the subset of compose's that ta_install/.env uses: KEY=VALUE, blanks around '=',
// optional matching quotes, '#' comments (inline only after whitespace in an unquoted value).
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
    var lineNo = 0;
    foreach (var raw in lines)
    {
      lineNo++;
      var line = raw.Trim();
      if (line.Length == 0 || line.StartsWith('#')) continue;

      var eq = line.IndexOf('=');
      if (eq <= 0)
        throw new FormatException($"{source}:{lineNo}: expected KEY=VALUE, got '{raw}'");

      var key = line[..eq].Trim();
      if (!KeyPattern().IsMatch(key))
        throw new FormatException($"{source}:{lineNo}: '{key}' is not a valid variable name");

      var value = Unquote(line[(eq + 1)..].Trim());
      merged[key] = Expand(value, merged, $"{source}:{lineNo}");
    }
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

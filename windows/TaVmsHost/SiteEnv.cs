using System.Text;
using System.Text.RegularExpressions;

namespace TaVmsHost;

// Changes to site.env made by TaVmsHost (core, join): a key already there gets its new value on its
// own line, the rest are appended; comments, order and every other line stay as the administrator
// left them, and so do the file's line endings.
internal static partial class SiteEnv
{
  [GeneratedRegex(@"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=")]
  private static partial Regex KeyLine();

  public static void Set(string path, IReadOnlyList<(string Key, string Value)> values)
  {
    var text = File.Exists(path) ? File.ReadAllText(path) : "";
    var newline = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : Environment.NewLine;
    var lines = text.Length == 0 ? [] : text.TrimEnd('\r', '\n').Split(newline).ToList();

    foreach (var (key, value) in values)
    {
      if (value.Contains('\n'))
        throw new FormatException($"{path}: the value of {key} would run over several lines");
      var line = $"{key}={EnvFile.Quote(key, value)}";
      var at = lines.FindIndex(l => KeyLine().Match(l) is { Success: true } m && m.Groups[1].Value == key);
      if (at < 0)
      {
        lines.Add(line);
        continue;
      }
      lines[at] = line;
      // Later lines of the same key would win on reading; one line per key, the one set here.
      for (var i = lines.Count - 1; i > at; i--)
      {
        if (KeyLine().Match(lines[i]) is { Success: true } m && m.Groups[1].Value == key) lines.RemoveAt(i);
      }
    }
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, string.Join(newline, lines) + newline, new UTF8Encoding(false));
  }
}

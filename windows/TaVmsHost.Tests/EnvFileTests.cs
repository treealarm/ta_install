namespace TaVmsHost.Tests;

public class EnvFileTests
{
  private static Dictionary<string, string> Parse(params string[] lines)
  {
    var env = new Dictionary<string, string> { ["TAVMS_DATA"] = @"C:\Data" };
    EnvFile.Apply(env, "test.env", lines);
    return env;
  }

  [Fact]
  public void Reads_the_syntax_ta_install_env_uses()
  {
    var env = Parse(
      "# comment",
      "",
      "PLAIN=value",
      "VMS_STORAGE_BASE_PATH = /data/media_storage",
      "QUOTED=\"vmscfg\"",
      "SINGLE='x y'",
      "EMPTY=",
      "COMMENTED=abc # trailing",
      "HASH_IN_VALUE=pa#ss",
      "TEMPLATE=https://tile.openstreetmap.org/{z}/{x}/{y}.png");

    Assert.Equal("value", env["PLAIN"]);
    Assert.Equal("/data/media_storage", env["VMS_STORAGE_BASE_PATH"]);
    Assert.Equal("vmscfg", env["QUOTED"]);
    Assert.Equal("x y", env["SINGLE"]);
    Assert.Equal("", env["EMPTY"]);
    Assert.Equal("abc", env["COMMENTED"]);
    Assert.Equal("pa#ss", env["HASH_IN_VALUE"]);
    Assert.Equal("https://tile.openstreetmap.org/{z}/{x}/{y}.png", env["TEMPLATE"]);
  }

  [Fact]
  public void Later_lines_override_earlier_ones_and_may_refer_to_them()
  {
    var env = Parse("A=1", "A=2", @"LOGS=${TAVMS_DATA}\logs", @"SUB=${LOGS}\vms");
    Assert.Equal("2", env["A"]);
    Assert.Equal(@"C:\Data\logs", env["LOGS"]);
    Assert.Equal(@"C:\Data\logs\vms", env["SUB"]);
  }

  [Fact]
  public void An_unknown_reference_is_an_error_not_an_empty_string()
  {
    var ex = Assert.Throws<FormatException>(() => Parse(@"LOGS=${NOPE}\logs"));
    Assert.Contains("test.env:1", ex.Message);
    Assert.Contains("NOPE", ex.Message);
  }

  [Theory]
  [InlineData("no equals sign")]
  [InlineData("=value")]
  [InlineData("1BAD=value")]
  public void A_malformed_line_names_its_place(string line)
  {
    var ex = Assert.Throws<FormatException>(() => Parse("OK=1", line));
    Assert.Contains("test.env:2", ex.Message);
  }
}

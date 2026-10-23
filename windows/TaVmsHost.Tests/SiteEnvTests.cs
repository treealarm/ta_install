namespace TaVmsHost.Tests;

public sealed class SiteEnvTests : IDisposable
{
  private readonly string _path = Path.Combine(Directory.CreateTempSubdirectory("tavms-site").FullName, "site.env");

  public void Dispose() => Directory.Delete(Path.GetDirectoryName(_path)!, true);

  [Fact]
  public void Keys_are_replaced_in_place_and_new_ones_appended_keeping_comments()
  {
    File.WriteAllText(_path, "# kept\r\nPOSTGRES_PORT=5434\r\nTAVMS_ROLE=standalone\r\n# also kept\r\nTAVMS_ROLE=x\r\n");
    SiteEnv.Set(_path, [("TAVMS_ROLE", "core"), ("DAPR_HOST_IP", "192.168.1.10"), ("NOTE", "a b")]);

    Assert.Equal("# kept\r\nPOSTGRES_PORT=5434\r\nTAVMS_ROLE=core\r\n# also kept\r\nDAPR_HOST_IP=192.168.1.10\r\nNOTE=\"a b\"\r\n",
      File.ReadAllText(_path));
  }

  [Fact]
  public void What_is_written_reads_back_unchanged()
  {
    string[] values = ["plain", "a b", "pa#ss", "it's", "say \"hi\"", @"C:\Program Files\x", ""];
    SiteEnv.Set(_path, values.Select((v, i) => ($"K{i}", v)).ToList());
    var env = new Dictionary<string, string>();
    EnvFile.Apply(env, _path, File.ReadAllLines(_path));
    for (var i = 0; i < values.Length; i++) Assert.Equal(values[i], env[$"K{i}"]);
  }

  [Fact]
  public void A_value_that_cannot_be_written_is_refused()
  {
    Assert.Throws<FormatException>(() => SiteEnv.Set(_path, [("K", "both ' and \"")]));
    Assert.Throws<FormatException>(() => SiteEnv.Set(_path, [("K", "${TAVMS_DATA}")]));
    Assert.Throws<FormatException>(() => SiteEnv.Set(_path, [("K", "two\nlines")]));
  }
}

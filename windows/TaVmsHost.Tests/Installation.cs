namespace TaVmsHost.Tests;

// An installation in temporary directories, made of the real files this repository ships —
// ../.env, windows/config/windows.env and services.json — and a site.env like the one the
// installer writes, so that a variable renamed in one of them without the others fails here rather
// than on a customer's machine.
internal sealed class Installation : IDisposable
{
  public string Home { get; }
  public string Data { get; }

  public Installation()
  {
    var repo = FindRepoRoot();
    Home = Directory.CreateTempSubdirectory("tavms-home").FullName;
    Data = Directory.CreateTempSubdirectory("tavms-data").FullName;

    Directory.CreateDirectory(Path.Combine(Home, "config"));
    File.Copy(Path.Combine(repo, ".env"), Path.Combine(Home, "config", "ta_vms.env"));
    File.Copy(Path.Combine(repo, "windows", "config", "windows.env"), Path.Combine(Home, "config", "windows.env"));
    File.Copy(Path.Combine(repo, "windows", "config", "services.json"), Path.Combine(Home, "config", "services.json"));

    Directory.CreateDirectory(Path.Combine(Data, "config"));
    File.WriteAllLines(SiteEnv,
    [
      "POSTGRES_PORT=5433",
      "POSTGRES_PASSWORD=it's\\a\"secret",
      "RABBITMQ_PORT=5673",
      "RABBITMQ_DEFAULT_PASS=p@ss/word",
      "VMS_MEDIAMTX_PUBLISH_PASS=pub",
      "VMS_MEDIAMTX_READ_PASS=read",
      "KEYCLOAK_ADMIN_PASSWORD=kc-admin",
      @"VMS_STORAGE_BASE_PATH=D:\Archive",
    ]);
  }

  public string SiteEnv => StackConfig.SiteEnvPath(Data);

  public StackConfig Load() => StackConfig.Load(Home, Data);

  public void AppendSite(params string[] lines) => File.AppendAllLines(SiteEnv, lines);

  public void Dispose()
  {
    Directory.Delete(Home, true);
    Directory.Delete(Data, true);
  }

  private static string FindRepoRoot()
  {
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
      if (File.Exists(Path.Combine(dir.FullName, "windows", "config", "services.json"))) return dir.FullName;
    }
    throw new DirectoryNotFoundException("ta_install checkout not found above the test binaries");
  }
}

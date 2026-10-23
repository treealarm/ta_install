using Microsoft.Extensions.Logging.Abstractions;

namespace TaVmsHost.Tests;

// Loads the real files this repository ships — ../.env, windows/config/windows.env and
// services.json — with a site.env like the one the installer writes, so that a variable renamed in
// one of them without the others fails here rather than on a customer's machine.
public sealed class StackConfigTests : IDisposable
{
  private readonly string _home;
  private readonly string _data;

  public StackConfigTests()
  {
    var repo = FindRepoRoot();
    _home = Directory.CreateTempSubdirectory("tavms-home").FullName;
    _data = Directory.CreateTempSubdirectory("tavms-data").FullName;

    Directory.CreateDirectory(Path.Combine(_home, "config"));
    File.Copy(Path.Combine(repo, ".env"), Path.Combine(_home, "config", "ta_vms.env"));
    File.Copy(Path.Combine(repo, "windows", "config", "windows.env"), Path.Combine(_home, "config", "windows.env"));
    File.Copy(Path.Combine(repo, "windows", "config", "services.json"), Path.Combine(_home, "config", "services.json"));

    Directory.CreateDirectory(Path.Combine(_data, "config"));
    File.WriteAllLines(Path.Combine(_data, "config", "site.env"),
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

  public void Dispose()
  {
    Directory.Delete(_home, true);
    Directory.Delete(_data, true);
  }

  private StackConfig Load() => StackConfig.Load(_home, _data);

  [Fact]
  public void Site_env_overrides_windows_env_which_overrides_the_shared_env()
  {
    var config = Load();
    Assert.Equal("127.0.0.1", config.Env["POSTGRES_HOST"]);          // windows.env over .env
    Assert.Equal("5433", config.Env["POSTGRES_PORT"]);                // site.env over .env
    Assert.Equal(@"D:\Archive", config.Env["VMS_STORAGE_BASE_PATH"]); // site.env over windows.env
    Assert.Equal(_data + @"\logs", config.Env["VMS_LOG_PATH"]);       // ${TAVMS_DATA} expanded
    Assert.Equal("vms:vmsfs:vmsonvif:vmslogger", config.Env["MEDIA_SERVER_PAIRS"]);
  }

  [Fact]
  public void Every_process_in_the_manifest_resolves()
  {
    var config = Load();
    foreach (var infra in config.Manifest.Infrastructure)
    {
      config.Infra(infra);
      config.ReadyPort(infra);
    }
    foreach (var app in config.Manifest.Apps)
    {
      config.Sidecar(app);
      config.App(app);
    }
  }

  [Fact]
  public void Sidecar_ports_are_unique_on_the_one_localhost()
  {
    var config = Load();
    var ports = config.Manifest.Apps
      .SelectMany(a => new[] { a.DaprHttpPort, a.DaprGrpcPort, a.DaprInternalGrpcPort, a.MetricsPort, config.AppPort(a) })
      .ToList();
    Assert.Equal(ports.Count, ports.Distinct().Count());
  }

  // Windows reserves ports above 49152 for Hyper-V, WSL and WinNAT, per machine and per boot; one
  // there binds on one machine and is "access forbidden" on the next (see windows.env).
  [Fact]
  public void Every_fixed_port_is_below_the_windows_dynamic_range()
  {
    var config = Load();
    var ports = new Dictionary<string, int>
    {
      ["DAPR_PLACEMENT_PORT"] = config.RequirePort("DAPR_PLACEMENT_PORT"),
      ["POSTGRES_PORT"] = config.RequirePort("POSTGRES_PORT"),
      ["RABBITMQ_PORT"] = config.RequirePort("RABBITMQ_PORT"),
    };
    foreach (var infra in config.Manifest.Infrastructure)
    {
      if (config.ReadyPort(infra) is int ready) ports[$"{infra.Name}.readyPort"] = ready;
      foreach (var arg in config.Infra(infra).Args.Where(a => int.TryParse(a, out _)))
        ports[$"{infra.Name} {arg}"] = int.Parse(arg);
    }
    foreach (var app in config.Manifest.Apps)
    {
      ports[$"{app.AppId}.appPort"] = config.AppPort(app);
      ports[$"{app.AppId}.daprHttpPort"] = app.DaprHttpPort;
      ports[$"{app.AppId}.daprGrpcPort"] = app.DaprGrpcPort;
      ports[$"{app.AppId}.daprInternalGrpcPort"] = app.DaprInternalGrpcPort;
      ports[$"{app.AppId}.metricsPort"] = app.MetricsPort;
    }
    Assert.Empty(ports.Where(p => p.Value >= 49152).Select(p => $"{p.Key}={p.Value}"));
  }

  [Fact]
  public void An_app_gets_the_shared_env_plus_its_own_identity_and_sidecar()
  {
    var config = Load();
    var onvif = config.App(config.Manifest.Apps.Single(a => a.AppId == "vmsonvif"));
    Assert.Equal("vmsonvif", onvif.Env["APP_ID"]);
    Assert.Equal("41003", onvif.Env["DAPR_GRPC_PORT"]);
    Assert.Equal("3602", onvif.Env["DAPR_HTTP_PORT"]);
    Assert.Equal("vms", onvif.Env["OWN_SERVER_ID"]);
    Assert.Equal("vmscfg", onvif.Env["DAPR_CFG_APP_ID"]);
  }

  [Fact]
  public void Only_actor_sidecars_get_placement()
  {
    var config = Load();
    var withPlacement = config.Manifest.Apps
      .Where(a => config.Sidecar(a).Args.Contains("--placement-host-address"))
      .Select(a => a.AppId)
      .ToHashSet();
    Assert.Equal(["vmsonvif", "vmsonvif-actors", "vmsptztrackingsingleton", "vmssingleton"], withPlacement.Order(StringComparer.Ordinal));
  }

  [Fact]
  public void Not_installed_services_are_not_in_the_manifest()
  {
    var ids = Load().Manifest.Apps.Select(a => a.AppId).ToList();
    Assert.DoesNotContain("roitrc", ids);
    Assert.DoesNotContain("analytics-worker", ids);
    Assert.DoesNotContain("vmssquaresingleton", ids);
  }

  [Fact]
  public void Components_carry_the_credentials_escaped()
  {
    var config = Load();

    var state = DaprComponents.StateStore(config);
    // libpq quoting inside a YAML double-quoted string: \' and \\ for libpq, then \\ and \" for YAML.
    Assert.Contains(@"password='it\\'s\\\\a\""secret'", state);
    Assert.Contains("port=5433", state);
    Assert.Contains("database='ta_state'", state);

    var pubsub = DaprComponents.PubSub(config);
    Assert.Contains("amqp://vms:p%40ss%2Fword@127.0.0.1:5673/", pubsub);
    Assert.Contains("name: orderpubsub", pubsub);
  }

  [Fact]
  public void Components_get_longer_than_daprs_default_to_connect()
  {
    var config = Load();
    Assert.Contains("\n  initTimeout: 30s", DaprComponents.StateStore(config));
    Assert.Contains("\n  initTimeout: 30s", DaprComponents.PubSub(config));
  }

  [Fact]
  public void Sidecars_register_on_the_loopback_through_sqlite()
  {
    var config = Load();
    foreach (var app in config.Manifest.Apps)
      Assert.Equal("127.0.0.1", config.Sidecar(app).Env["DAPR_HOST_IP"]);
    Assert.Contains("component: \"sqlite\"", DaprComponents.Configuration(config));
  }

  [Fact]
  public void Sidecars_listen_on_the_loopback_only()
  {
    var config = Load();
    foreach (var app in config.Manifest.Apps)
    {
      var args = config.Sidecar(app).Args.ToList();
      foreach (var flag in new[] { "--dapr-listen-addresses", "--dapr-internal-grpc-listen-address", "--metrics-listen-address" })
        Assert.Equal("127.0.0.1", args[args.IndexOf(flag) + 1]);
    }
  }

  [Fact]
  public void Name_resolution_is_validated()
  {
    File.AppendAllLines(Path.Combine(_data, "config", "site.env"), ["TAVMS_DAPR_NAME_RESOLUTION=mdns"]);
    Assert.Contains("component: \"mdns\"", DaprComponents.Configuration(Load()));

    File.AppendAllLines(Path.Combine(_data, "config", "site.env"), ["TAVMS_DAPR_NAME_RESOLUTION=consul"]);
    Assert.Throws<InvalidOperationException>(() => DaprComponents.Configuration(Load()));
  }

  [Fact]
  public void Every_database_the_env_names_is_found()
  {
    var names = Infrastructure.DatabaseNames(Load().Env, NullLogger.Instance).Select(n => n.Name).ToList();
    Assert.Equal(["ta_analytics", "ta_cfg", "ta_fs", "ta_keycloak", "ta_state"], names);
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

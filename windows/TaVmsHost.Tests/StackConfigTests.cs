using Microsoft.Extensions.Logging.Abstractions;

namespace TaVmsHost.Tests;

// A standalone installation: one machine, as every installation was before roles. What a core
// and a node get is in DistributedTests.
public sealed class StackConfigTests : IDisposable
{
  private readonly Installation _install = new();

  public void Dispose() => _install.Dispose();

  private StackConfig Load() => _install.Load();

  [Fact]
  public void Site_env_overrides_windows_env_which_overrides_the_shared_env()
  {
    var config = Load();
    Assert.Equal("127.0.0.1", config.Env["POSTGRES_HOST"]);          // windows.env over .env
    Assert.Equal("5433", config.Env["POSTGRES_PORT"]);                // site.env over .env
    Assert.Equal(@"D:\Archive", config.Env["VMS_STORAGE_BASE_PATH"]); // site.env over windows.env
    Assert.Equal(_install.Data + @"\logs", config.Env["VMS_LOG_PATH"]);       // ${TAVMS_DATA} expanded
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
  public void Standalone_runs_neither_consul_nor_sentry_nor_tls()
  {
    var config = Load();
    Assert.Equal(Role.Standalone, config.Role);
    Assert.Equal(["placement", "mediamtx", "keycloak", "mt_admin"], config.Manifest.Infrastructure.Select(i => i.Name));
    Assert.Equal("127.0.0.1", config.Infra(config.Manifest.Infrastructure[0]).Args[3]);
    // Keycloak and MediaMTX's API on the loopback: web_srv and VmsUtils are on this machine.
    Assert.Equal("127.0.0.1", config.Infra(config.Manifest.Infrastructure.Single(i => i.Name == "keycloak")).Env["KC_HTTP_HOST"]);
    Assert.Equal("127.0.0.1:9997", config.Infra(config.Manifest.Infrastructure.Single(i => i.Name == "mediamtx")).Env["MTX_APIADDRESS"]);
    foreach (var app in config.Manifest.Apps)
    {
      var sidecar = config.Sidecar(app);
      Assert.DoesNotContain("--enable-mtls", sidecar.Args);
      Assert.Equal(config.DaprConfigPath, sidecar.Args[sidecar.Args.ToList().IndexOf("--config") + 1]);
      Assert.Equal(["DAPR_HOST_IP"], sidecar.Env.Keys);
    }
  }

  [Fact]
  public void Standalone_keeps_the_app_ids_and_server_id_it_always_had()
  {
    var config = Load();
    Assert.Equal(
      ["vmscfg", "vmsutils", "vmslogger", "vmsfs", "vmssingleton", "vmsonvif", "vmsonvif-actors",
       "vmsanalyticssingleton", "vmsptztrackingsingleton", "vms", "web-vms"],
      config.Manifest.Apps.Select(a => a.AppId));
    Assert.Equal("vms", config.App(config.Manifest.Apps.Single(a => a.AppId == "vmsonvif-actors")).Env["OWN_SERVER_ID"]);
  }

  [Fact]
  public void The_resolver_setting_of_old_is_refused_rather_than_ignored()
  {
    _install.AppendSite("TAVMS_DAPR_NAME_RESOLUTION=sqlite");
    var error = Assert.Throws<InvalidOperationException>(Load);
    Assert.Contains("TAVMS_ROLE", error.Message);
  }

  [Theory]
  [InlineData("TAVMS_ROLE=primary")]
  [InlineData("TAVMS_ROLE=node", "TAVMS_NODE_NAME=Hall")]
  [InlineData("TAVMS_ROLE=node", "TAVMS_NODE_NAME=core")]
  public void A_role_is_validated(params string[] lines)
  {
    _install.AppendSite(lines);
    Assert.Throws<InvalidOperationException>(Load);
  }

  [Fact]
  public void A_core_without_its_distributed_files_says_what_to_run()
  {
    _install.AppendSite("TAVMS_ROLE=core", "DAPR_HOST_IP=192.168.1.10");
    var error = Assert.Throws<InvalidOperationException>(Load);
    Assert.Contains("TaVmsHost core", error.Message);
  }

  [Fact]
  public void Every_database_the_env_names_is_found()
  {
    var names = Infrastructure.DatabaseNames(Load().Env, NullLogger.Instance).Select(n => n.Name).ToList();
    Assert.Equal(["ta_analytics", "ta_cfg", "ta_fs", "ta_keycloak", "ta_state"], names);
  }
}

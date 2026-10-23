using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace TaVmsHost.Tests;

// A core and a node made the way the installer makes them: TaVmsHost core on one installation,
// package there, join on another.
public sealed class DistributedTests : IDisposable
{
  private const string CoreAddress = "192.168.1.10";
  private const string NodeAddress = "192.168.1.20";
  private readonly Installation _core = new();
  private readonly Installation _node = new();

  public DistributedTests()
  {
    // A password with both kinds of quote cannot travel in a package (SiteEnvTests); a real one is
    // letters and digits (Install-TaVms.ps1, New-Secret).
    _core.AppendSite("POSTGRES_PASSWORD=core-pg-pass");
    Distributed.Core(_core.Load(), CoreAddress, TextWriter.Null);
    var package = Distributed.Package(_core.Load(), "hall", TextWriter.Null);
    // A node's site.env is the installer's header and storage paths only: its passwords are the core's.
    File.WriteAllLines(_node.SiteEnv, ["# site.env of a node"]);
    Distributed.Join(_node.Load(), package, NodeAddress, TextWriter.Null);
  }

  public void Dispose()
  {
    _core.Dispose();
    _node.Dispose();
  }

  [Fact]
  public void The_core_runs_consul_sentry_and_placement_on_its_address()
  {
    var config = _core.Load();
    Assert.Equal(Role.Core, config.Role);
    Assert.Equal(["consul-server", "sentry", "placement", "mediamtx", "keycloak", "mt_admin"],
      config.Manifest.Infrastructure.Select(i => i.Name));

    var consul = config.Infra(Infra(config, "consul-server"));
    Assert.Contains($"-bind={CoreAddress}", consul.Args);
    Assert.Contains("-client=127.0.0.1", consul.Args);
    Assert.Contains($"-encrypt={config.Env["CONSUL_ENCRYPT"]}", consul.Args);

    var placementSpec = Infra(config, "placement");
    var placement = config.Infra(placementSpec);
    Assert.Equal(CoreAddress, placement.Args[placement.Args.ToList().IndexOf("--listen-address") + 1]);
    Assert.Contains("--tls-enabled", placement.Args);
    // services.json names the token where TaVmsHost core puts it, and Sentry's files likewise.
    Assert.Equal(Slashes(config.TokenPath("dapr-placement")), Slashes(placement.Env["DAPR_SENTRY_TOKEN_FILE"]));
    Assert.True(File.Exists(config.TokenPath("dapr-placement")));
    var sentry = config.Infra(Infra(config, "sentry")).Args.ToList();
    Assert.True(File.Exists(Slashes(sentry[sentry.IndexOf("--config") + 1])));
    Assert.True(File.Exists(Path.Combine(Slashes(sentry[sentry.IndexOf("--issuer-credentials") + 1]), "issuer.key")));
    Assert.True(File.Exists(Slashes(placement.Args[placement.Args.ToList().IndexOf("--trust-anchors-file") + 1])));
    Assert.Equal(CoreAddress, config.ReadyAddress(placementSpec));
    Assert.Equal(CoreAddress, config.ReadyAddress(Infra(config, "sentry")));
  }

  [Fact]
  public void The_core_is_a_node_too_with_the_standalone_app_ids()
  {
    var config = _core.Load();
    Assert.Equal(11, config.Manifest.Apps.Count);
    Assert.Contains(config.Manifest.Apps, a => a.AppId == "vms");
    Assert.Equal("vms", config.App(App(config, "vmsonvif")).Env["OWN_SERVER_ID"]);
    Assert.Equal(CoreAddress, config.Env["VMS_MTX_HOST"]);
    Assert.Equal("127.0.0.1", config.Env["POSTGRES_HOST"]);
    Assert.Equal("vms:vmsfs:vmsonvif:vmslogger,vms-hall:vmsfs-hall:vmsonvif-hall:vmslogger-hall",
      config.Env["MEDIA_SERVER_PAIRS"]);
  }

  [Fact]
  public void A_node_runs_its_five_services_under_its_name()
  {
    var config = _node.Load();
    Assert.Equal(Role.Node, config.Role);
    Assert.Equal(["consul-agent"], config.Manifest.Infrastructure.Select(i => i.Name));
    Assert.Equal(["vmslogger-hall", "vmsfs-hall", "vmsonvif-hall", "vmsonvif-actors-hall", "vms-hall"],
      config.Manifest.Apps.Select(a => a.AppId));
    Assert.Equal("vms-hall", config.App(App(config, "vmsonvif-actors-hall")).Env["OWN_SERVER_ID"]);
    Assert.Equal("vms-hall.dapr", App(config, "vms-hall").SidecarName);

    var agent = config.Infra(config.Manifest.Infrastructure[0]);
    Assert.Contains($"-retry-join={CoreAddress}", agent.Args);
    Assert.Contains("-node=hall", agent.Args);
    Assert.Contains($"-bind={NodeAddress}", agent.Args);
  }

  [Fact]
  public void A_node_uses_the_cores_database_broker_and_mediamtx()
  {
    var node = _node.Load();
    var core = _core.Load();
    foreach (var key in new[] { "POSTGRES_HOST", "RABBITMQ_HOST", "VMS_MTX_HOST" })
      Assert.Equal(CoreAddress, node.Env[key]);
    foreach (var key in new[] { "POSTGRES_PORT", "POSTGRES_PASSWORD", "RABBITMQ_PORT", "RABBITMQ_DEFAULT_PASS", "VMS_MEDIAMTX_PUBLISH_PASS" })
      Assert.Equal(core.Env[key], node.Env[key]);
    Assert.Contains($"host='{CoreAddress}' port=5433", DaprComponents.StateStore(node));
    Assert.Contains($"@{CoreAddress}:5673/", DaprComponents.PubSub(node));
  }

  [Fact]
  public void Distributed_sidecars_get_mtls_their_own_consul_configuration_and_the_cores_placement()
  {
    foreach (var config in new[] { _core.Load(), _node.Load() })
    {
      foreach (var app in config.Manifest.Apps)
      {
        var sidecar = config.Sidecar(app);
        var args = sidecar.Args.ToList();
        string After(string flag) => args[args.IndexOf(flag) + 1];
        Assert.Contains("--enable-mtls", args);
        Assert.Equal($"{CoreAddress}:6055", After("--sentry-address"));
        Assert.Equal(config.SidecarConfigPath(app.AppId), After("--config"));
        Assert.Equal(config.Env["DAPR_HOST_IP"], After("--dapr-internal-grpc-listen-address"));
        Assert.Equal("127.0.0.1", After("--dapr-listen-addresses"));
        Assert.Equal("127.0.0.1", After("--metrics-listen-address"));
        if (app.Placement) Assert.Equal($"{CoreAddress}:6050", After("--placement-host-address"));
        Assert.StartsWith("-----BEGIN CERTIFICATE-----\n", sidecar.Env["DAPR_TRUST_ANCHORS"]);
        Assert.True(File.Exists(sidecar.Env["DAPR_SENTRY_TOKEN_FILE"]), sidecar.Env["DAPR_SENTRY_TOKEN_FILE"]);

        var yaml = DaprComponents.SidecarConfiguration(app);
        Assert.Contains("component: \"consul\"", yaml);
        Assert.Contains($"checkID: \"daprHealth:{app.AppId}\"", yaml);
        Assert.Contains($"http: \"http://127.0.0.1:{app.DaprHttpPort}/v1.0/healthz\"", yaml);
      }
    }
  }

  [Fact]
  public void A_node_holds_tokens_for_its_own_app_ids_only()
  {
    var tokens = Directory.GetFiles(Path.Combine(_node.Load().DistributedDir, "tokens")).Select(Path.GetFileName).Order();
    Assert.Equal(["vms-hall.token", "vmsfs-hall.token", "vmslogger-hall.token", "vmsonvif-actors-hall.token", "vmsonvif-hall.token"], tokens);
    Assert.False(Directory.Exists(Path.Combine(_node.Load().DistributedDir, "core")));
  }

  [Fact]
  public void Tokens_are_es256_jwts_that_verify_against_sentrys_jwks()
  {
    var config = _core.Load();
    var sentryConfig = File.ReadAllText(Path.Combine(config.DistributedDir, "sentry", "config.yaml"));
    var jwks = sentryConfig.Split("source: '")[1].Split("'")[0];
    var key = JsonDocument.Parse(jwks).RootElement.GetProperty("keys")[0];
    Assert.Equal("EC", key.GetProperty("kty").GetString());
    using var ec = ECDsa.Create(new ECParameters
    {
      Curve = ECCurve.NamedCurves.nistP256,
      Q = new ECPoint { X = FromBase64Url(key.GetProperty("x").GetString()!), Y = FromBase64Url(key.GetProperty("y").GetString()!) },
    });

    var token = File.ReadAllText(_node.Load().TokenPath("vmsonvif-hall"));
    Assert.DoesNotContain('\n', token);
    var parts = token.Split('.');
    Assert.True(ec.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), FromBase64Url(parts[2]),
      HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    var header = JsonDocument.Parse(FromBase64Url(parts[0])).RootElement;
    Assert.Equal("ES256", header.GetProperty("alg").GetString());
    Assert.Equal(key.GetProperty("kid").GetString(), header.GetProperty("kid").GetString());
    var payload = JsonDocument.Parse(FromBase64Url(parts[1])).RootElement;
    Assert.Equal("spiffe://public/ns/default/vmsonvif-hall", payload.GetProperty("sub").GetString());
    Assert.Equal("spiffe://localhost/ns/default/dapr-sentry", payload.GetProperty("aud")[0].GetString());
    Assert.False(payload.TryGetProperty("exp", out _));

    var placement = JsonDocument.Parse(FromBase64Url(File.ReadAllText(config.TokenPath("dapr-placement")).Split('.')[1])).RootElement;
    Assert.Equal("spiffe://localhost/ns/default/dapr-placement", placement.GetProperty("sub").GetString());
  }

  [Fact]
  public void The_issuer_chains_to_the_root_for_ten_years()
  {
    var sentry = Path.Combine(_core.Load().DistributedDir, "sentry");
    using var root = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(sentry, "ca.crt")));
    using var issuer = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(sentry, "issuer.crt")));
    using var chain = new X509Chain();
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(root);
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    Assert.True(chain.Build(issuer), string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));
    Assert.True(issuer.NotAfter > DateTime.UtcNow.AddYears(9));
    Assert.Equal("O=spiffe://localhost/ns/default/dapr-sentry", issuer.Subject);
    Assert.Contains(issuer.Extensions.OfType<X509BasicConstraintsExtension>(), e => e.CertificateAuthority);
    // Sentry takes the issuer's key in SEC1 form, as openssl writes it on a Linux core.
    Assert.StartsWith("-----BEGIN EC PRIVATE KEY-----", File.ReadAllText(Path.Combine(sentry, "issuer.key")));
    Assert.Equal(root.ExportCertificatePem(), _node.Load().Env["DAPR_TRUST_ANCHORS"]);
  }

  [Fact]
  public void The_package_is_what_a_linux_node_reads()
  {
    var files = NodePackage.Read(Path.Combine(_core.Load().DistributedDir, "node-hall.tar.gz"));
    Assert.All(files.Values, text => Assert.DoesNotContain('\r', text));
    Assert.Equal("hall\n", files["name"]);
    // Every key a node takes from its core, and nothing else: no Windows paths, no Keycloak.
    var keys = files["core.env"].Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('=')[0]).ToList();
    Assert.All(keys, k => Assert.Matches(NodePackage.CoreKeys(), k));
    Assert.Contains("POSTGRES_PASSWORD", keys);
    Assert.Contains("VMS_MEDIAMTX_READ_PASS", keys);
    // shared.env as distributed.sh writes it: CORE_ADDRESS on its own line, for sed.
    Assert.StartsWith($"CORE_ADDRESS={CoreAddress}\n", files["shared.env"]);
  }

  [Fact]
  public void A_second_package_for_the_same_node_lists_it_once()
  {
    Distributed.Package(_core.Load(), "hall", TextWriter.Null);
    Distributed.Package(_core.Load(), "yard", TextWriter.Null);
    Assert.Equal(["vms-hall:vmsfs-hall:vmsonvif-hall:vmslogger-hall", "vms-yard:vmsfs-yard:vmsonvif-yard:vmslogger-yard"],
      File.ReadAllLines(Path.Combine(_core.Load().DistributedDir, "nodes")));
  }

  [Fact]
  public void Neither_machine_becomes_a_core_or_joins_twice()
  {
    Assert.Throws<InvalidOperationException>(() => Distributed.Core(_core.Load(), CoreAddress, TextWriter.Null));
    var package = Path.Combine(_core.Load().DistributedDir, "node-hall.tar.gz");
    Assert.Throws<InvalidOperationException>(() => Distributed.Join(_node.Load(), package, NodeAddress, TextWriter.Null));
    Assert.Throws<InvalidOperationException>(() => Distributed.Package(_node.Load(), "yard", TextWriter.Null));
    Assert.Throws<ArgumentException>(() => Distributed.Core(new Installation().Load(), "127.0.0.1", TextWriter.Null));
  }

  [Fact]
  public void A_package_that_climbs_out_is_refused_before_anything_is_written()
  {
    using var other = new Installation();
    var hostile = Path.Combine(other.Data, "hostile.tar.gz");
    using (var stream = File.Create(hostile))
    using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
    using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
    {
      tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "name") { DataStream = new MemoryStream("yard\n"u8.ToArray()) });
      tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "tokens/../../config/site.env") { DataStream = new MemoryStream("X=1"u8.ToArray()) });
    }
    Assert.Throws<InvalidDataException>(() => Distributed.Join(other.Load(), hostile, NodeAddress, TextWriter.Null));
    Assert.False(Directory.Exists(other.Load().DistributedDir));
  }

  [Fact]
  public void A_package_from_a_linux_core_joins_a_windows_node()
  {
    // The layout distributed.sh's tar makes: "./" entries, a directory record, core.env lines
    // copied from .env with their quotes.
    using var other = new Installation();
    var shared = File.ReadAllText(Path.Combine(_core.Load().DistributedDir, "shared.env"));
    var linux = Path.Combine(other.Data, "node-yard.tar.gz");
    using (var stream = File.Create(linux))
    using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
    using (var tar = new TarWriter(gzip, TarEntryFormat.Gnu))
    {
      void Add(string name, string text) => tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, name)
        { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
      tar.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "./"));
      Add("./shared.env", shared);
      Add("./nodes", "vms-yard:vmsfs-yard:vmsonvif-yard:vmslogger-yard\n");
      Add("./core.env", "POSTGRES_PORT=5432\nPOSTGRES_USER=postgres\nPOSTGRES_PASSWORD=\"lin pass\"\nPOSTGRES_STATE_DB=ta_state\n" +
        "POSTGRES_FS_DB=ta_fs\nRABBITMQ_PORT=5674\nRABBITMQ_DEFAULT_USER=vms\nRABBITMQ_DEFAULT_PASS=rq\n");
      Add("./name", "yard\n");
      tar.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "./tokens/"));
      foreach (var app in new[] { "vms", "vmsfs", "vmsonvif", "vmsonvif-actors", "vmslogger" })
        Add($"./tokens/{app}-yard.token", "eyJ.eyJ.sig");
    }
    File.WriteAllLines(other.SiteEnv, ["# a node"]);
    Distributed.Join(other.Load(), linux, NodeAddress, TextWriter.Null);

    var config = other.Load();
    Assert.Equal("lin pass", config.Env["POSTGRES_PASSWORD"]);
    Assert.Equal("5674", config.Env["RABBITMQ_PORT"]);
    Assert.Equal("vms-yard", config.App(App(config, "vmsonvif-yard")).Env["OWN_SERVER_ID"]);
  }

  [Fact]
  public void Every_fixed_port_of_a_core_and_a_node_is_below_the_windows_dynamic_range_and_unique()
  {
    foreach (var config in new[] { _core.Load(), _node.Load() })
    {
      var ports = new List<(string, int)> { ("DAPR_SENTRY_PORT", config.RequirePort("DAPR_SENTRY_PORT")) };
      foreach (var infra in config.Manifest.Infrastructure)
      {
        if (config.ReadyPort(infra) is int ready) ports.Add(($"{infra.Name}.readyPort", ready));
        foreach (var arg in config.Infra(infra).Args.Where(a => int.TryParse(a, out _)))
          ports.Add(($"{infra.Name} {arg}", int.Parse(arg)));
      }
      Assert.DoesNotContain(ports, p => p.Item2 >= 49152);
      var sidecarPorts = config.Manifest.Apps
        .SelectMany(a => new[] { a.DaprHttpPort, a.DaprGrpcPort, a.DaprInternalGrpcPort, a.MetricsPort, config.AppPort(a) })
        .ToList();
      Assert.Equal(sidecarPorts.Count, sidecarPorts.Distinct().Count());
    }
  }

  // services.json is written for Windows; these tests also run on Linux.
  private static string Slashes(string path) => path.Replace('\\', Path.DirectorySeparatorChar);

  private static InfraProcess Infra(StackConfig config, string name) => config.Manifest.Infrastructure.Single(i => i.Name == name);
  private static DaprApp App(StackConfig config, string appId) => config.Manifest.Apps.Single(a => a.AppId == appId);

  private static byte[] FromBase64Url(string value)
  {
    var s = value.Replace('-', '+').Replace('_', '/');
    return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
  }
}

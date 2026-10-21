using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TaVmsHost;

// The stack, in the order compose's depends_on gives it: databases and the broker answering, then
// placement and MediaMTX, then every app behind its own sidecar. Stopped the other way round.
internal sealed class Supervisor(StackConfig config, ILogger<Supervisor> log) : BackgroundService
{
  private static readonly TimeSpan InfraReadyTimeout = TimeSpan.FromSeconds(30);
  private static readonly TimeSpan SidecarReadyTimeout = TimeSpan.FromSeconds(60);
  private static readonly TimeSpan AppStopTimeout = TimeSpan.FromSeconds(8);
  private static readonly TimeSpan SidecarStopTimeout = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan InfraStopTimeout = TimeSpan.FromSeconds(5);

  private readonly List<SupervisedProcess> _infra = [];
  private readonly List<(SupervisedProcess Sidecar, SupervisedProcess App)> _apps = [];
  private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };

  // A snapshot: the control pipe reads it while startup is still adding to the lists.
  public IReadOnlyList<SupervisedProcess> All
  {
    get
    {
      lock (_infra) lock (_apps)
        return _infra.Concat(_apps.SelectMany(a => new[] { a.Sidecar, a.App })).ToList();
    }
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    try
    {
      Directory.CreateDirectory(config.HostLogDir);
      DaprComponents.Write(config);

      await Infrastructure.WaitForPostgresAsync(config, log, stoppingToken);
      await Infrastructure.EnsureDatabasesAsync(config, checkOnly: false, log, stoppingToken);
      await Infrastructure.WaitForRabbitMqAsync(config, log, stoppingToken);

      foreach (var spec in config.Manifest.Infrastructure)
      {
        var process = Add(_infra, config.Infra(spec));
        process.Start();
        if (config.ReadyPort(spec) is int port)
          await WaitForPortAsync(spec.Name, port, InfraReadyTimeout, stoppingToken);
      }

      foreach (var app in config.Manifest.Apps)
      {
        var sidecar = new SupervisedProcess(config.Sidecar(app), config.HostLogDir, log);
        var process = new SupervisedProcess(config.App(app), config.HostLogDir, log);
        lock (_apps) _apps.Add((sidecar, process));

        sidecar.Start();
        await WaitForSidecarAsync(app, stoppingToken);
        process.Start();
      }
      log.LogInformation("All {Count} processes started", All.Count());
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
      // Stopped during startup; StopAsync takes down whatever did start.
    }
    catch (Exception ex)
    {
      // The host then stops (BackgroundServiceExceptionBehavior.StopHost). A non-zero exit code is
      // what makes the SCM count it as a failure and apply the restart the installer configured.
      log.LogCritical(ex, "Startup failed");
      Environment.ExitCode = 1;
      throw;
    }
  }

  public override async Task StopAsync(CancellationToken cancellationToken)
  {
    // Every process at once, before the tiers below: at system shutdown Windows has already told
    // each child to exit, and one that does so while an earlier tier is still stopping must not be
    // started again.
    foreach (var process in All) process.StopRestarting();
    await base.StopAsync(cancellationToken);

    // In parallel within each tier: the SCM allows a service about 20 seconds at system shutdown.
    await Task.WhenAll(_apps.Select(a => a.App.StopAsync(AppStopTimeout)));
    await Task.WhenAll(_apps.Select(a => a.Sidecar.StopAsync(SidecarStopTimeout)));
    foreach (var process in Enumerable.Reverse(_infra))
    {
      await process.StopAsync(InfraStopTimeout);
    }
    foreach (var process in All) await process.DisposeAsync();
    log.LogInformation("All processes stopped");
  }

  public SupervisedProcess? Find(string name) =>
    All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

  private SupervisedProcess Add(List<SupervisedProcess> list, ProcessSpec spec)
  {
    var process = new SupervisedProcess(spec, config.HostLogDir, log);
    lock (list) list.Add(process);
    return process;
  }

  // Not readiness of the app — nothing generic says that — only that the next one in line has
  // something to talk to. Past the timeout the next process is started anyway: the supervisor keeps
  // restarting whatever failed, and holding the rest of the stack back would not help it.
  private async Task WaitForPortAsync(string name, int port, TimeSpan timeout, CancellationToken ct)
  {
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
      try
      {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port, ct);
        return;
      }
      catch (SocketException)
      {
        await Task.Delay(500, ct);
      }
    }
    log.LogWarning("{Name} is not listening on {Port} after {Timeout:0}s; going on", name, port, timeout.TotalSeconds);
  }

  // /v1.0/healthz/outbound turns 204 once the sidecar has loaded its components and can serve the
  // app — unlike /v1.0/healthz, it does not wait for the app, which is only started after this.
  private async Task WaitForSidecarAsync(DaprApp app, CancellationToken ct)
  {
    var url = $"http://127.0.0.1:{app.DaprHttpPort}/v1.0/healthz/outbound";
    var deadline = DateTime.UtcNow + SidecarReadyTimeout;
    while (DateTime.UtcNow < deadline)
    {
      try
      {
        using var response = await _http.GetAsync(url, ct);
        if (response.IsSuccessStatusCode) return;
      }
      catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
      {
      }
      await Task.Delay(500, ct);
    }
    log.LogWarning("Sidecar of {AppId} not healthy after {Timeout:0}s; starting the app anyway",
      app.AppId, SidecarReadyTimeout.TotalSeconds);
  }

  public override void Dispose()
  {
    _http.Dispose();
    base.Dispose();
  }
}

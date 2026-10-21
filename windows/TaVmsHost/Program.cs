// TaVmsHost: runs the ta_vms stack on Windows without Docker. One Windows service ("TreeAlarm VMS")
// that starts placement, MediaMTX, and every service behind its own Dapr sidecar as child
// processes, restarts whatever exits, and stops them in reverse order. PostgreSQL and RabbitMQ are
// Windows services of their own, which this one depends on.
//
//   TaVmsHost [run] [--data <dir>]     the supervisor; what the service runs, also runs in a console
//   TaVmsHost status                   every process, its pid, uptime and restarts
//   TaVmsHost restart <name>           restart one: an app id, "<app-id>.dapr", placement, mediamtx
//   TaVmsHost check-db [--data <dir>]  report the databases POSTGRES_*_DB names; creates nothing
//   TaVmsHost env <KEY> [--data <dir>] print one value of the merged environment (for the
//                                      installer scripts, so that only one parser reads env files)
//
// The installation lives in the directory above this executable (<home>\host\TaVmsHost.exe); its
// data in --data, %ProgramData%\TreeAlarm\VMS unless told otherwise.

using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using TaVmsHost;

var home = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TreeAlarm", "VMS");
var positional = new List<string>();
for (var i = 0; i < args.Length; i++)
{
  if (args[i] == "--data" && i + 1 < args.Length) data = Path.GetFullPath(args[++i]);
  else positional.Add(args[i]);
}

switch (positional)
{
  case [] or ["run"]:
    return await RunAsync(home, data);
  case ["status"]:
    return ControlPipe.Send("status");
  case ["restart", var name]:
    return ControlPipe.Send($"restart {name}");
  case ["check-db"]:
    return await CheckDatabasesAsync(home, data);
  case ["env", var key]:
    return PrintEnv(home, data, key);
  case ["send-ctrl-c", var pid] when OperatingSystem.IsWindows():
    // Internal: see Native.SendCtrlC.
    Native.SendCtrlC(int.Parse(pid, System.Globalization.CultureInfo.InvariantCulture));
    return 0;
  default:
    Console.Error.WriteLine("usage: TaVmsHost [run] [--data <dir>] | status | restart <name> | check-db [--data <dir>] | env <KEY> [--data <dir>]");
    return 2;
}

static async Task<int> RunAsync(string home, string data)
{
  Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(Path.Combine(StackConfig.HostLogDirOf(data), "TaVmsHost.log"),
      outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
      rollOnFileSizeLimit: true,
      fileSizeLimitBytes: 10 * 1024 * 1024,
      retainedFileCountLimit: 5,
      shared: true)
    .CreateLogger();
  try
  {
    Log.Information("TaVmsHost starting: home {Home}, data {Data}", home, data);
    if (OperatingSystem.IsWindows())
    {
      try
      {
        Native.KillChildrenWithHost();
      }
      catch (Exception ex)
      {
        Log.Warning("Could not tie the children to this process ({Error}): if it is killed, they keep running", ex.Message);
      }
    }

    var config = StackConfig.Load(home, data);

    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
      Args = [],
      ContentRootPath = AppContext.BaseDirectory,
    });
    builder.Services.AddWindowsService(o => o.ServiceName = "TaVmsHost");
    builder.Services.AddSerilog();
    // Covers Supervisor.StopAsync's tiers (apps, sidecars, infrastructure) with room to spare.
    builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(60));
    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton<Supervisor>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<Supervisor>());
    builder.Services.AddHostedService<ControlPipe>();

    var host = builder.Build();
    // Windows tells every console process that it is shutting down (CTRL_SHUTDOWN_EVENT, SIGTERM to
    // .NET) about as soon as the shutdown starts; the children, each on a hidden console of its own,
    // exit on it. The SCM's own stop reaches this service only after it has notified the services
    // before it -- 20 s later on the test machine, all of which the supervisor spent restarting
    // children. The same event reaches this process, so the stop starts from it.
    using var shutdown = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
    {
      context.Cancel = true;
      Log.Information("Windows is shutting down: stopping the stack");
      host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
    });
    await host.RunAsync();
    return Environment.ExitCode;
  }
  catch (Exception ex)
  {
    Log.Fatal(ex, "TaVmsHost failed");
    return 1;
  }
  finally
  {
    await Log.CloseAndFlushAsync();
  }
}

static async Task<int> CheckDatabasesAsync(string home, string data)
{
  Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: "{Message:lj}{NewLine}{Exception}")
    .CreateLogger();
  try
  {
    var config = StackConfig.Load(home, data);
    var log = new SerilogLoggerFactory(Log.Logger).CreateLogger("check-db");
    var missing = await Infrastructure.EnsureDatabasesAsync(config, checkOnly: true, log, CancellationToken.None);
    return missing == 0 ? 0 : 1;
  }
  catch (Exception ex)
  {
    Log.Error(ex.Message);
    return 1;
  }
  finally
  {
    await Log.CloseAndFlushAsync();
  }
}

static int PrintEnv(string home, string data, string key)
{
  try
  {
    Console.WriteLine(StackConfig.Load(home, data).Require(key));
    return 0;
  }
  catch (Exception ex)
  {
    Console.Error.WriteLine(ex.Message);
    return 1;
  }
}

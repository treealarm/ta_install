using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace TaVmsHost;

// One child process, kept running: started, its output written to a rotating file, and started
// again whenever it exits on its own — after 1s, 2s, 4s … up to 30s, the delay resetting once a run
// has lasted a minute. Stopping asks it to finish first (Ctrl+C on its console, which every process
// here treats as a shutdown request) and kills it only if it does not.
internal sealed class SupervisedProcess : IAsyncDisposable
{
  private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
  private static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(1);

  private readonly ProcessSpec _spec;
  private readonly ILogger _log;
  private readonly Serilog.Core.Logger _output;
  private readonly object _gate = new();
  private Process? _process;
  // The current process's own job (Native.ContainProcessTree); IntPtr.Zero when there is none.
  private IntPtr _job;
  private DateTime _startedAt;
  private Task? _loop;
  private CancellationTokenSource? _stopping;
  private bool _restartRequested;

  public SupervisedProcess(ProcessSpec spec, string logDir, ILogger log)
  {
    _spec = spec;
    _log = log;
    _output = new LoggerConfiguration()
      .WriteTo.File(Path.Combine(logDir, $"{spec.Name}.log"),
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Message:l}{NewLine}",
        rollOnFileSizeLimit: true,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        retainedFileCountLimit: 5,
        shared: true)
      .CreateLogger();
  }

  public string Name => _spec.Name;
  public int Restarts { get; private set; }
  public int? LastExitCode { get; private set; }

  public string Describe()
  {
    lock (_gate)
    {
      var running = _process is { HasExited: false };
      var state = running
        ? $"running  pid {_process!.Id,-6} up {Format(DateTime.UtcNow - _startedAt)}"
        : _loop is null ? "not started" : "restarting";
      var exit = LastExitCode is { } code ? $"  last exit 0x{code:X8}" : "";
      return $"{Name,-30} {state}  restarts {Restarts}{exit}";
    }
  }

  public void Start()
  {
    if (_loop is not null) throw new InvalidOperationException($"{Name} already started");
    _stopping = new CancellationTokenSource();
    StartOnce();
    _loop = Task.Run(() => SuperviseAsync(_stopping.Token));
  }

  // Restarts it now, with no backoff — `TaVmsHost restart <name>`.
  public async Task RestartAsync(TimeSpan timeout)
  {
    lock (_gate) _restartRequested = true;
    await StopProcessAsync(timeout);
  }

  // From here on an exit is final. The process itself is stopped later, by StopAsync.
  public void StopRestarting() => _stopping?.Cancel();

  public async Task StopAsync(TimeSpan timeout)
  {
    if (_stopping is null) return;
    await _stopping.CancelAsync();
    await StopProcessAsync(timeout);
    if (_loop is not null) await _loop;
  }

  private async Task SuperviseAsync(CancellationToken stopping)
  {
    var backoff = TimeSpan.FromSeconds(1);
    while (!stopping.IsCancellationRequested)
    {
      Process? process;
      lock (_gate) process = _process;

      TimeSpan delay;
      if (process is null)
      {
        // The last start failed outright (see below).
        delay = MaxBackoff;
      }
      else
      {
        try
        {
          await process.WaitForExitAsync(stopping);
        }
        catch (OperationCanceledException)
        {
          return;
        }
        ReleaseLeftovers();

        bool requested;
        lock (_gate)
        {
          LastExitCode = process.ExitCode;
          requested = _restartRequested;
          _restartRequested = false;
          _process = null;
        }
        // Not disposed here: StopProcessAsync may still be waiting on the same object.
        if (stopping.IsCancellationRequested) return;

        if (requested)
        {
          _log.LogInformation("{Name} stopped for a restart", Name);
          backoff = TimeSpan.FromSeconds(1);
          delay = TimeSpan.Zero;
        }
        else
        {
          if (DateTime.UtcNow - _startedAt >= StableAfter) backoff = TimeSpan.FromSeconds(1);
          delay = backoff;
          backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
          _log.LogError("{Name} exited with 0x{Code:X8}, restarting in {Delay:0}s (its output: {Log})",
            Name, LastExitCode, delay.TotalSeconds, $"host-logs\\{Name}.log");
          ReportPortHolders();
        }
      }

      try
      {
        await Task.Delay(delay, stopping);
      }
      catch (OperationCanceledException)
      {
        return;
      }

      try
      {
        StartOnce();
        Restarts++;
      }
      catch (Exception ex)
      {
        // The executable is missing or cannot be started. Retrying is still right -- an upgrade
        // may be putting it back -- but slowly, and saying so every time.
        _log.LogError(ex, "{Name} could not be started, retrying in {Delay:0}s", Name, MaxBackoff.TotalSeconds);
        lock (_gate) _process = null;
      }
    }
  }

  private void StartOnce()
  {
    var psi = new ProcessStartInfo(_spec.FileName)
    {
      WorkingDirectory = _spec.WorkingDirectory,
      UseShellExecute = false,
      // A hidden console of its own: what lets Stop reach exactly this process with Ctrl+C, and
      // keeps a Ctrl+C on the host's console (when it is run by hand) from reaching the children
      // before the host has decided the order they stop in.
      CreateNoWindow = true,
      // An empty stdin. A batch file (Keycloak's kc.bat) asks "Terminate batch job (Y/N)?" on its
      // console after Ctrl+C has already stopped its java, and waits there until the stop times out;
      // reading the end of a closed pipe instead, it finishes at once. Nothing here reads stdin.
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      StandardOutputEncoding = Encoding.UTF8,
      StandardErrorEncoding = Encoding.UTF8,
    };
    foreach (var arg in _spec.Args) psi.ArgumentList.Add(arg);
    foreach (var (key, value) in _spec.Env) psi.Environment[key] = value;

    var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
    if (!process.Start()) throw new InvalidOperationException($"{Name}: Process.Start returned false");
    var job = IntPtr.Zero;
    if (OperatingSystem.IsWindows())
    {
      try
      {
        job = Native.ContainProcessTree(process.Handle);
      }
      catch (System.ComponentModel.Win32Exception ex)
      {
        // It runs all the same; only what it leaves behind on an exit is not cleared up.
        _log.LogWarning("{Name}: no job of its own ({Error})", Name, ex.Message);
      }
    }
    process.StandardInput.Close();
    PumpLines(process.StandardOutput, "stdout");
    PumpLines(process.StandardError, "stderr");

    lock (_gate)
    {
      _process = process;
      _job = job;
      _startedAt = DateTime.UtcNow;
    }
    _output.Write(LogEventLevel.Information, "{Line:l}", $"=== TaVmsHost started {_spec.FileName} (pid {process.Id})");
    _log.LogInformation("{Name} started, pid {Pid}", Name, process.Id);
  }

  // A thread of its own per stream rather than BeginOutputReadLine. The pipes Process creates on
  // Windows are synchronous, so a pending read holds a thread-pool thread until data arrives: two
  // per child, 48 for the whole stack, all parked. Every burst of output then waited for the pool to
  // grow a thread at a time, and a child whose pipe filled meanwhile stopped dead in its next write
  // to the console -- for up to 25 s on a 4-core machine, long enough for calls to it to time out.
  private void PumpLines(StreamReader reader, string stream)
  {
    var thread = new Thread(() =>
    {
      try
      {
        while (reader.ReadLine() is { } line)
          _output.Write(LogEventLevel.Information, "{Line:l}", line);
      }
      catch (IOException)
      {
        // The pipe broke with the process; its exit is handled by SuperviseAsync.
      }
    })
    {
      IsBackground = true,
      Name = $"{Name} {stream}",
    };
    thread.Start();
  }

  private async Task StopProcessAsync(TimeSpan timeout)
  {
    Process? process;
    int pid;
    lock (_gate)
    {
      process = _process;
      if (process is null || process.HasExited) return;
      pid = process.Id;
    }

    try
    {
      await SendCtrlCAsync(pid);
    }
    catch (Exception ex)
    {
      _log.LogWarning("{Name}: could not send Ctrl+C ({Error}), killing it", Name, ex.Message);
    }

    using var wait = new CancellationTokenSource(timeout);
    try
    {
      await process.WaitForExitAsync(wait.Token);
      _log.LogInformation("{Name} stopped", Name);
    }
    catch (OperationCanceledException)
    {
      _log.LogWarning("{Name} did not stop within {Timeout:0}s, killing it", Name, timeout.TotalSeconds);
      try
      {
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
      }
      catch (InvalidOperationException)
      {
        // Exited between the timeout and the kill.
      }
    }
  }

  // The process has exited; anything it started that is still running goes too, before the next
  // start can find it holding a port or a file.
  private void ReleaseLeftovers()
  {
    IntPtr job;
    lock (_gate)
    {
      job = _job;
      _job = IntPtr.Zero;
    }
    if (job == IntPtr.Zero || !OperatingSystem.IsWindows()) return;
    // Named before the job is closed, while they still exist. The console host every hidden console
    // comes with is not counted: it outlives its process by a moment, and goes anyway.
    var left = Native.ProcessesIn(job).Select(Describe).Where(d => d is not null && !d.StartsWith("conhost ")).ToList();
    Native.CloseProcessTree(job);
    if (left.Count > 0)
      _log.LogWarning("{Name} exited leaving {Left} running; killed", Name, string.Join(", ", left));

    static string? Describe(int pid)
    {
      try
      {
        using var process = Process.GetProcessById(pid);
        return $"{process.ProcessName} (pid {pid})";
      }
      catch (ArgumentException)
      {
        return null; // exited meanwhile
      }
    }
  }

  // A process that exits as soon as it starts has most often found its port taken. Its own log says
  // "bind" and no more; this says by whom — Orwell's onvifserver, an automatic service, took 8554
  // from MediaMTX on a developer's desktop. Anything still listening there now is not this process.
  private void ReportPortHolders()
  {
    if (!OperatingSystem.IsWindows() || _spec.Ports is not { Count: > 0 } ports) return;
    try
    {
      foreach (var port in ports)
      {
        foreach (var pid in Native.TcpListenerPids(port))
        {
          string owner;
          try
          {
            using var holder = Process.GetProcessById(pid);
            owner = holder.ProcessName;
          }
          catch (ArgumentException)
          {
            owner = "a process that has since exited";
          }
          _log.LogError("{Name}: port {Port} is held by {Owner} (pid {Pid})", Name, port, owner, pid);
        }
      }
    }
    catch (System.ComponentModel.Win32Exception ex)
    {
      _log.LogWarning("{Name}: could not list the listeners on its ports ({Error})", Name, ex.Message);
    }
  }

  // The host cannot do this itself without leaving its own console (see Native.SendCtrlC), so a
  // second copy of it does.
  private static async Task SendCtrlCAsync(int pid)
  {
    if (!OperatingSystem.IsWindows())
    {
      Process.GetProcessById(pid).Kill();
      return;
    }
    var self = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
    var psi = new ProcessStartInfo(self) { UseShellExecute = false, CreateNoWindow = true };
    psi.ArgumentList.Add("send-ctrl-c");
    psi.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
    using var helper = Process.Start(psi) ?? throw new InvalidOperationException("helper did not start");
    using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await helper.WaitForExitAsync(wait.Token);
    if (helper.ExitCode != 0) throw new InvalidOperationException($"send-ctrl-c exited with {helper.ExitCode}");
  }

  private static string Format(TimeSpan t) =>
    t.TotalDays >= 1 ? $"{(int)t.TotalDays}d{t.Hours}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes}m" : $"{(int)t.TotalMinutes}m{t.Seconds}s";

  public ValueTask DisposeAsync()
  {
    ReleaseLeftovers();
    _output.Dispose();
    _stopping?.Dispose();
    return ValueTask.CompletedTask;
  }
}

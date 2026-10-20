using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TaVmsHost;

// `TaVmsHost status` and `TaVmsHost restart <name>` talk to the running host through this pipe:
// with the whole stack under one Windows service, this is how a single process is looked at or
// restarted. One line in, the answer back, then the connection closes. The pipe's default ACL lets
// only administrators and the service's own account open it for writing.
internal sealed class ControlPipe(Supervisor supervisor, ILogger<ControlPipe> log) : BackgroundService
{
  public const string PipeName = "TaVmsHost.control";
  private static readonly TimeSpan RestartStopTimeout = TimeSpan.FromSeconds(10);

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await using var pipe = new NamedPipeServerStream(
          PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync(stoppingToken);

        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var command = (await reader.ReadLineAsync(stoppingToken))?.Trim() ?? "";
        await writer.WriteAsync(await HandleAsync(command));
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        return;
      }
      catch (Exception ex)
      {
        log.LogWarning("Control pipe: {Error}", ex.Message);
      }
    }
  }

  private async Task<string> HandleAsync(string command)
  {
    var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    switch (parts)
    {
      case ["status"]:
        return string.Join(Environment.NewLine, supervisor.All.Select(p => p.Describe())) + Environment.NewLine;

      case ["restart", var name]:
        var process = supervisor.Find(name);
        if (process is null) return $"error: no process named '{name}'{Environment.NewLine}";
        log.LogInformation("Restart of {Name} requested over the control pipe", process.Name);
        await process.RestartAsync(RestartStopTimeout);
        return $"restarting {process.Name}{Environment.NewLine}";

      default:
        return $"error: unknown command '{command}'{Environment.NewLine}";
    }
  }

  // The client side, run by `TaVmsHost status|restart`.
  public static int Send(string command)
  {
    using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
    try
    {
      pipe.Connect(3000);
    }
    catch (TimeoutException)
    {
      Console.Error.WriteLine("TaVmsHost is not running (or this console is not elevated).");
      return 1;
    }
    catch (UnauthorizedAccessException)
    {
      Console.Error.WriteLine("Access denied: run this from an elevated console.");
      return 1;
    }

    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    writer.WriteLine(command);
    using var reader = new StreamReader(pipe, Encoding.UTF8);
    var answer = reader.ReadToEnd();
    Console.Write(answer);
    return answer.StartsWith("error:", StringComparison.Ordinal) ? 1 : 0;
  }
}

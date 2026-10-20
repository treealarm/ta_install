using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;
using RabbitMQ.Client;

namespace TaVmsHost;

// PostgreSQL and RabbitMQ run as Windows services of their own. The SCM dependency on them only
// waits for "running", which both report well before they accept a client — and daprd exits for
// good when a component cannot initialise. So nothing is started until each answers a real request.
internal static partial class Infrastructure
{
  private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
  private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(30);

  public static async Task WaitForPostgresAsync(StackConfig c, ILogger log, CancellationToken ct)
  {
    await RetryAsync("PostgreSQL", log, ct, async () =>
    {
      await using var conn = new NpgsqlConnection(MaintenanceConnectionString(c));
      await conn.OpenAsync(ct);
      await using var cmd = new NpgsqlCommand("SELECT 1", conn);
      await cmd.ExecuteScalarAsync(ct);
    });
  }

  public static async Task WaitForRabbitMqAsync(StackConfig c, ILogger log, CancellationToken ct)
  {
    var factory = new ConnectionFactory
    {
      HostName = c.Require("RABBITMQ_HOST"),
      Port = c.RequirePort("RABBITMQ_PORT"),
      UserName = c.Require("RABBITMQ_DEFAULT_USER"),
      Password = c.Require("RABBITMQ_DEFAULT_PASS"),
    };
    // A login, not a port probe: a wrong password is the failure daprd would otherwise only log
    // once and then carry on with a bus that never delivers.
    await RetryAsync("RabbitMQ", log, ct, async () =>
    {
      await using var conn = await factory.CreateConnectionAsync(ct);
    });
  }

  // The same rule scripts/prepare-databases.sh applies in Docker: every POSTGRES_<NAME>_DB in the
  // environment names a database this stack owns, and a missing one is created. POSTGRES_DB itself
  // does not match — it needs at least one character between the prefix and the suffix.
  [GeneratedRegex(@"^POSTGRES_[A-Z0-9_]+_DB$")]
  private static partial Regex DatabaseVariable();

  internal static IReadOnlyList<(string Variable, string Name)> DatabaseNames(
    IReadOnlyDictionary<string, string> env, ILogger log)
  {
    var names = new List<(string, string)>();
    foreach (var (key, value) in env.Where(kv => DatabaseVariable().IsMatch(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
    {
      if (string.IsNullOrWhiteSpace(value))
      {
        log.LogWarning("{Variable} is set but empty, skipped", key);
        continue;
      }
      names.Add((key, value));
    }
    if (names.Count == 0)
      throw new InvalidOperationException("No POSTGRES_*_DB variables in the environment: nothing names the databases");
    return names;
  }

  // Returns how many are still missing (always 0 unless checkOnly).
  public static async Task<int> EnsureDatabasesAsync(StackConfig c, bool checkOnly, ILogger log, CancellationToken ct)
  {
    var wanted = DatabaseNames(c.Env, log);
    await using var conn = new NpgsqlConnection(MaintenanceConnectionString(c));
    await conn.OpenAsync(ct);

    var existing = new HashSet<string>(StringComparer.Ordinal);
    await using (var cmd = new NpgsqlCommand("SELECT datname FROM pg_database", conn))
    await using (var reader = await cmd.ExecuteReaderAsync(ct))
    {
      while (await reader.ReadAsync(ct)) existing.Add(reader.GetString(0));
    }

    var missing = 0;
    foreach (var (variable, name) in wanted)
    {
      if (existing.Contains(name))
      {
        log.LogInformation("[ ok ] database {Name} ({Variable})", name, variable);
        continue;
      }
      if (checkOnly)
      {
        log.LogWarning("[MISSING] database {Name} ({Variable})", name, variable);
        missing++;
        continue;
      }
      // Quoted, so that a tenant-stamped name with mixed case or dashes still works.
      await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name.Replace("\"", "\"\"")}\"", conn);
      await create.ExecuteNonQueryAsync(ct);
      log.LogInformation("[ new ] database {Name} created ({Variable})", name, variable);
    }
    return missing;
  }

  private static string MaintenanceConnectionString(StackConfig c) => new NpgsqlConnectionStringBuilder
  {
    Host = c.Require("POSTGRES_HOST"),
    Port = c.RequirePort("POSTGRES_PORT"),
    Username = c.Require("POSTGRES_USER"),
    Password = c.Require("POSTGRES_PASSWORD"),
    Database = "postgres",
    Timeout = 5,
    Pooling = false,
  }.ConnectionString;

  private static async Task RetryAsync(string what, ILogger log, CancellationToken ct, Func<Task> attempt)
  {
    var started = DateTime.UtcNow;
    var lastReport = started;
    while (true)
    {
      try
      {
        await attempt();
        log.LogInformation("{What} is ready", what);
        return;
      }
      catch (Exception ex) when (!ct.IsCancellationRequested)
      {
        if (DateTime.UtcNow - lastReport >= ReportEvery || lastReport == started)
        {
          log.LogWarning("Waiting for {What} ({Elapsed:0}s so far): {Error}",
            what, (DateTime.UtcNow - started).TotalSeconds, ex.Message);
          lastReport = DateTime.UtcNow;
        }
      }
      await Task.Delay(RetryDelay, ct);
    }
  }
}

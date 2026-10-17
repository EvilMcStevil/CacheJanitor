using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CacheJanitor;

/// <summary>An evicted entry that came back, with the cache size that would have kept it.</summary>
public sealed record ChurnReturn(
    string Entry,
    DateTime EvictedUtc,
    DateTime LastUsedUtc,
    long MaxBytesAtEviction,
    DateTime ReturnedUtc,
    long ReturnedBytes,
    long ReuseDistance)
{
    /// <summary>The limit at which the entry would have stayed cached until it was needed again.</summary>
    public long NeededMaxBytes => MaxBytesAtEviction + ReuseDistance;

    /// <summary>How long the entry was out of the cache, from eviction to return.</summary>
    public TimeSpan GoneFor => ReturnedUtc - EvictedUtc;
}

/// <summary>
/// SQLite log of every eviction and of the entries that were later recreated, kept in
/// <see cref="AppPaths.ChurnDbPath"/>. A database that cannot be opened or used, or a native SQLite that
/// cannot be loaded, leaves the store inert: every method returns an empty result.
/// </summary>
public sealed class ChurnStore : IDisposable
{
    private const int RetentionDays = 180;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS caches(id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE COLLATE NOCASE,
          first_seen_utc INTEGER NOT NULL, last_pass_utc INTEGER, evicted_total INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS evictions(id INTEGER PRIMARY KEY, cache_id INTEGER NOT NULL,
          entry TEXT NOT NULL COLLATE NOCASE, bytes INTEGER NOT NULL, last_used_utc INTEGER NOT NULL,
          evicted_utc INTEGER NOT NULL, max_bytes INTEGER NOT NULL, total_after INTEGER NOT NULL,
          returned_utc INTEGER, created_utc INTEGER, returned_bytes INTEGER, total_at_return INTEGER);
        CREATE INDEX IF NOT EXISTS ix_open ON evictions(cache_id, entry) WHERE returned_utc IS NULL;
        CREATE INDEX IF NOT EXISTS ix_evicted ON evictions(cache_id, evicted_utc);
        CREATE INDEX IF NOT EXISTS ix_returned ON evictions(cache_id, returned_utc);
        CREATE INDEX IF NOT EXISTS ix_created ON evictions(cache_id, entry, created_utc) WHERE created_utc IS NOT NULL;
        """;

    private readonly ILogger _log;
    private readonly string _path;
    private readonly Func<string, SqliteConnection> _connect;
    private readonly Lock _lock = new();
    private readonly HashSet<string> _warned = new();
    private SqliteConnection? _db;

    public ChurnStore(ILogger<ChurnStore> log) : this(log, AppPaths.ChurnDbPath) { }

    public ChurnStore(ILogger log, string path) : this(log, path, cs => new SqliteConnection(cs)) { }

    /// <summary>Opens the store with <paramref name="connect"/> creating the connection from a connection string.</summary>
    internal ChurnStore(ILogger log, string path, Func<string, SqliteConnection> connect)
    {
        _log = log;
        _path = path;
        _connect = connect;
        Open();
    }

    public DateTime? LastPassUtc(string cachePath) => Guarded<DateTime?>(db =>
    {
        using var cmd = Command(db, "SELECT last_pass_utc FROM caches WHERE path = $p");
        cmd.Parameters.AddWithValue("$p", PathGuard.Normalize(cachePath));
        return cmd.ExecuteScalar() is long ticks ? FromTicks(ticks) : null;
    }, null);

    /// <summary>When the cache was first recorded. Registers the cache if it is not yet known.</summary>
    public DateTime RecordingSinceUtc(string cachePath) => Guarded(db =>
    {
        using var tx = db.BeginTransaction();
        long id = EnsureCache(db, tx, PathGuard.Normalize(cachePath));
        using var cmd = Command(db, "SELECT first_seen_utc FROM caches WHERE id = $id", tx);
        cmd.Parameters.AddWithValue("$id", id);
        var since = FromTicks((long)cmd.ExecuteScalar()!);
        tx.Commit();
        return since;
    }, DateTime.UtcNow);

    /// <summary>
    /// Matches <paramref name="created"/> against earlier evictions, logs <paramref name="evictions"/> and
    /// stamps the pass. Returns how many entries were matched as returns.
    /// </summary>
    public int RecordPass(string cachePath, long maxBytes, DateTime passStartedUtc,
        IReadOnlyList<EvictionRecord> evictions, IReadOnlyList<CacheTrimmer.CreatedEntry> created) => Guarded(db =>
    {
        using var tx = db.BeginTransaction();
        long id = EnsureCache(db, tx, PathGuard.Normalize(cachePath));

        using var totalCmd = Command(db, "SELECT evicted_total FROM caches WHERE id = $id", tx);
        totalCmd.Parameters.AddWithValue("$id", id);
        long total = (long)totalCmd.ExecuteScalar()!;

        int matched = 0;
        if (created.Count > 0)
        {
            using var find = Command(db, """
                SELECT id FROM evictions WHERE cache_id = $c AND entry = $e AND returned_utc IS NULL
                  AND NOT EXISTS (SELECT 1 FROM evictions WHERE cache_id = $c AND entry = $e AND created_utc = $t)
                ORDER BY evicted_utc DESC, id DESC LIMIT 1
                """, tx);
            find.Parameters.AddWithValue("$c", id);
            var findEntry = find.Parameters.Add("$e", SqliteType.Text);
            var findTime = find.Parameters.Add("$t", SqliteType.Integer);
            using var mark = Command(db, """
                UPDATE evictions SET returned_utc = MAX($t, evicted_utc), created_utc = $t, returned_bytes = $b,
                  total_at_return = $total WHERE id = $id
                """, tx);
            var markTime = mark.Parameters.Add("$t", SqliteType.Integer);
            var markBytes = mark.Parameters.Add("$b", SqliteType.Integer);
            mark.Parameters.AddWithValue("$total", total);
            var markId = mark.Parameters.Add("$id", SqliteType.Integer);
            foreach (var c in created)
            {
                findEntry.Value = c.RelativePath;
                findTime.Value = c.CreatedUtc.Ticks;
                if (find.ExecuteScalar() is not long rowId) continue;
                markTime.Value = c.CreatedUtc.Ticks;
                markBytes.Value = c.Bytes;
                markId.Value = rowId;
                mark.ExecuteNonQuery();
                matched++;
            }
        }

        if (evictions.Count > 0)
        {
            using var insert = Command(db, """
                INSERT INTO evictions(cache_id, entry, bytes, last_used_utc, evicted_utc, max_bytes, total_after)
                VALUES($c, $e, $b, $lu, $eu, $max, $after)
                """, tx);
            insert.Parameters.AddWithValue("$c", id);
            var entry = insert.Parameters.Add("$e", SqliteType.Text);
            var bytes = insert.Parameters.Add("$b", SqliteType.Integer);
            var lastUsed = insert.Parameters.Add("$lu", SqliteType.Integer);
            var evicted = insert.Parameters.Add("$eu", SqliteType.Integer);
            insert.Parameters.AddWithValue("$max", maxBytes);
            var after = insert.Parameters.Add("$after", SqliteType.Integer);
            foreach (var e in evictions)
            {
                total += e.Bytes;
                entry.Value = e.Entry;
                bytes.Value = e.Bytes;
                lastUsed.Value = e.LastUsedUtc.Ticks;
                evicted.Value = e.Utc.Ticks;
                after.Value = total;
                insert.ExecuteNonQuery();
            }
        }

        using var stamp = Command(db, "UPDATE caches SET last_pass_utc = $t, evicted_total = $total WHERE id = $id", tx);
        stamp.Parameters.AddWithValue("$t", passStartedUtc.Ticks);
        stamp.Parameters.AddWithValue("$total", total);
        stamp.Parameters.AddWithValue("$id", id);
        stamp.ExecuteNonQuery();

        tx.Commit();
        return matched;
    }, 0);

    /// <summary>Entries that returned at or after <paramref name="sinceUtc"/>, oldest return first.</summary>
    public IReadOnlyList<ChurnReturn> ReturnsSince(string cachePath, DateTime sinceUtc) => Guarded<IReadOnlyList<ChurnReturn>>(db =>
    {
        using var cmd = Command(db, """
            SELECT e.entry, e.evicted_utc, e.last_used_utc, e.max_bytes, e.returned_utc, e.returned_bytes,
                   e.total_at_return - (e.total_after - e.bytes)
            FROM evictions e JOIN caches c ON c.id = e.cache_id
            WHERE c.path = $p AND e.returned_utc >= $since
            ORDER BY e.returned_utc, e.id
            """);
        cmd.Parameters.AddWithValue("$p", PathGuard.Normalize(cachePath));
        cmd.Parameters.AddWithValue("$since", sinceUtc.Ticks);
        var list = new List<ChurnReturn>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ChurnReturn(reader.GetString(0), FromTicks(reader.GetInt64(1)), FromTicks(reader.GetInt64(2)),
                reader.GetInt64(3), FromTicks(reader.GetInt64(4)), reader.GetInt64(5), reader.GetInt64(6)));
        }
        return list;
    }, []);

    /// <summary>Drops rows older than the retention period, then trims each cache to its newest rows.</summary>
    public void Prune(DateTime nowUtc, int maxRowsPerCache = 500_000) => Guarded(db =>
    {
        using var tx = db.BeginTransaction();
        using (var old = Command(db, "DELETE FROM evictions WHERE COALESCE(returned_utc, evicted_utc) < $cutoff", tx))
        {
            old.Parameters.AddWithValue("$cutoff", nowUtc.AddDays(-RetentionDays).Ticks);
            old.ExecuteNonQuery();
        }

        var cacheIds = new List<long>();
        using (var list = Command(db, "SELECT id FROM caches", tx))
        using (var reader = list.ExecuteReader())
            while (reader.Read()) cacheIds.Add(reader.GetInt64(0));

        using var cap = Command(db, """
            DELETE FROM evictions WHERE id IN (
                SELECT id FROM evictions WHERE cache_id = $c ORDER BY evicted_utc DESC, id DESC LIMIT -1 OFFSET $keep)
            """, tx);
        var capCache = cap.Parameters.Add("$c", SqliteType.Integer);
        cap.Parameters.AddWithValue("$keep", maxRowsPerCache);
        foreach (var cacheId in cacheIds)
        {
            capCache.Value = cacheId;
            cap.ExecuteNonQuery();
        }

        tx.Commit();
        return 0;
    }, 0);

    public void Dispose()
    {
        lock (_lock)
        {
            _db?.Dispose();
            _db = null;
        }
    }

    private void Open()
    {
        lock (_lock)
        {
            try
            {
                var db = _connect(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
                try
                {
                    db.Open();
                    Execute(db, "PRAGMA journal_mode=WAL");
                    Execute(db, Schema);
                }
                catch
                {
                    db.Dispose();
                    throw;
                }
                _db = db;
            }
            catch (Exception ex) when (!IsFatal(ex))
            {
                Warn("Could not open the churn history at {Path}, sizing advice is unavailable: {Message}", ex);
            }
        }
    }

    private T Guarded<T>(Func<SqliteConnection, T> action, T fallback)
    {
        lock (_lock)
        {
            if (_db is null) return fallback;
            try
            {
                return action(_db);
            }
            catch (Exception ex) when (!IsFatal(ex))
            {
                Warn("Could not use the churn history at {Path}: {Message}", ex);
                return fallback;
            }
        }
    }

    private void Warn(string template, Exception ex)
    {
        if (_warned.Add(ex.Message))
            _log.LogWarning(template, _path, ex.Message);
    }

    private static bool IsFatal(Exception ex) => ex is OutOfMemoryException;

    private static long EnsureCache(SqliteConnection db, SqliteTransaction tx, string key)
    {
        using var insert = Command(db, "INSERT INTO caches(path, first_seen_utc) VALUES($p, $t) ON CONFLICT(path) DO NOTHING", tx);
        insert.Parameters.AddWithValue("$p", key);
        insert.Parameters.AddWithValue("$t", DateTime.UtcNow.Ticks);
        insert.ExecuteNonQuery();
        using var select = Command(db, "SELECT id FROM caches WHERE path = $p", tx);
        select.Parameters.AddWithValue("$p", key);
        return (long)select.ExecuteScalar()!;
    }

    private static SqliteCommand Command(SqliteConnection db, string sql, SqliteTransaction? tx = null)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var cmd = Command(db, sql);
        cmd.ExecuteNonQuery();
    }

    private static DateTime FromTicks(long ticks) => new(ticks, DateTimeKind.Utc);
}

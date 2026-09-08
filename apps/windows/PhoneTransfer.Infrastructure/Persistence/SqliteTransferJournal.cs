using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using PhoneTransfer.Application.Files;

namespace PhoneTransfer.Infrastructure.Persistence;

// An exclusive process lease prevents two runtimes from reconciling/mutating the same journal.
// It is not a SQLite transaction and never blocks devices.db authentication reads.
public sealed class SqliteTransferJournal : ITransferJournal
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };
    private readonly string connectionString;
    private readonly FileStream lease;

    public SqliteTransferJournal(string databasePath)
    {
        var path = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        lease = new FileStream(path + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (version is not (0 or SchemaVersion)) throw new TransferJournalException();
            command.CommandText = "PRAGMA quick_check";
            if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal)) throw new TransferJournalException();
            if (version == 0)
            {
                // Version zero is initialization only, not permission to repair an unknown schema.
                command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";
                if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) throw new TransferJournalException();
            }
            command.CommandText = "PRAGMA journal_mode=WAL";
            if (!string.Equals(command.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase)) throw new TransferJournalException();
            if (version == 0)
            {
                using var transaction = connection.BeginTransaction();
                command.Transaction = transaction;
                command.CommandText = """
                    CREATE TABLE transfers (
                        transfer_id TEXT PRIMARY KEY NOT NULL,
                        owner_device_id TEXT NOT NULL,
                        idempotency_key TEXT NOT NULL,
                        revision INTEGER NOT NULL CHECK(revision >= 0),
                        record TEXT NOT NULL CHECK(length(record) <= 16384),
                        UNIQUE(owner_device_id, idempotency_key)
                    );
                    PRAGMA user_version=1;
                    """;
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            _ = Load(); // Missing columns/metadata and oversized registries fail before filesystem recovery.
        }
        catch (Exception exception)
        {
            lease.Dispose();
            throw new TransferJournalException(exception);
        }
    }

    public IReadOnlyList<DurableTransfer> Load()
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT transfer_id, owner_device_id, idempotency_key, revision, record FROM transfers LIMIT $limit";
            command.Parameters.AddWithValue("$limit", DurableTransferMachine.MaximumRecords + 1);
            using var reader = command.ExecuteReader();
            var records = new List<DurableTransfer>();
            while (reader.Read())
            {
                var json = reader.GetString(4);
                if (json.Length > 16384 || records.Count == DurableTransferMachine.MaximumRecords) throw new TransferJournalException();
                var record = JsonSerializer.Deserialize<DurableTransfer>(json, JsonOptions) ?? throw new TransferJournalException();
                DurableTransferMachine.Validate(record);
                if (record.TransferId.ToString("D") != reader.GetString(0) || record.OwnerDeviceId.ToString("D") != reader.GetString(1) ||
                    record.IdempotencyKey.ToString("D") != reader.GetString(2) || record.Revision != reader.GetInt64(3))
                    throw new TransferJournalException();
                records.Add(record);
            }
            return records;
        }
        catch (Exception exception) when (exception is SqliteException or JsonException or ArgumentException or InvalidOperationException)
        {
            throw new TransferJournalException(exception);
        }
    }

    public void Insert(DurableTransfer record)
    {
        DurableTransferMachine.Validate(record);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM transfers";
            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) >= DurableTransferMachine.MaximumRecords)
                throw new TransferJournalException();
            command.CommandText = "INSERT INTO transfers VALUES ($id, $owner, $key, $revision, $record)";
            command.Parameters.AddWithValue("$id", record.TransferId.ToString("D"));
            command.Parameters.AddWithValue("$owner", record.OwnerDeviceId.ToString("D"));
            command.Parameters.AddWithValue("$key", record.IdempotencyKey.ToString("D"));
            command.Parameters.AddWithValue("$revision", record.Revision);
            command.Parameters.AddWithValue("$record", JsonSerializer.Serialize(record, JsonOptions));
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        catch (SqliteException exception) { throw new TransferJournalException(exception); }
    }

    public void Replace(DurableTransfer previous, DurableTransfer next)
    {
        DurableTransferMachine.Validate(next);
        if (next.TransferId != previous.TransferId || next.Revision != previous.Revision + 1) throw new TransferJournalException();
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE transfers SET revision=$next, record=$record
                WHERE transfer_id=$id AND revision=$previous;
                """;
            command.Parameters.AddWithValue("$id", previous.TransferId.ToString("D"));
            command.Parameters.AddWithValue("$previous", previous.Revision);
            command.Parameters.AddWithValue("$next", next.Revision);
            command.Parameters.AddWithValue("$record", JsonSerializer.Serialize(next, JsonOptions));
            if (command.ExecuteNonQuery() != 1) throw new TransferJournalException();
            transaction.Commit();
        }
        catch (SqliteException exception) { throw new TransferJournalException(exception); }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA synchronous=FULL;
                PRAGMA busy_timeout=5000;
                PRAGMA wal_autocheckpoint=256;
                PRAGMA journal_size_limit=4194304;
                PRAGMA max_page_count=32768;
                """;
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    public void Dispose() => lease.Dispose();
}

using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.Sqlite;
using PhoneTransfer.Application.Pairing;
using PhoneTransfer.Application.Security;
using PhoneTransfer.Domain;

namespace PhoneTransfer.Infrastructure.Persistence;

public sealed class SqliteDeviceRegistry : IPairedDeviceRegistry
{
    private static readonly TimeSpan LastSeenWriteInterval = TimeSpan.FromMinutes(1);
    private readonly string connectionString;
    private readonly TimeProvider clock;

    public SqliteDeviceRegistry(string databasePath, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version is not (0 or 1)) throw new InvalidOperationException("Unsupported device database version.");
        command.CommandText = "PRAGMA journal_mode=WAL";
        command.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS devices (
                device_id TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                certificate_sha256 TEXT NOT NULL UNIQUE,
                certificate_der TEXT NOT NULL,
                registered_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                permissions INTEGER NOT NULL,
                revoked INTEGER NOT NULL CHECK (revoked IN (0, 1))
            );
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public bool Register(VerifiedPairingIdentity identity, string certificateDer)
    {
        // Registration is local-only and receives the result of proof verification.
        // Check the digest again so persistence cannot accidentally bind the wrong DER.
        var digest = Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(certificateDer)));
        if (digest != identity.CertificateSha256) throw new ArgumentException("Certificate identity mismatch.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO devices VALUES ($id, $name, $hash, $der, $now, $now, $permissions, 0)
            ON CONFLICT(device_id) DO UPDATE SET
                display_name=excluded.display_name, certificate_sha256=excluded.certificate_sha256,
                certificate_der=excluded.certificate_der, registered_at=excluded.registered_at,
                last_seen_at=excluded.last_seen_at, permissions=excluded.permissions, revoked=0
            WHERE devices.revoked=1;
            """;
        command.Parameters.AddWithValue("$id", identity.DeviceId.ToString("D"));
        command.Parameters.AddWithValue("$name", identity.DisplayName);
        command.Parameters.AddWithValue("$hash", digest);
        command.Parameters.AddWithValue("$der", certificateDer);
        command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToString("O"));
        command.Parameters.AddWithValue("$permissions", (int)DevicePermissions.All);
        try { return command.ExecuteNonQuery() == 1; }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19) { return false; }
    }

    public PairedDevice? Authorize(X509Certificate2 certificate)
    {
        var now = clock.GetUtcNow();
        if (now < certificate.NotBefore.ToUniversalTime() || now >= certificate.NotAfter.ToUniversalTime()) return null;
        var digest = Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT device_id, display_name, certificate_sha256, registered_at, last_seen_at, permissions, revoked
            FROM devices WHERE certificate_sha256=$hash AND revoked=0 LIMIT 1;
            """;
        command.Parameters.AddWithValue("$hash", digest);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadDevice(reader) : null;
    }

    public bool TryTouchLastSeen(PairedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var now = clock.GetUtcNow();
        if (device.Revoked || now <= device.LastSeenAt || now - device.LastSeenAt < LastSeenWriteInterval) return false;

        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE devices SET last_seen_at=$now
                WHERE device_id=$id AND certificate_sha256=$hash AND revoked=0 AND last_seen_at=$previous;
                """;
            command.Parameters.AddWithValue("$id", device.DeviceId.ToString("D"));
            command.Parameters.AddWithValue("$hash", device.CertificateSha256);
            command.Parameters.AddWithValue("$previous", device.LastSeenAt.ToString("O"));
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            return command.ExecuteNonQuery() == 1;
        }
        catch (SqliteException)
        {
            // last-seen telemetry must not make an otherwise authorized API request unavailable.
            return false;
        }
    }

    public IReadOnlyList<PairedDevice> List()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT device_id, display_name, certificate_sha256, registered_at, last_seen_at, permissions, revoked
            FROM devices ORDER BY registered_at;
            """;
        using var reader = command.ExecuteReader();
        var devices = new List<PairedDevice>();
        while (reader.Read()) devices.Add(ReadDevice(reader));
        return devices;
    }

    public bool Revoke(Guid deviceId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE devices SET revoked=1 WHERE device_id=$id AND revoked=0";
        command.Parameters.AddWithValue("$id", deviceId.ToString("D"));
        return command.ExecuteNonQuery() == 1;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static PairedDevice ReadDevice(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
        DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
        (DevicePermissions)reader.GetInt32(5), reader.GetBoolean(6));
}

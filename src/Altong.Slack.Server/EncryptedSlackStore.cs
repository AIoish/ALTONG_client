using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Altong.Slack.Contracts;
using Microsoft.Data.Sqlite;

namespace Altong.Slack.Server;

/// <summary>Single-server durable store. Encryption binds each payload to its table/key.</summary>
public sealed class EncryptedSlackStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly byte[] _key;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly TimeSpan _retention;

    public EncryptedSlackStore(SlackServerOptions options, TimeProvider? time = null)
    {
        _key = options.EncryptionKey.ToArray();
        _time = time ?? TimeProvider.System;
        _retention = options.Retention;
        Directory.CreateDirectory(options.DataDirectory);
        _db = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(options.DataDirectory, "slack.db"), Pooling = false }.ToString());
        _db.Open();
        Execute("""
            PRAGMA journal_mode=WAL;
            PRAGMA secure_delete=ON;
            CREATE TABLE IF NOT EXISTS Pairings(Id TEXT PRIMARY KEY, StateHash TEXT UNIQUE, ProofHash TEXT, Expires INTEGER, Status TEXT, Result BLOB);
            CREATE TABLE IF NOT EXISTS Installations(UserId TEXT PRIMARY KEY, TeamId TEXT, SlackUserId TEXT, Credential BLOB, Active INTEGER,
                UNIQUE(TeamId, SlackUserId));
            CREATE TABLE IF NOT EXISTS Devices(Id TEXT PRIMARY KEY, UserId TEXT, TokenHash TEXT UNIQUE, Created INTEGER, Activated INTEGER DEFAULT 0);
            CREATE TABLE IF NOT EXISTS Inbox(Id TEXT PRIMARY KEY, Payload BLOB, Received INTEGER, Done INTEGER DEFAULT 0, RetryAt INTEGER DEFAULT 0);
            CREATE TABLE IF NOT EXISTS Deliveries(Id TEXT PRIMARY KEY, DeviceId TEXT, MessageId TEXT, Payload BLOB, Created INTEGER,
                UNIQUE(DeviceId, MessageId));
            CREATE TABLE IF NOT EXISTS DeliveryReceipts(DeviceId TEXT, MessageId TEXT, Created INTEGER, PRIMARY KEY(DeviceId,MessageId));
            """);
    }

    private long Now => _time.GetUtcNow().ToUnixTimeSeconds();
    private SqliteCommand Command(string sql, params (string, object?)[] parameters)
    {
        var cmd = _db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
    private int Execute(string sql, params (string, object?)[] parameters)
    { using var cmd = Command(sql, parameters); return cmd.ExecuteNonQuery(); }

    private byte[] Encrypt<T>(string context, T value)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(value), nonce = RandomNumberGenerator.GetBytes(12);
        var encrypted = new byte[28 + plain.Length]; nonce.CopyTo(encrypted, 0);
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(nonce, plain, encrypted.AsSpan(28), encrypted.AsSpan(12, 16), Encoding.UTF8.GetBytes(context));
            return encrypted;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private T Decrypt<T>(string context, byte[] encrypted)
    {
        var plain = new byte[encrypted.Length - 28];
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(encrypted.AsSpan(0, 12), encrypted.AsSpan(28), encrypted.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(context));
            return JsonSerializer.Deserialize<T>(plain) ?? throw new InvalidDataException("Invalid stored Slack data.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public (string Id, DateTimeOffset Expires) BeginPairing(string state, string proofHash)
    {
        if (!SlackConnectionProof.IsValid(proofHash)) throw new ArgumentException("Invalid connection challenge.");
        lock (_sync)
        {
            Cleanup();
            string id = Guid.NewGuid().ToString("N"); var expires = _time.GetUtcNow().AddMinutes(10);
            Execute("INSERT INTO Pairings VALUES($id,$state,$proof,$expires,'pending',NULL)",
                ("$id", id), ("$state", SlackConnectionProof.Hash(state)), ("$proof", proofHash), ("$expires", expires.ToUnixTimeSeconds()));
            return (id, expires);
        }
    }

    public string? ConsumeState(string state)
    {
        if (!SlackConnectionProof.IsValid(state)) return null;
        lock (_sync)
        {
            using var cmd = Command("UPDATE Pairings SET Status='authorizing' WHERE StateHash=$hash AND Status='pending' AND Expires>$now RETURNING Id",
                ("$hash", SlackConnectionProof.Hash(state)), ("$now", Now));
            return cmd.ExecuteScalar() as string;
        }
    }
    public void FailPairing(string id, string status)
    {
        lock (_sync) Execute("UPDATE Pairings SET Status=$status WHERE Id=$id AND Status='authorizing'", ("$id", id), ("$status", status));
    }

    public SlackDevice? CancelPairing(string id, string proof)
    {
        lock (_sync)
        {
            var status = PairingStatus(id, proof);
            if (status is null) return null;
            var device = status.Session is { } session ? Authenticate(session.Token) : null;
            Execute("UPDATE Pairings SET Status='cancelled',Result=NULL WHERE Id=$id", ("$id", id));
            return device;
        }
    }

    public SlackPairingStatus? PairingStatus(string id, string proof)
    {
        lock (_sync)
        {
            using var cmd = Command("SELECT ProofHash,Status,Result FROM Pairings WHERE Id=$id AND Expires>$now", ("$id", id), ("$now", Now));
            using var row = cmd.ExecuteReader();
            if (!row.Read() || !SlackConnectionProof.Matches(proof, row.GetString(0))) return null;
            return new(row.GetString(1), row.IsDBNull(2) ? null : Decrypt<SlackDeviceSession>("pair:" + id, (byte[])row[2]));
        }
    }

    public SlackDeviceSession CompletePairing(string id, SlackCredential credential)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            using (var valid = Command("SELECT COUNT(*) FROM Pairings WHERE Id=$id AND Status='authorizing' AND Expires>$now", ("$id", id), ("$now", Now)))
                if ((long)valid.ExecuteScalar()! != 1) throw new InvalidOperationException("Connection request expired.");
            string userId;
            using (var lookup = Command("SELECT UserId FROM Installations WHERE TeamId=$team AND SlackUserId=$user",
                ("$team", credential.Account.TeamId), ("$user", credential.Account.UserId)))
                userId = lookup.ExecuteScalar() as string ?? Guid.NewGuid().ToString("N");
            Execute("INSERT INTO Installations VALUES($id,$team,$user,$credential,1) ON CONFLICT(UserId) DO UPDATE SET Credential=$credential,Active=1",
                ("$id", userId), ("$team", credential.Account.TeamId), ("$user", credential.Account.UserId), ("$credential", Encrypt("token:" + userId, credential)));
            string deviceId = Guid.NewGuid().ToString("N"), token = SlackConnectionProof.NewSecret();
            Execute("INSERT INTO Devices VALUES($id,$user,$hash,$created,0)", ("$id", deviceId), ("$user", userId),
                ("$hash", SlackConnectionProof.Hash(token)), ("$created", _time.GetUtcNow().ToUnixTimeMilliseconds()));
            var session = new SlackDeviceSession(token, userId, credential.Account.Account, credential.Account.Workspace);
            Execute("UPDATE Pairings SET Status='complete',Result=$result WHERE Id=$id", ("$id", id), ("$result", Encrypt("pair:" + id, session)));
            tx.Commit(); return session;
        }
    }

    public SlackDevice? Authenticate(string token)
    {
        if (!SlackConnectionProof.IsValid(token)) return null;
        lock (_sync)
        {
            using var cmd = Command("SELECT d.Id,d.UserId,d.Activated FROM Devices d JOIN Installations i ON i.UserId=d.UserId WHERE d.TokenHash=$hash AND i.Active=1",
                ("$hash", SlackConnectionProof.Hash(token)));
            using var row = cmd.ExecuteReader();
            if (!row.Read()) return null;
            var device = new SlackDevice(row.GetString(0), row.GetString(1)); bool activated = row.GetInt64(2) == 1; row.Close();
            if (!activated) Execute("UPDATE Devices SET Activated=1 WHERE Id=$id", ("$id", device.Id));
            return device;
        }
    }
    public SlackInstallation? Installation(string userId)
    {
        lock (_sync)
        {
            using var cmd = Command("SELECT Credential FROM Installations WHERE UserId=$id AND Active=1", ("$id", userId));
            return cmd.ExecuteScalar() is byte[] bytes ? new(userId, Decrypt<SlackCredential>("token:" + userId, bytes)) : null;
        }
    }
    public SlackInstallation? InstallationFor(string team, string slackUser)
    {
        lock (_sync)
        {
            using var cmd = Command("SELECT UserId FROM Installations WHERE TeamId=$team AND SlackUserId=$user AND Active=1", ("$team", team), ("$user", slackUser));
            return cmd.ExecuteScalar() is string id ? Installation(id) : null;
        }
    }
    public void UpdateCredential(string userId, SlackCredential credential)
    { lock (_sync) Execute("UPDATE Installations SET Credential=$c WHERE UserId=$id AND Active=1", ("$id", userId), ("$c", Encrypt("token:" + userId, credential))); }

    public void Deactivate(string userId)
    {
        lock (_sync)
        {
            Execute("DELETE FROM Deliveries WHERE DeviceId IN (SELECT Id FROM Devices WHERE UserId=$id)", ("$id", userId));
            Execute("DELETE FROM DeliveryReceipts WHERE DeviceId IN (SELECT Id FROM Devices WHERE UserId=$id)", ("$id", userId));
            Execute("DELETE FROM Devices WHERE UserId=$id", ("$id", userId));
            Execute("UPDATE Installations SET Active=0,Credential=NULL WHERE UserId=$id", ("$id", userId));
        }
    }

    public void DeactivateTeam(string teamId)
    {
        lock (_sync)
        {
            using var cmd = Command("SELECT UserId FROM Installations WHERE TeamId=$team AND Active=1", ("$team", teamId));
            using var rows = cmd.ExecuteReader(); var ids = new List<string>(); while (rows.Read()) ids.Add(rows.GetString(0)); rows.Close();
            foreach (string id in ids) Deactivate(id);
        }
    }

    // Disconnect affects this authenticated PC. The last PC also removes the stored Slack grant.
    public SlackInstallation? Disconnect(SlackDevice device)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            Execute("DELETE FROM Deliveries WHERE DeviceId=$id", ("$id", device.Id));
            Execute("DELETE FROM DeliveryReceipts WHERE DeviceId=$id", ("$id", device.Id));
            Execute("DELETE FROM Devices WHERE Id=$id AND UserId=$user", ("$id", device.Id), ("$user", device.UserId));
            using var cmd = Command("SELECT COUNT(*) FROM Devices WHERE UserId=$id", ("$id", device.UserId));
            var last = (long)cmd.ExecuteScalar()! == 0 ? Installation(device.UserId) : null;
            if (last is not null) Deactivate(device.UserId);
            tx.Commit(); return last;
        }
    }

    public void Enqueue(string id, string payload)
    {
        lock (_sync) Execute("INSERT OR IGNORE INTO Inbox(Id,Payload,Received) VALUES($id,$p,$time)",
            ("$id", id), ("$p", Encrypt("inbox:" + id, payload)), ("$time", _time.GetUtcNow().ToUnixTimeMilliseconds()));
    }
    public SlackInboxItem? NextInbox()
    {
        lock (_sync)
        {
            Cleanup();
            using var cmd = Command("SELECT Id,Payload,Received FROM Inbox WHERE Done=0 AND RetryAt<=$now ORDER BY Received LIMIT 1", ("$now", Now));
            using var row = cmd.ExecuteReader(); if (!row.Read()) return null;
            string id = row.GetString(0);
            return new(id, Decrypt<string>("inbox:" + id, (byte[])row[1]), DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(2)));
        }
    }
    public void CompleteInbox(string id)
    { lock (_sync) Execute("UPDATE Inbox SET Done=1,Payload=NULL WHERE Id=$id", ("$id", id)); }
    public void DeferInbox(string id, TimeSpan delay)
    { lock (_sync) Execute("UPDATE Inbox SET RetryAt=$retry WHERE Id=$id", ("$id", id), ("$retry", Now + Math.Max(1, (long)Math.Ceiling(delay.TotalSeconds)))); }
    public void Deliver(string userId, SlackRelayMessage message, DateTimeOffset receivedAt)
    {
        lock (_sync)
        {
            // Only PCs already connected at receipt are recipients. No history backfill on OAuth.
            using var cmd = Command("SELECT Id FROM Devices WHERE UserId=$user AND Activated=1 AND Created<=$received", ("$user", userId),
                ("$received", receivedAt.ToUnixTimeMilliseconds()));
            using var rows = cmd.ExecuteReader(); var ids = new List<string>(); while (rows.Read()) ids.Add(rows.GetString(0)); rows.Close();
            foreach (string device in ids)
            {
                string id = SlackConnectionProof.Hash(device + ":" + message.Id);
                Execute("INSERT OR IGNORE INTO Deliveries SELECT $id,$device,$message,$payload,$created WHERE NOT EXISTS (SELECT 1 FROM DeliveryReceipts WHERE DeviceId=$device AND MessageId=$message)",
                    ("$id", id), ("$device", device), ("$message", message.Id), ("$payload", Encrypt("delivery:" + id, message)), ("$created", receivedAt.ToUnixTimeSeconds()));
            }
        }
    }
    public SlackDelivery[] Deliveries(SlackDevice device)
    {
        lock (_sync)
        {
            Cleanup();
            using var cmd = Command("SELECT Id,Payload FROM Deliveries WHERE DeviceId=$id ORDER BY Created,Id LIMIT 10", ("$id", device.Id));
            using var rows = cmd.ExecuteReader(); var result = new List<SlackDelivery>();
            while (rows.Read()) { string id = rows.GetString(0); result.Add(new(id, Decrypt<SlackRelayMessage>("delivery:" + id, (byte[])rows[1]))); }
            return result.ToArray();
        }
    }
    public void Acknowledge(SlackDevice device, IEnumerable<string> ids)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            foreach (string id in ids.Take(100))
            {
                Execute("INSERT OR IGNORE INTO DeliveryReceipts SELECT DeviceId,MessageId,Created FROM Deliveries WHERE Id=$id AND DeviceId=$device",
                    ("$id", id), ("$device", device.Id));
                Execute("DELETE FROM Deliveries WHERE Id=$id AND DeviceId=$device", ("$id", id), ("$device", device.Id));
            }
            tx.Commit();
        }
    }
    private void Cleanup()
    {
        Execute("DELETE FROM Pairings WHERE Expires<=$now", ("$now", Now));
        Execute("DELETE FROM Devices WHERE Activated=0 AND Created<$cutoff", ("$cutoff", _time.GetUtcNow().AddMinutes(-10).ToUnixTimeMilliseconds()));
        Execute("UPDATE Installations SET Active=0,Credential=NULL WHERE Active=1 AND NOT EXISTS (SELECT 1 FROM Devices WHERE Devices.UserId=Installations.UserId)");
        Execute("DELETE FROM Deliveries WHERE Created<$cutoff", ("$cutoff", Now - (long)_retention.TotalSeconds));
        Execute("DELETE FROM DeliveryReceipts WHERE Created<$cutoff", ("$cutoff", Now - (long)_retention.TotalSeconds));
        Execute("DELETE FROM Inbox WHERE Received<$cutoff", ("$cutoff", (_time.GetUtcNow() - _retention).ToUnixTimeMilliseconds()));
    }
    public void Dispose() { lock (_sync) { _db.Dispose(); CryptographicOperations.ZeroMemory(_key); } }
}

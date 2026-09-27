using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AlchemistPayTrack.Infrastructure;

namespace AlchemistPayTrack.Application
{
    public class AuditEntry
    {
        public int AuditId { get; set; }
        public string Action { get; set; } = "";
        public string TableName { get; set; } = "";
        public string RecordId { get; set; } = "";
        public string Username { get; set; } = "System";
        public DateTime Timestamp { get; set; }
        public string? OldValue { get; set; }
        public string? NewValue { get; set; }
        public string? Device { get; set; }
    }

    public interface IAuditService
    {
        Task LogAsync(string action, string tableName, string recordId, int? userId, string? oldValue = null, string? newValue = null);
        Task<List<AuditEntry>> GetPagedAsync(int pageNumber, int pageSize);
        Task<int> CountAsync();
    }

    /// <summary>
    /// Writes to the existing AuditLog table. UserId is a real foreign key
    /// into Users. Device is captured automatically (the local machine name)
    /// on every entry - this is an offline app with no network calls, so
    /// there is no IP address to meaningfully record; Device is the honest
    /// local equivalent for a machine-level audit trail (useful mainly if
    /// the same database is later shared across workstations).
    ///
    /// Failures here are logged but never thrown - losing an audit entry
    /// should not stop the underlying business operation from completing.
    /// </summary>
    public class AuditService : IAuditService
    {
        private readonly IConnectionPool _connectionPool;
        private readonly ILogger _logger;
        private static readonly string DeviceName = Environment.MachineName;

        public AuditService(IConnectionPool connectionPool, ILogger logger)
        {
            _connectionPool = connectionPool;
            _logger = logger;
        }

        public async Task LogAsync(string action, string tableName, string recordId, int? userId, string? oldValue = null, string? newValue = null)
        {
            try
            {
                var conn = await _connectionPool.GetConnectionAsync();
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO AuditLog (Action, TableName, RecordId, UserId, Timestamp, OldValue, NewValue, Device, IPAddress)
                        VALUES (@action, @table, @recordId, @userId, @timestamp, @oldValue, @newValue, @device, NULL)";
                    cmd.Parameters.AddWithValue("@action", action);
                    cmd.Parameters.AddWithValue("@table", tableName);
                    cmd.Parameters.AddWithValue("@recordId", recordId);
                    cmd.Parameters.AddWithValue("@userId", (object?)userId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    cmd.Parameters.AddWithValue("@oldValue", (object?)oldValue ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@newValue", (object?)newValue ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@device", DeviceName);

                    await cmd.ExecuteNonQueryAsync();
                }
                finally
                {
                    _connectionPool.ReturnConnection(conn);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to write audit entry for {action} on {tableName}", ex);
            }
        }

        public async Task<List<AuditEntry>> GetPagedAsync(int pageNumber, int pageSize)
        {
            // Deliberately NOT gated by ViewAuditLogs here: this same method
            // backs both the full Activity Log page (Administrator only,
            // already gated by hiding that whole tab in the UI) and the
            // Dashboard's small "recent activity" widget, which every role
            // sees on the landing page. Gating this method itself would
            // break the Dashboard for Supervisor and Clerk. Audit data is
            // read-only and never actioned on, so UI-level gating of the
            // full page is the appropriate amount of protection here.
            var conn = await _connectionPool.GetConnectionAsync();
            try
            {
                var results = new List<AuditEntry>();
                var offset = (pageNumber - 1) * pageSize;

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT a.AuditId, a.Action, a.TableName, a.RecordId, a.Timestamp, a.OldValue, a.NewValue,
                           COALESCE(u.FullName, 'System') AS ActorName, a.Device
                    FROM AuditLog a
                    LEFT JOIN Users u ON a.UserId = u.UserId
                    ORDER BY a.Timestamp DESC
                    LIMIT @limit OFFSET @offset";
                cmd.Parameters.AddWithValue("@limit", pageSize);
                cmd.Parameters.AddWithValue("@offset", offset);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    results.Add(new AuditEntry
                    {
                        AuditId = reader.GetInt32(0),
                        Action = reader.GetString(1),
                        TableName = reader.GetString(2),
                        RecordId = reader.GetString(3),
                        Timestamp = DateTime.Parse(reader.GetString(4)),
                        OldValue = reader.IsDBNull(5) ? null : reader.GetString(5),
                        NewValue = reader.IsDBNull(6) ? null : reader.GetString(6),
                        Username = reader.GetString(7),
                        Device = reader.IsDBNull(8) ? null : reader.GetString(8)
                    });
                }

                return results;
            }
            finally
            {
                _connectionPool.ReturnConnection(conn);
            }
        }

        public async Task<int> CountAsync()
        {
            // See GetPagedAsync above - not gated for the same reason.
            var conn = await _connectionPool.GetConnectionAsync();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM AuditLog";
                return Convert.ToInt32(await cmd.ExecuteScalarAsync());
            }
            finally
            {
                _connectionPool.ReturnConnection(conn);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace AlchemistPayTrack.Infrastructure
{
    public class BackupFileInfo
    {
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public long SizeBytes { get; set; }
    }

    public interface IBackupService
    {
        string BackupFolder { get; }

        /// <summary>Copies the current database file to a timestamped backup, then
        /// deletes old backups beyond <paramref name="keepCount"/>.</summary>
        Task<string?> BackupNowAsync(int keepCount = 10);

        /// <summary>Lists available backup files, most recent first.</summary>
        Task<List<BackupFileInfo>> ListBackupsAsync();

        /// <summary>Restores the database from a previously created backup.
        /// Requires AppPermission.RestoreBackups. Takes a safety snapshot of
        /// the current (pre-restore) database first, so a restore can
        /// itself be undone if it turns out to be the wrong file.</summary>
        Task<(bool Success, string Message)> RestoreAsync(string backupFileName);
    }

    /// <summary>
    /// Simple file-copy backup for the SQLite database. Deliberately avoids
    /// anything fancier (encryption, cloud upload) - the goal is a cheap,
    /// reliable safety net against accidental deletion/corruption, not a full
    /// disaster-recovery system.
    /// </summary>
    public class BackupService : IBackupService
    {
        private readonly IAppConfiguration _config;
        private readonly ILogger _logger;
        private readonly IConnectionPool _connectionPool;
        private readonly IAuthorizationService _authorization;

        public string BackupFolder { get; }

        // BackupService is registered as a singleton. IAuthorizationService
        // is transient but stateless - it just reads the (singleton)
        // ICurrentUserContext on every call - so capturing one instance for
        // the app's lifetime is safe here; it always reflects whoever is
        // currently signed in, not whoever was signed in when the app
        // started.
        public BackupService(IAppConfiguration config, ILogger logger, IConnectionPool connectionPool, IAuthorizationService authorization)
        {
            _config = config;
            _logger = logger;
            _connectionPool = connectionPool;
            _authorization = authorization;
            BackupFolder = Path.Combine(_config.AppDataPath, "Backups");
        }

        public async Task<string?> BackupNowAsync(int keepCount = 10)
        {
            try
            {
                if (!File.Exists(_config.DatabasePath))
                {
                    _logger.LogWarning("Backup skipped: database file not found yet.");
                    return null;
                }

                Directory.CreateDirectory(BackupFolder);

                // The database runs in WAL mode, which means recently
                // committed writes can still be sitting in the separate
                // -wal file rather than the main .db file at any given
                // moment. A plain file copy of just the .db file could
                // therefore silently miss the most recent transactions.
                // Forcing a checkpoint first flushes everything from the
                // WAL into the main file, so the single .db file copied
                // below is guaranteed to be complete and self-consistent
                // on its own.
                await CheckpointWalAsync();

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var backupFileName = $"AlchemistPayTrack_{timestamp}.db";
                var backupPath = Path.Combine(BackupFolder, backupFileName);

                await Task.Run(() => File.Copy(_config.DatabasePath, backupPath, overwrite: true));

                _logger.LogInfo($"Database backed up to {backupPath}");

                RotateOldBackups(keepCount);

                return backupPath;
            }
            catch (Exception ex)
            {
                // A failed backup should never take the app down with it.
                _logger.LogError("Database backup failed", ex);
                return null;
            }
        }

        /// <summary>Forces SQLite to flush the write-ahead log into the main
        /// database file and truncate the -wal file, so a plain file copy
        /// taken immediately afterwards is complete on its own. Best-effort:
        /// if it fails for some reason, backup/restore still proceed (most
        /// of the time WAL contents get folded in anyway), but this makes
        /// that the common case rather than something left to chance.</summary>
        private async Task CheckpointWalAsync()
        {
            try
            {
                var conn = await _connectionPool.GetConnectionAsync();
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    await cmd.ExecuteNonQueryAsync();
                }
                finally
                {
                    _connectionPool.ReturnConnection(conn);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"WAL checkpoint before backup did not complete cleanly: {ex.Message}");
            }
        }

        public Task<List<BackupFileInfo>> ListBackupsAsync()
        {
            try
            {
                Directory.CreateDirectory(BackupFolder);

                var files = new DirectoryInfo(BackupFolder)
                    .GetFiles("AlchemistPayTrack_*.db")
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .Select(f => new BackupFileInfo
                    {
                        FileName = f.Name,
                        FullPath = f.FullName,
                        CreatedAt = f.CreationTime,
                        SizeBytes = f.Length
                    })
                    .ToList();

                return Task.FromResult(files);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to list backups", ex);
                return Task.FromResult(new List<BackupFileInfo>());
            }
        }

        public async Task<(bool Success, string Message)> RestoreAsync(string backupFileName)
        {
            // Server-side enforcement, independent of whether the UI
            // happens to still be showing the Restore button - a role
            // without RestoreBackups can never trigger this, full stop.
            _authorization.Require(AppPermission.RestoreBackups);

            if (string.IsNullOrWhiteSpace(backupFileName))
            {
                return (false, "No backup file specified.");
            }

            // Only ever accept a bare file name and resolve it inside
            // BackupFolder - never trust a caller-supplied path, which
            // would otherwise let something like "..\..\Windows\x.db" read
            // or overwrite files outside the backup directory.
            var safeName = Path.GetFileName(backupFileName);
            var sourcePath = Path.Combine(BackupFolder, safeName);

            if (!File.Exists(sourcePath))
            {
                return (false, $"Backup file '{safeName}' was not found.");
            }

            // Validate the candidate file BEFORE touching the live
            // database. A corrupted, truncated, or non-database file
            // copied over the live data would otherwise take the whole
            // app down with no way back - checking first means a bad
            // backup file fails loudly here, with the live database
            // completely untouched, rather than destructively.
            var validation = await ValidateBackupFileAsync(sourcePath);
            if (!validation.IsValid)
            {
                _logger.LogWarning($"Restore aborted - backup file failed validation: {validation.Reason}");
                return (false, $"This backup file doesn't look valid ({validation.Reason}) and was not restored. The current database was not touched.");
            }

            try
            {
                // Snapshot the current (about-to-be-replaced) database first,
                // tagged distinctly, so a restore of the wrong backup can
                // itself be undone. Checkpoint first for the same reason as
                // BackupNowAsync - so the snapshot is complete on its own.
                if (File.Exists(_config.DatabasePath))
                {
                    await CheckpointWalAsync();
                    Directory.CreateDirectory(BackupFolder);
                    var preRestoreName = $"AlchemistPayTrack_{DateTime.Now:yyyyMMdd_HHmmss}_PreRestore.db";
                    await Task.Run(() => File.Copy(_config.DatabasePath, Path.Combine(BackupFolder, preRestoreName), overwrite: true));
                    _logger.LogInfo($"Pre-restore safety snapshot created: {preRestoreName}");
                }

                // Release every pooled connection (and SQLite's own native
                // handles) so the database file on disk is free to be
                // overwritten. WAL mode also leaves -wal/-shm sidecar files
                // next to the main .db file; clear those out too so no stale
                // write-ahead data gets replayed against the restored file.
                await _connectionPool.CloseAllConnectionsAsync();

                await Task.Run(() => File.Copy(sourcePath, _config.DatabasePath, overwrite: true));

                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    var sidecar = _config.DatabasePath + suffix;
                    if (File.Exists(sidecar))
                    {
                        try { File.Delete(sidecar); } catch { /* best-effort cleanup */ }
                    }
                }

                // Pre-restore snapshots match the same "AlchemistPayTrack_*.db"
                // pattern RotateOldBackups already globs on, so they're
                // naturally swept into the same rotation pool as regular
                // backups (by creation time) rather than accumulating
                // separately and unboundedly.
                RotateOldBackups(10);

                _logger.LogInfo($"Database restored from backup '{safeName}'");
                return (true, $"Database restored from '{safeName}'.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Restore from '{safeName}' failed", ex);
                return (false, "Restore failed. The current database has not been changed. Please try again, or contact support if this keeps happening.");
            }
        }

        /// <summary>Opens the candidate file read-only, independent of the
        /// live connection pool, and checks it's actually a readable SQLite
        /// database with the tables this app expects - catching a corrupted,
        /// truncated, wrong-format, or unrelated file before it ever touches
        /// the live database.</summary>
        private async Task<(bool IsValid, string Reason)> ValidateBackupFileAsync(string path)
        {
            try
            {
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly
                };

                using var conn = new SqliteConnection(builder.ConnectionString);
                await conn.OpenAsync();

                using (var integrityCmd = conn.CreateCommand())
                {
                    integrityCmd.CommandText = "PRAGMA integrity_check;";
                    var result = (string?)await integrityCmd.ExecuteScalarAsync();
                    if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        return (false, $"integrity check reported: {result}");
                    }
                }

                // Confirm this is actually an AlchemistPayTrack database and
                // not just some other well-formed SQLite file.
                string[] expectedTables = { "Users", "Workers", "TimeRecords", "Payments" };
                foreach (var table in expectedTables)
                {
                    using var checkCmd = conn.CreateCommand();
                    checkCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name;";
                    checkCmd.Parameters.AddWithValue("@name", table);
                    var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());
                    if (count == 0)
                    {
                        return (false, $"missing expected table '{table}'");
                    }
                }

                return (true, "");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Backup file validation failed for '{path}': {ex.Message}");
                return (false, "could not be opened as a database - it may be corrupted, incomplete, or not a database file at all");
            }
        }

        private void RotateOldBackups(int keepCount)
        {
            try
            {
                var backups = new DirectoryInfo(BackupFolder)
                    .GetFiles("AlchemistPayTrack_*.db")
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .Skip(keepCount)
                    .ToList();

                foreach (var old in backups)
                {
                    old.Delete();
                }

                if (backups.Count > 0)
                {
                    _logger.LogInfo($"Removed {backups.Count} old backup(s), keeping most recent {keepCount}.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to rotate old backups", ex);
            }
        }
    }
}

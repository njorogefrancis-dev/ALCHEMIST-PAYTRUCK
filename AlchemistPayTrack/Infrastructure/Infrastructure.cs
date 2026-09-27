/*
 * LAYER 1: INFRASTRUCTURE IMPLEMENTATION
 * Complete production-ready code for configuration, logging, exceptions,
 * connection pooling, and database initialization
 */

using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace AlchemistPayTrack.Infrastructure
{
    #region Configuration

    /// <summary>
    /// Application configuration interface
    /// </summary>
    public interface IAppConfiguration
    {
        string DatabasePath { get; }
        string AppDataPath { get; }
        string ReciptsPath { get; }
        string ReportsPath { get; }
        string LogsPath { get; }
        int ConnectionPoolSize { get; }
        int CommandTimeoutSeconds { get; }
        string DefaultDateFormat { get; }
        bool EnableQueryLogging { get; }
    }

    /// <summary>
    /// Default implementation - can be overridden for different environments
    /// </summary>
    public class AppConfiguration : IAppConfiguration
    {
        public string AppDataPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AlchemistPayTrack");

        public string DatabasePath => Path.Combine(AppDataPath, "alchemistpaytrack.db");
        public string ReciptsPath => Path.Combine(AppDataPath, "Receipts");
        public string ReportsPath => Path.Combine(AppDataPath, "Reports");
        public string LogsPath => Path.Combine(AppDataPath, "Logs");
        public int ConnectionPoolSize => 5;
        public int CommandTimeoutSeconds => 30;
        public string DefaultDateFormat => "yyyy-MM-dd";
        public bool EnableQueryLogging => false;
    }

    #endregion

    #region Logging

    /// <summary>
    /// Logging interface - abstraction for any logging implementation
    /// </summary>
    public interface ILogger
    {
        void LogDebug(string message, params object[] args);
        void LogInfo(string message, params object[] args);
        void LogWarning(string message, params object[] args);
        void LogError(string message, Exception? ex = null, params object[] args);
    }

    /// <summary>
    /// File-based logging implementation
    /// </summary>
    public class FileLogger : ILogger
    {
        private readonly IAppConfiguration _config;
        private readonly object _lock = new();

        public FileLogger(IAppConfiguration config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            Directory.CreateDirectory(_config.LogsPath);
        }

        private void WriteLog(string level, string message, Exception? ex, params object[] args)
        {
            lock (_lock)
            {
                try
                {
                    var logPath = Path.Combine(_config.LogsPath, $"{DateTime.Now:yyyyMMdd}.log");
                    var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {string.Format(message, args)}";

                    if (ex != null)
                    {
                        logMessage += Environment.NewLine + ex;
                    }

                    File.AppendAllText(logPath, logMessage + Environment.NewLine);

                    // Keep only last 30 days of logs
                    CleanOldLogs();
                }
                catch
                {
                    // Silently fail - don't throw from logging
                }
            }
        }

        private void CleanOldLogs()
        {
            try
            {
                var dir = new DirectoryInfo(_config.LogsPath);
                var oldFiles = dir.GetFiles("*.log")
                    .Where(f => f.LastWriteTime < DateTime.Now.AddDays(-30))
                    .ToList();

                foreach (var file in oldFiles)
                {
                    file.Delete();
                }
            }
            catch { }
        }

        public void LogDebug(string message, params object[] args) => WriteLog("DEBUG", message, null, args);
        public void LogInfo(string message, params object[] args) => WriteLog("INFO", message, null, args);
        public void LogWarning(string message, params object[] args) => WriteLog("WARN", message, null, args);
        public void LogError(string message, Exception? ex = null, params object[] args) => WriteLog("ERROR", message, ex, args);
    }

    #endregion

    #region Exceptions

    /// <summary>
    /// Base exception for all data access errors
    /// </summary>
    public class DataAccessException : Exception
    {
        public DataAccessException(string message) : base(message) { }
        public DataAccessException(string message, Exception innerException) 
            : base(message, innerException) { }
    }

    /// <summary>
    /// Validation exception for business rule violations
    /// </summary>
    public class ValidationException : Exception
    {
        public List<string> Errors { get; }

        public ValidationException(string message) : base(message)
        {
            Errors = new() { message };
        }

        public ValidationException(List<string> errors) 
            : base(string.Join(", ", errors))
        {
            Errors = errors;
        }
    }

    /// <summary>
    /// Exception for concurrent modification conflicts
    /// </summary>
    public class ConcurrencyException : Exception
    {
        public ConcurrencyException(string message) : base(message) { }
    }

    /// <summary>
    /// Thrown by the service layer when the signed-in user's role does not
    /// carry a required permission. This is the server-side enforcement
    /// backstop - UI visibility/enabled bindings are only a convenience,
    /// they are not a security boundary on their own. Every sensitive
    /// service method re-checks the permission itself so a role that isn't
    /// supposed to do something can't do it even if a view somehow still
    /// exposed the control.
    /// </summary>
    public class AuthorizationException : Exception
    {
        public AuthorizationException(string message) : base(message) { }
    }

    #endregion

    #region Connection Pooling

    /// <summary>
    /// Connection pool interface
    /// </summary>
    public interface IConnectionPool : IDisposable
    {
        Task<SqliteConnection> GetConnectionAsync();
        void ReturnConnection(SqliteConnection connection);
        int AvailableConnections { get; }
        int TotalConnections { get; }

        /// <summary>Closes and discards every pooled connection so the
        /// underlying database file is released on disk. Needed before a
        /// restore overwrites the file - a copy over an open SQLite file
        /// (especially with WAL mode holding -wal/-shm handles) fails or
        /// corrupts the target. Safe to call at any time: subsequent calls
        /// to GetConnectionAsync simply open fresh connections again.</summary>
        Task CloseAllConnectionsAsync();
    }

    /// <summary>
    /// Production-grade SQLite connection pool
    /// </summary>
    public class SqliteConnectionPool : IConnectionPool
    {
        private readonly IAppConfiguration _config;
        private readonly ILogger _logger;
        private readonly Queue<SqliteConnection> _availableConnections;
        private readonly HashSet<SqliteConnection> _allConnections;
        private readonly int _maxPoolSize;
        private readonly object _lock = new();
        private bool _disposed;

        public int AvailableConnections 
        { 
            get 
            { 
                lock (_lock) return _availableConnections.Count; 
            } 
        }

        public int TotalConnections 
        { 
            get 
            { 
                lock (_lock) return _allConnections.Count; 
            } 
        }

        public SqliteConnectionPool(IAppConfiguration config, ILogger logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _maxPoolSize = config.ConnectionPoolSize;
            _availableConnections = new Queue<SqliteConnection>(_maxPoolSize);
            _allConnections = new HashSet<SqliteConnection>();

            EnsureDatabase();
            _logger.LogInfo($"Connection pool initialized with max size {_maxPoolSize}");
        }

        private void EnsureDatabase()
        {
            Directory.CreateDirectory(_config.AppDataPath);
            
            // Create database if it doesn't exist
            var path = _config.DatabasePath;
            if (!File.Exists(path))
            {
                using var conn = new SqliteConnection($"Data Source={path}");
                conn.Open();
                conn.Close();
                _logger.LogInfo($"Database created at {path}");
            }
        }

        public async Task<SqliteConnection> GetConnectionAsync()
        {
            ThrowIfDisposed();

            SqliteConnection? conn = null;

            // Try to get from pool
            lock (_lock)
            {
                if (_availableConnections.Count > 0)
                {
                    conn = _availableConnections.Dequeue();
                    _logger.LogDebug("Connection retrieved from pool. Available: {0}", 
                        _availableConnections.Count);
                }
            }

            // Create new if pool empty and under limit
            if (conn == null)
            {
                lock (_lock)
                {
                    if (_allConnections.Count < _maxPoolSize)
                    {
                        conn = CreateNewConnection();
                        _allConnections.Add(conn);
                        _logger.LogDebug("New connection created. Total: {0}", _allConnections.Count);
                    }
                }
            }

            // Wait if at capacity
            if (conn == null)
            {
                return await WaitForConnectionAsync();
            }

            return conn;
        }

        public void ReturnConnection(SqliteConnection connection)
        {
            if (connection?.State == System.Data.ConnectionState.Open)
            {
                lock (_lock)
                {
                    _availableConnections.Enqueue(connection);
                    _logger.LogDebug("Connection returned to pool. Available: {0}", 
                        _availableConnections.Count);
                }
            }
        }

        private SqliteConnection CreateNewConnection()
        {
            var conn = new SqliteConnection($"Data Source={_config.DatabasePath}");
            conn.Open();

            // Performance pragmas
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;
                PRAGMA synchronous=NORMAL;
                PRAGMA cache_size=10000;
                PRAGMA temp_store=MEMORY;";
            cmd.ExecuteNonQuery();

            return conn;
        }

        private async Task<SqliteConnection> WaitForConnectionAsync()
        {
            var maxWaitMs = 30000;
            var elapsedMs = 0;
            var waitMs = 100;

            while (elapsedMs < maxWaitMs)
            {
                await Task.Delay(waitMs);
                elapsedMs += waitMs;

                lock (_lock)
                {
                    if (_availableConnections.Count > 0)
                    {
                        var conn = _availableConnections.Dequeue();
                        _logger.LogDebug("Connection retrieved from pool after waiting {0}ms", elapsedMs);
                        return conn;
                    }
                }

                waitMs = Math.Min(waitMs * 2, 1000);
            }

            _logger.LogError($"Connection pool timeout after {maxWaitMs}ms");
            throw new DataAccessException("Connection pool timeout: no connections available");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SqliteConnectionPool));
        }

        public Task CloseAllConnectionsAsync()
        {
            ThrowIfDisposed();

            lock (_lock)
            {
                foreach (var conn in _allConnections)
                {
                    try { conn?.Close(); conn?.Dispose(); }
                    catch { }
                }
                _allConnections.Clear();
                _availableConnections.Clear();
            }

            // Ask SQLite's own process-wide connection pool to drop its
            // native handles too - Microsoft.Data.Sqlite keeps a pool below
            // this one, and closing our SqliteConnection objects above
            // doesn't always release the underlying file immediately.
            SqliteConnection.ClearAllPools();

            _logger.LogInfo("All database connections closed for restore.");
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_lock)
            {
                foreach (var conn in _allConnections)
                {
                    try { conn?.Dispose(); }
                    catch { }
                }
                _allConnections.Clear();
            }

            lock (_lock)
            {
                _availableConnections.Clear();
            }

            _logger.LogInfo("Connection pool disposed");
        }
    }

    #endregion

    #region Database Initialization

    /// <summary>
    /// Database initialization service
    /// </summary>
    public interface IDatabaseInitializer
    {
        Task InitializeAsync();
        Task CreateTablesAsync();
        Task MigrateAsync();
    }

    public class DatabaseInitializer : IDatabaseInitializer
    {
        private readonly IConnectionPool _connectionPool;
        private readonly ILogger _logger;

        public DatabaseInitializer(IConnectionPool connectionPool, ILogger logger)
        {
            _connectionPool = connectionPool;
            _logger = logger;
        }

        public async Task InitializeAsync()
        {
            _logger.LogInfo("Initializing database");
            await CreateTablesAsync();
            await MigrateAsync();
            _logger.LogInfo("Database initialization complete");
        }

        public async Task CreateTablesAsync()
        {
            var conn = await _connectionPool.GetConnectionAsync();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Users (
                        UserId INTEGER PRIMARY KEY AUTOINCREMENT,
                        Username TEXT UNIQUE NOT NULL,
                        PasswordHash TEXT NOT NULL,
                        FullName TEXT NOT NULL,
                        Role TEXT NOT NULL DEFAULT 'Clerk',
                        Status TEXT NOT NULL DEFAULT 'Active',
                        CreatedAt DATETIME NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS PasswordHistory (
                        HistoryId INTEGER PRIMARY KEY AUTOINCREMENT,
                        UserId INTEGER NOT NULL,
                        PasswordHash TEXT NOT NULL,
                        ChangedAt DATETIME NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS SecurityQuestions (
                        QuestionId INTEGER PRIMARY KEY AUTOINCREMENT,
                        UserId INTEGER NOT NULL,
                        QuestionText TEXT NOT NULL,
                        AnswerHash TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS IX_PasswordHistory_UserId ON PasswordHistory(UserId);
                    CREATE INDEX IF NOT EXISTS IX_SecurityQuestions_UserId ON SecurityQuestions(UserId);

                    CREATE TABLE IF NOT EXISTS Workers (
                        WorkerId TEXT PRIMARY KEY,
                        FullName TEXT NOT NULL,
                        NationalId TEXT UNIQUE NOT NULL,
                        PhoneNumber TEXT NOT NULL,
                        DateJoined DATETIME NOT NULL,
                        Gender TEXT,
                        Address TEXT,
                        EmergencyContact TEXT,
                        WorkerCategory TEXT NOT NULL DEFAULT 'Casual',
                        JobRole TEXT NOT NULL DEFAULT 'General Laborer',
                        DailyWageRate REAL NOT NULL,
                        Status TEXT NOT NULL DEFAULT 'Active',
                        CreatedBy TEXT NOT NULL,
                        CreatedAt DATETIME NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS TimeRecords (
                        TimeRecordId INTEGER PRIMARY KEY AUTOINCREMENT,
                        WorkerId TEXT NOT NULL,
                        Date DATETIME NOT NULL,
                        ClockInTime TIME,
                        ClockOutTime TIME,
                        HoursWorked REAL DEFAULT 0,
                        Status TEXT NOT NULL DEFAULT 'Present',
                        RecordType TEXT NOT NULL DEFAULT 'Manual',
                        CreatedBy TEXT NOT NULL,
                        CreatedAt DATETIME NOT NULL,
                        FOREIGN KEY(WorkerId) REFERENCES Workers(WorkerId),
                        UNIQUE(WorkerId, Date, RecordType)
                    );

                    CREATE TABLE IF NOT EXISTS Payments (
                        PaymentId INTEGER PRIMARY KEY AUTOINCREMENT,
                        ReceiptNumber TEXT UNIQUE NOT NULL,
                        WorkerId TEXT NOT NULL,
                        AmountPaid REAL NOT NULL,
                        PaymentDate DATETIME NOT NULL,
                        PaymentMethod TEXT NOT NULL,
                        MpesaCode TEXT,
                        RecordedBy TEXT NOT NULL,
                        CreatedAt DATETIME NOT NULL,
                        FOREIGN KEY(WorkerId) REFERENCES Workers(WorkerId)
                    );

                    CREATE TABLE IF NOT EXISTS AppSettings (
                        Key TEXT PRIMARY KEY,
                        Value TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS AuditLog (
                        AuditId INTEGER PRIMARY KEY AUTOINCREMENT,
                        Action TEXT NOT NULL,
                        TableName TEXT NOT NULL,
                        RecordId TEXT NOT NULL,
                        UserId INTEGER,
                        Timestamp DATETIME NOT NULL,
                        OldValue TEXT,
                        NewValue TEXT
                    );

                    CREATE INDEX IF NOT EXISTS IX_TimeRecords_WorkerId ON TimeRecords(WorkerId);
                    CREATE INDEX IF NOT EXISTS IX_TimeRecords_Date ON TimeRecords(Date);
                    CREATE INDEX IF NOT EXISTS IX_Payments_WorkerId ON Payments(WorkerId);
                    CREATE INDEX IF NOT EXISTS IX_Payments_Date ON Payments(PaymentDate);
                    CREATE INDEX IF NOT EXISTS IX_AuditLog_Timestamp ON AuditLog(Timestamp);
                ";

                await cmd.ExecuteNonQueryAsync();
                _logger.LogInfo("Database tables created/verified");
            }
            finally
            {
                _connectionPool.ReturnConnection(conn);
            }
        }

        public async Task MigrateAsync()
        {
            await AddColumnIfMissingAsync("Users", "FailedLoginAttempts", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync("Users", "LockedUntil", "TEXT NULL");
            await AddColumnIfMissingAsync("Users", "PasswordChangedAt", "TEXT NULL");
            await AddColumnIfMissingAsync("AuditLog", "Device", "TEXT NULL");
            await AddColumnIfMissingAsync("AuditLog", "IPAddress", "TEXT NULL");
            await MigrateRoleNamesAsync();

            _logger.LogInfo("Database migrations complete");
        }

        /// <summary>
        /// Earlier versions of this app used "Admin"/"DataEntry" as the two
        /// role names. RBAC introduces three fixed roles instead
        /// (Administrator/Supervisor/Clerk) - this maps existing accounts
        /// onto the closest equivalent so nobody is silently locked out of
        /// permissions they previously had.
        /// </summary>
        private async Task MigrateRoleNamesAsync()
        {
            var conn = await _connectionPool.GetConnectionAsync();
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE Users SET Role = 'Administrator' WHERE Role = 'Admin'";
                    var adminRows = await cmd.ExecuteNonQueryAsync();
                    if (adminRows > 0) _logger.LogInfo($"Migrated {adminRows} user(s) from role 'Admin' to 'Administrator'");
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE Users SET Role = 'Clerk' WHERE Role = 'DataEntry'";
                    var clerkRows = await cmd.ExecuteNonQueryAsync();
                    if (clerkRows > 0) _logger.LogInfo($"Migrated {clerkRows} user(s) from role 'DataEntry' to 'Clerk'");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Role name migration failed", ex);
            }
            finally
            {
                _connectionPool.ReturnConnection(conn);
            }
        }

        /// <summary>
        /// Adds a column to an existing table only if it isn't already
        /// there. SQLite's CREATE TABLE IF NOT EXISTS doesn't retroactively
        /// add columns to a table that already exists from an older version
        /// of the app, so new columns need this explicit, idempotent check
        /// (PRAGMA table_info) before ALTER TABLE ... ADD COLUMN.
        /// </summary>
        private async Task AddColumnIfMissingAsync(string table, string column, string columnDefinition)
        {
            var conn = await _connectionPool.GetConnectionAsync();
            try
            {
                using (var checkCmd = conn.CreateCommand())
                {
                    checkCmd.CommandText = $"PRAGMA table_info({table})";
                    using var reader = await checkCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        // Column 1 in PRAGMA table_info's result is the column name
                        if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                        {
                            return; // already present, nothing to do
                        }
                    }
                }

                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnDefinition}";
                await alterCmd.ExecuteNonQueryAsync();
                _logger.LogInfo($"Migration: added column {table}.{column}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Migration failed for {table}.{column}", ex);
            }
            finally
            {
                _connectionPool.ReturnConnection(conn);
            }
        }
    }

    #endregion

    #region Dependency Injection

    /// <summary>
    /// Extension method to register infrastructure services
    /// </summary>
    public static class InfrastructureServiceCollectionExtensions
    {
        public static void AddInfrastructure(this IServiceCollection services, IAppConfiguration config)
        {
            services.AddSingleton(config);
            services.AddSingleton<ILogger, FileLogger>();
            services.AddSingleton<IConnectionPool, SqliteConnectionPool>();
            services.AddTransient<IDatabaseInitializer, DatabaseInitializer>();
        }
    }

    #endregion
}


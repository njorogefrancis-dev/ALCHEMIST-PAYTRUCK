/*
 * LAYER 2: DATA ACCESS IMPLEMENTATION
 * Complete generic repository pattern with base class, interfaces,
 * and concrete repository implementations
 */

using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using AlchemistPayTrack.Infrastructure;

namespace AlchemistPayTrack.Data
{
    #region Generic Repository Pattern

    /// <summary>
    /// Generic repository interface
    /// </summary>
    public interface IRepository<T> where T : class
    {
        Task<IEnumerable<T>> GetAllAsync();
        Task<T?> GetByIdAsync(object id);
        Task<IEnumerable<T>> GetPagedAsync(int pageNumber, int pageSize);
        Task<int> CountAsync();
        Task InsertAsync(T entity);
        Task UpdateAsync(T entity);
        Task DeleteAsync(object id);
    }

    /// <summary>
    /// Base repository class with common functionality
    /// </summary>
    public abstract class RepositoryBase<T> : IRepository<T> where T : class
    {
        protected readonly IConnectionPool _connectionPool;
        protected readonly ILogger _logger;
        protected readonly IAppConfiguration _config;

        protected RepositoryBase(IConnectionPool connectionPool, ILogger logger, IAppConfiguration config)
        {
            _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// Execute query with proper error handling and connection management
        /// </summary>
        protected async Task<TResult> ExecuteAsync<TResult>(
            Func<SqliteConnection, Task<TResult>> operation,
            string operationName = "")
        {
            var conn = await _connectionPool.GetConnectionAsync();
            try
            {
                _logger.LogDebug($"Executing {operationName}");
                return await operation(conn);
            }
            catch (SqliteException ex)
            {
                _logger.LogError($"Database error in {operationName}", ex);
                throw new DataAccessException($"Database error in {operationName}: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Unexpected error in {operationName}", ex);
                throw new DataAccessException($"Operation failed: {ex.Message}", ex);
            }
            finally
            {
                _connectionPool.ReturnConnection(conn);
            }
        }

        /// <summary>
        /// Build WHERE clause dynamically (prevents SQL injection)
        /// </summary>
        protected string BuildWhereClause(Dictionary<string, object?> conditions)
        {
            if (conditions.Count == 0) return "";

            var clauses = conditions
                .Where(kv => kv.Value != null)
                .Select(kv => $"{kv.Key} = @{kv.Key}")
                .ToList();

            return clauses.Count > 0 ? " WHERE " + string.Join(" AND ", clauses) : "";
        }

        /// <summary>
        /// Add parameters to command
        /// </summary>
        protected void AddParameters(SqliteCommand cmd, Dictionary<string, object?> parameters)
        {
            foreach (var param in parameters.Where(p => p.Value != null))
            {
                cmd.Parameters.AddWithValue($"@{param.Key}", param.Value ?? DBNull.Value);
            }
        }

        public abstract Task<IEnumerable<T>> GetAllAsync();
        public abstract Task<T?> GetByIdAsync(object id);
        public abstract Task<IEnumerable<T>> GetPagedAsync(int pageNumber, int pageSize);
        public abstract Task<int> CountAsync();
        public abstract Task InsertAsync(T entity);
        public abstract Task UpdateAsync(T entity);
        public abstract Task DeleteAsync(object id);
    }

    #endregion

    #region Domain Entities

    /// <summary>
    /// Unified time record (replaces both Attendance and Clock)
    /// </summary>
    public class TimeRecord
    {
        public int TimeRecordId { get; set; }
        public string WorkerId { get; set; } = "";
        public DateTime Date { get; set; }
        public TimeSpan? ClockInTime { get; set; }
        public TimeSpan? ClockOutTime { get; set; }
        public decimal HoursWorked { get; set; }
        public string Status { get; set; } = "Present"; // Present, Absent, Late, HalfDay
        public string RecordType { get; set; } = "Manual"; // Manual or Clock
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Worker entity
    /// </summary>
    public class Worker
    {
        public string WorkerId { get; set; } = "";
        public string FullName { get; set; } = "";
        public string NationalId { get; set; } = "";
        public string PhoneNumber { get; set; } = "";
        public DateTime DateJoined { get; set; }
        public string? Gender { get; set; }
        public string? Address { get; set; }
        public string? EmergencyContact { get; set; }
        public string WorkerCategory { get; set; } = "Casual";
        public string JobRole { get; set; } = "General Laborer";
        public decimal DailyWageRate { get; set; }
        public string Status { get; set; } = "Active";
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Payment entity
    /// </summary>
    public class Payment
    {
        public int PaymentId { get; set; }
        public string ReceiptNumber { get; set; } = "";
        public string WorkerId { get; set; } = "";
        public string WorkerName { get; set; } = "";
        public decimal AmountPaid { get; set; }
        public DateTime PaymentDate { get; set; }
        public string PaymentMethod { get; set; } = "Cash"; // Cash or M-Pesa
        public string? MpesaCode { get; set; }
        public string RecordedBy { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// User entity
    /// </summary>
    public class User
    {
        public int UserId { get; set; }
        public string Username { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string FullName { get; set; } = "";
        public string Role { get; set; } = "DataEntry"; // Admin, Supervisor, DataEntry
        public string Status { get; set; } = "Active";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    #endregion

    #region Specialized Repositories

    /// <summary>
    /// Time Record Repository - unified time tracking
    /// </summary>
    public interface ITimeRecordRepository : IRepository<TimeRecord>
    {
        Task<TimeRecord?> GetTodayClockStatusAsync(string workerId);
        Task<IEnumerable<TimeRecord>> GetByDateAsync(DateTime date);
        Task<IEnumerable<TimeRecord>> GetByDateRangeAsync(DateTime from, DateTime to, string? workerId = null);
        Task<decimal> GetHoursWorkedAsync(string workerId, DateTime from, DateTime to);
        Task<(bool Success, string Message)> ClockInAsync(string workerId, string workerName);
        Task<(bool Success, string Message)> ClockOutAsync(string workerId);

        /// <summary>Total number of distinct days this worker has ever been
        /// recorded present (any RecordType). Used to block payments to a
        /// worker who has never actually worked a day.</summary>
        Task<int> GetTotalDaysWorkedAsync(string workerId);
    }

    public class TimeRecordRepository : RepositoryBase<TimeRecord>, ITimeRecordRepository
    {
        public TimeRecordRepository(IConnectionPool pool, ILogger logger, IAppConfiguration config)
            : base(pool, logger, config) { }

        public override async Task<IEnumerable<TimeRecord>> GetAllAsync()
        {
            return await ExecuteAsync(async conn =>
            {
                var records = new List<TimeRecord>();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT TimeRecordId, WorkerId, Date, ClockInTime, ClockOutTime,
                           HoursWorked, Status, RecordType, CreatedBy, CreatedAt
                    FROM TimeRecords ORDER BY Date DESC";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    records.Add(MapTimeRecord(reader));
                return records;
            }, nameof(GetAllAsync));
        }

        public override async Task<TimeRecord?> GetByIdAsync(object id)
        {
            return await ExecuteAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT TimeRecordId, WorkerId, Date, ClockInTime, ClockOutTime,
                           HoursWorked, Status, RecordType, CreatedBy, CreatedAt
                    FROM TimeRecords WHERE TimeRecordId = @id";
                cmd.Parameters.AddWithValue("@id", (int)id);
                using var reader = await cmd.ExecuteReaderAsync();
                return await reader.ReadAsync() ? MapTimeRecord(reader) : null;
            }, nameof(GetByIdAsync));
        }

        public override async Task<IEnumerable<TimeRecord>> GetPagedAsync(int pageNumber, int pageSize)
        {
            return await ExecuteAsync(async conn =>
            {
                var records = new List<TimeRecord>();
                var offset = (pageNumber - 1) * pageSize;
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT TimeRecordId, WorkerId, Date, ClockInTime, ClockOutTime,
                           HoursWorked, Status, RecordType, CreatedBy, CreatedAt
                    FROM TimeRecords ORDER BY Date DESC LIMIT @pageSize OFFSET @offset";
                cmd.Parameters.AddWithValue("@pageSize", pageSize);
                cmd.Parameters.AddWithValue("@offset", offset);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    records.Add(MapTimeRecord(reader));
                return records;
            }, nameof(GetPagedAsync));
        }

        public override async Task<int> CountAsync()
        {
            return await ExecuteAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM TimeRecords";
                return (int)(long)(await cmd.ExecuteScalarAsync() ?? 0);
            }, nameof(CountAsync));
        }

        public override async Task InsertAsync(TimeRecord entity)
        {
            await ExecuteAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO TimeRecords 
                    (WorkerId, Date, ClockInTime, ClockOutTime, HoursWorked,
                     Status, RecordType, CreatedBy, CreatedAt)
                    VALUES (@wid, @date, @clockIn, @clockOut, @hours,
                            @status, @type, @createdBy, @createdAt)";
                
                cmd.Parameters.AddWithValue("@wid", entity.WorkerId);
                cmd.Parameters.AddWithValue("@date", entity.Date.ToString(_config.DefaultDateFormat));
                cmd.Parameters.AddWithValue("@clockIn", entity.ClockInTime?.ToString() ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@clockOut", entity.ClockOutTime?.ToString() ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@hours", entity.HoursWorked);
                cmd.Parameters.AddWithValue("@status", entity.Status);
                cmd.Parameters.AddWithValue("@type", entity.RecordType);
                cmd.Parameters.AddWithValue("@createdBy", entity.CreatedBy);
                cmd.Parameters.AddWithValue("@createdAt", entity.CreatedAt);

                await cmd.ExecuteNonQueryAsync();
                return 0;
            }, nameof(InsertAsync));
        }

        public override async Task UpdateAsync(TimeRecord entity)
        {
            await ExecuteAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE TimeRecords 
                    SET ClockOutTime = @clockOut, HoursWorked = @hours, Status = @status
                    WHERE TimeRecordId = @id";
                cmd.Parameters.AddWithValue("@clockOut", entity.ClockOutTime?.ToString() ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@hours", entity.HoursWorked);
                cmd.Parameters.AddWithValue("@status", entity.Status);
                cmd.Parameters.AddWithValue("@id", entity.TimeRecordId);
                await cmd.ExecuteNonQueryAsync();
                return 0;
            }, nameof(UpdateAsync));
        }

        public override async Task DeleteAsync(object id)
        {
            await ExecuteAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM TimeRecords WHERE TimeRecordId = @id";
                cmd.Parameters.AddWithValue("@id", (int)id);
                await cmd.ExecuteNonQueryAsync();
                return 0;
            }, nameof(DeleteAsync));
        }

        public async Task<TimeRecord?> GetTodayClockStatusAsync(string workerId)
        {
            return await ExecuteAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT TimeRecordId, WorkerId, Date, ClockInTime, ClockOutTime,
                           HoursWorked, Status, RecordType, CreatedBy, CreatedAt
                    FROM TimeRecords
                    WHERE WorkerId = @wid AND DATE(Date) = DATE('now') AND RecordType = 'Clock'
                    ORDER BY CreatedAt DESC LIMIT 1";
                cmd.Parameters.AddWithValue("@wid", workerId);
                using var reader = await cmd.ExecuteReaderAsync();
                return await reader.ReadAsync() ? MapTimeRecord(reader) : null;
            }, nameof(GetTodayClockStatusAsync));
        }

        public async Task<IEnumerable<TimeRecord>> GetByDateAsync(DateTime date)
        {
            return await ExecuteAsync(async conn =>
            {
                var records = new List<TimeRecord>();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT TimeRecordId, WorkerId, Date, ClockInTime, ClockOutTime,
                           HoursWorked, Status, RecordType, CreatedBy, CreatedAt
                    FROM TimeRecords WHERE DATE(Date) = @date ORDER BY WorkerId";
                cmd.Parameters.AddWithValue("@date", date.ToString(_config.DefaultDateFormat));
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    records.Add(MapTimeRecord(reader));
                return records;
            }, nameof(GetByDateAsync));
        }

        public async Task<IEnumerable<TimeRecord>> GetByDateRangeAsync(DateTime from, DateTime to, string? workerId = null)
        {
            return await ExecuteAsync(async conn =>
            {
                var records = new List<TimeRecord>();
                using var cmd = conn.CreateCommand();
                var whereClause = workerId != null ? " AND WorkerId = @wid" : "";
                cmd.CommandText = $@"
                    SELECT TimeRecordId, WorkerId, Date, ClockInTime, ClockOutTime,
                           HoursWorked, Status, RecordType, CreatedBy, CreatedAt
                    FROM TimeRecords
                    WHERE Date BETWEEN @from AND @to{whereClause}
                    ORDER BY Date DESC";
                cmd.Parameters.AddWithValue("@from", from.ToString(_config.DefaultDateFormat));
                cmd.Parameters.AddWithValue("@to", to.ToString(_config.DefaultDateFormat));
                if (workerId != null) cmd.Parameters.AddWithValue("@wid", workerId);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    records.Add(MapTimeRecord(reader));
                return records;
            }, nameof(GetByDateRangeAsync));
        }

        public async Task<decimal> GetHoursWorkedAsync(string workerId, DateTime from, DateTime to)
        {
            return await ExecuteAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT COALESCE(SUM(HoursWorked), 0)
                    FROM TimeRecords
                    WHERE WorkerId = @wid AND Date BETWEEN @from AND @to";
                cmd.Parameters.AddWithValue("@wid", workerId);
                cmd.Parameters.AddWithValue("@from", from.ToString(_config.DefaultDateFormat));
                cmd.Parameters.AddWithValue("@to", to.ToString(_config.DefaultDateFormat));
                return Convert.ToDecimal(await cmd.ExecuteScalarAsync() ?? 0);
            }, nameof(GetHoursWorkedAsync));
        }

        public async Task<int> GetTotalDaysWorkedAsync(string workerId)
        {
            return await ExecuteAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT COUNT(DISTINCT Date)
                    FROM TimeRecords
                    WHERE WorkerId = @wid AND Status IN ('Present', 'Late', 'HalfDay')";
                cmd.Parameters.AddWithValue("@wid", workerId);
                return Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
            }, nameof(GetTotalDaysWorkedAsync));
        }

        public async Task<(bool Success, string Message)> ClockInAsync(string workerId, string workerName)
        {
            try
            {
                var existing = await GetTodayClockStatusAsync(workerId);
                if (existing != null) 
                    return (false, "❌ Already clocked in today");

                var record = new TimeRecord
                {
                    WorkerId = workerId,
                    Date = DateTime.Now,
                    ClockInTime = DateTime.Now.TimeOfDay,
                    Status = "Present",
                    RecordType = "Clock",
                    CreatedBy = workerId,
                    CreatedAt = DateTime.Now
                };

                await InsertAsync(record);
                return (true, $"✅ {workerName} clocked in at {DateTime.Now:HH:mm:ss}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Clock in failed for {workerId}", ex);
                return (false, "❌ Clock in failed. Please try again, and let your administrator know if this keeps happening.");
            }
        }

        public async Task<(bool Success, string Message)> ClockOutAsync(string workerId)
        {
            try
            {
                var existing = await GetTodayClockStatusAsync(workerId);
                if (existing == null) 
                    return (false, "❌ No active clock in found");

                var duration = DateTime.Now.TimeOfDay - (existing.ClockInTime ?? TimeSpan.Zero);
                existing.ClockOutTime = DateTime.Now.TimeOfDay;
                existing.HoursWorked = (decimal)duration.TotalHours;
                existing.Status = "Present";

                await UpdateAsync(existing);
                return (true, $"✅ Clocked out. Duration: {duration.Hours}h {duration.Minutes}m");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Clock out failed for {workerId}", ex);
                return (false, "❌ Clock out failed. Please try again, and let your administrator know if this keeps happening.");
            }
        }

        private TimeRecord MapTimeRecord(SqliteDataReader reader) => new()
        {
            TimeRecordId = reader.GetInt32(0),
            WorkerId = reader.GetString(1),
            Date = DateTime.Parse(reader.GetString(2)),
            ClockInTime = reader.IsDBNull(3) ? null : TimeSpan.Parse(reader.GetString(3)),
            ClockOutTime = reader.IsDBNull(4) ? null : TimeSpan.Parse(reader.GetString(4)),
            HoursWorked = (decimal)reader.GetDouble(5),
            Status = reader.GetString(6),
            RecordType = reader.GetString(7),
            CreatedBy = reader.GetString(8),
            CreatedAt = DateTime.Parse(reader.GetString(9))
        };
    }

    /// <summary>
    /// Worker Repository
    /// </summary>
    public interface IWorkerRepository : IRepository<Worker> { }

    public class WorkerRepository : RepositoryBase<Worker>, IWorkerRepository
    {
        public WorkerRepository(IConnectionPool pool, ILogger logger, IAppConfiguration config)
            : base(pool, logger, config) { }

        public override async Task<IEnumerable<Worker>> GetAllAsync()
        {
            return await ExecuteAsync(async conn =>
            {
                var workers = new List<Worker>();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT WorkerId, FullName, NationalId, PhoneNumber, DateJoined,
                           Gender, Address, EmergencyContact, WorkerCategory, JobRole,
                           DailyWageRate, Status, CreatedBy, CreatedAt
                    FROM Workers ORDER BY WorkerId";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    workers.Add(MapWorker(reader));
                return workers;
            }, nameof(GetAllAsync));
        }

        public override async Task<Worker?> GetByIdAsync(object id)
        {
            return await ExecuteAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT WorkerId, FullName, NationalId, PhoneNumber, DateJoined,
                           Gender, Address, EmergencyContact, WorkerCategory, JobRole,
                           DailyWageRate, Status, CreatedBy, CreatedAt
                    FROM Workers WHERE WorkerId = @id";
                cmd.Parameters.AddWithValue("@id", id);
                using var reader = await cmd.ExecuteReaderAsync();
                return await reader.ReadAsync() ? MapWorker(reader) : null;
            }, nameof(GetByIdAsync));
        }

        public override async Task<IEnumerable<Worker>> GetPagedAsync(int pageNumber, int pageSize)
        {
            return await ExecuteAsync(async conn =>
            {
                var workers = new List<Worker>();
                var offset = (pageNumber - 1) * pageSize;
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT WorkerId, FullName, NationalId, PhoneNumber, DateJoined,
                           Gender, Address, EmergencyContact, WorkerCategory, JobRole,
                           DailyWageRate, Status, CreatedBy, CreatedAt
                    FROM Workers ORDER BY WorkerId LIMIT @pageSize OFFSET @offset";
                cmd.Parameters.AddWithValue("@pageSize", pageSize);
                cmd.Parameters.AddWithValue("@offset", offset);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    workers.Add(MapWorker(reader));
                return workers;
            }, nameof(GetPagedAsync));
        }

        public override async Task<int> CountAsync()
        {
            return await ExecuteAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Workers";
                return (int)(long)(await cmd.ExecuteScalarAsync() ?? 0);
            }, nameof(CountAsync));
        }

        public override async Task InsertAsync(Worker entity)
        {
            await ExecuteAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO Workers
                    (WorkerId, FullName, NationalId, PhoneNumber, DateJoined,
                     Gender, Address, EmergencyContact, WorkerCategory, JobRole,
                     DailyWageRate, Status, CreatedBy, CreatedAt)
                    VALUES (@wid, @name, @nid, @phone, @joined,
                            @gender, @addr, @emergency, @category, @role,
                            @rate, @status, @createdBy, @createdAt)";
                
                cmd.Parameters.AddWithValue("@wid", entity.WorkerId);
                cmd.Parameters.AddWithValue("@name", entity.FullName);
                cmd.Parameters.AddWithValue("@nid", entity.NationalId);
                cmd.Parameters.AddWithValue("@phone", entity.PhoneNumber);
                cmd.Parameters.AddWithValue("@joined", entity.DateJoined);
                cmd.Parameters.AddWithValue("@gender", entity.Gender ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@addr", entity.Address ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@emergency", entity.EmergencyContact ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@category", entity.WorkerCategory);
                cmd.Parameters.AddWithValue("@role", entity.JobRole);
                cmd.Parameters.AddWithValue("@rate", entity.DailyWageRate);
                cmd.Parameters.AddWithValue("@status", entity.Status);
                cmd.Parameters.AddWithValue("@createdBy", entity.CreatedBy);
                cmd.Parameters.AddWithValue("@createdAt", entity.CreatedAt);

                await cmd.ExecuteNonQueryAsync();
                return 0;
            }, nameof(InsertAsync));
        }

        public override async Task UpdateAsync(Worker entity)
        {
            await ExecuteAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE Workers SET
                        FullName = @name, PhoneNumber = @phone,
                        Gender = @gender, Address = @addr,
                        EmergencyContact = @emergency, JobRole = @role,
                        DailyWageRate = @rate, Status = @status
                    WHERE WorkerId = @wid";
                
                cmd.Parameters.AddWithValue("@name", entity.FullName);
                cmd.Parameters.AddWithValue("@phone", entity.PhoneNumber);
                cmd.Parameters.AddWithValue("@gender", entity.Gender ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@addr", entity.Address ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@emergency", entity.EmergencyContact ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@role", entity.JobRole);
                cmd.Parameters.AddWithValue("@rate", entity.DailyWageRate);
                cmd.Parameters.AddWithValue("@status", entity.Status);
                cmd.Parameters.AddWithValue("@wid", entity.WorkerId);

                await cmd.ExecuteNonQueryAsync();
                return 0;
            }, nameof(UpdateAsync));
        }

        public override async Task DeleteAsync(object id)
        {
            await ExecuteAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM Workers WHERE WorkerId = @id";
                cmd.Parameters.AddWithValue("@id", id);
                await cmd.ExecuteNonQueryAsync();
                return 0;
            }, nameof(DeleteAsync));
        }

        private Worker MapWorker(SqliteDataReader reader) => new()
        {
            WorkerId = reader.GetString(0),
            FullName = reader.GetString(1),
            NationalId = reader.GetString(2),
            PhoneNumber = reader.GetString(3),
            DateJoined = DateTime.Parse(reader.GetString(4)),
            Gender = reader.IsDBNull(5) ? null : reader.GetString(5),
            Address = reader.IsDBNull(6) ? null : reader.GetString(6),
            EmergencyContact = reader.IsDBNull(7) ? null : reader.GetString(7),
            WorkerCategory = reader.GetString(8),
            JobRole = reader.GetString(9),
            DailyWageRate = (decimal)reader.GetDouble(10),
            Status = reader.GetString(11),
            CreatedBy = reader.GetString(12),
            CreatedAt = DateTime.Parse(reader.GetString(13))
        };
    }

    #endregion

    #region DI Extension

    public static class DataAccessServiceCollectionExtensions
    {
        public static void AddDataAccess(this IServiceCollection services)
        {
            services.AddTransient<ITimeRecordRepository, TimeRecordRepository>();
            services.AddTransient<IWorkerRepository, WorkerRepository>();
        }
    }

    #endregion
}


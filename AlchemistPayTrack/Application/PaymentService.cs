using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AlchemistPayTrack.Infrastructure;

namespace AlchemistPayTrack.Application
{
    public class PaymentRecord
    {
        public int PaymentId { get; set; }
        public string ReceiptNumber { get; set; } = "";
        public string WorkerId { get; set; } = "";
        public string WorkerName { get; set; } = "";
        public decimal AmountPaid { get; set; }
        public DateTime PaymentDate { get; set; }
        public string PaymentMethod { get; set; } = "";
        public string? MpesaCode { get; set; }

        /// <summary>UI-only selection flag for bulk receipt printing/export
        /// in the Payments page - not persisted, not used anywhere in the
        /// data layer.</summary>
        public bool IsSelected { get; set; }
    }

    public interface IPaymentService
    {
        Task<string> RecordPaymentAsync(string workerId, decimal amount, string method, string? mpesaCode, string recordedBy);
        Task<List<PaymentRecord>> GetRecentPaymentsAsync(int limit = 25);
        Task<List<PaymentRecord>> GetPagedAsync(int pageNumber, int pageSize);
        Task<List<PaymentRecord>> SearchAsync(string searchTerm, int limit = 100);
        Task<int> CountAsync();
    }

    /// <summary>
    /// Records payments/advances against the existing Payments table.
    /// PayrollService already reads this table (SUM(p.AmountPaid) feeds the
    /// "Total Paid" / "Balance" columns) - this was the missing write side.
    /// </summary>
    public class PaymentService : IPaymentService
    {
        private readonly IConnectionPool _connectionPool;
        private readonly ILogger _logger;
        private readonly IAuthorizationService _authorization;
        private readonly ITimeRecordService _timeRecordService;

        public PaymentService(IConnectionPool connectionPool, ILogger logger, IAuthorizationService authorization, ITimeRecordService timeRecordService)
        {
            _connectionPool = connectionPool;
            _logger = logger;
            _authorization = authorization;
            _timeRecordService = timeRecordService;
        }

        public async Task<string> RecordPaymentAsync(string workerId, decimal amount, string method, string? mpesaCode, string recordedBy)
        {
            _authorization.Require(AppPermission.RecordPayments);

            if (amount <= 0)
            {
                throw new ArgumentException("Amount must be greater than zero.", nameof(amount));
            }

            // A worker who has never actually worked a day has nothing to be
            // paid for. Checking here (not just in the UI) means this can
            // never be bypassed, whatever path a payment request comes in
            // through.
            var daysWorked = await _timeRecordService.GetTotalDaysWorkedAsync(workerId);
            if (daysWorked <= 0)
            {
                throw new ValidationException("This worker has zero recorded working days and cannot be paid. Clock them in for at least one day first.");
            }

            var conn = await _connectionPool.GetConnectionAsync();
            try
            {
                // Receipt numbers only need to be unique, not sequential, so
                // a short random suffix is fine - but `new Random()` reseeded
                // from the system clock on every call can produce the same
                // sequence if two payments land in the same clock tick,
                // colliding with the UNIQUE constraint below. Guid gives a
                // collision-proof suffix without that failure mode; a small
                // retry loop is still kept as a defensive backstop in case
                // the constraint is ever hit for some other reason (e.g. a
                // restored backup that already contains today's numbers).
                const int maxAttempts = 5;
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    var receiptNumber = "RCPT" + DateTime.Now.ToString("yyyyMMddHHmmss") + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();

                    try
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = @"
                            INSERT INTO Payments
                            (ReceiptNumber, WorkerId, AmountPaid, PaymentDate, PaymentMethod, MpesaCode, RecordedBy, CreatedAt)
                            VALUES (@receipt, @workerId, @amount, @date, @method, @mpesa, @recordedBy, @createdAt)";
                        cmd.Parameters.AddWithValue("@receipt", receiptNumber);
                        cmd.Parameters.AddWithValue("@workerId", workerId);
                        cmd.Parameters.AddWithValue("@amount", (double)amount);
                        cmd.Parameters.AddWithValue("@date", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        cmd.Parameters.AddWithValue("@method", method);
                        cmd.Parameters.AddWithValue("@mpesa", (object?)mpesaCode ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@recordedBy", recordedBy);
                        cmd.Parameters.AddWithValue("@createdAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                        await cmd.ExecuteNonQueryAsync();
                        _logger.LogInfo($"Payment recorded: {receiptNumber} - {amount:N2} to worker {workerId}");
                        return receiptNumber;
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19 && attempt < maxAttempts)
                    {
                        // SQLITE_CONSTRAINT (19) - almost certainly the
                        // ReceiptNumber uniqueness check. Try again with a
                        // fresh suffix rather than surfacing a raw DB error
                        // for something the user did nothing wrong to cause.
                        _logger.LogWarning($"Receipt number collision on attempt {attempt}, retrying");
                    }
                }

                throw new DataAccessException("Could not generate a unique receipt number after several attempts. Please try again.");
            }
            finally
            {
                _connectionPool.ReturnConnection(conn);
            }
        }

        public async Task<List<PaymentRecord>> GetRecentPaymentsAsync(int limit = 25)
        {
            var conn = await _connectionPool.GetConnectionAsync();
            try
            {
                var results = new List<PaymentRecord>();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT p.PaymentId, p.ReceiptNumber, p.WorkerId, w.FullName, p.AmountPaid, p.PaymentDate, p.PaymentMethod, p.MpesaCode
                    FROM Payments p
                    JOIN Workers w ON p.WorkerId = w.WorkerId
                    ORDER BY p.PaymentDate DESC
                    LIMIT @limit";
                cmd.Parameters.AddWithValue("@limit", limit);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    results.Add(new PaymentRecord
                    {
                        PaymentId = reader.GetInt32(0),
                        ReceiptNumber = reader.GetString(1),
                        WorkerId = reader.GetString(2),
                        WorkerName = reader.GetString(3),
                        AmountPaid = (decimal)reader.GetDouble(4),
                        PaymentDate = DateTime.Parse(reader.GetString(5)),
                        PaymentMethod = reader.GetString(6),
                        MpesaCode = reader.IsDBNull(7) ? null : reader.GetString(7)
                    });
                }

                return results;
            }
            finally
            {
                _connectionPool.ReturnConnection(conn);
            }
        }

        public async Task<List<PaymentRecord>> GetPagedAsync(int pageNumber, int pageSize)
        {
            var conn = await _connectionPool.GetConnectionAsync();
            try
            {
                var results = new List<PaymentRecord>();
                var offset = (pageNumber - 1) * pageSize;

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT p.PaymentId, p.ReceiptNumber, p.WorkerId, w.FullName, p.AmountPaid, p.PaymentDate, p.PaymentMethod, p.MpesaCode
                    FROM Payments p
                    JOIN Workers w ON p.WorkerId = w.WorkerId
                    ORDER BY p.PaymentDate DESC
                    LIMIT @limit OFFSET @offset";
                cmd.Parameters.AddWithValue("@limit", pageSize);
                cmd.Parameters.AddWithValue("@offset", offset);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    results.Add(new PaymentRecord
                    {
                        PaymentId = reader.GetInt32(0),
                        ReceiptNumber = reader.GetString(1),
                        WorkerId = reader.GetString(2),
                        WorkerName = reader.GetString(3),
                        AmountPaid = (decimal)reader.GetDouble(4),
                        PaymentDate = DateTime.Parse(reader.GetString(5)),
                        PaymentMethod = reader.GetString(6),
                        MpesaCode = reader.IsDBNull(7) ? null : reader.GetString(7)
                    });
                }

                return results;
            }
            finally
            {
                _connectionPool.ReturnConnection(conn);
            }
        }

        public async Task<List<PaymentRecord>> SearchAsync(string searchTerm, int limit = 100)
        {
            var conn = await _connectionPool.GetConnectionAsync();
            try
            {
                var results = new List<PaymentRecord>();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT p.PaymentId, p.ReceiptNumber, p.WorkerId, w.FullName, p.AmountPaid, p.PaymentDate, p.PaymentMethod, p.MpesaCode
                    FROM Payments p
                    JOIN Workers w ON p.WorkerId = w.WorkerId
                    WHERE w.FullName LIKE @term OR p.ReceiptNumber LIKE @term OR p.WorkerId LIKE @term
                    ORDER BY p.PaymentDate DESC
                    LIMIT @limit";
                cmd.Parameters.AddWithValue("@term", $"%{searchTerm}%");
                cmd.Parameters.AddWithValue("@limit", limit);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    results.Add(new PaymentRecord
                    {
                        PaymentId = reader.GetInt32(0),
                        ReceiptNumber = reader.GetString(1),
                        WorkerId = reader.GetString(2),
                        WorkerName = reader.GetString(3),
                        AmountPaid = (decimal)reader.GetDouble(4),
                        PaymentDate = DateTime.Parse(reader.GetString(5)),
                        PaymentMethod = reader.GetString(6),
                        MpesaCode = reader.IsDBNull(7) ? null : reader.GetString(7)
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
            var conn = await _connectionPool.GetConnectionAsync();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Payments";
                return Convert.ToInt32(await cmd.ExecuteScalarAsync());
            }
            finally
            {
                _connectionPool.ReturnConnection(conn);
            }
        }
    }
}

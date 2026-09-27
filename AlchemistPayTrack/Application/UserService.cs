using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AlchemistPayTrack.Infrastructure;

namespace AlchemistPayTrack.Application
{
    public class AppUser
    {
        public int UserId { get; set; }
        public string Username { get; set; } = "";
        public string FullName { get; set; } = "";
        public string Role { get; set; } = "Admin";
        public string Status { get; set; } = "Active";
    }

    public enum AuthOutcome
    {
        Success,
        InvalidCredentials,
        AccountLocked,
        AccountInactive,
        PasswordExpired
    }

    public class AuthResult
    {
        public AuthOutcome Outcome { get; set; }
        public AppUser? User { get; set; }
        public string Message { get; set; } = "";
    }

    public interface IUserService
    {
        Task<bool> HasAnyUsersAsync();
        Task<AuthResult> AuthenticateAsync(string username, string password);
        Task<int> CreateAsync(string username, string password, string fullName, string role, List<(string Question, string Answer)> securityQuestions);
        Task<List<AppUser>> GetAllAsync();
        Task DeactivateAsync(int userId);
        Task ReactivateAsync(int userId);
        Task<(bool Success, string Message)> ChangePasswordAsync(int userId, string newPassword);
        Task<List<string>> GetSecurityQuestionsAsync(string username);
        Task<(bool Success, string Message)> ResetPasswordWithSecurityAnswersAsync(string username, List<string> answers, string newPassword);
    }

    /// <summary>
    /// Authentication against the existing Users table. Passwords are hashed
    /// with BCrypt - never stored or compared in plain text. Also enforces
    /// account lockout/throttling, password expiry, password history reuse
    /// prevention, and security-question-based recovery.
    /// </summary>
    public class UserService : IUserService
    {
        private readonly IConnectionPool _connectionPool;
        private readonly ISettingsService _settingsService;
        private readonly IAuditService _auditService;
        private readonly ILogger _logger;
        private readonly IAuthorizationService _authorization;

        public UserService(IConnectionPool connectionPool, ISettingsService settingsService, IAuditService auditService, ILogger logger, IAuthorizationService authorization)
        {
            _connectionPool = connectionPool;
            _settingsService = settingsService;
            _auditService = auditService;
            _logger = logger;
            _authorization = authorization;
        }

        public async Task<bool> HasAnyUsersAsync()
        {
            var conn = await _connectionPool.GetConnectionAsync();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Users";
                var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                return count > 0;
            }
            finally
            {
                _connectionPool.ReturnConnection(conn);
            }
        }

        public async Task<AuthResult> AuthenticateAsync(string username, string password)
        {
            var settings = await _settingsService.LoadAsync();
            var conn = await _connectionPool.GetConnectionAsync();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT UserId, Username, PasswordHash, FullName, Role, Status,
                                            FailedLoginAttempts, LockedUntil, PasswordChangedAt
                                     FROM Users WHERE Username = @username";
                cmd.Parameters.AddWithValue("@username", username);

                int userId; string storedUsername, passwordHash, fullName, role, status;
                int failedAttempts; string? lockedUntilRaw; string? passwordChangedAtRaw;

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync())
                    {
                        await _auditService.LogAsync("LoginFailed", "Users", username, null, null, "Unknown username");
                        _logger.LogWarning($"Login failed: unknown username '{username}'");
                        return new AuthResult { Outcome = AuthOutcome.InvalidCredentials, Message = "Incorrect username or password." };
                    }

                    userId = reader.GetInt32(0);
                    storedUsername = reader.GetString(1);
                    passwordHash = reader.GetString(2);
                    fullName = reader.GetString(3);
                    role = reader.GetString(4);
                    status = reader.GetString(5);
                    failedAttempts = reader.GetInt32(6);
                    lockedUntilRaw = reader.IsDBNull(7) ? null : reader.GetString(7);
                    passwordChangedAtRaw = reader.IsDBNull(8) ? null : reader.GetString(8);
                }

                if (status != "Active")
                {
                    _logger.LogWarning($"Login blocked: user '{username}' is not active");
                    return new AuthResult { Outcome = AuthOutcome.AccountInactive, Message = "This account has been deactivated." };
                }

                // Lockout / throttling: while locked, reject immediately
                // without even checking the password.
                if (lockedUntilRaw != null && DateTime.TryParse(lockedUntilRaw, out var lockedUntil) && lockedUntil > DateTime.Now)
                {
                    var remaining = lockedUntil - DateTime.Now;
                    var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
                    return new AuthResult
                    {
                        Outcome = AuthOutcome.AccountLocked,
                        Message = $"Account locked due to repeated failed attempts. Try again in {minutes} minute(s)."
                    };
                }

                bool valid;
                try
                {
                    valid = BCrypt.Net.BCrypt.Verify(password, passwordHash);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Password verification failed", ex);
                    valid = false;
                }

                if (!valid)
                {
                    var newAttempts = failedAttempts + 1;
                    string? newLockedUntil = null;

                    if (newAttempts >= settings.MaxFailedLoginAttempts)
                    {
                        newLockedUntil = DateTime.Now.AddMinutes(settings.LockoutDurationMinutes).ToString("yyyy-MM-dd HH:mm:ss");
                        await _auditService.LogAsync("AccountLocked", "Users", userId.ToString(), userId, null,
                            $"Locked for {settings.LockoutDurationMinutes} min after {newAttempts} failed attempts");
                        _logger.LogWarning($"Account '{username}' locked after {newAttempts} failed attempts");
                    }
                    else
                    {
                        await _auditService.LogAsync("LoginFailed", "Users", userId.ToString(), userId, null,
                            $"Attempt {newAttempts} of {settings.MaxFailedLoginAttempts}");
                    }

                    await UpdateLoginAttemptStateAsync(userId, newAttempts, newLockedUntil);

                    if (newLockedUntil != null)
                    {
                        return new AuthResult
                        {
                            Outcome = AuthOutcome.AccountLocked,
                            Message = $"Too many failed attempts. Account locked for {settings.LockoutDurationMinutes} minute(s)."
                        };
                    }

                    return new AuthResult { Outcome = AuthOutcome.InvalidCredentials, Message = "Incorrect username or password." };
                }

                // Successful password check - reset throttling state
                await UpdateLoginAttemptStateAsync(userId, 0, null);

                var user = new AppUser { UserId = userId, Username = storedUsername, FullName = fullName, Role = role, Status = status };

                bool expired = false;
                if (DateTime.TryParse(passwordChangedAtRaw, out var changedAt))
                {
                    expired = (DateTime.Now - changedAt).TotalDays > PasswordPolicy.PasswordExpiryDays;
                }

                await _auditService.LogAsync("Login", "Users", userId.ToString(), userId);
                _logger.LogInfo($"User '{username}' logged in");

                if (expired)
                {
                    return new AuthResult
                    {
                        Outcome = AuthOutcome.PasswordExpired,
                        User = user,
                        Message = $"Your password is more than {PasswordPolicy.PasswordExpiryDays} days old and must be changed."
                    };
                }

                return new AuthResult { Outcome = AuthOutcome.Success, User = user };
            }
            finally
            {
                _connectionPool.ReturnConnection(conn);
            }
        }

        private async Task UpdateLoginAttemptStateAsync(int userId, int failedAttempts, string? lockedUntil)
        {
            var conn = await _connectionPool.GetConnectionAsync();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE Users SET FailedLoginAttempts = @attempts, LockedUntil = @lockedUntil WHERE UserId = @userId";
                cmd.Parameters.AddWithValue("@attempts", failedAttempts);
                cmd.Parameters.AddWithValue("@lockedUntil", (object?)lockedUntil ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@userId", userId);
                await cmd.ExecuteNonQueryAsync();
            }
            finally
            {
                _connectionPool.ReturnConnection(conn);
            }
        }

        public async Task<int> CreateAsync(string username, string password, string fullName, string role, List<(string Question, string Answer)> securityQuestions)
        {
            // CreateAsync serves two very different callers: the first-run
            // "set up your administrator account" screen (nobody is signed
            // in yet - there's nothing to check a permission against) and
            // the User Management page, where an already-signed-in
            // Administrator adds another account. Only enforce ManageUsers
            // once at least one account exists; before that, account
            // creation IS the bootstrap step.
            if (await HasAnyUsersAsync())
            {
                _authorization.Require(AppPermission.ManageUsers);
            }

            var policyResult = PasswordPolicy.Validate(password);
            if (!policyResult.IsValid)
            {
                throw new InvalidOperationException("Password does not meet the security policy: " + string.Join(", ", policyResult.Violations));
            }

            if (securityQuestions == null || securityQuestions.Count != 3 || securityQuestions.Any(q => string.IsNullOrWhiteSpace(q.Question) || string.IsNullOrWhiteSpace(q.Answer)))
            {
                throw new InvalidOperationException("Exactly three security questions with answers are required.");
            }

            var conn = await _connectionPool.GetConnectionAsync();
            try
            {
                var hash = BCrypt.Net.BCrypt.HashPassword(password);
                var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO Users (Username, PasswordHash, FullName, Role, Status, CreatedAt, PasswordChangedAt, FailedLoginAttempts)
                                     VALUES (@username, @hash, @fullName, @role, 'Active', @createdAt, @passwordChangedAt, 0);
                                     SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("@username", username);
                cmd.Parameters.AddWithValue("@hash", hash);
                cmd.Parameters.AddWithValue("@fullName", fullName);
                cmd.Parameters.AddWithValue("@role", role);
                cmd.Parameters.AddWithValue("@createdAt", now);
                cmd.Parameters.AddWithValue("@passwordChangedAt", now);

                var newId = Convert.ToInt32(await cmd.ExecuteScalarAsync());

                foreach (var (question, answer) in securityQuestions)
                {
                    using var qCmd = conn.CreateCommand();
                    qCmd.CommandText = "INSERT INTO SecurityQuestions (UserId, QuestionText, AnswerHash) VALUES (@userId, @question, @answerHash)";
                    qCmd.Parameters.AddWithValue("@userId", newId);
                    qCmd.Parameters.AddWithValue("@question", question);
                    qCmd.Parameters.AddWithValue("@answerHash", BCrypt.Net.BCrypt.HashPassword(NormalizeAnswer(answer)));
                    await qCmd.ExecuteNonQueryAsync();
                }

                using var histCmd = conn.CreateCommand();
                histCmd.CommandText = "INSERT INTO PasswordHistory (UserId, PasswordHash, ChangedAt) VALUES (@userId, @hash, @changedAt)";
                histCmd.Parameters.AddWithValue("@userId", newId);
                histCmd.Parameters.AddWithValue("@hash", hash);
                histCmd.Parameters.AddWithValue("@changedAt", now);
                await histCmd.ExecuteNonQueryAsync();

                _logger.LogInfo($"User account '{username}' created (UserId {newId})");
                return newId;
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
            {
                _logger.LogWarning($"User creation failed - username '{username}' already exists");
                throw new InvalidOperationException($"Username '{username}' is already taken.");
            }
            finally
            {
                _connectionPool.ReturnConnection(conn);
            }
        }

        public async Task<List<AppUser>> GetAllAsync()
        {
            var conn = await _connectionPool.GetConnectionAsync();
            try
            {
                var results = new List<AppUser>();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT UserId, Username, FullName, Role, Status FROM Users ORDER BY FullName";

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    results.Add(new AppUser
                    {
                        UserId = reader.GetInt32(0),
                        Username = reader.GetString(1),
                        FullName = reader.GetString(2),
                        Role = reader.GetString(3),
                        Status = reader.GetString(4)
                    });
                }

                return results;
            }
            finally
            {
                _connectionPool.ReturnConnection(conn);
            }
        }

        public Task DeactivateAsync(int userId) => SetStatusAsync(userId, "Inactive");

        public Task ReactivateAsync(int userId) => SetStatusAsync(userId, "Active");

        private async Task SetStatusAsync(int userId, string status)
        {
            _authorization.Require(AppPermission.ManageUsers);

            var conn = await _connectionPool.GetConnectionAsync();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE Users SET Status = @status WHERE UserId = @userId";
                cmd.Parameters.AddWithValue("@status", status);
                cmd.Parameters.AddWithValue("@userId", userId);
                await cmd.ExecuteNonQueryAsync();
                _logger.LogInfo($"User {userId} status set to {status}");
            }
            finally
            {
                _connectionPool.ReturnConnection(conn);
            }
        }

        public async Task<(bool Success, string Message)> ChangePasswordAsync(int userId, string newPassword)
        {
            var policyResult = PasswordPolicy.Validate(newPassword);
            if (!policyResult.IsValid)
            {
                return (false, "Password does not meet policy: " + string.Join(", ", policyResult.Violations));
            }

            if (await IsPasswordReusedAsync(userId, newPassword))
            {
                return (false, $"You can't reuse any of your last {PasswordPolicy.PasswordHistoryDepth} passwords.");
            }

            await ApplyNewPasswordAsync(userId, newPassword);
            await _auditService.LogAsync("PasswordChanged", "Users", userId.ToString(), userId);
            return (true, "Password changed successfully.");
        }

        private async Task<bool> IsPasswordReusedAsync(int userId, string newPassword)
        {
            var conn = await _connectionPool.GetConnectionAsync();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT PasswordHash FROM PasswordHistory WHERE UserId = @userId
                                     ORDER BY ChangedAt DESC LIMIT @depth";
                cmd.Parameters.AddWithValue("@userId", userId);
                cmd.Parameters.AddWithValue("@depth", PasswordPolicy.PasswordHistoryDepth);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var oldHash = reader.GetString(0);
                    if (BCrypt.Net.BCrypt.Verify(newPassword, oldHash))
                    {
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                _connectionPool.ReturnConnection(conn);
            }
        }

        private async Task ApplyNewPasswordAsync(int userId, string newPassword)
        {
            var conn = await _connectionPool.GetConnectionAsync();
            try
            {
                var hash = BCrypt.Net.BCrypt.HashPassword(newPassword);
                var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE Users SET PasswordHash = @hash, PasswordChangedAt = @changedAt,
                                         FailedLoginAttempts = 0, LockedUntil = NULL WHERE UserId = @userId";
                    cmd.Parameters.AddWithValue("@hash", hash);
                    cmd.Parameters.AddWithValue("@changedAt", now);
                    cmd.Parameters.AddWithValue("@userId", userId);
                    await cmd.ExecuteNonQueryAsync();
                }

                using (var histCmd = conn.CreateCommand())
                {
                    histCmd.CommandText = "INSERT INTO PasswordHistory (UserId, PasswordHash, ChangedAt) VALUES (@userId, @hash, @changedAt)";
                    histCmd.Parameters.AddWithValue("@userId", userId);
                    histCmd.Parameters.AddWithValue("@hash", hash);
                    histCmd.Parameters.AddWithValue("@changedAt", now);
                    await histCmd.ExecuteNonQueryAsync();
                }
            }
            finally
            {
                _connectionPool.ReturnConnection(conn);
            }
        }

        public async Task<List<string>> GetSecurityQuestionsAsync(string username)
        {
            var conn = await _connectionPool.GetConnectionAsync();
            try
            {
                var questions = new List<string>();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT sq.QuestionText
                                     FROM SecurityQuestions sq
                                     JOIN Users u ON sq.UserId = u.UserId
                                     WHERE u.Username = @username
                                     ORDER BY sq.QuestionId";
                cmd.Parameters.AddWithValue("@username", username);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    questions.Add(reader.GetString(0));
                }

                return questions;
            }
            finally
            {
                _connectionPool.ReturnConnection(conn);
            }
        }

        public async Task<(bool Success, string Message)> ResetPasswordWithSecurityAnswersAsync(string username, List<string> answers, string newPassword)
        {
            var conn = await _connectionPool.GetConnectionAsync();
            int userId;
            List<string> answerHashes;
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT sq.AnswerHash, u.UserId
                                     FROM SecurityQuestions sq
                                     JOIN Users u ON sq.UserId = u.UserId
                                     WHERE u.Username = @username
                                     ORDER BY sq.QuestionId";
                cmd.Parameters.AddWithValue("@username", username);

                answerHashes = new List<string>();
                userId = 0;
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    answerHashes.Add(reader.GetString(0));
                    userId = reader.GetInt32(1);
                }
            }
            finally
            {
                _connectionPool.ReturnConnection(conn);
            }

            if (userId == 0 || answerHashes.Count != 3 || answers == null || answers.Count != 3)
            {
                return (false, "Could not verify your identity.");
            }

            for (int i = 0; i < 3; i++)
            {
                if (!BCrypt.Net.BCrypt.Verify(NormalizeAnswer(answers[i]), answerHashes[i]))
                {
                    _logger.LogWarning($"Security question recovery failed for '{username}'");
                    return (false, "One or more answers were incorrect.");
                }
            }

            var policyResult = PasswordPolicy.Validate(newPassword);
            if (!policyResult.IsValid)
            {
                return (false, "Password does not meet policy: " + string.Join(", ", policyResult.Violations));
            }

            if (await IsPasswordReusedAsync(userId, newPassword))
            {
                return (false, $"You can't reuse any of your last {PasswordPolicy.PasswordHistoryDepth} passwords.");
            }

            await ApplyNewPasswordAsync(userId, newPassword);
            await _auditService.LogAsync("PasswordReset", "Users", userId.ToString(), userId, null, "Reset via security questions");
            _logger.LogInfo($"Password reset via security questions for '{username}'");
            return (true, "Password reset successfully. You can now sign in.");
        }

        private static string NormalizeAnswer(string answer) => (answer ?? "").Trim().ToLowerInvariant();
    }
}

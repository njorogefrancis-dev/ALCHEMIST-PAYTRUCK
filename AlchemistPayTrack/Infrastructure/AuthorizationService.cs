using System.Collections.Generic;
using System.Linq;

namespace AlchemistPayTrack.Infrastructure
{
    /// <summary>
    /// Fixed set of roles. Deliberately not a dynamic/database-driven role
    /// system - the spec calls for exactly these three roles with fixed
    /// permission sets, not arbitrary custom roles.
    /// </summary>
    public static class AppRoles
    {
        public const string Administrator = "Administrator";
        public const string Supervisor = "Supervisor";
        public const string Clerk = "Clerk";

        public static readonly IReadOnlyList<string> All = new[] { Administrator, Supervisor, Clerk };
    }

    public enum AppPermission
    {
        // Users & system
        ManageUsers,
        ChangeSystemSettings,
        ManageRoles,
        ViewAuditLogs,
        ManageBackups,
        RestoreBackups,

        // Workers
        RegisterWorkers,
        EditWorkers,
        DeleteWorkers,
        ViewWorkers,

        // Attendance
        ClockWorkers,
        EditAttendance,
        ApproveAttendanceCorrections,

        // Payroll
        GeneratePayroll,
        ApprovePayroll,
        ViewPayrollSummary,
        RecordPayments,
        PrintPayslips,

        // Reports
        ViewReports,
        ExportReports,

        // Projects (reserved for when project tracking is built)
        ManageProjects
    }

    /// <summary>
    /// Maps each of the three fixed roles to what it's allowed to do.
    /// Administrator has everything; Supervisor and Clerk have the specific
    /// subsets called out in the spec. Permissions with no current
    /// enforcement point (e.g. ManageProjects, ApprovePayroll - those
    /// features don't exist in the app yet) are still modeled here so the
    /// permission set is ready for when those features are built, rather
    /// than needing another migration later.
    /// </summary>
    public static class RolePermissions
    {
        private static readonly HashSet<AppPermission> AdministratorPermissions = new()
        {
            AppPermission.ManageUsers, AppPermission.ChangeSystemSettings, AppPermission.ManageRoles,
            AppPermission.ViewAuditLogs, AppPermission.ManageBackups, AppPermission.RestoreBackups,
            AppPermission.RegisterWorkers, AppPermission.EditWorkers, AppPermission.DeleteWorkers, AppPermission.ViewWorkers,
            AppPermission.ClockWorkers, AppPermission.EditAttendance, AppPermission.ApproveAttendanceCorrections,
            AppPermission.GeneratePayroll, AppPermission.ApprovePayroll, AppPermission.ViewPayrollSummary,
            AppPermission.RecordPayments, AppPermission.PrintPayslips,
            AppPermission.ViewReports, AppPermission.ExportReports, AppPermission.ManageProjects
        };

        private static readonly HashSet<AppPermission> SupervisorPermissions = new()
        {
            AppPermission.ViewWorkers,
            AppPermission.ClockWorkers, AppPermission.EditAttendance, AppPermission.ApproveAttendanceCorrections,
            AppPermission.ViewPayrollSummary,
            AppPermission.ViewReports
        };

        private static readonly HashSet<AppPermission> ClerkPermissions = new()
        {
            AppPermission.RegisterWorkers, AppPermission.EditWorkers, AppPermission.ViewWorkers,
            AppPermission.ClockWorkers,
            AppPermission.GeneratePayroll, AppPermission.RecordPayments, AppPermission.PrintPayslips,
            AppPermission.ViewReports, AppPermission.ExportReports
        };

        private static readonly Dictionary<string, HashSet<AppPermission>> ByRole = new()
        {
            [AppRoles.Administrator] = AdministratorPermissions,
            [AppRoles.Supervisor] = SupervisorPermissions,
            [AppRoles.Clerk] = ClerkPermissions
        };

        public static bool Has(string? role, AppPermission permission)
            => role != null && ByRole.TryGetValue(role, out var perms) && perms.Contains(permission);
    }

    public interface IAuthorizationService
    {
        bool HasPermission(AppPermission permission);

        /// <summary>Throws AuthorizationException if the current user lacks
        /// this permission. Services call this at the top of every
        /// sensitive method so permissions are enforced server-side, not
        /// just by hiding buttons in the UI.</summary>
        void Require(AppPermission permission);

        /// <summary>Like Require, but passes if the user has ANY one of the
        /// given permissions. Useful where the same underlying operation
        /// serves two different intents at different permission levels -
        /// e.g. payroll figures are read by a read-only summary view
        /// (ViewPayrollSummary) as well as by the actual "run payroll"
        /// action (GeneratePayroll); either is enough to read the numbers.</summary>
        void RequireAny(params AppPermission[] permissions);
    }

    /// <summary>
    /// Thin wrapper over RolePermissions + the current signed-in user, so
    /// ViewModels can ask "can I do this?" without knowing about roles
    /// directly. Registered per DI scope (transient) since it just reads
    /// from ICurrentUserContext, which is the actual source of truth.
    /// </summary>
    public class AuthorizationService : IAuthorizationService
    {
        private readonly ICurrentUserContext _currentUser;

        public AuthorizationService(ICurrentUserContext currentUser)
        {
            _currentUser = currentUser;
        }

        public bool HasPermission(AppPermission permission) => RolePermissions.Has(_currentUser.Role, permission);

        public void Require(AppPermission permission)
        {
            if (!HasPermission(permission))
            {
                var role = _currentUser.Role ?? "no role (not signed in)";
                throw new AuthorizationException($"You don't have permission to do this ({permission}). Current role: {role}.");
            }
        }

        public void RequireAny(params AppPermission[] permissions)
        {
            if (permissions.Length == 0 || permissions.Any(HasPermission)) return;

            var role = _currentUser.Role ?? "no role (not signed in)";
            throw new AuthorizationException($"You don't have permission to do this (requires one of: {string.Join(", ", permissions)}). Current role: {role}.");
        }
    }
}

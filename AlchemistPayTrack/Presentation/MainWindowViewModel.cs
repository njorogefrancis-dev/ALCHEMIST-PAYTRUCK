using System.Windows.Input;
using AlchemistPayTrack.Infrastructure;

namespace AlchemistPayTrack.Presentation
{
    /// <summary>
    /// Hosts every page ViewModel and drives sidebar navigation. MainWindow's
    /// ContentControl binds to CurrentView; an implicit DataTemplate per
    /// ViewModel type (declared in MainWindow.xaml) picks the matching view.
    /// </summary>
    public class MainWindowViewModel : ViewModelBase
    {
        private readonly ILogger _logger;
        private readonly ICurrentUserContext _currentUser;
        private readonly IAuthorizationService _authorization;
        private object _currentView;

        public DashboardViewModel Dashboard { get; }
        public ClockViewModel Clock { get; }
        public PayrollViewModel Payroll { get; }
        public PaymentsViewModel Payments { get; }
        public WorkersViewModel Workers { get; }
        public AuditLogViewModel ActivityLog { get; }
        public UserManagementViewModel UserManagement { get; }
        public SettingsViewModel Settings { get; }
        public DevelopersViewModel Developers { get; }

        public string LoggedInUserDisplay =>
            _currentUser.IsAuthenticated ? $"{_currentUser.FullName} ({_currentUser.Role})" : "";

        // Permission-gated sidebar visibility. These two pages have no
        // partial-access story in the spec (unlike Settings, which still
        // needs to expose "change my own password" to everyone) so they're
        // hidden outright for roles that lack the permission, rather than
        // just having individual controls disabled inside.
        public bool CanManageUsers => _authorization.HasPermission(AppPermission.ManageUsers);
        public bool CanViewAuditLogs => _authorization.HasPermission(AppPermission.ViewAuditLogs);
        public bool CanGeneratePayroll => _authorization.HasPermission(AppPermission.GeneratePayroll);
        public bool CanExportReports => _authorization.HasPermission(AppPermission.ExportReports);
        public bool CanRecordPayments => _authorization.HasPermission(AppPermission.RecordPayments);

        /// <summary>Whether the Payroll tab has anything at all to offer this
        /// role - either running payroll or just viewing the summary.
        /// A role with neither is not shown the tab.</summary>
        public bool CanUsePayroll => CanGeneratePayroll || _authorization.HasPermission(AppPermission.ViewPayrollSummary);

        /// <summary>Payments has no read-only story in the permission set -
        /// a role without RecordPayments has nothing to do here, so the tab
        /// is hidden outright rather than shown with a disabled form.</summary>
        public bool CanUsePayments => CanRecordPayments;

        public object CurrentView
        {
            get => _currentView;
            set => SetProperty(ref _currentView, value, nameof(CurrentView));
        }

        private bool _isDashboardActive;
        private bool _isClockActive;
        private bool _isPayrollActive;
        private bool _isPaymentsActive;
        private bool _isWorkersActive;
        private bool _isActivityLogActive;
        private bool _isUserManagementActive;
        private bool _isSettingsActive;
        private bool _isDevelopersActive;

        public bool IsDashboardActive
        {
            get => _isDashboardActive;
            set => SetProperty(ref _isDashboardActive, value, nameof(IsDashboardActive));
        }

        public bool IsClockActive
        {
            get => _isClockActive;
            set => SetProperty(ref _isClockActive, value, nameof(IsClockActive));
        }

        public bool IsPayrollActive
        {
            get => _isPayrollActive;
            set => SetProperty(ref _isPayrollActive, value, nameof(IsPayrollActive));
        }

        public bool IsPaymentsActive
        {
            get => _isPaymentsActive;
            set => SetProperty(ref _isPaymentsActive, value, nameof(IsPaymentsActive));
        }

        public bool IsWorkersActive
        {
            get => _isWorkersActive;
            set => SetProperty(ref _isWorkersActive, value, nameof(IsWorkersActive));
        }

        public bool IsActivityLogActive
        {
            get => _isActivityLogActive;
            set => SetProperty(ref _isActivityLogActive, value, nameof(IsActivityLogActive));
        }

        public bool IsUserManagementActive
        {
            get => _isUserManagementActive;
            set => SetProperty(ref _isUserManagementActive, value, nameof(IsUserManagementActive));
        }

        public bool IsSettingsActive
        {
            get => _isSettingsActive;
            set => SetProperty(ref _isSettingsActive, value, nameof(IsSettingsActive));
        }

        public bool IsDevelopersActive
        {
            get => _isDevelopersActive;
            set => SetProperty(ref _isDevelopersActive, value, nameof(IsDevelopersActive));
        }

        public ICommand NavigateDashboardCommand { get; }
        public ICommand NavigateClockCommand { get; }
        public ICommand NavigatePayrollCommand { get; }
        public ICommand NavigatePaymentsCommand { get; }
        public ICommand NavigateWorkersCommand { get; }
        public ICommand NavigateActivityLogCommand { get; }
        public ICommand NavigateUserManagementCommand { get; }
        public ICommand NavigateSettingsCommand { get; }
        public ICommand NavigateDevelopersCommand { get; }

        public MainWindowViewModel(
            ILogger logger,
            ICurrentUserContext currentUser,
            IAuthorizationService authorization,
            DashboardViewModel dashboardViewModel,
            ClockViewModel clockViewModel,
            PayrollViewModel payrollViewModel,
            PaymentsViewModel paymentsViewModel,
            WorkersViewModel workersViewModel,
            AuditLogViewModel activityLogViewModel,
            UserManagementViewModel userManagementViewModel,
            SettingsViewModel settingsViewModel,
            DevelopersViewModel developersViewModel)
        {
            _logger = logger;
            _currentUser = currentUser;
            _authorization = authorization;
            Dashboard = dashboardViewModel;
            Clock = clockViewModel;
            Payroll = payrollViewModel;
            Payments = paymentsViewModel;
            Workers = workersViewModel;
            ActivityLog = activityLogViewModel;
            UserManagement = userManagementViewModel;
            Settings = settingsViewModel;
            Developers = developersViewModel;

            NavigateDashboardCommand = new RelayCommand(_ => NavigateToDashboard());
            NavigateClockCommand = new RelayCommand(_ => NavigateToClock());
            NavigatePayrollCommand = new RelayCommand(_ => NavigateToPayroll());
            NavigatePaymentsCommand = new RelayCommand(_ => NavigateToPayments());
            NavigateWorkersCommand = new RelayCommand(_ => NavigateToWorkers());
            NavigateActivityLogCommand = new RelayCommand(_ => NavigateToActivityLog());
            NavigateUserManagementCommand = new RelayCommand(_ => NavigateToUserManagement());
            NavigateSettingsCommand = new RelayCommand(_ => NavigateToSettings());
            NavigateDevelopersCommand = new RelayCommand(_ => NavigateToDevelopers());

            _logger.LogInfo($"Application started (user: {_currentUser.Username ?? "unknown"})");

            // Start on the Dashboard
            _currentView = Dashboard;
            IsDashboardActive = true;

            // Fire-and-forget initial data loads
            _ = Dashboard.InitializeAsync();
            _ = Clock.InitializeAsync();
            _ = Workers.InitializeAsync();
            _ = Settings.InitializeAsync();
        }

        private void ClearActiveFlags()
        {
            IsDashboardActive = false;
            IsClockActive = false;
            IsPayrollActive = false;
            IsPaymentsActive = false;
            IsWorkersActive = false;
            IsActivityLogActive = false;
            IsUserManagementActive = false;
            IsSettingsActive = false;
            IsDevelopersActive = false;
        }

        private void NavigateToDashboard()
        {
            ClearActiveFlags();
            IsDashboardActive = true;
            CurrentView = Dashboard;
            _ = Dashboard.InitializeAsync();
        }

        private void NavigateToClock()
        {
            ClearActiveFlags();
            IsClockActive = true;
            CurrentView = Clock;
        }

        private void NavigateToPayroll()
        {
            if (!CanUsePayroll) return;

            ClearActiveFlags();
            IsPayrollActive = true;
            CurrentView = Payroll;
        }

        private void NavigateToPayments()
        {
            if (!CanUsePayments) return;

            ClearActiveFlags();
            IsPaymentsActive = true;
            CurrentView = Payments;
            _ = Payments.InitializeAsync();
        }

        private void NavigateToWorkers()
        {
            ClearActiveFlags();
            IsWorkersActive = true;
            CurrentView = Workers;
        }

        private void NavigateToActivityLog()
        {
            if (!CanViewAuditLogs) return;

            ClearActiveFlags();
            IsActivityLogActive = true;
            CurrentView = ActivityLog;
            _ = ActivityLog.InitializeAsync();
        }

        private void NavigateToUserManagement()
        {
            if (!CanManageUsers) return;

            ClearActiveFlags();
            IsUserManagementActive = true;
            CurrentView = UserManagement;
            _ = UserManagement.InitializeAsync();
        }

        private void NavigateToSettings()
        {
            ClearActiveFlags();
            IsSettingsActive = true;
            CurrentView = Settings;
        }

        private void NavigateToDevelopers()
        {
            ClearActiveFlags();
            IsDevelopersActive = true;
            CurrentView = Developers;
        }
    }
}

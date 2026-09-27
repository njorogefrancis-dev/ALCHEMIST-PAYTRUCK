/*
 * APP.XAML.CS - COMPLETE DEPENDENCY INJECTION SETUP
 * Configures all services and initializes the application
 */

using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using QuestPDF.Infrastructure;
using AlchemistPayTrack.Application;
using AlchemistPayTrack.Data;
using AlchemistPayTrack.Infrastructure;
using AlchemistPayTrack.Presentation;
using ILogger = AlchemistPayTrack.Infrastructure.ILogger;

namespace AlchemistPayTrack
{
    public interface IAppLifecycleService
    {
        void RequestLogout();

        /// <summary>Fully relaunches the application as a new process, then
        /// shuts the current one down. Used after a database restore, where
        /// a same-process logout wouldn't be enough - every ViewModel,
        /// cached setting, and the logged-in user's own record could all
        /// point at data that no longer matches what's now on disk. A full
        /// process restart is the only way to guarantee nothing stale is
        /// left in memory.</summary>
        void RequestRestart();
    }

    public partial class App : System.Windows.Application, IAppLifecycleService
    {
        private IServiceProvider? _serviceProvider;
        private SessionActivityMonitor? _sessionMonitor;

        /// <summary>
        /// Application startup - configure all services
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // WPF's default ShutdownMode is OnLastWindowClose. Since we show
            // LoginWindow (via ShowDialog) before MainWindow exists, closing
            // LoginWindow after a successful login would otherwise be seen
            // as "the last window closed" and silently shut the app down
            // before MainWindow ever gets created. Explicit mode avoids that;
            // it's switched back below once MainWindow is actually showing.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Global safety nets: log unhandled exceptions instead of letting
            // the app silently die, and where possible keep it running.
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            try
            {
                // QuestPDF requires an explicit license declaration since v2023;
                // Community is free for small businesses/individuals - see
                // https://www.questpdf.com/license/ for the exact terms.
                QuestPDF.Settings.License = LicenseType.Community;

                // Build service container
                var services = new ServiceCollection();
                ConfigureServices(services);
                _serviceProvider = services.BuildServiceProvider();

                // Initialize database
                InitializeDatabase();

                // Best-effort backup on every startup. Failures inside
                // BackupService are caught and logged - they never bubble up
                // and never block the app from opening.
                // Wrapped in Task.Run: some of these calls use real async I/O
                // (e.g. File.ReadAllTextAsync in JsonSettingsService) whose
                // continuation can try to resume on the UI thread's
                // dispatcher. Blocking the UI thread directly on that with
                // .GetAwaiter().GetResult() can deadlock forever - no window,
                // no error, the app just never appears. Running the awaited
                // work on a background thread via Task.Run sidesteps that
                // entirely, since the continuation no longer needs the UI
                // thread to proceed.
                var backupService = _serviceProvider.GetRequiredService<IBackupService>();
                Task.Run(() => backupService.BackupNowAsync()).GetAwaiter().GetResult();

                // Apply the saved color theme before any window is shown, so
                // there's no flash of the wrong theme.
                var settingsService = _serviceProvider.GetRequiredService<ISettingsService>();
                var savedSettings = Task.Run(() => settingsService.LoadAsync()).GetAwaiter().GetResult();
                ThemeManager.ApplyTheme(savedSettings.IsDarkMode);

                // Require sign-in before showing the main window. First run
                // (no users yet) shows the same window in "create admin
                // account" mode instead of "sign in".
                if (!ShowLoginGate())
                {
                    Shutdown(0);
                    return;
                }

                // Show main window
                MainWindow = new MainWindow();
                MainWindow.DataContext = _serviceProvider.GetRequiredService<MainWindowViewModel>();
                MainWindow.Show();

                // Now that the real main window exists, restore normal
                // behavior: closing it (or the last remaining window) exits
                // the app as expected.
                ShutdownMode = ShutdownMode.OnMainWindowClose;

                StartSessionMonitor();
            }
            catch (Exception ex)
            {
                LogFatal("Startup failed", ex);

                var logsPath = _serviceProvider?.GetService<IAppConfiguration>()?.LogsPath;
                var logsHint = string.IsNullOrEmpty(logsPath)
                    ? ""
                    : $"\n\nA detailed error log has been saved to:\n{logsPath}";

                MessageBox.Show(
                    $"Alchemist PayTrack could not start.{logsHint}\n\nIf this keeps happening, please share the log file with support.",
                    "Startup Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        /// <summary>
        /// Starts idle-activity tracking on the main window. After the
        /// configured number of idle minutes with no mouse/keyboard input,
        /// the session is locked: the main window hides and the login
        /// screen reappears, requiring a valid sign-in before it's shown
        /// again. In-progress data isn't lost - this locks the existing
        /// MainWindow/MainWindowViewModel rather than tearing it down.
        /// </summary>
        private void StartSessionMonitor()
        {
            var settingsService = _serviceProvider!.GetRequiredService<ISettingsService>();

            _sessionMonitor = new SessionActivityMonitor(() =>
            {
                var settings = Task.Run(() => settingsService.LoadAsync()).GetAwaiter().GetResult();
                return settings.SessionIdleTimeoutMinutes;
            });

            MainWindow.PreviewMouseDown += (_, _) => _sessionMonitor.RecordActivity();
            MainWindow.PreviewMouseMove += (_, _) => _sessionMonitor.RecordActivity();
            MainWindow.PreviewKeyDown += (_, _) => _sessionMonitor.RecordActivity();

            _sessionMonitor.IdleTimeoutReached += OnSessionIdleTimeout;
            _sessionMonitor.Start();
        }

        private void OnSessionIdleTimeout() => LockSession("SessionTimeout");

        public void RequestLogout() => LockSession("Logout");

        public void RequestRestart()
        {
            try
            {
                _serviceProvider?.GetService<ILogger>()?.LogInfo("Application restart requested (e.g. after a database restore).");

                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = true
                    });
                }

                Shutdown(0);
            }
            catch (Exception ex)
            {
                LogFatal("Failed to restart the application", ex);
                MessageBox.Show(
                    "Could not restart automatically. Please close and reopen the application manually.",
                    "Restart Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LockSession(string auditAction)
        {
            try
            {
                var currentUser = _serviceProvider!.GetRequiredService<ICurrentUserContext>();
                var auditService = _serviceProvider!.GetRequiredService<IAuditService>();
                Task.Run(() => auditService.LogAsync(auditAction, "Users", currentUser.UserId?.ToString() ?? "", currentUser.UserId))
                    .GetAwaiter().GetResult();

                MainWindow.Hide();

                if (!ShowLoginGate())
                {
                    Shutdown(0);
                    return;
                }

                MainWindow.Show();
                _sessionMonitor?.Start();
            }
            catch (Exception ex)
            {
                LogFatal($"Failed to handle {auditAction}", ex);
                Shutdown(1);
            }
        }

        /// <summary>
        /// Shows the login/setup window. Returns true if the user signed in
        /// (or created the admin account) successfully, false if they closed
        /// the window instead.
        /// </summary>
        private bool ShowLoginGate()
        {
            var userService = _serviceProvider!.GetRequiredService<IUserService>();
            var hasUsers = Task.Run(() => userService.HasAnyUsersAsync()).GetAwaiter().GetResult();

            var loginViewModel = _serviceProvider.GetRequiredService<LoginViewModel>();
            loginViewModel.IsSetupMode = !hasUsers;

            var loginWindow = new LoginWindow(loginViewModel);
            var result = loginWindow.ShowDialog();
            return result == true;
        }

        /// <summary>
        /// Configure all dependency injection services
        /// </summary>
        private void ConfigureServices(IServiceCollection services)
        {
            // Configuration
            var config = new AppConfiguration();
            services.AddSingleton<IAppConfiguration>(config);

            // Infrastructure
            services.AddSingleton<ILogger, FileLogger>();
            services.AddSingleton<IConnectionPool, SqliteConnectionPool>();
            services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
            services.AddSingleton<ISettingsService, JsonSettingsService>();
            services.AddSingleton<IBackupService, BackupService>();
            services.AddSingleton<ICurrentUserContext, CurrentUserContext>();
            services.AddSingleton<IAppLifecycleService>(this);

            // Data Access
            services.AddTransient<ITimeRecordRepository, TimeRecordRepository>();
            services.AddTransient<IWorkerRepository, WorkerRepository>();
            // Add other repositories as needed...

            // Application Services
            services.AddTransient<IPayrollService, PayrollService>();
            services.AddTransient<ITimeRecordService, TimeRecordService>();
            services.AddTransient<IWorkerService, WorkerService>();
            services.AddTransient<IUserService, UserService>();
            services.AddTransient<IPaymentService, PaymentService>();
            services.AddTransient<IAuditService, AuditService>();
            services.AddTransient<IAuthorizationService, AuthorizationService>();

            // ViewModels
            services.AddTransient<ClockViewModel>();
            services.AddTransient<PayrollViewModel>();
            services.AddTransient<PaymentsViewModel>();
            services.AddTransient<WorkersViewModel>();
            services.AddTransient<AuditLogViewModel>();
            services.AddTransient<UserManagementViewModel>();
            services.AddTransient<DashboardViewModel>();
            services.AddTransient<SettingsViewModel>();
            services.AddTransient<DevelopersViewModel>();
            services.AddTransient<LoginViewModel>();
            services.AddTransient<MainWindowViewModel>();
        }

        /// <summary>
        /// Initialize database on startup
        /// </summary>
        private void InitializeDatabase()
        {
            var initializer = _serviceProvider!.GetRequiredService<IDatabaseInitializer>();
            Task.Run(() => initializer.InitializeAsync()).GetAwaiter().GetResult();
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            LogFatal("Unhandled AppDomain exception", e.ExceptionObject as Exception);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogFatal("Unhandled UI-thread exception", e.Exception);

            MessageBox.Show(
                $"Something went wrong: {UserMessage.For(e.Exception)}\n\nThe application will try to keep running, but if this keeps happening please restart it.",
                "Unexpected Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            // Prevent the exception from crashing the app when it's safe to
            // continue (most UI-thread exceptions from binding/command
            // failures are recoverable).
            e.Handled = true;
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LogFatal("Unobserved background task exception", e.Exception);
            e.SetObserved();
        }

        private void LogFatal(string message, Exception? ex)
        {
            try
            {
                var logger = _serviceProvider?.GetService<ILogger>();
                logger?.LogError(message, ex);
            }
            catch
            {
                // Logging itself failed - nothing more we can safely do here.
            }
        }

        /// <summary>
        /// Cleanup on shutdown
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            // Dispose connection pool
            var connectionPool = _serviceProvider?.GetService<IConnectionPool>();
            connectionPool?.Dispose();

            base.OnExit(e);
        }

        /// <summary>
        /// Get service from container
        /// </summary>
        public T GetService<T>() where T : notnull
        {
            return _serviceProvider!.GetRequiredService<T>();
        }
    }

    /// <summary>
    /// Multi-purpose visibility converter used across the app:
    ///  - bool (e.g. IsLoading) -> Visible when true
    ///  - bool + ConverterParameter="Invert" -> Visible when false
    ///  - string (e.g. a status message) -> Visible when non-empty
    ///  - int (e.g. a collection Count) -> Visible when greater than zero,
    ///    or ConverterParameter="ZeroToVisible" -> Visible when exactly zero
    ///    (used for empty-state placeholders)
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
            var zeroToVisible = string.Equals(parameter as string, "ZeroToVisible", StringComparison.OrdinalIgnoreCase);

            bool isVisible = value switch
            {
                bool b => invert ? !b : b,
                string s => !string.IsNullOrEmpty(s),
                int count => zeroToVisible ? count == 0 : count > 0,
                null => false,
                _ => true
            };

            return isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Password strength (0-4, matching PasswordStrength) -> a fill width in
    /// pixels for the strength meter bar (out of a 160px-wide track).
    /// </summary>
    public class PasswordStrengthToWidthConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var strength = value is int i ? i : 0;
            return Math.Max(8, strength * 40);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Password strength (0-4) -> a red/orange/green brush for the meter.</summary>
    public class PasswordStrengthToBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var strength = value is int i ? i : 0;
            var (r, g, b) = strength switch
            {
                <= 1 => (220, 38, 38),   // red - very weak/weak
                2 => (217, 119, 6),      // amber - fair
                3 => (202, 138, 4),      // yellow-green - strong
                _ => (22, 163, 74)       // green - very strong
            };
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)r, (byte)g, (byte)b));
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Password strength (0-4) -> a human-readable label.</summary>
    public class PasswordStrengthToLabelConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var strength = value is int i ? i : 0;
            return strength switch
            {
                0 => "Very weak",
                1 => "Weak",
                2 => "Fair",
                3 => "Strong",
                _ => "Very strong"
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

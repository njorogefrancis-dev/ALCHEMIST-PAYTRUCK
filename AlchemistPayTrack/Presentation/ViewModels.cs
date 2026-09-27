/*
 * LAYER 4: VIEW MODELS & APP STARTUP
 * MVVM implementation with proper async/await, INotifyPropertyChanged,
 * and complete DI setup
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ClosedXML.Excel;
using Microsoft.Win32;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using AlchemistPayTrack.Application;
using AlchemistPayTrack.Data;
using AlchemistPayTrack.Infrastructure;

namespace AlchemistPayTrack.Presentation
{
    #region Base ViewModel

    /// <summary>
    /// Base view model with INotifyPropertyChanged implementation
    /// </summary>
    public class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected void SetProperty<T>(ref T field, T value, string propertyName)
        {
            if (!EqualityComparer<T>.Default.Equals(field, value))
            {
                field = value;
                OnPropertyChanged(propertyName);
            }
        }
    }

    #endregion

    #region Relay Command

    /// <summary>
    /// Generic relay command for MVVM
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public event EventHandler? CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => _execute(parameter);
    }

    /// <summary>
    /// Async relay command
    /// </summary>
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool>? _canExecute;
        private bool _isExecuting;

        public event EventHandler? CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;

            _isExecuting = true;
            CommandManager.InvalidateRequerySuggested();

            try
            {
                await _execute();
            }
            finally
            {
                _isExecuting = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>
    /// Async relay command that passes the WPF CommandParameter through to
    /// the handler - needed for per-row actions in a list (e.g. "View
    /// Receipt" on a specific payment), where the plain AsyncRelayCommand's
    /// parameterless Func&lt;Task&gt; has nothing to bind the row to.
    /// </summary>
    public class AsyncRelayCommand<T> : ICommand
    {
        private readonly Func<T?, Task> _execute;
        private readonly Predicate<T?>? _canExecute;
        private bool _isExecuting;

        public event EventHandler? CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public AsyncRelayCommand(Func<T?, Task> execute, Predicate<T?>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        private static T? Cast(object? parameter) => parameter is T t ? t : default;

        public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke(Cast(parameter)) ?? true);

        public async void Execute(object? parameter)
        {
            var value = Cast(parameter);
            if (!CanExecute(parameter)) return;

            _isExecuting = true;
            CommandManager.InvalidateRequerySuggested();

            try
            {
                await _execute(value);
            }
            finally
            {
                _isExecuting = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    #endregion

    #region Clock ViewModel

    /// <summary>
    /// ViewModel for Clock In/Out functionality
    /// </summary>
    public class ClockViewModel : ViewModelBase
    {
        private readonly ITimeRecordService _timeRecordService;
        private readonly IWorkerService _workerService;
        private readonly ILogger _logger;
        private readonly IAuthorizationService _authorization;
        private readonly ISettingsService _settingsService;

        private ObservableCollection<Worker> _workers = new();
        private ObservableCollection<Worker> _filteredWorkers = new();
        private Worker? _selectedWorker;
        private TimeRecord? _todayStatus;
        private bool _isLoading;
        private string _statusMessage = "";
        private decimal _hoursWorkedThisMonth;
        private string _searchTerm = "";

        private List<Worker> _presentWorkersAll = new();
        private List<Worker> _absentWorkersAll = new();
        private ObservableCollection<Worker> _presentWorkers = new();
        private ObservableCollection<Worker> _absentWorkers = new();
        private string _attendanceSearchTerm = "";

        public ObservableCollection<Worker> Workers
        {
            get => _workers;
            set
            {
                SetProperty(ref _workers, value, nameof(Workers));
                ApplyFilter();
            }
        }

        public ObservableCollection<Worker> FilteredWorkers
        {
            get => _filteredWorkers;
            set => SetProperty(ref _filteredWorkers, value, nameof(FilteredWorkers));
        }

        public string SearchTerm
        {
            get => _searchTerm;
            set
            {
                SetProperty(ref _searchTerm, value, nameof(SearchTerm));
                ApplyFilter();
            }
        }

        private void ApplyFilter()
        {
            if (string.IsNullOrWhiteSpace(SearchTerm))
            {
                FilteredWorkers = new ObservableCollection<Worker>(Workers);
                return;
            }

            var term = SearchTerm.Trim();
            var matches = Workers.Where(w =>
                w.FullName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                w.PhoneNumber.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                w.NationalId.Contains(term, StringComparison.OrdinalIgnoreCase));
            FilteredWorkers = new ObservableCollection<Worker>(matches);
        }

        public Worker? SelectedWorker
        {
            get => _selectedWorker;
            set
            {
                SetProperty(ref _selectedWorker, value, nameof(SelectedWorker));
                _ = RefreshSelectedWorkerStatusAsync();
            }
        }

        private async Task RefreshSelectedWorkerStatusAsync()
        {
            if (SelectedWorker == null)
            {
                TodayStatus = null;
                HoursWorkedThisMonth = 0;
                return;
            }

            try
            {
                TodayStatus = await _timeRecordService.GetTodayStatusAsync(SelectedWorker.WorkerId);
                HoursWorkedThisMonth = await _timeRecordService.GetHoursWorkedThisMonthAsync(SelectedWorker.WorkerId);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to refresh worker status", ex);
            }
        }

        public TimeRecord? TodayStatus
        {
            get => _todayStatus;
            set => SetProperty(ref _todayStatus, value, nameof(TodayStatus));
        }

        public ObservableCollection<Worker> PresentWorkers
        {
            get => _presentWorkers;
            private set => SetProperty(ref _presentWorkers, value, nameof(PresentWorkers));
        }

        public ObservableCollection<Worker> AbsentWorkers
        {
            get => _absentWorkers;
            private set => SetProperty(ref _absentWorkers, value, nameof(AbsentWorkers));
        }

        public string AttendanceSearchTerm
        {
            get => _attendanceSearchTerm;
            set
            {
                SetProperty(ref _attendanceSearchTerm, value, nameof(AttendanceSearchTerm));
                ApplyAttendanceFilter();
            }
        }

        public int PresentCount => _presentWorkersAll.Count;
        public int AbsentCount => _absentWorkersAll.Count;

        private void ApplyAttendanceFilter()
        {
            IEnumerable<Worker> FilterList(List<Worker> source)
            {
                if (string.IsNullOrWhiteSpace(AttendanceSearchTerm)) return source;
                var term = AttendanceSearchTerm.Trim();
                return source.Where(w =>
                    w.FullName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    w.PhoneNumber.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    w.NationalId.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    w.JobRole.Contains(term, StringComparison.OrdinalIgnoreCase));
            }

            PresentWorkers = new ObservableCollection<Worker>(FilterList(_presentWorkersAll));
            AbsentWorkers = new ObservableCollection<Worker>(FilterList(_absentWorkersAll));
        }

        private async Task RefreshAttendanceListsAsync()
        {
            try
            {
                var todayRecords = (await _timeRecordService.GetTodayRecordsAsync()).ToList();
                var presentIds = todayRecords
                    .Where(r => r.Status == "Present" || r.Status == "Late" || r.Status == "HalfDay")
                    .Select(r => r.WorkerId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var activeWorkers = Workers.Where(w => w.Status == "Active").ToList();

                _presentWorkersAll = activeWorkers.Where(w => presentIds.Contains(w.WorkerId))
                    .OrderBy(w => w.FullName).ToList();
                _absentWorkersAll = activeWorkers.Where(w => !presentIds.Contains(w.WorkerId))
                    .OrderBy(w => w.FullName).ToList();

                ApplyAttendanceFilter();
                OnPropertyChanged(nameof(PresentCount));
                OnPropertyChanged(nameof(AbsentCount));
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to refresh attendance lists", ex);
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value, nameof(IsLoading));
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value, nameof(StatusMessage));
        }

        public decimal HoursWorkedThisMonth
        {
            get => _hoursWorkedThisMonth;
            set => SetProperty(ref _hoursWorkedThisMonth, value, nameof(HoursWorkedThisMonth));
        }

        /// <summary>Hides the clock in/out controls entirely for a role
        /// that isn't allowed to clock workers, rather than leaving them
        /// visible and failing only when clicked.</summary>
        public bool CanClockWorkers => _authorization.HasPermission(AppPermission.ClockWorkers);

        public ICommand ClockInCommand { get; }
        public ICommand ClockOutCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand PrintAttendanceCommand { get; }
        public ICommand ExportAttendanceCommand { get; }

        // Whoever can see the Clock page's attendance lists at all (every
        // role has ClockWorkers) should be able to print them - this isn't
        // a financial report, it's the same data already on screen.
        // Supervisor in particular is the role most likely to want a
        // printed attendance sheet day-to-day, and doesn't hold
        // ExportReports, so gating on that would have shut out exactly the
        // role this feature is for.
        public bool CanPrintAttendance => _authorization.HasPermission(AppPermission.ClockWorkers);

        public ClockViewModel(
            ITimeRecordService timeRecordService,
            IWorkerService workerService,
            ILogger logger,
            IAuthorizationService authorization,
            ISettingsService settingsService)
        {
            _timeRecordService = timeRecordService;
            _workerService = workerService;
            _logger = logger;
            _authorization = authorization;
            _settingsService = settingsService;

            ClockInCommand = new AsyncRelayCommand(ClockInExecuteAsync, () => CanClockWorkers && SelectedWorker != null && TodayStatus == null);
            ClockOutCommand = new AsyncRelayCommand(ClockOutExecuteAsync, () => CanClockWorkers && SelectedWorker != null && TodayStatus != null);
            RefreshCommand = new AsyncRelayCommand(LoadDataAsync);
            PrintAttendanceCommand = new AsyncRelayCommand(PrintAttendanceAsync, () => CanPrintAttendance);
            ExportAttendanceCommand = new AsyncRelayCommand(ExportAttendanceAsync, () => CanPrintAttendance);
        }

        private async Task<string> BuildAttendancePdfAsync()
        {
            var settings = await _settingsService.LoadAsync();
            var tempPath = Path.Combine(Path.GetTempPath(), $"AlchemistPayTrack_Attendance_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
            AttendancePdfBuilder.Generate(_presentWorkersAll, _absentWorkersAll, settings, tempPath);
            return tempPath;
        }

        private async Task PrintAttendanceAsync()
        {
            try
            {
                var path = await BuildAttendancePdfAsync();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                    Verb = "print"
                });
                StatusMessage = "✅ Attendance report sent to print.";
            }
            catch (Exception ex)
            {
                _logger.LogError("Attendance print failed", ex);
                StatusMessage = $"❌ Could not print attendance: {UserMessage.For(ex)}";
            }
        }

        private async Task ExportAttendanceAsync()
        {
            try
            {
                var settings = await _settingsService.LoadAsync();
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "PDF Document (*.pdf)|*.pdf",
                    FileName = $"Attendance_{DateTime.Now:yyyyMMdd}.pdf"
                };

                if (dialog.ShowDialog() != true) return;

                AttendancePdfBuilder.Generate(_presentWorkersAll, _absentWorkersAll, settings, dialog.FileName);
                StatusMessage = $"✅ Attendance exported to {Path.GetFileName(dialog.FileName)}";
            }
            catch (Exception ex)
            {
                _logger.LogError("Attendance export failed", ex);
                StatusMessage = $"❌ Could not export attendance: {UserMessage.For(ex)}";
            }
        }

        public async Task InitializeAsync()
        {
            await LoadDataAsync();
        }

        private async Task LoadDataAsync()
        {
            try
            {
                IsLoading = true;
                var workers = await _workerService.GetAllAsync();
                Workers = new ObservableCollection<Worker>(workers);

                await RefreshSelectedWorkerStatusAsync();
                await RefreshAttendanceListsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to load data", ex);
                StatusMessage = $"❌ Error loading data: {UserMessage.For(ex)}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task ClockInExecuteAsync()
        {
            if (SelectedWorker == null) return;

            try
            {
                IsLoading = true;
                var result = await _timeRecordService.ClockInAsync(
                    SelectedWorker.WorkerId,
                    SelectedWorker.FullName);

                StatusMessage = result.Message;

                if (result.Success)
                {
                    await LoadDataAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Clock in failed", ex);
                StatusMessage = $"❌ Clock in failed: {UserMessage.For(ex)}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task ClockOutExecuteAsync()
        {
            if (SelectedWorker == null) return;

            try
            {
                IsLoading = true;
                var result = await _timeRecordService.ClockOutAsync(SelectedWorker.WorkerId);

                StatusMessage = result.Message;

                if (result.Success)
                {
                    await LoadDataAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Clock out failed", ex);
                StatusMessage = $"❌ Clock out failed: {UserMessage.For(ex)}";
            }
            finally
            {
                IsLoading = false;
            }
        }
    }

    #endregion

    #region Payroll ViewModel

    /// <summary>
    /// ViewModel for Payroll calculation
    /// </summary>
    public class PayrollViewModel : ViewModelBase
    {
        private readonly IPayrollService _payrollService;
        private readonly ILogger _logger;
        private readonly IAuthorizationService _authorization;
        private readonly ISettingsService _settingsService;

        private ObservableCollection<PayrollEntry> _payrollEntries = new();
        private int _selectedYear = DateTime.Now.Year;
        private int _selectedMonth = DateTime.Now.Month;
        private bool _isLoading;
        private string _statusMessage = "";
        private string _periodLabel = "";

        public ObservableCollection<PayrollEntry> PayrollEntries
        {
            get => _payrollEntries;
            set => SetProperty(ref _payrollEntries, value, nameof(PayrollEntries));
        }

        public int SelectedYear
        {
            get => _selectedYear;
            set => SetProperty(ref _selectedYear, value, nameof(SelectedYear));
        }

        public int SelectedMonth
        {
            get => _selectedMonth;
            set => SetProperty(ref _selectedMonth, value, nameof(SelectedMonth));
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value, nameof(IsLoading));
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value, nameof(StatusMessage));
        }

        public string PeriodLabel
        {
            get => _periodLabel;
            set => SetProperty(ref _periodLabel, value, nameof(PeriodLabel));
        }

        public ICommand CalculateCommand { get; }
        public ICommand ExportToExcelCommand { get; }
        public ICommand ExportToPdfCommand { get; }

        public bool CanGeneratePayroll => _authorization.HasPermission(AppPermission.GeneratePayroll);
        public bool CanExportReports => _authorization.HasPermission(AppPermission.ExportReports);

        public PayrollViewModel(IPayrollService payrollService, ILogger logger, IAuthorizationService authorization, ISettingsService settingsService)
        {
            _payrollService = payrollService;
            _logger = logger;
            _authorization = authorization;
            _settingsService = settingsService;

            CalculateCommand = new AsyncRelayCommand(CalculateExecuteAsync, () => CanGeneratePayroll);
            ExportToExcelCommand = new AsyncRelayCommand(ExportToExcelAsync, () => CanExportReports && PayrollEntries.Count > 0);
            ExportToPdfCommand = new AsyncRelayCommand(ExportToPdfAsync, () => CanExportReports && PayrollEntries.Count > 0);
        }

        private async Task CalculateExecuteAsync()
        {
            try
            {
                IsLoading = true;
                StatusMessage = "Calculating payroll...";

                var (from, to, label) = await _payrollService.GetMonthRangeAsync(_selectedYear, _selectedMonth);
                var entries = await _payrollService.CalculateAsync(from, to);

                PayrollEntries = new ObservableCollection<PayrollEntry>(entries);
                PeriodLabel = label;

                var totalGross = entries.Sum(e => e.GrossEarnings);
                var totalPaid = entries.Sum(e => e.TotalPaid);

                StatusMessage = $"✅ Calculated for {entries.Count} workers | " +
                    $"Total Gross: {totalGross:C} | Total Paid: {totalPaid:C}";

                _logger.LogInfo($"Payroll calculated: {entries.Count} workers, Period: {label}");
            }
            catch (Exception ex)
            {
                _logger.LogError("Payroll calculation failed", ex);
                StatusMessage = $"❌ Error: {UserMessage.For(ex)}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task ExportToExcelAsync()
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                    FileName = $"Payroll_{PeriodLabel.Replace(" ", "_").Replace("/", "-")}.xlsx"
                };

                if (dialog.ShowDialog() != true) return;

                var settings = await _settingsService.LoadAsync();

                using var workbook = new XLWorkbook();
                var sheet = workbook.Worksheets.Add("Payroll");

                string[] headers = { "Worker ID", "Name", "Daily Rate", "Days Worked", "Hours Worked", "Gross Earnings", "Total Paid", "Balance" };

                int headerRow = ExportBranding.WriteExcelCompanyHeader(sheet, settings, $"Payroll Report - {PeriodLabel}", headers.Length);

                for (int i = 0; i < headers.Length; i++)
                {
                    sheet.Cell(headerRow, i + 1).Value = headers[i];
                }
                ExportBranding.StyleHeaderRow(sheet, headerRow, headers.Length);

                int row = headerRow + 1;
                int firstDataRow = row;
                foreach (var entry in PayrollEntries)
                {
                    sheet.Cell(row, 1).Value = entry.WorkerId;
                    sheet.Cell(row, 2).Value = entry.WorkerName;
                    sheet.Cell(row, 3).Value = entry.DailyRate;
                    sheet.Cell(row, 4).Value = entry.DaysWorked;
                    sheet.Cell(row, 5).Value = entry.HoursWorked;
                    sheet.Cell(row, 6).Value = entry.GrossEarnings;
                    sheet.Cell(row, 7).Value = entry.TotalPaid;
                    sheet.Cell(row, 8).Value = entry.Balance;

                    sheet.Range(row, 3, row, 3).Style.NumberFormat.Format = "#,##0.00";
                    sheet.Range(row, 6, row, 8).Style.NumberFormat.Format = "#,##0.00";
                    row++;
                }
                ExportBranding.ApplyRowBanding(sheet, firstDataRow, row - 1, headers.Length);

                var totalGross = PayrollEntries.Sum(e => e.GrossEarnings);
                var totalPaid = PayrollEntries.Sum(e => e.TotalPaid);
                sheet.Cell(row, 5).Value = "TOTAL";
                sheet.Cell(row, 5).Style.Font.Bold = true;
                sheet.Cell(row, 6).Value = totalGross;
                sheet.Cell(row, 6).Style.Font.Bold = true;
                sheet.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
                sheet.Cell(row, 7).Value = totalPaid;
                sheet.Cell(row, 7).Style.Font.Bold = true;
                sheet.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
                sheet.Range(row, 1, row, headers.Length).Style.Border.TopBorder = XLBorderStyleValues.Medium;

                ExportBranding.ApplyExcelWatermark(sheet, headers.Length);

                row += 2; // blank spacer rows
                ExportBranding.ApplyExcelFooter(sheet, row, headers.Length);

                sheet.SheetView.FreezeRows(headerRow);
                sheet.Columns().AdjustToContents();
                workbook.SaveAs(dialog.FileName);

                StatusMessage = $"\u2705 Exported to {Path.GetFileName(dialog.FileName)}";
                _logger.LogInfo($"Payroll exported to Excel: {dialog.FileName}");
            }
            catch (Exception ex)
            {
                _logger.LogError("Excel export failed", ex);
                StatusMessage = $"\u274c Excel export failed: {UserMessage.For(ex)}";
            }
        }

        private async Task ExportToPdfAsync()
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Filter = "PDF Document (*.pdf)|*.pdf",
                    FileName = $"Payroll_{PeriodLabel.Replace(" ", "_").Replace("/", "-")}.pdf"
                };

                if (dialog.ShowDialog() != true) return;

                var settings = await _settingsService.LoadAsync();
                var entries = PayrollEntries.ToList();
                var totalGross = entries.Sum(e => e.GrossEarnings);
                var totalPaid = entries.Sum(e => e.TotalPaid);
                var period = PeriodLabel;

                Document.Create(container =>
                {
                    container.Page(page =>
                    {
                        page.Size(PageSizes.A4);
                        page.Margin(0);
                        page.DefaultTextStyle(x => x.FontSize(10));

                        ExportBranding.ApplyPdfWatermark(page);

                        page.Content().Column(column =>
                        {
                            ExportBranding.ApplyGradientHeader(column, settings, "Payroll Report", period);

                            column.Item().PaddingHorizontal(30).PaddingTop(14).Column(body =>
                            {
                                ExportBranding.ApplyCompanyDetailsLine(body, settings);

                                body.Item().Table(table =>
                                {
                                    table.ColumnsDefinition(columns =>
                                    {
                                        columns.RelativeColumn(1.2f);
                                        columns.RelativeColumn(2f);
                                        columns.RelativeColumn(1f);
                                        columns.RelativeColumn(0.9f);
                                        columns.RelativeColumn(1f);
                                        columns.RelativeColumn(1.3f);
                                        columns.RelativeColumn(1.3f);
                                        columns.RelativeColumn(1.3f);
                                    });

                                    table.Header(header =>
                                    {
                                        string[] headers = { "Worker ID", "Name", "Rate", "Days", "Hours", "Gross", "Paid", "Balance" };
                                        foreach (var h in headers)
                                        {
                                            header.Cell().Background(ExportBranding.BrandHexStart).Padding(5)
                                                .Text(h).SemiBold().FontColor(Colors.White);
                                        }
                                    });

                                    bool alternate = false;
                                    foreach (var entry in entries)
                                    {
                                        var bg = alternate ? "#EEF2FF" : "#FFFFFF";
                                        alternate = !alternate;

                                        table.Cell().Background(bg).Border(1).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(entry.WorkerId);
                                        table.Cell().Background(bg).Border(1).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(entry.WorkerName);
                                        table.Cell().Background(bg).Border(1).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(entry.DailyRate.ToString("N2"));
                                        table.Cell().Background(bg).Border(1).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(entry.DaysWorked.ToString());
                                        table.Cell().Background(bg).Border(1).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(entry.HoursWorked.ToString("N1"));
                                        table.Cell().Background(bg).Border(1).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(entry.GrossEarnings.ToString("N2"));
                                        table.Cell().Background(bg).Border(1).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(entry.TotalPaid.ToString("N2"));
                                        table.Cell().Background(bg).Border(1).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(entry.Balance.ToString("N2"));
                                    }
                                });

                                body.Item().PaddingTop(14).AlignRight().Background("#EEF2FF").Padding(8)
                                    .Text($"Total Gross: {totalGross:N2}    Total Paid: {totalPaid:N2}")
                                    .Bold().FontColor(ExportBranding.BrandHexEnd);
                            });
                        });

                        page.Footer().PaddingTop(10).Column(footer =>
                        {
                            footer.Item().AlignCenter().Text(x =>
                            {
                                x.Span("Generated ");
                                x.Span(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                            });
                            footer.Item().PaddingTop(2).AlignCenter().Text(AppBranding.ExportFooterLine)
                                .FontSize(7).FontColor(Colors.Grey.Medium);
                        });
                    });
                }).GeneratePdf(dialog.FileName);

                StatusMessage = $"\u2705 Exported to {Path.GetFileName(dialog.FileName)}";
                _logger.LogInfo($"Payroll exported to PDF: {dialog.FileName}");
            }
            catch (Exception ex)
            {
                _logger.LogError("PDF export failed", ex);
                StatusMessage = $"\u274c PDF export failed: {UserMessage.For(ex)}";
            }
        }
    }

    #endregion

    #region Workers ViewModel

    /// <summary>
    /// ViewModel for Worker Management
    /// </summary>
    public class WorkersViewModel : ViewModelBase
    {
        private readonly IWorkerService _workerService;
        private readonly ILogger _logger;
        private readonly IAuditService _auditService;
        private readonly ICurrentUserContext _currentUser;
        private readonly ISettingsService _settingsService;
        private readonly IAuthorizationService _authorization;

        private ObservableCollection<Worker> _workers = new();
        private Worker? _selectedWorker;
        private string _searchTerm = "";
        private string _filterCategory = "All";
        private string _filterStatus = "All";
        private bool _isLoading;
        private int _currentPage = 1;
        private int _pageSize = 50;
        private int _totalPages = 1;
        private string _statusMessage = "";

        // Bound fields for the "Add / Edit Worker" form
        private string _newWorkerFullName = "";
        private string _newWorkerNationalId = "";
        private string _newWorkerPhoneNumber = "";
        private string _newWorkerCategory = "Casual";
        private string _newWorkerJobRole = "General Laborer";
        private string _newWorkerDailyWageRate = "";
        private bool _isEditing;
        private Worker? _workerBeingEdited;
        private ObservableCollection<string> _availableJobRoles = new();

        public ObservableCollection<string> AvailableJobRoles
        {
            get => _availableJobRoles;
            private set => SetProperty(ref _availableJobRoles, value, nameof(AvailableJobRoles));
        }

        public ObservableCollection<Worker> Workers
        {
            get => _workers;
            set => SetProperty(ref _workers, value, nameof(Workers));
        }

        public Worker? SelectedWorker
        {
            get => _selectedWorker;
            set => SetProperty(ref _selectedWorker, value, nameof(SelectedWorker));
        }

        public string SearchTerm
        {
            get => _searchTerm;
            set => SetProperty(ref _searchTerm, value, nameof(SearchTerm));
        }

        public string FilterCategory
        {
            get => _filterCategory;
            set
            {
                SetProperty(ref _filterCategory, value, nameof(FilterCategory));
                _ = SearchAsync();
            }
        }

        public string FilterStatus
        {
            get => _filterStatus;
            set
            {
                SetProperty(ref _filterStatus, value, nameof(FilterStatus));
                _ = SearchAsync();
            }
        }

        public List<string> CategoryFilterOptions { get; } = new() { "All", "Casual", "Skilled", "Security", "Supervisor" };
        public List<string> StatusFilterOptions { get; } = new() { "All", "Active", "Inactive" };

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value, nameof(IsLoading));
        }

        public int CurrentPage
        {
            get => _currentPage;
            set => SetProperty(ref _currentPage, value, nameof(CurrentPage));
        }

        public int TotalPages
        {
            get => _totalPages;
            set => SetProperty(ref _totalPages, value, nameof(TotalPages));
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value, nameof(StatusMessage));
        }

        public string NewWorkerFullName
        {
            get => _newWorkerFullName;
            set => SetProperty(ref _newWorkerFullName, value, nameof(NewWorkerFullName));
        }

        public string NewWorkerNationalId
        {
            get => _newWorkerNationalId;
            set => SetProperty(ref _newWorkerNationalId, value, nameof(NewWorkerNationalId));
        }

        public string NewWorkerPhoneNumber
        {
            get => _newWorkerPhoneNumber;
            set => SetProperty(ref _newWorkerPhoneNumber, value, nameof(NewWorkerPhoneNumber));
        }

        public string NewWorkerCategory
        {
            get => _newWorkerCategory;
            set => SetProperty(ref _newWorkerCategory, value, nameof(NewWorkerCategory));
        }

        public string NewWorkerJobRole
        {
            get => _newWorkerJobRole;
            set => SetProperty(ref _newWorkerJobRole, value, nameof(NewWorkerJobRole));
        }

        public string NewWorkerDailyWageRate
        {
            get => _newWorkerDailyWageRate;
            set => SetProperty(ref _newWorkerDailyWageRate, value, nameof(NewWorkerDailyWageRate));
        }

        public bool IsEditing
        {
            get => _isEditing;
            set
            {
                SetProperty(ref _isEditing, value, nameof(IsEditing));
                OnPropertyChanged(nameof(FormTitle));
                OnPropertyChanged(nameof(SaveButtonLabel));
            }
        }

        public string FormTitle => IsEditing ? "Edit Worker" : "Add Worker";
        public string SaveButtonLabel => IsEditing ? "Save Changes" : "Add Worker";

        public bool CanRegisterWorkers => _authorization.HasPermission(AppPermission.RegisterWorkers);
        public bool CanEditWorkers => _authorization.HasPermission(AppPermission.EditWorkers);
        public bool CanDeleteWorkers => _authorization.HasPermission(AppPermission.DeleteWorkers);

        /// <summary>True if this role has neither add nor edit rights - used
        /// to hide the whole Add/Edit form for read-only roles (Supervisor),
        /// rather than showing a form full of disabled controls.</summary>
        public bool CanUseWorkerForm => CanRegisterWorkers || CanEditWorkers;

        public ICommand LoadWorkersCommand { get; }
        public ICommand SearchCommand { get; }
        public ICommand NextPageCommand { get; }
        public ICommand PreviousPageCommand { get; }
        public ICommand AddWorkerCommand { get; }
        public ICommand EditWorkerCommand { get; }
        public ICommand CancelEditCommand { get; }
        public ICommand DeactivateWorkerCommand { get; }
        public ICommand ImportWorkersCommand { get; }

        public WorkersViewModel(
            IWorkerService workerService,
            ILogger logger,
            IAuditService auditService,
            ICurrentUserContext currentUser,
            ISettingsService settingsService,
            IAuthorizationService authorization)
        {
            _workerService = workerService;
            _logger = logger;
            _auditService = auditService;
            _currentUser = currentUser;
            _settingsService = settingsService;
            _authorization = authorization;

            LoadWorkersCommand = new AsyncRelayCommand(LoadWorkersAsync);
            SearchCommand = new AsyncRelayCommand(SearchAsync);
            NextPageCommand = new RelayCommand(_ => NextPage(), _ => CurrentPage < TotalPages);
            PreviousPageCommand = new RelayCommand(_ => PreviousPage(), _ => CurrentPage > 1);
            AddWorkerCommand = new AsyncRelayCommand(SaveWorkerAsync, () => IsEditing ? CanEditWorkers : CanRegisterWorkers);
            EditWorkerCommand = new RelayCommand(_ => BeginEdit(), _ => SelectedWorker != null && CanEditWorkers);
            CancelEditCommand = new RelayCommand(_ => CancelEdit());
            DeactivateWorkerCommand = new AsyncRelayCommand(DeactivateSelectedWorkerAsync, () => SelectedWorker != null && CanDeleteWorkers);
            ImportWorkersCommand = new AsyncRelayCommand(ImportWorkersAsync, () => CanRegisterWorkers);

            _ = PrefillFromSettingsAsync();
        }

        private async Task PrefillFromSettingsAsync()
        {
            try
            {
                var settings = await _settingsService.LoadAsync();
                if (string.IsNullOrWhiteSpace(NewWorkerDailyWageRate))
                {
                    NewWorkerDailyWageRate = settings.DefaultDailyWageRate.ToString();
                }

                AvailableJobRoles = new ObservableCollection<string>(settings.JobRoles);

                // If the role currently selected for the add/edit form isn't
                // in the configured list (e.g. it's the very first time this
                // runs, or the list was edited in Settings since), fall back
                // to the first available role rather than leaving a value
                // the dropdown can't actually display.
                if (!AvailableJobRoles.Contains(NewWorkerJobRole) && AvailableJobRoles.Count > 0)
                {
                    NewWorkerJobRole = AvailableJobRoles[0];
                }
            }
            catch
            {
                // Non-critical - the fields just keep their current values
                // if this fails.
            }
        }

        public async Task InitializeAsync()
        {
            await LoadWorkersAsync();
        }

        private async Task LoadWorkersAsync()
        {
            try
            {
                IsLoading = true;
                var workers = await _workerService.GetPagedAsync(CurrentPage, _pageSize);
                Workers = new ObservableCollection<Worker>(workers);

                var count = await _workerService.CountAsync();
                TotalPages = Math.Max(1, (count + _pageSize - 1) / _pageSize);

                _logger.LogInfo($"Loaded {workers.Count()} workers");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to load workers", ex);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task SearchAsync()
        {
            try
            {
                IsLoading = true;
                var results = await _workerService.SearchAsync(SearchTerm);

                if (FilterCategory != "All")
                {
                    results = results.Where(w => string.Equals(w.WorkerCategory, FilterCategory, StringComparison.OrdinalIgnoreCase));
                }

                if (FilterStatus != "All")
                {
                    results = results.Where(w => string.Equals(w.Status, FilterStatus, StringComparison.OrdinalIgnoreCase));
                }

                Workers = new ObservableCollection<Worker>(results);
            }
            catch (Exception ex)
            {
                _logger.LogError("Search failed", ex);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void NextPage()
        {
            if (CurrentPage < TotalPages)
            {
                CurrentPage++;
                _ = LoadWorkersAsync();
            }
        }

        private void PreviousPage()
        {
            if (CurrentPage > 1)
            {
                CurrentPage--;
                _ = LoadWorkersAsync();
            }
        }

        private async Task SaveWorkerAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(NewWorkerFullName))
                {
                    StatusMessage = "Full name is required.";
                    return;
                }
                if (string.IsNullOrWhiteSpace(NewWorkerNationalId))
                {
                    StatusMessage = "National ID is required.";
                    return;
                }
                if (!decimal.TryParse(NewWorkerDailyWageRate, out var dailyRate) || dailyRate <= 0)
                {
                    StatusMessage = "Daily wage rate must be a number greater than 0.";
                    return;
                }

                IsLoading = true;
                var actor = _currentUser.Username ?? "system";

                if (IsEditing && _workerBeingEdited != null)
                {
                    var oldSnapshot = $"{_workerBeingEdited.FullName}|{_workerBeingEdited.PhoneNumber}|{_workerBeingEdited.DailyWageRate}";

                    _workerBeingEdited.FullName = NewWorkerFullName.Trim();
                    _workerBeingEdited.NationalId = NewWorkerNationalId.Trim();
                    _workerBeingEdited.PhoneNumber = NewWorkerPhoneNumber.Trim();
                    _workerBeingEdited.WorkerCategory = string.IsNullOrWhiteSpace(NewWorkerCategory) ? "Casual" : NewWorkerCategory;
                    _workerBeingEdited.JobRole = string.IsNullOrWhiteSpace(NewWorkerJobRole) ? "General Laborer" : NewWorkerJobRole;
                    _workerBeingEdited.DailyWageRate = dailyRate;

                    await _workerService.UpdateAsync(_workerBeingEdited);
                    StatusMessage = $"Worker '{_workerBeingEdited.FullName}' updated.";

                    var newSnapshot = $"{_workerBeingEdited.FullName}|{_workerBeingEdited.PhoneNumber}|{_workerBeingEdited.DailyWageRate}";
                    await _auditService.LogAsync("Update", "Workers", _workerBeingEdited.WorkerId, _currentUser.UserId, oldSnapshot, newSnapshot);
                }
                else
                {
                    var worker = new Worker
                    {
                        WorkerId = "W" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
                        FullName = NewWorkerFullName.Trim(),
                        NationalId = NewWorkerNationalId.Trim(),
                        PhoneNumber = NewWorkerPhoneNumber.Trim(),
                        DateJoined = DateTime.Now,
                        WorkerCategory = string.IsNullOrWhiteSpace(NewWorkerCategory) ? "Casual" : NewWorkerCategory,
                        JobRole = string.IsNullOrWhiteSpace(NewWorkerJobRole) ? "General Laborer" : NewWorkerJobRole,
                        DailyWageRate = dailyRate,
                        Status = "Active"
                    };

                    await _workerService.CreateAsync(worker, actor);
                    StatusMessage = $"Worker '{worker.FullName}' added.";

                    await _auditService.LogAsync("Create", "Workers", worker.WorkerId, _currentUser.UserId, null, $"{worker.FullName}|{worker.DailyWageRate}");
                }

                ClearForm();
                await LoadWorkersAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to save worker", ex);
                StatusMessage = $"Failed to save worker: {UserMessage.For(ex)}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void BeginEdit()
        {
            if (SelectedWorker == null) return;

            _workerBeingEdited = SelectedWorker;
            NewWorkerFullName = SelectedWorker.FullName;
            NewWorkerNationalId = SelectedWorker.NationalId;
            NewWorkerPhoneNumber = SelectedWorker.PhoneNumber;
            NewWorkerCategory = SelectedWorker.WorkerCategory;

            // The worker's existing role might not be in the current list -
            // e.g. it predates this feature, came from a CSV import with
            // free text, or was later removed from Settings. Rather than
            // silently blanking the dropdown, add it temporarily so the
            // form shows the worker's real current value; it isn't saved
            // back into the canonical Settings list unless an administrator
            // adds it there deliberately.
            if (!string.IsNullOrWhiteSpace(SelectedWorker.JobRole) && !AvailableJobRoles.Contains(SelectedWorker.JobRole))
            {
                AvailableJobRoles.Add(SelectedWorker.JobRole);
            }
            NewWorkerJobRole = SelectedWorker.JobRole;

            NewWorkerDailyWageRate = SelectedWorker.DailyWageRate.ToString();
            IsEditing = true;
            StatusMessage = $"Editing {SelectedWorker.FullName}. Make changes and click Save.";
        }

        private void CancelEdit()
        {
            ClearForm();
            StatusMessage = "";
        }

        private void ClearForm()
        {
            NewWorkerFullName = "";
            NewWorkerNationalId = "";
            NewWorkerPhoneNumber = "";
            NewWorkerCategory = "Casual";
            NewWorkerJobRole = "General Laborer";
            NewWorkerDailyWageRate = "";
            IsEditing = false;
            _workerBeingEdited = null;
        }

        private async Task DeactivateSelectedWorkerAsync()
        {
            if (SelectedWorker == null) return;

            var confirm = MessageBox.Show(
                $"Deactivate '{SelectedWorker.FullName}'? They will no longer appear in the Clock In/Out worker list or new payroll runs. This cannot be undone from within the app.",
                "Confirm Deactivation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                IsLoading = true;
                var name = SelectedWorker.FullName;
                var workerId = SelectedWorker.WorkerId;
                var actor = _currentUser.Username ?? "system";

                await _workerService.DeactivateAsync(workerId);
                StatusMessage = $"Worker '{name}' deactivated.";
                await _auditService.LogAsync("Deactivate", "Workers", workerId, _currentUser.UserId, "Active", "Inactive");

                if (_workerBeingEdited?.WorkerId == workerId)
                {
                    ClearForm();
                }

                await LoadWorkersAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to deactivate worker", ex);
                StatusMessage = $"Failed to deactivate worker: {UserMessage.For(ex)}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Imports workers from a CSV file. Expected columns (header row
        /// required, case-insensitive, order-independent):
        ///   FullName, NationalId, PhoneNumber, Category, JobRole, DailyWageRate
        /// Category and JobRole are optional (default to Casual / General
        /// Laborer, same as the Add Worker form). Rows with missing
        /// required fields or an invalid wage rate are skipped and reported
        /// rather than aborting the whole import.
        /// </summary>
        private async Task ImportWorkersAsync()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                Title = "Import Workers from CSV"
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                IsLoading = true;
                StatusMessage = "Importing...";

                var content = await System.IO.File.ReadAllTextAsync(dialog.FileName);
                var rows = CsvParser.Parse(content);

                if (rows.Count < 2)
                {
                    StatusMessage = "❌ The file has no data rows (only a header, or is empty).";
                    return;
                }

                var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
                int Col(string name) => header.IndexOf(name);

                var iName = Col("fullname");
                var iNationalId = Col("nationalid");
                var iPhone = Col("phonenumber");
                var iCategory = Col("category");
                var iJobRole = Col("jobrole");
                var iRate = Col("dailywagerate");

                if (iName < 0 || iNationalId < 0 || iRate < 0)
                {
                    StatusMessage = "❌ CSV must include at least FullName, NationalId, and DailyWageRate columns.";
                    return;
                }

                var actor = _currentUser.Username ?? "system";
                int imported = 0;
                var errors = new List<string>();

                for (int r = 1; r < rows.Count; r++)
                {
                    var row = rows[r];
                    string Get(int idx) => idx >= 0 && idx < row.Length ? row[idx].Trim() : "";

                    var name = Get(iName);
                    var nationalId = Get(iNationalId);

                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(nationalId))
                    {
                        errors.Add($"Row {r + 1}: missing full name or national ID - skipped.");
                        continue;
                    }

                    if (!decimal.TryParse(Get(iRate), out var rate) || rate <= 0)
                    {
                        errors.Add($"Row {r + 1} ({name}): invalid daily wage rate - skipped.");
                        continue;
                    }

                    try
                    {
                        var worker = new Worker
                        {
                            WorkerId = "W" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
                            FullName = name,
                            NationalId = nationalId,
                            PhoneNumber = Get(iPhone),
                            DateJoined = DateTime.Now,
                            WorkerCategory = string.IsNullOrWhiteSpace(Get(iCategory)) ? "Casual" : Get(iCategory),
                            JobRole = string.IsNullOrWhiteSpace(Get(iJobRole)) ? "General Laborer" : Get(iJobRole),
                            DailyWageRate = rate,
                            Status = "Active"
                        };

                        await _workerService.CreateAsync(worker, actor);
                        imported++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Row {r + 1} ({name}): {UserMessage.For(ex)}");
                    }
                }

                await _auditService.LogAsync("Import", "Workers", "", _currentUser.UserId, null,
                    $"Imported {imported} worker(s) from {System.IO.Path.GetFileName(dialog.FileName)}, {errors.Count} error(s)");

                var summary = $"✅ Imported {imported} worker(s).";
                if (errors.Count > 0)
                {
                    var shown = errors.Take(5);
                    summary += $" {errors.Count} row(s) skipped:\n" + string.Join("\n", shown);
                    if (errors.Count > 5) summary += $"\n...and {errors.Count - 5} more.";
                }
                StatusMessage = summary;

                await LoadWorkersAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError("Worker import failed", ex);
                StatusMessage = $"❌ Import failed: {UserMessage.For(ex)}";
            }
            finally
            {
                IsLoading = false;
            }
        }
    }

    #endregion

    #region Payments and Receipt ViewModels

    #region Receipt ViewModel

    /// <summary>
    /// Everything needed to render a payment receipt - on screen, printed,
    /// or exported to PDF - all from one place so the three outputs never
    /// drift out of sync with each other.
    /// </summary>
    public class ReceiptViewModel
    {
        public string ReceiptNumber { get; set; } = "";
        public DateTime PaymentDate { get; set; }
        public string WorkerId { get; set; } = "";
        public string WorkerName { get; set; } = "";
        public string? JobRole { get; set; }
        public decimal AmountPaid { get; set; }
        public string PaymentMethod { get; set; } = "";
        public string? MpesaCode { get; set; }
        public string RecordedBy { get; set; } = "";
        public AppSettingsData Settings { get; set; } = new();

        public string FormattedAmount => $"{Settings.Currency} {AmountPaid:N2}";
        public string FormattedDate => PaymentDate.ToString("dddd, dd MMMM yyyy  HH:mm");
        public string JobRoleDisplay => string.IsNullOrWhiteSpace(JobRole) ? "-" : JobRole;
        public string MpesaCodeDisplay => string.IsNullOrWhiteSpace(MpesaCode) ? "-" : MpesaCode;
        public bool HasMpesaCode => !string.IsNullOrWhiteSpace(MpesaCode);

        public string CompanyDetailsLine
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(Settings.CompanyAddress)) parts.Add(Settings.CompanyAddress);
                if (!string.IsNullOrWhiteSpace(Settings.CompanyPhone)) parts.Add($"Tel: {Settings.CompanyPhone}");
                if (!string.IsNullOrWhiteSpace(Settings.CompanyEmail)) parts.Add(Settings.CompanyEmail);
                if (!string.IsNullOrWhiteSpace(Settings.CompanyRegistrationNumber)) parts.Add($"Reg: {Settings.CompanyRegistrationNumber}");
                return string.Join("   |   ", parts);
            }
        }

        public static ReceiptViewModel FromPaymentRecord(PaymentRecord record, AppSettingsData settings, string? jobRole, string viewedBy)
        {
            return new ReceiptViewModel
            {
                ReceiptNumber = record.ReceiptNumber,
                PaymentDate = record.PaymentDate,
                WorkerId = record.WorkerId,
                WorkerName = record.WorkerName,
                JobRole = jobRole,
                AmountPaid = record.AmountPaid,
                PaymentMethod = record.PaymentMethod,
                MpesaCode = record.MpesaCode,
                RecordedBy = viewedBy,
                Settings = settings
            };
        }
    }

    #endregion

    /// <summary>
    /// Records payments/advances against workers and shows recent payment
    /// history. Kept as its own page (rather than folded into Payroll) so
    /// PayrollViewModel's constructor - and the existing unit test that
    /// instantiates it directly - doesn't need to change.
    /// </summary>
    public class PaymentsViewModel : ViewModelBase
    {
        private readonly IPaymentService _paymentService;
        private readonly IWorkerService _workerService;
        private readonly IPayrollService _payrollService;
        private readonly IAuditService _auditService;
        private readonly ICurrentUserContext _currentUser;
        private readonly ILogger _logger;
        private readonly IAuthorizationService _authorization;
        private readonly ITimeRecordService _timeRecordService;
        private readonly ISettingsService _settingsService;

        private ObservableCollection<Worker> _workers = new();
        private ObservableCollection<Worker> _filteredWorkers = new();
        private string _workerSearchTerm = "";
        private Worker? _selectedWorker;
        private string _amount = "";
        private string _paymentMethod = "Cash";
        private string _mpesaCode = "";
        private ObservableCollection<PaymentRecord> _recentPayments = new();
        private bool _isLoading;
        private string _statusMessage = "";
        private int _currentPage = 1;
        private int _totalPages = 1;
        private readonly int _pageSize = 25;
        private decimal? _weekBalanceDue;
        private decimal? _monthBalanceDue;
        private string _weekBalanceHint = "";
        private string _monthBalanceHint = "";
        private bool _selectedWorkerHasWorkedDays = true;
        private string _zeroDaysWarning = "";
        private ReceiptViewModel? _lastReceipt;

        public ObservableCollection<Worker> Workers
        {
            get => _workers;
            set
            {
                SetProperty(ref _workers, value, nameof(Workers));
                ApplyWorkerFilter();
            }
        }

        public ObservableCollection<Worker> FilteredWorkers
        {
            get => _filteredWorkers;
            set => SetProperty(ref _filteredWorkers, value, nameof(FilteredWorkers));
        }

        public string WorkerSearchTerm
        {
            get => _workerSearchTerm;
            set
            {
                SetProperty(ref _workerSearchTerm, value, nameof(WorkerSearchTerm));
                ApplyWorkerFilter();
            }
        }

        private void ApplyWorkerFilter()
        {
            if (string.IsNullOrWhiteSpace(WorkerSearchTerm))
            {
                FilteredWorkers = new ObservableCollection<Worker>(Workers);
                return;
            }

            var term = WorkerSearchTerm.Trim();
            var matches = Workers.Where(w =>
                w.FullName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                w.PhoneNumber.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                w.NationalId.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                w.WorkerId.Contains(term, StringComparison.OrdinalIgnoreCase));
            FilteredWorkers = new ObservableCollection<Worker>(matches);

            // Selecting a worker via search shouldn't require the picker to
            // still show every worker - if exactly one match remains, that's
            // almost certainly what was being searched for.
        }

        public bool SelectedWorkerHasWorkedDays
        {
            get => _selectedWorkerHasWorkedDays;
            private set => SetProperty(ref _selectedWorkerHasWorkedDays, value, nameof(SelectedWorkerHasWorkedDays));
        }

        public string ZeroDaysWarning
        {
            get => _zeroDaysWarning;
            private set => SetProperty(ref _zeroDaysWarning, value, nameof(ZeroDaysWarning));
        }

        public ReceiptViewModel? LastReceipt
        {
            get => _lastReceipt;
            private set => SetProperty(ref _lastReceipt, value, nameof(LastReceipt));
        }

        public Worker? SelectedWorker
        {
            get => _selectedWorker;
            set
            {
                SetProperty(ref _selectedWorker, value, nameof(SelectedWorker));
                PrefillAmountFromSelectedWorker();
                _ = RefreshBalanceDueAsync();
                _ = RefreshWorkedDaysStatusAsync();
            }
        }

        public string Amount
        {
            get => _amount;
            set => SetProperty(ref _amount, value, nameof(Amount));
        }

        public string PaymentMethod
        {
            get => _paymentMethod;
            set => SetProperty(ref _paymentMethod, value, nameof(PaymentMethod));
        }

        public string MpesaCode
        {
            get => _mpesaCode;
            set => SetProperty(ref _mpesaCode, value, nameof(MpesaCode));
        }

        public ObservableCollection<PaymentRecord> RecentPayments
        {
            get => _recentPayments;
            set => SetProperty(ref _recentPayments, value, nameof(RecentPayments));
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value, nameof(IsLoading));
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value, nameof(StatusMessage));
        }

        public int CurrentPage
        {
            get => _currentPage;
            set => SetProperty(ref _currentPage, value, nameof(CurrentPage));
        }

        public int TotalPages
        {
            get => _totalPages;
            set => SetProperty(ref _totalPages, value, nameof(TotalPages));
        }

        public string WeekBalanceHint
        {
            get => _weekBalanceHint;
            private set => SetProperty(ref _weekBalanceHint, value, nameof(WeekBalanceHint));
        }

        public string MonthBalanceHint
        {
            get => _monthBalanceHint;
            private set => SetProperty(ref _monthBalanceHint, value, nameof(MonthBalanceHint));
        }

        public ICommand RecordPaymentCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand NextPageCommand { get; }
        public ICommand PreviousPageCommand { get; }
        public ICommand UseWeekBalanceCommand { get; }
        public ICommand UseMonthBalanceCommand { get; }
        public ICommand ViewReceiptCommand { get; }
        public ICommand ViewLastReceiptCommand { get; }
        public ICommand PrintSelectedReceiptsCommand { get; }
        public ICommand ExportSelectedReceiptsCommand { get; }
        public ICommand SelectAllOnPageCommand { get; }
        public ICommand ClearSelectionCommand { get; }

        public bool CanRecordPayments => _authorization.HasPermission(AppPermission.RecordPayments);
        public bool CanPrintReceipts => _authorization.HasPermission(AppPermission.PrintPayslips);

        public PaymentsViewModel(
            IPaymentService paymentService,
            IWorkerService workerService,
            IPayrollService payrollService,
            IAuditService auditService,
            ICurrentUserContext currentUser,
            ILogger logger,
            IAuthorizationService authorization,
            ITimeRecordService timeRecordService,
            ISettingsService settingsService)
        {
            _paymentService = paymentService;
            _workerService = workerService;
            _payrollService = payrollService;
            _auditService = auditService;
            _currentUser = currentUser;
            _logger = logger;
            _authorization = authorization;
            _timeRecordService = timeRecordService;
            _settingsService = settingsService;

            RecordPaymentCommand = new AsyncRelayCommand(RecordPaymentAsync, () => CanRecordPayments && SelectedWorker != null && SelectedWorkerHasWorkedDays);
            RefreshCommand = new AsyncRelayCommand(LoadAsync);
            NextPageCommand = new RelayCommand(_ => NextPage(), _ => CurrentPage < TotalPages);
            PreviousPageCommand = new RelayCommand(_ => PreviousPage(), _ => CurrentPage > 1);
            UseWeekBalanceCommand = new AsyncRelayCommand(UseWeekBalanceAsync, () => SelectedWorker != null && SelectedWorkerHasWorkedDays);
            UseMonthBalanceCommand = new AsyncRelayCommand(UseMonthBalanceAsync, () => SelectedWorker != null && SelectedWorkerHasWorkedDays);
            ViewReceiptCommand = new AsyncRelayCommand<PaymentRecord>(ViewReceiptAsync);
            ViewLastReceiptCommand = new RelayCommand(_ => OpenReceiptWindow(LastReceipt!), _ => LastReceipt != null);
            PrintSelectedReceiptsCommand = new AsyncRelayCommand(PrintSelectedReceiptsAsync, () => CanPrintReceipts && RecentPayments.Any(p => p.IsSelected));
            ExportSelectedReceiptsCommand = new AsyncRelayCommand(ExportSelectedReceiptsAsync, () => CanPrintReceipts && RecentPayments.Any(p => p.IsSelected));
            SelectAllOnPageCommand = new RelayCommand(_ => SetAllSelected(true));
            ClearSelectionCommand = new RelayCommand(_ => SetAllSelected(false));
        }

        private void SetAllSelected(bool selected)
        {
            foreach (var payment in RecentPayments)
            {
                payment.IsSelected = selected;
            }
            // The DataGrid's checkbox cells bind directly to each row's
            // IsSelected; refreshing the collection is the simplest way to
            // repaint them all after a bulk select/clear, since
            // PaymentRecord itself doesn't implement INotifyPropertyChanged.
            RecentPayments = new ObservableCollection<PaymentRecord>(RecentPayments);
        }

        private async Task<List<ReceiptViewModel>> BuildSelectedReceiptsAsync()
        {
            var selected = RecentPayments.Where(p => p.IsSelected).ToList();
            if (selected.Count == 0) return new List<ReceiptViewModel>();

            var settings = await _settingsService.LoadAsync();
            var workers = (await _workerService.GetAllAsync()).ToDictionary(w => w.WorkerId, w => w.JobRole);
            var actor = _currentUser.Username ?? "system";

            return selected
                .Select(p => ReceiptViewModel.FromPaymentRecord(p, settings, workers.GetValueOrDefault(p.WorkerId), actor))
                .ToList();
        }

        private async Task PrintSelectedReceiptsAsync()
        {
            try
            {
                var receipts = await BuildSelectedReceiptsAsync();
                if (receipts.Count == 0)
                {
                    StatusMessage = "Select at least one payment to print receipts for.";
                    return;
                }

                var tempPath = Path.Combine(Path.GetTempPath(), $"AlchemistPayTrack_Receipts_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
                ReceiptPdfBuilder.GenerateBulk(receipts, tempPath);

                // There's no reliable way to send a PDF straight to a
                // printer from .NET without depending on a third-party PDF
                // engine, so this hands the merged file to the OS's own
                // default PDF handler's print action (Edge, Acrobat, etc. -
                // virtually always present on Windows) rather than trying
                // to reimplement PDF rendering for print purposes.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tempPath,
                    UseShellExecute = true,
                    Verb = "print"
                });

                StatusMessage = $"✅ Sent {receipts.Count} receipt(s) to print.";
            }
            catch (Exception ex)
            {
                _logger.LogError("Bulk receipt print failed", ex);
                StatusMessage = $"❌ Could not print receipts: {UserMessage.For(ex)}";
            }
        }

        private async Task ExportSelectedReceiptsAsync()
        {
            try
            {
                var receipts = await BuildSelectedReceiptsAsync();
                if (receipts.Count == 0)
                {
                    StatusMessage = "Select at least one payment to export receipts for.";
                    return;
                }

                var dialog = new SaveFileDialog
                {
                    Filter = "PDF Document (*.pdf)|*.pdf",
                    FileName = $"Receipts_{DateTime.Now:yyyyMMdd_HHmmss}.pdf"
                };

                if (dialog.ShowDialog() != true) return;

                ReceiptPdfBuilder.GenerateBulk(receipts, dialog.FileName);
                StatusMessage = $"✅ Exported {receipts.Count} receipt(s) to {Path.GetFileName(dialog.FileName)}";
            }
            catch (Exception ex)
            {
                _logger.LogError("Bulk receipt export failed", ex);
                StatusMessage = $"❌ Could not export receipts: {UserMessage.For(ex)}";
            }
        }

        private async Task RefreshWorkedDaysStatusAsync()
        {
            if (SelectedWorker == null)
            {
                SelectedWorkerHasWorkedDays = true;
                ZeroDaysWarning = "";
                return;
            }

            try
            {
                var days = await _timeRecordService.GetTotalDaysWorkedAsync(SelectedWorker.WorkerId);
                SelectedWorkerHasWorkedDays = days > 0;
                ZeroDaysWarning = days > 0
                    ? ""
                    : $"{SelectedWorker.FullName} has zero recorded working days and cannot be paid yet. Clock them in first.";
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to check worked-days status", ex);
                // Fail open on the UI hint only - the service-layer check in
                // PaymentService.RecordPaymentAsync is the real backstop, so
                // a transient failure here shouldn't itself block payment.
                SelectedWorkerHasWorkedDays = true;
                ZeroDaysWarning = "";
            }
        }

        private async Task ViewReceiptAsync(PaymentRecord? record)
        {
            if (record == null) return;

            try
            {
                var settings = await _settingsService.LoadAsync();
                var worker = (await _workerService.GetAllAsync()).FirstOrDefault(w => w.WorkerId == record.WorkerId);

                var receipt = ReceiptViewModel.FromPaymentRecord(record, settings, worker?.JobRole, _currentUser.Username ?? "system");
                OpenReceiptWindow(receipt);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to open receipt", ex);
                StatusMessage = $"❌ Could not open receipt: {UserMessage.For(ex)}";
            }
        }

        private void OpenReceiptWindow(ReceiptViewModel receipt)
        {
            var window = new ReceiptWindow(receipt, CanPrintReceipts)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            window.Show();
        }

        public async Task InitializeAsync()
        {
            CurrentPage = 1;
            await LoadAsync();
        }

        private void NextPage()
        {
            if (CurrentPage < TotalPages)
            {
                CurrentPage++;
                _ = LoadAsync();
            }
        }

        private void PreviousPage()
        {
            if (CurrentPage > 1)
            {
                CurrentPage--;
                _ = LoadAsync();
            }
        }

        private void PrefillAmountFromSelectedWorker()
        {
            Amount = SelectedWorker?.DailyWageRate.ToString() ?? "";
        }

        private async Task RefreshBalanceDueAsync()
        {
            if (SelectedWorker == null)
            {
                _weekBalanceDue = null;
                _monthBalanceDue = null;
                WeekBalanceHint = "";
                MonthBalanceHint = "";
                return;
            }

            try
            {
                var now = DateTime.Now;
                var workerId = SelectedWorker.WorkerId;

                var (weekFrom, weekTo, weekLabel) = await _payrollService.GetWeekRangeAsync(now);
                var weekEntry = (await _payrollService.CalculateAsync(weekFrom, weekTo))
                    .FirstOrDefault(e => e.WorkerId == workerId);
                _weekBalanceDue = weekEntry?.Balance;
                WeekBalanceHint = weekEntry != null
                    ? $"{weekLabel}: {weekEntry.Balance:N2} due"
                    : "";

                var (monthFrom, monthTo, monthLabel) = await _payrollService.GetMonthRangeAsync(now.Year, now.Month);
                var monthEntry = (await _payrollService.CalculateAsync(monthFrom, monthTo))
                    .FirstOrDefault(e => e.WorkerId == workerId);
                _monthBalanceDue = monthEntry?.Balance;
                MonthBalanceHint = monthEntry != null
                    ? $"{monthLabel}: {monthEntry.Balance:N2} due"
                    : "";
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to load balance due", ex);
                WeekBalanceHint = "";
                MonthBalanceHint = "";
            }
        }

        private async Task UseWeekBalanceAsync()
        {
            if (SelectedWorker == null)
            {
                StatusMessage = "Select a worker first.";
                return;
            }

            await RefreshBalanceDueAsync();

            if ((_weekBalanceDue ?? 0) <= 0)
            {
                StatusMessage = "No outstanding balance for this week.";
                return;
            }

            Amount = _weekBalanceDue!.Value.ToString();
            StatusMessage = $"Amount set to week balance due ({_weekBalanceDue.Value:N2}).";
        }

        private async Task UseMonthBalanceAsync()
        {
            if (SelectedWorker == null)
            {
                StatusMessage = "Select a worker first.";
                return;
            }

            await RefreshBalanceDueAsync();

            if ((_monthBalanceDue ?? 0) <= 0)
            {
                StatusMessage = "No outstanding balance for this month.";
                return;
            }

            Amount = _monthBalanceDue!.Value.ToString();
            StatusMessage = $"Amount set to month balance due ({_monthBalanceDue.Value:N2}).";
        }

        private async Task LoadAsync()
        {
            try
            {
                IsLoading = true;

                var activeWorkers = (await _workerService.GetAllAsync())
                    .Where(w => w.Status == "Active")
                    .OrderBy(w => w.FullName);
                Workers = new ObservableCollection<Worker>(activeWorkers);

                var count = await _paymentService.CountAsync();
                TotalPages = Math.Max(1, (count + _pageSize - 1) / _pageSize);
                if (CurrentPage > TotalPages) CurrentPage = TotalPages;

                var recent = await _paymentService.GetPagedAsync(CurrentPage, _pageSize);
                RecentPayments = new ObservableCollection<PaymentRecord>(recent);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to load payments page", ex);
                StatusMessage = $"Failed to load: {UserMessage.For(ex)}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task RecordPaymentAsync()
        {
            if (SelectedWorker == null)
            {
                StatusMessage = "Select a worker first.";
                return;
            }

            if (!SelectedWorkerHasWorkedDays)
            {
                StatusMessage = $"❌ {ZeroDaysWarning}";
                return;
            }

            if (!decimal.TryParse(Amount, out var amount) || amount <= 0)
            {
                StatusMessage = "Enter a valid amount greater than 0.";
                return;
            }

            if (PaymentMethod == "M-Pesa" && string.IsNullOrWhiteSpace(MpesaCode))
            {
                StatusMessage = "M-Pesa transaction code is required for M-Pesa payments.";
                return;
            }

            try
            {
                IsLoading = true;
                var actor = _currentUser.Username ?? "system";
                var workerName = SelectedWorker.FullName;
                var workerId = SelectedWorker.WorkerId;
                var jobRole = SelectedWorker.JobRole;
                var method = PaymentMethod;
                var mpesa = PaymentMethod == "M-Pesa" ? MpesaCode.Trim() : null;

                var receiptNumber = await _paymentService.RecordPaymentAsync(workerId, amount, method, mpesa, actor);

                await _auditService.LogAsync("Create", "Payments", receiptNumber, _currentUser.UserId, null, $"{workerName}: {amount:N2} via {method}");

                StatusMessage = $"✅ Payment of {amount:N2} recorded for {workerName} (Receipt: {receiptNumber}).";

                try
                {
                    var settings = await _settingsService.LoadAsync();
                    LastReceipt = new ReceiptViewModel
                    {
                        ReceiptNumber = receiptNumber,
                        PaymentDate = DateTime.Now,
                        WorkerId = workerId,
                        WorkerName = workerName,
                        JobRole = jobRole,
                        AmountPaid = amount,
                        PaymentMethod = method,
                        MpesaCode = mpesa,
                        RecordedBy = actor,
                        Settings = settings
                    };
                }
                catch (Exception ex)
                {
                    // A receipt-object build failure shouldn't be treated as
                    // a payment failure - the payment already succeeded.
                    _logger.LogError("Failed to prepare receipt view after payment", ex);
                }

                MpesaCode = "";
                PrefillAmountFromSelectedWorker();
                await RefreshBalanceDueAsync();

                CurrentPage = 1;
                await LoadAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to record payment", ex);
                StatusMessage = $"❌ Failed to record payment: {UserMessage.For(ex)}";
            }
            finally
            {
                IsLoading = false;
            }
        }
    }

    #endregion

    #region Dashboard ViewModel

    /// <summary>
    /// Overview/landing page - summary stats pulled from the existing
    /// worker and payroll services (no new data-access code needed).
    /// </summary>
    public class ChartPoint
    {
        public string Label { get; set; } = "";
        public decimal Value { get; set; }
        /// <summary>Precomputed bar height in pixels (0-120), so the XAML
        /// can bind directly without needing a value converter that would
        /// otherwise need cross-item awareness of the max value.</summary>
        public double BarHeight { get; set; }
    }

    public class DashboardViewModel : ViewModelBase
    {
        private readonly IWorkerService _workerService;
        private readonly IPayrollService _payrollService;
        private readonly ITimeRecordService _timeRecordService;
        private readonly IAuditService _auditService;
        private readonly ILogger _logger;

        private const double MaxBarHeight = 120;

        private int _totalWorkers;
        private int _activeWorkers;
        private int _workersPresent;
        private int _workersAbsent;
        private int _workersClockedIn;
        private int _workersClockedOut;
        private decimal _todayPayroll;
        private decimal _weeklyPayroll;
        private decimal _monthlyPayroll;
        private string _currentMonthLabel = "";
        private bool _isLoading;
        private string _statusMessage = "";
        private ObservableCollection<AuditEntry> _recentActivities = new();
        private ObservableCollection<ChartPoint> _weeklyAttendanceChart = new();
        private ObservableCollection<ChartPoint> _payrollTrendChart = new();

        public int TotalWorkers
        {
            get => _totalWorkers;
            set => SetProperty(ref _totalWorkers, value, nameof(TotalWorkers));
        }

        public int ActiveWorkers
        {
            get => _activeWorkers;
            set => SetProperty(ref _activeWorkers, value, nameof(ActiveWorkers));
        }

        public int WorkersPresent
        {
            get => _workersPresent;
            set => SetProperty(ref _workersPresent, value, nameof(WorkersPresent));
        }

        public int WorkersAbsent
        {
            get => _workersAbsent;
            set => SetProperty(ref _workersAbsent, value, nameof(WorkersAbsent));
        }

        public int WorkersClockedIn
        {
            get => _workersClockedIn;
            set => SetProperty(ref _workersClockedIn, value, nameof(WorkersClockedIn));
        }

        public int WorkersClockedOut
        {
            get => _workersClockedOut;
            set => SetProperty(ref _workersClockedOut, value, nameof(WorkersClockedOut));
        }

        public decimal TodayPayroll
        {
            get => _todayPayroll;
            set => SetProperty(ref _todayPayroll, value, nameof(TodayPayroll));
        }

        public decimal WeeklyPayroll
        {
            get => _weeklyPayroll;
            set => SetProperty(ref _weeklyPayroll, value, nameof(WeeklyPayroll));
        }

        public decimal MonthlyPayroll
        {
            get => _monthlyPayroll;
            set => SetProperty(ref _monthlyPayroll, value, nameof(MonthlyPayroll));
        }

        public string CurrentMonthLabel
        {
            get => _currentMonthLabel;
            set => SetProperty(ref _currentMonthLabel, value, nameof(CurrentMonthLabel));
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value, nameof(IsLoading));
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value, nameof(StatusMessage));
        }

        public ObservableCollection<AuditEntry> RecentActivities
        {
            get => _recentActivities;
            set => SetProperty(ref _recentActivities, value, nameof(RecentActivities));
        }

        public ObservableCollection<ChartPoint> WeeklyAttendanceChart
        {
            get => _weeklyAttendanceChart;
            set => SetProperty(ref _weeklyAttendanceChart, value, nameof(WeeklyAttendanceChart));
        }

        public ObservableCollection<ChartPoint> PayrollTrendChart
        {
            get => _payrollTrendChart;
            set => SetProperty(ref _payrollTrendChart, value, nameof(PayrollTrendChart));
        }

        public ICommand RefreshCommand { get; }

        public DashboardViewModel(
            IWorkerService workerService,
            IPayrollService payrollService,
            ITimeRecordService timeRecordService,
            IAuditService auditService,
            ILogger logger)
        {
            _workerService = workerService;
            _payrollService = payrollService;
            _timeRecordService = timeRecordService;
            _auditService = auditService;
            _logger = logger;

            RefreshCommand = new AsyncRelayCommand(LoadAsync);
        }

        public async Task InitializeAsync()
        {
            await LoadAsync();
        }

        private async Task LoadAsync()
        {
            try
            {
                IsLoading = true;

                var allWorkers = (await _workerService.GetAllAsync()).ToList();
                TotalWorkers = allWorkers.Count;
                ActiveWorkers = allWorkers.Count(w => w.Status == "Active");

                // Today's attendance breakdown
                var todayRecords = (await _timeRecordService.GetTodayRecordsAsync()).ToList();
                WorkersClockedIn = todayRecords.Count(r => r.ClockOutTime == null);
                WorkersClockedOut = todayRecords.Count(r => r.ClockOutTime != null);
                WorkersPresent = WorkersClockedIn + WorkersClockedOut;
                WorkersAbsent = Math.Max(0, ActiveWorkers - WorkersPresent);

                // Today / Weekly / Monthly payroll
                var now = DateTime.Now;
                var todayEntries = await _payrollService.CalculateAsync(now.Date, now.Date);
                TodayPayroll = todayEntries.Sum(e => e.GrossEarnings);

                var startOfWeek = now.Date.AddDays(-(int)now.DayOfWeek);
                var weekEntries = await _payrollService.CalculateAsync(startOfWeek, now.Date);
                WeeklyPayroll = weekEntries.Sum(e => e.GrossEarnings);

                var (from, to, label) = await _payrollService.GetMonthRangeAsync(now.Year, now.Month);
                var monthEntries = await _payrollService.CalculateAsync(from, to);
                MonthlyPayroll = monthEntries.Sum(e => e.GrossEarnings);
                CurrentMonthLabel = label;

                // Recent activity feed (last 8 audit entries)
                var recent = await _auditService.GetPagedAsync(1, 8);
                RecentActivities = new ObservableCollection<AuditEntry>(recent);

                await LoadWeeklyAttendanceChartAsync(now);
                await LoadPayrollTrendChartAsync(now);

                StatusMessage = "";
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to load dashboard", ex);
                StatusMessage = $"Failed to load dashboard: {UserMessage.For(ex)}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task LoadWeeklyAttendanceChartAsync(DateTime now)
        {
            var points = new List<ChartPoint>();
            for (int i = 6; i >= 0; i--)
            {
                var day = now.Date.AddDays(-i);
                var dayRecords = await _timeRecordService.GetRecordsForDateAsync(day);
                var distinctWorkers = dayRecords.Select(r => r.WorkerId).Distinct().Count();
                points.Add(new ChartPoint { Label = day.ToString("ddd"), Value = distinctWorkers });
            }

            var max = points.Count > 0 ? Math.Max(1, points.Max(p => p.Value)) : 1;
            foreach (var p in points)
            {
                p.BarHeight = (double)(p.Value / max) * MaxBarHeight;
                if (p.Value > 0 && p.BarHeight < 4) p.BarHeight = 4; // keep small bars visible
            }

            WeeklyAttendanceChart = new ObservableCollection<ChartPoint>(points);
        }

        private async Task LoadPayrollTrendChartAsync(DateTime now)
        {
            var points = new List<ChartPoint>();
            for (int i = 5; i >= 0; i--)
            {
                var monthDate = now.AddMonths(-i);
                var (from, to, _) = await _payrollService.GetMonthRangeAsync(monthDate.Year, monthDate.Month);
                var entries = await _payrollService.CalculateAsync(from, to);
                var total = entries.Sum(e => e.GrossEarnings);
                points.Add(new ChartPoint { Label = monthDate.ToString("MMM"), Value = total });
            }

            var max = points.Count > 0 ? Math.Max(1, points.Max(p => p.Value)) : 1;
            foreach (var p in points)
            {
                p.BarHeight = (double)(p.Value / max) * MaxBarHeight;
                if (p.Value > 0 && p.BarHeight < 4) p.BarHeight = 4;
            }

            PayrollTrendChart = new ObservableCollection<ChartPoint>(points);
        }
    }

    #endregion

    #region Settings ViewModel

    /// <summary>
    /// Company/app preferences (name, currency, default wage rate) plus the
    /// dark-mode toggle. Persisted as JSON via ISettingsService - deliberately
    /// independent of the SQLite schema.
    /// </summary>
    /// <summary>Display-friendly wrapper around BackupFileInfo for the
    /// restore dropdown.</summary>
    public class BackupListItem
    {
        public BackupFileInfo Info { get; }
        public string DisplayLabel { get; }

        public BackupListItem(BackupFileInfo info)
        {
            Info = info;
            var sizeMb = info.SizeBytes / 1024.0 / 1024.0;
            var tag = info.FileName.Contains("_PreRestore") ? "  (pre-restore snapshot)" : "";
            DisplayLabel = $"{info.CreatedAt:yyyy-MM-dd HH:mm:ss}  •  {sizeMb:N2} MB{tag}";
        }
    }

    public class SettingsViewModel : ViewModelBase
    {
        private readonly ISettingsService _settingsService;
        private readonly ILogger _logger;
        private readonly IBackupService _backupService;
        private readonly IUserService _userService;
        private readonly ICurrentUserContext _currentUser;
        private readonly IAppLifecycleService _appLifecycle;
        private readonly IAuthorizationService _authorization;

        private string _companyName = "";
        private string _currency = "KES";
        private string _defaultDailyWageRate = "";
        private bool _isDarkMode;
        private string _statusMessage = "";

        private string _companyContactPerson = "";
        private string _companyPhone = "";
        private string _companyEmail = "";
        private string _companyAddress = "";
        private string _companyRegistrationNumber = "";
        private string _companyWebsite = "";

        private string _maxFailedLoginAttempts = "5";
        private string _lockoutDurationMinutes = "15";
        private string _sessionIdleTimeoutMinutes = "15";

        private string _currentPassword = "";
        private string _newPassword = "";
        private string _confirmNewPassword = "";
        private int _newPasswordStrength;
        private string _passwordChangeMessage = "";

        private ObservableCollection<BackupListItem> _availableBackups = new();
        private BackupListItem? _selectedBackup;

        private ObservableCollection<string> _jobRoles = new();
        private string _newJobRoleText = "";

        public ObservableCollection<string> JobRoles
        {
            get => _jobRoles;
            set => SetProperty(ref _jobRoles, value, nameof(JobRoles));
        }

        public string NewJobRoleText
        {
            get => _newJobRoleText;
            set => SetProperty(ref _newJobRoleText, value, nameof(NewJobRoleText));
        }

        public string CompanyName
        {
            get => _companyName;
            set => SetProperty(ref _companyName, value, nameof(CompanyName));
        }

        public string Currency
        {
            get => _currency;
            set => SetProperty(ref _currency, value, nameof(Currency));
        }

        public string DefaultDailyWageRate
        {
            get => _defaultDailyWageRate;
            set => SetProperty(ref _defaultDailyWageRate, value, nameof(DefaultDailyWageRate));
        }

        public string CompanyContactPerson
        {
            get => _companyContactPerson;
            set => SetProperty(ref _companyContactPerson, value, nameof(CompanyContactPerson));
        }

        public string CompanyPhone
        {
            get => _companyPhone;
            set => SetProperty(ref _companyPhone, value, nameof(CompanyPhone));
        }

        public string CompanyEmail
        {
            get => _companyEmail;
            set => SetProperty(ref _companyEmail, value, nameof(CompanyEmail));
        }

        public string CompanyAddress
        {
            get => _companyAddress;
            set => SetProperty(ref _companyAddress, value, nameof(CompanyAddress));
        }

        public string CompanyRegistrationNumber
        {
            get => _companyRegistrationNumber;
            set => SetProperty(ref _companyRegistrationNumber, value, nameof(CompanyRegistrationNumber));
        }

        public string CompanyWebsite
        {
            get => _companyWebsite;
            set => SetProperty(ref _companyWebsite, value, nameof(CompanyWebsite));
        }

        public bool IsDarkMode
        {
            get => _isDarkMode;
            set
            {
                SetProperty(ref _isDarkMode, value, nameof(IsDarkMode));
                // Apply immediately so the toggle gives instant visual feedback;
                // SaveCommand persists it to disk for next launch.
                ThemeManager.ApplyTheme(value);
            }
        }

        public string MaxFailedLoginAttempts
        {
            get => _maxFailedLoginAttempts;
            set => SetProperty(ref _maxFailedLoginAttempts, value, nameof(MaxFailedLoginAttempts));
        }

        public string LockoutDurationMinutes
        {
            get => _lockoutDurationMinutes;
            set => SetProperty(ref _lockoutDurationMinutes, value, nameof(LockoutDurationMinutes));
        }

        public string SessionIdleTimeoutMinutes
        {
            get => _sessionIdleTimeoutMinutes;
            set => SetProperty(ref _sessionIdleTimeoutMinutes, value, nameof(SessionIdleTimeoutMinutes));
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value, nameof(StatusMessage));
        }

        public string BackupFolder => _backupService.BackupFolder;

        public string LoggedInUserDisplay => _currentUser.IsAuthenticated ? $"{_currentUser.FullName} ({_currentUser.Username})" : "";

        public bool CanChangeSystemSettings => _authorization.HasPermission(AppPermission.ChangeSystemSettings);
        public bool CanManageBackups => _authorization.HasPermission(AppPermission.ManageBackups);
        public bool CanRestoreBackups => _authorization.HasPermission(AppPermission.RestoreBackups);

        public ObservableCollection<BackupListItem> AvailableBackups
        {
            get => _availableBackups;
            set => SetProperty(ref _availableBackups, value, nameof(AvailableBackups));
        }

        public BackupListItem? SelectedBackup
        {
            get => _selectedBackup;
            set => SetProperty(ref _selectedBackup, value, nameof(SelectedBackup));
        }

        public string SignedInSince => _currentUser.LoginTime.HasValue
            ? _currentUser.LoginTime.Value.ToString("dddd, MMM d 'at' h:mm tt")
            : "";

        public string CurrentPassword
        {
            get => _currentPassword;
            set => SetProperty(ref _currentPassword, value, nameof(CurrentPassword));
        }

        public string NewPassword
        {
            get => _newPassword;
            set => SetProperty(ref _newPassword, value, nameof(NewPassword));
        }

        public string ConfirmNewPassword
        {
            get => _confirmNewPassword;
            set => SetProperty(ref _confirmNewPassword, value, nameof(ConfirmNewPassword));
        }

        public int NewPasswordStrength
        {
            get => _newPasswordStrength;
            set => SetProperty(ref _newPasswordStrength, value, nameof(NewPasswordStrength));
        }

        public string PasswordChangeMessage
        {
            get => _passwordChangeMessage;
            set => SetProperty(ref _passwordChangeMessage, value, nameof(PasswordChangeMessage));
        }

        public ICommand SaveCommand { get; }
        public ICommand BackupNowCommand { get; }
        public ICommand OpenBackupFolderCommand { get; }
        public ICommand RefreshBackupsCommand { get; }
        public ICommand RestoreBackupCommand { get; }
        public ICommand ChangePasswordCommand { get; }
        public ICommand LogoutCommand { get; }
        public ICommand AddJobRoleCommand { get; }
        public ICommand RemoveJobRoleCommand { get; }

        public SettingsViewModel(
            ISettingsService settingsService,
            ILogger logger,
            IBackupService backupService,
            IUserService userService,
            ICurrentUserContext currentUser,
            IAppLifecycleService appLifecycle,
            IAuthorizationService authorization)
        {
            _settingsService = settingsService;
            _logger = logger;
            _backupService = backupService;
            _userService = userService;
            _currentUser = currentUser;
            _appLifecycle = appLifecycle;
            _authorization = authorization;

            SaveCommand = new AsyncRelayCommand(SaveAsync);
            BackupNowCommand = new AsyncRelayCommand(BackupNowAsync);
            OpenBackupFolderCommand = new RelayCommand(_ => OpenBackupFolder());
            RefreshBackupsCommand = new AsyncRelayCommand(RefreshBackupsAsync);
            RestoreBackupCommand = new AsyncRelayCommand(RestoreBackupAsync, () => CanRestoreBackups && SelectedBackup != null);
            ChangePasswordCommand = new AsyncRelayCommand(ChangePasswordAsync);
            LogoutCommand = new RelayCommand(_ => _appLifecycle.RequestLogout());
            AddJobRoleCommand = new RelayCommand(_ => AddJobRole(), _ => CanChangeSystemSettings && !string.IsNullOrWhiteSpace(NewJobRoleText));
            RemoveJobRoleCommand = new RelayCommand(role => RemoveJobRole(role as string), _ => CanChangeSystemSettings);
        }

        private void AddJobRole()
        {
            var role = NewJobRoleText.Trim();
            if (string.IsNullOrWhiteSpace(role)) return;

            if (JobRoles.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase)))
            {
                StatusMessage = $"'{role}' is already in the list.";
                return;
            }

            JobRoles.Add(role);
            JobRoles = new ObservableCollection<string>(JobRoles.OrderBy(r => r));
            NewJobRoleText = "";
            StatusMessage = $"Added '{role}'. Click Save Settings to keep this change.";
        }

        private void RemoveJobRole(string? role)
        {
            if (string.IsNullOrWhiteSpace(role)) return;

            JobRoles = new ObservableCollection<string>(JobRoles.Where(r => r != role));
            StatusMessage = $"Removed '{role}'. Click Save Settings to keep this change.";
        }

        private async Task BackupNowAsync()
        {
            try
            {
                StatusMessage = "Backing up...";
                var path = await _backupService.BackupNowAsync();
                StatusMessage = path != null
                    ? $"✅ Backup created: {System.IO.Path.GetFileName(path)}"
                    : "❌ Backup failed. Check the log for details.";
            }
            catch (Exception ex)
            {
                _logger.LogError("Manual backup failed", ex);
                StatusMessage = $"❌ Backup failed: {UserMessage.For(ex)}";
            }
        }

        private void OpenBackupFolder()
        {
            try
            {
                System.IO.Directory.CreateDirectory(_backupService.BackupFolder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _backupService.BackupFolder,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to open backup folder", ex);
                StatusMessage = $"❌ Could not open backup folder: {UserMessage.For(ex)}";
            }
        }

        private async Task RefreshBackupsAsync()
        {
            try
            {
                var backups = await _backupService.ListBackupsAsync();
                AvailableBackups = new ObservableCollection<BackupListItem>(backups.Select(b => new BackupListItem(b)));

                // Keep the current selection if it's still in the list;
                // otherwise default to the most recent backup.
                if (SelectedBackup != null)
                {
                    SelectedBackup = AvailableBackups.FirstOrDefault(b => b.Info.FileName == SelectedBackup.Info.FileName);
                }
                if (SelectedBackup == null)
                {
                    SelectedBackup = AvailableBackups.FirstOrDefault();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to list backups", ex);
                StatusMessage = $"❌ Could not load backup list: {UserMessage.For(ex)}";
            }
        }

        private async Task RestoreBackupAsync()
        {
            if (SelectedBackup == null)
            {
                StatusMessage = "Choose a backup to restore first.";
                return;
            }

            // A destructive, irreversible-feeling action deserves an explicit
            // confirmation in addition to the permission check the service
            // itself enforces.
            var confirm = MessageBox.Show(
                $"This replaces the current database with the backup from {SelectedBackup.Info.CreatedAt:yyyy-MM-dd HH:mm:ss}.\n\n" +
                "A safety snapshot of the current data will be taken first. Continue?",
                "Confirm Restore",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                StatusMessage = "Restoring...";
                var (success, message) = await _backupService.RestoreAsync(SelectedBackup.Info.FileName);
                StatusMessage = (success ? "✅ " : "❌ ") + message;

                if (success)
                {
                    await RefreshBackupsAsync();

                    // The app must restart for this to be safe: everything
                    // currently in memory - the signed-in user's own record,
                    // every loaded worker/payroll/payment list, cached
                    // settings - could all now be stale or reference data
                    // that no longer exists in the restored database.
                    var restart = MessageBox.Show(
                        "The database was restored successfully. The application needs to restart to load the restored data safely - continuing without restarting could show stale or mismatched information.\n\nRestart now?",
                        "Restart Required",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);

                    if (restart == MessageBoxResult.Yes)
                    {
                        _appLifecycle.RequestRestart();
                    }
                    else
                    {
                        StatusMessage += " Restart the application when you're ready for the restored data to take effect.";
                    }
                }
            }
            catch (AuthorizationException ex)
            {
                StatusMessage = $"❌ {UserMessage.For(ex)}";
            }
            catch (Exception ex)
            {
                _logger.LogError("Restore failed", ex);
                StatusMessage = $"❌ Restore failed: {UserMessage.For(ex)}";
            }
        }

        public async Task InitializeAsync()
        {
            try
            {
                var settings = await _settingsService.LoadAsync();
                _companyName = settings.CompanyName;
                _currency = settings.Currency;
                _defaultDailyWageRate = settings.DefaultDailyWageRate.ToString();
                _isDarkMode = settings.IsDarkMode;
                _companyContactPerson = settings.CompanyContactPerson;
                _companyPhone = settings.CompanyPhone;
                _companyEmail = settings.CompanyEmail;
                _companyAddress = settings.CompanyAddress;
                _companyRegistrationNumber = settings.CompanyRegistrationNumber;
                _companyWebsite = settings.CompanyWebsite;
                _maxFailedLoginAttempts = settings.MaxFailedLoginAttempts.ToString();
                _lockoutDurationMinutes = settings.LockoutDurationMinutes.ToString();
                _sessionIdleTimeoutMinutes = settings.SessionIdleTimeoutMinutes.ToString();
                JobRoles = new ObservableCollection<string>(settings.JobRoles.OrderBy(r => r));

                OnPropertyChanged(nameof(CompanyName));
                OnPropertyChanged(nameof(Currency));
                OnPropertyChanged(nameof(DefaultDailyWageRate));
                OnPropertyChanged(nameof(IsDarkMode));
                OnPropertyChanged(nameof(CompanyContactPerson));
                OnPropertyChanged(nameof(CompanyPhone));
                OnPropertyChanged(nameof(CompanyEmail));
                OnPropertyChanged(nameof(CompanyAddress));
                OnPropertyChanged(nameof(CompanyRegistrationNumber));
                OnPropertyChanged(nameof(CompanyWebsite));
                OnPropertyChanged(nameof(MaxFailedLoginAttempts));
                OnPropertyChanged(nameof(LockoutDurationMinutes));
                OnPropertyChanged(nameof(SessionIdleTimeoutMinutes));
                OnPropertyChanged(nameof(LoggedInUserDisplay));
                OnPropertyChanged(nameof(SignedInSince));

                if (CanRestoreBackups)
                {
                    await RefreshBackupsAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to load settings", ex);
                StatusMessage = $"Failed to load settings: {UserMessage.For(ex)}";
            }
        }

        private async Task SaveAsync()
        {
            try
            {
                if (!decimal.TryParse(DefaultDailyWageRate, out var rate) || rate <= 0)
                {
                    StatusMessage = "Default daily wage rate must be a number greater than 0.";
                    return;
                }

                if (!int.TryParse(MaxFailedLoginAttempts, out var maxAttempts) || maxAttempts < 1)
                {
                    StatusMessage = "Max failed login attempts must be a whole number of 1 or more.";
                    return;
                }

                if (!int.TryParse(LockoutDurationMinutes, out var lockoutMinutes) || lockoutMinutes < 1)
                {
                    StatusMessage = "Lockout duration must be a whole number of 1 or more.";
                    return;
                }

                if (!int.TryParse(SessionIdleTimeoutMinutes, out var idleMinutes) || idleMinutes < 1)
                {
                    StatusMessage = "Session idle timeout must be a whole number of 1 or more.";
                    return;
                }

                if (!string.IsNullOrWhiteSpace(CompanyEmail) && !System.Text.RegularExpressions.Regex.IsMatch(CompanyEmail.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
                {
                    StatusMessage = "Company email doesn't look like a valid email address.";
                    return;
                }

                var settings = new AppSettingsData
                {
                    CompanyName = string.IsNullOrWhiteSpace(CompanyName) ? "My Company" : CompanyName.Trim(),
                    Currency = string.IsNullOrWhiteSpace(Currency) ? "KES" : Currency.Trim(),
                    DefaultDailyWageRate = rate,
                    IsDarkMode = IsDarkMode,
                    CompanyContactPerson = (CompanyContactPerson ?? "").Trim(),
                    CompanyPhone = (CompanyPhone ?? "").Trim(),
                    CompanyEmail = (CompanyEmail ?? "").Trim(),
                    CompanyAddress = (CompanyAddress ?? "").Trim(),
                    CompanyRegistrationNumber = (CompanyRegistrationNumber ?? "").Trim(),
                    CompanyWebsite = (CompanyWebsite ?? "").Trim(),
                    MaxFailedLoginAttempts = maxAttempts,
                    LockoutDurationMinutes = lockoutMinutes,
                    SessionIdleTimeoutMinutes = idleMinutes,
                    JobRoles = JobRoles.ToList()
                };

                await _settingsService.SaveAsync(settings);
                StatusMessage = "✅ Settings saved.";
                _logger.LogInfo("Settings saved");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to save settings", ex);
                StatusMessage = $"❌ Failed to save settings: {UserMessage.For(ex)}";
            }
        }

        private async Task ChangePasswordAsync()
        {
            PasswordChangeMessage = "";

            if (!_currentUser.UserId.HasValue)
            {
                PasswordChangeMessage = "❌ Session error - please sign in again.";
                return;
            }

            if (string.IsNullOrEmpty(CurrentPassword))
            {
                PasswordChangeMessage = "❌ Enter your current password.";
                return;
            }

            if (NewPassword != ConfirmNewPassword)
            {
                PasswordChangeMessage = "❌ New passwords do not match.";
                return;
            }

            try
            {
                // Verify the current password before allowing a change, even
                // though the user is already signed in - this stops someone
                // who walked up to an unlocked, already-logged-in session
                // from silently taking over the account.
                var verify = await _userService.AuthenticateAsync(_currentUser.Username!, CurrentPassword);
                if (verify.Outcome != AuthOutcome.Success && verify.Outcome != AuthOutcome.PasswordExpired)
                {
                    PasswordChangeMessage = "❌ Current password is incorrect.";
                    return;
                }

                var (success, message) = await _userService.ChangePasswordAsync(_currentUser.UserId.Value, NewPassword);
                PasswordChangeMessage = (success ? "✅ " : "❌ ") + message;

                if (success)
                {
                    CurrentPassword = "";
                    NewPassword = "";
                    ConfirmNewPassword = "";
                    NewPasswordStrength = 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to change password", ex);
                PasswordChangeMessage = $"❌ Failed to change password: {UserMessage.For(ex)}";
            }
        }
    }

    #endregion

    #region Audit Log ViewModel

    /// <summary>
    /// Read-only view over the AuditLog table populated by IAuditService.
    /// </summary>
    public class AuditLogViewModel : ViewModelBase
    {
        private readonly IAuditService _auditService;
        private readonly ILogger _logger;
        private readonly int _pageSize = 50;

        private ObservableCollection<AuditEntry> _entries = new();
        private bool _isLoading;
        private string _statusMessage = "";
        private int _currentPage = 1;
        private int _totalPages = 1;

        public ObservableCollection<AuditEntry> Entries
        {
            get => _entries;
            set => SetProperty(ref _entries, value, nameof(Entries));
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value, nameof(IsLoading));
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value, nameof(StatusMessage));
        }

        public int CurrentPage
        {
            get => _currentPage;
            set => SetProperty(ref _currentPage, value, nameof(CurrentPage));
        }

        public int TotalPages
        {
            get => _totalPages;
            set => SetProperty(ref _totalPages, value, nameof(TotalPages));
        }

        public ICommand RefreshCommand { get; }
        public ICommand NextPageCommand { get; }
        public ICommand PreviousPageCommand { get; }

        public AuditLogViewModel(IAuditService auditService, ILogger logger)
        {
            _auditService = auditService;
            _logger = logger;

            RefreshCommand = new AsyncRelayCommand(LoadAsync);
            NextPageCommand = new RelayCommand(_ => NextPage(), _ => CurrentPage < TotalPages);
            PreviousPageCommand = new RelayCommand(_ => PreviousPage(), _ => CurrentPage > 1);
        }

        public async Task InitializeAsync()
        {
            CurrentPage = 1;
            await LoadAsync();
        }

        private void NextPage()
        {
            if (CurrentPage < TotalPages)
            {
                CurrentPage++;
                _ = LoadAsync();
            }
        }

        private void PreviousPage()
        {
            if (CurrentPage > 1)
            {
                CurrentPage--;
                _ = LoadAsync();
            }
        }

        private async Task LoadAsync()
        {
            try
            {
                IsLoading = true;

                var count = await _auditService.CountAsync();
                TotalPages = Math.Max(1, (count + _pageSize - 1) / _pageSize);
                if (CurrentPage > TotalPages) CurrentPage = TotalPages;

                var entries = await _auditService.GetPagedAsync(CurrentPage, _pageSize);
                Entries = new ObservableCollection<AuditEntry>(entries);
                StatusMessage = "";
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to load activity log", ex);
                StatusMessage = $"Failed to load activity log: {UserMessage.For(ex)}";
            }
            finally
            {
                IsLoading = false;
            }
        }
    }

    #endregion

    #region User Management ViewModel

    /// <summary>
    /// List/create/deactivate user accounts. Every logged-in user currently
    /// has full access regardless of Role (Role is enforced starting in the
    /// RBAC phase) - this page just makes account administration possible
    /// from within the app instead of only at first-run setup.
    /// </summary>
    public class UserManagementViewModel : ViewModelBase
    {
        private readonly IUserService _userService;
        private readonly IAuditService _auditService;
        private readonly ICurrentUserContext _currentUser;
        private readonly ILogger _logger;

        private ObservableCollection<AppUser> _users = new();
        private AppUser? _selectedUser;
        private bool _isLoading;
        private string _statusMessage = "";

        private string _newUsername = "";
        private string _newPassword = "";
        private string _newConfirmPassword = "";
        private string _newFullName = "";
        private string _newRole = AppRoles.Clerk;
        private int _newPasswordStrength;

        private string _newQuestion1 = LoginViewModel.AvailableSecurityQuestions[0];
        private string _newAnswer1 = "";
        private string _newQuestion2 = LoginViewModel.AvailableSecurityQuestions[1];
        private string _newAnswer2 = "";
        private string _newQuestion3 = LoginViewModel.AvailableSecurityQuestions[2];
        private string _newAnswer3 = "";

        public ObservableCollection<AppUser> Users
        {
            get => _users;
            set => SetProperty(ref _users, value, nameof(Users));
        }

        public AppUser? SelectedUser
        {
            get => _selectedUser;
            set => SetProperty(ref _selectedUser, value, nameof(SelectedUser));
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value, nameof(IsLoading));
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value, nameof(StatusMessage));
        }

        public string NewUsername
        {
            get => _newUsername;
            set => SetProperty(ref _newUsername, value, nameof(NewUsername));
        }

        // Not bound directly in XAML - PasswordBox.Password is deliberately
        // not a bindable dependency property. MainWindow.xaml.cs sets these
        // from PasswordChanged event handlers instead.
        public string NewPassword
        {
            get => _newPassword;
            set => SetProperty(ref _newPassword, value, nameof(NewPassword));
        }

        public string NewConfirmPassword
        {
            get => _newConfirmPassword;
            set => SetProperty(ref _newConfirmPassword, value, nameof(NewConfirmPassword));
        }

        public string NewFullName
        {
            get => _newFullName;
            set => SetProperty(ref _newFullName, value, nameof(NewFullName));
        }

        public string NewRole
        {
            get => _newRole;
            set => SetProperty(ref _newRole, value, nameof(NewRole));
        }

        public int NewPasswordStrength
        {
            get => _newPasswordStrength;
            set => SetProperty(ref _newPasswordStrength, value, nameof(NewPasswordStrength));
        }

        public IReadOnlyList<string> AvailableSecurityQuestions => LoginViewModel.AvailableSecurityQuestions;

        public string NewQuestion1
        {
            get => _newQuestion1;
            set => SetProperty(ref _newQuestion1, value, nameof(NewQuestion1));
        }

        public string NewAnswer1
        {
            get => _newAnswer1;
            set => SetProperty(ref _newAnswer1, value, nameof(NewAnswer1));
        }

        public string NewQuestion2
        {
            get => _newQuestion2;
            set => SetProperty(ref _newQuestion2, value, nameof(NewQuestion2));
        }

        public string NewAnswer2
        {
            get => _newAnswer2;
            set => SetProperty(ref _newAnswer2, value, nameof(NewAnswer2));
        }

        public string NewQuestion3
        {
            get => _newQuestion3;
            set => SetProperty(ref _newQuestion3, value, nameof(NewQuestion3));
        }

        public string NewAnswer3
        {
            get => _newAnswer3;
            set => SetProperty(ref _newAnswer3, value, nameof(NewAnswer3));
        }

        public ICommand LoadUsersCommand { get; }
        public ICommand CreateUserCommand { get; }
        public ICommand DeactivateUserCommand { get; }
        public ICommand ReactivateUserCommand { get; }

        public UserManagementViewModel(
            IUserService userService,
            IAuditService auditService,
            ICurrentUserContext currentUser,
            ILogger logger)
        {
            _userService = userService;
            _auditService = auditService;
            _currentUser = currentUser;
            _logger = logger;

            LoadUsersCommand = new AsyncRelayCommand(LoadAsync);
            CreateUserCommand = new AsyncRelayCommand(CreateUserAsync);
            DeactivateUserCommand = new AsyncRelayCommand(DeactivateSelectedAsync, () => SelectedUser != null && SelectedUser.Status == "Active");
            ReactivateUserCommand = new AsyncRelayCommand(ReactivateSelectedAsync, () => SelectedUser != null && SelectedUser.Status != "Active");
        }

        public async Task InitializeAsync()
        {
            await LoadAsync();
        }

        private async Task LoadAsync()
        {
            try
            {
                IsLoading = true;
                var users = await _userService.GetAllAsync();
                Users = new ObservableCollection<AppUser>(users);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to load users", ex);
                StatusMessage = $"Failed to load users: {UserMessage.For(ex)}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task CreateUserAsync()
        {
            if (string.IsNullOrWhiteSpace(NewUsername) || string.IsNullOrWhiteSpace(NewFullName))
            {
                StatusMessage = "Username and full name are required.";
                return;
            }

            var policy = PasswordPolicy.Validate(NewPassword);
            if (!policy.IsValid)
            {
                StatusMessage = "Password needs: " + string.Join(", ", policy.Violations);
                return;
            }

            if (NewPassword != NewConfirmPassword)
            {
                StatusMessage = "Passwords do not match.";
                return;
            }

            if (string.IsNullOrWhiteSpace(NewAnswer1) || string.IsNullOrWhiteSpace(NewAnswer2) || string.IsNullOrWhiteSpace(NewAnswer3))
            {
                StatusMessage = "Please answer all three security questions.";
                return;
            }

            if (new[] { NewQuestion1, NewQuestion2, NewQuestion3 }.Distinct().Count() != 3)
            {
                StatusMessage = "Please choose three different security questions.";
                return;
            }

            try
            {
                IsLoading = true;
                var securityQuestions = new List<(string Question, string Answer)>
                {
                    (NewQuestion1, NewAnswer1),
                    (NewQuestion2, NewAnswer2),
                    (NewQuestion3, NewAnswer3)
                };
                var newId = await _userService.CreateAsync(NewUsername.Trim(), NewPassword, NewFullName.Trim(), NewRole, securityQuestions);
                await _auditService.LogAsync("Create", "Users", newId.ToString(), _currentUser.UserId, null, $"{NewFullName.Trim()} ({NewRole})");

                StatusMessage = $"✅ User '{NewUsername.Trim()}' created.";

                NewUsername = "";
                NewPassword = "";
                NewConfirmPassword = "";
                NewFullName = "";
                NewRole = AppRoles.Clerk;
                NewAnswer1 = "";
                NewAnswer2 = "";
                NewAnswer3 = "";

                await LoadAsync();
            }
            catch (InvalidOperationException ex)
            {
                // Friendly message from UserService (e.g. duplicate username)
                StatusMessage = $"❌ {UserMessage.For(ex)}";
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to create user", ex);
                StatusMessage = $"❌ Failed to create user: {UserMessage.For(ex)}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task DeactivateSelectedAsync()
        {
            if (SelectedUser == null) return;

            if (SelectedUser.UserId == _currentUser.UserId)
            {
                StatusMessage = "You can't deactivate the account you're currently signed in with.";
                return;
            }

            var confirm = MessageBox.Show(
                $"Deactivate the account '{SelectedUser.Username}'? They won't be able to sign in until reactivated.",
                "Confirm Deactivation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                IsLoading = true;
                await _userService.DeactivateAsync(SelectedUser.UserId);
                await _auditService.LogAsync("Deactivate", "Users", SelectedUser.UserId.ToString(), _currentUser.UserId, "Active", "Inactive");
                StatusMessage = $"User '{SelectedUser.Username}' deactivated.";
                await LoadAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to deactivate user", ex);
                StatusMessage = $"Failed to deactivate user: {UserMessage.For(ex)}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task ReactivateSelectedAsync()
        {
            if (SelectedUser == null) return;

            try
            {
                IsLoading = true;
                await _userService.ReactivateAsync(SelectedUser.UserId);
                await _auditService.LogAsync("Reactivate", "Users", SelectedUser.UserId.ToString(), _currentUser.UserId, "Inactive", "Active");
                StatusMessage = $"User '{SelectedUser.Username}' reactivated.";
                await LoadAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to reactivate user", ex);
                StatusMessage = $"Failed to reactivate user: {UserMessage.For(ex)}";
            }
            finally
            {
                IsLoading = false;
            }
        }
    }

    #endregion

    #region Developers ViewModel

    /// <summary>
    /// Simple, static "About / Developers" page. No permission gate - it's
    /// attribution and support-contact information, not sensitive data, so
    /// every role can see it. Deliberately sourced from AppBranding rather
    /// than hardcoding the details a second time here, so the sidebar page,
    /// the export footer watermark, and this page can never drift apart.
    /// </summary>
    public class DevelopersViewModel : ViewModelBase
    {
        private readonly ILogger _logger;

        public string AppName => AppBranding.WatermarkShort;
        public string PrimaryEmail => AppBranding.SupportEmail;
        public string PrimaryPhone => AppBranding.SupportPhone;
        public string MarketerEmail => AppBranding.MarketerEmail;
        public string MarketerPhone => AppBranding.MarketerPhone;
        public string CopyrightLine => $"\u00a9 {DateTime.Now.Year} {AppBranding.WatermarkShort}. All rights reserved.";

        public ICommand EmailPrimaryCommand { get; }
        public ICommand WhatsAppPrimaryCommand { get; }
        public ICommand CallPrimaryCommand { get; }
        public ICommand EmailMarketerCommand { get; }
        public ICommand WhatsAppMarketerCommand { get; }
        public ICommand CallMarketerCommand { get; }

        public DevelopersViewModel(ILogger logger)
        {
            _logger = logger;

            EmailPrimaryCommand = new RelayCommand(_ => OpenLink($"mailto:{PrimaryEmail}"));
            WhatsAppPrimaryCommand = new RelayCommand(_ => OpenLink($"https://wa.me/254{PrimaryPhone.TrimStart('0')}"));
            CallPrimaryCommand = new RelayCommand(_ => OpenLink($"tel:{PrimaryPhone}"));
            EmailMarketerCommand = new RelayCommand(_ => OpenLink($"mailto:{MarketerEmail}"));
            WhatsAppMarketerCommand = new RelayCommand(_ => OpenLink($"https://wa.me/254{MarketerPhone.TrimStart('0')}"));
            CallMarketerCommand = new RelayCommand(_ => OpenLink($"tel:{MarketerPhone}"));
        }

        private void OpenLink(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                // Not opening a mail/WhatsApp/dialer app isn't something the
                // person needs an error dialog for - the email and phone
                // number are printed right there on the page either way.
                _logger.LogWarning($"Could not open link '{url}': {ex.Message}");
            }
        }
    }

    #endregion
}


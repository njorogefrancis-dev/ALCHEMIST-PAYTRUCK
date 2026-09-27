using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace AlchemistPayTrack.Infrastructure
{
    /// <summary>
    /// Plain data holder for user-configurable application settings.
    /// Persisted as JSON under %AppData%\AlchemistPayTrack\settings.json.
    /// </summary>
    public class AppSettingsData
    {
        public string CompanyName { get; set; } = "My Company";
        public string Currency { get; set; } = "KES";
        public decimal DefaultDailyWageRate { get; set; } = 500m;
        public bool IsDarkMode { get; set; } = false;

        // Company details - shown on payslips/reports and used as the
        // organization's contact info within the app. All optional
        // (empty string default) since not every company will fill in
        // every field, and blank fields are simply omitted from exports.
        public string CompanyContactPerson { get; set; } = "";
        public string CompanyPhone { get; set; } = "";
        public string CompanyEmail { get; set; } = "";
        public string CompanyAddress { get; set; } = "";
        public string CompanyRegistrationNumber { get; set; } = "";
        public string CompanyWebsite { get; set; } = "";

        // Security policy (configurable so an administrator can tune it
        // without a code change; sensible defaults are applied otherwise)
        public int MaxFailedLoginAttempts { get; set; } = 5;
        public int LockoutDurationMinutes { get; set; } = 15;
        public int SessionIdleTimeoutMinutes { get; set; } = 15;

        // The fixed list of job roles offered when registering or editing a
        // worker. Kept here (not free text on the worker form) so every
        // worker's role is chosen from the same list rather than typed by
        // hand - "Mason" and "mason" and "Masons" all being different free
        // -text values would make filtering and reporting unreliable.
        public List<string> JobRoles { get; set; } = new()
        {
            "General Laborer", "Mason", "Machine Operator", "Site Supervisor",
            "Security Guard", "Watchman", "Driver", "Carpenter", "Electrician",
            "Plumber", "Welder", "Painter", "Storekeeper", "Cook", "Cleaner",
            "Foreman", "Steel Fixer", "Tile Fitter", "Groundsman", "Loader"
        };
    }

    public interface ISettingsService
    {
        Task<AppSettingsData> LoadAsync();
        Task SaveAsync(AppSettingsData settings);
    }

    /// <summary>
    /// Simple JSON file-backed settings store. Deliberately independent of
    /// SQLite/the repository layer so app preferences can be read/written
    /// even before the database is initialized.
    /// </summary>
    public class JsonSettingsService : ISettingsService
    {
        private readonly string _settingsPath;
        private readonly ILogger _logger;

        public JsonSettingsService(IAppConfiguration config, ILogger logger)
        {
            _logger = logger;
            Directory.CreateDirectory(config.AppDataPath);
            _settingsPath = Path.Combine(config.AppDataPath, "settings.json");
        }

        public async Task<AppSettingsData> LoadAsync()
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    return new AppSettingsData();
                }

                var json = await File.ReadAllTextAsync(_settingsPath);
                var settings = JsonSerializer.Deserialize<AppSettingsData>(json);
                return settings ?? new AppSettingsData();
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to load settings, using defaults", ex);
                return new AppSettingsData();
            }
        }

        public async Task SaveAsync(AppSettingsData settings)
        {
            try
            {
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_settingsPath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to save settings", ex);
            }
        }
    }
}

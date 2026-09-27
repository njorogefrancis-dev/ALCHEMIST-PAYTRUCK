using System;
using System.Linq;
using System.Windows;

namespace AlchemistPayTrack.Presentation
{
    /// <summary>
    /// Swaps the active color theme by replacing the "Theme" merged resource
    /// dictionary on the Application. Controls/styles bind to the brush keys
    /// (e.g. "PrimaryBrush") via DynamicResource, so they update immediately
    /// when the dictionary is swapped - no restart required.
    /// </summary>
    public static class ThemeManager
    {
        private static readonly Uri LightThemeUri =
            new("/AlchemistPayTrack;component/Themes/Light.xaml", UriKind.Relative);

        private static readonly Uri DarkThemeUri =
            new("/AlchemistPayTrack;component/Themes/Dark.xaml", UriKind.Relative);

        public static void ApplyTheme(bool isDarkMode)
        {
            var app = System.Windows.Application.Current;
            if (app == null) return;

            var targetUri = isDarkMode ? DarkThemeUri : LightThemeUri;
            var newDictionary = new ResourceDictionary { Source = targetUri };

            var merged = app.Resources.MergedDictionaries;

            // Remove any previously-loaded theme dictionary (identified by
            // containing the "PrimaryBrush" key, which every theme defines).
            var existing = merged.FirstOrDefault(d => d.Contains("PrimaryBrush"));
            if (existing != null)
            {
                merged.Remove(existing);
            }

            merged.Add(newDictionary);
        }
    }
}

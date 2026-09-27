using System.Windows;
using System.Windows.Controls;
using AlchemistPayTrack.Presentation;

namespace AlchemistPayTrack
{
    /// <summary>
    /// Main application window. DataContext (MainWindowViewModel) is assigned
    /// by App.xaml.cs after the DI container is built.
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        // PasswordBox.Password is deliberately not a bindable dependency
        // property (security reasons), so the User Management "create user"
        // form reads it here and pushes it into the ViewModel manually.
        private void NewUserPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm && sender is PasswordBox box)
            {
                vm.UserManagement.NewPassword = box.Password;
                vm.UserManagement.NewPasswordStrength = (int)LoginViewModel.GetStrength(box.Password);
            }
        }

        private void NewUserConfirmPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm && sender is PasswordBox box)
            {
                vm.UserManagement.NewConfirmPassword = box.Password;
            }
        }

        private void CurrentPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm && sender is PasswordBox box)
            {
                vm.Settings.CurrentPassword = box.Password;
            }
        }

        private void SettingsNewPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm && sender is PasswordBox box)
            {
                vm.Settings.NewPassword = box.Password;
                vm.Settings.NewPasswordStrength = (int)LoginViewModel.GetStrength(box.Password);
            }
        }

        private void SettingsConfirmNewPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm && sender is PasswordBox box)
            {
                vm.Settings.ConfirmNewPassword = box.Password;
            }
        }
    }
}

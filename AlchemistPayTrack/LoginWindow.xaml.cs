using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using AlchemistPayTrack.Presentation;

namespace AlchemistPayTrack
{
    public partial class LoginWindow : Window
    {
        private readonly LoginViewModel _viewModel;

        public LoginWindow(LoginViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;

            if (_viewModel.IsSetupMode)
            {
                SubtitleText.Text = "Create the admin account to get started";
            }
        }

        private async void SubmitButton_Click(object sender, RoutedEventArgs e)
        {
            SubmitButton.IsEnabled = false;
            try
            {
                bool success;

                switch (_viewModel.Step)
                {
                    case "Setup":
                        var questions = new List<(string Question, string Answer)>
                        {
                            ((string)Question1Combo.SelectedItem, Answer1Box.Text),
                            ((string)Question2Combo.SelectedItem, Answer2Box.Text),
                            ((string)Question3Combo.SelectedItem, Answer3Box.Text)
                        };
                        success = await _viewModel.TryCreateAdminAsync(
                            SetupUsernameBox.Text,
                            GetCurrentPassword(SetupPasswordBox, SetupPasswordVisibleBox),
                            SetupConfirmPasswordBox.Password,
                            SetupFullNameBox.Text,
                            questions);
                        break;

                    case "ChangeExpired":
                        success = await _viewModel.TryChangeExpiredPasswordAsync(
                            GetCurrentPassword(ExpiredNewPasswordBox, ExpiredNewPasswordVisibleBox),
                            ExpiredConfirmPasswordBox.Password);
                        break;

                    case "RecoverUsername":
                        success = await _viewModel.TryLoadRecoveryQuestionsAsync(RecoveryUsernameBox.Text);
                        return; // don't close the dialog either way - just advances the step

                    case "RecoverAnswers":
                        var answers = new List<string> { RecoveryAnswer1Box.Text, RecoveryAnswer2Box.Text, RecoveryAnswer3Box.Text };
                        success = await _viewModel.TryResetPasswordAsync(answers, RecoveryNewPasswordBox.Password, RecoveryConfirmPasswordBox.Password);
                        return; // on success this returns to the Login step rather than closing

                    default: // "Login"
                        success = await _viewModel.TryLoginAsync(UsernameBox.Text, GetCurrentPassword(PasswordBox, PasswordVisibleBox));
                        break;
                }

                if (success)
                {
                    DialogResult = true;
                    Close();
                }
            }
            finally
            {
                SubmitButton.IsEnabled = true;
            }
        }

        private void ForgotPassword_Click(object sender, RoutedEventArgs e) => _viewModel.BeginRecovery();

        private void CancelToLogin_Click(object sender, RoutedEventArgs e) => _viewModel.CancelToLogin();

        private static string GetCurrentPassword(PasswordBox pwBox, TextBox visibleBox)
            => pwBox.Visibility == Visibility.Visible ? pwBox.Password : visibleBox.Text;

        // ===== Password visibility toggles =====
        // WPF's PasswordBox deliberately can't be data-bound (security), so
        // "show password" is implemented as a PasswordBox/TextBox pair that
        // swaps Visibility and transfers the current value across at the
        // moment of toggling.

        private void ShowPasswordCheck_Changed(object sender, RoutedEventArgs e)
            => ToggleVisibility(PasswordBox, PasswordVisibleBox, ShowPasswordCheck.IsChecked == true);

        private void ShowSetupPasswordCheck_Changed(object sender, RoutedEventArgs e)
            => ToggleVisibility(SetupPasswordBox, SetupPasswordVisibleBox, ShowSetupPasswordCheck.IsChecked == true);

        private void ShowExpiredPasswordCheck_Changed(object sender, RoutedEventArgs e)
            => ToggleVisibility(ExpiredNewPasswordBox, ExpiredNewPasswordVisibleBox, ShowExpiredPasswordCheck.IsChecked == true);

        private static void ToggleVisibility(PasswordBox pwBox, TextBox visibleBox, bool show)
        {
            if (show)
            {
                visibleBox.Text = pwBox.Password;
                visibleBox.Visibility = Visibility.Visible;
                pwBox.Visibility = Visibility.Collapsed;
            }
            else
            {
                pwBox.Password = visibleBox.Text;
                pwBox.Visibility = Visibility.Visible;
                visibleBox.Visibility = Visibility.Collapsed;
            }
        }

        // ===== Strength meter updates =====

        private void SetupPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
            => _viewModel.NewPasswordStrength = (int)LoginViewModel.GetStrength(SetupPasswordBox.Password);

        private void SetupPasswordVisibleBox_TextChanged(object sender, TextChangedEventArgs e)
            => _viewModel.NewPasswordStrength = (int)LoginViewModel.GetStrength(SetupPasswordVisibleBox.Text);

        private void ExpiredNewPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
            => _viewModel.NewPasswordStrength = (int)LoginViewModel.GetStrength(ExpiredNewPasswordBox.Password);

        private void ExpiredNewPasswordVisibleBox_TextChanged(object sender, TextChangedEventArgs e)
            => _viewModel.NewPasswordStrength = (int)LoginViewModel.GetStrength(ExpiredNewPasswordVisibleBox.Text);
    }
}

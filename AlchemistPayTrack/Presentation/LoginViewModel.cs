using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AlchemistPayTrack.Application;
using AlchemistPayTrack.Infrastructure;

namespace AlchemistPayTrack.Presentation
{
    /// <summary>
    /// Backs LoginWindow through all of its steps: normal sign-in, first-run
    /// admin setup (with mandatory security questions), forgot-password
    /// recovery via those questions, and a forced password change when the
    /// current one has expired. Password fields are read directly from
    /// PasswordBox controls in code-behind (WPF's PasswordBox.Password is
    /// deliberately not a bindable dependency property) and passed into
    /// these methods as plain arguments - never stored on this ViewModel.
    /// </summary>
    public class LoginViewModel : ViewModelBase
    {
        private readonly IUserService _userService;
        private readonly ICurrentUserContext _currentUser;
        private readonly ILogger _logger;

        public static readonly IReadOnlyList<string> AvailableSecurityQuestions = new List<string>
        {
            "What was the name of your first school?",
            "What is your favorite teacher's name?",
            "What was your childhood nickname?",
            "What is your favorite food?",
            "What is your favorite place?",
            "What was your dream job as a child?",
            "What is your mother's maiden name?"
        };

        private bool _isSetupMode;
        private bool _isBusy;
        private string _errorMessage = "";
        private string _infoMessage = "";
        private string _step = "Login"; // Login, Setup, RecoverUsername, RecoverAnswers, ChangeExpired

        // Tracks the user who just authenticated but whose password has
        // expired - login isn't complete until they set a new one.
        private AppUser? _pendingExpiredUser;

        private List<string> _recoveryQuestions = new();
        private string _recoveryUsername = "";

        public bool IsSetupMode
        {
            get => _isSetupMode;
            set
            {
                SetProperty(ref _isSetupMode, value, nameof(IsSetupMode));
                Step = value ? "Setup" : "Login";
            }
        }

        public string Step
        {
            get => _step;
            set => SetProperty(ref _step, value, nameof(Step));
        }

        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value, nameof(IsBusy));
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set => SetProperty(ref _errorMessage, value, nameof(ErrorMessage));
        }

        public string InfoMessage
        {
            get => _infoMessage;
            set => SetProperty(ref _infoMessage, value, nameof(InfoMessage));
        }

        public ObservableCollection<string> RecoveryQuestions { get; } = new();

        private int _newPasswordStrength;
        public int NewPasswordStrength
        {
            get => _newPasswordStrength;
            set => SetProperty(ref _newPasswordStrength, value, nameof(NewPasswordStrength));
        }

        public LoginViewModel(IUserService userService, ICurrentUserContext currentUser, ILogger logger)
        {
            _userService = userService;
            _currentUser = currentUser;
            _logger = logger;
        }

        public static PasswordStrength GetStrength(string password) => PasswordPolicy.CalculateStrength(password);

        public static PasswordValidationResult ValidatePassword(string password) => PasswordPolicy.Validate(password);

        // ================= Normal login =================

        public async Task<bool> TryLoginAsync(string username, string password)
        {
            ErrorMessage = "";
            InfoMessage = "";

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                ErrorMessage = "Enter both username and password.";
                return false;
            }

            try
            {
                IsBusy = true;
                var result = await _userService.AuthenticateAsync(username.Trim(), password);

                switch (result.Outcome)
                {
                    case AuthOutcome.Success:
                        _currentUser.SetUser(result.User!.UserId, result.User.Username, result.User.FullName, result.User.Role);
                        return true;

                    case AuthOutcome.PasswordExpired:
                        _pendingExpiredUser = result.User;
                        Step = "ChangeExpired";
                        InfoMessage = result.Message;
                        return false;

                    default:
                        ErrorMessage = result.Message;
                        return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Login failed with an unexpected error", ex);
                ErrorMessage = "Something went wrong. Please try again.";
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ================= First-run admin setup =================

        public async Task<bool> TryCreateAdminAsync(
            string username, string password, string confirmPassword, string fullName,
            List<(string Question, string Answer)> securityQuestions)
        {
            ErrorMessage = "";

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(fullName))
            {
                ErrorMessage = "All fields are required.";
                return false;
            }

            if (password != confirmPassword)
            {
                ErrorMessage = "Passwords do not match.";
                return false;
            }

            var policy = PasswordPolicy.Validate(password);
            if (!policy.IsValid)
            {
                ErrorMessage = "Password needs: " + string.Join(", ", policy.Violations);
                return false;
            }

            if (securityQuestions.Count != 3 || securityQuestions.Any(q => string.IsNullOrWhiteSpace(q.Answer)))
            {
                ErrorMessage = "Please answer all three security questions.";
                return false;
            }

            if (securityQuestions.Select(q => q.Question).Distinct().Count() != 3)
            {
                ErrorMessage = "Please choose three different security questions.";
                return false;
            }

            try
            {
                IsBusy = true;
                var newUserId = await _userService.CreateAsync(username.Trim(), password, fullName.Trim(), AppRoles.Administrator, securityQuestions);
                _currentUser.SetUser(newUserId, username.Trim(), fullName.Trim(), AppRoles.Administrator);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to create admin account", ex);
                ErrorMessage = $"Could not create account: {UserMessage.For(ex)}";
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ================= Forced password change (expired) =================

        public async Task<bool> TryChangeExpiredPasswordAsync(string newPassword, string confirmPassword)
        {
            ErrorMessage = "";

            if (_pendingExpiredUser == null)
            {
                ErrorMessage = "Session error - please sign in again.";
                Step = "Login";
                return false;
            }

            if (newPassword != confirmPassword)
            {
                ErrorMessage = "Passwords do not match.";
                return false;
            }

            try
            {
                IsBusy = true;
                var (success, message) = await _userService.ChangePasswordAsync(_pendingExpiredUser.UserId, newPassword);
                if (!success)
                {
                    ErrorMessage = message;
                    return false;
                }

                _currentUser.SetUser(_pendingExpiredUser.UserId, _pendingExpiredUser.Username, _pendingExpiredUser.FullName, _pendingExpiredUser.Role);
                _pendingExpiredUser = null;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to change expired password", ex);
                ErrorMessage = "Something went wrong. Please try again.";
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ================= Forgot password recovery =================

        public void BeginRecovery()
        {
            ErrorMessage = "";
            InfoMessage = "";
            Step = "RecoverUsername";
        }

        public void CancelToLogin()
        {
            ErrorMessage = "";
            InfoMessage = "";
            _pendingExpiredUser = null;
            Step = "Login";
        }

        public async Task<bool> TryLoadRecoveryQuestionsAsync(string username)
        {
            ErrorMessage = "";

            if (string.IsNullOrWhiteSpace(username))
            {
                ErrorMessage = "Enter your username.";
                return false;
            }

            try
            {
                IsBusy = true;
                var questions = await _userService.GetSecurityQuestionsAsync(username.Trim());
                if (questions.Count != 3)
                {
                    // Deliberately vague - don't confirm/deny whether the
                    // username exists to avoid leaking account existence.
                    ErrorMessage = "Could not find security questions for that username.";
                    return false;
                }

                _recoveryUsername = username.Trim();
                _recoveryQuestions = questions;

                RecoveryQuestions.Clear();
                foreach (var q in questions) RecoveryQuestions.Add(q);

                Step = "RecoverAnswers";
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to load recovery questions", ex);
                ErrorMessage = "Something went wrong. Please try again.";
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task<bool> TryResetPasswordAsync(List<string> answers, string newPassword, string confirmPassword)
        {
            ErrorMessage = "";

            if (newPassword != confirmPassword)
            {
                ErrorMessage = "Passwords do not match.";
                return false;
            }

            try
            {
                IsBusy = true;
                var (success, message) = await _userService.ResetPasswordWithSecurityAnswersAsync(_recoveryUsername, answers, newPassword);
                if (!success)
                {
                    ErrorMessage = message;
                    return false;
                }

                InfoMessage = message;
                Step = "Login";
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to reset password", ex);
                ErrorMessage = "Something went wrong. Please try again.";
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}

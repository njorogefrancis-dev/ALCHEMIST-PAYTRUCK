using System.Collections.Generic;
using System.Linq;

namespace AlchemistPayTrack.Infrastructure
{
    public enum PasswordStrength
    {
        VeryWeak = 0,
        Weak = 1,
        Fair = 2,
        Strong = 3,
        VeryStrong = 4
    }

    public class PasswordValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Violations { get; set; } = new();
        public PasswordStrength Strength { get; set; }
    }

    /// <summary>
    /// Central password policy: minimum length + character-class mix for
    /// validity, plus a 0-4 strength score for the UI meter. Used by both
    /// account creation and password-change flows so the rule is defined
    /// exactly once.
    /// </summary>
    public static class PasswordPolicy
    {
        public const int MinLength = 8;
        public const int PasswordHistoryDepth = 5;
        public const int PasswordExpiryDays = 90;

        public static PasswordValidationResult Validate(string password)
        {
            var violations = new List<string>();
            password ??= "";

            if (password.Length < MinLength)
                violations.Add($"At least {MinLength} characters");
            if (!password.Any(char.IsUpper))
                violations.Add("At least one uppercase letter");
            if (!password.Any(char.IsLower))
                violations.Add("At least one lowercase letter");
            if (!password.Any(char.IsDigit))
                violations.Add("At least one digit");
            if (!password.Any(c => !char.IsLetterOrDigit(c)))
                violations.Add("At least one special character");

            return new PasswordValidationResult
            {
                IsValid = violations.Count == 0,
                Violations = violations,
                Strength = CalculateStrength(password)
            };
        }

        public static PasswordStrength CalculateStrength(string? password)
        {
            password ??= "";
            if (password.Length == 0) return PasswordStrength.VeryWeak;

            int score = 0;
            if (password.Length >= MinLength) score++;
            if (password.Length >= 12) score++;
            if (password.Any(char.IsUpper) && password.Any(char.IsLower)) score++;
            if (password.Any(char.IsDigit)) score++;
            if (password.Any(c => !char.IsLetterOrDigit(c))) score++;

            return score switch
            {
                <= 1 => PasswordStrength.VeryWeak,
                2 => PasswordStrength.Weak,
                3 => PasswordStrength.Fair,
                4 => PasswordStrength.Strong,
                _ => PasswordStrength.VeryStrong
            };
        }
    }
}

using System;
using AlchemistPayTrack.Infrastructure;

namespace AlchemistPayTrack.Presentation
{
    /// <summary>
    /// Decides what an exception is allowed to say to the person using the
    /// app. Some exception types (ValidationException, AuthorizationException)
    /// are deliberately written to be shown as-is - their whole purpose is
    /// to explain, in plain language, why something didn't happen. Anything
    /// else - a DataAccessException wrapping a raw SqliteException, a
    /// ConcurrencyException, an IOException, or any other unexpected
    /// failure - can contain SQL fragments, column/table names, file paths,
    /// or other internal detail that means nothing to a site supervisor or
    /// clerk and shouldn't be shown to them. Those get a generic, friendly
    /// fallback instead; the real detail still reaches the log file via the
    /// existing _logger.LogError(...) call at each catch site, so nothing
    /// is lost for diagnosing the problem later.
    /// </summary>
    public static class UserMessage
    {
        private const string GenericFallback = "Something went wrong. Please try again, and if it keeps happening, let your administrator know.";

        /// <summary>Returns a message safe to put directly in front of the
        /// person using the app.</summary>
        public static string For(Exception ex) => ex switch
        {
            ValidationException => ex.Message,
            AuthorizationException => ex.Message,
            ArgumentException => ex.Message,
            // UserService deliberately throws this with hand-written,
            // already-friendly text (e.g. "Username 'x' is already
            // taken."), never a wrapped technical error.
            InvalidOperationException => ex.Message,
            _ => GenericFallback
        };

        /// <summary>Same as For(ex), but with a short prefix - the common
        /// "{Action} failed: {message}" pattern used throughout the app.</summary>
        public static string For(Exception ex, string actionDescription) => $"{actionDescription}: {For(ex)}";
    }
}

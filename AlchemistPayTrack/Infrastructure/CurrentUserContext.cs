namespace AlchemistPayTrack.Infrastructure
{
    /// <summary>
    /// Tracks who is currently logged in, so mutation operations (creating a
    /// worker, recording a payment, etc.) can attribute the action to a real
    /// person instead of a hardcoded "system" string. Registered as a
    /// singleton - set once at login, read everywhere else.
    /// </summary>
    public interface ICurrentUserContext
    {
        int? UserId { get; }
        string? Username { get; }
        string? FullName { get; }
        string? Role { get; }
        System.DateTime? LoginTime { get; }
        bool IsAuthenticated { get; }

        void SetUser(int userId, string username, string fullName, string role);
        void Clear();
    }

    public class CurrentUserContext : ICurrentUserContext
    {
        public int? UserId { get; private set; }
        public string? Username { get; private set; }
        public string? FullName { get; private set; }
        public string? Role { get; private set; }
        public System.DateTime? LoginTime { get; private set; }
        public bool IsAuthenticated => Username != null;

        public void SetUser(int userId, string username, string fullName, string role)
        {
            UserId = userId;
            Username = username;
            FullName = fullName;
            Role = role;
            LoginTime = System.DateTime.Now;
        }

        public void Clear()
        {
            UserId = null;
            Username = null;
            FullName = null;
            Role = null;
            LoginTime = null;
        }
    }
}

using System;
using System.Windows.Threading;

namespace AlchemistPayTrack.Infrastructure
{
    /// <summary>
    /// Tracks time since the last recorded user activity and raises
    /// IdleTimeoutReached once that exceeds the configured threshold.
    /// The host (App.xaml.cs) is responsible for calling RecordActivity()
    /// from input events and deciding what "timeout" means for the UI
    /// (locking the session, in this app's case).
    /// </summary>
    public class SessionActivityMonitor
    {
        private readonly DispatcherTimer _timer;
        private readonly Func<int> _getTimeoutMinutes;
        private DateTime _lastActivity;
        private bool _fired;

        public event Action? IdleTimeoutReached;

        public SessionActivityMonitor(Func<int> getTimeoutMinutes)
        {
            _getTimeoutMinutes = getTimeoutMinutes;
            _lastActivity = DateTime.Now;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _timer.Tick += (_, _) => CheckIdle();
        }

        public void Start()
        {
            _lastActivity = DateTime.Now;
            _fired = false;
            _timer.Start();
        }

        public void Stop() => _timer.Stop();

        public void RecordActivity()
        {
            _lastActivity = DateTime.Now;
        }

        private void CheckIdle()
        {
            if (_fired) return;

            var idleMinutes = (DateTime.Now - _lastActivity).TotalMinutes;
            if (idleMinutes >= _getTimeoutMinutes())
            {
                _fired = true;
                _timer.Stop();
                IdleTimeoutReached?.Invoke();
            }
        }
    }
}

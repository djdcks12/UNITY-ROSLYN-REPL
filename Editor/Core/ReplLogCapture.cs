using System.Collections.Generic;
using UnityEngine;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// Captures Unity log messages emitted between Begin() and End().
    /// Single-use: do not call Begin() twice on the same instance.
    /// Thread-safe; logs from background threads are captured too.
    /// </summary>
    public class ReplLogCapture
    {
        private readonly List<LogEntry> _entries = new();
        private readonly object _lock = new();
        private bool _active;

        public void Begin()
        {
            if (_active) return;
            _active = true;
            Application.logMessageReceivedThreaded += OnLog;
        }

        public List<LogEntry> End()
        {
            if (_active)
            {
                Application.logMessageReceivedThreaded -= OnLog;
                _active = false;
            }
            lock (_lock)
            {
                return new List<LogEntry>(_entries);
            }
        }

        private void OnLog(string message, string stackTrace, LogType type)
        {
            lock (_lock)
            {
                _entries.Add(new LogEntry
                {
                    Type = type,
                    Message = message,
                    StackTrace = stackTrace
                });
            }
        }
    }
}

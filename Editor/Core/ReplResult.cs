using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace RoslynRepl.Editor.Core
{
    public enum ReplResultKind
    {
        Success,
        CompileError,
        RuntimeError,
        /// <summary>
        /// Snippet observed the cancellation token (or hit
        /// <see cref="ReplOptions.TimeoutMs"/>) and bailed out.
        /// Distinct from <see cref="RuntimeError"/> so the UI can show
        /// it differently — a cancellation isn't a bug, it's the safety
        /// net the user opted into.
        /// </summary>
        Cancelled
    }

    public class ReplResult
    {
        public ReplResultKind Kind { get; private set; }
        public object Value { get; private set; }
        public string ValueDisplay { get; private set; }
        public List<LogEntry> Logs { get; private set; } = new();
        public string ErrorMessage { get; private set; }
        public string StackTrace { get; private set; }
        public List<DiagnosticInfo> Diagnostics { get; private set; } = new();
        public TimeSpan Duration { get; private set; }

        public bool HasReturnValue => Kind == ReplResultKind.Success && Value != null;
        public bool IsSuccess => Kind == ReplResultKind.Success;

        public static ReplResult Success(object value, string valueDisplay, List<LogEntry> logs, TimeSpan duration)
        {
            return new ReplResult
            {
                Kind = ReplResultKind.Success,
                Value = value,
                ValueDisplay = valueDisplay,
                Logs = logs ?? new List<LogEntry>(),
                Duration = duration
            };
        }

        public static ReplResult CompileError(IEnumerable<DiagnosticInfo> diagnostics, List<LogEntry> logs, TimeSpan duration)
        {
            return new ReplResult
            {
                Kind = ReplResultKind.CompileError,
                Diagnostics = diagnostics?.ToList() ?? new List<DiagnosticInfo>(),
                Logs = logs ?? new List<LogEntry>(),
                Duration = duration
            };
        }

        public static ReplResult RuntimeError(Exception exception, List<LogEntry> logs, TimeSpan duration)
        {
            return new ReplResult
            {
                Kind = ReplResultKind.RuntimeError,
                ErrorMessage = exception?.Message ?? "(unknown)",
                StackTrace = exception?.StackTrace,
                Logs = logs ?? new List<LogEntry>(),
                Duration = duration
            };
        }

        public static ReplResult Cancelled(string reason, List<LogEntry> logs, TimeSpan duration)
        {
            return new ReplResult
            {
                Kind = ReplResultKind.Cancelled,
                ErrorMessage = reason ?? "Snippet cancelled",
                Logs = logs ?? new List<LogEntry>(),
                Duration = duration
            };
        }
    }

    public class LogEntry
    {
        public LogType Type { get; set; }
        public string Message { get; set; }
        public string StackTrace { get; set; }
        // True when StackTrace contains the generated wrapper class name
        // (i.e. the log originated from inside the user's snippet).
        public bool FromSnippet { get; set; }
    }

    public class DiagnosticInfo
    {
        public string Severity { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public bool IsInUserCode { get; set; }

        public override string ToString()
        {
            var loc = IsInUserCode ? $"line {Line}, col {Column}" : "(internal)";
            return $"{loc}: {Severity?.ToLowerInvariant()} {Code}: {Message}";
        }
    }
}

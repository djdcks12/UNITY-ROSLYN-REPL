using System;
using System.Collections.Generic;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// One row of evaluated watch state — what the user typed, the last
    /// rendered preview, and whether the value changed on the most recent
    /// re-evaluation. The view layer reads <see cref="JustChanged"/> to
    /// drive a brief highlight animation.
    /// </summary>
    public class WatchResult
    {
        public string Expression;
        public string Preview;          // ValueFormatter output of the value
        public string TypeName;         // for the row's type column
        public bool Failed;             // true on compile/runtime error or cancel
        public string ErrorMessage;
        public bool JustChanged;        // true if Preview differs from last run
    }

    /// <summary>
    /// Re-evaluates every saved watch expression on demand. The evaluator
    /// keeps a per-expression preview snapshot from the previous run so it
    /// can mark rows that just changed; the host UI uses that flag to
    /// flash the row briefly. Each expression compiles and runs
    /// independently with a short timeout (default 1s) — a bad watch
    /// shouldn't be able to hang the Editor or cascade-fail other rows.
    /// </summary>
    public class WatchEvaluator
    {
        public event Action Changed;

        private const int WatchTimeoutMs = 1000;

        private readonly Dictionary<string, string> _previousPreviews = new();
        private readonly List<WatchResult> _current = new();

        public IReadOnlyList<WatchResult> Current => _current;

        /// <summary>
        /// Re-evaluate every saved watch expression. Honours the host's
        /// effective Usings, runs each expression with its own short
        /// timeout, and updates <see cref="Current"/>. Fires
        /// <see cref="Changed"/> at the end so the view can refresh.
        /// </summary>
        public void RefreshAll()
        {
            var expressions = WatchStore.Load();
            _current.Clear();

            foreach (var expr in expressions)
            {
                _current.Add(EvaluateOne(expr));
            }

            // Drop snapshot entries for expressions the user removed so
            // re-adding the same expression later doesn't replay an old
            // "changed" highlight against a stale preview.
            var stillPresent = new HashSet<string>(expressions);
            var keys = new List<string>(_previousPreviews.Keys);
            foreach (var k in keys)
                if (!stillPresent.Contains(k)) _previousPreviews.Remove(k);

            Changed?.Invoke();
        }

        public WatchResult EvaluateOne(string expression)
        {
            var result = new WatchResult { Expression = expression };

            // Wrap as a return statement so the user can type just an
            // expression like `Manager.Instance.Count` without typing
            // `return …;` themselves. If the user's input already starts
            // with `return`, leave it alone — they probably want full
            // control (e.g. multi-line code that ends in `return`).
            string snippet;
            var trimmed = expression?.Trim() ?? string.Empty;
            if (trimmed.StartsWith("return ", StringComparison.Ordinal) || trimmed.StartsWith("return\t", StringComparison.Ordinal))
                snippet = trimmed.EndsWith(";", StringComparison.Ordinal) ? trimmed : trimmed + ";";
            else
                snippet = "return " + trimmed + (trimmed.EndsWith(";", StringComparison.Ordinal) ? "" : ";");

            ReplResult r;
            try
            {
                var opts = new ReplOptions
                {
                    Usings = UsingsStore.EffectiveUsings(),
                    TimeoutMs = WatchTimeoutMs,
                    // Watch evaluations are passive — they must not
                    // overwrite the user-visible `_` carry-over. Without
                    // this opt-out, a watch like `Manager.Count` would
                    // leave `_` pointing at the count instead of the
                    // user's actual previous run result.
                    UpdateLastResult = false,
                };
                r = ReplEngine.Execute(snippet, opts);
            }
            catch (Exception ex)
            {
                // Defensive: ReplEngine.Execute already wraps internal
                // exceptions in ReplResult, so reaching here means
                // something pathological. Surface it on the row so the
                // user can see why the watch failed.
                result.Failed = true;
                result.ErrorMessage = ex.Message;
                result.Preview = "<error>";
                MarkChange(result);
                return result;
            }

            switch (r.Kind)
            {
                case ReplResultKind.Success:
                    result.Preview = ValueFormatter.Format(r.Value);
                    result.TypeName = r.Value == null ? "null" : TypeFormatter.Short(r.Value.GetType());
                    break;
                case ReplResultKind.CompileError:
                    result.Failed = true;
                    result.Preview = "<compile error>";
                    result.ErrorMessage = r.Diagnostics.Count > 0
                        ? r.Diagnostics[0].Message
                        : "Compile error";
                    break;
                case ReplResultKind.RuntimeError:
                    result.Failed = true;
                    result.Preview = "<runtime error>";
                    result.ErrorMessage = r.ErrorMessage;
                    break;
                case ReplResultKind.Cancelled:
                    result.Failed = true;
                    result.Preview = "<cancelled>";
                    result.ErrorMessage = r.ErrorMessage;
                    break;
            }

            MarkChange(result);
            return result;
        }

        private void MarkChange(WatchResult result)
        {
            _previousPreviews.TryGetValue(result.Expression, out var previous);
            // First evaluation isn't a "change" — only flag if the user
            // had a snapshot to compare against. This keeps the view from
            // flashing every row on the first refresh after the panel
            // opens.
            result.JustChanged = previous != null && previous != result.Preview;
            _previousPreviews[result.Expression] = result.Preview;
        }

        public void ClearChangeFlags()
        {
            foreach (var r in _current) r.JustChanged = false;
        }
    }
}

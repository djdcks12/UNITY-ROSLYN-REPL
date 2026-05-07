using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

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
        public object Value;            // raw value for expandable watch trees
        public ReplValueNode Tree;      // serialized value tree for expanded rows
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
        private const int GlobalSearchMaxEntries = int.MaxValue;

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
                    SetResolvedResult(result, r.Value);
                    break;
                case ReplResultKind.CompileError:
                    if (TryEvaluateFallbackPath(trimmed, result))
                    {
                        MarkChange(result);
                        return result;
                    }
                    result.Failed = true;
                    result.Preview = "<compile error>";
                    result.ErrorMessage = r.Diagnostics.Count > 0
                        ? r.Diagnostics[0].Message
                        : "Compile error";
                    break;
                case ReplResultKind.RuntimeError:
                    if (TryEvaluateFallbackPath(trimmed, result))
                    {
                        MarkChange(result);
                        return result;
                    }
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

        private static bool TryEvaluateFallbackPath(string expression, WatchResult result)
        {
            try
            {
                if (!TryNormalizeOutputPath(expression, out var path)) return false;

                if (TryEvaluateLastResultPath(path, out var value))
                {
                    SetResolvedResult(result, value);
                    return true;
                }

                if (path == "_" || path.StartsWith("_.", StringComparison.Ordinal) || path.StartsWith("_[", StringComparison.Ordinal))
                    return false;

                if (!TryEvaluateGlobalPath(path, out value)) return false;
                SetResolvedResult(result, value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryEvaluateLastResultPath(string path, out object value)
        {
            value = null;
            object current = ReplEngine.LastResult;
            if (IsDestroyedUnityObject(current)) return false;

            int index = 0;
            if (path == "_")
            {
                value = current;
                return true;
            }

            if (path.StartsWith("_.", StringComparison.Ordinal))
            {
                index = 2;
            }
            else if (path.StartsWith("_[", StringComparison.Ordinal))
            {
                index = 1;
            }

            return TryResolvePathFromIndex(current, path, index, out value);
        }

        private static bool TryEvaluateGlobalPath(string path, out object value)
        {
            value = null;
            var entries = InstanceLocator.Find(InstanceCategory.All, string.Empty, GlobalSearchMaxEntries);
            if (entries.Count == 0) return false;

            TryReadIdentifier(path, 0, out var rootName);

            foreach (var entry in entries)
            {
                var root = entry?.Value;
                if (root == null || IsDestroyedUnityObject(root)) continue;

                if (!string.IsNullOrEmpty(rootName) && IsEntryNameMatch(entry, rootName))
                {
                    int index = rootName.Length;
                    if (index == path.Length)
                    {
                        value = root;
                        return true;
                    }

                    if (path[index] == '.' || path[index] == '[')
                    {
                        if (TryResolvePathFromIndex(root, path, index, out value))
                            return true;
                    }
                }

                if (TryResolvePathFromIndex(root, path, 0, out value))
                    return true;
            }

            return false;
        }

        private static bool TryResolvePathFromIndex(object root, string path, int index, out object value)
        {
            value = null;
            object current = root;
            if (index == path.Length)
            {
                value = current;
                return true;
            }

            while (index < path.Length)
            {
                if (path[index] == '.')
                {
                    index++;
                    if (index >= path.Length) return false;
                }

                if (path[index] == '[')
                {
                    if (!TryReadIndex(path, ref index, out var key)) return false;
                    if (!TryResolveIndex(current, key, out current)) return false;
                    continue;
                }

                if (!TryReadIdentifier(path, ref index, out var memberName)) return false;
                if (!TryResolveMember(current, memberName, out current)) return false;

                while (index < path.Length && path[index] == '[')
                {
                    if (!TryReadIndex(path, ref index, out var key)) return false;
                    if (!TryResolveIndex(current, key, out current)) return false;
                }
            }

            value = current;
            return true;
        }

        private static bool IsEntryNameMatch(InstanceEntry entry, string rootName)
        {
            if (entry == null || string.IsNullOrEmpty(rootName)) return false;
            return string.Equals(entry.TypeName, rootName, StringComparison.Ordinal)
                || string.Equals(entry.DisplayName, rootName, StringComparison.Ordinal)
                || string.Equals(entry.DeclaredType?.Name, rootName, StringComparison.Ordinal)
                || string.Equals(entry.DeclaredType?.FullName, rootName, StringComparison.Ordinal);
        }

        private static bool TryNormalizeOutputPath(string expression, out string path)
        {
            path = expression?.Trim() ?? string.Empty;
            if (path.StartsWith("return ", StringComparison.Ordinal) || path.StartsWith("return\t", StringComparison.Ordinal))
                path = path.Substring(6).Trim();

            if (path.EndsWith(";", StringComparison.Ordinal))
                path = path.Substring(0, path.Length - 1).Trim();

            if (string.IsNullOrEmpty(path)) return false;

            foreach (char c in path)
            {
                if (char.IsWhiteSpace(c)) return false;
                if (char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '[' || c == ']' || c == '"' || c == '\'')
                    continue;
                return false;
            }

            return true;
        }

        private static bool TryReadIdentifier(string path, ref int index, out string identifier)
        {
            identifier = null;
            if (index >= path.Length) return false;

            char first = path[index];
            if (!(char.IsLetter(first) || first == '_')) return false;

            int start = index++;
            while (index < path.Length)
            {
                char c = path[index];
                if (!(char.IsLetterOrDigit(c) || c == '_')) break;
                index++;
            }

            identifier = path.Substring(start, index - start);
            return true;
        }

        private static bool TryReadIdentifier(string path, int index, out string identifier)
        {
            return TryReadIdentifier(path, ref index, out identifier);
        }

        private static bool TryReadIndex(string path, ref int index, out object key)
        {
            key = null;
            if (index >= path.Length || path[index] != '[') return false;
            index++;
            if (index >= path.Length) return false;

            if (path[index] == '"' || path[index] == '\'')
            {
                char quote = path[index++];
                int start = index;
                while (index < path.Length && path[index] != quote) index++;
                if (index >= path.Length) return false;
                key = path.Substring(start, index - start);
                index++;
            }
            else
            {
                int start = index;
                while (index < path.Length && char.IsDigit(path[index])) index++;
                if (start == index) return false;
                if (!int.TryParse(path.Substring(start, index - start), out var numericKey)) return false;
                key = numericKey;
            }

            if (index >= path.Length || path[index] != ']') return false;
            index++;
            return true;
        }

        private static bool TryResolveMember(object target, string memberName, out object value)
        {
            value = null;
            if (target == null || IsDestroyedUnityObject(target)) return false;

            var type = target.GetType();
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    value = field.GetValue(target);
                    return true;
                }

                var property = t.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property == null || property.GetIndexParameters().Length != 0) continue;

                var getter = property.GetGetMethod(true);
                if (getter == null) continue;

                try
                {
                    value = getter.Invoke(target, null);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static bool TryResolveIndex(object target, object key, out object value)
        {
            value = null;
            if (target == null || IsDestroyedUnityObject(target)) return false;

            if (target is IDictionary dictionary)
            {
                if (!dictionary.Contains(key)) return false;
                value = dictionary[key];
                return true;
            }

            if (!(key is int index)) return false;

            if (target is Array array)
            {
                if (index < 0 || index >= array.Length) return false;
                value = array.GetValue(index);
                return true;
            }

            if (target is IList list)
            {
                if (index < 0 || index >= list.Count) return false;
                value = list[index];
                return true;
            }

            if (target is IEnumerable enumerable)
            {
                int i = 0;
                foreach (var item in enumerable)
                {
                    if (i == index)
                    {
                        value = item;
                        return true;
                    }
                    i++;
                }
            }

            return false;
        }

        private static void SetResolvedResult(WatchResult result, object value)
        {
            if (IsDestroyedUnityObject(value))
                value = null;

            result.Value = value;
            result.Preview = ValueFormatter.Format(value);
            result.TypeName = value == null ? "null" : TypeFormatter.Short(value.GetType());
            result.Tree = BuildTree(value);
            result.Failed = false;
            result.ErrorMessage = null;
        }

        private static ReplValueNode BuildTree(object value)
        {
            try
            {
                return SimpleObjectSerializer.ToTree(value);
            }
            catch (Exception ex)
            {
                return new ReplValueNode
                {
                    Name = "(result)",
                    TypeName = "<error>",
                    Preview = $"[error: {ex.GetBaseException().Message}]",
                    IsExpandable = false
                };
            }
        }

        private static bool IsDestroyedUnityObject(object value)
        {
            return value is UnityEngine.Object unityObject && unityObject == null;
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

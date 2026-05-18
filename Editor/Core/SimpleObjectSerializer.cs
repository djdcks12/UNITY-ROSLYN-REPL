using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// Reflection-based object → <see cref="ReplValueNode"/> tree converter.
    /// Walks fields (incl. private + inherited) and readable instance properties
    /// of the target object, recursively, with cycle detection, depth caps, and
    /// collection-head truncation. Designed to be safe for large or self-
    /// referential graphs.
    /// </summary>
    public static class SimpleObjectSerializer
    {
        public class Options
        {
            public int MaxDepth { get; set; } = 4;
            public int CollectionHeadCount { get; set; } = 50;
            public bool IncludeNonPublic { get; set; } = true;
            public bool IncludeProperties { get; set; } = true;
            // Hard cap on the total node count produced by a single ToTree call.
            // Plain C# managers often hold Action events whose invocation lists
            // reach view-side MonoBehaviours, exploding the graph. Even with
            // depth caps, fan-out can produce tens of thousands of nodes and
            // freeze the Editor. The cap aborts cleanly with a marker leaf.
            public int MaxTotalNodes { get; set; } = 2000;
        }

        private class BuildState
        {
            public Options Options;
            public HashSet<object> Visited;
            public int NodeCount;
        }

        public static ReplValueNode ToTree(object value, Options options = null, string rootPath = "_")
        {
            options ??= new Options();
            var state = new BuildState
            {
                Options = options,
                Visited = new HashSet<object>(ReferenceEqualityComparer.Instance),
                NodeCount = 0,
            };
            // rootPath defaults to "_" because every UI-driven ToTree
            // call site assigns the value to ReplEngine.LastResult on
            // the same beat (Run / Browse / Reinspect), so `_` resolves
            // to the same instance. WatchEvaluator overrides with the
            // user's actual expression so its sub-tree paths grow off
            // the watch row's accessor rather than a stale `_`.
            return BuildNode("(result)", value, depth: 0, state, rootPath);
        }

        private static ReplValueNode BuildNode(
            string name, object value, int depth, BuildState state, string path)
        {
            state.NodeCount++;
            if (state.NodeCount > state.Options.MaxTotalNodes)
            {
                // Placeholder — Value/ExpressionPath stay null so
                // context menus disable Inspect / Set as `_` /
                // Add Watch for the truncation marker.
                return new ReplValueNode
                {
                    Name = name,
                    TypeName = "",
                    Preview = $"(node cap reached at {state.Options.MaxTotalNodes}; subtree truncated)",
                    IsExpandable = false
                };
            }

            if (value == null)
            {
                return new ReplValueNode
                {
                    Name = name,
                    TypeName = "null",
                    Preview = "null",
                    IsExpandable = false,
                    Value = null,
                    ExpressionPath = path,
                };
            }

            var type = value.GetType();
            var typeName = TypeFormatter.Short(type);

            if (value is UnityEngine.Object uo && uo == null)
            {
                // Destroyed Unity object — keep the path so the user
                // can still copy a snippet that references the
                // (now-broken) accessor, but Value stays null so
                // Inspect / Set as `_` correctly grey out.
                return new ReplValueNode
                {
                    Name = name,
                    TypeName = typeName,
                    Preview = $"{typeName} (missing/destroyed)",
                    IsExpandable = false,
                    Value = null,
                    ExpressionPath = path,
                };
            }

            if (IsLeafType(type))
            {
                return new ReplValueNode
                {
                    Name = name,
                    TypeName = typeName,
                    Preview = ValueFormatter.Format(value),
                    IsExpandable = false,
                    Value = value,
                    ExpressionPath = path,
                };
            }

            if (!type.IsValueType && state.Visited.Contains(value))
            {
                return new ReplValueNode
                {
                    Name = name,
                    TypeName = typeName,
                    Preview = "[circular reference]",
                    IsExpandable = false,
                    Value = value,
                    ExpressionPath = path,
                };
            }

            if (depth >= state.Options.MaxDepth)
            {
                return new ReplValueNode
                {
                    Name = name,
                    TypeName = typeName,
                    Preview = ValueFormatter.Format(value) + "  …(depth limit)",
                    IsExpandable = false,
                    Value = value,
                    ExpressionPath = path,
                };
            }

            bool added = false;
            if (!type.IsValueType)
            {
                state.Visited.Add(value);
                added = true;
            }

            try
            {
                var node = new ReplValueNode
                {
                    Name = name,
                    TypeName = typeName,
                    Preview = ValueFormatter.Format(value),
                    IsExpandable = true,
                    Value = value,
                    ExpressionPath = path
                };

                if (value is IDictionary dict)
                {
                    node.Children = BuildDictChildren(dict, depth, state, path);
                }
                else if (value is IEnumerable enumerable && !(value is string))
                {
                    node.Children = BuildEnumerableChildren(enumerable, depth, state, path);
                }
                else
                {
                    node.Children = BuildMemberChildren(value, type, depth, state, path);
                }

                if (node.Children.Count == 0)
                    node.IsExpandable = false;

                return node;
            }
            finally
            {
                if (added) state.Visited.Remove(value);
            }
        }

        private static List<ReplValueNode> BuildMemberChildren(
            object obj, Type type, int depth, BuildState state, string parentPath)
        {
            var children = new List<ReplValueNode>();
            var bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            if (state.Options.IncludeNonPublic) bf |= BindingFlags.NonPublic;

            // Walk type hierarchy so base-class fields are visible too.
            var seenFieldNames = new HashSet<string>();
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var f in t.GetFields(bf).OrderBy(f => f.Name))
                {
                    if (f.IsStatic) continue;
                    // Skip compiler-generated backing fields visually noisy
                    if (f.Name.Contains("k__BackingField")) continue;
                    if (!seenFieldNames.Add(f.Name)) continue;

                    object v;
                    try { v = f.GetValue(obj); }
                    catch (Exception ex) { children.Add(ErrorNode(f.Name, ex)); continue; }

                    // Field accessors are always safe to splice into
                    // an expression — propagate path only when the
                    // parent had one (parent==null = unsafe lineage,
                    // e.g. inside a dict bucket whose key wasn't
                    // expressible).
                    string childPath = parentPath == null
                        ? null
                        : parentPath + "." + f.Name;
                    children.Add(BuildNode(f.Name, v, depth + 1, state, childPath));
                    if (state.NodeCount > state.Options.MaxTotalNodes) return children;
                }
            }

            if (state.Options.IncludeProperties)
            {
                // Only the most-derived declaration of each property to avoid
                // duplicates from `new` shadowing or virtual overrides.
                var seenPropNames = new HashSet<string>();
                var pbf = BindingFlags.Public | BindingFlags.Instance;
                if (state.Options.IncludeNonPublic) pbf |= BindingFlags.NonPublic;

                foreach (var p in type.GetProperties(pbf).OrderBy(p => p.Name))
                {
                    if (!p.CanRead) continue;
                    if (p.GetIndexParameters().Length > 0) continue;
                    var getter = p.GetMethod;
                    if (getter == null || getter.IsStatic) continue;
                    if (!seenPropNames.Add(p.Name)) continue;

                    // Skip properties whose *declaring type* lives in a
                    // Unity-shipped assembly. Unity's accessors (Image.color,
                    // Renderer.bounds, Canvas.worldCamera, Transform.position…)
                    // commonly call into native code; degenerate state fires
                    // "Assertion failed" entries that bypass managed try/catch
                    // and spam the Console. Properties declared on user types
                    // (MonoBehaviour / ScriptableObject subclasses, etc.) are
                    // walked normally — that's where user intent lives.
                    if (IsUnityFrameworkType(p.DeclaringType)) continue;

                    object v;
                    try { v = p.GetValue(obj); }
                    catch (TargetInvocationException tie)
                    { children.Add(ErrorNode(p.Name, tie.InnerException ?? tie)); continue; }
                    catch (Exception ex)
                    { children.Add(ErrorNode(p.Name, ex)); continue; }

                    string childPath = parentPath == null
                        ? null
                        : parentPath + "." + p.Name;
                    children.Add(BuildNode(p.Name, v, depth + 1, state, childPath));
                    if (state.NodeCount > state.Options.MaxTotalNodes) return children;
                }
            }

            return children;
        }

        private static List<ReplValueNode> BuildEnumerableChildren(
            IEnumerable enumerable, int depth, BuildState state, string parentPath)
        {
            var children = new List<ReplValueNode>();
            int idx = 0;
            try
            {
                foreach (var item in enumerable)
                {
                    if (idx >= state.Options.CollectionHeadCount)
                    {
                        children.Add(new ReplValueNode
                        {
                            Name = "...",
                            TypeName = "",
                            Preview = $"(remaining items truncated at {state.Options.CollectionHeadCount})",
                            IsExpandable = false
                        });
                        break;
                    }
                    // Numeric indexer is always C#-safe: `parent[0]`,
                    // `parent[1]`, …. Inherits parent's safe/unsafe
                    // lineage like every other path-accumulating step.
                    string childPath = parentPath == null
                        ? null
                        : parentPath + "[" + idx + "]";
                    children.Add(BuildNode($"[{idx}]", item, depth + 1, state, childPath));
                    idx++;
                    if (state.NodeCount > state.Options.MaxTotalNodes) break;
                }
            }
            catch (Exception ex)
            {
                children.Add(ErrorNode("<enumeration>", ex));
            }
            return children;
        }

        private static List<ReplValueNode> BuildDictChildren(
            IDictionary dict, int depth, BuildState state, string parentPath)
        {
            var children = new List<ReplValueNode>();
            int idx = 0;
            try
            {
                foreach (DictionaryEntry e in dict)
                {
                    if (idx >= state.Options.CollectionHeadCount)
                    {
                        int remaining = -1;
                        try { remaining = dict.Count - idx; } catch { /* swallow */ }
                        children.Add(new ReplValueNode
                        {
                            Name = "...",
                            TypeName = "",
                            Preview = remaining >= 0
                                ? $"(remaining {remaining} entries truncated)"
                                : "(remaining entries truncated)",
                            IsExpandable = false
                        });
                        break;
                    }
                    var keyPreview = ValueFormatter.Format(e.Key);
                    // Try to express the key as C# source so an
                    // Add-Watch on this entry produces a path that
                    // evaluates back to the same bucket. Non-
                    // expressible keys (custom struct keys, control
                    // chars in strings, flag-enum combinations)
                    // surface as a null child path — Add Watch will
                    // grey out for those rows.
                    string encodedKey = TryEncodeDictKeyAsCSharp(e.Key);
                    string childPath = (parentPath == null || encodedKey == null)
                        ? null
                        : parentPath + "[" + encodedKey + "]";
                    children.Add(BuildNode($"[{keyPreview}]", e.Value, depth + 1, state, childPath));
                    idx++;
                    if (state.NodeCount > state.Options.MaxTotalNodes) break;
                }
            }
            catch (Exception ex)
            {
                children.Add(ErrorNode("<enumeration>", ex));
            }
            return children;
        }

        private static ReplValueNode ErrorNode(string name, Exception ex) => new ReplValueNode
        {
            Name = name,
            TypeName = "<error>",
            Preview = $"[error: {ex.GetBaseException().Message}]",
            IsExpandable = false
            // Value + ExpressionPath stay null — error nodes don't
            // have a usable value, and re-asking for the same path
            // would just re-throw the same exception.
        };

        // Render a dictionary key as a C# source-text expression. The
        // result is spliced into a path like `parent[<key>]`, so we
        // only accept forms that would *compile* against a typical
        // REPL context. Anything outside that returns null and the
        // caller marks the row's ExpressionPath unsafe.
        //
        // - Numeric, bool, string, char, enum (single value only).
        // - Strings: regular literal, backslash + quote escaped;
        //   control chars reject so embedded \n / \t etc. don't
        //   silently become wrong source. Unicode glyphs pass
        //   through.
        // - Enums: render type as `Namespace.Type.Value`, mapping
        //   nested-type `+` separators to `.`. Flag combinations
        //   (`"A, B"`-style) reject.
        // - Reference types and unknown structs: null.
        private static string TryEncodeDictKeyAsCSharp(object key)
        {
            if (key == null) return null;
            switch (key)
            {
                case bool b:    return b ? "true" : "false";
                case sbyte i:   return i.ToString(CultureInfo.InvariantCulture);
                case byte i:    return i.ToString(CultureInfo.InvariantCulture);
                case short i:   return i.ToString(CultureInfo.InvariantCulture);
                case ushort i:  return i.ToString(CultureInfo.InvariantCulture);
                case int i:     return i.ToString(CultureInfo.InvariantCulture);
                case uint i:    return i.ToString(CultureInfo.InvariantCulture) + "u";
                case long i:    return i.ToString(CultureInfo.InvariantCulture) + "L";
                case ulong i:   return i.ToString(CultureInfo.InvariantCulture) + "uL";
                case float f:   return f.ToString("R", CultureInfo.InvariantCulture) + "f";
                case double d:  return d.ToString("R", CultureInfo.InvariantCulture);
                case decimal m: return m.ToString(CultureInfo.InvariantCulture) + "m";
                case char c:
                    if (char.IsControl(c)) return null;
                    if (c == '\'' || c == '\\') return "'\\" + c + "'";
                    return "'" + c + "'";
                case string s:
                    foreach (var ch in s) if (char.IsControl(ch)) return null;
                    return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                case Enum e:
                {
                    var s = e.ToString();
                    // Flag combinations land here as comma-separated
                    // names — that won't compile in `dict[Flags.A,
                    // B]` form without parens, and even with parens
                    // the meaning is ambiguous, so reject.
                    if (s.Contains(",")) return null;
                    var t = e.GetType();
                    // CSharpTypeName lives in the Patches layer; we
                    // can't take a hard dep on it from Core. The
                    // FullName + `+` → `.` substitution covers the
                    // realistic cases (top-level + nested enums) and
                    // matches the rendered form Patches would emit.
                    var typeExpr = t.FullName?.Replace('+', '.');
                    if (string.IsNullOrEmpty(typeExpr)) return null;
                    return typeExpr + "." + s;
                }
                default:
                    return null;
            }
        }

        // Types treated as atomic leaves — preview is the whole story, no expand.
        private static readonly HashSet<Type> _leafLikeTypes = new HashSet<Type>
        {
            typeof(string), typeof(char), typeof(bool), typeof(decimal),
            typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan), typeof(Guid),
            typeof(Vector2), typeof(Vector3), typeof(Vector4),
            typeof(Vector2Int), typeof(Vector3Int),
            typeof(Color), typeof(Color32),
            typeof(Quaternion),
            typeof(Rect), typeof(RectInt),
            typeof(Bounds), typeof(BoundsInt),
        };

        private static bool IsUnityFrameworkType(Type t)
        {
            if (t == null) return false;
            var asmName = t.Assembly.GetName().Name;
            if (string.IsNullOrEmpty(asmName)) return false;
            return asmName == "UnityEngine"
                || asmName.StartsWith("UnityEngine.", StringComparison.Ordinal)
                || asmName == "UnityEditor"
                || asmName.StartsWith("UnityEditor.", StringComparison.Ordinal)
                || asmName.StartsWith("Unity.", StringComparison.Ordinal);
        }

        private static bool IsLeafType(Type t)
        {
            if (t.IsPrimitive) return true;
            if (t.IsEnum) return true;
            if (_leafLikeTypes.Contains(t)) return true;
            if (typeof(UnityEngine.Transform).IsAssignableFrom(t)) return true;
            // Treat any Delegate / Action / Func / Event as a leaf. Following
            // their internal _invocationList field walks into every subscribed
            // receiver — typically views and managers across the whole scene —
            // and explodes the graph. Preview shows handler count + first
            // target instead.
            if (typeof(System.Delegate).IsAssignableFrom(t)) return true;
            return false;
        }

        // .NET 5+ has System.Collections.Generic.ReferenceEqualityComparer; we
        // ship our own to stay compatible across Unity Mono runtimes.
        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}

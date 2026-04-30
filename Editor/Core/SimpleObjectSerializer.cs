using System;
using System.Collections;
using System.Collections.Generic;
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

        public static ReplValueNode ToTree(object value, Options options = null)
        {
            options ??= new Options();
            var state = new BuildState
            {
                Options = options,
                Visited = new HashSet<object>(ReferenceEqualityComparer.Instance),
                NodeCount = 0,
            };
            return BuildNode("(result)", value, depth: 0, state);
        }

        private static ReplValueNode BuildNode(
            string name, object value, int depth, BuildState state)
        {
            state.NodeCount++;
            if (state.NodeCount > state.Options.MaxTotalNodes)
            {
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
                    IsExpandable = false
                };
            }

            var type = value.GetType();
            var typeName = TypeFormatter.Short(type);

            if (value is UnityEngine.Object uo && uo == null)
            {
                return new ReplValueNode
                {
                    Name = name,
                    TypeName = typeName,
                    Preview = $"{typeName} (missing/destroyed)",
                    IsExpandable = false
                };
            }

            if (IsLeafType(type))
            {
                return new ReplValueNode
                {
                    Name = name,
                    TypeName = typeName,
                    Preview = ValueFormatter.Format(value),
                    IsExpandable = false
                };
            }

            if (!type.IsValueType && state.Visited.Contains(value))
            {
                return new ReplValueNode
                {
                    Name = name,
                    TypeName = typeName,
                    Preview = "[circular reference]",
                    IsExpandable = false
                };
            }

            if (depth >= state.Options.MaxDepth)
            {
                return new ReplValueNode
                {
                    Name = name,
                    TypeName = typeName,
                    Preview = ValueFormatter.Format(value) + "  …(depth limit)",
                    IsExpandable = false
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
                    IsExpandable = true
                };

                if (value is IDictionary dict)
                {
                    node.Children = BuildDictChildren(dict, depth, state);
                }
                else if (value is IEnumerable enumerable && !(value is string))
                {
                    node.Children = BuildEnumerableChildren(enumerable, depth, state);
                }
                else
                {
                    node.Children = BuildMemberChildren(value, type, depth, state);
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
            object obj, Type type, int depth, BuildState state)
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

                    children.Add(BuildNode(f.Name, v, depth + 1, state));
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

                    children.Add(BuildNode(p.Name, v, depth + 1, state));
                    if (state.NodeCount > state.Options.MaxTotalNodes) return children;
                }
            }

            return children;
        }

        private static List<ReplValueNode> BuildEnumerableChildren(
            IEnumerable enumerable, int depth, BuildState state)
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
                    children.Add(BuildNode($"[{idx}]", item, depth + 1, state));
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
            IDictionary dict, int depth, BuildState state)
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
                    children.Add(BuildNode($"[{keyPreview}]", e.Value, depth + 1, state));
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
        };

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

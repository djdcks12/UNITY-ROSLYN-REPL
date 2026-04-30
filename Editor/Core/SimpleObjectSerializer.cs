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
            public int MaxDepth { get; set; } = 6;
            public int CollectionHeadCount { get; set; } = 50;
            public bool IncludeNonPublic { get; set; } = true;
            public bool IncludeProperties { get; set; } = true;
        }

        public static ReplValueNode ToTree(object value, Options options = null)
        {
            options ??= new Options();
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            return BuildNode("(result)", value, options, depth: 0, visited);
        }

        private static ReplValueNode BuildNode(
            string name, object value, Options opt, int depth, HashSet<object> visited)
        {
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

            // Unity "fake null": the C# wrapper survives after the native side
            // is destroyed or was never assigned. value != null is true (C#),
            // but Unity's == overload returns true for null. Walking such an
            // object's fields will throw NullReferenceException from native
            // accessors. Surface as a leaf with a destroyed/missing marker.
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

            // Reference-typed objects: detect cycles. Value types skip this
            // (each reading produces a fresh box, never recurses on identity).
            if (!type.IsValueType && visited.Contains(value))
            {
                return new ReplValueNode
                {
                    Name = name,
                    TypeName = typeName,
                    Preview = "[circular reference]",
                    IsExpandable = false
                };
            }

            if (depth >= opt.MaxDepth)
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
                visited.Add(value);
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
                    node.Children = BuildDictChildren(dict, opt, depth, visited);
                }
                else if (value is IEnumerable enumerable && !(value is string))
                {
                    node.Children = BuildEnumerableChildren(enumerable, opt, depth, visited);
                }
                else
                {
                    node.Children = BuildMemberChildren(value, type, opt, depth, visited);
                }

                if (node.Children.Count == 0)
                    node.IsExpandable = false;

                return node;
            }
            finally
            {
                if (added) visited.Remove(value);
            }
        }

        private static List<ReplValueNode> BuildMemberChildren(
            object obj, Type type, Options opt, int depth, HashSet<object> visited)
        {
            var children = new List<ReplValueNode>();
            var bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            if (opt.IncludeNonPublic) bf |= BindingFlags.NonPublic;

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

                    children.Add(BuildNode(f.Name, v, opt, depth + 1, visited));
                }
            }

            if (opt.IncludeProperties)
            {
                // Only the most-derived declaration of each property to avoid
                // duplicates from `new` shadowing or virtual overrides.
                var seenPropNames = new HashSet<string>();
                var pbf = BindingFlags.Public | BindingFlags.Instance;
                if (opt.IncludeNonPublic) pbf |= BindingFlags.NonPublic;

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

                    children.Add(BuildNode(p.Name, v, opt, depth + 1, visited));
                }
            }

            return children;
        }

        private static List<ReplValueNode> BuildEnumerableChildren(
            IEnumerable enumerable, Options opt, int depth, HashSet<object> visited)
        {
            var children = new List<ReplValueNode>();
            int idx = 0;
            try
            {
                foreach (var item in enumerable)
                {
                    if (idx >= opt.CollectionHeadCount)
                    {
                        children.Add(new ReplValueNode
                        {
                            Name = "...",
                            TypeName = "",
                            Preview = $"(remaining items truncated at {opt.CollectionHeadCount})",
                            IsExpandable = false
                        });
                        break;
                    }
                    children.Add(BuildNode($"[{idx}]", item, opt, depth + 1, visited));
                    idx++;
                }
            }
            catch (Exception ex)
            {
                children.Add(ErrorNode("<enumeration>", ex));
            }
            return children;
        }

        private static List<ReplValueNode> BuildDictChildren(
            IDictionary dict, Options opt, int depth, HashSet<object> visited)
        {
            var children = new List<ReplValueNode>();
            int idx = 0;
            // Mirror BuildEnumerableChildren: a custom IDictionary whose
            // GetEnumerator() (or MoveNext / Current) throws must not abort
            // the whole ToTree call. Surface as an error leaf and keep any
            // entries already produced.
            try
            {
                foreach (DictionaryEntry e in dict)
                {
                    if (idx >= opt.CollectionHeadCount)
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
                    children.Add(BuildNode($"[{keyPreview}]", e.Value, opt, depth + 1, visited));
                    idx++;
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
            // UnityEngine.Transform (incl. RectTransform) has many computed
            // properties (position, lossyScale, eulerAngles, …) that read from
            // an internal matrix and fire a native ValidTRS() assertion when
            // the underlying TRS is degenerate (NaN, zero scale, etc.). Those
            // asserts log to the Console even when the managed call returns
            // normally — try/catch can't suppress them. Treat as a leaf so we
            // never recurse into them. Users can `return someRect.localPosition`
            // directly when they need spatial details.
            if (typeof(UnityEngine.Transform).IsAssignableFrom(t)) return true;
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

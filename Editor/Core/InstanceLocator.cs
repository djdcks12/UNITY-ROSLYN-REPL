using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace RoslynRepl.Editor.Core
{
    public enum InstanceCategory
    {
        All,
        MonoBehaviour,
        ScriptableObject,
        Singleton,
    }

    public class InstanceEntry
    {
        public object Value;                  // the live instance — Unity Object or plain C# object
        public Type DeclaredType;             // C# type of the entry
        public string DisplayName;            // GO/asset/instance name
        public string TypeName;               // short type name (e.g. "PopupMinimap")
        public string SubLabel;               // scene name / "asset" / "Singleton"
        public InstanceCategory Category;
        public bool IsActive;                 // active in hierarchy (MB) / true otherwise

        // Convenience for Unity-Object-typed entries.
        public UnityEngine.Object UnityObject => Value as UnityEngine.Object;
    }

    /// <summary>
    /// Collects user-side instances the REPL window can browse: scene
    /// MonoBehaviours, ScriptableObject assets, and types exposing a static
    /// <c>Instance</c> singleton accessor. Filters out Unity-shipped types so
    /// the list stays focused on what the user wrote.
    /// </summary>
    public static class InstanceLocator
    {
        public static List<InstanceEntry> Find(
            InstanceCategory category,
            string filter,
            int maxResults = 200)
        {
            // "All" intentionally excludes Singleton scanning — that path does
            // a domain-wide reflection sweep for self-returning static members,
            // which can be expensive and (depending on the project) read user
            // backing fields whose graphs are heavy. The user must opt in by
            // selecting the Singleton category explicitly.
            IEnumerable<InstanceEntry> pool = category switch
            {
                InstanceCategory.MonoBehaviour    => FindMonoBehaviours(),
                InstanceCategory.ScriptableObject => FindScriptableObjects(),
                InstanceCategory.Singleton        => SingletonScanner.Find(),
                _                                 => FindMonoBehaviours()
                                                       .Concat(FindScriptableObjects()),
            };

            if (!string.IsNullOrEmpty(filter))
            {
                var f = filter;
                pool = pool.Where(e =>
                    (e.TypeName    != null && e.TypeName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                 || (e.DisplayName != null && e.DisplayName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            // Order: active first, then by type name, then by display name.
            return pool
                .OrderByDescending(e => e.IsActive)
                .ThenBy(e => e.TypeName, StringComparer.Ordinal)
                .ThenBy(e => e.DisplayName, StringComparer.Ordinal)
                .Take(maxResults)
                .ToList();
        }

        private static IEnumerable<InstanceEntry> FindMonoBehaviours()
        {
            var all = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
            foreach (var mb in all)
            {
                if (mb == null) continue;
                var t = mb.GetType();
                if (IsHiddenAssembly(t)) continue;
                // Editor-only / hidden objects (preview scenes, internal helpers)
                if ((mb.hideFlags & (HideFlags.HideAndDontSave | HideFlags.HideInHierarchy)) != 0)
                {
                    if (!IsUserMonoBehaviour(t)) continue;
                }

                string subLabel = "(no scene)";
                bool active = false;
                if (mb.gameObject != null)
                {
                    var scene = mb.gameObject.scene;
                    subLabel = scene.IsValid() ? scene.name : "(prefab/asset)";
                    active = mb.gameObject.activeInHierarchy;
                }

                yield return new InstanceEntry
                {
                    Value = mb,
                    DeclaredType = t,
                    DisplayName = mb.name,
                    TypeName = TypeFormatter.Short(t),
                    SubLabel = subLabel,
                    Category = InstanceCategory.MonoBehaviour,
                    IsActive = active,
                };
            }
        }

        private static IEnumerable<InstanceEntry> FindScriptableObjects()
        {
            var all = Resources.FindObjectsOfTypeAll<ScriptableObject>();
            foreach (var so in all)
            {
                if (so == null) continue;
                var t = so.GetType();
                if (IsHiddenAssembly(t)) continue;

                yield return new InstanceEntry
                {
                    Value = so,
                    DeclaredType = t,
                    DisplayName = string.IsNullOrEmpty(so.name) ? t.Name : so.name,
                    TypeName = TypeFormatter.Short(t),
                    SubLabel = "ScriptableObject",
                    Category = InstanceCategory.ScriptableObject,
                    IsActive = true,
                };
            }
        }

        // True for Unity-shipped or framework assemblies whose objects we want
        // to hide from the browser. Mirrors the property-skip rule used by
        // SimpleObjectSerializer.
        private static bool IsHiddenAssembly(Type t)
        {
            if (t == null) return true;
            var asm = t.Assembly.GetName().Name;
            if (string.IsNullOrEmpty(asm)) return false;
            return asm.StartsWith("UnityEngine", StringComparison.Ordinal)
                || asm.StartsWith("UnityEditor", StringComparison.Ordinal)
                || asm.StartsWith("Unity.",       StringComparison.Ordinal)
                || asm.StartsWith("System",       StringComparison.Ordinal)
                || asm.StartsWith("Microsoft.",   StringComparison.Ordinal)
                || asm.StartsWith("Mono.",        StringComparison.Ordinal)
                || asm.StartsWith("netstandard",  StringComparison.Ordinal)
                || asm == "mscorlib"
                || asm == "RoslynRepl.Editor";
        }

        // Override to let user MBs through even if they have unusual hideFlags
        // (some pooling systems hide their objects in hierarchy).
        private static bool IsUserMonoBehaviour(Type t) => !IsHiddenAssembly(t);
    }
}

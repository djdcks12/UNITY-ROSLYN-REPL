using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// Surfaces types declaring a public static <c>Instance</c> singleton
    /// accessor.
    ///
    /// CRITICAL: We never invoke a property getter for a Unity Object type.
    /// Many user singletons lazy-construct themselves inside the getter
    /// (e.g. <c>Instance ??= new MyMB()</c> via FindObjectsOfType / Awake),
    /// and Unity throws UnityException when FindObjectsOfType runs from a
    /// MonoBehaviour ctor. Even when the throw is caught, user defensive
    /// code may have already logged "이미 X가 존재합니다 … 앱을 종료합니다"
    /// and called Application.Quit. So:
    ///
    ///  - For Unity Object–typed Instance members, we *reverse-lookup*: enumerate
    ///    every alive MonoBehaviour / ScriptableObject via
    ///    <see cref="Resources.FindObjectsOfTypeAll(Type)"/> (no user code invoked)
    ///    and report the first instance that's compatible with the declaring type.
    ///    No alive instance → not surfaced (we don't try to spawn one).
    ///  - For plain C# (non-UnityEngine.Object) singleton fields, a direct
    ///    field read is safe and used.
    ///  - Plain C# *property* singletons are intentionally skipped — we cannot
    ///    distinguish a pure getter from one that triggers init.
    /// </summary>
    [InitializeOnLoad]
    public static class SingletonScanner
    {
        private static List<MemberInfo> _cachedMembers;
        private static readonly object _lock = new();

        static SingletonScanner()
        {
            AppDomain.CurrentDomain.AssemblyLoad += (_, __) => InvalidateCache();
        }

        public static IEnumerable<InstanceEntry> Find()
        {
            var members = GetCachedMembers();
            if (members.Count == 0) yield break;

            // Build a (declaredType -> alive Unity Object) lookup once. We use
            // FindObjectsOfTypeAll which is safe to call from editor code, but
            // only call it ourselves — user code never sees us touch its
            // accessors.
            var aliveUnityByType = new Dictionary<Type, UnityEngine.Object>();
            foreach (var mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (mb == null) continue;
                var t = mb.GetType();
                if (!aliveUnityByType.ContainsKey(t)) aliveUnityByType[t] = mb;
            }
            foreach (var so in Resources.FindObjectsOfTypeAll<ScriptableObject>())
            {
                if (so == null) continue;
                var t = so.GetType();
                if (!aliveUnityByType.ContainsKey(t)) aliveUnityByType[t] = so;
            }

            foreach (var m in members)
            {
                var ownerType = m.DeclaringType;
                if (ownerType == null) continue;

                object value = null;

                if (typeof(UnityEngine.Object).IsAssignableFrom(ownerType))
                {
                    // Reverse-lookup: never invoke the user accessor. If the
                    // owning type is concrete and has a live instance, surface
                    // that. Subclass alive instances also count.
                    if (aliveUnityByType.TryGetValue(ownerType, out var direct))
                    {
                        value = direct;
                    }
                    else
                    {
                        foreach (var kv in aliveUnityByType)
                        {
                            if (ownerType.IsAssignableFrom(kv.Key))
                            {
                                value = kv.Value;
                                break;
                            }
                        }
                    }
                }
                else if (m is FieldInfo f)
                {
                    // Plain C# field — direct static read is safe; it doesn't
                    // run any user code.
                    try { value = f.GetValue(null); }
                    catch { continue; }
                }
                else
                {
                    // Plain C# property — we can't tell if the getter triggers
                    // initialization side effects. Skip.
                    continue;
                }

                if (value == null) continue;
                if (value is UnityEngine.Object uo && uo == null) continue;

                yield return new InstanceEntry
                {
                    Object = value as UnityEngine.Object,
                    DeclaredType = ownerType,
                    DisplayName = ownerType.Name + ".Instance",
                    TypeName = TypeFormatter.Short(ownerType),
                    SubLabel = "Singleton",
                    Category = InstanceCategory.Singleton,
                    IsActive = true,
                };
            }
        }

        public static void InvalidateCache()
        {
            lock (_lock) _cachedMembers = null;
        }

        private static List<MemberInfo> GetCachedMembers()
        {
            lock (_lock)
            {
                if (_cachedMembers != null) return _cachedMembers;
                _cachedMembers = ScanAssemblies();
                return _cachedMembers;
            }
        }

        private static List<MemberInfo> ScanAssemblies()
        {
            var found = new List<MemberInfo>(256);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (ShouldSkipAssembly(asm)) continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = SafeTypes(rtle); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null) continue;
                    if (!t.IsClass || t.IsAbstract) continue;
                    if (t.IsGenericTypeDefinition) continue;

                    var prop = t.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    if (prop != null && prop.GetMethod != null && prop.GetIndexParameters().Length == 0)
                    {
                        found.Add(prop);
                        continue;
                    }

                    var field = t.GetField("Instance",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    if (field != null) found.Add(field);
                }
            }
            return found;
        }

        private static Type[] SafeTypes(ReflectionTypeLoadException rtle)
        {
            var list = new List<Type>(rtle.Types?.Length ?? 0);
            if (rtle.Types != null)
                foreach (var t in rtle.Types)
                    if (t != null) list.Add(t);
            return list.ToArray();
        }

        private static bool ShouldSkipAssembly(Assembly asm)
        {
            if (asm == null) return true;
            if (asm.IsDynamic) return true;
            var name = asm.GetName().Name;
            if (string.IsNullOrEmpty(name)) return true;
            return name.StartsWith("UnityEngine", StringComparison.Ordinal)
                || name.StartsWith("UnityEditor", StringComparison.Ordinal)
                || name.StartsWith("Unity.",       StringComparison.Ordinal)
                || name.StartsWith("System",       StringComparison.Ordinal)
                || name.StartsWith("Microsoft.",   StringComparison.Ordinal)
                || name.StartsWith("Mono.",        StringComparison.Ordinal)
                || name.StartsWith("netstandard",  StringComparison.Ordinal)
                || name == "mscorlib"
                || name == "RoslynRepl.Editor";
        }
    }
}

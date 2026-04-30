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
                else if (m is PropertyInfo p)
                {
                    // Plain C# property — getter may lazy-init with arbitrary
                    // side effects, so we never invoke it. Try the canonical
                    // pattern of reading a sibling static backing field of the
                    // same type. If the user singleton has been touched once
                    // already, the field is non-null and we surface it.
                    var backing = FindBackingField(ownerType, p.PropertyType);
                    if (backing != null)
                    {
                        try { value = backing.GetValue(null); }
                        catch { continue; }
                    }
                    if (value == null) continue;
                }
                else
                {
                    continue;
                }

                if (value == null) continue;
                if (value is UnityEngine.Object uo && uo == null) continue;

                yield return new InstanceEntry
                {
                    Value = value,
                    DeclaredType = ownerType,
                    DisplayName = ownerType.Name + "." + m.Name,
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
                    // Skip compiler-generated types (lambda caches "<>c",
                    // closures "<…>d__N", anon types) — they self-return their
                    // own cache instance and would flood the list as noise.
                    if (t.Name.Length > 0 && t.Name[0] == '<') continue;

                    var member = FindSelfReturningStaticMember(t);
                    if (member != null) found.Add(member);
                }
            }
            return found;
        }

        // Looks for any *static* public member (property or field) on `t`
        // whose value type is related to `t`. Two acceptance shapes:
        //  (a) member type is `t` itself or a derived type — always a singleton
        //      candidate regardless of name (covers the canonical
        //      `public static Foo Instance;` declared on `Foo`).
        //  (b) member type is a base class or interface that `t` implements —
        //      accepted only when the name is a standard singleton accessor
        //      ("Instance", "it", "Current", …). This avoids false positives
        //      such as `public static IDisposable s_dispose` on unrelated
        //      classes while still catching `public static IService Current
        //      => _instance` and `public static BaseThing Instance = new
        //      DerivedThing()` declared on the derived class.
        // Property is preferred when a standard-named match exists; otherwise
        // the first match wins. Fields are searched only if no property hit.
        private static MemberInfo FindSelfReturningStaticMember(Type t)
        {
            const BindingFlags bf = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;

            PropertyInfo standardNamedProp = null;
            PropertyInfo firstProp = null;
            foreach (var p in t.GetProperties(bf))
            {
                if (p.GetMethod == null) continue;
                if (p.GetIndexParameters().Length > 0) continue;
                bool nameStandard = IsStandardSingletonName(p.Name);
                if (!IsSingletonShape(p.PropertyType, t, nameStandard)) continue;
                firstProp ??= p;
                if (nameStandard) { standardNamedProp = p; break; }
            }
            if (standardNamedProp != null) return standardNamedProp;
            if (firstProp != null) return firstProp;

            FieldInfo standardNamedField = null;
            FieldInfo firstField = null;
            foreach (var f in t.GetFields(bf))
            {
                bool nameStandard = IsStandardSingletonName(f.Name);
                if (!IsSingletonShape(f.FieldType, t, nameStandard)) continue;
                firstField ??= f;
                if (nameStandard) { standardNamedField = f; break; }
            }
            return standardNamedField ?? firstField;
        }

        // Decides whether a static member of `memberType` declared on
        // `declaringType` is shaped like a singleton accessor. See
        // FindSelfReturningStaticMember for the two accepted shapes.
        private static bool IsSingletonShape(Type memberType, Type declaringType, bool nameIsStandard)
        {
            if (memberType == null) return false;
            // Shape (a): member type ≡ declaring type or a subclass.
            if (declaringType.IsAssignableFrom(memberType)) return true;
            // Shape (b): member type is a base / interface of declaring type —
            // only accept with a standard singleton name to keep noise out.
            if (nameIsStandard && memberType.IsAssignableFrom(declaringType)) return true;
            return false;
        }

        private static bool IsStandardSingletonName(string name)
        {
            return name == "Instance" || name == "instance"
                || name == "it"       || name == "It"
                || name == "I"
                || name == "Self"     || name == "self"
                || name == "Current"  || name == "current"
                || name == "Singleton";
        }

        // Standard lazy-singleton pattern stores the live value in a static
        // backing field of the same type ("private static T _instance;").
        // We never invoke the user property getter (could lazy-init with
        // arbitrary side effects); instead we read the matching field. Returns
        // null if no matching static field is declared, in which case the
        // singleton is silently skipped.
        private static FieldInfo FindBackingField(Type owner, Type valueType)
        {
            const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic
                                  | BindingFlags.Static | BindingFlags.DeclaredOnly;
            FieldInfo first = null;
            foreach (var f in owner.GetFields(bf))
            {
                if (!valueType.IsAssignableFrom(f.FieldType)) continue;
                if (IsStandardBackingFieldName(f.Name)) return f;
                first ??= f;
            }
            return first;
        }

        private static bool IsStandardBackingFieldName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name == "_instance" || name == "s_instance" || name == "m_instance"
                || name == "instance"  || name == "_Instance" || name == "Instance"
                || name == "_it"        || name == "_self"     || name == "_current";
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

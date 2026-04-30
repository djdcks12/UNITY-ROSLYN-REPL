using System;
using System.Collections;
using UnityEngine;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// Produces a single-line preview string for a value. Used as the leaf
    /// representation and as the row preview in the result tree.
    /// </summary>
    public static class ValueFormatter
    {
        public static string Format(object value)
        {
            // Format() is the ToTree pipeline's "always-safe" preview producer.
            // Anything throwing in here aborts the entire tree build, so wrap
            // unconditionally and surface a marker instead. Specific known
            // hazards (collection Count getters, ToString) have their own
            // narrower try/catch inside FormatCore for nicer messages.
            try { return FormatCore(value); }
            catch (Exception ex)
            {
                var typeName = value != null ? TypeFormatter.Short(value.GetType()) : "?";
                return $"{typeName} <preview error: {ex.GetBaseException().Message}>";
            }
        }

        private static string FormatCore(object value)
        {
            if (value == null) return "null";

            var type = value.GetType();

            switch (value)
            {
                case string s: return "\"" + Escape(s) + "\"";
                case char c:   return "'" + c + "'";
                case bool b:   return b ? "true" : "false";
            }

            if (type.IsPrimitive) return value.ToString();
            if (type.IsEnum)      return type.Name + "." + value;

            // Unity types — keep one-liner descriptive
            switch (value)
            {
                case Vector2    v: return $"({v.x:0.###}, {v.y:0.###})";
                case Vector3    v: return $"({v.x:0.###}, {v.y:0.###}, {v.z:0.###})";
                case Vector4    v: return $"({v.x:0.###}, {v.y:0.###}, {v.z:0.###}, {v.w:0.###})";
                case Vector2Int v: return $"({v.x}, {v.y})";
                case Vector3Int v: return $"({v.x}, {v.y}, {v.z})";
                case Color      c: return $"RGBA({c.r:0.##}, {c.g:0.##}, {c.b:0.##}, {c.a:0.##})";
                case Color32    c: return $"RGBA32({c.r}, {c.g}, {c.b}, {c.a})";
                case Quaternion q: return $"Q({q.x:0.##}, {q.y:0.##}, {q.z:0.##}, {q.w:0.##})";
                case Rect       r: return $"Rect(x={r.x:0.##}, y={r.y:0.##}, w={r.width:0.##}, h={r.height:0.##})";
                case Bounds     b: return $"Bounds(center={Format(b.center)}, size={Format(b.size)})";
            }

            // Note: pattern-match `is UnityEngine.Object` returns true even
            // when the native side is destroyed (Unity's "fake null"), so
            // accessing .name / .gameObject would throw a NullReferenceException
            // from native bindings. Compare via the Unity == overload first.
            if (value is GameObject go)
            {
                if (go == null) return "GameObject (destroyed)";
                return $"GameObject \"{go.name}\" (id={go.GetInstanceID()})";
            }
            if (value is Component comp)
            {
                var compTypeName = comp.GetType().Name;
                if (comp == null) return $"{compTypeName} (destroyed)";
                var goRef = comp.gameObject;
                var ownerName = (goRef != null) ? goRef.name : "<destroyed>";
                return $"{compTypeName} on \"{ownerName}\"";
            }
            if (value is UnityEngine.Object uo)
            {
                var uoTypeName = uo.GetType().Name;
                if (uo == null) return $"{uoTypeName} (destroyed)";
                return $"{uoTypeName}: \"{uo.name}\"";
            }

            // Collections — Count is a user-overridable getter, so it can
            // throw on custom IDictionary / ICollection implementations.
            // Without this guard, Format would surface the exception out of
            // BuildNode before BuildDictChildren's try/catch ever runs.
            if (value is IDictionary dict)
            {
                int dictCount;
                try { dictCount = dict.Count; }
                catch { return $"{TypeFormatter.Short(type)}(IDictionary, count unavailable)"; }
                return $"{TypeFormatter.Short(type)}(count={dictCount})";
            }
            if (value is ICollection coll)
            {
                int collCount;
                try { collCount = coll.Count; }
                catch { return $"{TypeFormatter.Short(type)}(ICollection, count unavailable)"; }
                return $"{TypeFormatter.Short(type)}(count={collCount})";
            }
            if (value is IEnumerable && !(value is string))
                return $"{TypeFormatter.Short(type)}(IEnumerable)";

            // Generic fallback to ToString, with safety
            try
            {
                var s = value.ToString();
                if (string.IsNullOrEmpty(s) || s == type.FullName)
                    return TypeFormatter.Short(type);
                if (s.Length > 120) s = s.Substring(0, 117) + "...";
                return s;
            }
            catch (Exception ex)
            {
                return $"<ToString error: {ex.Message}>";
            }
        }

        private static string Escape(string s)
        {
            if (s == null) return string.Empty;
            if (s.Length > 80) s = s.Substring(0, 77) + "...";
            return s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\"", "\\\"");
        }
    }
}

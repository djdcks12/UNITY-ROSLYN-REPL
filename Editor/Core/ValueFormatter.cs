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

            if (value is GameObject go)
                return $"GameObject \"{go.name}\" (id={go.GetInstanceID()})";
            if (value is Component comp)
                return $"{comp.GetType().Name} on \"{(comp.gameObject != null ? comp.gameObject.name : "<destroyed>")}\"";
            if (value is UnityEngine.Object uo)
                return $"{uo.GetType().Name}: \"{uo.name}\"";

            // Collections
            if (value is IDictionary dict)
                return $"{TypeFormatter.Short(type)}(count={dict.Count})";
            if (value is ICollection coll)
                return $"{TypeFormatter.Short(type)}(count={coll.Count})";
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

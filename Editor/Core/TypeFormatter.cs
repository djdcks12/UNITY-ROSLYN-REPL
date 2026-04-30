using System;
using System.Linq;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// Produces human-readable short type names for the REPL UI.
    /// "List`1[Int32]" → "List&lt;int&gt;", primitives use C# keywords.
    /// </summary>
    public static class TypeFormatter
    {
        public static string Short(Type t)
        {
            if (t == null) return "?";
            if (t == typeof(int))     return "int";
            if (t == typeof(long))    return "long";
            if (t == typeof(short))   return "short";
            if (t == typeof(byte))    return "byte";
            if (t == typeof(sbyte))   return "sbyte";
            if (t == typeof(uint))    return "uint";
            if (t == typeof(ulong))   return "ulong";
            if (t == typeof(ushort))  return "ushort";
            if (t == typeof(float))   return "float";
            if (t == typeof(double))  return "double";
            if (t == typeof(decimal)) return "decimal";
            if (t == typeof(bool))    return "bool";
            if (t == typeof(string))  return "string";
            if (t == typeof(char))    return "char";
            if (t == typeof(object))  return "object";
            if (t == typeof(void))    return "void";

            if (t.IsArray)
            {
                var elem = Short(t.GetElementType());
                var rank = t.GetArrayRank();
                return rank == 1 ? elem + "[]" : elem + "[" + new string(',', rank - 1) + "]";
            }

            // Nullable<T> → T?
            var nullableArg = Nullable.GetUnderlyingType(t);
            if (nullableArg != null) return Short(nullableArg) + "?";

            if (t.IsGenericType)
            {
                var name = t.Name;
                int idx = name.IndexOf('`');
                if (idx > 0) name = name.Substring(0, idx);
                var args = string.Join(", ", t.GetGenericArguments().Select(Short));
                return name + "<" + args + ">";
            }

            return t.Name;
        }
    }
}

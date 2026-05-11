using System;
using System.Collections.Generic;
using System.Text;

namespace RoslynRepl.Editor.Patches
{
    /// <summary>
    /// Render a CLR <see cref="Type"/> as a string that is valid C#
    /// source — usable both as a type expression in a method signature
    /// (<c>Prefix({rendered} __instance, …)</c>) and inside a
    /// <c>typeof()</c> in the same generated wrapper.
    ///
    /// The historic patch wrapper used <see cref="Type.FullName"/>
    /// with <c>Replace('+', '.')</c>, which only worked for plain
    /// non-nested non-generic types. For closed generics like
    /// <c>List&lt;int&gt;</c> / <c>Dictionary&lt;string,int&gt;</c>
    /// the FullName output carries CLR-internal arity markers
    /// (<c>`1</c>, <c>`2</c>) and an assembly-qualified inner type
    /// list with embedded commas — neither is valid C# syntax, so the
    /// generated wrapper failed to compile even when the spec
    /// resolved to a real method. Centralising the renderer here
    /// (issue #42) keeps every code-gen call site honest about how
    /// generics, nested types, arrays, and nullables show up in
    /// emitted source.
    ///
    /// Open generics (generic type definitions, generic parameters)
    /// are intentionally rejected with a clear
    /// <see cref="NotSupportedException"/>: the patch wrapper only
    /// makes sense for fully-bound types, and generating
    /// <c>Foo&lt;T&gt;</c> source against an open T has no sane
    /// runtime mapping for the Harmony detour to install. The patch
    /// engine catches the exception upstream and surfaces it to the
    /// user as a friendly "open generic declaring types not supported"
    /// dialog.
    /// </summary>
    public static class CSharpTypeName
    {
        /// <summary>Render <paramref name="type"/> as a C# source-text
        /// type expression. Throws <see cref="NotSupportedException"/>
        /// for open generic types and generic parameters; throws
        /// <see cref="ArgumentNullException"/> if <paramref name="type"/>
        /// is <c>null</c>.</summary>
        public static string Render(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            var sb = new StringBuilder();
            AppendType(sb, type);
            return sb.ToString();
        }

        /// <summary>True iff <see cref="Render"/> would succeed for
        /// this type. Useful for picker / resolver call sites that
        /// want to short-circuit with a friendly message before they
        /// even start building a generated wrapper.</summary>
        public static bool IsRenderable(Type type)
        {
            if (type == null) return false;
            try { Render(type); return true; }
            catch (NotSupportedException) { return false; }
        }

        private static void AppendType(StringBuilder sb, Type type)
        {
            // Generic parameter (the `T` in `List<T>`) — the wrapper
            // can't emit a meaningful type expression for it.
            if (type.IsGenericParameter)
            {
                throw new NotSupportedException(
                    $"Cannot render open generic parameter '{type.Name}' as a C# type expression.");
            }

            // ByRef / Pointer: defensive — by-ref parameters are
            // rejected upstream in PatchEngine, but the renderer
            // still has to do *something* if we ever pass one in.
            // Drop the ref decoration and render the underlying
            // type so a caller-side debug print at least produces
            // valid C#.
            if (type.IsByRef)   { AppendType(sb, type.GetElementType()); return; }
            if (type.IsPointer) { AppendType(sb, type.GetElementType()); sb.Append('*'); return; }

            if (type.IsArray)
            {
                AppendType(sb, type.GetElementType());
                int rank = type.GetArrayRank();
                if (rank == 1) sb.Append("[]");
                else { sb.Append('['); sb.Append(',', rank - 1); sb.Append(']'); }
                return;
            }

            // Nullable<T> renders as `T?` for readability — the
            // canonical short form is itself a valid C# type
            // expression and round-trips through Roslyn parsing
            // identically to `System.Nullable<T>`.
            var nullableUnderlying = Nullable.GetUnderlyingType(type);
            if (nullableUnderlying != null)
            {
                AppendType(sb, nullableUnderlying);
                sb.Append('?');
                return;
            }

            if (type.IsGenericTypeDefinition)
            {
                throw new NotSupportedException(
                    $"Cannot render open generic type '{StripArity(type.Name)}<>' as a C# type expression.");
            }

            // Walk the nesting chain (outermost first). For nested
            // generic types — `Outer<T>.Inner` or
            // `Outer<T>.Inner<U>` — the closed form's
            // GetGenericArguments() returns *all* args (outer +
            // inner), so we have to attribute each arg to the level
            // that actually introduced it. Per-level "introduced
            // count" = totalArity at this level − parent's totalArity.
            var chain = new List<Type>();
            for (var t = type; t != null; t = t.DeclaringType)
                chain.Add(t);
            chain.Reverse();

            var allArgs = type.IsConstructedGenericType
                ? type.GetGenericArguments()
                : Array.Empty<Type>();

            var ns = chain[0].Namespace;
            if (!string.IsNullOrEmpty(ns))
            {
                sb.Append(ns);
                sb.Append('.');
            }

            int consumed = 0;
            for (int i = 0; i < chain.Count; i++)
            {
                if (i > 0) sb.Append('.');
                var level = chain[i];
                sb.Append(StripArity(level.Name));

                int parentArity = level.DeclaringType?.GetGenericArguments().Length ?? 0;
                int totalArity  = level.GetGenericArguments().Length;
                int introduced  = totalArity - parentArity;
                if (introduced > 0)
                {
                    if (consumed + introduced > allArgs.Length)
                    {
                        // Defensive: a level claims to introduce more
                        // generic args than the closed type carries.
                        // Means we hit an open generic mid-chain
                        // (e.g. `Outer<>.Inner` without binding Outer)
                        // — should have been caught by the
                        // IsGenericTypeDefinition check above; treat
                        // it the same way.
                        throw new NotSupportedException(
                            $"Cannot render partially-open generic type '{StripArity(type.Name)}' as a C# type expression.");
                    }
                    sb.Append('<');
                    for (int j = 0; j < introduced; j++)
                    {
                        if (j > 0) sb.Append(", ");
                        AppendType(sb, allArgs[consumed + j]);
                    }
                    sb.Append('>');
                    consumed += introduced;
                }
            }
        }

        private static string StripArity(string name)
        {
            int idx = name.IndexOf('`');
            return idx < 0 ? name : name.Substring(0, idx);
        }
    }
}

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
        public static string Render(Type type) => Render(type, substituteParameter: null);

        /// <summary>
        /// Render with an optional callback that supplies a C# source
        /// string for an open generic parameter. Used by
        /// <c>PatchSyntaxRewriter</c> to bridge method-level type
        /// arguments — the rewriter has the user's <c>TypeSyntax</c>
        /// for each method generic argument and routes them through
        /// here so a body that calls a generic helper like
        /// <c>__call&lt;Foo&gt;("Bar")</c> emits the right C# type
        /// expression at every nesting level.
        ///
        /// When <paramref name="substituteParameter"/> is <c>null</c>
        /// (the default), open generic parameters and generic type
        /// definitions are rejected with
        /// <see cref="NotSupportedException"/> just like the simple
        /// <see cref="Render(Type)"/> overload.
        ///
        /// When the callback is supplied, every <c>IsGenericParameter</c>
        /// type encountered (anywhere in the chain — top-level,
        /// generic argument, array element) is offered to the callback.
        /// A non-<c>null</c> return becomes the rendered string. A
        /// <c>null</c> return falls back to emitting the parameter
        /// name as a bare identifier — primarily useful for diagnostic
        /// scenarios where the resulting compile error pointing at
        /// `T` is still more informative than a silent
        /// <c>typeof(object)</c> cast.
        /// </summary>
        internal static string Render(Type type, Func<Type, string> substituteParameter)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            var sb = new StringBuilder();
            AppendType(sb, type, substituteParameter);
            return sb.ToString();
        }

        /// <summary>True iff <see cref="Render(Type)"/> would succeed
        /// for this type. Useful for picker / resolver call sites that
        /// want to short-circuit with a friendly message before they
        /// even start building a generated wrapper.</summary>
        public static bool IsRenderable(Type type)
        {
            if (type == null) return false;
            try { Render(type); return true; }
            catch (NotSupportedException) { return false; }
        }

        private static void AppendType(StringBuilder sb, Type type, Func<Type, string> substituteParameter)
        {
            // Generic parameter (the `T` in `List<T>`). With a
            // substitution callback the rewriter supplies the user's
            // TypeSyntax; without one we throw — the simple Render
            // overload has no way to produce valid C# for an open T.
            if (type.IsGenericParameter)
            {
                if (substituteParameter != null)
                {
                    var subbed = substituteParameter(type);
                    if (subbed != null) { sb.Append(subbed); return; }
                    // Fallback: the parameter is unbound. Emit the
                    // declared name so the eventual compile error
                    // points the user at the symbol they need to
                    // qualify (`T`) rather than at a silent
                    // typeof(object) substitution.
                    sb.Append(type.Name);
                    return;
                }
                throw new NotSupportedException(
                    $"Cannot render open generic parameter '{type.Name}' as a C# type expression.");
            }

            // ByRef / Pointer: defensive — by-ref parameters are
            // rejected upstream in PatchEngine, but the renderer
            // still has to do *something* if we ever pass one in.
            // Drop the ref decoration and render the underlying
            // type so a caller-side debug print at least produces
            // valid C#.
            if (type.IsByRef)   { AppendType(sb, type.GetElementType(), substituteParameter); return; }
            if (type.IsPointer) { AppendType(sb, type.GetElementType(), substituteParameter); sb.Append('*'); return; }

            if (type.IsArray)
            {
                AppendType(sb, type.GetElementType(), substituteParameter);
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
                AppendType(sb, nullableUnderlying, substituteParameter);
                sb.Append('?');
                return;
            }

            if (type.IsGenericTypeDefinition)
            {
                // Without substitution we have no way to fill the
                // generic parameter slots — reject with a friendly
                // message. With substitution the rewriter is responsible
                // for ensuring it never hands an open generic type
                // definition to the renderer (it only substitutes
                // parameters, not whole types).
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
                        AppendType(sb, allArgs[consumed + j], substituteParameter);
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

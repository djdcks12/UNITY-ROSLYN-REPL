using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Linq;

namespace RoslynRepl.Editor.Patches
{
    /// <summary>
    /// Phase D pre-flight: walk a candidate patch body looking for
    /// language constructs we can't compile cleanly through the
    /// runtime helpers. Returning a one-line user-readable reason
    /// before the heavy compile pipeline runs spares the user a wall
    /// of CS-error-codes from the Roslyn diagnostic loop.
    ///
    /// Each rejected shape is "not yet" rather than "never" — the
    /// intent is to surface the limitation early. Callers (PatchEngine
    /// .Apply) translate the reason into the spec's LastError so the
    /// Patches UI shows it on the row.
    ///
    /// What gets rejected (as of this commit):
    ///   - `await` / async (AwaitExpressionSyntax)
    ///   - `yield return` / `yield break` (YieldStatementSyntax)
    ///   - named arguments (`Foo(id: 1)`) — RenderExactCallArgs would
    ///     emit `(int)(id: 1)` which doesn't parse
    ///   - `ref` / `out` / `in` arguments at the call site — same
    ///     casting concern, plus the runtime helpers can't represent
    ///     by-ref invocation
    ///   - `base.field` / `base.SomeProperty` *non-invocation* reads
    ///     and writes — the wrapper supports `base.X(args)` only
    ///     (handled by the rewriter pre-pass through __callBase);
    ///     non-call uses of `base.X` would need a separate non-virtual
    ///     accessor helper that doesn't exist yet
    ///
    /// Lambdas / anonymous methods are *not* rejected — they're
    /// common Unity patterns (event handlers, LINQ predicates) and
    /// the rewriter's diagnostic-driven path handles their captured
    /// references the same as the surrounding scope.
    /// </summary>
    public static class PatchSyntaxValidator
    {
        public class Result
        {
            public bool Ok;
            public string Reason;

            public static Result Pass() => new Result { Ok = true };
            public static Result Fail(string reason) => new Result { Ok = false, Reason = reason };
        }

        public static Result Validate(string body)
        {
            if (string.IsNullOrEmpty(body)) return Result.Pass();

            // Wrap in a trivial method declaration so Roslyn parses
            // statements as method-body syntax rather than top-level
            // script statements (which would skew node kinds).
            //
            // IMPORTANT: this `async void` wrapper is *only* used for
            // parsing inside the validator. We throw the tree away
            // immediately after the visit. The Harmony Prefix that
            // PatchCodeGenerator actually emits stays
            // `public static bool Prefix(...)` (synchronous) — the
            // bool return is what tells Harmony to skip the original
            // method, and an `async void` Prefix would never produce
            // that signal. We use `async` here purely so the parser
            // produces an AwaitExpressionSyntax for `await x;` (in a
            // sync method body the parser keeps `await` as an
            // IdentifierName and the visitor would miss it). Once
            // detected, `await` is rejected — runtime patches stay
            // sync because async-aware Prefix semantics aren't
            // representable in Harmony's bool-skip protocol.
            //
            // `yield return` still parses as YieldStatementSyntax
            // even inside an `async` method — semantically invalid
            // (you can't have await + yield together), but Roslyn
            // produces the node anyway so both shapes are detectable
            // through this single wrapper.
            var wrapped = "class __PV { async void __M() { " + body + " } }";

            SyntaxTree tree;
            try { tree = CSharpSyntaxTree.ParseText(wrapped); }
            catch (Exception ex) { return Result.Fail("Patch body failed to parse: " + ex.Message); }

            var root = tree.GetRoot();

            foreach (var node in root.DescendantNodes())
            {
                if (node is AwaitExpressionSyntax)
                    return Result.Fail("Runtime patch does not support `await` / async syntax yet.");

                if (node is YieldStatementSyntax)
                    return Result.Fail("Runtime patch does not support `yield return` / `yield break` syntax yet.");

                if (node is ArgumentSyntax arg)
                {
                    if (arg.NameColon != null)
                        return Result.Fail("Runtime patch does not support named arguments yet (e.g., `Foo(id: 1)`). Pass arguments positionally for now.");
                    if (arg.RefKindKeyword.RawKind != 0)
                        return Result.Fail("Runtime patch does not support `ref` / `out` / `in` argument modifiers yet.");
                }

                // base.X non-invocation reads/writes. The rewriter
                // pre-pass handles `base.X(args)` via __callBase, but
                // bare `base.field` reads or `base.field = expr`
                // writes need a separate non-virtual accessor we
                // haven't built. Detecting them by walking
                // BaseExpressionSyntax → MemberAccess parent, then
                // checking whether the MemberAccess is itself the
                // .Expression of an InvocationExpression.
                if (node is BaseExpressionSyntax baseExpr)
                {
                    var maybeMa = baseExpr.Parent as MemberAccessExpressionSyntax;
                    if (maybeMa == null)
                    {
                        // `base` used outside member access — invalid C# anyway.
                        return Result.Fail("Runtime patch does not support `base` outside of `base.Method(...)` invocations yet.");
                    }
                    var asInvocation = maybeMa.Parent as InvocationExpressionSyntax;
                    if (asInvocation == null || asInvocation.Expression != maybeMa)
                    {
                        return Result.Fail("Runtime patch supports `base.X(...)` invocations only; `base.field` / `base.Property` reads or writes are not yet supported.");
                    }
                }
            }

            return Result.Pass();
        }
    }
}

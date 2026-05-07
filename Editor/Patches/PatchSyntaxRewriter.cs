using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynRepl.Editor.Patches
{
    /// <summary>
    /// Phase D — diagnostic-driven syntax rewriter that takes a wrapper
    /// source produced by <see cref="PatchCodeGenerator"/>, asks Roslyn
    /// to compile it, and rewrites every "name doesn't exist" / "cannot
    /// access due to its protection level" / "no definition" location
    /// into a reflection helper call. The user's natural code (`hp -=
    /// amount;`, `Singleton.Instance.PrivateField`, `MyClass.Cooldown =
    /// 0;`) compiles by routing the inaccessible names through the
    /// __get / __set / __call / __getOn / __setOn / __callOn /
    /// __getStatic / __setStatic / __callStatic helpers Phase D1
    /// emits into the wrapper.
    ///
    /// The rewriter does NOT try to be a static analyzer: it only
    /// touches names Roslyn itself flagged. Code that compiles
    /// untouched is left exactly as the user wrote it, so common
    /// expressions like `transform.position`, `Mathf.Min(...)`,
    /// `Vector3.zero` keep their fast direct-call codegen.
    ///
    /// Phase D2 scope: instance fields + properties on the declaring
    /// type (read context, simple `=` writes). Subsequent phases (D3+)
    /// extend this to method invocations, compound assignments,
    /// external instance access, and static access — all by adding
    /// new branches to <see cref="ClassifyAndRewrite"/> that share
    /// this scaffolding.
    /// </summary>
    public static class PatchSyntaxRewriter
    {
        public class Result
        {
            /// <summary>The (possibly rewritten) tree. Same reference as
            /// the input when no rewrites happened.</summary>
            public SyntaxTree Tree;

            /// <summary>Human-readable summary lines, one per rewrite,
            /// for surfacing in status messages or commit logs.
            /// `Singleton.Instance.PrivateField (read) → __getOn`.</summary>
            public List<string> Notes = new();

            /// <summary>Diagnostics the rewriter saw but couldn't
            /// repair (e.g., a misspelled local variable). Returned
            /// so the engine can decide whether to retry compile or
            /// surface them to the user as-is.</summary>
            public List<Diagnostic> Unhandled = new();

            public bool DidRewrite => Notes.Count > 0;
        }

        // The diagnostic IDs we know how to repair. Anything else stays
        // in Unhandled and bubbles up as a normal compile error.
        // - CS0103: "The name 'foo' does not exist in the current
        //           context" — typical for unqualified references to a
        //           declaring-type member, since the wrapper class
        //           sees the user body without `this`.
        // - CS0122: "'X.foo' is inaccessible due to its protection
        //           level" — fires when Roslyn does see the symbol but
        //           it's private/protected from outside. Common for
        //           `singleton.privateField`.
        // - CS1061: "'X' does not contain a definition for 'foo'"
        //           — fires when the symbol isn't visible at all (the
        //           private member is filtered out of metadata-only
        //           references). Common for cross-assembly access.
        private static readonly HashSet<string> _rewritableIds = new() { "CS0103", "CS0122", "CS1061" };

        public static Result Rewrite(SyntaxTree tree, Compilation compilation, Type declaringType)
        {
            if (tree == null) throw new ArgumentNullException(nameof(tree));
            if (compilation == null) throw new ArgumentNullException(nameof(compilation));
            if (declaringType == null) throw new ArgumentNullException(nameof(declaringType));

            var result = new Result { Tree = tree };
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            // Each rewrite is "for SyntaxNode N, replace it with this
            // new SyntaxNode". We collect all rewrites first, then
            // ReplaceNodes them in a single pass — this is the only
            // shape SyntaxNode supports for batched edits, since
            // immediate replacement would invalidate every later
            // span.
            var replacements = new Dictionary<SyntaxNode, SyntaxNode>();

            // Roslyn re-emits the same diagnostic ID at multiple
            // depths sometimes (CS0103 on the IdentifierName plus a
            // wrapping CS0103 on the parent). De-dup by source span
            // + ID so we visit each location once.
            var seen = new HashSet<(string Id, TextSpan Span)>();

            foreach (var diag in model.GetDiagnostics())
            {
                if (diag.Severity != DiagnosticSeverity.Error) continue;
                if (!_rewritableIds.Contains(diag.Id))
                {
                    result.Unhandled.Add(diag);
                    continue;
                }

                var span = diag.Location.SourceSpan;
                if (!seen.Add((diag.Id, span)))
                    continue;

                if (!TryClassifyAndRewrite(diag, root, model, declaringType, replacements, result.Notes))
                {
                    result.Unhandled.Add(diag);
                }
            }

            if (replacements.Count == 0)
                return result;

            var newRoot = root.ReplaceNodes(replacements.Keys, (orig, _) =>
                replacements.TryGetValue(orig, out var rep) ? rep : orig);

            result.Tree = CSharpSyntaxTree.Create((CSharpSyntaxNode)newRoot, (CSharpParseOptions)tree.Options, tree.FilePath, tree.Encoding);
            return result;
        }

        // Walks the diagnostic location up to the smallest expression we
        // can rewrite, decides which helper to substitute, and stages
        // the replacement. Returns false when the diagnostic looks
        // rewritable on paper but the surrounding shape isn't one we
        // handle yet (Phase D2: read context + simple `=` writes only;
        // later phases extend the handled set).
        private static bool TryClassifyAndRewrite(
            Diagnostic diag,
            SyntaxNode root,
            SemanticModel model,
            Type declaringType,
            Dictionary<SyntaxNode, SyntaxNode> replacements,
            List<string> notes)
        {
            var node = root.FindNode(diag.Location.SourceSpan, getInnermostNodeForTie: true);
            if (node == null) return false;

            // The diagnostic span often lands on an IdentifierName or
            // the ".name" half of a member access. Walk up to the
            // smallest expression that fully describes the access so
            // we can decide read vs. write context from its parent.
            var access = NormalizeAccess(node);
            if (access == null) return false;

            // The same access can be wrapped by an assignment (write)
            // or used directly (read). Decide here so we pick the
            // right helper.
            var ctx = ClassifyContext(access);

            switch (ctx.Kind)
            {
                case AccessKind.Read:
                    return TryStageRead(access, model, declaringType, replacements, notes);

                case AccessKind.SimpleWrite:
                    return TryStageSimpleWrite(ctx.AssignmentNode, access, model, declaringType, replacements, notes);

                default:
                    return false;
            }
        }

        // The "smallest expression for this access" — IdentifierName
        // (`hp`), MemberAccessExpression (`x.hp`, `Type.X`), or null
        // when the diagnostic sits on something we don't rewrite
        // (e.g., a missing using directive at the top of the file).
        private static ExpressionSyntax NormalizeAccess(SyntaxNode node)
        {
            // If the diagnostic landed inside the ".name" half of a
            // MemberAccessExpression, surface the whole expression
            // (we need both halves to construct the helper call).
            if (node is IdentifierNameSyntax id)
            {
                if (id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id)
                    return ma;
                return id;
            }
            if (node is MemberAccessExpressionSyntax mae)
                return mae;
            return null;
        }

        private enum AccessKind { Other, Read, SimpleWrite }
        private struct AccessContext
        {
            public AccessKind Kind;
            public AssignmentExpressionSyntax AssignmentNode;
        }

        // Decide read vs. write by the access's role in its parent.
        // `x = …` → SimpleWrite (rewrite the whole assignment).
        // Compound assignments, `++`, method invocations are Phase D3.
        private static AccessContext ClassifyContext(ExpressionSyntax access)
        {
            if (access.Parent is AssignmentExpressionSyntax assign && assign.Left == access)
            {
                if (assign.IsKind(SyntaxKind.SimpleAssignmentExpression))
                    return new AccessContext { Kind = AccessKind.SimpleWrite, AssignmentNode = assign };
                // Compound assignment / await assignment / etc. — D3.
                return new AccessContext { Kind = AccessKind.Other };
            }
            // The access is being invoked: foo(args). D3.
            if (access.Parent is InvocationExpressionSyntax inv && inv.Expression == access)
                return new AccessContext { Kind = AccessKind.Other };

            return new AccessContext { Kind = AccessKind.Read };
        }

        // ─── Read context ──────────────────────────────────────────
        // `hp` (unqualified) → __get<T>("hp")
        // `singleton.privateField` → __getOn<T>(singleton, "privateField")
        // `MyClass.PrivateStatic` → __getStatic<T>(typeof(MyClass), "PrivateStatic")
        // The T is inferred from declaringType's reflection metadata
        // when possible; otherwise falls back to `object` and lets
        // implicit conversion (or an assignment target's declared
        // type) pin it down.
        private static bool TryStageRead(
            ExpressionSyntax access,
            SemanticModel model,
            Type declaringType,
            Dictionary<SyntaxNode, SyntaxNode> replacements,
            List<string> notes)
        {
            // Unqualified identifier — assume member of declaringType.
            // Roslyn already failed to bind it; the user almost always
            // means a private member rather than a typo, since typos
            // would also show up as "name doesn't exist" with no
            // intent to rewrite anyway. If the name doesn't match a
            // declaringType member we leave the diagnostic alone.
            if (access is IdentifierNameSyntax id)
            {
                var name = id.Identifier.ValueText;
                var memberType = ResolveMemberType(declaringType, name, includeStatic: false);
                if (memberType == null) return false;

                var helper = SyntaxFactory.ParseExpression($"__get<{TypeRef(memberType)}>(\"{name}\")");
                replacements[access] = helper.WithTriviaFrom(access);
                notes.Add($"{name} (read) → __get");
                return true;
            }

            if (access is MemberAccessExpressionSyntax ma)
            {
                var memberName = ma.Name.Identifier.ValueText;

                // Static access first — `Type.Member`. We detect this
                // by asking the SemanticModel whether the LHS resolves
                // to a type symbol; reflection-side, we then look up
                // both the type and the static member to confirm.
                var lhsSymbol = model.GetSymbolInfo(ma.Expression).Symbol;
                if (lhsSymbol is INamedTypeSymbol nts)
                {
                    var staticType = ResolveTypeFromSymbol(nts);
                    if (staticType == null) return false;
                    var staticMemberType = ResolveMemberType(staticType, memberName, includeStatic: true, instanceOnly: false, staticOnly: true);
                    if (staticMemberType == null) return false;

                    var helper = SyntaxFactory.ParseExpression(
                        $"__getStatic<{TypeRef(staticMemberType)}>(typeof({nts.ToDisplayString()}), \"{memberName}\")");
                    replacements[access] = helper.WithTriviaFrom(access);
                    notes.Add($"{nts.Name}.{memberName} (static read) → __getStatic");
                    return true;
                }

                // Instance access on an external object. Try to learn
                // the LHS's runtime type from its TypeInfo so we can
                // reflection-check the member exists; if we can't,
                // fall through to a generic <object> helper and let
                // runtime reflection decide.
                var lhsTypeInfo = model.GetTypeInfo(ma.Expression);
                Type lhsType = ResolveTypeFromSymbol(lhsTypeInfo.Type);
                Type memberT = lhsType != null
                    ? ResolveMemberType(lhsType, memberName, includeStatic: true)
                    : null;
                string typeArg = memberT != null ? TypeRef(memberT) : "object";

                // Render the LHS expression unchanged inside the
                // helper call. ToFullString preserves trivia (helps
                // diagnostics in the generated source).
                var lhsText = ma.Expression.ToFullString();
                var helper2 = SyntaxFactory.ParseExpression($"__getOn<{typeArg}>({lhsText}, \"{memberName}\")");
                replacements[access] = helper2.WithTriviaFrom(access);
                notes.Add($"{lhsText.Trim()}.{memberName} (read) → __getOn");
                return true;
            }

            return false;
        }

        // ─── Simple-write context ──────────────────────────────────
        // `hp = expr` → __set("hp", expr)
        // `obj.field = expr` → __setOn(obj, "field", expr)
        // `Type.field = expr` → __setStatic(typeof(Type), "field", expr)
        private static bool TryStageSimpleWrite(
            AssignmentExpressionSyntax assignment,
            ExpressionSyntax accessLhs,
            SemanticModel model,
            Type declaringType,
            Dictionary<SyntaxNode, SyntaxNode> replacements,
            List<string> notes)
        {
            var rhsText = assignment.Right.ToFullString();

            if (accessLhs is IdentifierNameSyntax id)
            {
                var name = id.Identifier.ValueText;
                if (ResolveMemberType(declaringType, name, includeStatic: false) == null) return false;
                var call = SyntaxFactory.ParseExpression($"__set(\"{name}\", {rhsText})");
                replacements[assignment] = call.WithTriviaFrom(assignment);
                notes.Add($"{name} (write) → __set");
                return true;
            }

            if (accessLhs is MemberAccessExpressionSyntax ma)
            {
                var memberName = ma.Name.Identifier.ValueText;

                var lhsSymbol = model.GetSymbolInfo(ma.Expression).Symbol;
                if (lhsSymbol is INamedTypeSymbol nts)
                {
                    var staticType = ResolveTypeFromSymbol(nts);
                    if (staticType == null) return false;
                    if (ResolveMemberType(staticType, memberName, includeStatic: true, instanceOnly: false, staticOnly: true) == null)
                        return false;
                    var call = SyntaxFactory.ParseExpression(
                        $"__setStatic(typeof({nts.ToDisplayString()}), \"{memberName}\", {rhsText})");
                    replacements[assignment] = call.WithTriviaFrom(assignment);
                    notes.Add($"{nts.Name}.{memberName} (static write) → __setStatic");
                    return true;
                }

                var lhsText = ma.Expression.ToFullString();
                var call2 = SyntaxFactory.ParseExpression($"__setOn({lhsText}, \"{memberName}\", {rhsText})");
                replacements[assignment] = call2.WithTriviaFrom(assignment);
                notes.Add($"{lhsText.Trim()}.{memberName} (write) → __setOn");
                return true;
            }

            return false;
        }

        // ─── Helpers ──────────────────────────────────────────────

        // Find a field/property on the type and return its CLR type, or
        // null when no such member exists. Walks base types via
        // FlattenHierarchy. instance/static filtering is opt-in so the
        // same routine handles both same-instance and Type.X cases.
        private static Type ResolveMemberType(
            Type t,
            string name,
            bool includeStatic,
            bool instanceOnly = false,
            bool staticOnly = false)
        {
            BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
            if (!staticOnly) bf |= BindingFlags.Instance;
            if (!instanceOnly && (includeStatic || staticOnly)) bf |= BindingFlags.Static;

            var f = t.GetField(name, bf);
            if (f != null) return f.FieldType;
            var p = t.GetProperty(name, bf);
            if (p != null) return p.PropertyType;
            return null;
        }

        private static Type ResolveTypeFromSymbol(ITypeSymbol sym)
        {
            if (sym == null) return null;
            // Roslyn ITypeSymbol → System.Type. ToDisplayString gives
            // the namespace-qualified name including generic arity.
            // We don't carry across `+` for nested types because
            // Roslyn already uses dots; map back when looking up.
            var name = sym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", "");
            // For nested types Roslyn writes A.B but reflection uses A+B —
            // try both before giving up.
            var t = Type.GetType(name, throwOnError: false);
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(name, throwOnError: false)
                    ?? asm.GetType(name.Replace('.', '+'), throwOnError: false);
                if (t != null) return t;
            }
            return null;
        }

        // Render a System.Type as the C# type expression we paste into
        // the generated code. Avoids the open-generic edge cases by
        // routing through Roslyn-friendly names.
        private static string TypeRef(Type t)
        {
            if (t == null) return "object";
            if (t == typeof(void)) return "object"; // helpers return object then; T=object yields default
            if (t.IsByRef) t = t.GetElementType();
            // For generics + nested types, FullName uses `+` and a
            // backtick-arity suffix; turn that back into the C# form.
            // Phase D2 doesn't try to handle open generics, so a plain
            // FullName is fine for the names we expect (int, string,
            // Vector3, the user's plain-old types).
            return (t.FullName ?? t.Name).Replace('+', '.');
        }
    }
}

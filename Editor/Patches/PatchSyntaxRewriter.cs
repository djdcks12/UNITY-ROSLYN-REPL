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
        // - CS0117: "'X' does not contain a definition for 'foo'" —
        //           fires for static lookups when the private member
        //           is filtered out of metadata-only references; very
        //           common for `MyType.PrivateStaticMethod()`.
        // - CS0122: "'X.foo' is inaccessible due to its protection
        //           level" — fires when Roslyn does see the symbol but
        //           it's private/protected from outside. Common for
        //           `singleton.privateField`.
        // - CS1061: "'X' does not contain a definition for 'foo' and
        //           no accessible extension method..." — instance
        //           variant of CS0117 for cross-assembly access.
        private static readonly HashSet<string> _rewritableIds = new() { "CS0103", "CS0117", "CS0122", "CS1061" };

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

                case AccessKind.CompoundWrite:
                    return TryStageCompoundWrite(ctx.AssignmentNode, access, model, declaringType, replacements, notes);

                case AccessKind.Increment:
                    return TryStageIncrement(ctx.UnaryNode, access, model, declaringType, replacements, notes);

                case AccessKind.Invocation:
                    return TryStageInvocation(ctx.InvocationNode, access, model, declaringType, replacements, notes);

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

        private enum AccessKind { Other, Read, SimpleWrite, CompoundWrite, Increment, Invocation }
        private struct AccessContext
        {
            public AccessKind Kind;
            public AssignmentExpressionSyntax AssignmentNode;
            public ExpressionSyntax UnaryNode;        // PrefixUnary or PostfixUnary that wraps the access
            public InvocationExpressionSyntax InvocationNode;
        }

        // Decide read vs. write by the access's role in its parent.
        // `x = …` → SimpleWrite (rewrite the whole assignment).
        // `x += …` etc. → CompoundWrite (rewrite as set(x, get(x) op rhs)).
        // `x++` / `++x` → Increment (rewrite as set(x, get(x) ± 1)).
        // `x(args)` / `obj.x(args)` → Invocation (rewrite as call helper).
        private static AccessContext ClassifyContext(ExpressionSyntax access)
        {
            if (access.Parent is AssignmentExpressionSyntax assign && assign.Left == access)
            {
                if (assign.IsKind(SyntaxKind.SimpleAssignmentExpression))
                    return new AccessContext { Kind = AccessKind.SimpleWrite, AssignmentNode = assign };
                // Compound assignment: +=, -=, *=, /=, %=, &=, |=, ^=, <<=, >>=
                return new AccessContext { Kind = AccessKind.CompoundWrite, AssignmentNode = assign };
            }

            // ++x / x++ / --x / x--. We treat all four uniformly because
            // statement-context increments are by far the common case
            // and the result-value distinction (postfix returns the
            // pre-modify value) only matters in expression context.
            // The Phase D scope doc lists this caveat.
            if (access.Parent is PrefixUnaryExpressionSyntax pre
                && pre.Operand == access
                && (pre.IsKind(SyntaxKind.PreIncrementExpression) || pre.IsKind(SyntaxKind.PreDecrementExpression)))
            {
                return new AccessContext { Kind = AccessKind.Increment, UnaryNode = pre };
            }
            if (access.Parent is PostfixUnaryExpressionSyntax post
                && post.Operand == access
                && (post.IsKind(SyntaxKind.PostIncrementExpression) || post.IsKind(SyntaxKind.PostDecrementExpression)))
            {
                return new AccessContext { Kind = AccessKind.Increment, UnaryNode = post };
            }

            // The access is being invoked: foo(args) or obj.foo(args).
            if (access.Parent is InvocationExpressionSyntax inv && inv.Expression == access)
                return new AccessContext { Kind = AccessKind.Invocation, InvocationNode = inv };

            return new AccessContext { Kind = AccessKind.Read };
        }

        // ─── Read context ──────────────────────────────────────────
        // `hp` (unqualified) → __get<T>("hp")
        // `cacheVersion` (unqualified, private static on declaringType)
        //   → __getStatic<T>(typeof(decl), "cacheVersion")
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
            // declaringType member (instance OR static) we leave the
            // diagnostic alone.
            //
            // Source pulled from an instance method legitimately
            // references same-class private statics without a type
            // qualifier (`cacheVersion++;`, `ResetCache();`). Roslyn
            // reports those as CS0103 the same way it reports unbound
            // instance members; the wrapper sees both the same way
            // (no `this`, no enclosing type). So we try instance
            // first — by far the more common case — and fall back to
            // static on the declaringType when nothing instance-side
            // matches.
            if (access is IdentifierNameSyntax id)
            {
                var name = id.Identifier.ValueText;
                var kind = ClassifySelfMember(declaringType, name, out var memberType);
                if (kind == SelfMemberKind.NotFound) return false;

                if (kind == SelfMemberKind.Instance)
                {
                    var helper = SyntaxFactory.ParseExpression($"__get<{TypeRef(memberType)}>(\"{name}\")");
                    replacements[access] = helper.WithTriviaFrom(access);
                    notes.Add($"{name} (read) → __get");
                }
                else // Static fallback on the declaring type.
                {
                    var helper = SyntaxFactory.ParseExpression(
                        $"__getStatic<{TypeRef(memberType)}>(typeof({TypeRef(declaringType)}), \"{name}\")");
                    replacements[access] = helper.WithTriviaFrom(access);
                    notes.Add($"{name} (static read on self) → __getStatic");
                }
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
                var kind = ClassifySelfMember(declaringType, name, out _);
                if (kind == SelfMemberKind.NotFound) return false;

                ExpressionSyntax call;
                if (kind == SelfMemberKind.Instance)
                {
                    call = SyntaxFactory.ParseExpression($"__set(\"{name}\", {rhsText})");
                    notes.Add($"{name} (write) → __set");
                }
                else
                {
                    call = SyntaxFactory.ParseExpression(
                        $"__setStatic(typeof({TypeRef(declaringType)}), \"{name}\", {rhsText})");
                    notes.Add($"{name} (static write on self) → __setStatic");
                }
                replacements[assignment] = call.WithTriviaFrom(assignment);
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

        // ─── Compound-write context ────────────────────────────────
        // `hp += 5` → `__mutate<int>("hp", __cur => __cur + (5))`.
        // The mutator delegate captures rhs through closure; the
        // helper reads, applies the binary op once via the lambda,
        // and writes back. Compared to the naive `set(name, get(name)
        // op rhs)` rewrite, this passes the receiver / name / type
        // through the helper exactly once — important for external
        // members (`Manager.Instance.hp += d`) where evaluating
        // `Manager.Instance` twice could call a side-effecting
        // property accessor twice or even read and write different
        // objects.
        //
        // Right-hand side is wrapped in parens so operator precedence
        // (`hp += 1 << 2`) doesn't shift inside the lambda body.
        private static bool TryStageCompoundWrite(
            AssignmentExpressionSyntax assignment,
            ExpressionSyntax accessLhs,
            SemanticModel model,
            Type declaringType,
            Dictionary<SyntaxNode, SyntaxNode> replacements,
            List<string> notes)
        {
            var binOp = CompoundOperator(assignment.OperatorToken);
            if (binOp == null) return false;

            var rhsText = assignment.Right.ToFullString();

            if (accessLhs is IdentifierNameSyntax id)
            {
                var name = id.Identifier.ValueText;
                var kind = ClassifySelfMember(declaringType, name, out var memberType);
                if (kind == SelfMemberKind.NotFound) return false;
                var typeArg = TypeRef(memberType);

                ExpressionSyntax call;
                if (kind == SelfMemberKind.Instance)
                {
                    call = SyntaxFactory.ParseExpression(
                        $"__mutate<{typeArg}>(\"{name}\", __cur => __cur {binOp} ({rhsText}))");
                    notes.Add($"{name} ({assignment.OperatorToken.Text}) → __mutate");
                }
                else
                {
                    var typeRef = TypeRef(declaringType);
                    call = SyntaxFactory.ParseExpression(
                        $"__mutateStatic<{typeArg}>(typeof({typeRef}), \"{name}\", __cur => __cur {binOp} ({rhsText}))");
                    notes.Add($"{name} (static {assignment.OperatorToken.Text} on self) → __mutateStatic");
                }
                replacements[assignment] = call.WithTriviaFrom(assignment);
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
                    var t = ResolveMemberType(staticType, memberName, includeStatic: true, instanceOnly: false, staticOnly: true);
                    if (t == null) return false;
                    var typeArg = TypeRef(t);
                    var typeRef = nts.ToDisplayString();
                    var call = SyntaxFactory.ParseExpression(
                        $"__mutateStatic<{typeArg}>(typeof({typeRef}), \"{memberName}\", __cur => __cur {binOp} ({rhsText}))");
                    replacements[assignment] = call.WithTriviaFrom(assignment);
                    notes.Add($"{nts.Name}.{memberName} (static {assignment.OperatorToken.Text}) → __mutateStatic");
                    return true;
                }

                var lhsText = ma.Expression.ToFullString();
                var lhsTypeInfo = model.GetTypeInfo(ma.Expression);
                Type lhsType = ResolveTypeFromSymbol(lhsTypeInfo.Type);
                Type memberT = lhsType != null
                    ? ResolveMemberType(lhsType, memberName, includeStatic: true)
                    : null;
                string typeArg2 = memberT != null ? TypeRef(memberT) : "object";

                var call2 = SyntaxFactory.ParseExpression(
                    $"__mutateOn<{typeArg2}>({lhsText}, \"{memberName}\", __cur => __cur {binOp} ({rhsText}))");
                replacements[assignment] = call2.WithTriviaFrom(assignment);
                notes.Add($"{lhsText.Trim()}.{memberName} ({assignment.OperatorToken.Text}) → __mutateOn");
                return true;
            }

            return false;
        }

        // ─── Increment context ─────────────────────────────────────
        // `hp++` / `++hp` / `hp--` / `--hp` → __mutate<T>("hp", c => c ± 1).
        // Same single-evaluation reasoning as compound assignment;
        // the receiver / type / name go through the mutate helper
        // exactly once. Statement-context increments are exact;
        // expression-context postfix (`var x = hp++;`) reads as the
        // post-increment value rather than the pre-increment one
        // (documented Phase D limitation — the rewriter doesn't
        // synthesize a temp).
        private static bool TryStageIncrement(
            ExpressionSyntax unaryNode,
            ExpressionSyntax accessOperand,
            SemanticModel model,
            Type declaringType,
            Dictionary<SyntaxNode, SyntaxNode> replacements,
            List<string> notes)
        {
            string op =
                unaryNode.IsKind(SyntaxKind.PreIncrementExpression)
                    || unaryNode.IsKind(SyntaxKind.PostIncrementExpression)
                ? "+" : "-";

            if (accessOperand is IdentifierNameSyntax id)
            {
                var name = id.Identifier.ValueText;
                var kind = ClassifySelfMember(declaringType, name, out var memberType);
                if (kind == SelfMemberKind.NotFound) return false;
                var typeArg = TypeRef(memberType);

                ExpressionSyntax call;
                if (kind == SelfMemberKind.Instance)
                {
                    call = SyntaxFactory.ParseExpression(
                        $"__mutate<{typeArg}>(\"{name}\", __cur => __cur {op} 1)");
                    notes.Add($"{name} ({(op == "+" ? "++" : "--")}) → __mutate");
                }
                else
                {
                    var typeRef = TypeRef(declaringType);
                    call = SyntaxFactory.ParseExpression(
                        $"__mutateStatic<{typeArg}>(typeof({typeRef}), \"{name}\", __cur => __cur {op} 1)");
                    notes.Add($"{name} ({(op == "+" ? "++" : "--")} static on self) → __mutateStatic");
                }
                replacements[unaryNode] = call.WithTriviaFrom(unaryNode);
                return true;
            }

            if (accessOperand is MemberAccessExpressionSyntax ma)
            {
                var memberName = ma.Name.Identifier.ValueText;
                var lhsSymbol = model.GetSymbolInfo(ma.Expression).Symbol;
                if (lhsSymbol is INamedTypeSymbol nts)
                {
                    var staticType = ResolveTypeFromSymbol(nts);
                    if (staticType == null) return false;
                    var t = ResolveMemberType(staticType, memberName, includeStatic: true, instanceOnly: false, staticOnly: true);
                    if (t == null) return false;
                    var typeArg = TypeRef(t);
                    var typeRef = nts.ToDisplayString();
                    var call = SyntaxFactory.ParseExpression(
                        $"__mutateStatic<{typeArg}>(typeof({typeRef}), \"{memberName}\", __cur => __cur {op} 1)");
                    replacements[unaryNode] = call.WithTriviaFrom(unaryNode);
                    notes.Add($"{nts.Name}.{memberName} ({(op == "+" ? "++" : "--")} static) → __mutateStatic");
                    return true;
                }

                var lhsText = ma.Expression.ToFullString();
                var lhsTypeInfo = model.GetTypeInfo(ma.Expression);
                Type lhsType = ResolveTypeFromSymbol(lhsTypeInfo.Type);
                Type memberT = lhsType != null
                    ? ResolveMemberType(lhsType, memberName, includeStatic: true)
                    : null;
                string typeArg2 = memberT != null ? TypeRef(memberT) : "object";

                var call2 = SyntaxFactory.ParseExpression(
                    $"__mutateOn<{typeArg2}>({lhsText}, \"{memberName}\", __cur => __cur {op} 1)");
                replacements[unaryNode] = call2.WithTriviaFrom(unaryNode);
                notes.Add($"{lhsText.Trim()}.{memberName} ({(op == "+" ? "++" : "--")}) → __mutateOn");
                return true;
            }

            return false;
        }

        // ─── Invocation context ────────────────────────────────────
        // `Foo(args)` (unqualified) → `__call<R>("Foo", args)`.
        // `obj.PrivateMethod(args)` → `__callOn<R>(obj, "PrivateMethod", args)`.
        // `Type.PrivateStatic(args)` → `__callStatic<R>(typeof(Type), "PrivateStatic", args)`.
        // R is the method's return type if we can find a matching
        // overload by name + arity (an exact arg-type match would
        // require a SemanticModel-side reflection and we want this to
        // work even when Roslyn couldn't bind anything). Falls back to
        // `object` for void / unknown returns; that matches Phase A's
        // existing __call<T> behavior of returning default(T) when the
        // boxed result isn't assignable.
        private static bool TryStageInvocation(
            InvocationExpressionSyntax invocation,
            ExpressionSyntax accessExpr,
            SemanticModel model,
            Type declaringType,
            Dictionary<SyntaxNode, SyntaxNode> replacements,
            List<string> notes)
        {
            var argsList = invocation.ArgumentList.Arguments;
            int arity = argsList.Count;
            var argsText = string.Join(", ", argsList.Select(a => a.ToFullString()));

            if (accessExpr is IdentifierNameSyntax id)
            {
                var name = id.Identifier.ValueText;
                // Try instance methods first, then static fall-back —
                // mirrors the field/property logic in
                // ClassifySelfMember. Source pulled from an instance
                // method can call private static helpers without a
                // type qualifier (`ResetCache();`); without this
                // fall-back those bodies leak out as Unhandled CS0103.
                var ret = FindMethodReturnType(declaringType, name, arity, includeStatic: false);
                if (ret != null)
                {
                    var typeArg = TypeRef(ret);
                    var argsBlob = arity == 0 ? string.Empty : ", " + argsText;
                    var call = SyntaxFactory.ParseExpression($"__call<{typeArg}>(\"{name}\"{argsBlob})");
                    replacements[invocation] = call.WithTriviaFrom(invocation);
                    notes.Add($"{name}({arity} args) → __call");
                    return true;
                }
                ret = FindMethodReturnType(declaringType, name, arity, includeStatic: true, staticOnly: true);
                if (ret != null)
                {
                    var typeArg = TypeRef(ret);
                    var typeRef = TypeRef(declaringType);
                    var argsBlob = arity == 0 ? string.Empty : ", " + argsText;
                    var call = SyntaxFactory.ParseExpression(
                        $"__callStatic<{typeArg}>(typeof({typeRef}), \"{name}\"{argsBlob})");
                    replacements[invocation] = call.WithTriviaFrom(invocation);
                    notes.Add($"{name}({arity} args, static on self) → __callStatic");
                    return true;
                }
                return false;
            }

            if (accessExpr is MemberAccessExpressionSyntax ma)
            {
                var memberName = ma.Name.Identifier.ValueText;
                var lhsSymbol = model.GetSymbolInfo(ma.Expression).Symbol;
                if (lhsSymbol is INamedTypeSymbol nts)
                {
                    var staticType = ResolveTypeFromSymbol(nts);
                    if (staticType == null) return false;
                    var ret = FindMethodReturnType(staticType, memberName, arity, includeStatic: true, staticOnly: true);
                    if (ret == null) return false;
                    var typeArg = TypeRef(ret);
                    var typeRef = nts.ToDisplayString();
                    var argsBlob = arity == 0 ? string.Empty : ", " + argsText;
                    var call = SyntaxFactory.ParseExpression(
                        $"__callStatic<{typeArg}>(typeof({typeRef}), \"{memberName}\"{argsBlob})");
                    replacements[invocation] = call.WithTriviaFrom(invocation);
                    notes.Add($"{nts.Name}.{memberName}({arity} args) → __callStatic");
                    return true;
                }

                var lhsText = ma.Expression.ToFullString();
                var lhsTypeInfo = model.GetTypeInfo(ma.Expression);
                Type lhsType = ResolveTypeFromSymbol(lhsTypeInfo.Type);
                Type ret2 = lhsType != null
                    ? FindMethodReturnType(lhsType, memberName, arity, includeStatic: false)
                    : null;
                string typeArg2 = ret2 != null ? TypeRef(ret2) : "object";
                var argsBlob2 = arity == 0 ? string.Empty : ", " + argsText;
                var call2 = SyntaxFactory.ParseExpression(
                    $"__callOn<{typeArg2}>({lhsText}, \"{memberName}\"{argsBlob2})");
                replacements[invocation] = call2.WithTriviaFrom(invocation);
                notes.Add($"{lhsText.Trim()}.{memberName}({arity} args) → __callOn");
                return true;
            }

            return false;
        }

        // Map `+=` etc. to the binary operator. Returns null for
        // operators we don't expand (e.g., `??=` requires nullable
        // semantics that don't survive a get/set round-trip without
        // additional handling).
        private static string CompoundOperator(SyntaxToken op)
        {
            switch (op.Kind())
            {
                case SyntaxKind.PlusEqualsToken:        return "+";
                case SyntaxKind.MinusEqualsToken:       return "-";
                case SyntaxKind.AsteriskEqualsToken:    return "*";
                case SyntaxKind.SlashEqualsToken:       return "/";
                case SyntaxKind.PercentEqualsToken:     return "%";
                case SyntaxKind.AmpersandEqualsToken:   return "&";
                case SyntaxKind.BarEqualsToken:         return "|";
                case SyntaxKind.CaretEqualsToken:       return "^";
                case SyntaxKind.LessThanLessThanEqualsToken:        return "<<";
                case SyntaxKind.GreaterThanGreaterThanEqualsToken:  return ">>";
                default: return null;
            }
        }

        // Find a method by name + arg count and return its declared
        // return type. Picks the first arity-matching overload — we
        // can't bind by argument types statically here, so the helper
        // does the runtime tie-breaking. This is good enough for the
        // generic-arg decision; the helper itself re-resolves the
        // overload at invoke time.
        private static Type FindMethodReturnType(
            Type t,
            string name,
            int arity,
            bool includeStatic,
            bool staticOnly = false)
        {
            BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
            if (!staticOnly) bf |= BindingFlags.Instance;
            if (includeStatic || staticOnly) bf |= BindingFlags.Static;

            foreach (var m in t.GetMethods(bf))
            {
                if (m.Name == name && m.GetParameters().Length == arity) return m.ReturnType;
            }
            return null;
        }

        // ─── Helpers ──────────────────────────────────────────────

        // Self-class member classification for unqualified identifiers.
        // Source pulled out of an instance method legitimately
        // references same-class private *static* members without a
        // type qualifier (`cacheVersion`, `ResetCache()`), so the
        // fall-back from instance to static must happen on the
        // declaring type before we surrender the diagnostic.
        private enum SelfMemberKind { NotFound, Instance, Static }

        private static SelfMemberKind ClassifySelfMember(Type t, string name, out Type memberType)
        {
            memberType = ResolveMemberType(t, name, includeStatic: false);
            if (memberType != null) return SelfMemberKind.Instance;
            memberType = ResolveMemberType(t, name, includeStatic: true, instanceOnly: false, staticOnly: true);
            if (memberType != null) return SelfMemberKind.Static;
            return SelfMemberKind.NotFound;
        }

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
        // the generated code. Closed generics (`List<int>`,
        // `Dictionary<string, int>`, `Nullable<int>`) need recursive
        // rendering — Type.FullName for those is the assembly-qualified
        // form `System.Collections.Generic.List`1[[System.Int32, mscorlib,
        // ...]]` which is not valid C#. We strip the backtick arity,
        // recursively render each type argument, and re-emit the
        // angle-bracket form. Arrays go through GetElementType + "[]".
        private static string TypeRef(Type t)
        {
            if (t == null) return "object";
            if (t == typeof(void)) return "object"; // helpers return object then; T=object yields default
            if (t.IsByRef) t = t.GetElementType();
            return RenderCSharpType(t);
        }

        private static string RenderCSharpType(Type t)
        {
            if (t == null) return "object";
            if (t.IsByRef) t = t.GetElementType();

            // Arrays — recurse on element type, then re-attach the
            // bracket suffix. Multi-dim arrays use [,] etc. — preserve
            // the rank so the round-trip stays accurate.
            if (t.IsArray)
            {
                var elem = t.GetElementType();
                int rank = t.GetArrayRank();
                var brackets = "[" + new string(',', rank - 1) + "]";
                return RenderCSharpType(elem) + brackets;
            }

            // Pointer types — uncommon in patch bodies but cheap to
            // handle.
            if (t.IsPointer)
            {
                return RenderCSharpType(t.GetElementType()) + "*";
            }

            // Generics: strip the `N` arity suffix from the
            // (namespace-qualified, '+'→'.') name and re-emit each
            // type argument inside angle brackets. Open generics
            // (a generic parameter T) just render as their declared
            // name — patch bodies basically never see those.
            if (t.IsGenericParameter)
            {
                return t.Name;
            }
            if (t.IsGenericType)
            {
                var def = t.GetGenericTypeDefinition();
                var rawName = def.FullName ?? def.Name;
                int tick = rawName.IndexOf('`');
                var head = (tick >= 0 ? rawName.Substring(0, tick) : rawName).Replace('+', '.');
                var args = t.GetGenericArguments();
                if (args.Length == 0) return head;
                return head + "<" + string.Join(", ", args.Select(RenderCSharpType)) + ">";
            }

            // Plain non-generic — FullName covers namespace + nested
            // chain (with `+`), so map `+` to `.` for the C# form.
            return (t.FullName ?? t.Name).Replace('+', '.');
        }
    }
}

using ICSharpCode.CodeConverter.Util.FromRoslyn;
using Microsoft.CodeAnalysis.Editing;

namespace ICSharpCode.CodeConverter.VB;

/// <summary>
/// Roslyn's VB variable declarator reducer removes "As T" when the inferred type would be the same,
/// but doesn't check that the name still binds to the variable being declared.
/// VB names are case-insensitive, so C# names that only differ in case from another symbol are affected.
/// </summary>
internal static class ImplicitVariableTypeBinding
{
    private static readonly SyntaxAnnotation InferredAsClause = new("CodeConverter.InferredAsClause");

    /// <summary>
    /// States the type C# inferred for "var", so that <see cref="RemoveInferredAsClausesAsync"/> can keep it where VB needs it.
    /// </summary>
    /// <returns>null when the type can't be named, e.g. an anonymous type</returns>
    public static VBSyntax.SimpleAsClauseSyntax CreateInferredAsClauseOrNull(ITypeSymbol inferredType, SyntaxGenerator vbSyntaxGenerator) =>
        inferredType is null or IErrorTypeSymbol || inferredType.ContainsAnonymousType()
            ? null
            : VBasic.SyntaxFactory.SimpleAsClause((VBSyntax.TypeSyntax)vbSyntaxGenerator.TypeExpression(inferredType)).WithAdditionalAnnotations(InferredAsClause);

    /// <summary>
    /// Removes the "As" clauses <see cref="CreateInferredAsClauseOrNull"/> added, except where the name would then refer to something else.
    /// Doesn't depend on simplification, which is optional and can time out.
    /// </summary>
    /// <remarks>
    /// Conversion errors are reported from the first pass, which has these clauses, so a clash the rules miss wouldn't be reported.
    /// Instead, a clause whose removal brings a new error into its statement is put back, and kept out of simplification.
    /// </remarks>
    public static async Task<Document> RemoveInferredAsClausesAsync(Document document, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var declarators = root.GetAnnotatedNodes(InferredAsClause).Select(asClause => asClause.Parent).OfType<VBSyntax.VariableDeclaratorSyntax>().ToArray();
        if (!declarators.Any()) return document;
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var removable = declarators.Where(d => !WouldBindDifferentlyWithoutAsClause(d, semanticModel)).ToArray();
        if (!removable.Any()) return document;

        var withAsClauses = new Dictionary<SyntaxAnnotation, (SyntaxNode Original, SyntaxNode Rewritten)>();
        var withoutAsClauses = document.WithSyntaxRoot(root.ReplaceNodes(removable, (original, rewritten) => {
            var annotation = new SyntaxAnnotation(RemovedAsClauseKind);
            withAsClauses.Add(annotation, (original, rewritten));
            return WithoutAsClause(original, rewritten).WithAdditionalAnnotations(annotation);
        }));
        var newRoot = await withoutAsClauses.GetSyntaxRootAsync(cancellationToken);
        var errorsWithout = new StatementErrors(await withoutAsClauses.GetSemanticModelAsync(cancellationToken));
        var errorsWith = new StatementErrors(semanticModel);
        var toRestore = newRoot.GetAnnotatedNodes(RemovedAsClauseKind)
            .Where(n => errorsWithout.GetIds(n) is { Count: > 0 } newErrors
                        && newErrors.Except(errorsWith.GetIds(withAsClauses[n.GetAnnotations(RemovedAsClauseKind).Single()].Original)).Any())
            .ToArray();
        return toRestore.Any()
            ? withoutAsClauses.WithSyntaxRoot(newRoot.ReplaceNodes(toRestore, (original, _) => withAsClauses[original.GetAnnotations(RemovedAsClauseKind).Single()].Rewritten.WithAdditionalAnnotations(NeededAsClause)))
            : withoutAsClauses;
    }

    private const string RemovedAsClauseKind = "CodeConverter.RemovedInferredAsClause";
    private static readonly SyntaxAnnotation NeededAsClause = new("CodeConverter.NeededInferredAsClause");

    /// <summary>
    /// Error ids in the statement declaring a variable: for a loop, its first line, which holds the collection or bounds.
    /// </summary>
    /// <remarks>Getting diagnostics compiles the whole containing member, so it's done once per member.</remarks>
    private sealed class StatementErrors
    {
        private readonly SemanticModel _semanticModel;
        private readonly Dictionary<SyntaxNode, Diagnostic[]> _errorsByMember = new();

        public StatementErrors(SemanticModel semanticModel) => _semanticModel = semanticModel;

        public IReadOnlyCollection<string> GetIds(SyntaxNode declaratorOrControlVariable)
        {
            if (declaratorOrControlVariable.FirstAncestorOrSelf<VBSyntax.StatementSyntax>() is not { } statement) return Array.Empty<string>();
            var member = (SyntaxNode)statement.FirstAncestorOrSelf<VBSyntax.MethodBlockBaseSyntax>() ?? statement;
            if (!_errorsByMember.TryGetValue(member, out var errors)) {
                errors = _semanticModel.GetDiagnostics(member.Span).Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
                _errorsByMember.Add(member, errors);
            }
            return new HashSet<string>(errors.Where(d => statement.Span.Contains(d.Location.SourceSpan)).Select(d => d.Id));
        }
    }

    /// <remarks>A loop control variable without "As" is an identifier, as VB parses it.</remarks>
    private static SyntaxNode WithoutAsClause(VBSyntax.VariableDeclaratorSyntax original, VBSyntax.VariableDeclaratorSyntax rewritten) =>
        original.Parent is VBSyntax.ForStatementSyntax or VBSyntax.ForEachStatementSyntax
            ? VBasic.SyntaxFactory.IdentifierName(rewritten.Names.Single().Identifier).WithTriviaFrom(rewritten)
            : rewritten.WithAsClause(null);

    /// <summary>
    /// Whether removing the "As" clause of <paramref name="node"/> would make its name refer to something else.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A "For"/"For Each" control variable without "As" reuses a symbol of that name already in scope, unless it is a type, instead of declaring a new local:
    /// a field silently becomes the loop variable, a method is an error (BC30455).</item>
    /// <item>An inferred variable is in scope in its own initializer or "For Each" collection,
    /// so that expression can no longer refer to a type or namespace of the same name (BC30980).</item>
    /// </list>
    /// </remarks>
    public static bool WouldBindDifferentlyWithoutAsClause(SyntaxNode node, SemanticModel semanticModel) =>
        node is VBSyntax.VariableDeclaratorSyntax { AsClause: VBSyntax.SimpleAsClauseSyntax } declarator
        && GetExpressionsInScopeOfVariable(declarator) is { } expressionsInScope
        && (declarator.HasAnnotation(NeededAsClause) || declarator.Names.Any(n =>
            declarator.Parent is VBSyntax.ForStatementSyntax or VBSyntax.ForEachStatementSyntax && WouldReuseExistingSymbol(declarator.Parent.Parent, n, semanticModel)
            || RefersToTypeWithSameName(expressionsInScope, n, semanticModel)));

    private static SyntaxNode[] GetExpressionsInScopeOfVariable(VBSyntax.VariableDeclaratorSyntax declarator) =>
        declarator.Parent switch {
            VBSyntax.ForStatementSyntax => Array.Empty<SyntaxNode>(),
            VBSyntax.ForEachStatementSyntax f => new SyntaxNode[] { f.Expression },
            VBSyntax.LocalDeclarationStatementSyntax or VBSyntax.UsingStatementSyntax => new SyntaxNode[] { declarator.Initializer },
            _ => null
        };

    /// <remarks>
    /// Mirrors ForOrForEachBlockBinder.BuildLocals: with Option Infer On, a control variable without "As" is a new local
    /// only when looking its name up in the enclosing scope finds nothing, or only a type or type parameter
    /// (https://github.com/dotnet/roslyn/blob/main/src/Compilers/VisualBasic/Portable/Binding/ForOrForEachBlockBinder.vb).
    /// Look the name up at the start of the containing member: inside the loop its own variable hides the symbols that matter,
    /// and positions just before the loop can belong to the previous statement's scope. C# doesn't allow locals of enclosing blocks with the same name.
    /// </remarks>
    private static bool WouldReuseExistingSymbol(SyntaxNode loopBlock, VBSyntax.ModifiedIdentifierSyntax name, SemanticModel semanticModel) =>
        GetContainingMemberStart(loopBlock) is { } position
        && semanticModel.LookupSymbols(position, name: name.Identifier.ValueText).Any(s => s.Kind is not (SymbolKind.NamedType or SymbolKind.TypeParameter));

    private static int? GetContainingMemberStart(SyntaxNode node) =>
        ((SyntaxNode)node.FirstAncestorOrSelf<VBSyntax.MethodBlockBaseSyntax>()?.BlockStatement ?? node.FirstAncestorOrSelf<VBSyntax.DeclarationStatementSyntax>())?.SpanStart;

    /// <remarks>
    /// With "As T", "Dim icon As Icon = Icon.FromHandle(1)" and "For Each item As Item In Item.All()" are fine,
    /// because a member access on a value whose type has the same name may bind to the type.
    /// The reducer relies on that to reduce the type's qualifier, but without "As T" the name binds to the local being inferred.
    /// Other names in the initializer are only reduced when they'd still bind the same way, and a type in a type context ("New Icon()") never binds to the local.
    /// A bare type name is an expression only as a member access qualifier or a "NameOf" argument.
    /// "NameOf(Icon)" binds to the local even with "As String" (C# doesn't allow a variable in its own initializer, so the name meant something else),
    /// but without "As" it's BC30980, and with it the result is the same text.
    /// </remarks>
    private static bool RefersToTypeWithSameName(IEnumerable<SyntaxNode> expressions, VBSyntax.ModifiedIdentifierSyntax name, SemanticModel semanticModel) =>
        GetUnqualifiableTypeReferences(expressions, name.Identifier.ValueText, semanticModel).Any()
        || GetIdentifiers(expressions, name.Identifier.ValueText)
            .Any(n => semanticModel.GetSymbolInfo(n).Symbol is ILocalSymbol local && local.Locations.Any(l => l.SourceSpan == name.Identifier.Span));

    private static IEnumerable<VBSyntax.IdentifierNameSyntax> GetIdentifiers(IEnumerable<SyntaxNode> expressions, string name) =>
        expressions.Where(e => e != null).SelectMany(e => e.DescendantNodesAndSelf()).OfType<VBSyntax.IdentifierNameSyntax>()
            .Where(n => CaseInsensitiveComparison.Equals(n.Identifier.ValueText, name));

    /// <summary>
    /// "Item(Of Integer).Create()" can't bind to the type where a variable, parameter or member named "item" is in scope, even if its type is "Item(Of Integer)":
    /// the "Color Color" rule doesn't cover generic names (BC32045). Such a name is qualified (<see cref="QualifyHiddenGenericTypesAsync"/>)
    /// and keeps its qualifier.
    /// </summary>
    public static bool IsQualifiedHiddenGenericType(SyntaxNode node, SemanticModel semanticModel) =>
        GetQualifiedGenericName(node) is { } genericName
        && node.Parent is VBSyntax.MemberAccessExpressionSyntax
        && IsHiddenGenericType(genericName, semanticModel);

    /// <summary>
    /// Qualifies a generic type name that binds to something else of the same name, see <see cref="IsQualifiedHiddenGenericType"/>.
    /// Expansion already qualifies types in namespaces, but not those in the global namespace.
    /// </summary>
    public static async Task<Document> QualifyHiddenGenericTypesAsync(Document document, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var candidates = root.DescendantNodes().OfType<VBSyntax.GenericNameSyntax>()
            .Where(g => g.Parent is VBSyntax.MemberAccessExpressionSyntax { Expression: var leftmost } && leftmost == g).ToArray();
        if (!candidates.Any()) return document;
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var generator = SyntaxGenerator.GetGenerator(document);
        var qualifiers = candidates.Select(g => (Name: g, Qualifier: IsHiddenGenericType(g, semanticModel) ? GetType(g, semanticModel)?.ContainingSymbol : null))
            .Where(q => q.Qualifier is INamespaceOrTypeSymbol).ToDictionary(q => q.Name, q => (INamespaceOrTypeSymbol)q.Qualifier);
        return document.WithSyntaxRoot(root.ReplaceNodes(qualifiers.Keys, (original, rewritten) =>
            VBasic.SyntaxFactory.SimpleMemberAccessExpression(
                QualifierExpression(qualifiers[original], generator), rewritten.WithoutLeadingTrivia()).WithLeadingTrivia(rewritten.GetLeadingTrivia())));
    }

    private static VBSyntax.ExpressionSyntax QualifierExpression(INamespaceOrTypeSymbol qualifier, SyntaxGenerator generator) =>
        qualifier is INamespaceSymbol { IsGlobalNamespace: true }
            ? VBasic.SyntaxFactory.GlobalName()
            : (VBSyntax.ExpressionSyntax)generator.NameExpression(qualifier);

    /// <remarks>The variable makes the name an error already, so look the type up instead of binding the name.</remarks>
    private static INamedTypeSymbol GetType(VBSyntax.GenericNameSyntax genericName, SemanticModel semanticModel) =>
        GetContainingMemberStart(genericName) is { } position
            ? semanticModel.LookupNamespacesAndTypes(position, name: genericName.Identifier.ValueText).OfType<INamedTypeSymbol>()
                .FirstOrDefault(t => t.Arity == genericName.TypeArgumentList.Arguments.Count)
            : null;

    private static VBSyntax.GenericNameSyntax GetQualifiedGenericName(SyntaxNode node) => node switch {
        VBSyntax.MemberAccessExpressionSyntax { Name: VBSyntax.GenericNameSyntax g } => g,
        VBSyntax.QualifiedNameSyntax { Right: VBSyntax.GenericNameSyntax g } => g,
        _ => null
    };

    private static bool IsHiddenGenericType(VBSyntax.GenericNameSyntax genericName, SemanticModel semanticModel) =>
        semanticModel.LookupSymbols(genericName.SpanStart, name: genericName.Identifier.ValueText)
            .Any(s => s.Kind is not (SymbolKind.NamedType or SymbolKind.Namespace or SymbolKind.TypeParameter))
        && GetType(genericName, semanticModel) is not null;

    /// <summary>
    /// Names of a type or namespace in an expression that the reducer can leave unqualified,
    /// because the same name already refers to that symbol at the start of the containing member.
    /// A qualifier that is needed ("Res.Icon.Load()" outside "Res") stays, and the name can't bind to the local.
    /// </summary>
    private static IEnumerable<VBSyntax.IdentifierNameSyntax> GetUnqualifiableTypeReferences(IEnumerable<SyntaxNode> expressions, string name, SemanticModel semanticModel) =>
        GetIdentifiers(expressions, name)
            .Where(n => n.Parent is VBSyntax.MemberAccessExpressionSyntax or VBSyntax.NameOfExpressionSyntax
                        && semanticModel.GetSymbolInfo(n).Symbol is INamespaceOrTypeSymbol symbol
                        && (n.Parent is not VBSyntax.MemberAccessExpressionSyntax { Name: var qualified } || qualified != n
                            || GetContainingMemberStart(n) is { } position
                            && semanticModel.LookupNamespacesAndTypes(position, name: n.Identifier.ValueText)
                                .Any(s => SymbolEqualityComparer.Default.Equals(s.OriginalDefinition, symbol.OriginalDefinition))));
}

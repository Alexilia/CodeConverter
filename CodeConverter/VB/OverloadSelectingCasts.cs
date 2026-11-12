namespace ICSharpCode.CodeConverter.VB;

/// <summary>
/// Roslyn's VB cast reducer removes a cast in an object creation argument without checking that the same constructor is still chosen:
/// with "New(Img)" and "New(String)", "New P(CType(o, Img))" becomes "New P(o)" (BC30519);
/// with "New(Integer)" and "New(Long)", "New P(CInt(l))" silently calls "New(Long)" and "New P(CLng(i))" silently calls "New(Integer)".
/// A cast deeper in the argument does the same: "New P(If(b, CInt(l), 0))", and so does one that picks a lambda's return type:
/// "New P(Function() CInt(l))" with "New(Func(Of Integer))" and "New(Func(Of Long))".
/// Method calls keep such casts, but the check is the same for any call.
/// </summary>
internal static class OverloadSelectingCasts
{
    /// <summary>
    /// Whether <paramref name="node"/> is a cast within an argument that the call needs to bind to the member it binds to now.
    /// </summary>
    /// <remarks>
    /// Only the outermost of nested casts counts: "Simplifier.Expand" can wrap the argument in its own cast to the parameter type,
    /// "CType(CType(o, Img), Img)", and the reducer removes both when it checks each against the original tree.
    /// Casts around it in the argument are each redundant while it is there, so the reducer removes them too:
    /// "CType(If(b, CInt(l), 0), Integer)" has to be checked as "If(b, l, 0)".
    /// </remarks>
    public static bool IsSelectingOverload(SyntaxNode node, SemanticModel semanticModel) =>
        node is VBSyntax.ExpressionSyntax cast
        && GetCastOperand(cast) is not null
        && !IsOperandOfCast(cast)
        && GetArgumentOf(cast) is { Parent.Parent: VBSyntax.ExpressionSyntax call } argument
        && semanticModel.GetSymbolInfo(call).Symbol is { } member
        && BindsDifferently(semanticModel.GetSpeculativeSymbolInfo(call.SpanStart,
            call.ReplaceNode(argument.Expression, WithoutCastsAround(argument.Expression, cast)), SpeculativeBindingOption.BindAsExpression), member);

    /// <summary>
    /// Removes the parentheses around a cast argument that <see cref="IsSelectingOverload"/> keeps:
    /// they're redundant, but the reducer can't remove them once the cast is excluded from simplification.
    /// </summary>
    public static async Task<Document> UnparenthesizeSelectingCastArgumentsAsync(Document document, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var candidates = root.DescendantNodes().OfType<VBSyntax.ParenthesizedExpressionSyntax>()
            .Where(p => p.Parent is VBSyntax.SimpleArgumentSyntax && GetCastOperand(GetWithoutParentheses(p)) is not null).ToArray();
        if (!candidates.Any()) return document;
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var toUnwrap = candidates.Where(p => IsSelectingOverload(GetWithoutParentheses(p), semanticModel));
        return document.WithSyntaxRoot(root.ReplaceNodes(toUnwrap, (original, _) => GetWithoutParentheses(original).WithTriviaFrom(original)));
    }

    /// <remarks>
    /// Overload resolution failing without the cast leaves no symbol but the candidates.
    /// No symbol and no candidates means the call can't be bound on its own, e.g. "?.Show(o)" outside its conditional access: no conclusion.
    /// </remarks>
    private static bool BindsDifferently(SymbolInfo withoutCast, ISymbol member) =>
        withoutCast.Symbol is { } other
            ? !SymbolEqualityComparer.Default.Equals(other, member)
            : withoutCast.CandidateSymbols.Any();

    /// <returns>The innermost argument containing <paramref name="expression"/>, also through a lambda and its statements</returns>
    private static VBSyntax.SimpleArgumentSyntax GetArgumentOf(VBSyntax.ExpressionSyntax expression) =>
        expression.Ancestors().TakeWhile(a => a is not (VBSyntax.MethodBlockBaseSyntax or VBSyntax.DeclarationStatementSyntax))
            .OfType<VBSyntax.SimpleArgumentSyntax>().FirstOrDefault();

    /// <returns><paramref name="argument"/> without <paramref name="cast"/>, the casts within it and the casts around it</returns>
    private static VBSyntax.ExpressionSyntax WithoutCastsAround(VBSyntax.ExpressionSyntax argument, VBSyntax.ExpressionSyntax cast)
    {
        var castsAround = cast.AncestorsAndSelf().TakeWhile(n => n != argument.Parent).OfType<VBSyntax.ExpressionSyntax>()
            .Where(e => GetCastOperand(e) is not null).ToArray();
        return argument.ReplaceNodes(castsAround, (original, rewritten) =>
            original == cast ? GetInnermostOperand(rewritten) : GetCastOperand(rewritten));
    }

    private static bool IsOperandOfCast(VBSyntax.ExpressionSyntax cast)
    {
        SyntaxNode outer = cast;
        while (outer.Parent is VBSyntax.ParenthesizedExpressionSyntax parentheses) outer = parentheses;
        return outer.Parent is VBSyntax.ExpressionSyntax parent && GetCastOperand(parent) == outer;
    }

    private static VBSyntax.ExpressionSyntax GetInnermostOperand(VBSyntax.ExpressionSyntax cast)
    {
        while (GetCastOperand(GetWithoutParentheses(cast)) is { } operand) cast = operand;
        return GetWithoutParentheses(cast);
    }

    private static VBSyntax.ExpressionSyntax GetWithoutParentheses(VBSyntax.ExpressionSyntax expression)
    {
        while (expression is VBSyntax.ParenthesizedExpressionSyntax parentheses) expression = parentheses.Expression;
        return expression;
    }

    private static VBSyntax.ExpressionSyntax GetCastOperand(VBSyntax.ExpressionSyntax expression) => expression switch {
        VBSyntax.CastExpressionSyntax c => c.Expression,
        VBSyntax.PredefinedCastExpressionSyntax c => c.Expression,
        _ => null
    };
}

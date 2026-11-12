using System.Collections.Immutable;
using ICSharpCode.CodeConverter.CSharp;
using ICSharpCode.CodeConverter.VB;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Simplification;

namespace ICSharpCode.CodeConverter.Common;

internal static class DocumentExtensions
{
    public static async Task<Document> SimplifyStatementsAsync<TUsingDirectiveSyntax>(this Document convertedDocument, string unresolvedTypeDiagnosticId, CancellationToken cancellationToken)
        where TUsingDirectiveSyntax : SyntaxNode
    {
        Func<SyntaxNode, SemanticModel, bool> wouldBeSimplifiedIncorrectly =
            convertedDocument.Project.Language == LanguageNames.VisualBasic
                ? VbWouldBeSimplifiedIncorrectly
                : (n, _) => CsWouldBeSimplifiedIncorrectly(n);
        var originalRoot = await convertedDocument.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await convertedDocument.GetSemanticModelAsync(cancellationToken);
        var nodesWithUnresolvedTypes = semanticModel.GetDiagnostics(cancellationToken: cancellationToken)
            .Where(d => d.Id == unresolvedTypeDiagnosticId && d.Location.IsInSource)
            .Select(d => originalRoot.FindNode(d.Location.SourceSpan).GetAncestor<TUsingDirectiveSyntax>())
            .ToLookup(d => (SyntaxNode) d);
        var nodesToConsider = originalRoot
            .DescendantNodes()
            .Where(n => nodesWithUnresolvedTypes.Contains(n) || wouldBeSimplifiedIncorrectly(n, semanticModel))
            .ToImmutableHashSet();
        var doNotSimplify = nodesToConsider
            .SelectMany(n => n.AncestorsAndSelf())
            .ToImmutableHashSet();
        var toSimplify = originalRoot
            .DescendantNodes()
            .Where(n => !doNotSimplify.Contains(n));
        // Simplifier.Expand already annotated the nodes it added, e.g. a cast for an argument's conversion
        var newRoot = originalRoot.ReplaceNodes(toSimplify.Concat(nodesToConsider), (original, rewritten) =>
            nodesToConsider.Contains(original)
                ? rewritten.WithoutAnnotations(Simplifier.Annotation)
                : rewritten.WithAdditionalAnnotations(Simplifier.Annotation)
        );

        var document = await convertedDocument.WithReducedRootAsync(newRoot, cancellationToken);
        return document;
    }

    private static bool VbWouldBeSimplifiedIncorrectly(SyntaxNode n, SemanticModel semanticModel)
    {
        //Roslyn bug: empty argument list gets removed and changes behaviour: https://github.com/dotnet/roslyn/issues/40442
        // (Also null Expression blows up even though that's how conditional invocation on an IdentifierName happens)
        return n is VBSyntax.InvocationExpressionSyntax ies && (ies.ArgumentList?.Arguments.Any() != true || ies.Expression == null)
               || n is VBSyntax.TryCastExpressionSyntax
               // Roslyn bug: Tries to simplify to "InferredFieldInitializerSyntax" which cannot be placed within an ObjectCreationExpression https://github.com/icsharpcode/CodeConverter/issues/484
               || n is VBSyntax.ObjectCreationExpressionSyntax
               || ImplicitVariableTypeBinding.WouldBindDifferentlyWithoutAsClause(n, semanticModel)
               || ImplicitVariableTypeBinding.IsQualifiedHiddenGenericType(n, semanticModel)
               || OverloadSelectingCasts.IsSelectingOverload(n, semanticModel);
    }

    private static bool CsWouldBeSimplifiedIncorrectly(SyntaxNode n)
    {
        //"private int value = unchecked((int)0x80000010);" is simplified to "private int value = unchecked(0x80000010);"
        // which causes CS0266: "Cannot implicitly convert type 'uint' to 'int'"
        return (n is CSSyntax.CastExpressionSyntax { Parent : CSSyntax.CheckedExpressionSyntax });
    }

    public static async Task<Document> WithExpandedRootAsync(this Document document, CancellationToken cancellationToken)
    {
        if (document.Project.Language == LanguageNames.VisualBasic) {
            document = await ExpandAsync(document, VbNameExpander.Instance, cancellationToken);
        } else {
            document = await ExpandAsync(document, CsExpander.Instance, cancellationToken);
        }

        return document;
    }

    private static async Task<Document> ExpandAsync(Document document, ISyntaxExpander expander, CancellationToken cancellationToken)
    {
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var workspace = document.Project.Solution.Workspace;
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        try {
            var filteredNodes = root.DescendantNodes(n => expander.ShouldExpandWithinNode(n, semanticModel))
                .Where(n => expander.ShouldExpandNode(n, semanticModel));

            var newRoot = root.ReplaceNodes(filteredNodes, 
                (node, _) => TryExpandNode(expander, node, semanticModel, workspace, cancellationToken)
            );

            return document.WithSyntaxRoot(newRoot);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            var warningText = "Conversion warning: Name qualification failed for this file. " + ex;
            return document.WithSyntaxRoot(WithWarningAnnotation(root, warningText));
        }
    }

    private static SyntaxNode TryExpandNode(ISyntaxExpander expander, SyntaxNode node, SemanticModel semanticModel, Workspace workspace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try {
            return expander.ExpandNode(node, semanticModel, workspace);
        } catch (Exception ex) {
            var warningText = new ExceptionWithNodeInformation(ex, node, "Conversion warning").ToString();
            return WithWarningAnnotation(node, warningText);
        }
    }

    internal static async Task<Document> WithReducedRootAsync(this Document doc, SyntaxNode syntaxRoot, CancellationToken cancellationToken)
    {
        var root = syntaxRoot ?? await doc.GetSyntaxRootAsync(cancellationToken);
        var withSyntaxRoot = doc.WithSyntaxRoot(root);
        try {
            var options = await doc.GetOptionsAsync(cancellationToken);
            var newOptions = doc.Project.Language == LanguageNames.VisualBasic ? GetVBOptions(options) : GetCSOptions(options);
            return await Simplifier.ReduceAsync(withSyntaxRoot, newOptions, cancellationToken: cancellationToken);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            var warningText = "Conversion warning: Qualified name reduction failed for this file. " + ex;
            return doc.WithSyntaxRoot(WithWarningAnnotation(root, warningText));
        }
    }

    private static SyntaxNode WithWarningAnnotation(SyntaxNode node, string warningText)
    {
        return node.WithAdditionalAnnotations(new SyntaxAnnotation(AnnotationConstants.ConversionErrorAnnotationKind, warningText));
    }
    private static DocumentOptionSet GetVBOptions(DocumentOptionSet options)
    {
        return options;
    }
    private static DocumentOptionSet GetCSOptions(DocumentOptionSet options) {
        return options;
    }
}
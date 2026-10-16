using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SwarmCore.Scripting;

/// <summary>
/// Adds a host cancellation check to every authored loop body. This is not a
/// timer: useful code may run indefinitely while the objective is alive. It
/// ensures cancellation/no-progress decisions can actually stop a pure CPU
/// loop instead of returning to the caller while an orphaned loop keeps a core
/// busy for the rest of the process lifetime.
/// </summary>
public static class CSharpLoopCancellation
{
    public static string Inject(string source, string cancellationExpression = "Cancellation.ThrowIfCancellationRequested();")
    {
        if (string.IsNullOrWhiteSpace(source)) return source;
        var tree = CSharpSyntaxTree.ParseText(
            source,
            CSharpParseOptions.Default.WithKind(SourceCodeKind.Script).WithLanguageVersion(LanguageVersion.Latest));
        var root = (CompilationUnitSyntax)tree.GetRoot();
        // Force the generated async entry point to yield before user code. A
        // script whose first statement is `while(true)` would otherwise run
        // synchronously inside the delegate call and the host would never get
        // control back to observe cancellation/progress.
        var initialYield = SyntaxFactory.GlobalStatement(
            SyntaxFactory.ParseStatement("await Task.Yield();"));
        root = root.WithMembers(root.Members.Insert(0, initialYield));
        var rewritten = new Rewriter(cancellationExpression).Visit(root);
        return rewritten?.ToFullString() ?? source;
    }

    private sealed class Rewriter(string cancellationExpression) : CSharpSyntaxRewriter
    {
        private readonly StatementSyntax _check = SyntaxFactory.ParseStatement(cancellationExpression);

        public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
        {
            var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
            return visited.WithStatement(Guard(visited.Statement));
        }

        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
            return visited.WithStatement(Guard(visited.Statement));
        }

        public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
        {
            var visited = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node)!;
            return visited.WithStatement(Guard(visited.Statement));
        }

        public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
        {
            var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
            return visited.WithStatement(Guard(visited.Statement));
        }

        public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
        {
            var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
            return visited.WithStatement(Guard(visited.Statement));
        }

        private BlockSyntax Guard(StatementSyntax body)
        {
            var check = _check.WithLeadingTrivia(body.GetLeadingTrivia());
            return body is BlockSyntax block
                ? block.WithStatements(block.Statements.Insert(0, check))
                : SyntaxFactory.Block(check, body.WithoutLeadingTrivia());
        }
    }
}

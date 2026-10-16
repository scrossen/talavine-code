using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SwarmCore.Scripting;

/// <summary>
/// The run-time half of the hosted script guards. <see cref="CSharpExecutionGuards.Inject"/> places a
/// <see cref="Checkpoint"/> at the start of every loop body, every script-declared method, accessor,
/// constructor and local function, and every lambda compiled to a delegate, and an <see cref="Enter"/>
/// field initializer at the top of every script class. Each checkpoint observes the host's cancellation
/// token (so a CPU-bound loop or runaway recursion stops when the time limit fires) and refuses to go
/// deeper once the thread is near the end of its stack, turning what would be a process-ending stack
/// overflow into an ordinary exception.
/// </summary>
public static class ScriptExecutionGuard
{
    /// <summary>The statement <see cref="CSharpExecutionGuards"/> injects.</summary>
    public const string CheckpointStatement = "global::SwarmCore.Scripting.ScriptExecutionGuard.Checkpoint();";

    /// <summary>The expression <see cref="CSharpExecutionGuards"/> uses as the first field initializer of script classes.</summary>
    public const string EnterExpression = "global::SwarmCore.Scripting.ScriptExecutionGuard.Enter()";

    private static readonly AsyncLocal<CancellationToken> Token = new();

    /// <summary>Binds <paramref name="token"/> to the current flow until the returned scope is disposed.</summary>
    public static IDisposable Begin(CancellationToken token)
    {
        var previous = Token.Value;
        Token.Value = token;
        return new Scope(previous);
    }

    public static void Checkpoint()
    {
        Token.Value.ThrowIfCancellationRequested();
        RuntimeHelpers.EnsureSufficientExecutionStack();
    }

    public static bool Enter()
    {
        Checkpoint();
        return true;
    }

    private sealed class Scope(CancellationToken previous) : IDisposable
    {
        public void Dispose() => Token.Value = previous;
    }
}

/// <summary>
/// Rewrites an analyzed script so it cooperates with <see cref="ScriptExecutionGuard"/>. The rewrite is
/// driven by the semantic model of the compilation the allow-list approved: lambdas that become
/// expression trees (LINQ providers translate those, they never run as script code) are left alone,
/// and expression bodies are expanded to blocks with the correct return shape.
/// </summary>
public static class CSharpExecutionGuards
{
    /// <summary>Returns the guarded source for <paramref name="tree"/>, bound with <paramref name="model"/>.</summary>
    public static string Inject(SemanticModel model, SyntaxTree tree, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tree);
        var rewritten = new Rewriter(model, cancellationToken).Visit(tree.GetRoot(cancellationToken));
        return rewritten.ToFullString();
    }

    private sealed class Rewriter(SemanticModel model, CancellationToken cancellationToken) : CSharpSyntaxRewriter
    {
        private int _expressionTreeDepth;

        private static StatementSyntax Checkpoint() => SyntaxFactory.ParseStatement(ScriptExecutionGuard.CheckpointStatement + " ");

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

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var returnsValue = ReturnsValue(model.GetDeclaredSymbol(node, cancellationToken));
            var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
            return visited.Body is not null || visited.ExpressionBody is not null
                ? visited.WithBody(GuardBody(visited.Body, visited.ExpressionBody, returnsValue)).WithExpressionBody(null).WithSemicolonToken(default)
                : visited;
        }

        public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
        {
            var returnsValue = ReturnsValue(model.GetDeclaredSymbol(node, cancellationToken) as IMethodSymbol);
            var visited = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
            return visited.Body is not null || visited.ExpressionBody is not null
                ? visited.WithBody(GuardBody(visited.Body, visited.ExpressionBody, returnsValue)).WithExpressionBody(null).WithSemicolonToken(default)
                : visited;
        }

        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            var visited = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;
            return visited.Body is not null || visited.ExpressionBody is not null
                ? visited.WithBody(GuardBody(visited.Body, visited.ExpressionBody, returnsValue: false)).WithExpressionBody(null).WithSemicolonToken(default)
                : visited;
        }

        public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node)
        {
            var visited = (OperatorDeclarationSyntax)base.VisitOperatorDeclaration(node)!;
            return visited.Body is not null || visited.ExpressionBody is not null
                ? visited.WithBody(GuardBody(visited.Body, visited.ExpressionBody, returnsValue: true)).WithExpressionBody(null).WithSemicolonToken(default)
                : visited;
        }

        public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
        {
            var visited = (ConversionOperatorDeclarationSyntax)base.VisitConversionOperatorDeclaration(node)!;
            return visited.Body is not null || visited.ExpressionBody is not null
                ? visited.WithBody(GuardBody(visited.Body, visited.ExpressionBody, returnsValue: true)).WithExpressionBody(null).WithSemicolonToken(default)
                : visited;
        }

        public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node)
        {
            var returnsValue = ReturnsValue(model.GetDeclaredSymbol(node, cancellationToken));
            var visited = (AccessorDeclarationSyntax)base.VisitAccessorDeclaration(node)!;
            return visited.Body is not null || visited.ExpressionBody is not null
                ? visited.WithBody(GuardBody(visited.Body, visited.ExpressionBody, returnsValue)).WithExpressionBody(null).WithSemicolonToken(default)
                : visited;
        }

        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            var visited = (PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!;
            return visited.ExpressionBody is { } arrow
                ? visited.WithExpressionBody(null).WithSemicolonToken(default).WithAccessorList(GetterOnly(arrow))
                : visited;
        }

        public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node)
        {
            var visited = (IndexerDeclarationSyntax)base.VisitIndexerDeclaration(node)!;
            return visited.ExpressionBody is { } arrow
                ? visited.WithExpressionBody(null).WithSemicolonToken(default).WithAccessorList(GetterOnly(arrow))
                : visited;
        }

        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
            if (visited.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            {
                return visited;
            }

            // Field initializers run before any constructor body, so recursion through them
            // (class A { A next = new A(); }) is guarded by a first initializer of its own.
            var guardField = (MemberDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(
                $"private readonly bool __swarmScriptGuard = {ScriptExecutionGuard.EnterExpression}; ")!;
            return visited.WithMembers(visited.Members.Insert(0, guardField));
        }

        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
            => VisitLambda(node, n => base.VisitSimpleLambdaExpression((SimpleLambdaExpressionSyntax)n));

        public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
            => VisitLambda(node, n => base.VisitParenthesizedLambdaExpression((ParenthesizedLambdaExpressionSyntax)n));

        public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
            => VisitLambda(node, n => base.VisitAnonymousMethodExpression((AnonymousMethodExpressionSyntax)n));

        private SyntaxNode? VisitLambda(AnonymousFunctionExpressionSyntax node, Func<SyntaxNode, SyntaxNode?> visitChildren)
        {
            var isExpressionTree = _expressionTreeDepth > 0 || IsExpressionTree(model.GetTypeInfo(node, cancellationToken).ConvertedType);
            if (isExpressionTree)
            {
                _expressionTreeDepth++;
                try
                {
                    return visitChildren(node);
                }
                finally
                {
                    _expressionTreeDepth--;
                }
            }

            var returnsValue = ReturnsValue(model.GetSymbolInfo(node, cancellationToken).Symbol as IMethodSymbol);
            var visited = (AnonymousFunctionExpressionSyntax)visitChildren(node)!;
            var body = visited.Block is not null
                ? GuardBody(visited.Block, null, returnsValue)
                : GuardBody(null, SyntaxFactory.ArrowExpressionClause(visited.ExpressionBody!), returnsValue);
            return visited switch
            {
                SimpleLambdaExpressionSyntax simple => simple.WithExpressionBody(null).WithBlock(body),
                ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.WithExpressionBody(null).WithBlock(body),
                AnonymousMethodExpressionSyntax anonymous => anonymous.WithBlock(body),
                _ => visited,
            };
        }

        private static bool IsExpressionTree(ITypeSymbol? type)
        {
            for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
            {
                if (current.ContainingNamespace?.ToDisplayString() == "System.Linq.Expressions"
                    && current.Name is "Expression" or "LambdaExpression")
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when the function's body must <c>return</c> its expression: it is not void and not an async
        /// method whose task carries no value.
        /// </summary>
        private static bool ReturnsValue(IMethodSymbol? method)
        {
            if (method is null || method.ReturnsVoid)
            {
                return false;
            }

            if (method.IsAsync && method.ReturnType is INamedTypeSymbol { IsGenericType: false } task
                && task.Name is "Task" or "ValueTask")
            {
                return false;
            }

            return true;
        }

        private static StatementSyntax Guard(StatementSyntax body)
            => body is BlockSyntax block
                ? block.WithStatements(block.Statements.Insert(0, Checkpoint()))
                : SyntaxFactory.Block(Checkpoint(), body);

        private static BlockSyntax GuardBody(BlockSyntax? block, ArrowExpressionClauseSyntax? arrow, bool returnsValue)
        {
            if (block is not null)
            {
                return block.WithStatements(block.Statements.Insert(0, Checkpoint()));
            }

            var expression = arrow!.Expression;
            // The newline keeps the semicolon out of any trailing single-line comment on the expression.
            var statement = expression is ThrowExpressionSyntax thrown
                ? SyntaxFactory.ParseStatement("throw " + thrown.Expression.ToFullString() + "\n;")
                : SyntaxFactory.ParseStatement((returnsValue ? "return " : string.Empty) + expression.ToFullString() + "\n;");
            return SyntaxFactory.Block(Checkpoint(), statement);
        }

        private static AccessorListSyntax GetterOnly(ArrowExpressionClauseSyntax arrow)
            => SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration, GuardBody(null, arrow, returnsValue: true))));
    }
}

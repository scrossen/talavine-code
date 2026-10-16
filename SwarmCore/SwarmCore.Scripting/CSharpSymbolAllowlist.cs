using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SwarmCore.Scripting;

/// <summary>
/// Symbol-level allow-list check for agent- or LLM-authored C# that a host runs in-process for a
/// read-only tier. It walks the host's real (error-free) compilation on the semantic model and refuses
/// every symbol, expression type and declaration that <see cref="CSharpSymbolAllowlistPolicy"/> does not
/// allow, plus the constructs a symbol check cannot see: <c>#r</c>/<c>#load</c>, unsafe code and pointers,
/// <c>dynamic</c>, <c>goto</c>, finalizers, attributes, records and structs, inheritance from runtime
/// types, <c>async void</c>, and deferred queries that escape a single-assignment top-level variable.
/// Unlike <see cref="CSharpSemanticSafety"/> (a block-list of known-dangerous prefixes), anything the
/// policy does not name is refused, so a new or overlooked runtime API is unreachable by default.
/// </summary>
public static class CSharpSymbolAllowlist
{
    /// <summary>
    /// Returns the refusal reasons (empty when the script is allowed). <paramref name="compilation"/> must be
    /// the compilation the host will run, with its real references and typed globals, and must have no errors.
    /// </summary>
    public static IReadOnlyList<string> Analyze(Compilation compilation, CSharpSymbolAllowlistPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(policy);

        var violations = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees)
        {
            var root = tree.GetRoot(cancellationToken);
            if (!CheckShape(root, policy, violations))
            {
                continue;
            }

            new Walker(compilation, compilation.GetSemanticModel(tree), policy, violations, cancellationToken).Visit(root);
        }

        return violations.ToList();
    }

    /// <summary>Syntax-only refusals, checked before binding. Returns false when the tree is too deep to bind safely.</summary>
    private static bool CheckShape(SyntaxNode root, CSharpSymbolAllowlistPolicy policy, ISet<string> violations)
    {
        var stack = new Stack<(SyntaxNode Node, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (depth > policy.MaxSyntaxDepth)
            {
                violations.Add($"The script is nested more than {policy.MaxSyntaxDepth} levels deep.");
                return false;
            }

            foreach (var child in node.ChildNodes())
            {
                stack.Push((child, depth + 1));
            }
        }

        foreach (var directive in root.DescendantNodes(descendIntoTrivia: true).OfType<DirectiveTriviaSyntax>())
        {
            if (directive is ReferenceDirectiveTriviaSyntax or LoadDirectiveTriviaSyntax)
            {
                violations.Add("#r and #load directives are not allowed.");
            }
        }

        foreach (var node in root.DescendantNodes())
        {
            var refused = node switch
            {
                UnsafeStatementSyntax or FixedStatementSyntax or PointerTypeSyntax or FunctionPointerTypeSyntax
                    or StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax
                    or MakeRefExpressionSyntax or RefTypeExpressionSyntax or RefValueExpressionSyntax
                    => "unsafe code, pointers, stackalloc and typed references are not allowed",
                PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.AddressOfExpression) || prefix.IsKind(SyntaxKind.PointerIndirectionExpression)
                    => "unsafe code, pointers, stackalloc and typed references are not allowed",
                MemberAccessExpressionSyntax access when access.IsKind(SyntaxKind.PointerMemberAccessExpression)
                    => "unsafe code, pointers, stackalloc and typed references are not allowed",
                ExpressionSyntax expression when expression.IsKind(SyntaxKind.ArgListExpression)
                    => "__arglist is not allowed",
                GotoStatementSyntax => "goto is not allowed",
                DestructorDeclarationSyntax => "finalizers are not allowed",
                RecordDeclarationSyntax => "records are not allowed; declare a class",
                StructDeclarationSyntax => "structs are not allowed; declare a class",
                AttributeListSyntax => "attributes are not allowed",
                ExternAliasDirectiveSyntax => "extern aliases are not allowed",
                _ => null,
            };
            if (refused is not null)
            {
                violations.Add(char.ToUpperInvariant(refused[0]) + refused[1..] + ".");
            }

            if (node is MemberDeclarationSyntax member
                && member.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword) || m.IsKind(SyntaxKind.UnsafeKeyword) || m.IsKind(SyntaxKind.FixedKeyword)))
            {
                violations.Add("Extern, unsafe and fixed members are not allowed.");
            }

            if (node is LocalFunctionStatementSyntax local
                && local.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword) || m.IsKind(SyntaxKind.UnsafeKeyword)))
            {
                violations.Add("Extern, unsafe and fixed members are not allowed.");
            }
        }

        return true;
    }

    private sealed class Walker(
        Compilation compilation,
        SemanticModel model,
        CSharpSymbolAllowlistPolicy policy,
        ISet<string> violations,
        CancellationToken cancellationToken)
    {
        private static readonly SymbolDisplayFormat Display = SymbolDisplayFormat.CSharpErrorMessageFormat;
        private readonly Dictionary<ITypeSymbol, bool> _typeVerdicts = new(SymbolEqualityComparer.IncludeNullability);

        public void Visit(SyntaxNode root)
        {
            foreach (var node in root.DescendantNodesAndSelf())
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckReferencedSymbols(node);
                CheckDeclaredSymbol(node);
                if (node is ExpressionSyntax expression)
                {
                    CheckExpression(expression);
                }
                CheckDeferredQueryWrites(node);
            }
        }

        private void CheckReferencedSymbols(SyntaxNode node)
        {
            var info = model.GetSymbolInfo(node, cancellationToken);
            if (info.Symbol is { } symbol)
            {
                CheckSymbol(symbol);
            }
            else
            {
                foreach (var candidate in info.CandidateSymbols)
                {
                    CheckSymbol(candidate);
                }
            }

            if (node is QueryClauseSyntax clause)
            {
                var clauseInfo = model.GetQueryClauseInfo(clause, cancellationToken);
                if (clauseInfo.CastInfo.Symbol is { } cast) CheckSymbol(cast);
                if (clauseInfo.OperationInfo.Symbol is { } operation) CheckSymbol(operation);
            }

            if (node is ExpressionSyntax expression && model.GetConversion(expression, cancellationToken).MethodSymbol is { } conversion)
            {
                CheckSymbol(conversion);
            }
        }

        private void CheckExpression(ExpressionSyntax expression)
        {
            var info = model.GetTypeInfo(expression, cancellationToken);
            CheckType(info.Type, expression);
            CheckType(info.ConvertedType, expression);
        }

        private void CheckDeclaredSymbol(SyntaxNode node)
        {
            var declared = model.GetDeclaredSymbol(node, cancellationToken);
            switch (declared)
            {
                case ILocalSymbol local:
                    CheckType(local.Type, node);
                    CheckVariableHoldsQuery(local.Type, isTopLevelVariable: local.RefKind == RefKind.None, node);
                    break;
                case IParameterSymbol parameter:
                    CheckType(parameter.Type, node);
                    CheckVariableHoldsQuery(parameter.Type, isTopLevelVariable: false, node);
                    break;
                case IFieldSymbol field:
                    CheckType(field.Type, node);
                    CheckVariableHoldsQuery(field.Type, isTopLevelVariable: field.ContainingType?.IsScriptClass == true, node);
                    break;
                case IPropertySymbol property:
                    CheckType(property.Type, node);
                    CheckVariableHoldsQuery(property.Type, isTopLevelVariable: false, node);
                    break;
                case IMethodSymbol method:
                    CheckDeclaredMethod(method, node);
                    break;
                case INamedTypeSymbol type when node is TypeDeclarationSyntax:
                    CheckDeclaredType(type, node);
                    break;
            }

            // Lambdas are not declarations, but their parameters and asynchrony matter the same way.
            if (node is AnonymousFunctionExpressionSyntax && model.GetSymbolInfo(node, cancellationToken).Symbol is IMethodSymbol lambda)
            {
                if (lambda.IsAsync && lambda.ReturnsVoid)
                {
                    violations.Add("async void lambdas are not allowed; an exception in one would end the host process.");
                }
            }
        }

        private void CheckDeclaredMethod(IMethodSymbol method, SyntaxNode node)
        {
            if (method.IsAsync && method.ReturnsVoid)
            {
                violations.Add($"async void is not allowed ({method.Name}); return a Task.");
            }

            CheckType(method.ReturnType, node);
            if (method.MethodKind is not (MethodKind.AnonymousFunction or MethodKind.Constructor or MethodKind.StaticConstructor)
                && ContainsDeferredQuery(method.ReturnType, nested: true))
            {
                violations.Add($"{method.Name} returns a query; build queries in top-level variables instead of methods.");
            }
        }

        private void CheckDeclaredType(INamedTypeSymbol type, SyntaxNode node)
        {
            if (type.TypeKind == TypeKind.Class
                && type.BaseType is { } baseType
                && baseType.SpecialType != SpecialType.System_Object
                && !IsSource(baseType))
            {
                violations.Add($"{type.Name} may not derive from {baseType.ToDisplayString(Display)}; script classes derive from object or another script class.");
            }

            foreach (var implemented in type.Interfaces)
            {
                CheckType(implemented, node);
            }
        }

        private void CheckSymbol(ISymbol symbol)
        {
            switch (symbol)
            {
                case INamespaceSymbol:
                case ILabelSymbol:
                    return;
                case IAliasSymbol alias:
                    CheckSymbol(alias.Target);
                    return;
                case ITypeSymbol type:
                    CheckType(type, null);
                    return;
                case ILocalSymbol local:
                    CheckType(local.Type, null);
                    return;
                case IParameterSymbol parameter:
                    CheckType(parameter.Type, null);
                    return;
                case IDiscardSymbol discard:
                    CheckType(discard.Type, null);
                    return;
                case IRangeVariableSymbol:
                    return;
                case IMethodSymbol method:
                    CheckMember(method, method.ReducedFrom ?? method);
                    foreach (var argument in method.TypeArguments) CheckType(argument, null);
                    CheckType(method.ReturnType, null);
                    return;
                case IPropertySymbol property:
                    CheckMember(property, property);
                    CheckType(property.Type, null);
                    return;
                case IFieldSymbol field:
                    CheckMember(field, field);
                    CheckType(field.Type, null);
                    return;
                case IEventSymbol @event:
                    CheckMember(@event, @event);
                    return;
                default:
                    violations.Add($"{symbol.Kind} '{symbol.ToDisplayString(Display)}' is not allowed.");
                    return;
            }
        }

        private void CheckMember(ISymbol member, ISymbol definition)
        {
            if (IsSource(member))
            {
                return;
            }

            if (member is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
            {
                return;
            }

            var containing = definition.OriginalDefinition.ContainingType;
            if (containing is null)
            {
                violations.Add($"'{member.ToDisplayString(Display)}' is not allowed.");
                return;
            }

            if (containing.IsAnonymousType || IsSource(containing))
            {
                return;
            }

            if (policy.IsDeniedMember(containing, member.Name))
            {
                violations.Add($"'{containing.ToDisplayString(Display)}.{member.Name}' is not allowed.");
                return;
            }

            if (policy.IsMemberType(containing) || policy.IsAllowedMember(containing, member.Name))
            {
                return;
            }

            violations.Add($"'{containing.ToDisplayString(Display)}.{member.Name}' is not allowed.");
        }

        private void CheckType(ITypeSymbol? type, SyntaxNode? at)
        {
            if (type is null || IsAllowedType(type))
            {
                return;
            }

            violations.Add(type switch
            {
                IDynamicTypeSymbol => "dynamic is not allowed; use typed values.",
                IPointerTypeSymbol or IFunctionPointerTypeSymbol => "Pointers are not allowed.",
                _ => $"Type '{type.ToDisplayString(Display)}' is not allowed.",
            });
        }

        private bool IsAllowedType(ITypeSymbol type)
        {
            if (_typeVerdicts.TryGetValue(type, out var known))
            {
                return known;
            }

            // Provisional entry breaks cycles through anonymous types and type arguments.
            _typeVerdicts[type] = true;
            var verdict = Evaluate(type);
            _typeVerdicts[type] = verdict;
            return verdict;
        }

        private bool Evaluate(ITypeSymbol type)
        {
            if (type.SpecialType == SpecialType.System_Void)
            {
                return true;
            }

            switch (type)
            {
                case IErrorTypeSymbol:
                case IDynamicTypeSymbol:
                case IPointerTypeSymbol:
                case IFunctionPointerTypeSymbol:
                    return false;
                case ITypeParameterSymbol:
                    return true;
                case IArrayTypeSymbol array:
                    return IsAllowedType(array.ElementType);
                case INamedTypeSymbol named:
                    if (named.IsAnonymousType)
                    {
                        return named.GetMembers().OfType<IPropertySymbol>().All(p => IsAllowedType(p.Type));
                    }

                    if (named.IsTupleType)
                    {
                        return named.TupleElements.All(e => IsAllowedType(e.Type));
                    }

                    if (!IsSource(named)
                        && !policy.IsMemberType(named)
                        && !policy.IsOpaqueType(named))
                    {
                        return false;
                    }

                    return named.TypeArguments.All(IsAllowedType)
                           && (named.ContainingType is null || IsAllowedType(named.ContainingType));
                default:
                    return false;
            }
        }

        private bool IsSource(ISymbol symbol)
            => SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, compilation.Assembly)
               || (symbol is INamedTypeSymbol { IsScriptClass: true });

        // ---- Deferred queries ------------------------------------------------------------------

        /// <summary>
        /// A deferred query may be held directly by a top-level variable (a local or a script-level field).
        /// Parameters, members of script classes, method results and values nested inside other values
        /// cannot hold one, because each of those is a way to compose a query without a syntactic bound.
        /// </summary>
        private void CheckVariableHoldsQuery(ITypeSymbol type, bool isTopLevelVariable, SyntaxNode at)
        {
            if (ContainsDeferredQuery(type, nested: !isTopLevelVariable))
            {
                violations.Add("Queries can only be held by top-level variables (not parameters, fields, collections, tuples or delegates).");
            }
        }

        private bool ContainsDeferredQuery(ITypeSymbol? type, bool nested)
        {
            switch (type)
            {
                case IArrayTypeSymbol array:
                    return ContainsDeferredQuery(array.ElementType, nested: true);
                case INamedTypeSymbol named:
                    if (nested && policy.IsDeferredQueryType(named))
                    {
                        return true;
                    }

                    if (named.IsAnonymousType)
                    {
                        return named.GetMembers().OfType<IPropertySymbol>().Any(p => ContainsDeferredQuery(p.Type, nested: true));
                    }

                    if (named.IsTupleType)
                    {
                        return named.TupleElements.Any(e => ContainsDeferredQuery(e.Type, nested: true));
                    }

                    // The query's own element type does not hold a query; anything else's type arguments do.
                    if (!policy.IsDeferredQueryType(named) && named.TypeArguments.Any(a => ContainsDeferredQuery(a, nested: true)))
                    {
                        return true;
                    }

                    return false;
                default:
                    return false;
            }
        }

        /// <summary>A variable holding a deferred query is assigned exactly once, by its declaration.</summary>
        private void CheckDeferredQueryWrites(SyntaxNode node)
        {
            switch (node)
            {
                case AssignmentExpressionSyntax assignment:
                    foreach (var target in AssignedTargets(assignment.Left))
                    {
                        RefuseQueryWrite(target);
                    }
                    break;
                case ArgumentSyntax argument when !argument.RefKindKeyword.IsKind(SyntaxKind.None):
                    if (argument.Expression is not DeclarationExpressionSyntax)
                    {
                        RefuseQueryWrite(argument.Expression);
                    }
                    break;
                case RefExpressionSyntax reference:
                    RefuseQueryWrite(reference.Expression);
                    break;
            }
        }

        private static IEnumerable<ExpressionSyntax> AssignedTargets(ExpressionSyntax left)
        {
            if (left is TupleExpressionSyntax tuple)
            {
                foreach (var argument in tuple.Arguments)
                {
                    foreach (var nested in AssignedTargets(argument.Expression))
                    {
                        yield return nested;
                    }
                }
            }
            else if (left is not DeclarationExpressionSyntax)
            {
                yield return left;
            }
        }

        private void RefuseQueryWrite(ExpressionSyntax target)
        {
            var type = model.GetTypeInfo(target, cancellationToken).Type;
            if (type is INamedTypeSymbol named && (policy.IsDeferredQueryType(named) || ContainsDeferredQuery(named, nested: false)))
            {
                violations.Add("Query variables cannot be reassigned; build the query in one expression or declare a new variable.");
            }
        }
    }
}

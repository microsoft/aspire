// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Aspire.Hosting.Analyzers;

public partial class AppHostAnalyzer
{
    /// <summary>
    /// Reports <c>ASPIRE013</c> when code reads a connection-string expression without resolving the effective resource.
    /// </summary>
    private static void DetectDirectConnectionStringExpressionAccess(
        OperationAnalysisContext context,
        WellKnownTypes wellKnownTypes)
    {
        if (!wellKnownTypes.TryGet(
                WellKnownTypeData.WellKnownType.Aspire_Hosting_ApplicationModel_IResourceWithConnectionString,
                out var connectionStringResource) ||
            !wellKnownTypes.TryGet(
                WellKnownTypeData.WellKnownType.Aspire_Hosting_ApplicationModel_ResourceExtensions,
                out var resourceExtensions))
        {
            return;
        }

        var connectionStringExpression = connectionStringResource
            .GetMembers("ConnectionStringExpression")
            .OfType<IPropertySymbol>()
            .SingleOrDefault();

        if (connectionStringExpression is null)
        {
            return;
        }

        var propertyReference = (IPropertyReferenceOperation)context.Operation;
        if (!ImplementsInterfaceProperty(propertyReference.Property, connectionStringExpression) ||
            IsExplicitlyResolved(propertyReference.Instance, resourceExtensions))
        {
            return;
        }

        var location = propertyReference.Syntax switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.GetLocation(),
            MemberBindingExpressionSyntax memberBinding => memberBinding.Name.GetLocation(),
            _ => propertyReference.Syntax.GetLocation()
        };

        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.s_connectionStringExpressionMustBeResolved,
            location));
    }

    private static bool ImplementsInterfaceProperty(
        IPropertySymbol property,
        IPropertySymbol interfaceProperty)
    {
        if (SymbolEqualityComparer.Default.Equals(property.OriginalDefinition, interfaceProperty.OriginalDefinition) ||
            property.ExplicitInterfaceImplementations.Any(
                implementation => SymbolEqualityComparer.Default.Equals(
                    implementation.OriginalDefinition,
                    interfaceProperty.OriginalDefinition)))
        {
            return true;
        }

        var implementation = property.ContainingType.FindImplementationForInterfaceMember(interfaceProperty);
        return implementation is IPropertySymbol implementationProperty &&
            SymbolEqualityComparer.Default.Equals(
                implementationProperty.OriginalDefinition,
                property.OriginalDefinition);
    }

    private static bool IsExplicitlyResolved(
        IOperation? instance,
        INamedTypeSymbol resourceExtensions)
    {
        while (instance is IConversionOperation conversion)
        {
            instance = conversion.Operand;
        }

        if (instance is IInvocationOperation invocation)
        {
            return IsResolverInvocation(invocation, resourceExtensions);
        }

        return instance is ILocalReferenceOperation localReference &&
            IsLocalInitializedByResolver(
                localReference,
                resourceExtensions);
    }

    private static bool IsResolverInvocation(
        IInvocationOperation invocation,
        INamedTypeSymbol resourceExtensions)
    {
        var targetMethod = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
        if (!SymbolEqualityComparer.Default.Equals(targetMethod.ContainingType, resourceExtensions))
        {
            return false;
        }

        return targetMethod.Name is "GetEffectiveCapability" or "GetOwnerOrSelf";
    }

    private static bool IsLocalInitializedByResolver(
        ILocalReferenceOperation localReference,
        INamedTypeSymbol resourceExtensions)
    {
        var operationBlock = GetOperationBlock(localReference);
        var declarator = FindVariableDeclarator(operationBlock, localReference.Local);
        if (declarator?.Initializer?.Value is not { } initializer)
        {
            return false;
        }

        if (!IsExplicitlyResolved(initializer, resourceExtensions))
        {
            return false;
        }

        // Only trust the resolved capability while the local still has its initializer value. A later assignment
        // could replace it with the owner and bypass projection-first resolution.
        return !ContainsWrite(operationBlock, localReference.Local);
    }

    private static IOperation GetOperationBlock(IOperation operation)
    {
        while (operation.Parent is { } parent)
        {
            operation = parent;
        }

        return operation;
    }

    private static IVariableDeclaratorOperation? FindVariableDeclarator(IOperation operation, ILocalSymbol local)
    {
        if (operation is IVariableDeclaratorOperation declarator &&
            SymbolEqualityComparer.Default.Equals(declarator.Symbol, local))
        {
            return declarator;
        }

        foreach (var child in operation.ChildOperations)
        {
            if (FindVariableDeclarator(child, local) is { } childDeclarator)
            {
                return childDeclarator;
            }
        }

        return null;
    }

    private static bool ContainsWrite(IOperation operation, ILocalSymbol local)
    {
        if (operation switch
            {
                ISimpleAssignmentOperation assignment => ContainsLocalReference(assignment.Target, local),
                ICompoundAssignmentOperation assignment => ContainsLocalReference(assignment.Target, local),
                IDeconstructionAssignmentOperation assignment => ContainsLocalReference(assignment.Target, local),
                IIncrementOrDecrementOperation increment => ContainsLocalReference(increment.Target, local),
                IArgumentOperation argument
                    when argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out =>
                    ContainsLocalReference(argument.Value, local),
                _ => false
            })
        {
            return true;
        }

        foreach (var child in operation.ChildOperations)
        {
            if (ContainsWrite(child, local))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsLocalReference(IOperation operation, ILocalSymbol local)
    {
        if (operation is ILocalReferenceOperation localReference &&
            SymbolEqualityComparer.Default.Equals(localReference.Local, local))
        {
            return true;
        }

        foreach (var child in operation.ChildOperations)
        {
            if (ContainsLocalReference(child, local))
            {
                return true;
            }
        }

        return false;
    }
}

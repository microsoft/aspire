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

        if (instance is not IInvocationOperation invocation)
        {
            return false;
        }

        var targetMethod = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
        if (!SymbolEqualityComparer.Default.Equals(targetMethod.ContainingType, resourceExtensions))
        {
            return false;
        }

        return targetMethod.Name is "GetEffectiveCapability" or "GetOwnerOrSelf";
    }
}

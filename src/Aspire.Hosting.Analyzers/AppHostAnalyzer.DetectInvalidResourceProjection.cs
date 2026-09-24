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
    /// Reports <c>ASPIRE012</c> for projection APIs with incompatible source and target shapes.
    /// </summary>
    /// <remarks>
    /// The projection APIs remain open to integration-defined resource types. This analyzer mirrors the hosting
    /// library's central validity checks only for combinations that are invalid regardless of integration behavior.
    /// </remarks>
    private static void DetectInvalidResourceProjection(
        OperationAnalysisContext context,
        WellKnownTypes wellKnownTypes)
    {
        if (!wellKnownTypes.TryGet(
                WellKnownTypeData.WellKnownType.Aspire_Hosting_ResourceProjectionBuilderExtensions,
                out var projectionExtensions) ||
            !wellKnownTypes.TryGet(
                WellKnownTypeData.WellKnownType.Aspire_Hosting_ApplicationModel_ContainerResource,
                out var containerResource) ||
            !wellKnownTypes.TryGet(
                WellKnownTypeData.WellKnownType.Aspire_Hosting_ApplicationModel_ExecutableResource,
                out var executableResource) ||
            !wellKnownTypes.TryGet(
                WellKnownTypeData.WellKnownType.Aspire_Hosting_ApplicationModel_ProjectResource,
                out var projectResource) ||
            !wellKnownTypes.TryGet(
                WellKnownTypeData.WellKnownType.Aspire_Hosting_ApplicationModel_IResourceWithoutProjections,
                out var resourceWithoutProjections))
        {
            return;
        }

        var invocation = (IInvocationOperation)context.Operation;
        var targetMethod = invocation.TargetMethod;

        if (!SymbolEqualityComparer.Default.Equals(targetMethod.ContainingType, projectionExtensions) ||
            targetMethod.Name is not (
                "RunAsContainerImage" or
                "WithContainerProjection" or
                "WithResourceProjection") ||
            targetMethod.TypeArguments.Length == 0)
        {
            return;
        }

        var ownerType = targetMethod.TypeArguments[0];
        var projectionType = GetProjectionType(targetMethod, containerResource);
        if (projectionType is null ||
            !TryGetInvalidProjectionReason(
                ownerType,
                projectionType,
                containerResource,
                executableResource,
                projectResource,
                resourceWithoutProjections,
                out var reason))
        {
            return;
        }

        // Prefer the method name over the whole invocation so the squiggle identifies the invalid projection API.
        var location = invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess }
            ? memberAccess.Name.GetLocation()
            : invocation.Syntax.GetLocation();

        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.s_invalidResourceProjection,
            location,
            targetMethod.Name,
            ownerType.Name,
            projectionType.Name,
            reason));
    }

    private static ITypeSymbol? GetProjectionType(
        IMethodSymbol targetMethod,
        INamedTypeSymbol containerResource)
    {
        if (targetMethod.Name is "RunAsContainerImage" or "WithContainerProjection")
        {
            return targetMethod.TypeArguments.Length > 1
                ? targetMethod.TypeArguments[1]
                : containerResource;
        }

        return targetMethod.TypeArguments.Length > 1
            ? targetMethod.TypeArguments[1]
            : null;
    }

    private static bool TryGetInvalidProjectionReason(
        ITypeSymbol ownerType,
        ITypeSymbol projectionType,
        INamedTypeSymbol containerResource,
        INamedTypeSymbol executableResource,
        INamedTypeSymbol projectResource,
        INamedTypeSymbol resourceWithoutProjections,
        out string reason)
    {
        if (ImplementsOrEquals(ownerType, resourceWithoutProjections))
        {
            reason = "the owner has a fixed model shape and cannot be projected";
            return true;
        }

        if (ImplementsOrEquals(projectionType, resourceWithoutProjections))
        {
            reason = "the target has a fixed model shape and cannot be used as a projection";
            return true;
        }

        if (InheritsFromOrEquals(ownerType, containerResource) &&
            InheritsFromOrEquals(projectionType, containerResource))
        {
            reason = "a container cannot be projected to another container";
            return true;
        }

        if (IsExecutableShape(ownerType, executableResource, projectResource) &&
            IsExecutableShape(projectionType, executableResource, projectResource))
        {
            reason = "an executable or project cannot be projected to another executable or project";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool IsExecutableShape(
        ITypeSymbol type,
        INamedTypeSymbol executableResource,
        INamedTypeSymbol projectResource) =>
        InheritsFromOrEquals(type, executableResource) ||
        InheritsFromOrEquals(type, projectResource);

    private static bool ImplementsOrEquals(ITypeSymbol type, INamedTypeSymbol interfaceType)
    {
        if (type is ITypeParameterSymbol typeParameter &&
            typeParameter.ConstraintTypes.Any(constraint => ImplementsOrEquals(constraint, interfaceType)))
        {
            return true;
        }

        return SymbolEqualityComparer.Default.Equals(type, interfaceType) ||
            type.AllInterfaces.Any(
                @interface => SymbolEqualityComparer.Default.Equals(@interface, interfaceType));
    }

    private static bool InheritsFromOrEquals(ITypeSymbol type, INamedTypeSymbol baseType)
    {
        if (type is ITypeParameterSymbol typeParameter &&
            typeParameter.ConstraintTypes.Any(constraint => InheritsFromOrEquals(constraint, baseType)))
        {
            return true;
        }

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
    }
}

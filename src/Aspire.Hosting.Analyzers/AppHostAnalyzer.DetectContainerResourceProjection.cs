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
    /// Reports <c>ASPIRE012</c> when a projection API is called on a resource that is already a container.
    /// </summary>
    /// <remarks>
    /// The projection APIs are constrained <c>where T : IResource</c> because C# has no way to express
    /// "T is not a ContainerResource". A container projected onto a container shares the owner's annotation
    /// collection, so the projected image and endpoints collide with the ones the container already carries.
    /// The hosting library throws for this, but the mistake is fully determined by the type argument, so it can
    /// be reported at build time instead of at AppHost startup.
    /// </remarks>
    private static void DetectContainerResourceProjection(OperationAnalysisContext context, WellKnownTypes wellKnownTypes)
    {
        if (!wellKnownTypes.TryGet(WellKnownTypeData.WellKnownType.Aspire_Hosting_ResourceProjectionBuilderExtensions, out var projectionExtensions) ||
            !wellKnownTypes.TryGet(WellKnownTypeData.WellKnownType.Aspire_Hosting_ApplicationModel_ContainerResource, out var containerResource))
        {
            return;
        }

        var invocation = (IInvocationOperation)context.Operation;
        var targetMethod = invocation.TargetMethod;

        if (!SymbolEqualityComparer.Default.Equals(targetMethod.ContainingType, projectionExtensions))
        {
            return;
        }

        // Every projection method names the owner first, so the owner type is always the first type argument.
        // This matches on the containing type rather than a list of method names so methods added later are
        // covered without touching the analyzer.
        if (targetMethod.TypeArguments.Length == 0 ||
            !InheritsFromOrEquals(targetMethod.TypeArguments[0], containerResource))
        {
            return;
        }

        // Prefer the method name over the whole invocation so the squiggle lands on `RunAsContainerImage`
        // rather than spanning the entire builder chain.
        var location = invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess }
            ? memberAccess.Name.GetLocation()
            : invocation.Syntax.GetLocation();

        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.s_containerResourceCannotBeProjected,
            location,
            targetMethod.TypeArguments[0].Name,
            targetMethod.Name));
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

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Aspire.Hosting.Analyzers;

public partial class AppHostAnalyzer
{
    internal static class Diagnostics
    {
        private const string ModelNameMustBeValidId = "ASPIRE006";
        internal static readonly DiagnosticDescriptor s_modelNameMustBeValid = new(
            id: ModelNameMustBeValidId,
            title: "Application model items must have valid names",
            messageFormat: "{0}",
            category: "Design",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: $"https://aka.ms/aspire/diagnostics/{ModelNameMustBeValidId}");

        private const string ContainerResourceCannotBeProjectedId = "ASPIRE012";
        internal static readonly DiagnosticDescriptor s_containerResourceCannotBeProjected = new(
            id: ContainerResourceCannotBeProjectedId,
            title: "Container resources cannot be projected as containers",
            messageFormat: "'{0}' is already a container resource, so '{1}' cannot be used on it. Configure the container directly instead.",
            category: "Usage",
            // Error rather than Warning: the hosting library throws unconditionally for this, so the code cannot
            // work at runtime. There is no legitimate case where suppressing it produces a working AppHost.
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: $"https://aka.ms/aspire/diagnostics/{ContainerResourceCannotBeProjectedId}");

        public static readonly ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics = ImmutableArray.Create(
            s_modelNameMustBeValid,
            s_containerResourceCannotBeProjected
        );
    }
}

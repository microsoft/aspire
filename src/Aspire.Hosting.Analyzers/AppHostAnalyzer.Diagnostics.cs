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

        private const string InvalidResourceProjectionId = "ASPIRE012";
        internal static readonly DiagnosticDescriptor s_invalidResourceProjection = new(
            id: InvalidResourceProjectionId,
            title: "Resource cannot be projected to the requested shape",
            messageFormat: "Projection API '{0}' cannot project resource type '{1}' to '{2}': {3}",
            category: "Usage",
            // Error rather than Warning: the hosting library rejects these source and target combinations
            // unconditionally, so suppressing the diagnostic cannot produce a working AppHost.
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            helpLinkUri: $"https://aka.ms/aspire/diagnostics/{InvalidResourceProjectionId}");

        private const string ConnectionStringAccessMustBeResolvedId = "ASPIRE013";
        internal static readonly DiagnosticDescriptor s_connectionStringAccessMustBeResolved = new(
            id: ConnectionStringAccessMustBeResolvedId,
            title: "Resolve connection-string access through the effective resource",
            messageFormat: "Direct use of '{0}' may ignore a selected resource projection; {1}",
            category: "Usage",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            helpLinkUri: $"https://aka.ms/aspire/diagnostics/{ConnectionStringAccessMustBeResolvedId}");

        public static readonly ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics = ImmutableArray.Create(
            s_modelNameMustBeValid,
            s_invalidResourceProjection,
            s_connectionStringAccessMustBeResolved
        );
    }
}

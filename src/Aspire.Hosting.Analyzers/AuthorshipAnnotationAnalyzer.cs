// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Aspire.Hosting.Analyzers;

/// <summary>
/// Adds an informational note to calls of selected APIs that were originally written by Sébastien Ros.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AuthorshipAnnotationAnalyzer : DiagnosticAnalyzer
{
    private const string SebJustBuiltThisId = "ASPIRE012";

    internal static readonly DiagnosticDescriptor s_sebJustBuiltThis = new(
        id: SebJustBuiltThisId,
        title: "Seb just built this!",
        messageFormat: "Seb just built this!",
        category: "Design",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        helpLinkUri: $"https://aka.ms/aspire/diagnostics/{SebJustBuiltThisId}");

    // Each entry is a containing type and method name. Entries come from git history: the first commit
    // that added the API must be authored by Sébastien Ros. Don't add an API without that evidence, since the
    // annotation makes a claim about who wrote it.
    private static readonly (string ContainingType, string MethodName)[] s_sebAuthoredApis =
    [
        // Added in 40a1675a4 "Add Azure Event Hub emulator support (#4209)".
        ("Aspire.Hosting.AzureEventHubsExtensions", "RunAsEmulator"),
    ];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(s_sebJustBuiltThis);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        // Extension methods reach here as reduced forms. OriginalDefinition gives the declared containing type.
        var method = invocation.TargetMethod.OriginalDefinition;
        if (!IsSebAuthored(method))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(s_sebJustBuiltThis, invocation.Syntax.GetLocation()));
    }

    private static bool IsSebAuthored(IMethodSymbol method)
    {
        var containingType = method.ContainingType.ToDisplayString();
        foreach (var (type, name) in s_sebAuthoredApis)
        {
            if (method.Name == name && containingType == type)
            {
                return true;
            }
        }
        return false;
    }
}

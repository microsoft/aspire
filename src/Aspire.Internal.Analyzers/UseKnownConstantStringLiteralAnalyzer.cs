// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Aspire.Internal.Analyzers;

/// <summary>
/// Flags string literals whose value is also defined as a <c>public const string</c>
/// member of an <c>internal static class Known*</c>
/// (e.g. <c>KnownConfigNames.ResourceServiceEndpointUrl = "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"</c>).
/// Suggests replacing the literal with the named constant.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseKnownConstantStringLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "ASPIREINT001";

    internal static readonly DiagnosticDescriptor s_rule = new(
        id: DiagnosticId,
        title: "Use the existing named constant instead of a string literal",
        messageFormat: "String literal \"{0}\" is also defined as {1}. Use the named constant instead.",
        category: "Maintainability",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Replace duplicated string literals with the existing named constant from a Known* class to keep the repo consistent.",
        helpLinkUri: $"https://aka.ms/aspire/diagnostics/{DiagnosticId}");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(s_rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var catalog = KnownConstantCatalog.GetOrCreate(context.Compilation);
        var assemblyName = context.Compilation.AssemblyName ?? string.Empty;
        var compilation = context.Compilation;

        context.RegisterOperationAction(
            ctx => AnalyzeLiteral(ctx, catalog, assemblyName, compilation),
            OperationKind.Literal);
    }

    private static void AnalyzeLiteral(OperationAnalysisContext context, KnownConstantCatalog catalog, string assemblyName, Compilation compilation)
    {
        var operation = (ILiteralOperation)context.Operation;

        if (operation.Type?.SpecialType != SpecialType.System_String)
        {
            return;
        }

        if (operation.ConstantValue.Value is not string value)
        {
            return;
        }

        // Don't flag the constant's own initializer (e.g. inside Known*.cs itself).
        if (IsInsideConstFieldInitializer(operation))
        {
            return;
        }

        if (!catalog.TryGetMatches(value, out var matches))
        {
            return;
        }

        // Drop matches whose target field (or its containing types) is not accessible
        // from the consumer's compilation. Suggesting an inaccessible constant would
        // produce a fix the user can't apply (e.g. internal class in a different
        // assembly without InternalsVisibleTo).
        matches = FilterAccessible(matches, compilation);
        if (matches.IsEmpty)
        {
            return;
        }

        // Don't flag literals that occur inside the Known* class itself
        // (e.g. helper methods within KnownConfigNames that happen to repeat
        // a constant value). Replacing them with the named constant would be
        // self-referential and just adds noise.
        var containingType = context.ContainingSymbol?.ContainingType;
        if (containingType is not null && AllMatchesInside(matches, containingType))
        {
            return;
        }

        var consumerNamespace = GetNamespaceFullName(containingType?.ContainingNamespace);
        var resolved = ResolveScopedMatches(matches, assemblyName, consumerNamespace);
        if (resolved.IsDefaultOrEmpty)
        {
            return;
        }

        // Suppress cross-domain ambiguity: if the surviving matches don't all share
        // the same outermost (root) Known* class, the literal could legitimately mean
        // any of several unrelated things and we shouldn't assert a fix at Error
        // severity. Same-root multi-match (e.g. modern + nested Legacy class) is fine.
        if (!ShareSingleRoot(resolved))
        {
            return;
        }

        var displayName = FormatMatches(resolved);
        context.ReportDiagnostic(Diagnostic.Create(s_rule, operation.Syntax.GetLocation(), value, displayName));
    }

    private static ImmutableArray<KnownConstant> FilterAccessible(ImmutableArray<KnownConstant> matches, Compilation compilation)
    {
        ImmutableArray<KnownConstant>.Builder? builder = null;
        var withinAssembly = compilation.Assembly;
        for (var i = 0; i < matches.Length; i++)
        {
            var field = matches[i].Field;
            if (compilation.IsSymbolAccessibleWithin(field, withinAssembly)
                && AllContainingTypesAccessible(field.ContainingType, compilation, withinAssembly))
            {
                builder ??= ImmutableArray.CreateBuilder<KnownConstant>(matches.Length);
                builder.Add(matches[i]);
            }
        }
        return builder?.ToImmutable() ?? ImmutableArray<KnownConstant>.Empty;
    }

    private static bool AllContainingTypesAccessible(INamedTypeSymbol? type, Compilation compilation, IAssemblySymbol withinAssembly)
    {
        for (var t = type; t is not null; t = t.ContainingType)
        {
            if (!compilation.IsSymbolAccessibleWithin(t, withinAssembly))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Most-specific-scope-wins resolution:
    /// <list type="number">
    /// <item><description>If any match has a non-empty scope that applies to the consumer,
    /// keep only those scoped matches.</description></item>
    /// <item><description>Otherwise, keep only the unscoped (no-attribute / empty-filter) matches.</description></item>
    /// <item><description>Drop scoped matches that don't apply to the consumer.</description></item>
    /// </list>
    /// </summary>
    private static ImmutableArray<KnownConstant> ResolveScopedMatches(
        ImmutableArray<KnownConstant> matches,
        string assemblyName,
        string consumerNamespace)
    {
        ImmutableArray<KnownConstant>.Builder? scoped = null;
        ImmutableArray<KnownConstant>.Builder? unscoped = null;

        foreach (var match in matches)
        {
            if (match.Scope.IsUnscoped)
            {
                unscoped ??= ImmutableArray.CreateBuilder<KnownConstant>();
                unscoped.Add(match);
            }
            else if (match.Scope.AppliesTo(assemblyName, consumerNamespace))
            {
                scoped ??= ImmutableArray.CreateBuilder<KnownConstant>();
                scoped.Add(match);
            }
            // else: scoped match that doesn't apply here — drop it.
        }

        if (scoped is not null)
        {
            return scoped.ToImmutable();
        }
        if (unscoped is not null)
        {
            return unscoped.ToImmutable();
        }
        return ImmutableArray<KnownConstant>.Empty;
    }

    private static bool ShareSingleRoot(ImmutableArray<KnownConstant> matches)
    {
        if (matches.Length <= 1)
        {
            return true;
        }

        var firstRoot = matches[0].RootContainingType;
        for (var i = 1; i < matches.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(firstRoot, matches[i].RootContainingType))
            {
                return false;
            }
        }
        return true;
    }

    private static string GetNamespaceFullName(INamespaceSymbol? ns)
    {
        if (ns is null || ns.IsGlobalNamespace)
        {
            return string.Empty;
        }

        var parts = new Stack<string>();
        for (var current = ns; current is not null && !current.IsGlobalNamespace; current = current.ContainingNamespace)
        {
            parts.Push(current.Name);
        }
        return string.Join(".", parts);
    }

    private static bool IsInsideConstFieldInitializer(IOperation operation)
    {
        // Walk up the operation tree looking for a field initializer whose field is const.
        for (var current = operation; current is not null; current = current.Parent)
        {
            if (current is IFieldInitializerOperation fieldInit)
            {
                foreach (var field in fieldInit.InitializedFields)
                {
                    if (field.IsConst)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private static bool AllMatchesInside(ImmutableArray<KnownConstant> matches, INamedTypeSymbol containingType)
    {
        foreach (var match in matches)
        {
            // Walk up the containing-type chain so nested helpers (e.g. inside
            // Legacy) are still considered "inside" the outer Known* class.
            var matchType = match.Field.ContainingType;
            var inside = false;
            for (var t = containingType; t is not null; t = t.ContainingType)
            {
                if (SymbolEqualityComparer.Default.Equals(matchType, t))
                {
                    inside = true;
                    break;
                }
            }
            if (!inside)
            {
                return false;
            }
        }
        return true;
    }

    private static string FormatMatches(ImmutableArray<KnownConstant> matches)
    {
        if (matches.Length == 1)
        {
            return matches[0].GetDisplayName();
        }

        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < matches.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(i == matches.Length - 1 ? " or " : ", ");
            }
            sb.Append(matches[i].GetDisplayName());
        }
        return sb.ToString();
    }
}

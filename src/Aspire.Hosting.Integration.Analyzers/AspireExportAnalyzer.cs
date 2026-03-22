// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Aspire.Hosting.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Aspire.Hosting.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public partial class AspireExportAnalyzer : DiagnosticAnalyzer
{
    private const string RunSyncOnBackgroundThreadPropertyName = "RunSyncOnBackgroundThread";

    // Matches: valid method name (camelCase identifier, may contain dots for namespacing)
    // Examples: addRedis, addContainer, Dictionary.set
    private static readonly Regex s_exportIdPattern = new(
        @"^[a-zA-Z][a-zA-Z0-9.]*$",
        RegexOptions.Compiled);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => Diagnostics.SupportedDiagnostics;

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(AnalyzeCompilationStart);
    }

    private void AnalyzeCompilationStart(CompilationStartAnalysisContext context)
    {
        var wellKnownTypes = WellKnownTypes.GetOrCreate(context.Compilation);

        // Try to get the AspireExportAttribute type - if it doesn't exist, nothing to analyze
        INamedTypeSymbol? aspireExportAttribute;
        try
        {
            aspireExportAttribute = wellKnownTypes.Get(WellKnownTypeData.WellKnownType.Aspire_Hosting_AspireExportAttribute);
        }
        catch (InvalidOperationException)
        {
            // Type not found in compilation, nothing to analyze
            return;
        }

        // Try to get AspireExportIgnoreAttribute for ASPIREEXPORT008
        INamedTypeSymbol? aspireExportIgnoreAttribute = null;
        try
        {
            aspireExportIgnoreAttribute = wellKnownTypes.Get(WellKnownTypeData.WellKnownType.Aspire_Hosting_AspireExportIgnoreAttribute);
        }
        catch (InvalidOperationException)
        {
            // Type not found, missing attribute check won't run
        }

        // Try to get AspireUnionAttribute for ASPIREEXPORT005/006 validation
        INamedTypeSymbol? aspireUnionAttribute = null;
        try
        {
            aspireUnionAttribute = wellKnownTypes.Get(WellKnownTypeData.WellKnownType.Aspire_Hosting_AspireUnionAttribute);
        }
        catch (InvalidOperationException)
        {
            // Type not found, union validation won't run
        }

        // Collection for ASPIREEXPORT007: track export IDs to detect duplicates
        // Key: (exportId, targetTypeFullName), Value: list of (method, location)
        var exportsByKey = new ConcurrentDictionary<(string ExportId, string TargetType), ConcurrentBag<(IMethodSymbol Method, Location Location)>>();

        context.RegisterSymbolAction(
            c => AnalyzeMethod(c, wellKnownTypes, aspireExportAttribute, aspireExportIgnoreAttribute, aspireUnionAttribute, exportsByKey),
            SymbolKind.Method);

        // At the end of compilation, report duplicate export IDs
        context.RegisterCompilationEndAction(c => ReportDuplicateExports(c, exportsByKey));

        // Warn when exported builder methods invoke synchronous callback delegates inline. Deferred callbacks
        // that are stored for later execution are fine, and exports that opt into background-thread dispatch
        // are handled safely by the runtime.
        context.RegisterOperationBlockStartAction(c =>
        {
            if (c.OwningSymbol is not IMethodSymbol method ||
                !method.IsExtensionMethod ||
                method.Parameters.Length == 0 ||
                !IsBuilderType(method.Parameters[0].Type, wellKnownTypes) ||
                !TryGetEffectiveAspireExportAttribute(method, aspireExportAttribute, out var exportAttribute, out var containingTypeExportAttribute) ||
                IsRunSyncOnBackgroundThreadEnabled(exportAttribute) ||
                IsRunSyncOnBackgroundThreadEnabled(containingTypeExportAttribute))
            {
                return;
            }

            var synchronousDelegateParameters = method.Parameters
                .Skip(1)
                .Where(IsSynchronousDelegateParameter)
                .ToDictionary(p => p.Name, p => p, StringComparer.Ordinal);

            if (synchronousDelegateParameters.Count == 0)
            {
                return;
            }

            var reportedParameters = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            c.RegisterOperationAction(
                oc => AnalyzeInlineSynchronousDelegateInvocation(oc, method, synchronousDelegateParameters, reportedParameters),
                OperationKind.Invocation);
        });
    }

    private static void AnalyzeMethod(
        SymbolAnalysisContext context,
        WellKnownTypes wellKnownTypes,
        INamedTypeSymbol aspireExportAttribute,
        INamedTypeSymbol? aspireExportIgnoreAttribute,
        INamedTypeSymbol? aspireUnionAttribute,
        ConcurrentDictionary<(string ExportId, string TargetType), ConcurrentBag<(IMethodSymbol Method, Location Location)>> exportsByKey)
    {
        var method = (IMethodSymbol)context.Symbol;

        // Find AspireExportAttribute on the method
        AttributeData? exportAttribute = null;
        var hasExportIgnore = false;
        var isObsolete = false;
        foreach (var attr in method.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, aspireExportAttribute))
            {
                exportAttribute = attr;
            }
            else if (aspireExportIgnoreAttribute is not null &&
                     SymbolEqualityComparer.Default.Equals(attr.AttributeClass, aspireExportIgnoreAttribute))
            {
                hasExportIgnore = true;
            }
            else if (attr.AttributeClass?.Name == "ObsoleteAttribute")
            {
                isObsolete = true;
            }
        }

        // ASPIREEXPORT008: Check for missing export attributes on builder extension methods
        if (exportAttribute is null && !hasExportIgnore && !isObsolete)
        {
            AnalyzeMissingExportAttribute(context, method, wellKnownTypes, aspireExportAttribute);
        }

        if (exportAttribute is null)
        {
            return;
        }

        var attributeSyntax = exportAttribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken);
        var location = attributeSyntax?.GetLocation() ?? method.Locations.FirstOrDefault() ?? Location.None;

        // Rule 1: Method must be static
        if (!method.IsStatic)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.s_exportMethodMustBeStatic,
                location,
                method.Name));
        }

        // Rule 2: Validate export ID format
        var exportId = GetExportId(exportAttribute);
        if (exportId is not null && !s_exportIdPattern.IsMatch(exportId))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.s_invalidExportIdFormat,
                location,
                exportId));
        }

        // Rule 3: Validate return type is ATS-compatible
        if (!IsAtsCompatibleType(method.ReturnType, wellKnownTypes, aspireExportAttribute))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.s_returnTypeMustBeAtsCompatible,
                location,
                method.Name,
                method.ReturnType.ToDisplayString()));
        }

        // Rule 4: Validate parameter types are ATS-compatible
        foreach (var parameter in method.Parameters)
        {
            if (!IsAtsCompatibleParameter(parameter, wellKnownTypes, aspireExportAttribute))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.s_parameterTypeMustBeAtsCompatible,
                    location,
                    parameter.Name,
                    parameter.Type.ToDisplayString(),
                    method.Name));
            }

            // Rule 5 (ASPIREEXPORT005/006): Validate [AspireUnion] on parameters
            if (aspireUnionAttribute is not null)
            {
                AnalyzeUnionAttribute(context, parameter.GetAttributes(), aspireUnionAttribute, wellKnownTypes, aspireExportAttribute);
            }
        }

        // Rule 6 (ASPIREEXPORT007): Track export for duplicate detection
        if (exportId is not null && method.IsExtensionMethod && method.Parameters.Length > 0)
        {
            var targetType = method.Parameters[0].Type;
            var targetTypeName = targetType.ToDisplayString();
            var key = (exportId, targetTypeName);
            var bag = exportsByKey.GetOrAdd(key, _ => new ConcurrentBag<(IMethodSymbol, Location)>());
            bag.Add((method, location));
        }

        // Rule 7 (ASPIREEXPORT009): Warn when export name may collide across integrations
        if (exportId is not null && method.IsExtensionMethod && method.Parameters.Length > 0)
        {
            AnalyzeExportNameUniqueness(context, method, exportId, wellKnownTypes, location);
        }
    }

    private static void AnalyzeMissingExportAttribute(
        SymbolAnalysisContext context,
        IMethodSymbol method,
        WellKnownTypes wellKnownTypes,
        INamedTypeSymbol aspireExportAttribute)
    {
        // Only check public static extension methods
        if (!method.IsStatic || !method.IsExtensionMethod || method.DeclaredAccessibility != Accessibility.Public)
        {
            return;
        }

        if (method.Parameters.Length == 0)
        {
            return;
        }

        // Only check methods extending exported handle types that participate in ATS.
        var firstParamType = method.Parameters[0].Type;
        if (!RequiresExplicitExportCoverage(firstParamType, wellKnownTypes, aspireExportAttribute))
        {
            return;
        }

        // Determine the incompatibility reason (if any) to include in the warning
        var reason = GetIncompatibilityReason(method, wellKnownTypes, aspireExportAttribute);
        var location = method.Locations.FirstOrDefault() ?? Location.None;

        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.s_missingExportAttribute,
            location,
            method.Name,
            reason ?? "Add [AspireExport] if ATS-compatible, or [AspireExportIgnore] with a reason."));
    }

    private static void AnalyzeExportNameUniqueness(
        SymbolAnalysisContext context,
        IMethodSymbol method,
        string exportId,
        WellKnownTypes wellKnownTypes,
        Location location)
    {
        // Only applies to extension methods where the first param is IResourceBuilder<T>
        // with T being an open generic type parameter (constrained to IResource)
        var firstParamType = method.Parameters[0].Type;
        if (!IsOpenGenericResourceBuilder(firstParamType, wellKnownTypes))
        {
            return;
        }

        // Look for a parameter (beyond the first) that is IResourceBuilder<ConcreteType>
        // where ConcreteType is a specific resource type (not a type parameter)
        string? concreteTargetTypeName = null;
        for (var i = 1; i < method.Parameters.Length; i++)
        {
            concreteTargetTypeName = GetConcreteResourceBuilderTypeName(method.Parameters[i].Type, wellKnownTypes);
            if (concreteTargetTypeName is not null)
            {
                break;
            }
        }

        if (concreteTargetTypeName is null)
        {
            return;
        }

        // Check if the export ID matches the method name (camelCase), suggesting it wasn't made unique
        var expectedDefault = char.ToLowerInvariant(method.Name[0]) + method.Name.Substring(1);
        if (!string.Equals(exportId, expectedDefault, StringComparison.Ordinal))
        {
            // Export name was explicitly customized, assume the author made it unique
            return;
        }

        // Strip the "Resource" suffix from the concrete type name to build a suggested unique name
        var shortName = concreteTargetTypeName;
        if (shortName.EndsWith("Resource", StringComparison.Ordinal))
        {
            shortName = shortName.Substring(0, shortName.Length - "Resource".Length);
        }

        // Remove common prefixes like "Azure" to keep the suggestion concise
        if (shortName.StartsWith("Azure", StringComparison.Ordinal))
        {
            shortName = shortName.Substring("Azure".Length);
        }

        var suggestedName = $"with{shortName}{method.Name.Substring(4)}"; // e.g., "withSearchRoleAssignments"
        if (method.Name.Length <= 4)
        {
            suggestedName = $"{exportId}{shortName}";
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.s_exportNameShouldBeUnique,
            location,
            exportId,
            method.Name,
            concreteTargetTypeName,
            suggestedName));
    }

    private static void AnalyzeInlineSynchronousDelegateInvocation(
        OperationAnalysisContext context,
        IMethodSymbol method,
        IReadOnlyDictionary<string, IParameterSymbol> synchronousDelegateParameters,
        ConcurrentDictionary<string, byte> reportedParameters)
    {
        var invocation = (IInvocationOperation)context.Operation;

        if (invocation.TargetMethod.MethodKind != MethodKind.DelegateInvoke ||
            invocation.Syntax is not InvocationExpressionSyntax invocationSyntax ||
            IsInsideNestedCallback(invocationSyntax))
        {
            return;
        }

        var parameterName = GetInvokedDelegateParameterName(invocationSyntax);
        if (parameterName is null || !synchronousDelegateParameters.TryGetValue(parameterName, out var parameter))
        {
            return;
        }

        if (!reportedParameters.TryAdd(parameterName, default))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.s_exportedSyncDelegateInvokedInline,
            invocationSyntax.GetLocation(),
            method.Name,
            parameter.Name));
    }

    private static bool IsInsideNestedCallback(InvocationExpressionSyntax invocation)
    {
        foreach (var ancestor in invocation.Ancestors())
        {
            switch (ancestor)
            {
                case AnonymousFunctionExpressionSyntax anonymousFunction:
                    return !IsImmediatelyInvokedAnonymousFunction(anonymousFunction);
                case LocalFunctionStatementSyntax localFunction:
                    return !IsImmediatelyInvokedLocalFunction(localFunction);
            }
        }

        return false;
    }

    private static bool IsImmediatelyInvokedAnonymousFunction(AnonymousFunctionExpressionSyntax anonymousFunction)
    {
        SyntaxNode current = anonymousFunction;

        while (current.Parent is ParenthesizedExpressionSyntax or CastExpressionSyntax)
        {
            current = current.Parent;
        }

        return current.Parent is InvocationExpressionSyntax invocation &&
            invocation.Expression == current;
    }

    private static bool IsImmediatelyInvokedLocalFunction(LocalFunctionStatementSyntax localFunction)
    {
        if (localFunction.Parent is null)
        {
            return false;
        }

        var localFunctionName = localFunction.Identifier.ValueText;

        foreach (var invocation in localFunction.Parent.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (localFunction.Span.Contains(invocation.Span))
            {
                continue;
            }

            if (invocation.Expression is IdentifierNameSyntax identifier &&
                identifier.Identifier.ValueText == localFunctionName)
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetInvokedDelegateParameterName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "Invoke",
                Expression: IdentifierNameSyntax identifier
            } => identifier.Identifier.ValueText,
            MemberBindingExpressionSyntax
            {
                Name.Identifier.ValueText: "Invoke"
            } when invocation.Parent is ConditionalAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax identifier
            } => identifier.Identifier.ValueText,
            _ => null
        };
    }

    private static bool IsSynchronousDelegateParameter(IParameterSymbol parameter)
    {
        if (parameter.Type is not INamedTypeSymbol namedType || !IsDelegateType(namedType))
        {
            return false;
        }

        var invokeMethod = namedType.DelegateInvokeMethod;
        if (invokeMethod is null)
        {
            return false;
        }

        return !IsTaskReturnType(invokeMethod.ReturnType);
    }

    private static bool IsTaskReturnType(ITypeSymbol type)
    {
        return type is INamedTypeSymbol namedType
            && namedType.Name == "Task"
            && namedType.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks";
    }

    /// <summary>
    /// Checks if the type is IResourceBuilder&lt;T&gt; where T is a type parameter (open generic).
    /// </summary>
    private static bool IsOpenGenericResourceBuilder(ITypeSymbol type, WellKnownTypes wellKnownTypes)
    {
        if (type is not INamedTypeSymbol namedType || !namedType.IsGenericType)
        {
            return false;
        }

        try
        {
            var iResourceBuilderType = wellKnownTypes.Get(WellKnownTypeData.WellKnownType.Aspire_Hosting_ApplicationModel_IResourceBuilder_1);
            if (!SymbolEqualityComparer.Default.Equals(namedType.OriginalDefinition, iResourceBuilderType))
            {
                return false;
            }

            // Check that the type argument is a type parameter (open generic), not a concrete type
            return namedType.TypeArguments.Length == 1 && namedType.TypeArguments[0] is ITypeParameterSymbol;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// If the type is IResourceBuilder&lt;ConcreteType&gt; (not open generic), returns the ConcreteType name; otherwise null.
    /// </summary>
    private static string? GetConcreteResourceBuilderTypeName(ITypeSymbol type, WellKnownTypes wellKnownTypes)
    {
        if (type is not INamedTypeSymbol namedType || !namedType.IsGenericType)
        {
            return null;
        }

        try
        {
            var iResourceBuilderType = wellKnownTypes.Get(WellKnownTypeData.WellKnownType.Aspire_Hosting_ApplicationModel_IResourceBuilder_1);
            if (!SymbolEqualityComparer.Default.Equals(namedType.OriginalDefinition, iResourceBuilderType))
            {
                return null;
            }

            // Check that the type argument is a concrete type, not a type parameter
            if (namedType.TypeArguments.Length == 1 && namedType.TypeArguments[0] is not ITypeParameterSymbol)
            {
                return namedType.TypeArguments[0].Name;
            }
        }
        catch (InvalidOperationException)
        {
            // Type not found
        }

        return null;
    }

    private static bool IsBuilderType(ITypeSymbol type, WellKnownTypes wellKnownTypes)
    {
        // Check IDistributedApplicationBuilder
        try
        {
            var distributedAppBuilder = wellKnownTypes.Get(WellKnownTypeData.WellKnownType.Aspire_Hosting_IDistributedApplicationBuilder);
            if (SymbolEqualityComparer.Default.Equals(type, distributedAppBuilder) ||
                WellKnownTypes.Implements(type, distributedAppBuilder))
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            // Type not found
        }

        // Check IResourceBuilder<T>
        if (IsResourceBuilderType(type, wellKnownTypes))
        {
            return true;
        }

        return false;
    }

    private static bool RequiresExplicitExportCoverage(
        ITypeSymbol type,
        WellKnownTypes wellKnownTypes,
        INamedTypeSymbol aspireExportAttribute)
    {
        return IsBuilderType(type, wellKnownTypes) ||
               IsResourceType(type, wellKnownTypes) ||
               HasAspireExportAttribute(type, aspireExportAttribute);
    }

    private static string? GetIncompatibilityReason(
        IMethodSymbol method,
        WellKnownTypes wellKnownTypes,
        INamedTypeSymbol aspireExportAttribute)
    {
        var reasons = new List<string>();

        // Check for out parameters
        foreach (var param in method.Parameters)
        {
            if (param.RefKind == RefKind.Out)
            {
                reasons.Add($"'out' parameter '{param.Name}' is not ATS-compatible");
            }
        }

        // Check for open generic type parameters on the method itself (not constrained to IResource)
        if (method.TypeParameters.Length > 0)
        {
            foreach (var tp in method.TypeParameters)
            {
                var hasResourceConstraint = false;
                foreach (var constraint in tp.ConstraintTypes)
                {
                    if (IsResourceType(constraint, wellKnownTypes) || IsResourceBuilderType(constraint, wellKnownTypes))
                    {
                        hasResourceConstraint = true;
                        break;
                    }
                }
                if (!hasResourceConstraint)
                {
                    reasons.Add($"open generic type parameter '{tp.Name}' is not ATS-compatible");
                }
            }
        }

        // Check parameters (skip 'this' first parameter)
        for (var i = 1; i < method.Parameters.Length; i++)
        {
            var param = method.Parameters[i];
            var paramType = param.Type;

            // Skip params arrays if element type is compatible
            if (param.IsParams && paramType is IArrayTypeSymbol paramsArray)
            {
                if (!IsAtsCompatibleValueType(paramsArray.ElementType, wellKnownTypes, aspireExportAttribute))
                {
                    reasons.Add($"parameter '{param.Name}' uses '{paramsArray.ElementType.ToDisplayString()}[]' which is not ATS-compatible");
                }
                continue;
            }

            // Check delegate types more carefully
            if (IsDelegateType(paramType))
            {
                var reason = GetDelegateIncompatibilityReason(param, paramType, wellKnownTypes, aspireExportAttribute);
                if (reason is not null)
                {
                    reasons.Add(reason);
                }
                continue;
            }

            if (!IsAtsCompatibleValueType(paramType, wellKnownTypes, aspireExportAttribute))
            {
                reasons.Add($"parameter '{param.Name}' of type '{paramType.ToDisplayString()}' is not ATS-compatible");
            }
        }

        // Check return type
        if (!IsAtsCompatibleType(method.ReturnType, wellKnownTypes, aspireExportAttribute))
        {
            reasons.Add($"return type '{method.ReturnType.ToDisplayString()}' is not ATS-compatible");
        }

        if (reasons.Count == 0)
        {
            return null;
        }

        return string.Join("; ", reasons) + ".";
    }

    private static string? GetDelegateIncompatibilityReason(
        IParameterSymbol param,
        ITypeSymbol delegateType,
        WellKnownTypes wellKnownTypes,
        INamedTypeSymbol _)
    {
        if (delegateType is not INamedTypeSymbol namedDelegate)
        {
            return $"parameter '{param.Name}' uses delegate type which is not ATS-compatible";
        }

        // Find the Invoke method to get delegate signature
        var invokeMethod = namedDelegate.DelegateInvokeMethod;
        if (invokeMethod is null)
        {
            return null;
        }

        // Check delegate parameter types for known incompatible patterns
        foreach (var delegateParam in invokeMethod.Parameters)
        {
            var dpType = delegateParam.Type;
            var dpTypeName = dpType.ToDisplayString();

            // Check for known incompatible context types
            if (dpTypeName is "System.IServiceProvider" or
                "System.Text.Json.Utf8JsonWriter" or
                "System.Threading.CancellationToken" or
                "System.Net.Http.HttpRequestMessage")
            {
                return $"parameter '{param.Name}' uses delegate with '{dpType.Name}' which is not ATS-compatible";
            }

            // Check for IResource as a raw parameter (not wrapped in IResourceBuilder<T>)
            if (IsRawResourceInterface(dpType, wellKnownTypes))
            {
                return $"parameter '{param.Name}' uses delegate with raw '{dpType.Name}' interface which is not ATS-compatible";
            }
        }

        return null;
    }

    private static bool IsRawResourceInterface(ITypeSymbol type, WellKnownTypes wellKnownTypes)
    {
        try
        {
            var iResourceType = wellKnownTypes.Get(WellKnownTypeData.WellKnownType.Aspire_Hosting_ApplicationModel_IResource);
            return SymbolEqualityComparer.Default.Equals(type, iResourceType);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void AnalyzeUnionAttribute(
        SymbolAnalysisContext context,
        ImmutableArray<AttributeData> attributes,
        INamedTypeSymbol aspireUnionAttribute,
        WellKnownTypes wellKnownTypes,
        INamedTypeSymbol aspireExportAttribute)
    {
        foreach (var attr in attributes)
        {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, aspireUnionAttribute))
            {
                continue;
            }

            var attrSyntax = attr.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken);
            var attrLocation = attrSyntax?.GetLocation() ?? Location.None;

            // Get the types from the constructor argument (params Type[] types)
            if (attr.ConstructorArguments.Length == 0)
            {
                // No arguments - report ASPIREEXPORT005
                context.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.s_unionRequiresAtLeastTwoTypes,
                    attrLocation,
                    0));
                continue;
            }

            var typesArg = attr.ConstructorArguments[0];
            if (typesArg.Kind != TypedConstantKind.Array)
            {
                continue;
            }

            var types = typesArg.Values;

            // ASPIREEXPORT005: Check that we have at least 2 types
            if (types.Length < 2)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.s_unionRequiresAtLeastTwoTypes,
                    attrLocation,
                    types.Length));
            }

            // ASPIREEXPORT006: Check that each type is ATS-compatible
            foreach (var typeConstant in types)
            {
                if (typeConstant.Value is INamedTypeSymbol typeSymbol)
                {
                    if (!IsAtsCompatibleValueType(typeSymbol, wellKnownTypes, aspireExportAttribute))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            Diagnostics.s_unionTypeMustBeAtsCompatible,
                            attrLocation,
                            typeSymbol.ToDisplayString()));
                    }
                }
            }
        }
    }

    private static void ReportDuplicateExports(
        CompilationAnalysisContext context,
        ConcurrentDictionary<(string ExportId, string TargetType), ConcurrentBag<(IMethodSymbol Method, Location Location)>> exportsByKey)
    {
        foreach (var kvp in exportsByKey)
        {
            var methods = kvp.Value.ToArray();
            if (methods.Length > 1)
            {
                // Report on all methods that share the same export ID and target type
                foreach (var (_, location) in methods)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.s_duplicateExportId,
                        location,
                        kvp.Key.ExportId,
                        kvp.Key.TargetType));
                }
            }
        }
    }

    private static string? GetExportId(AttributeData attribute)
    {
        if (attribute.ConstructorArguments.Length > 0 &&
            attribute.ConstructorArguments[0].Value is string id)
        {
            return id;
        }
        return null;
    }

    private static bool TryGetEffectiveAspireExportAttribute(IMethodSymbol method, INamedTypeSymbol aspireExportAttribute, out AttributeData? exportAttribute, out AttributeData? containingTypeExportAttribute)
    {
        foreach (var attr in method.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, aspireExportAttribute))
            {
                exportAttribute = attr;
                containingTypeExportAttribute = GetContainingTypeAspireExportAttribute(method.ContainingType, aspireExportAttribute);
                return true;
            }
        }

        containingTypeExportAttribute = GetContainingTypeAspireExportAttribute(method.ContainingType, aspireExportAttribute);
        if (containingTypeExportAttribute is not null)
        {
            exportAttribute = containingTypeExportAttribute;
            return true;
        }

        exportAttribute = null;
        containingTypeExportAttribute = null;
        return false;
    }

    private static AttributeData? GetContainingTypeAspireExportAttribute(INamedTypeSymbol? type, INamedTypeSymbol aspireExportAttribute)
    {
        if (type is null)
        {
            return null;
        }

        foreach (var attr in type.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, aspireExportAttribute))
            {
                return attr;
            }
        }

        return null;
    }

    private static bool IsRunSyncOnBackgroundThreadEnabled(AttributeData? exportAttribute)
    {
        if (exportAttribute is null)
        {
            return false;
        }

        foreach (var namedArgument in exportAttribute.NamedArguments)
        {
            if (namedArgument.Key == RunSyncOnBackgroundThreadPropertyName &&
                namedArgument.Value.Value is bool enabled)
            {
                return enabled;
            }
        }

        return false;
    }

    private static bool IsAtsCompatibleType(
        ITypeSymbol type,
        WellKnownTypes wellKnownTypes,
        INamedTypeSymbol aspireExportAttribute)
    {
        // void is allowed
        if (type.SpecialType == SpecialType.System_Void)
        {
            return true;
        }

        // Task and Task<T> are allowed (for async methods)
        if (IsTaskType(type, wellKnownTypes, aspireExportAttribute))
        {
            return true;
        }

        return IsAtsCompatibleValueType(type, wellKnownTypes, aspireExportAttribute);
    }

    private static bool IsTaskType(
        ITypeSymbol type,
        WellKnownTypes wellKnownTypes,
        INamedTypeSymbol aspireExportAttribute)
    {
        // Check for Task
        try
        {
            var taskType = wellKnownTypes.Get(WellKnownTypeData.WellKnownType.System_Threading_Tasks_Task);
            if (SymbolEqualityComparer.Default.Equals(type, taskType))
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            // Type not found
        }

        // Check for Task<T>
        if (type is INamedTypeSymbol namedType && namedType.IsGenericType)
        {
            try
            {
                var taskOfTType = wellKnownTypes.Get(WellKnownTypeData.WellKnownType.System_Threading_Tasks_Task_1);
                if (SymbolEqualityComparer.Default.Equals(namedType.OriginalDefinition, taskOfTType))
                {
                    // Validate the T in Task<T> is also ATS-compatible
                    return namedType.TypeArguments.Length == 1 &&
                           IsAtsCompatibleValueType(namedType.TypeArguments[0], wellKnownTypes, aspireExportAttribute);
                }
            }
            catch (InvalidOperationException)
            {
                // Type not found
            }
        }

        return false;
    }

    private static bool IsAtsCompatibleValueType(
        ITypeSymbol type,
        WellKnownTypes wellKnownTypes,
        INamedTypeSymbol? aspireExportAttribute = null)
    {
        // Handle nullable types
        if (type is INamedTypeSymbol namedType &&
            namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            namedType.TypeArguments.Length == 1)
        {
            type = namedType.TypeArguments[0];
        }

        // Simple/primitive types (includes System.Object)
        if (IsSimpleType(type, wellKnownTypes))
        {
            return true;
        }

        // Enums
        if (type.TypeKind == TypeKind.Enum)
        {
            return true;
        }

        // Arrays of ATS-compatible types
        if (type is IArrayTypeSymbol arrayType)
        {
            return IsAtsCompatibleValueType(arrayType.ElementType, wellKnownTypes, aspireExportAttribute);
        }

        // Collection types (Dictionary, List, IReadOnlyList, etc.)
        if (IsAtsCompatibleCollectionType(type, wellKnownTypes, aspireExportAttribute))
        {
            return true;
        }

        // IResource types
        if (IsResourceType(type, wellKnownTypes))
        {
            return true;
        }

        // IResourceBuilder<T> types
        if (IsResourceBuilderType(type, wellKnownTypes))
        {
            return true;
        }

        // Types with [AspireExport] or [AspireDto] attribute
        if (aspireExportAttribute != null && HasAspireExportAttribute(type, aspireExportAttribute))
        {
            return true;
        }

        // Types with [AspireDto] attribute
        if (HasAspireDtoAttribute(type))
        {
            return true;
        }

        return false;
    }

    private static bool IsSimpleType(ITypeSymbol type, WellKnownTypes wellKnownTypes)
    {
        // Primitives via SpecialType
        if (type.SpecialType switch
        {
            SpecialType.System_Boolean => true,
            SpecialType.System_Byte => true,
            SpecialType.System_SByte => true,
            SpecialType.System_Int16 => true,
            SpecialType.System_UInt16 => true,
            SpecialType.System_Int32 => true,
            SpecialType.System_UInt32 => true,
            SpecialType.System_Int64 => true,
            SpecialType.System_UInt64 => true,
            SpecialType.System_Single => true,
            SpecialType.System_Double => true,
            SpecialType.System_Decimal => true,
            SpecialType.System_Char => true,
            SpecialType.System_String => true,
            SpecialType.System_DateTime => true,
            SpecialType.System_Object => true, // Maps to 'any' in ATS
            _ => false
        })
        {
            return true;
        }

        // Well-known scalar types using symbol comparison
        return IsWellKnownScalarType(type, wellKnownTypes);
    }

    private static bool IsWellKnownScalarType(ITypeSymbol type, WellKnownTypes wellKnownTypes)
    {
        // Date/time types
        if (TryMatchType(type, wellKnownTypes, WellKnownTypeData.WellKnownType.System_DateTimeOffset) ||
            TryMatchType(type, wellKnownTypes, WellKnownTypeData.WellKnownType.System_TimeSpan) ||
            TryMatchType(type, wellKnownTypes, WellKnownTypeData.WellKnownType.System_DateOnly) ||
            TryMatchType(type, wellKnownTypes, WellKnownTypeData.WellKnownType.System_TimeOnly))
        {
            return true;
        }

        // Other scalar types
        if (TryMatchType(type, wellKnownTypes, WellKnownTypeData.WellKnownType.System_Guid) ||
            TryMatchType(type, wellKnownTypes, WellKnownTypeData.WellKnownType.System_Uri))
        {
            return true;
        }

        return false;
    }

    private static bool TryMatchType(ITypeSymbol type, WellKnownTypes wellKnownTypes, WellKnownTypeData.WellKnownType wellKnownType)
    {
        try
        {
            var knownType = wellKnownTypes.Get(wellKnownType);
            return SymbolEqualityComparer.Default.Equals(type, knownType);
        }
        catch (InvalidOperationException)
        {
            // Type not found in compilation
            return false;
        }
    }

    private static bool TryMatchGenericType(ITypeSymbol type, WellKnownTypes wellKnownTypes, WellKnownTypeData.WellKnownType wellKnownType)
    {
        if (type is not INamedTypeSymbol namedType || !namedType.IsGenericType)
        {
            return false;
        }

        try
        {
            var knownType = wellKnownTypes.Get(wellKnownType);
            return SymbolEqualityComparer.Default.Equals(namedType.OriginalDefinition, knownType);
        }
        catch (InvalidOperationException)
        {
            // Type not found in compilation
            return false;
        }
    }

    private static bool IsAtsCompatibleCollectionType(
        ITypeSymbol type,
        WellKnownTypes wellKnownTypes,
        INamedTypeSymbol? aspireExportAttribute)
    {
        if (type is not INamedTypeSymbol namedType || !namedType.IsGenericType)
        {
            return false;
        }

        // Dictionary<K,V> and IDictionary<K,V>
        if (TryMatchGenericType(type, wellKnownTypes, WellKnownTypeData.WellKnownType.System_Collections_Generic_Dictionary_2) ||
            TryMatchGenericType(type, wellKnownTypes, WellKnownTypeData.WellKnownType.System_Collections_Generic_IDictionary_2))
        {
            // Validate key and value types are ATS-compatible
            return namedType.TypeArguments.Length == 2 &&
                   IsAtsCompatibleValueType(namedType.TypeArguments[0], wellKnownTypes, aspireExportAttribute) &&
                   IsAtsCompatibleValueType(namedType.TypeArguments[1], wellKnownTypes, aspireExportAttribute);
        }

        // List<T> and IList<T>
        if (TryMatchGenericType(type, wellKnownTypes, WellKnownTypeData.WellKnownType.System_Collections_Generic_List_1) ||
            TryMatchGenericType(type, wellKnownTypes, WellKnownTypeData.WellKnownType.System_Collections_Generic_IList_1))
        {
            return namedType.TypeArguments.Length == 1 &&
                   IsAtsCompatibleValueType(namedType.TypeArguments[0], wellKnownTypes, aspireExportAttribute);
        }

        // IReadOnlyList<T> and IReadOnlyCollection<T>
        if (TryMatchGenericType(type, wellKnownTypes, WellKnownTypeData.WellKnownType.System_Collections_Generic_IReadOnlyList_1) ||
            TryMatchGenericType(type, wellKnownTypes, WellKnownTypeData.WellKnownType.System_Collections_Generic_IReadOnlyCollection_1) ||
            TryMatchGenericType(type, wellKnownTypes, WellKnownTypeData.WellKnownType.System_Collections_Generic_IEnumerable_1))
        {
            return namedType.TypeArguments.Length == 1 &&
                   IsAtsCompatibleValueType(namedType.TypeArguments[0], wellKnownTypes, aspireExportAttribute);
        }

        // IReadOnlyDictionary<K,V>
        if (TryMatchGenericType(type, wellKnownTypes, WellKnownTypeData.WellKnownType.System_Collections_Generic_IReadOnlyDictionary_2))
        {
            return namedType.TypeArguments.Length == 2 &&
                   IsAtsCompatibleValueType(namedType.TypeArguments[0], wellKnownTypes, aspireExportAttribute) &&
                   IsAtsCompatibleValueType(namedType.TypeArguments[1], wellKnownTypes, aspireExportAttribute);
        }

        return false;
    }

    private static bool HasAspireExportAttribute(ITypeSymbol type, INamedTypeSymbol aspireExportAttribute)
    {
        // Check direct attributes on the type
        foreach (var attr in type.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, aspireExportAttribute))
            {
                return true;
            }
        }

        var containingAssembly = type.ContainingAssembly;
        if (containingAssembly is null)
        {
            return false;
        }

        foreach (var attr in containingAssembly.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, aspireExportAttribute))
            {
                continue;
            }

            if (TryGetAssemblyExportedType(attr, out var exportedType) &&
                SymbolEqualityComparer.Default.Equals(type, exportedType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetAssemblyExportedType(AttributeData attribute, out ITypeSymbol? exportedType)
    {
        exportedType = null;

        if (attribute.ConstructorArguments.Length > 0 &&
            attribute.ConstructorArguments[0].Value is ITypeSymbol constructorType)
        {
            exportedType = constructorType;
            return true;
        }

        foreach (var namedArgument in attribute.NamedArguments)
        {
            if (namedArgument.Key == "Type" &&
                namedArgument.Value.Value is ITypeSymbol namedType)
            {
                exportedType = namedType;
                return true;
            }
        }

        return false;
    }

    private static bool HasAspireDtoAttribute(ITypeSymbol type)
    {
        // Check for [AspireDto] attribute by name (simpler than adding to WellKnownTypes dependency)
        foreach (var attr in type.GetAttributes())
        {
            if (attr.AttributeClass?.Name == "AspireDtoAttribute" &&
                attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "Aspire.Hosting")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsResourceType(ITypeSymbol type, WellKnownTypes wellKnownTypes)
    {
        try
        {
            var iResourceType = wellKnownTypes.Get(WellKnownTypeData.WellKnownType.Aspire_Hosting_ApplicationModel_IResource);
            return WellKnownTypes.Implements(type, iResourceType) ||
                   SymbolEqualityComparer.Default.Equals(type, iResourceType);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsResourceBuilderType(ITypeSymbol type, WellKnownTypes wellKnownTypes)
    {
        if (type is not INamedTypeSymbol namedType)
        {
            return false;
        }

        try
        {
            var iResourceBuilderType = wellKnownTypes.Get(WellKnownTypeData.WellKnownType.Aspire_Hosting_ApplicationModel_IResourceBuilder_1);

            // Check if type itself is IResourceBuilder<T>
            if (namedType.IsGenericType &&
                SymbolEqualityComparer.Default.Equals(namedType.OriginalDefinition, iResourceBuilderType))
            {
                return true;
            }

            // Check interfaces for IResourceBuilder<T>
            foreach (var iface in namedType.AllInterfaces)
            {
                if (iface.IsGenericType &&
                    SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, iResourceBuilderType))
                {
                    return true;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Type not found
        }

        return false;
    }

    private static bool IsAtsCompatibleParameter(
        IParameterSymbol parameter,
        WellKnownTypes wellKnownTypes,
        INamedTypeSymbol aspireExportAttribute)
    {
        var type = parameter.Type;

        // Delegate types (Func<>, Action<>, custom delegates) are allowed as callbacks
        if (IsDelegateType(type))
        {
            return true;
        }

        // params arrays are allowed if element type is compatible
        if (parameter.IsParams && type is IArrayTypeSymbol arrayType)
        {
            return IsAtsCompatibleValueType(arrayType.ElementType, wellKnownTypes, aspireExportAttribute);
        }

        return IsAtsCompatibleValueType(type, wellKnownTypes, aspireExportAttribute);
    }

    private static bool IsDelegateType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol namedType)
        {
            return namedType.TypeKind == TypeKind.Delegate;
        }
        return false;
    }
}

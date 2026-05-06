// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Hosting.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;

namespace Aspire.Internal.Analyzers;

/// <summary>
/// Builds and caches a value -> [Type.Member] map of repo-local string constants.
/// </summary>
/// <remarks>
/// <para>Discovery rules:</para>
/// <list type="bullet">
/// <item><description><b>Convention</b>: any <c>internal static class</c> (or static class nested
/// inside one) whose name starts with <c>Known</c>, defined in an Aspire.* assembly.</description></item>
/// <item><description><b>Attribute opt-in</b>: any static class annotated with
/// <c>[InternalKnownConstants]</c> regardless of name (matched by simple name to avoid a
/// hard symbol dependency).</description></item>
/// </list>
/// <para>Each discovered class can also carry scope filters (Assemblies / Namespaces) on its
/// <c>[InternalKnownConstants]</c> attribute that constrain where its constants apply.</para>
/// </remarks>
internal sealed class KnownConstantCatalog
{
    private const int MinimumLiteralLength = 4;
    private const string AttributeSimpleName = "InternalKnownConstantsAttribute";

    private static readonly BoundedCacheWithFactory<Compilation, KnownConstantCatalog> s_cache = new();

    private readonly Dictionary<string, ImmutableArray<KnownConstant>> _byValue;

    private KnownConstantCatalog(Dictionary<string, ImmutableArray<KnownConstant>> byValue)
    {
        _byValue = byValue;
    }

    public static KnownConstantCatalog GetOrCreate(Compilation compilation)
        => s_cache.GetOrCreateValue(compilation, static c => Build(c));

    public bool TryGetMatches(string value, out ImmutableArray<KnownConstant> matches)
    {
        if (value.Length < MinimumLiteralLength)
        {
            matches = ImmutableArray<KnownConstant>.Empty;
            return false;
        }

        return _byValue.TryGetValue(value, out matches) && matches.Length > 0;
    }

    private static KnownConstantCatalog Build(Compilation compilation)
    {
        var byValue = new Dictionary<string, List<KnownConstant>>(System.StringComparer.Ordinal);

        // Walk the compilation's source assembly (always, regardless of name —
        // the consuming project is by definition in scope).
        Visit(compilation.Assembly.GlobalNamespace, byValue);

        // Walk referenced Aspire-owned assemblies. We deliberately exclude
        // third-party assemblies so we don't pick up unrelated Known* types.
        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol referencedAssembly)
            {
                var name = referencedAssembly.Identity.Name;
                if (name == "Aspire" || name.StartsWith("Aspire.", System.StringComparison.Ordinal))
                {
                    Visit(referencedAssembly.GlobalNamespace, byValue);
                }
            }
        }

        var frozen = new Dictionary<string, ImmutableArray<KnownConstant>>(byValue.Count, System.StringComparer.Ordinal);
        foreach (var kvp in byValue)
        {
            frozen[kvp.Key] = ImmutableArray.CreateRange(kvp.Value);
        }
        return new KnownConstantCatalog(frozen);
    }

    private static void Visit(INamespaceOrTypeSymbol container, Dictionary<string, List<KnownConstant>> byValue)
    {
        foreach (var member in container.GetMembers())
        {
            if (member is INamespaceSymbol ns)
            {
                Visit(ns, byValue);
            }
            else if (member is INamedTypeSymbol type)
            {
                if (IsConstantsRoot(type))
                {
                    // Found a constants root: collect its constants and recursively
                    // walk every nested static class regardless of name. This is why
                    // patterns like KnownConfigNames.Legacy.ResourceUrl (Legacy is
                    // just a static nested class) are still picked up.
                    var scope = ReadScope(type);
                    CollectFromRootAndNested(type, scope, byValue);
                }
                else
                {
                    // Not a constants root, but a constants root might appear deeper
                    // (e.g. internal static class Outer { public static class KnownInner }).
                    foreach (var nested in type.GetTypeMembers())
                    {
                        VisitTypeForConstantsRoots(nested, byValue);
                    }
                }
            }
        }
    }

    private static void VisitTypeForConstantsRoots(INamedTypeSymbol type, Dictionary<string, List<KnownConstant>> byValue)
    {
        if (IsConstantsRoot(type))
        {
            var scope = ReadScope(type);
            CollectFromRootAndNested(type, scope, byValue);
            return;
        }

        foreach (var nested in type.GetTypeMembers())
        {
            VisitTypeForConstantsRoots(nested, byValue);
        }
    }

    private static void CollectFromRootAndNested(INamedTypeSymbol type, KnownConstantScope scope, Dictionary<string, List<KnownConstant>> byValue)
    {
        CollectConstants(type, scope, byValue);

        foreach (var nested in type.GetTypeMembers())
        {
            if (nested.IsStatic && nested.TypeKind == TypeKind.Class)
            {
                // Nested static classes inherit the outer scope but may override it
                // by applying their own attribute (rare; allowed for completeness).
                var nestedScope = HasAttribute(nested) ? ReadScope(nested) : scope;
                CollectFromRootAndNested(nested, nestedScope, byValue);
            }
        }
    }

    private static bool IsConstantsRoot(INamedTypeSymbol type)
    {
        if (!type.IsStatic || type.TypeKind != TypeKind.Class)
        {
            return false;
        }

        // Convention: name starts with "Known".
        if (type.Name.StartsWith("Known", System.StringComparison.Ordinal))
        {
            return true;
        }

        // Attribute opt-in: any static class with [InternalKnownConstants].
        return HasAttribute(type);
    }

    private static bool HasAttribute(INamedTypeSymbol type)
    {
        foreach (var attr in type.GetAttributes())
        {
            if (attr.AttributeClass?.Name == AttributeSimpleName)
            {
                return true;
            }
        }
        return false;
    }

    private static KnownConstantScope ReadScope(INamedTypeSymbol type)
    {
        foreach (var attr in type.GetAttributes())
        {
            if (attr.AttributeClass?.Name != AttributeSimpleName)
            {
                continue;
            }

            ImmutableArray<string> assemblies = default;
            ImmutableArray<string> namespaces = default;
            foreach (var named in attr.NamedArguments)
            {
                switch (named.Key)
                {
                    case "Assemblies":
                        assemblies = ExtractStringArray(named.Value);
                        break;
                    case "Namespaces":
                        namespaces = ExtractStringArray(named.Value);
                        break;
                }
            }
            return new KnownConstantScope(assemblies, namespaces);
        }
        return KnownConstantScope.Unscoped;
    }

    private static ImmutableArray<string> ExtractStringArray(TypedConstant value)
    {
        if (value.Kind != TypedConstantKind.Array || value.Values.IsDefaultOrEmpty)
        {
            return ImmutableArray<string>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<string>(value.Values.Length);
        foreach (var item in value.Values)
        {
            if (item.Value is string s && !string.IsNullOrEmpty(s))
            {
                builder.Add(s);
            }
        }
        return builder.ToImmutable();
    }

    private static void CollectConstants(INamedTypeSymbol type, KnownConstantScope scope, Dictionary<string, List<KnownConstant>> byValue)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is IFieldSymbol field
                && field.IsConst
                && field.Type.SpecialType == SpecialType.System_String
                && field.ConstantValue is string value
                && value.Length >= MinimumLiteralLength
                && IsAccessibleConst(field))
            {
                if (!byValue.TryGetValue(value, out var list))
                {
                    list = new List<KnownConstant>(1);
                    byValue[value] = list;
                }
                list.Add(new KnownConstant(field, scope));
            }
        }
    }

    private static bool IsAccessibleConst(IFieldSymbol field)
    {
        // Private (and protected) consts are implementation details that the
        // consumer cannot reference; suggesting them would be wrong. Only treat
        // public / internal / public-in-internal-class fields as "use this" candidates.
        switch (field.DeclaredAccessibility)
        {
            case Accessibility.Public:
            case Accessibility.Internal:
            case Accessibility.ProtectedOrInternal:
                return true;
            default:
                return false;
        }
    }
}

internal readonly struct KnownConstantScope
{
    public static readonly KnownConstantScope Unscoped = new(default, default);

    public KnownConstantScope(ImmutableArray<string> assemblies, ImmutableArray<string> namespaces)
    {
        Assemblies = assemblies.IsDefault ? ImmutableArray<string>.Empty : assemblies;
        Namespaces = namespaces.IsDefault ? ImmutableArray<string>.Empty : namespaces;
    }

    public ImmutableArray<string> Assemblies { get; }
    public ImmutableArray<string> Namespaces { get; }

    public bool IsUnscoped => Assemblies.IsEmpty && Namespaces.IsEmpty;

    /// <summary>
    /// Returns <c>true</c> when this scope applies to the given consumer assembly + namespace.
    /// Unscoped (no filters) always applies. When filters are present, both must match
    /// (or be empty for that axis).
    /// </summary>
    public bool AppliesTo(string consumerAssembly, string consumerNamespace)
    {
        if (!Assemblies.IsEmpty)
        {
            var assemblyMatch = false;
            foreach (var name in Assemblies)
            {
                if (string.Equals(name, consumerAssembly, System.StringComparison.OrdinalIgnoreCase))
                {
                    assemblyMatch = true;
                    break;
                }
            }
            if (!assemblyMatch)
            {
                return false;
            }
        }

        if (!Namespaces.IsEmpty)
        {
            var namespaceMatch = false;
            foreach (var prefix in Namespaces)
            {
                if (NamespaceMatches(prefix, consumerNamespace))
                {
                    namespaceMatch = true;
                    break;
                }
            }
            if (!namespaceMatch)
            {
                return false;
            }
        }

        return true;
    }

    private static bool NamespaceMatches(string prefix, string consumerNamespace)
    {
        if (string.Equals(prefix, consumerNamespace, System.StringComparison.Ordinal))
        {
            return true;
        }
        return consumerNamespace.Length > prefix.Length
            && consumerNamespace[prefix.Length] == '.'
            && consumerNamespace.StartsWith(prefix, System.StringComparison.Ordinal);
    }
}

internal readonly struct KnownConstant
{
    public KnownConstant(IFieldSymbol field, KnownConstantScope scope)
    {
        Field = field;
        Scope = scope;
    }

    public IFieldSymbol Field { get; }
    public KnownConstantScope Scope { get; }

    /// <summary>
    /// Returns the outermost containing type (the "root" Known/attributed class) — used
    /// to detect whether a multi-match diagnostic is "same family" (one root) or
    /// "cross-domain" (multiple roots, ambiguous).
    /// </summary>
    public INamedTypeSymbol RootContainingType
    {
        get
        {
            var t = Field.ContainingType;
            while (t.ContainingType is { } parent)
            {
                t = parent;
            }
            return t;
        }
    }

    /// <summary>
    /// Returns a display name like "KnownConfigNames.ResourceServiceEndpointUrl"
    /// or, for nested classes, "KnownConfigNames.Legacy.ResourceServiceEndpointUrl".
    /// Namespace is intentionally omitted to keep diagnostic messages readable.
    /// </summary>
    public string GetDisplayName()
    {
        var sb = new System.Text.StringBuilder();
        AppendTypeChain(sb, Field.ContainingType);
        sb.Append('.');
        sb.Append(Field.Name);
        return sb.ToString();
    }

    private static void AppendTypeChain(System.Text.StringBuilder sb, INamedTypeSymbol type)
    {
        if (type.ContainingType is { } parent)
        {
            AppendTypeChain(sb, parent);
            sb.Append('.');
        }
        sb.Append(type.Name);
    }
}

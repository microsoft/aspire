// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Aspire.Hosting.Analyzers.Infrastructure;

internal class WellKnownTypes
{
    private static readonly BoundedCacheWithFactory<Compilation, WellKnownTypes> s_lazyWellKnownTypesCache = new();

    public static WellKnownTypes GetOrCreate(Compilation compilation) =>
        s_lazyWellKnownTypesCache.GetOrCreateValue(compilation, static c => new WellKnownTypes(c));

    private readonly INamedTypeSymbol?[] _lazyWellKnownTypes;
    private readonly Compilation _compilation;

    static WellKnownTypes()
    {
        AssertEnumAndTableInSync();
    }

    [Conditional("DEBUG")]
    private static void AssertEnumAndTableInSync()
    {
        for (var i = 0; i < WellKnownTypeData.WellKnownTypeNames.Length; i++)
        {
            var name = WellKnownTypeData.WellKnownTypeNames[i];
            var typeId = (WellKnownTypeData.WellKnownType)i;

            var typeIdName = typeId.ToString().Replace("__", "+").Replace('_', '.');

            var separator = name.IndexOf('`');
            if (separator >= 0)
            {
                // Ignore type parameter qualifier for generic types.
                name = name.Substring(0, separator);
                typeIdName = typeIdName.Substring(0, separator);
            }

            Debug.Assert(name == typeIdName, $"Enum name ({typeIdName}) and type name ({name}) must match at {i}");
        }
    }

    private WellKnownTypes(Compilation compilation)
    {
        _lazyWellKnownTypes = new INamedTypeSymbol?[WellKnownTypeData.WellKnownTypeNames.Length];
        _compilation = compilation;
    }

    public INamedTypeSymbol Get(SpecialType type)
    {
        return _compilation.GetSpecialType(type);
    }

    public INamedTypeSymbol Get(WellKnownTypeData.WellKnownType type)
    {
        var index = (int)type;
        var symbol = _lazyWellKnownTypes[index];
        if (symbol is not null)
        {
            return symbol;
        }

        // Symbol hasn't been added to the cache yet.
        // Resolve symbol from name, cache, and return.
        return GetAndCache(index);
    }

    public bool TryGet(WellKnownTypeData.WellKnownType type, [NotNullWhen(true)] out INamedTypeSymbol? symbol)
    {
        var index = (int)type;
        symbol = _lazyWellKnownTypes[index];
        if (symbol is not null)
        {
            return true;
        }

        symbol = GetTypeByMetadataNameInTargetAssembly(WellKnownTypeData.WellKnownTypeNames[index]);
        if (symbol is null)
        {
            return false;
        }

        Interlocked.CompareExchange(ref _lazyWellKnownTypes[index], symbol, null);
        symbol = _lazyWellKnownTypes[index];
        return true;
    }

    private INamedTypeSymbol GetAndCache(int index)
    {
        var result = GetTypeByMetadataNameInTargetAssembly(WellKnownTypeData.WellKnownTypeNames[index])
            ?? throw new InvalidOperationException($"Failed to resolve well-known type '{WellKnownTypeData.WellKnownTypeNames[index]}'.");

        Interlocked.CompareExchange(ref _lazyWellKnownTypes[index], result, null);

        // GetTypeByMetadataName should always return the same instance for a name.
        // To ensure we have a consistent value, for thread safety, return symbol set in the array.
        return _lazyWellKnownTypes[index]!;
    }

    // Filter for types within well-known (framework-owned) assemblies only.
    private INamedTypeSymbol? GetTypeByMetadataNameInTargetAssembly(string metadataName)
    {
        var types = _compilation.GetTypesByMetadataName(metadataName);
        if (types.Length == 0)
        {
            return null;
        }

        if (types.Length == 1)
        {
            return types[0];
        }

        // Multiple types match the name. This is most likely caused by someone reusing the namespace + type name in their apps or libraries.
        // Workaround this situation by prioritizing types in Aspire assemblies.
        foreach (var type in types)
        {
            if (type.ContainingAssembly.Identity.Name.StartsWith("Aspire.", StringComparison.Ordinal))
            {
                return type;
            }
        }

        return null;
    }

    public bool IsType(ITypeSymbol type, WellKnownTypeData.WellKnownType[] wellKnownTypes) => IsType(type, wellKnownTypes, out var _);

    public bool IsType(ITypeSymbol type, WellKnownTypeData.WellKnownType[] wellKnownTypes, [NotNullWhen(true)] out WellKnownTypeData.WellKnownType? match)
    {
        foreach (var wellKnownType in wellKnownTypes)
        {
            if (SymbolEqualityComparer.Default.Equals(type, Get(wellKnownType)))
            {
                match = wellKnownType;
                return true;
            }
        }

        match = null;
        return false;
    }

    public bool Implements(ITypeSymbol type, WellKnownTypeData.WellKnownType[] interfaceWellKnownTypes)
    {
        foreach (var wellKnownType in interfaceWellKnownTypes)
        {
            if (Implements(type, Get(wellKnownType)))
            {
                return true;
            }
        }

        return false;
    }

    public static bool Implements(ITypeSymbol? type, ITypeSymbol interfaceType)
    {
        if (type is null)
        {
            return false;
        }

        foreach (var t in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(t, interfaceType))
            {
                return true;
            }
        }
        return false;
    }
}

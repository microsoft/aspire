// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dcp.Model;

internal sealed class Schema
{
    private readonly Dictionary<Type, ResourceMetadata> _byType = new();

    public void Add<T>(string kind, string resource) where T : CustomResource
    {
        Add<T>(Dcp.GroupVersion, kind, resource, isNamespaced: true);
    }

    public void Add<T>(GroupVersion groupVersion, string kind, string resource, bool isNamespaced) where T : CustomResource
    {
        _byType.Add(typeof(T), new(groupVersion, kind, resource, isNamespaced));
    }

    public bool TryGet<T>(out ResourceMetadata metadata) where T : CustomResource
        => _byType.TryGetValue(typeof(T), out metadata);
}

internal readonly record struct ResourceMetadata(GroupVersion GroupVersion, string Kind, string Resource, bool IsNamespaced)
{
    public string? GetNamespace(string? namespaceParameter)
    {
        if (!IsNamespaced && !string.IsNullOrEmpty(namespaceParameter))
        {
            throw new ArgumentException($"Resource kind '{Kind}' is cluster-scoped.", nameof(namespaceParameter));
        }

        return IsNamespaced ? namespaceParameter : null;
    }
}

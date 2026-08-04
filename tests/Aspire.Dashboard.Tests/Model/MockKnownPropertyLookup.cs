// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;

namespace Aspire.Dashboard;

internal sealed class MockKnownPropertyLookup() : IKnownPropertyLookup
{
    private int _sortOrder = int.MaxValue;
    private KnownProperty? _knownProperty;

    public MockKnownPropertyLookup(int sortOrder, KnownProperty? knownProperty) : this()
    {
        _sortOrder = sortOrder;
        _knownProperty = knownProperty;
    }

    public void Set(int sortOrder, KnownProperty? knownProperty)
    {
        _sortOrder = sortOrder;
        _knownProperty = knownProperty;
    }

    public (int SortOrder, KnownProperty? KnownProperty) FindProperty(string uid)
    {
        return (_sortOrder, _knownProperty);
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray.Tests.Helpers;

internal sealed class TestTraySavedStateStore(TraySavedState state) : ITraySavedStateStore
{
    private readonly object _gate = new();
    private TraySavedState _state = state;
    private bool _failSaves;
    private int _saveCount;

    public bool FailSaves
    {
        set
        {
            lock (_gate)
            {
                _failSaves = value;
            }
        }
    }

    public int SaveCount
    {
        get
        {
            lock (_gate)
            {
                return _saveCount;
            }
        }
    }

    public TraySavedState Load()
    {
        lock (_gate)
        {
            return _state;
        }
    }

    public void Save(TraySavedState state)
    {
        lock (_gate)
        {
            if (_failSaves)
            {
                throw new IOException("The test store is unavailable.");
            }
            _state = state;
            _saveCount++;
        }
    }
}

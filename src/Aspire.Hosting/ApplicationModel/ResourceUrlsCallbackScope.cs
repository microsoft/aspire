// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Identifies active URL callbacks while deferred browser origins are resolved.
/// </summary>
internal sealed class ResourceUrlsCallbackScope : IDisposable
{
    private static readonly AsyncLocal<ResourceUrlsCallbackScope?> s_current = new();
    private readonly ResourceUrlsCallbackScope? _previous;
    private readonly IResource _resource;
    private int _active = 1;

    public ResourceUrlsCallbackScope(IResource resource)
    {
        _resource = resource;
        _previous = s_current.Value;
        s_current.Value = this;
    }

    public static IResource? CurrentResource
    {
        get
        {
            for (var scope = s_current.Value; scope is not null; scope = scope._previous)
            {
                if (Volatile.Read(ref scope._active) != 0)
                {
                    return scope._resource;
                }
            }

            return null;
        }
    }

    public void Dispose()
    {
        // Child tasks inherit the scope object through ExecutionContext. Mark it inactive
        // as well as restoring AsyncLocal, so work outliving the callback is not rejected.
        Volatile.Write(ref _active, 0);
        s_current.Value = _previous;
    }
}

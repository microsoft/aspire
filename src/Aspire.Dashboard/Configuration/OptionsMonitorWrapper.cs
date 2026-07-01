// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Configuration;

/// <summary>
/// Simple wrapper that implements IOptions, IOptionsSnapshot, and IOptionsMonitor, returning a fixed options instance.
/// Used in error mode to provide valid default options without triggering validation.
/// </summary>
internal sealed class OptionsMonitorWrapper<T> : IOptions<T>, IOptionsSnapshot<T>, IOptionsMonitor<T> where T : class
{
    private readonly T _options;

    public OptionsMonitorWrapper(T options)
    {
        _options = options;
    }

    // IOptions<T>
    public T Value => _options;

    // IOptionsSnapshot<T> and IOptionsMonitor<T>
    public T CurrentValue => _options;

    public T Get(string? name) => _options;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

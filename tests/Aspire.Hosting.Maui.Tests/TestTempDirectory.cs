// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests;

internal sealed class TestTempDirectory : IDisposable
{
    // The shared helper lives in the global namespace. Expose it in this test namespace
    // so MAUI tests can keep using the same helper without duplicating cleanup logic.
    private readonly global::TestTempDirectory _inner = new();

    public string Path => _inner.Path;

    public void Dispose() => _inner.Dispose();
}

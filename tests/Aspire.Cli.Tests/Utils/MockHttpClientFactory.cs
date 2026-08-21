// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;

namespace Aspire.Cli.Tests.Utils;

/// <summary>
/// Mock HTTP client factory that creates clients using the specified handler.
/// Useful for testing code that depends on IHttpClientFactory.
/// </summary>
internal sealed class MockHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    private readonly ConcurrentQueue<string> _createdClientNames = [];

    /// <summary>
    /// Gets the names requested from the factory.
    /// </summary>
    public IReadOnlyList<string> CreatedClientNames => [.. _createdClientNames];

    /// <summary>
    /// Creates an HTTP client using the configured handler.
    /// </summary>
    /// <param name="name">The name of the client.</param>
    /// <returns>An HTTP client configured with the mock handler.</returns>
    public HttpClient CreateClient(string name)
    {
        _createdClientNames.Enqueue(name);
        return new HttpClient(handler, disposeHandler: false);
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// A test implementation of IHttpClientFactory that creates basic HttpClient instances.
/// </summary>
internal sealed class TestHttpClientFactory : System.Net.Http.IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        return new HttpClient();
    }
}

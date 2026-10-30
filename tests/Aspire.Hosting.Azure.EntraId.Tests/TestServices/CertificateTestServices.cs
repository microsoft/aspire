// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Identity.Client;

namespace Aspire.Hosting.Azure.EntraId.Tests.TestServices;

internal sealed class CertificateTestHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}

internal sealed class CertificateTestMsalHttpClientFactory(HttpClient client) : IMsalHttpClientFactory
{
    public HttpClient GetHttpClient() => client;
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web;

namespace EntraId.Web;

public sealed class GraphController(ITokenAcquisition tokens, IHttpClientFactory clients) : ControllerBase
{
    internal const string GraphScope = "https://graph.microsoft.com/User.Read";
    internal const string HttpClientName = "MicrosoftGraph";

    [HttpGet("/me")]
    [Authorize]
    // The sign-in cookie can outlive the in-memory token cache after a restart. Let Identity.Web
    // challenge again for that account, incremental consent, or conditional access instead of returning 500.
    // See https://learn.microsoft.com/entra/identity-platform/scenario-web-app-call-api-call-api#handle-incremental-consent-and-conditional-access.
    [AuthorizeForScopes(Scopes = new[] { GraphScope })]
    public async Task<IActionResult> GetCurrentUser(CancellationToken cancellationToken)
    {
        var token = await tokens.GetAccessTokenForUserAsync([GraphScope], user: User);
        using var request = new HttpRequestMessage(HttpMethod.Get, "me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var client = clients.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return Content(await response.Content.ReadAsStringAsync(cancellationToken), "application/json");
    }
}

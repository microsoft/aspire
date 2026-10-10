// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// The AppHost's WithReference(entraWeb) fills in the AzureAd section with the tenant and client IDs.
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration);

builder.Services.AddAuthorization();
builder.Services.AddAntiforgery();

var app = builder.Build();

// The sign-in handler builds the redirect URI from the request URL, and README.md registers only the HTTPS one.
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", (HttpContext context, IAntiforgery antiforgery) =>
{
    if (context.User.Identity is not { IsAuthenticated: true } identity)
    {
        return Page("Entra ID playground", """
            <p>You're not signed in.</p>
            <p><a href="/signin">Sign in with Microsoft Entra ID</a></p>
            """);
    }

    var antiforgeryTokens = antiforgery.GetAndStoreTokens(context);
    return Page("Entra ID playground", $"""
        <p>Signed in as <strong>{WebUtility.HtmlEncode(identity.Name)}</strong>.</p>
        <form method="post" action="/signout">
            <input type="hidden" name="{antiforgeryTokens.FormFieldName}" value="{antiforgeryTokens.RequestToken}">
            <button type="submit">Sign out</button>
        </form>
        """);
});

app.MapGet("/signin", () => Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }));

app.MapPost("/signout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    // The form on the home page carries an antiforgery token, so another site can't sign the user out.
    if (!await antiforgery.IsRequestValidAsync(context))
    {
        return Results.BadRequest();
    }

    // Signing out of both schemes deletes the app's cookie, then ends the Entra ID session. Entra ID sends the browser back
    // to /signout-callback-oidc, which must be a registered redirect URI, and the handler then redirects to "/".
    return Results.SignOut(
        new AuthenticationProperties { RedirectUri = "/" },
        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
});

app.MapDefaultEndpoints();

app.Run();

static IResult Page(string title, string body) => Results.Content($"""
    <!DOCTYPE html>
    <html lang="en">
    <head>
        <meta charset="utf-8">
        <title>{title}</title>
    </head>
    <body>
        <h1>{title}</h1>
        {body}
    </body>
    </html>
    """, "text/html", Encoding.UTF8);

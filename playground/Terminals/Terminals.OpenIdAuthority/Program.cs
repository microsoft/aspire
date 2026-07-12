// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var authorizationCodes = new ConcurrentDictionary<string, string?>();

app.MapGet("/.well-known/openid-configuration", (HttpRequest request) =>
{
    var issuer = $"{request.Scheme}://{request.Host}";
    return Results.Json(new
    {
        issuer,
        authorization_endpoint = $"{issuer}/authorize",
        token_endpoint = $"{issuer}/token",
        end_session_endpoint = $"{issuer}/logout",
    });
});

app.MapGet("/authorize", (HttpRequest request) =>
{
    var redirectUri = request.Query["redirect_uri"].ToString();
    var state = request.Query["state"].ToString();
    var code = Guid.NewGuid().ToString("N");
    authorizationCodes[code] = request.Query["nonce"].ToString();
    return Results.Redirect($"{redirectUri}?code={code}&state={Uri.EscapeDataString(state)}");
});

app.MapPost("/token", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    var code = form["code"].ToString();
    if (!authorizationCodes.TryRemove(code, out var nonce))
    {
        return Results.BadRequest("Unknown authorization code.");
    }

    var issuer = $"{request.Scheme}://{request.Host}";
    var now = DateTimeOffset.UtcNow;
    var idToken = CreateUnsignedToken(new Dictionary<string, object?>
    {
        ["iss"] = issuer,
        ["aud"] = "terminals-dashboard",
        ["sub"] = "248289761001",
        ["name"] = "Ada Lovelace",
        ["preferred_username"] = "ada@example.com",
        ["nonce"] = nonce,
        ["iat"] = now.ToUnixTimeSeconds(),
        ["exp"] = now.AddHours(1).ToUnixTimeSeconds(),
    });

    return Results.Json(new
    {
        token_type = "Bearer",
        scope = "openid profile",
        expires_in = 3600,
        access_token = idToken,
        id_token = idToken,
    });
}).DisableAntiforgery();

app.MapGet("/logout", (HttpRequest request) =>
{
    var redirectUri = request.Query["post_logout_redirect_uri"].ToString();
    return Results.Redirect(string.IsNullOrEmpty(redirectUri) ? "/" : redirectUri);
});

app.Run();

static string CreateUnsignedToken(Dictionary<string, object?> claims)
{
    var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "none", typ = "JWT" }));
    var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(claims));
    return $"{header}.{payload}.";
}

static string Base64UrlEncode(byte[] value)
{
    return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

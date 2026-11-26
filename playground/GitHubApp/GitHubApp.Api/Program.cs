// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// WithReference supplies the app's credentials as the GitHub:App configuration section.
var gitHubApp = builder.Configuration.GetSection("GitHub:App");
var appId = gitHubApp["AppId"] ?? throw new InvalidOperationException("GitHub:App:AppId isn't configured.");
var privateKeyPem = gitHubApp["PrivateKey"] ?? throw new InvalidOperationException("GitHub:App:PrivateKey isn't configured.");
var apiUrl = gitHubApp["ApiUrl"] ?? "https://api.github.com";

builder.Services.AddHttpClient("github", client =>
{
    client.BaseAddress = new Uri(apiUrl);
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Aspire-GitHubApp-Playground", "1.0"));
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
});

var app = builder.Build();

app.MapDefaultEndpoints();

// Calls GET /app/installations authenticated as the app itself.
// See https://docs.github.com/rest/apps/apps#list-installations-for-the-authenticated-app
app.MapGet("/", async (IHttpClientFactory httpClientFactory, CancellationToken cancellationToken) =>
{
    var client = httpClientFactory.CreateClient("github");
    using var request = new HttpRequestMessage(HttpMethod.Get, "app/installations");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateAppJwt(appId, privateKeyPem));

    using var response = await client.SendAsync(request, cancellationToken);
    var body = await response.Content.ReadAsStringAsync(cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        return Results.Problem(body, statusCode: (int)response.StatusCode, title: "GitHub rejected the app's JWT.");
    }

    using var installations = JsonDocument.Parse(body);
    return Results.Ok(installations.RootElement.EnumerateArray().Select(installation => new
    {
        Id = installation.GetProperty("id").GetInt64(),
        Account = installation.GetProperty("account").GetProperty("login").GetString(),
        TargetType = installation.GetProperty("target_type").GetString(),
    }).ToList());
});

app.Run();

// A GitHub App authenticates as itself with a short-lived RS256 JWT whose issuer is the App ID. The issued-at time is
// backdated a minute to allow for clock drift, and GitHub rejects expiry times more than ten minutes ahead.
// See https://docs.github.com/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-json-web-token-jwt-for-a-github-app
static string CreateAppJwt(string appId, string privateKeyPem)
{
    var now = DateTimeOffset.UtcNow;
    var header = JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" });
    var payload = JsonSerializer.SerializeToUtf8Bytes(new
    {
        iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
        exp = now.AddMinutes(9).ToUnixTimeSeconds(),
        iss = appId,
    });

    var signingInput = $"{Base64Url.EncodeToString(header)}.{Base64Url.EncodeToString(payload)}";

    using var rsa = RSA.Create();
    rsa.ImportFromPem(privateKeyPem);
    var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.GitHub;

/// <summary>
/// Registers a GitHub App from a manifest, using a short-lived HTTP listener on the loopback interface.
/// </summary>
/// <remarks>
/// <para>
/// The flow follows https://docs.github.com/apps/sharing-github-apps/registering-a-github-app-from-a-manifest:
/// </para>
/// <list type="number">
/// <item>The user opens the listener's start page, which posts the manifest to GitHub's "new app" page.</item>
/// <item>The user reviews the app on GitHub and creates it. GitHub redirects the browser to the manifest's
/// <c>redirect_url</c>, the listener's callback page, with a temporary <c>code</c> and the <c>state</c> sent in step 1.</item>
/// <item>The listener exchanges the code for the app's credentials by calling
/// <c>POST /app-manifests/{code}/conversions</c>, then sends the browser to the app's installation page.</item>
/// </list>
/// <para>
/// The code must be exchanged within an hour, and can be exchanged only once. The listener accepts connections only on
/// 127.0.0.1, its pages are under a random path, and the callback is accepted only with the random <c>state</c> that the
/// start page sent to GitHub, so another site or local process can't complete the registration with its own code.
/// </para>
/// </remarks>
internal sealed class GitHubAppManifestFlow : IAsyncDisposable
{
    internal const string HttpClientName = "Aspire.Hosting.GitHub";
    internal const string GitHubUrl = "https://github.com";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly string _apiUrl;
    private readonly string _pathPrefix = "/" + CreateRandomToken();
    private readonly string _state = CreateRandomToken();
    private readonly TaskCompletionSource<GitHubAppCredentials> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication? _app;
    private string? _manifestJson;
    private string? _organization;
    private int _callbackReceived;

    public GitHubAppManifestFlow(IHttpClientFactory httpClientFactory, ILogger logger, string apiUrl)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _apiUrl = apiUrl.TrimEnd('/');
    }

    /// <summary>
    /// Gets the URL of the page that starts the registration, or <see langword="null"/> before the listener starts.
    /// </summary>
    public string? StartUrl { get; private set; }

    /// <summary>
    /// Gets the URL that GitHub redirects to when the app has been created, or <see langword="null"/> before the
    /// listener starts.
    /// </summary>
    public string? RedirectUrl { get; private set; }

    /// <summary>
    /// Gets the random value that GitHub must return with the temporary code.
    /// </summary>
    internal string State => _state;

    /// <summary>
    /// Starts the listener. The manifest is created once the listener's address, which the manifest's
    /// <c>redirect_url</c> contains, is known.
    /// </summary>
    public async Task StartAsync(Func<string, CancellationToken, Task<string>> createManifestJson, string? organization, CancellationToken cancellationToken)
    {
        // An empty builder doesn't read ASPNETCORE_URLS or appsettings.json, which belong to the AppHost and not to this
        // listener, and has no logging providers, so request logs don't reach the AppHost's console.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

        var app = builder.Build();
        app.Run(HandleRequestAsync);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var port = new Uri(address).Port;
        var baseUrl = $"http://127.0.0.1:{port}{_pathPrefix}";

        RedirectUrl = $"{baseUrl}/callback";
        _organization = organization;
        _manifestJson = await createManifestJson(RedirectUrl, cancellationToken).ConfigureAwait(false);
        StartUrl = $"{baseUrl}/start";
    }

    /// <summary>
    /// Waits until the app has been created on GitHub and its credentials retrieved.
    /// </summary>
    public Task<GitHubAppCredentials> WaitForCredentialsAsync(CancellationToken cancellationToken) => _completion.Task.WaitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_app is { } app)
        {
            // Give an in-progress response, such as the redirect to the installation page, a moment to finish.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(cts.Token).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static string GetRegistrationUrl(string? organization, string state)
    {
        var path = organization is null
            ? "/settings/apps/new"
            : $"/organizations/{Uri.EscapeDataString(organization)}/settings/apps/new";

        return $"{GitHubUrl}{path}?state={Uri.EscapeDataString(state)}";
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) || _manifestJson is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (request.Path == $"{_pathPrefix}/start")
        {
            await WriteStartPageAsync(context.Response).ConfigureAwait(false);
        }
        else if (request.Path == $"{_pathPrefix}/callback")
        {
            await HandleCallbackAsync(context).ConfigureAwait(false);
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
    }

    private async Task WriteStartPageAsync(HttpResponse response)
    {
        // GitHub reads the manifest from a form field named "manifest" posted to its "new app" page. A page on this
        // listener posts the form, because a link can only make a GET request.
        var encoder = HtmlEncoder.Default;
        var action = encoder.Encode(GetRegistrationUrl(_organization, _state));
        var manifest = encoder.Encode(_manifestJson!);

        var html = $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head><meta charset="utf-8"><title>Register GitHub App</title></head>
            <body onload="document.forms[0].submit()">
            <form method="post" action="{{action}}">
            <input type="hidden" name="manifest" value="{{manifest}}">
            <p>Continuing to GitHub to register the app&hellip;</p>
            <noscript><button type="submit">Continue to GitHub</button></noscript>
            </form>
            </body>
            </html>
            """;

        await WriteHtmlAsync(response, StatusCodes.Status200OK, html).ConfigureAwait(false);
    }

    private async Task HandleCallbackAsync(HttpContext context)
    {
        // Example redirect from GitHub once the user creates the app:
        //   GET /{random}/callback?code=a180b1a3d263c81bc6441d7b990bae27d4c10679&state=abc123
        // The code is a hex string today, but only URL-safe characters are required here, and it's escaped before it's
        // put in the conversion URL.
        var code = context.Request.Query["code"].ToString();
        var state = context.Request.Query["state"].ToString();

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(_state)))
        {
            _logger.LogWarning("Ignored a registration callback whose state didn't match the registration that was started.");
            await WriteHtmlAsync(context.Response, StatusCodes.Status400BadRequest, CreateMessagePage(
                "This link doesn't match the GitHub App registration that Aspire started.")).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrEmpty(code) || !code.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            await WriteHtmlAsync(context.Response, StatusCodes.Status400BadRequest, CreateMessagePage(
                "GitHub didn't return a valid registration code.")).ConfigureAwait(false);
            return;
        }

        // A code can only be exchanged once, so a refreshed or repeated callback must not try again.
        if (Interlocked.Exchange(ref _callbackReceived, 1) == 1)
        {
            await WriteHtmlAsync(context.Response, StatusCodes.Status409Conflict, CreateMessagePage(
                "The GitHub App registration has already been completed. You can close this page.")).ConfigureAwait(false);
            return;
        }

        GitHubAppCredentials credentials;
        try
        {
            credentials = await ConvertAsync(code, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _completion.TrySetException(ex);
            await WriteHtmlAsync(context.Response, StatusCodes.Status502BadGateway, CreateMessagePage(
                "Aspire couldn't retrieve the new GitHub App's credentials. See the resource's console logs in the Aspire dashboard.")).ConfigureAwait(false);
            return;
        }

        _completion.TrySetResult(credentials);

        // A new app isn't installed on any account yet. Continuing to the installation page lets the user pick the
        // repositories it can access, which is needed before it can do anything with installation tokens.
        context.Response.Redirect(credentials.Slug is { } slug
            ? $"{GitHubUrl}/apps/{Uri.EscapeDataString(slug)}/installations/new"
            : $"{GitHubUrl}/settings/apps");
    }

    internal async Task<GitHubAppCredentials> ConvertAsync(string code, CancellationToken cancellationToken)
    {
        // The conversion endpoint needs no authentication: the single-use code is the credential.
        // See https://docs.github.com/rest/apps/apps#create-a-github-app-from-a-manifest.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiUrl}/app-manifests/{Uri.EscapeDataString(code)}/conversions");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Aspire", null));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GitHub returned {(int)response.StatusCode} ({response.ReasonPhrase}) when exchanging the registration code for the app's credentials. " +
                "The code expires after an hour and can be used only once.");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseConversion(document.RootElement);
    }

    // Parses the response of the conversion endpoint, which describes the new app along with its secrets:
    //   {
    //     "id": 1, "slug": "octoapp", "client_id": "Iv1.8a61f9b3a7aba766", "client_secret": "1726be1638095a19edd134c77bde3aa2ece1e5d8",
    //     "webhook_secret": null, "pem": "-----BEGIN RSA PRIVATE KEY-----\n...\n-----END RSA PRIVATE KEY-----\n",
    //     "owner": { "login": "octocat", "type": "User" }, ...
    //   }
    // webhook_secret is null when the manifest has no webhook.
    internal static GitHubAppCredentials ParseConversion(JsonElement root)
    {
        var appId = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var idValue)
            ? idValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : throw new InvalidOperationException("GitHub's response didn't contain the app's ID.");

        var clientId = GetString(root, "client_id") ?? throw new InvalidOperationException("GitHub's response didn't contain the app's client ID.");
        var privateKey = GetString(root, "pem") ?? throw new InvalidOperationException("GitHub's response didn't contain the app's private key.");

        string? organization = null;
        if (root.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object &&
            GetString(owner, "type") == "Organization")
        {
            organization = GetString(owner, "login");
        }

        return new GitHubAppCredentials(
            appId,
            clientId,
            privateKey,
            GetString(root, "client_secret"),
            GetString(root, "webhook_secret"),
            GetString(root, "slug"),
            organization);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;
    }

    private static string CreateMessagePage(string message)
    {
        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head><meta charset="utf-8"><title>Register GitHub App</title></head>
            <body><p>{HtmlEncoder.Default.Encode(message)}</p></body>
            </html>
            """;
    }

    private static async Task WriteHtmlAsync(HttpResponse response, int statusCode, string html)
    {
        response.StatusCode = statusCode;
        response.ContentType = "text/html; charset=utf-8";
        // The pages are only for this browser session and contain the manifest or registration state.
        response.Headers.CacheControl = "no-store";
        await response.WriteAsync(html).ConfigureAwait(false);
    }

    private static string CreateRandomToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}

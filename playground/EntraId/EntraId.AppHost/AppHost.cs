// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure;

var builder = DistributedApplication.CreateBuilder(args);

// Set to true to reference an existing registration. README.md describes the setup and cleanup for both modes.
var useExistingApplication = false;

// The front ends sign users in with Entra ID. The .NET front end also calls the API on the user's behalf, so it uses the authorization
// code flow with a client secret; the Node front end validates ID tokens returned by Entra ID without redeeming codes.
IResourceBuilder<EntraIdApplicationResource> entraWeb;
IResourceBuilder<EntraIdApplicationResource> entraApi;
if (useExistingApplication)
{
    var tenantId = builder.AddParameter("entra-tenant-id")
        .WithDescription("The **Directory (tenant) ID** shown on the Overview page of the existing app registrations.", enableMarkdown: true);
    var webClientId = builder.AddParameter("entra-web-client-id")
        .WithDescription("The **Application (client) ID** shown on the Overview page of the existing app registration shared by the .NET and Node front ends. Enable **ID tokens (used for implicit and hybrid flows)** and configure both apps' redirect URIs as described in README.md.", enableMarkdown: true);
    var apiClientId = builder.AddParameter("entra-api-client-id")
        .WithDescription("The **Application (client) ID** shown on the Overview page of the existing app registration for the weather API. Expose an `access_as_user` scope and grant it to the web registration as described in README.md.", enableMarkdown: true);

    entraWeb = builder.AddExistingEntraApplication("entra-web", tenantId, webClientId);
    entraApi = builder.AddExistingEntraApplication("entra-api", tenantId, apiClientId);
}
else
{
    entraWeb = builder.AddEntraIdApplication("entra-web")
        .WithIdTokenIssuance();
    entraApi = builder.AddEntraIdApplication("entra-api");
}

// Aspire doesn't create credentials, so supply a client secret created on the web registration (see README.md).
var webClientSecret = builder.AddParameter("entra-web-client-secret", secret: true)
    .WithDescription("A **client secret** created under **Certificates & secrets** on the web app registration. The .NET front end uses it to redeem the authorization code and call the weather API for the signed-in user.", enableMarkdown: true);
entraWeb.WithClientSecret(webClientSecret);

// The API validates tokens issued for its own registration and requires the delegated access_as_user scope.
var api = builder.AddProject<Projects.EntraId_ApiService>("api")
    .WithReference(entraApi);

// WithDownstreamApi names the API's registration and service explicitly. It adds the DownstreamApis configuration that
// Microsoft.Identity.Web reads: the API's address through service discovery, and its scope as api://{clientId}/access_as_user.
var webFrontend = builder.AddProject<Projects.EntraId_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithReference(entraWeb)
    .WithDownstreamApi(entraApi, api, scopes: ["access_as_user"])
    .WaitFor(api);

// This independent Node front end shares the web registration, but doesn't call the .NET front end.
// Keep its public HTTPS port stable so its callback matches an existing registration's redirect URI.
#pragma warning disable ASPIRECERTIFICATES001
var nodeWeb = builder.AddNodeApp("nodefrontend", "../EntraId.NodeWeb", "server.mjs")
    .WithHttpsEndpoint(port: 7252, env: "PORT")
    .WithHttpsDeveloperCertificate()
    .WithHttpsCertificateConfiguration(ctx =>
    {
        ctx.EnvironmentVariables["HTTPS_CERT_FILE"] = ctx.CertificatePath;
        ctx.EnvironmentVariables["HTTPS_CERT_KEY_FILE"] = ctx.KeyPath;
        return Task.CompletedTask;
    })
    .WithExternalHttpEndpoints()
    .WithReference(entraWeb);
#pragma warning restore ASPIRECERTIFICATES001

nodeWeb.WithEnvironment("NODE_WEB_BASE_URL", nodeWeb.GetEndpoint("https"));

// The callback paths are configured by each front end, so they are declared here; the host and port follow the endpoints.
if (!useExistingApplication)
{
    entraWeb.WithRedirectUris(webFrontend.GetEndpoint("https"), "/signin-oidc", "/signout-callback-oidc");
    entraWeb.WithRedirectUris(nodeWeb.GetEndpoint("https"), "/auth/redirect", "/auth/signout-callback");
}

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();

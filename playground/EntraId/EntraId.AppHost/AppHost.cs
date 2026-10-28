// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure;

var builder = DistributedApplication.CreateBuilder(args);

// Set to true to reference an existing registration. README.md describes the setup and cleanup for both modes.
var useExistingApplication = false;

// Both front ends validate ID tokens returned by Entra ID, without redeeming codes or using a client credential.
IResourceBuilder<EntraIdApplicationResource> entraWeb;
if (useExistingApplication)
{
    var tenantId = builder.AddParameter("entra-tenant-id")
        .WithDescription("The **Directory (tenant) ID** shown on the Overview page of the existing app registration shared by the .NET and Node front ends.", enableMarkdown: true);
    var webClientId = builder.AddParameter("entra-web-client-id")
        .WithDescription("The **Application (client) ID** shown on the Overview page of the existing app registration shared by the .NET and Node front ends. Enable **ID tokens (used for implicit and hybrid flows)** and configure both apps' redirect URIs as described in README.md. No client secret is required.", enableMarkdown: true);

    entraWeb = builder.AddExistingEntraApplication("entra-web", tenantId, webClientId);
}
else
{
    entraWeb = builder.AddEntraIdApplication("entra-web")
        .WithIdTokenIssuance()
        // Roles appear in the roles claim of the ID token for users who are assigned to them. Aspire declares the roles;
        // assign them to users in the Entra admin center (Enterprise applications > Users and groups).
        .WithAppRole(new EntraIdAppRole("User", "User", "Can sign in and view the playground."))
        .WithAppRole(new EntraIdAppRole("Admin", "Administrator", "Can administer the playground."));
}

var webFrontend = builder.AddProject<Projects.EntraId_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithReference(entraWeb);

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

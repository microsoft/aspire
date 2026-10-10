// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

// The shared web app registration must already exist in your tenant; README.md shows how to create it. The dashboard prompts for
// any of these values that isn't already in user secrets.
var tenantId = builder.AddParameter("entra-tenant-id")
    .WithDescription("The **Directory (tenant) ID** shown on the Overview page of the existing `weather-web` app registration shared by the .NET and Node front ends.", enableMarkdown: true);
var webClientId = builder.AddParameter("entra-web-client-id")
    .WithDescription("The **Application (client) ID** shown on the Overview page of the existing `weather-web` app registration shared by the .NET and Node front ends. Enable **ID tokens (used for implicit and hybrid flows)** and configure both apps' redirect URIs as described in README.md. No client secret is required.", enableMarkdown: true);

// Both front ends validate ID tokens returned by Entra ID, without redeeming codes or using a client credential.
var entraWeb = builder.AddEntraIdApplication("entra-web")
    .AsExistingApplication(tenantId, webClientId);

builder.AddProject<Projects.EntraId_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithReference(entraWeb);

// This independent Node front end shares the web registration, but doesn't call the .NET front end.
// Keep its public HTTPS port stable so its callback matches the registered redirect URI.
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

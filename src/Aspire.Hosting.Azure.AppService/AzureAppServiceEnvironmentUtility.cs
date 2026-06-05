// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Core;
using Azure.Provisioning;
using Azure.Provisioning.AppService;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Resources;
using Azure.Provisioning.Roles;

namespace Aspire.Hosting.Azure.AppService;

internal static class AzureAppServiceEnvironmentUtility
{
    internal const string ResourceName = "aspiredashboard";

    public static BicepValue<string> GetDashboardHostName(string aspireResourceName)
    {
        return BicepFunction.Take(
    BicepFunction.Interpolate($"{BicepFunction.ToLower(aspireResourceName)}-{BicepFunction.ToLower(ResourceName)}-{BicepFunction.GetUniqueString(BicepFunction.GetResourceGroup().Id)}"), 60);
    }

    public static WebSite AddDashboard(AzureResourceInfrastructure infra,
        BicepValue<string> webAppIdentityClientId,
        BicepValue<ResourceIdentifier> appServicePlanId)
    {
        // The webAppIdentityClientId is the client id of the user-assigned identity that is attached
        // to every web app in the aspire project. That identity serves two roles in the deployed
        // environment, and both are wired up from this single client id:
        //   1. ACR pull: it holds the AcrPull role on the container registry, and the dashboard
        //      website itself reuses it via SiteConfig.AcrUserManagedIdentityId to pull its image.
        //   2. Telemetry auth: it is the identity the web apps present when sending OTLP telemetry
        //      to the dashboard, so the dashboard authorizes it via ALLOWED_MANAGED_IDENTITIES.
        var prefix = infra.AspireResource.Name;
        var contributorIdentity = new UserAssignedIdentity(Infrastructure.NormalizeBicepIdentifier($"{prefix}-contributor-mi"));

        infra.Add(contributorIdentity);

        // Add Reader role assignment
        var rgRaId = BicepFunction.GetSubscriptionResourceId(
            "Microsoft.Authorization/roleDefinitions",
            "acdd72a7-3385-48ef-bd42-f606fba81ae7");
        var rgRaName = BicepFunction.CreateGuid(BicepFunction.GetResourceGroup().Id, contributorIdentity.Id, rgRaId);

        var rgRa = new RoleAssignment(Infrastructure.NormalizeBicepIdentifier($"{prefix}_ra"))
        {
            Name = rgRaName,
            PrincipalType = RoleManagementPrincipalType.ServicePrincipal,
            PrincipalId = contributorIdentity.PrincipalId,
            RoleDefinitionId = rgRaId
        };

        infra.Add(rgRa);

        var dashboard = new WebSite("dashboard")
        {
            // Use the host name as the name of the web app
            Name = GetDashboardHostName(infra.AspireResource.Name),
            AppServicePlanId = appServicePlanId,
            // Aspire dashboards are created with a new kind aspiredashboard
            Kind = "app,linux,aspiredashboard",
            SiteConfig = new SiteConfigProperties()
            {
                LinuxFxVersion = "ASPIREDASHBOARD|1.0",
                AcrUserManagedIdentityId = webAppIdentityClientId,
                UseManagedIdentityCreds = true,
                IsHttp20Enabled = true,
                Http20ProxyFlag = 1,
                // Setting instance count to 1 to ensure dashboard runs on 1 instance
                NumberOfWorkers = 1,
                // IsAlwaysOn set to true ensures the app is always running
                IsAlwaysOn = true,
                AppSettings = []
            },
            Identity = new ManagedServiceIdentity()
            {
                ManagedServiceIdentityType = ManagedServiceIdentityType.UserAssigned,
                UserAssignedIdentities = []
            }
        };

        var contributorMid = BicepFunction.Interpolate($"{contributorIdentity.Id}").Compile().ToString();
        dashboard.Identity.UserAssignedIdentities[contributorMid] = new UserAssignedIdentityDetails();

        // Security is handled by app service platform
        dashboard.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = DashboardConfigNames.DashboardFrontendAuthModeName.EnvVarName, Value = "Unsecured" });
        dashboard.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = DashboardConfigNames.DashboardOtlpAuthModeName.EnvVarName, Value = "Unsecured" });
        dashboard.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = DashboardConfigNames.DashboardOtlpSuppressUnsecuredTelemetryMessageName.EnvVarName, Value = "true" });
        dashboard.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = DashboardConfigNames.ResourceServiceClientAuthModeName.EnvVarName, Value = "Unsecured" });
        dashboard.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = DashboardConfigNames.UIDisableImportName.EnvVarName, Value = "true" });
        // Dashboard ports
        dashboard.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "WEBSITES_PORT", Value = "5000" });
        dashboard.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "HTTP20_ONLY_PORT", Value = "4317" });
        // Enable SCM preloading to ensure dashboard is always available
        dashboard.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "WEBSITE_START_SCM_WITH_PRELOAD", Value = "true" });
        // Appsettings related to managed identity for auth
        dashboard.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "AZURE_CLIENT_ID", Value = contributorIdentity.ClientId });
        dashboard.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "ALLOWED_MANAGED_IDENTITIES", Value = webAppIdentityClientId });
        // Added appsetting to identify the resources in a specific aspire environment
        dashboard.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "ASPIRE_ENVIRONMENT_NAME", Value = infra.AspireResource.Name });

        infra.Add(dashboard);

        // Outputs needed by the app service environment
        // This identity needs website contributor access on the websites for resource server to work
        infra.Add(new ProvisioningOutput("AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_ID", typeof(string))
        {
            Value = contributorIdentity.Id.ToBicepExpression()
        });

        infra.Add(new ProvisioningOutput("AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_PRINCIPAL_ID", typeof(string))
        {
            Value = contributorIdentity.PrincipalId.ToBicepExpression()
        });

        return dashboard;
    }
}

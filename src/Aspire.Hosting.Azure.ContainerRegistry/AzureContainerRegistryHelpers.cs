// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIREAZURE001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Helper methods for Azure Container Registry operations.
/// </summary>
internal static class AzureContainerRegistryHelpers
{
    public static async Task LoginToRegistryAsync(IContainerRegistry registry, PipelineStepContext context)
    {
        var acrLoginService = context.Services.GetRequiredService<IAcrLoginService>();
        var tokenCredentialProvider = context.Services.GetRequiredService<ITokenCredentialProvider>();

        // Find the AzureEnvironmentResource from the application model
        var azureEnvironment = context.Model.Resources.OfType<AzureEnvironmentResource>().FirstOrDefault() ??
            throw new InvalidOperationException("AzureEnvironmentResource must be present in the application model.");
        var registryName = await registry.Name.GetValueAsync(context.CancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException("Failed to retrieve container registry information.");

        var registryEndpoint = await registry.Endpoint.GetValueAsync(context.CancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException("Failed to retrieve container registry endpoint.");

        var loginTask = await context.ReportingStep.CreateTaskAsync(
            new MarkdownString($"Logging in to **{registryName}**"),
            context.CancellationToken).ConfigureAwait(false);
        await using (loginTask.ConfigureAwait(false))
        {
            try
            {
                // Get tenant ID from the provisioning context (always available from subscription)
                var provisioningContext = await azureEnvironment.ProvisioningContextTask.Task.ConfigureAwait(false);
                var tenantId = provisioningContext.Tenant.TenantId?.ToString()
                    ?? throw new InvalidOperationException("Tenant ID is required for ACR authentication but was not available in provisioning context.");

                // Use the ACR login service to perform authentication
                await acrLoginService.LoginAsync(
                    registryEndpoint,
                    tenantId,
                    tokenCredentialProvider.TokenCredential,
                    context.CancellationToken).ConfigureAwait(false);

                await loginTask.CompleteAsync(
                    new MarkdownString($"Successfully logged in to **{registryEndpoint}**"),
                    CompletionState.Completed,
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await loginTask.FailAsync(
                    new MarkdownString($"Login to ACR **{registryEndpoint}** failed: {ex.Message}"),
                    context.CancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }
}

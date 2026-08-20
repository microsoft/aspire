// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Model;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Coordinates preparation and creation of executable DCP resources.
/// </summary>
internal sealed class ExecutableCreator(
    ExecutableResourcePreparer resourcePreparer,
    ExecutableConfigurationResolver configurationResolver,
    ExecutableLaunchPlanResolver launchPlanResolver,
    DcpExecutableRenderer renderer) : IObjectCreator<Executable, EmptyCreationContext>
{
    private readonly ExecutableResourcePreparer _resourcePreparer = resourcePreparer;
    private readonly ExecutableConfigurationResolver _configurationResolver = configurationResolver;
    private readonly ExecutableLaunchPlanResolver _launchPlanResolver = launchPlanResolver;
    private readonly DcpExecutableRenderer _renderer = renderer;

    public IEnumerable<RenderedModelResource<Executable>> PrepareObjects(CancellationToken cancellationToken) =>
        _resourcePreparer.PrepareObjects(cancellationToken);

    public bool IsReadyToCreate(
        RenderedModelResource<Executable> resource,
        EmptyCreationContext context) =>
        !DcpModelUtilities.ShouldDeferCreateForExplicitStart(
            resource.ModelResource,
            resource.DcpResource.Spec.Start);

    public async Task CreateObjectAsync(
        RenderedModelResource<Executable> renderedResource,
        EmptyCreationContext context,
        ILogger resourceLogger,
        IDcpObjectFactory factory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var configuration = await _configurationResolver
            .ResolveAsync(renderedResource, resourceLogger, cancellationToken)
            .ConfigureAwait(false);
        if (configuration.Configuration.Exception is not null)
        {
            throw new FailedToApplyEnvironmentException(
                $"Failed to apply configuration to executable {renderedResource.ModelResource.Name}",
                configuration.Configuration.Exception);
        }

        ExecutableLaunchPlan plan;
        try
        {
            plan = await _launchPlanResolver
                .ResolveAsync(
                    renderedResource.ModelResource,
                    configuration.Configuration,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failureMessage =
                $"Failed to apply launch configuration for resource '{renderedResource.ModelResource.Name}'. " +
                "Aspire does not retry launch configuration failures using DCP process fallback.";
            // DcpExecutor avoids duplicating FailedToApplyEnvironmentException logs, so record the underlying
            // launch producer failure on the resource logger before surfacing the actionable error.
            resourceLogger.LogError(ex, "{Message}", failureMessage);
            throw new FailedToApplyEnvironmentException(failureMessage, ex);
        }

        _renderer.Render(renderedResource, plan, configuration.PemCertificates);

        await factory
            .CreateDcpObjectsAsync([renderedResource.DcpResource], cancellationToken)
            .ConfigureAwait(false);
    }
}

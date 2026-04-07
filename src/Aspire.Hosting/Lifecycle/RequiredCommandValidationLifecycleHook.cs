// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMMAND001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;

namespace Aspire.Hosting.Lifecycle;

/// <summary>
/// An eventing subscriber that validates required commands are installed before resources start.
/// </summary>
/// <remarks>
/// This subscriber processes <see cref="RequiredCommandAnnotation"/> on resources and delegates
/// validation to <see cref="IRequiredCommandValidator"/>.
/// </remarks>
internal sealed class RequiredCommandValidationLifecycleHook(
    IRequiredCommandValidator validator) : IDistributedApplicationEventingSubscriber
{
    private readonly IRequiredCommandValidator _validator = validator ?? throw new ArgumentNullException(nameof(validator));

    /// <inheritdoc />
    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        // Subscribe to BeforeResourceStartedEvent to validate commands before each resource starts
        eventing.Subscribe<BeforeResourceStartedEvent>(ValidateRequiredCommandsAsync);
        return Task.CompletedTask;
    }

    private async Task ValidateRequiredCommandsAsync(BeforeResourceStartedEvent @event, CancellationToken cancellationToken)
    {
        var resource = @event.Resource;

        // Get all RequiredCommandAnnotation instances on the resource
        var requiredCommands = resource.Annotations.OfType<RequiredCommandAnnotation>().ToList();

        if (requiredCommands.Count == 0)
        {
            return;
        }

        foreach (var annotation in requiredCommands)
        {
            await _validator.ValidateAsync(resource, annotation, cancellationToken).ConfigureAwait(false);
        }
    }
}

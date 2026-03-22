// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.OpenAI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding OpenAI Model resources to the application model.
/// </summary>
public static class OpenAIExtensions
{
    /// <summary>
    /// Adds an OpenAI parent resource that can host multiple models.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the OpenAI resource.</param>
    /// <returns>The OpenAI resource builder.</returns>
    [AspireExport("addOpenAI", Description = "Adds an OpenAI resource to the distributed application model.")]
    public static IResourceBuilder<OpenAIResource> AddOpenAI(this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var defaultApiKeyParameter = builder.AddParameter($"{name}-openai-apikey", () =>
        {
            var configKey = $"Parameters:{name}-openai-apikey";
            var value = builder.Configuration.GetValueWithNormalizedKey(configKey);
            
            return value ??
                Environment.GetEnvironmentVariable("OPENAI_API_KEY") ??
                throw new MissingParameterValueException($"OpenAI API key parameter '{name}-openai-apikey' is missing and OPENAI_API_KEY environment variable is not set.");
        },
            secret: true);
        defaultApiKeyParameter.Resource.Description = """
            The API key used to authenticate requests to the OpenAI API.
            You can obtain an API key from the [OpenAI API Keys page](https://platform.openai.com/api-keys).
            """;
        defaultApiKeyParameter.Resource.EnableDescriptionMarkdown = true;

        var resource = new OpenAIResource(name, defaultApiKeyParameter.Resource);

        defaultApiKeyParameter.WithParentRelationship(resource);

        // Register the health check
        var healthCheckKey = $"{name}_check";

        // Ensure IHttpClientFactory is available by registering HTTP client services
        builder.Services.AddHttpClient();

        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            name: healthCheckKey,
            factory: sp =>
            {
                var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
                return new OpenAIHealthCheck(httpFactory, resource, "OpenAIHealthCheck", TimeSpan.FromSeconds(5));
            },
            failureStatus: HealthStatus.Unhealthy,
            tags: ["openai", "healthcheck"]));

        return builder.AddResource(resource)
            .WithInitialState(new()
            {
                ResourceType = "OpenAI",
                CreationTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.Waiting,
                Properties =
                [
                    new(CustomResourceKnownProperties.Source, "OpenAI")
                ]
            })
            .OnInitializeResource(async (r, evt, ct) =>
            {
                // Connection string resolution is dependent on parameters being resolved
                // We use this to wait for the parameters to be resolved before we can compute the connection string.
                var cs = await r.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);

                // Publish the update with the connection string value and the state as running.
                // This will allow health checks to start running.
                await evt.Notifications.PublishUpdateAsync(r, s => s with
                {
                    State = KnownResourceStates.Running,
                    Properties = [.. s.Properties, new(CustomResourceKnownProperties.ConnectionString, cs) { IsSensitive = true }]
                }).ConfigureAwait(false);

                // Publish the connection string available event for other resources that may depend on this resource.
                await evt.Eventing.PublishAsync(new ConnectionStringAvailableEvent(r, evt.Services), ct)
                                  .ConfigureAwait(false);
            })
            .WithHealthCheck(healthCheckKey);
    }

    /// <summary>
    /// Adds an OpenAI Model child to the provided OpenAI resource.
    /// </summary>
    /// <param name="builder">The OpenAI resource builder.</param>
    /// <param name="name">The name of the model resource. This name is used as the connection string name.</param>
    /// <param name="model">The model identifier, e.g., "gpt-4o-mini".</param>
    /// <returns>The model resource builder.</returns>
    [AspireExport("addModel", Description = "Adds an OpenAI model resource.")]
    public static IResourceBuilder<OpenAIModelResource> AddModel(this IResourceBuilder<OpenAIResource> builder, [ResourceName] string name, string model)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(model);

        var resource = new OpenAIModelResource(name, model, builder.Resource);

        return builder.ApplicationBuilder.AddResource(resource)
            .WithInitialState(new()
            {
                ResourceType = "OpenAI Model",
                CreationTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.Waiting,
                Properties =
                [
                    new(CustomResourceKnownProperties.Source, "OpenAI Models")
                ]
            })
            .WithParentRelationship(builder)
            .OnInitializeResource(async (r, evt, ct) =>
            {
                var cs = await r.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);

                await evt.Notifications.PublishUpdateAsync(r, s => s with
                {
                    State = KnownResourceStates.Running,
                    Properties = [.. s.Properties, new(CustomResourceKnownProperties.ConnectionString, cs) { IsSensitive = true }]
                }).ConfigureAwait(false);

                await evt.Eventing.PublishAsync(new ConnectionStringAvailableEvent(r, evt.Services), ct)
                                  .ConfigureAwait(false);
            });
    }

    /// <summary>
    /// Sets a custom OpenAI-compatible service endpoint URI on the parent resource.
    /// </summary>
    /// <param name="builder">The OpenAI parent resource builder.</param>
    /// <param name="endpoint">The endpoint URI, e.g., https://mygateway.example.com/v1.</param>
    [AspireExport("withEndpoint", Description = "Configures the endpoint URI for the OpenAI resource.")]
    public static IResourceBuilder<OpenAIResource> WithEndpoint(this IResourceBuilder<OpenAIResource> builder, string endpoint)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(endpoint);

        builder.Resource.Endpoint = endpoint;
        return builder;
    }

    /// <summary>
    /// Configures the API key for the OpenAI parent resource from a parameter.
    /// </summary>
    [AspireExport("withApiKey", Description = "Configures the API key for the OpenAI resource.")]
    public static IResourceBuilder<OpenAIResource> WithApiKey(this IResourceBuilder<OpenAIResource> builder, IResourceBuilder<ParameterResource> apiKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(apiKey);

        if (!apiKey.Resource.Secret)
        {
            throw new ArgumentException("The API key parameter must be marked as secret. Use AddParameter with secret: true when creating the parameter.", nameof(apiKey));
        }

        // Remove the existing parameter if it's the default one
        if (builder.Resource.DefaultKeyParameter == builder.Resource.Key)
        {
            builder.ApplicationBuilder.Resources.Remove(builder.Resource.Key);
        }

        builder.Resource.Key = apiKey.Resource;

        return builder;
    }

    /// <summary>
    /// Adds a health check to the OpenAI Model resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// <para>
    /// This method adds a health check that verifies the OpenAI endpoint is accessible,
    /// the API key is valid, and the specified model is available. The health check will:
    /// </para>
    /// <list type="bullet">
    /// <item>Return <see cref="HealthStatus.Healthy"/> when the endpoint returns HTTP 200</item>
    /// <item>Return <see cref="HealthStatus.Unhealthy"/> with details when the API key is invalid (HTTP 401)</item>
    /// <item>Return <see cref="HealthStatus.Unhealthy"/> with error details when the model is unknown (HTTP 404)</item>
    /// </list>
    /// <para>
    /// Because health checks are included in the rate limit of the OpenAI API,
    /// it is recommended to use this health check sparingly, such as when you are having issues understanding the reason
    /// the model is not working as expected. Furthermore, the health check will run a single time per application instance.
    /// </para>
    /// </remarks>
    [AspireExport("withHealthCheck", Description = "Adds a health check for the OpenAI model resource.")]
    public static IResourceBuilder<OpenAIModelResource> WithHealthCheck(this IResourceBuilder<OpenAIModelResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var healthCheckKey = $"{builder.Resource.Name}_check";
        OpenAIModelHealthCheck? healthCheck = null;

        // Ensure IHttpClientFactory is available by registering HTTP client services
        builder.ApplicationBuilder.Services.AddHttpClient();

        // Register the health check
        builder.ApplicationBuilder.Services.AddHealthChecks()
            .Add(new HealthCheckRegistration(
                healthCheckKey,
                sp =>
                {
                    // Cache the health check instance so we can reuse its result in order to avoid multiple API calls
                    // that would exhaust the rate limit.

                    if (healthCheck is not null)
                    {
                        return healthCheck;
                    }

                    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("OpenAIHealthCheck");

                    var resource = builder.Resource;

                    return healthCheck = new OpenAIModelHealthCheck(httpClient, async () => await resource.ConnectionStringExpression.GetValueAsync(default).ConfigureAwait(false));
                },
                failureStatus: default,
                tags: default,
                timeout: default));

        builder.WithHealthCheck(healthCheckKey);

        return builder;
    }
}

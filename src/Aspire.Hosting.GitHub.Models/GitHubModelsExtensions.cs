// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.GitHub;
using Aspire.Hosting.GitHub.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding GitHub Models resources to the application model.
/// </summary>
public static class GitHubModelsExtensions
{
    /// <summary>
    /// Adds a GitHub Model resource to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="model">The model name to use with GitHub Models.</param>
    /// <param name="organization">The organization login associated with the organization to which the request is to be attributed.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExportIgnore(Reason = "The polyglot overload uses the GitHubModelName enum instead. See the internal AddGitHubModel(GitHubModelName) overload.")]
    public static IResourceBuilder<GitHubModelResource> AddGitHubModel(this IDistributedApplicationBuilder builder, [ResourceName] string name, string model, IResourceBuilder<ParameterResource>? organization = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(model);

        var defaultApiKeyParameter = builder.AddParameter($"{name}-gh-apikey", () =>
        {
            var configKey = $"Parameters:{name}-gh-apikey";
            var value = builder.Configuration.GetValueWithNormalizedKey(configKey);
            
            return value ??
                Environment.GetEnvironmentVariable("GITHUB_TOKEN") ??
                throw new MissingParameterValueException($"GitHub API key parameter '{name}-gh-apikey' is missing and GITHUB_TOKEN environment variable is not set.");
        },
            secret: true);
        defaultApiKeyParameter.Resource.Description = """
            The API key used to authenticate requests to the GitHub Models API.
            A [fine-grained personal access token](https://github.com/settings/tokens) with the `models: read` scope is recommended.
            See [GitHub documentation for more details](https://docs.github.com/en/rest/models/inference).
            """;
        defaultApiKeyParameter.Resource.EnableDescriptionMarkdown = true;
        var resource = new GitHubModelResource(name, model, organization?.Resource, defaultApiKeyParameter.Resource);

        defaultApiKeyParameter.WithParentRelationship(resource);

        return builder.AddResource(resource)
            .WithInitialState(new()
            {
                ResourceType = "GitHubModel",
                CreationTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.Waiting,
                Properties =
                [
                    new(CustomResourceKnownProperties.Source, "GitHub Models")
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
            });
    }

    /// <summary>
    /// Adds a GitHub Model resource to the application model using a <see cref="GitHubModel"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="model">The model descriptor, using the <see cref="GitHubModel"/> class like so: <code lang="csharp">builder.AddGitHubModel(name: "chat", model: GitHubModel.Microsoft.Phi3MediumInstruct)</code></param>
    /// <param name="organization">The organization login associated with the organization to which the request is to be attributed.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>This overload is not available in polyglot app hosts. Use the string-based overload instead.</para>
    /// <example>
    /// Create a GitHub Model resource for the Microsoft Phi-3 Medium Instruct model:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var githubModel = builder.AddGitHubModel("chat", GitHubModel.Microsoft.Phi3MediumInstruct);
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExportIgnore(Reason = "GitHubModel is a .NET-specific descriptor type not compatible with ATS. Use the GitHubModelName enum-based overload instead.")]
    public static IResourceBuilder<GitHubModelResource> AddGitHubModel(this IDistributedApplicationBuilder builder, [ResourceName] string name, GitHubModel model, IResourceBuilder<ParameterResource>? organization = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        return AddGitHubModel(builder, name, model.Id, organization);
    }

    /// <summary>
    /// Adds a GitHub Model resource to the application model using a known <see cref="GitHubModelName"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="model">The known model name from the <see cref="GitHubModelName"/> enumeration.</param>
    /// <param name="organization">The organization login associated with the organization to which the request is to be attributed.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("addGitHubModel", Description = "Adds a GitHub Model resource to the distributed application model.")]
    internal static IResourceBuilder<GitHubModelResource> AddGitHubModel(this IDistributedApplicationBuilder builder, [ResourceName] string name, GitHubModelName model, IResourceBuilder<ParameterResource>? organization = null)
    {
        return AddGitHubModel(builder, name, GitHubModel.GetModelId(model), organization);
    }

    /// <summary>
    /// Adds a GitHub Model resource to the application model using a model identifier string.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="modelId">The model identifier string, for example <c>"openai/gpt-4o"</c>.</param>
    /// <param name="organization">The organization login associated with the organization to which the request is to be attributed.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("addGitHubModelById", Description = "Adds a GitHub Model resource using a model identifier string.")]
    internal static IResourceBuilder<GitHubModelResource> AddGitHubModelById(this IDistributedApplicationBuilder builder, [ResourceName] string name, string modelId, IResourceBuilder<ParameterResource>? organization = null)
    {
        return AddGitHubModel(builder, name, modelId, organization);
    }

    /// <summary>
    /// Configures the API key for the GitHub Model resource from a parameter.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="apiKey">The API key parameter.</param>
    /// <returns>The resource builder.</returns>
    /// <exception cref="ArgumentException">Thrown when the provided parameter is not marked as secret.</exception>
    [AspireExport("withApiKey", Description = "Configures the API key for the GitHub Model resource.")]
    public static IResourceBuilder<GitHubModelResource> WithApiKey(this IResourceBuilder<GitHubModelResource> builder, IResourceBuilder<ParameterResource> apiKey)
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
    /// Adds a health check to the GitHub Model resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// <para>
    /// This method adds a health check that verifies the GitHub Models endpoint is accessible,
    /// the API key is valid, and the specified model is available. The health check will:
    /// </para>
    /// <list type="bullet">
    /// <item>Return <see cref="HealthStatus.Healthy"/> when the endpoint returns HTTP 200</item>
    /// <item>Return <see cref="HealthStatus.Unhealthy"/> with details when the API key is invalid (HTTP 401)</item>
    /// <item>Return <see cref="HealthStatus.Unhealthy"/> with error details when the model is unknown (HTTP 404)</item>
    /// </list>
    /// <para>
    /// Because health checks are included in the rate limit of the GitHub Models API,
    /// it is recommended to use this health check sparingly, such as when you are having issues understanding the reason
    /// the model is not working as expected. Furthermore, the health check will run a single time per application instance.
    /// </para>
    /// </remarks>
    [AspireExport("enableHealthCheck", Description = "Adds a health check for the GitHub Model resource.")]
    public static IResourceBuilder<GitHubModelResource> WithHealthCheck(this IResourceBuilder<GitHubModelResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var healthCheckKey = $"{builder.Resource.Name}_check";
        GitHubModelsHealthCheck? healthCheck = null;

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

                    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("GitHubModelsHealthCheck");

                    var resource = builder.Resource;

                    return healthCheck = new GitHubModelsHealthCheck(httpClient, async () => await resource.ConnectionStringExpression.GetValueAsync(default).ConfigureAwait(false));
                },
                failureStatus: default,
                tags: default,
                timeout: default));

        builder.WithHealthCheck(healthCheckKey);

        return builder;
    }
}

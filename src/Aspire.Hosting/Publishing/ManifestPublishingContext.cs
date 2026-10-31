// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPROJECTS001

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Hashing;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Contextual information used for manifest publishing during this execution of the AppHost.
/// </summary>
/// <param name="executionContext">Global contextual information for this invocation of the AppHost.</param>
/// <param name="manifestPath">Manifest path passed in for this invocation of the AppHost.</param>
/// <param name="writer">JSON writer used to writing the manifest.</param>
/// <param name="cancellationToken">Cancellation token for this operation.</param>
public sealed class ManifestPublishingContext(DistributedApplicationExecutionContext executionContext, string manifestPath, Utf8JsonWriter writer, CancellationToken cancellationToken = default)
{
    /// <summary>
    /// Gets execution context for this invocation of the AppHost.
    /// </summary>
    public DistributedApplicationExecutionContext ExecutionContext { get; } = executionContext;

    /// <summary>
    /// Gets manifest path specified for this invocation of the AppHost.
    /// </summary>
    public string ManifestPath { get; } = manifestPath;

    /// <summary>
    /// Gets JSON writer for writing manifest entries.
    /// </summary>
    public Utf8JsonWriter Writer { get; } = writer;

    /// <summary>
    /// Gets cancellation token for this operation.
    /// </summary>
    public CancellationToken CancellationToken { get; } = cancellationToken;

    private readonly Dictionary<string, IResource> _referencedResources = [];

    private readonly HashSet<object?> _currentDependencySet = [];

    private readonly Dictionary<ParameterResource, Dictionary<string, string>> _formattedParameters = [];

    private readonly Dictionary<string, ManifestValue> _manifestValues = new(StringComparers.ResourceName);

    private readonly Dictionary<(string Expression, string Format), string> _formattedExpressions = [];

    private readonly Dictionary<(string Expression, string Format), string> _formattedReferences = [];

    private readonly Dictionary<string, FormattedManifestValue> _formattedValues = new(StringComparer.Ordinal);

    private readonly Dictionary<IValueProvider, string> _concreteManifestReferences = new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<string, string> _endpointPropertyInputs = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _resolvedManifestValues = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _loweredManifestValues = new(StringComparer.Ordinal);

    private readonly HashSet<string> _parameterNames = new(StringComparers.ResourceName);

    private readonly HashSet<object> _loweringExpressions = new(ReferenceEqualityComparer.Instance);

    private readonly Queue<string> _pendingManifestValues = [];

    private readonly HashSet<string> _writtenFormattedParameters = new(StringComparers.ResourceName);

    private readonly HashSet<string> _manifestResourceNames = new(StringComparers.ResourceName);

    private readonly IPortAllocator _portAllocator = new PortAllocator();

    /// <summary>
    /// Generates a relative path based on the location of the manifest path.
    /// </summary>
    /// <param name="path">A path to a file.</param>
    /// <returns>The specified path as a relative path to the manifest.</returns>
    /// <exception cref="DistributedApplicationException">Throws when could not get the directory directory name from the output path.</exception>
    [return: NotNullIfNotNull(nameof(path))]
    public string? GetManifestRelativePath(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var fullyQualifiedManifestPath = Path.GetFullPath(ManifestPath);
        var manifestDirectory = Path.GetDirectoryName(fullyQualifiedManifestPath) ?? throw new DistributedApplicationException("Could not get directory name of output path");

        var normalizedPath = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var relativePath = Path.GetRelativePath(manifestDirectory, normalizedPath);

        return relativePath.Replace('\\', '/');
    }

    internal async Task WriteModel(DistributedApplicationModel model, CancellationToken cancellationToken)
    {
        _formattedParameters.Clear();
        _manifestValues.Clear();
        _formattedExpressions.Clear();
        _formattedReferences.Clear();
        _formattedValues.Clear();
        _concreteManifestReferences.Clear();
        _endpointPropertyInputs.Clear();
        _resolvedManifestValues.Clear();
        _loweredManifestValues.Clear();
        _parameterNames.Clear();
        _loweringExpressions.Clear();
        _pendingManifestValues.Clear();
        _writtenFormattedParameters.Clear();
        _manifestResourceNames.Clear();

        foreach (var resource in model.Resources)
        {
            _manifestResourceNames.Add(resource.Name);
        }

        Writer.WriteStartObject();
        Writer.WriteStartObject("resources");

        foreach (var resource in model.Resources)
        {
            await WriteResourceAsync(resource).ConfigureAwait(false);
        }

        await WriteReferencedResources(model).ConfigureAwait(false);

        await WriteManifestValuesAsync().ConfigureAwait(false);

        WriteEndpointPropertyInputs();
        WriteRemainingFormattedParameters();
        WriteFormattedExpressions();

        Writer.WriteEndObject();
        Writer.WriteEndObject();

        await Writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        _formattedParameters.Clear();
        _manifestValues.Clear();
        _formattedExpressions.Clear();
        _formattedReferences.Clear();
        _formattedValues.Clear();
        _concreteManifestReferences.Clear();
        _endpointPropertyInputs.Clear();
        _resolvedManifestValues.Clear();
        _loweredManifestValues.Clear();
        _parameterNames.Clear();
        _loweringExpressions.Clear();
        _pendingManifestValues.Clear();
        _writtenFormattedParameters.Clear();
        _manifestResourceNames.Clear();
    }

    internal async Task WriteResourceAsync(IResource resource)
    {
        // First see if the resource has a callback annotation with overrides the behavior for rendering
        // out the JSON. If so use that callback, otherwise use the fallback logic that we have.
        if (resource.TryGetLastAnnotation<ManifestPublishingCallbackAnnotation>(out var manifestPublishingCallbackAnnotation))
        {
            if (manifestPublishingCallbackAnnotation.Callback != null)
            {
                await WriteResourceObjectAsync(resource, () => manifestPublishingCallbackAnnotation.Callback(this)).ConfigureAwait(false);
            }
        }
        else if (resource is ContainerResource container)
        {
            await WriteResourceObjectAsync(container, () => WriteContainerAsync(container)).ConfigureAwait(false);
        }
        else if (resource.SupportsDotnetProgramPublishing())
        {
            await WriteResourceObjectAsync(resource, () => WriteProjectAsync(resource)).ConfigureAwait(false);
        }
        else if (resource is IDotnetProgramResource)
        {
            await WriteResourceObjectAsync(resource, () => throw new DistributedApplicationException(
                $"The .NET program resource '{resource.Name}' is not configured for publishing. " +
                $"Create it with a supported builder API or call WithDotnetProgramPublishing() after attaching project metadata.")).ConfigureAwait(false);
        }
        else if (resource is ExecutableResource executable)
        {
            await WriteResourceObjectAsync(executable, () => WriteExecutableAsync(executable)).ConfigureAwait(false);
        }
        else if (resource is ParameterResource parameter)
        {
            await WriteResourceObjectAsync(parameter, () => WriteParameterAsync(parameter)).ConfigureAwait(false);
        }
        else if (resource is IResourceWithConnectionString resourceWithConnectionString)
        {
            await WriteResourceObjectAsync(resource, () => WriteConnectionStringAsync(resourceWithConnectionString)).ConfigureAwait(false);
        }
        else
        {
            await WriteResourceObjectAsync(resource, WriteErrorAsync).ConfigureAwait(false);
        }

        async Task WriteResourceObjectAsync<T>(T resource, Func<Task> action) where T : IResource
        {
            Writer.WriteStartObject(resource.Name);
            await action().ConfigureAwait(false);
            Writer.WriteEndObject();

            if (resource is ParameterResource parameterResource)
            {
                WriteFormattedParameterResources(parameterResource);
            }
        }
    }

    private Task WriteErrorAsync()
    {
        Writer.WriteString("error", "This resource does not support generation in the manifest.");
        return Task.CompletedTask;
    }

    private Task WriteConnectionStringAsync(IResourceWithConnectionString resource)
    {
        // Write connection strings as value.v0
        Writer.WriteString("type", "value.v0");
        WriteConnectionString(resource);

        return Task.CompletedTask;
    }

    private async Task WriteProjectAsync(IResource project)
    {
        if (!project.TryGetProjectMetadata(out var metadata))
        {
            throw new DistributedApplicationException($"Project metadata was not found for resource '{project.Name}'.");
        }

        var relativePathToProjectFile = GetManifestRelativePath(metadata.ProjectPath);

        var deploymentTarget = project.GetDeploymentTargetAnnotation();
        if (deploymentTarget is not null)
        {
            Writer.WriteString("type", "project.v1");
        }
        else
        {
            Writer.WriteString("type", "project.v0");
        }

        Writer.WriteString("path", relativePathToProjectFile);

        if (deploymentTarget is not null)
        {
            await WriteDeploymentTarget(deploymentTarget).ConfigureAwait(false);
        }

        WriteContainerFilesDestination(project);

        await WriteCommandLineArgumentsAsync(project).ConfigureAwait(false);

        await WriteEnvironmentVariablesAsync(project).ConfigureAwait(false);

        WriteBindings(project);
    }

    private async Task WriteDeploymentTarget(DeploymentTargetAnnotation deploymentTarget)
    {
        if (deploymentTarget.DeploymentTarget.TryGetLastAnnotation<ManifestPublishingCallbackAnnotation>(out var manifestPublishingCallbackAnnotation) &&
            manifestPublishingCallbackAnnotation.Callback is not null)
        {
            Writer.WriteStartObject("deployment");
            await manifestPublishingCallbackAnnotation.Callback(this).ConfigureAwait(false);
            Writer.WriteEndObject();
        }
    }

    private void WriteContainerFilesDestination(IResource resource)
    {
        if (!resource.TryGetAnnotationsOfType<ContainerFilesDestinationAnnotation>(out var containerFilesAnnotations))
        {
            return;
        }

        Writer.WriteStartObject("containerFiles");

        foreach (var containerFileDestination in containerFilesAnnotations)
        {
            var source = containerFileDestination.Source;

            Writer.WriteStartObject(source.Name);
            Writer.WriteString("destination", containerFileDestination.DestinationPath);

            // Get source paths from the source resource
            if (source.TryGetAnnotationsOfType<ContainerFilesSourceAnnotation>(out var sourceAnnotations))
            {
                Writer.WriteStartArray("sources");
                foreach (var sourceAnnotation in sourceAnnotations)
                {
                    Writer.WriteStringValue(sourceAnnotation.SourcePath);
                }
                Writer.WriteEndArray();
            }

            Writer.WriteEndObject();
        }

        Writer.WriteEndObject();
    }

    private async Task WriteExecutableAsync(ExecutableResource executable)
    {
        Writer.WriteString("type", "executable.v0");

        // Write the connection string if it exists.
        WriteConnectionString(executable);

        var relativePathToProjectFile = GetManifestRelativePath(executable.WorkingDirectory);

        Writer.WriteString("workingDirectory", relativePathToProjectFile);

        Writer.WriteString("command", executable.Command);

        WriteContainerFilesDestination(executable);

        await WriteCommandLineArgumentsAsync(executable).ConfigureAwait(false);

        await WriteEnvironmentVariablesAsync(executable).ConfigureAwait(false);
        WriteBindings(executable);
    }

    internal Task WriteParameterAsync(ParameterResource parameter)
    {
        _parameterNames.Add(parameter.Name);
        if (!parameter.Required)
        {
            throw new DistributedApplicationException(
                $"Optional parameter resource '{parameter.Name}' cannot be published because the manifest schema does not support optional parameter inputs.");
        }

        Writer.WriteString("type", "parameter.v0");

        if (parameter.IsConnectionString)
        {
            Writer.WriteString("connectionString", parameter.ValueExpression);
        }

        Writer.WriteString("value", $"{{{parameter.Name}.inputs.value}}");

        Writer.WriteStartObject("inputs");
        Writer.WriteStartObject("value");

        // https://github.com/Azure/azure-dev/issues/3487 tracks being able to remove this. All inputs are strings.
        Writer.WriteString("type", "string");

        if (parameter.Secret)
        {
            Writer.WriteBoolean("secret", true);
        }

        if (parameter.Default is not null)
        {
            Writer.WriteStartObject("default");
            parameter.Default.WriteToManifest(this);
            Writer.WriteEndObject();
        }

        Writer.WriteEndObject();
        Writer.WriteEndObject();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes JSON elements to the manifest which represent a container resource.
    /// </summary>
    /// <param name="container">The container resource to written to the manifest.</param>
    /// <exception cref="DistributedApplicationException">Thrown if the container resource does not contain a <see cref="ContainerImageAnnotation"/>.</exception>
    public async Task WriteContainerAsync(ContainerResource container)
    {
        var deploymentTarget = container.GetDeploymentTargetAnnotation();

        if (container.Annotations.OfType<DockerfileBuildAnnotation>().Any())
        {
            Writer.WriteString("type", "container.v1");
            WriteConnectionString(container);
            await WriteBuildContextAsync(container).ConfigureAwait(false);
        }
        else
        {
            if (!container.TryGetContainerImageName(out var image))
            {
                throw new DistributedApplicationException($"Could not get the container image name for resource '{container.Name}'.");
            }

            if (deploymentTarget is not null)
            {
                Writer.WriteString("type", "container.v1");
            }
            else
            {
                Writer.WriteString("type", "container.v0");
            }

            WriteConnectionString(container);
            Writer.WriteString("image", image);
        }

        if (deploymentTarget is not null)
        {
            await WriteDeploymentTarget(deploymentTarget).ConfigureAwait(false);
        }

        if (container.Entrypoint is not null)
        {
            Writer.WriteString("entrypoint", container.Entrypoint);
        }

        // Write args if they are present
        await WriteCommandLineArgumentsAsync(container).ConfigureAwait(false);

        // Write container files destination if present
        WriteContainerFilesDestination(container);

        // Write volume & bind mount details
        WriteContainerMounts(container);

        await WriteEnvironmentVariablesAsync(container).ConfigureAwait(false);
        WriteBindings(container);
    }

    private async Task WriteBuildContextAsync(ContainerResource container)
    {
        if (container.TryGetAnnotationsOfType<DockerfileBuildAnnotation>(out var annotations) && annotations.Single() is { } annotation)
        {
            var dockerfilePath = annotation.DockerfilePath;

            if (annotation.DockerfileFactory is not null)
            {
                // Copy to a resource-specific path in the manifest output directory for publishing
                var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(ManifestPath))!;
                var resourceDockerfilePath = Path.Combine(manifestDirectory, $"{container.Name}.Dockerfile");
                var dockerfileContext = new DockerfileFactoryContext
                {
                    Services = ExecutionContext.Services,
                    Resource = container,
                    CancellationToken = CancellationToken
                };
                await annotation.EmitDockerfileArtifactsAsync(dockerfileContext, resourceDockerfilePath).ConfigureAwait(false);

                // Update the dockerfile path to use the generated file for the manifest
                dockerfilePath = resourceDockerfilePath;
            }

            Writer.WriteStartObject("build");
            Writer.WriteString("context", GetManifestRelativePath(annotation.ContextPath));
            Writer.WriteString("dockerfile", GetManifestRelativePath(dockerfilePath));

            if (annotation.Stage is { } stage)
            {
                Writer.WriteString("stage", stage);
            }

            if (!annotation.HasEntrypoint)
            {
                Writer.WriteBoolean("buildOnly", true);
            }

            if (annotation.BuildArguments.Count > 0)
            {
                Writer.WriteStartObject("args");

                foreach (var (key, value) in annotation.BuildArguments)
                {
                    var valueString = value switch
                    {
                        string stringValue => stringValue,
                        IManifestExpressionProvider manifestExpression => GetManifestExpression(manifestExpression, manifestExpression.ValueExpression),
                        bool boolValue => boolValue ? "true" : "false",
                        null => null, // null means let docker build pull from env var.
                        _ => value.ToString()
                    };

                    TryAddDependentResources(value);

                    Writer.WriteString(key, valueString);
                }

                Writer.WriteEndObject();
            }

            if (annotation.BuildSecrets.Count > 0)
            {
                Writer.WriteStartObject("secrets");

                foreach (var (key, value) in annotation.BuildSecrets)
                {
                    var valueString = value switch
                    {
                        FileInfo fileValue => GetManifestRelativePath(fileValue.FullName),
                        string stringValue => stringValue,
                        IManifestExpressionProvider manifestExpression => GetManifestExpression(manifestExpression, manifestExpression.ValueExpression),
                        bool boolValue => boolValue ? "true" : "false",
                        null => null, // null means let docker build pull from env var.
                        _ => value.ToString()
                    };

                    Writer.WriteStartObject(key);

                    if (value is FileInfo)
                    {
                        Writer.WriteString("type", "file");
                        Writer.WriteString("source", valueString);
                    }
                    else
                    {
                        Writer.WriteString("type", "env");
                        Writer.WriteString("value", valueString);
                    }

                    Writer.WriteEndObject();

                    TryAddDependentResources(value);
                }

                Writer.WriteEndObject();
            }

            Writer.WriteEndObject();
        }
    }

    /// <summary>
    /// Writes the "connectionString" field for the underlying resource.
    /// </summary>
    /// <param name="resource">The <see cref="IResource"/>.</param>
    public void WriteConnectionString(IResource resource)
    {
        if (resource is IResourceWithConnectionString resourceWithConnectionString &&
            resourceWithConnectionString.ConnectionStringExpression is { } connectionString)
        {
            TryAddDependentResources(connectionString);
            Writer.WriteString("connectionString", GetManifestExpression(connectionString));
        }
    }

    /// <summary>
    /// Writes endpoints to the resource entry in the manifest based on the resource's
    /// <see cref="EndpointAnnotation"/> entries in the <see cref="IResource.Annotations"/>
    /// collection.
    /// </summary>
    /// <param name="resource">The <see cref="IResource"/> that contains <see cref="EndpointAnnotation"/> annotations.</param>
    public void WriteBindings(IResource resource)
    {
        var resolvedEndpoints = resource.ResolveEndpoints(_portAllocator);

        if (resolvedEndpoints.Count > 0)
        {
            Writer.WriteStartObject("bindings");
            foreach (var resolved in resolvedEndpoints)
            {
                var endpoint = resolved.Endpoint;

                Writer.WriteStartObject(endpoint.Name);
                Writer.WriteString("scheme", endpoint.UriScheme);
                Writer.WriteString("protocol", endpoint.Protocol.ToString().ToLowerInvariant());
                Writer.WriteString("transport", endpoint.Transport);

                // Only emit exposedPort if it's not implicit (i.e., it was explicitly set or allocated)
                // and it's different from the target port
                if (resolved.ExposedPort.Value is int exposedPort && !resolved.ExposedPort.IsImplicit &&
                    (!resolved.TargetPort.Value.HasValue || resolved.TargetPort.Value.Value != exposedPort))
                {
                    Writer.WriteNumber("port", exposedPort);
                }

                if (resolved.TargetPort.Value is int targetPort)
                {
                    Writer.WriteNumber("targetPort", targetPort);
                }

                if (endpoint.IsExternal)
                {
                    Writer.WriteBoolean("external", endpoint.IsExternal);
                }

                Writer.WriteEndObject();
            }
            Writer.WriteEndObject();
        }
    }

    /// <summary>
    /// Writes environment variables to the manifest base on the <see cref="IResource"/> resource's <see cref="EnvironmentCallbackAnnotation"/> annotations."/>
    /// </summary>
    /// <param name="resource">The <see cref="IResource"/> which contains <see cref="EnvironmentCallbackAnnotation"/> annotations.</param>
    public async Task WriteEnvironmentVariablesAsync(IResource resource)
    {
        var executionConfiguration = await ExecutionConfigurationBuilder.Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(ExecutionContext, NullLogger.Instance, CancellationToken)
            .ConfigureAwait(false);

        if (executionConfiguration.Exception is not null)
        {
            ExceptionDispatchInfo.Throw(executionConfiguration.Exception);
        }

        if (!executionConfiguration.EnvironmentVariablesWithUnprocessed.Any())
        {
            return;
        }

        Writer.WriteStartObject("env");

        foreach (var kvp in executionConfiguration.EnvironmentVariablesWithUnprocessed)
        {
            var (unprocessed, processed) = kvp.Value;

            var manifestExpression = GetManifestExpression(unprocessed, processed);

            Writer.WriteString(kvp.Key, manifestExpression);

            TryAddDependentResources(unprocessed);
        }

        Writer.WriteEndObject();
    }

    /// <summary>
    /// Writes command line arguments to the manifest based on the <see cref="IResource"/> resource's <see cref="CommandLineArgsCallbackAnnotation"/> annotations.
    /// </summary>
    /// <param name="resource">The <see cref="IResource"/> that contains <see cref="CommandLineArgsCallbackAnnotation"/> annotations.</param>
    /// <returns>The <see cref="Task"/> to await for completion.</returns>
    public async Task WriteCommandLineArgumentsAsync(IResource resource)
    {
        var executionConfiguration = await ExecutionConfigurationBuilder.Create(resource)
            .WithArgumentsConfig()
            .BuildAsync(ExecutionContext, NullLogger.Instance, CancellationToken)
            .ConfigureAwait(false);

        if (executionConfiguration.Exception is not null)
        {
            ExceptionDispatchInfo.Throw(executionConfiguration.Exception);
        }

        if (!executionConfiguration.ArgumentsWithUnprocessed.Any())
        {
            return;
        }

        Writer.WriteStartArray("args");

        foreach ((var Unprocessed, var Processed, _) in executionConfiguration.ArgumentsWithUnprocessed)
        {
            var manifestExpression = GetManifestExpression(Unprocessed, Processed);

            Writer.WriteStringValue(manifestExpression);

            TryAddDependentResources(Unprocessed);
        }

        Writer.WriteEndArray();
    }
    private void WriteContainerMounts(ContainerResource container)
    {
        if (container.TryGetAnnotationsOfType<ContainerMountAnnotation>(out var mounts))
        {
            // Write out details for bind mounts
            var bindMounts = mounts.Where(mounts => mounts.Type == ContainerMountType.BindMount).ToList();
            if (bindMounts.Count > 0)
            {
                // Bind mounts are written as an array of objects to be consistent with volumes
                Writer.WriteStartArray("bindMounts");

                foreach (var bindMount in bindMounts)
                {
                    Writer.WriteStartObject();

                    Writer.WritePropertyName("source");
                    var manifestRelativeSource = GetManifestRelativePath(bindMount.Source);
                    Writer.WriteStringValue(manifestRelativeSource);

                    Writer.WritePropertyName("target");
                    Writer.WriteStringValue(bindMount.Target.Replace('\\', '/'));

                    Writer.WriteBoolean("readOnly", bindMount.IsReadOnly);

                    Writer.WriteEndObject();
                }

                Writer.WriteEndArray();
            }

            // Write out details for volumes
            var volumes = mounts.Where(mounts => mounts.Type == ContainerMountType.Volume).ToList();
            if (volumes.Count > 0)
            {
                // Volumes are written as an array of objects as anonymous volumes do not have a name
                Writer.WriteStartArray("volumes");

                foreach (var volume in volumes)
                {
                    Writer.WriteStartObject();

                    // This can be null for anonymous volumes
                    if (volume.Source is not null)
                    {
                        Writer.WritePropertyName("name");
                        Writer.WriteStringValue(volume.Source);
                    }

                    Writer.WritePropertyName("target");
                    Writer.WriteStringValue(volume.Target);

                    Writer.WriteBoolean("readOnly", volume.IsReadOnly);

                    Writer.WriteEndObject();
                }

                Writer.WriteEndArray();
            }
        }
    }

    /// <summary>
    /// Ensures that any <see cref="IResource"/> instances referenced by <paramref name="value"/> are
    /// written to the manifest.
    /// </summary>
    /// <param name="value">The object to check for references that may be resources that need to be written.</param>
    public void TryAddDependentResources(object? value)
    {
        // Custom manifest callbacks can write ValueExpression directly. Register the values in that
        // expression, but keep dependency traversal from registering inactive conditional branches.
        if (value is IManifestExpressionProvider expression)
        {
            _ = GetManifestExpression(value, expression.ValueExpression);
        }

        AddDependentResources(value);
    }

    private void AddDependentResources(object? value)
    {
        if (value is ReferenceExpression referenceExpression)
        {
            RegisterFormattedParameters(referenceExpression);
        }

        if (value is IResource resource)
        {
            // add the resource to the ReferencedResources for now. After the whole model is written,
            // these will be written to the manifest
            if (_referencedResources.TryAdd(resource.Name, resource))
            {
                _manifestResourceNames.Add(resource.Name);
            }
        }
        else if (value is IValueWithReferences objectWithReferences)
        {
            // ensure we don't infinitely recurse if there are cycles in the graph
            _currentDependencySet.Add(value);
            foreach (var dependency in objectWithReferences.References)
            {
                if (!_currentDependencySet.Contains(dependency))
                {
                    AddDependentResources(dependency);
                }
            }
            _currentDependencySet.Remove(value);
        }
    }

    /// <summary>
    /// Gets the manifest representation of a value, registering any generated values or required inputs.
    /// </summary>
    /// <param name="value">The symbolic value to represent in the manifest.</param>
    /// <returns>The expression to write into the manifest.</returns>
    /// <remarks>
    /// Manifest callbacks should use this method rather than writing
    /// <see cref="IManifestExpressionProvider.ValueExpression"/> directly when a value may require lowering.
    /// Opaque tokens from symbolic-only providers are preserved without concrete evaluation.
    /// Call <see cref="TryAddDependentResources"/> to include its referenced resources.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public string GetManifestExpression(IManifestExpressionProvider value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return GetManifestExpression(value, value.ValueExpression);
    }

    private string GetManifestExpression(object? source, string expression)
    {
        if (source is not IManifestExpressionProvider)
        {
            return expression;
        }

        if (!_loweringExpressions.Add(source))
        {
            throw new InvalidOperationException("A cycle was encountered while lowering a manifest expression.");
        }

        try
        {
            return source switch
            {
                EndpointReferenceExpression endpoint when endpoint.Property is
                    EndpointProperty.BrowserOrigin or EndpointProperty.BrowserOrigins or EndpointProperty.BrowserOriginsJson =>
                    RegisterEndpointPropertyInput(endpoint),
                ReferenceExpression referenceExpression => GetManifestExpression(referenceExpression),
                _ => LowerManifestReferences(source, expression)
            };
        }
        finally
        {
            _loweringExpressions.Remove(source);
        }
    }

    private string GetManifestExpression(ReferenceExpression referenceExpression)
    {
        if (referenceExpression.IsConditional)
        {
            return RegisterManifestValue(referenceExpression.Name!, referenceExpression, resolveValue: false);
        }

        var arguments = new string[referenceExpression.ManifestExpressions.Count];

        for (var i = 0; i < arguments.Length; i++)
        {
            var expression = GetManifestExpression(referenceExpression.ValueProviders[i], referenceExpression.ManifestExpressions[i]);
            var format = referenceExpression.StringFormats[i];

            if (!string.IsNullOrEmpty(format))
            {
                if (GetFormattedResourceNameForProvider(referenceExpression.ValueProviders[i], format) is { } formattedResourceName)
                {
                    expression = $"{{{formattedResourceName}.value}}";
                }
                else
                {
                    expression = RegisterFormattedExpression(expression, format);
                }
            }

            arguments[i] = expression;
        }

        return string.Format(CultureInfo.InvariantCulture, referenceExpression.Format, arguments);
    }

    private string LowerManifestReferences(object source, string expression)
    {
        // A wrapper can expose a complete structured expression. Preserve its ordered provider
        // occurrences and formats rather than guessing which object each repeated token denotes.
        if (FindManifestReference(source, expression, structuredOnly: true) is ReferenceExpression structured)
        {
            return GetManifestExpression(structured, structured.ValueExpression);
        }

        var builder = new StringBuilder();
        var position = 0;
        foreach (var token in ManifestExpression.GetTokens(expression))
        {
            var referenced = FindManifestReference(source, token.Text, structuredOnly: false);
            string lowered;
            if (referenced is IManifestExpressionProvider provider)
            {
                lowered = GetManifestExpression(referenced, provider.ValueExpression);
            }
            else if (ManifestExpression.IsSupportedReference(token.Text))
            {
                lowered = token.Text;
            }
            else if (source is IValueProvider valueProvider)
            {
                // The provider's expression cannot be represented by azd. Resolve this provider only,
                // not the enclosing expression, so adjacent deployment references remain symbolic.
                return RegisterConcreteManifestValue(valueProvider, expression);
            }
            else
            {
                // Symbolic-only providers own their representation. They do not advertise
                // concrete evaluation, so preserve opaque tokens as before rather than
                // treating this publisher's vocabulary as validation of their contract.
                lowered = token.Text;
            }

            builder.Append(expression, position, token.Start - position);
            builder.Append(lowered);
            position = token.Start + token.Length;
        }

        return builder.Append(expression, position, expression.Length - position).ToString();
    }

    private static object? FindManifestReference(object source, string expression, bool structuredOnly)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var matches = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Collect(source);
        return matches.Count switch
        {
            0 => null,
            1 => matches.Single(),
            _ => throw new InvalidOperationException(
                $"Manifest expression '{expression}' matches multiple distinct providers without unambiguous expression structure.")
        };

        void Collect(object current)
        {
            if (!visited.Add(current) || current is IResource || current is not IValueWithReferences references)
            {
                return;
            }

            foreach (var reference in references.References)
            {
                if (ReferenceEquals(current, reference))
                {
                    continue;
                }

                if ((!structuredOnly || reference is ReferenceExpression) &&
                    reference is IManifestExpressionProvider provider && provider.ValueExpression == expression)
                {
                    matches.Add(reference);
                    continue;
                }

                // Conditional dependencies include both branches; only a selected alias may expand them.
                if (reference is IResource or ReferenceExpression { IsConditional: true })
                {
                    continue;
                }

                Collect(reference);
            }
        }
    }

    private string RegisterFormattedExpression(string expression, string format)
    {
        // annotated.string currently supports only URI-component encoding in azd.
        _ = FormattingHelpers.FormatValue(string.Empty, format);
        if (!ManifestExpression.GetTokens(expression).Any())
        {
            var literal = expression.Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal);
            return EscapeManifestLiteral(FormattingHelpers.FormatValue(literal, format));
        }

        var key = (expression, format.ToLowerInvariant());
        if (!_formattedExpressions.TryGetValue(key, out var name))
        {
            var hash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(expression)).ToString("x16", CultureInfo.InvariantCulture);
            name = CreateFormattedParameterResourceName($"value-{hash}", key.Item2);
            _formattedExpressions.Add(key, name);
            _formattedValues.Add($"{{{name}.connectionString}}", new FormattedManifestValue(expression, key.Item2));
        }

        return $"{{{name}.connectionString}}";
    }

    private void WriteFormattedExpressions()
    {
        foreach (var ((expression, format), name) in _formattedExpressions)
        {
            var formatted = FormatManifestExpression(expression, [format], new HashSet<string>(StringComparer.Ordinal));
            Writer.WriteStartObject(name);
            Writer.WriteString("type", "value.v0");
            Writer.WriteString("connectionString", formatted);
            Writer.WriteEndObject();
        }

        foreach (var ((expression, format), name) in _formattedReferences)
        {
            Writer.WriteStartObject(name);
            Writer.WriteString("type", "annotated.string");
            Writer.WriteString("value", expression);
            Writer.WriteString("filter", format);
            Writer.WriteEndObject();
        }
    }

    private string FormatManifestExpression(string expression, IReadOnlyList<string> formats, HashSet<string> visited)
    {
        var builder = new StringBuilder();
        var position = 0;
        foreach (var token in ManifestExpression.GetTokens(expression))
        {
            AppendLiteral(expression[position..token.Start]);
            if (_resolvedManifestValues.TryGetValue(token.Text, out var resolved))
            {
                builder.Append(FormatLiteral(resolved));
            }
            else if (_formattedValues.TryGetValue(token.Text, out var formatted))
            {
                if (!visited.Add(token.Text))
                {
                    throw new InvalidOperationException("A cycle was encountered while formatting a manifest expression.");
                }

                // Retain the inner operation, then apply the outer operations. For example, a
                // query value encoded inside a redirect URL must turn a space into "%2520".
                builder.Append(FormatManifestExpression(formatted.Expression, [formatted.Format, .. formats], visited));
                visited.Remove(token.Text);
            }
            else if (_loweredManifestValues.TryGetValue(token.Text, out var lowered))
            {
                if (!visited.Add(token.Text))
                {
                    throw new InvalidOperationException("A cycle was encountered while formatting a manifest expression.");
                }

                // These bodies have already been selected/lowered. Do not revisit conditions or
                // resolve deployment values just to format an alias.
                builder.Append(FormatManifestExpression(lowered, formats, visited));
                visited.Remove(token.Text);
            }
            else if (IsDeferredFormattingReference(token.Text))
            {
                // azd's URI filter encodes substitutions, not surrounding literals. Give it one
                // deferred value at a time and encode the literal segments in this publisher.
                var reference = token.Text;
                foreach (var format in formats)
                {
                    var key = (reference, format);
                    if (!_formattedReferences.TryGetValue(key, out var name))
                    {
                        var hash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(reference)).ToString("x16", CultureInfo.InvariantCulture);
                        name = CreateFormattedParameterResourceName($"value-{hash}", format);
                        _formattedReferences.Add(key, name);
                        _formattedValues.Add($"{{{name}.value}}", new FormattedManifestValue(reference, format));
                    }

                    reference = $"{{{name}.value}}";
                }

                builder.Append(reference);
            }
            else
            {
                throw new NotSupportedException(
                    $"URI formatting cannot be deferred for manifest reference '{token.Text}'. " +
                    "Use a parameter/output expression or a publication-time value.");
            }

            position = token.Start + token.Length;
        }

        AppendLiteral(expression[position..]);
        return builder.ToString();

        void AppendLiteral(string value)
        {
            var literal = value.Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal);
            builder.Append(FormatLiteral(literal));
        }

        string FormatLiteral(string value)
        {
            foreach (var format in formats)
            {
                value = FormattingHelpers.FormatValue(value, format);
            }

            return EscapeManifestLiteral(value);
        }
    }

    private bool IsDeferredFormattingReference(string expression)
    {
        var parts = expression[1..^1].Split('.');
        return parts.Length >= 3 && parts[1] is "inputs" or "outputs" or "secretOutputs" or "secrets" ||
            parts is [var resource, "value" or "connectionString"] && _parameterNames.Contains(resource);
    }

    private void RegisterFormattedParameters(ReferenceExpression referenceExpression)
    {
        if (referenceExpression.IsConditional)
        {
            RegisterFormattedParameters(referenceExpression.WhenTrue!);
            RegisterFormattedParameters(referenceExpression.WhenFalse!);
            return;
        }

        var providers = referenceExpression.ValueProviders;
        var formats = referenceExpression.StringFormats;

        for (var i = 0; i < providers.Count; i++)
        {
            var format = formats[i];

            if (string.IsNullOrEmpty(format))
            {
                continue;
            }

            _ = GetFormattedResourceNameForProvider(providers[i], format);
        }
    }

    private string RegisterFormattedParameter(ParameterResource parameter, string format)
    {
        if (!_formattedParameters.TryGetValue(parameter, out var formats))
        {
            formats = new Dictionary<string, string>(StringComparer.Ordinal);
            _formattedParameters[parameter] = formats;
        }

        if (!formats.TryGetValue(format, out var resourceName))
        {
            resourceName = CreateFormattedParameterResourceName(parameter.Name, format);
            formats[format] = resourceName;
            _formattedValues.Add($"{{{resourceName}.value}}", new FormattedManifestValue(parameter.ValueExpression, format));
        }

        return resourceName;
    }

    private string CreateFormattedParameterResourceName(string parameterName, string format)
    {
        var sanitizedFormat = SanitizeFormat(format);
        var baseName = $"{parameterName}-{sanitizedFormat}-encoded";
        var candidate = GetAvailableManifestResourceName(baseName);
        _manifestResourceNames.Add(candidate);
        return candidate;
    }

    private string GetAvailableManifestResourceName(string baseName)
    {
        var candidate = baseName;
        var suffix = 1;
        while (_manifestResourceNames.Contains(candidate))
        {
            candidate = $"{baseName}-{suffix++}";
        }

        return candidate;
    }

    private static string SanitizeFormat(string format)
    {
        if (string.IsNullOrEmpty(format))
        {
            return "formatted";
        }

        var builder = new StringBuilder(format.Length);
        var lastWasSeparator = false;

        foreach (var ch in format)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }

        var sanitized = builder.ToString().Trim('-');
        return sanitized.Length > 0 ? sanitized : "formatted";
    }

    private void WriteFormattedParameterResources(ParameterResource parameter)
    {
        if (!_formattedParameters.TryGetValue(parameter, out var formats))
        {
            return;
        }

        // Retain names after emission so selected conditional branches can reuse these resources.
        foreach (var (format, resourceName) in formats)
        {
            if (!_writtenFormattedParameters.Add(resourceName))
            {
                continue;
            }

            Writer.WriteStartObject(resourceName);
            Writer.WriteString("type", "annotated.string");
            Writer.WriteString("value", parameter.ValueExpression);
            Writer.WriteString("filter", format);
            Writer.WriteEndObject();
        }
    }

    private void WriteRemainingFormattedParameters()
    {
        if (_formattedParameters.Count == 0)
        {
            return;
        }

        var pending = new List<ParameterResource>(_formattedParameters.Keys);

        foreach (var parameter in pending)
        {
            WriteFormattedParameterResources(parameter);
        }
    }

    private async Task WriteManifestValuesAsync()
    {
        var context = new ValueProviderContext { ExecutionContext = ExecutionContext };
        while (_pendingManifestValues.TryDequeue(out var name))
        {
            var manifestValue = _manifestValues[name];
            if (manifestValue.ResolveValue)
            {
                var resolved = await manifestValue.Provider.GetValueAsync(context, CancellationToken).ConfigureAwait(false) ?? string.Empty;
                _resolvedManifestValues.Add($"{{{name}.connectionString}}", resolved);
                Writer.WriteStartObject(name);
                Writer.WriteString("type", "value.v0");
                // Resolved values are literals, not new manifest expressions.
                Writer.WriteString("connectionString", EscapeManifestLiteral(resolved));
                Writer.WriteEndObject();
                continue;
            }

            ReferenceExpression expression;
            switch (manifestValue.Provider)
            {
                case ReferenceExpression { IsConditional: true } conditional:
                    // azd cannot evaluate conditions. Select only the active branch here, but retain
                    // its supported parameter/output references for deployment-time resolution.
                    var condition = await conditional.Condition!.GetValueAsync(context, CancellationToken).ConfigureAwait(false);
                    expression = string.Equals(condition, conditional.MatchValue, StringComparison.OrdinalIgnoreCase)
                        ? conditional.WhenTrue!
                        : conditional.WhenFalse!;
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported manifest value '{name}'.");
            }

            // Serializing the selected expression can enqueue nested values. Do not discover them
            // by walking both branches, which would require publishing support for inactive values.
            var value = GetManifestExpression(expression);
            _loweredManifestValues.Add($"{{{name}.connectionString}}", value);

            Writer.WriteStartObject(name);
            Writer.WriteString("type", "value.v0");
            Writer.WriteString("connectionString", value);
            Writer.WriteEndObject();
        }
    }

    private string RegisterManifestValue(string name, IValueProvider provider, bool resolveValue)
    {
        if (!_manifestValues.ContainsKey(name))
        {
            if (!_manifestResourceNames.Add(name))
            {
                throw new InvalidOperationException($"The generated manifest value resource name '{name}' conflicts with another manifest resource.");
            }
            _manifestValues.Add(name, new ManifestValue(provider, resolveValue));
            _pendingManifestValues.Enqueue(name);
        }

        return $"{{{name}.connectionString}}";
    }

    private string RegisterConcreteManifestValue(IValueProvider provider, string expression)
    {
        if (_concreteManifestReferences.TryGetValue(provider, out var reference))
        {
            return reference;
        }

        // Expression text is a naming hint, not an equivalence contract for arbitrary providers.
        // Allocate deterministic suffixes for distinct instances with the same naming hint.
        var hash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(expression)).ToString("x16", CultureInfo.InvariantCulture);
        var name = GetAvailableManifestResourceName($"value-{hash}");
        reference = RegisterManifestValue(name, provider, resolveValue: true);
        _concreteManifestReferences.Add(provider, reference);
        return reference;
    }

    private static string EscapeManifestLiteral(string value) =>
        value.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);

    private string RegisterEndpointPropertyInput(EndpointReferenceExpression endpoint)
    {
        if (!_endpointPropertyInputs.TryGetValue(endpoint.ValueExpression, out var name))
        {
            // azd understands ordinary binding tokens, but not browser-origin properties.
            // Keep those properties symbolic as required inputs; no runtime allocation is read.
            var baseName = ResourceNameHelpers.TruncateWithHash(
                $"{endpoint.Endpoint.Resource.Name}-{endpoint.Endpoint.EndpointName}-{endpoint.Property}",
                ModelName.DefaultMaxLength);
            name = GetAvailableManifestResourceName(baseName);
            _manifestResourceNames.Add(name);
            _parameterNames.Add(name);
            _endpointPropertyInputs.Add(endpoint.ValueExpression, name);
        }

        return $"{{{name}.value}}";
    }

    private void WriteEndpointPropertyInputs()
    {
        foreach (var name in _endpointPropertyInputs.Values)
        {
            Writer.WriteStartObject(name);
            Writer.WriteString("type", "parameter.v0");
            Writer.WriteString("value", $"{{{name}.inputs.value}}");
            Writer.WriteStartObject("inputs");
            Writer.WriteStartObject("value");
            Writer.WriteString("type", "string");
            Writer.WriteEndObject();
            Writer.WriteEndObject();
            Writer.WriteEndObject();
        }
    }

    private string? GetFormattedResourceNameForProvider(object provider, string format)
    {
        return provider switch
        {
            ParameterResource parameter => RegisterFormattedParameter(parameter, format),
            ReferenceExpression referenceExpression when TryGetSingleParameterProvider(referenceExpression, out var parameter) => RegisterFormattedParameter(parameter, format),
            _ => null
        };
    }

    private static bool TryGetSingleParameterProvider(ReferenceExpression referenceExpression, out ParameterResource parameter)
    {
        if (referenceExpression.ValueProviders.Count == 1 &&
            referenceExpression.ValueProviders[0] is ParameterResource parameterResource &&
            referenceExpression.ManifestExpressions.Count == 1 &&
            referenceExpression.Format == "{0}")
        {
            parameter = parameterResource;
            return true;
        }

        parameter = null!;
        return false;
    }

    private async Task WriteReferencedResources(DistributedApplicationModel model)
    {
        // remove references that were already in the model and were already written
        foreach (var existingResource in model.Resources)
        {
            _referencedResources.Remove(existingResource.Name);
        }

        // now write all the leftover referenced resources
        foreach (var resource in _referencedResources.Values)
        {
            await WriteResourceAsync(resource).ConfigureAwait(false);
        }

        _referencedResources.Clear();
    }

    private sealed record ManifestValue(IValueProvider Provider, bool ResolveValue);

    private sealed record FormattedManifestValue(string Expression, string Format);
}

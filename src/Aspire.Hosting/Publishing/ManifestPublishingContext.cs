// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
        _manifestResourceNames.Clear();

        foreach (var resource in model.Resources)
        {
            _manifestResourceNames.Add(resource.Name);
        }

        Writer.WriteStartObject();
        Writer.WriteString("$schema", SchemaUtils.SchemaVersion);
        Writer.WriteStartObject("resources");

        foreach (var resource in model.Resources)
        {
            await WriteResourceAsync(resource).ConfigureAwait(false);
        }

        await WriteReferencedResources(model).ConfigureAwait(false);

        WriteRemainingFormattedParameters();

        Writer.WriteEndObject();
        Writer.WriteEndObject();

        await Writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        _formattedParameters.Clear();
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
        else if (resource is ProjectResource project)
        {
            await WriteResourceObjectAsync(project, () => WriteProjectAsync(project)).ConfigureAwait(false);
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

    private async Task WriteProjectAsync(ProjectResource project)
    {
        if (!project.TryGetLastAnnotation<IProjectMetadata>(out var metadata))
        {
            throw new DistributedApplicationException("Project metadata not found.");
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
                throw new DistributedApplicationException("Could not get container image name.");
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

            // If there's a factory, generate the Dockerfile content and write it to both the original path and a resource-specific path
            await DockerfileHelper.ExecuteDockerfileFactoryAsync(annotation, container, ExecutionContext.ServiceProvider, CancellationToken).ConfigureAwait(false);

            if (annotation.DockerfileFactory is not null)
            {
                // Copy to a resource-specific path in the manifest output directory for publishing
                var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(ManifestPath))!;
                var resourceDockerfilePath = Path.Combine(manifestDirectory, $"{container.Name}.Dockerfile");
                Directory.CreateDirectory(manifestDirectory);
                File.Copy(annotation.DockerfilePath, resourceDockerfilePath, overwrite: true);

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
                    TryAddDependentResources(dependency);
                }
            }
            _currentDependencySet.Remove(value);
        }
    }

    private string GetManifestExpression(object? source, string expression)
    {
        return source switch
        {
            ReferenceExpression referenceExpression => GetManifestExpression(referenceExpression),
            _ => expression
        };
    }

    private string GetManifestExpression(ReferenceExpression referenceExpression)
    {
        var arguments = new string[referenceExpression.ManifestExpressions.Count];

        for (var i = 0; i < arguments.Length; i++)
        {
            var expression = referenceExpression.ManifestExpressions[i];
            var format = referenceExpression.StringFormats[i];

            if (!string.IsNullOrEmpty(format))
            {
                if (GetFormattedResourceNameForProvider(referenceExpression.ValueProviders[i], format) is { } formattedResourceName)
                {
                    expression = $"{{{formattedResourceName}.value}}";
                }
            }

            arguments[i] = expression;
        }

        return string.Format(CultureInfo.InvariantCulture, referenceExpression.Format, arguments);
    }

    private void RegisterFormattedParameters(ReferenceExpression referenceExpression)
    {
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
        }

        return resourceName;
    }

    private string CreateFormattedParameterResourceName(string parameterName, string format)
    {
        var sanitizedFormat = SanitizeFormat(format);
        var baseName = $"{parameterName}-{sanitizedFormat}-encoded";
        var candidate = baseName;
        var suffix = 1;

        while (_manifestResourceNames.Contains(candidate))
        {
            candidate = $"{baseName}-{suffix++}";
        }

        _manifestResourceNames.Add(candidate);
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

        foreach (var (format, resourceName) in formats)
        {
            Writer.WriteStartObject(resourceName);
            Writer.WriteString("type", "annotated.string");
            Writer.WriteString("value", parameter.ValueExpression);
            Writer.WriteString("filter", format);
            Writer.WriteEndObject();
        }

        _formattedParameters.Remove(parameter);
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
}

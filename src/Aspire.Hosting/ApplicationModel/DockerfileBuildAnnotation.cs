// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an annotation for customizing a Dockerfile build.
/// </summary>
/// <param name="contextPath">The path to the context directory for the build. </param>
/// <param name="dockerfilePath">The path to the Dockerfile to use for the build.</param>
/// <param name="stage">The name of the build stage to use for the build.</param>
public class DockerfileBuildAnnotation(string contextPath, string dockerfilePath, string? stage) : IResourceAnnotation
{
    private readonly SemaphoreSlim _materializationLock = new(1, 1);
    private bool _isMaterialized;

    /// <summary>
    /// Gets the path to the context directory for the build.
    /// </summary>
    public string ContextPath => contextPath;

    /// <summary>
    /// Gets the path to the Dockerfile to use for the build.
    /// </summary>
    public string DockerfilePath => dockerfilePath;

    /// <summary>
    /// Gets the name of the build stage to use for the build.
    /// </summary>
    public string? Stage => stage;

    /// <summary>
    /// Gets the arguments to pass to the build.
    /// </summary>
    public Dictionary<string, object?> BuildArguments { get; } = [];

    /// <summary>
    /// Gets the secrets to pass to the build.
    /// </summary>
    public Dictionary<string, object> BuildSecrets { get; } = [];

    /// <summary>
    /// Gets or sets the factory function that generates Dockerfile content dynamically.
    /// When set, this factory will be invoked to generate the Dockerfile content at build time,
    /// and the content will be written to a generated file path.
    /// </summary>
    public Func<DockerfileFactoryContext, Task<string>>? DockerfileFactory { get; init; }

    /// <summary>
    /// Gets or sets the image name for the generated container image.
    /// When set, this will be used as the container image name instead of the value from ContainerImageAnnotation.
    /// </summary>
    public string? ImageName { get; set; }

    /// <summary>
    /// Gets or sets the image tag for the generated container image.
    /// When set, this will be used as the container image tag instead of the value from ContainerImageAnnotation.
    /// </summary>
    public string? ImageTag { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether an entry point is defined in the Dockerfile.
    /// </summary>
    /// <remarks>
    /// Container images without an entry point are not considered compute resources.
    /// </remarks>
    public bool HasEntrypoint { get; set; } = true;

    /// <summary>
    /// Gets or sets the default <c>.dockerignore</c> content to emit alongside the published
    /// Dockerfile using BuildKit's per-Dockerfile ignore convention
    /// (<c>&lt;dockerfile-name&gt;.dockerignore</c> next to the Dockerfile).
    /// </summary>
    /// <remarks>
    /// When the build context root already contains a <c>.dockerignore</c> authored by the user,
    /// no per-Dockerfile file is emitted so the user's file is honored — BuildKit gives per-Dockerfile
    /// ignore files precedence over the context-root file, so emitting both would silently shadow
    /// a user override. See https://docs.docker.com/build/concepts/context/#filename-and-location.
    /// </remarks>
    public string? BuildContextIgnoreContent { get; set; }

    /// <summary>
    /// Materializes the Dockerfile from the factory if it hasn't been materialized yet.
    /// This method is thread-safe and ensures the Dockerfile is only written once.
    /// </summary>
    /// <param name="context">The context containing services and resource information.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task MaterializeDockerfileAsync(DockerfileFactoryContext context, CancellationToken cancellationToken)
    {
        if (DockerfileFactory is null)
        {
            return;
        }

        // Fast path: check if already materialized before acquiring lock
        if (_isMaterialized)
        {
            return;
        }

        await _materializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Check again after acquiring the lock to avoid redundant work
            if (_isMaterialized)
            {
                return;
            }

            var dockerfileContent = await DockerfileFactory(context).ConfigureAwait(false);
            await File.WriteAllTextAsync(DockerfilePath, dockerfileContent, cancellationToken).ConfigureAwait(false);
            _isMaterialized = true;
        }
        finally
        {
            _materializationLock.Release();
        }
    }

    /// <summary>
    /// Emits the per-Dockerfile <c>.dockerignore</c> sibling next to a published Dockerfile when
    /// <see cref="BuildContextIgnoreContent"/> is set. Intended to be called by publishers after
    /// copying the materialized Dockerfile into the publish output directory.
    /// </summary>
    /// <param name="resourceDockerfilePath">The path of the published Dockerfile (typically
    /// <c>&lt;outputDir&gt;/&lt;resource-name&gt;.Dockerfile</c>). The sibling
    /// <c>&lt;resourceDockerfilePath&gt;.dockerignore</c> is written next to it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <remarks>
    /// Skipped when <see cref="BuildContextIgnoreContent"/> is null. Also skipped when the build
    /// context root already contains a <c>.dockerignore</c> authored by the user — BuildKit gives
    /// per-Dockerfile ignore files precedence over the context-root file, so emitting both would
    /// silently shadow a user override. See
    /// https://docs.docker.com/build/concepts/context/#filename-and-location.
    /// </remarks>
    public async Task EmitBuildContextIgnoreAsync(string resourceDockerfilePath, CancellationToken cancellationToken)
    {
        if (BuildContextIgnoreContent is not { } content)
        {
            return;
        }

        var contextRootIgnore = Path.Combine(ContextPath, ".dockerignore");
        if (File.Exists(contextRootIgnore))
        {
            return;
        }

        var perDockerfileIgnore = $"{resourceDockerfilePath}.dockerignore";
        await File.WriteAllTextAsync(perDockerfileIgnore, content, cancellationToken).ConfigureAwait(false);
    }
}

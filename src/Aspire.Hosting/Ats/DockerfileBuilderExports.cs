// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only and is subject to change or removal in future updates.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Docker;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Ats;

/// <summary>
/// ATS exports for the Dockerfile builder DSL.
/// </summary>
internal static class DockerfileBuilderExports
{
    /// <summary>
    /// Adds a global ARG statement to the Dockerfile
    /// </summary>
    [AspireExport("dockerfileBuilderArg", MethodName = "arg")]
    public static DockerfileBuilder Arg(this DockerfileBuilder builder, string name, string? defaultValue = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return defaultValue is null ? builder.Arg(name) : builder.Arg(name, defaultValue);
    }

    /// <summary>
    /// Adds a FROM statement to start a Dockerfile stage
    /// </summary>
    [AspireExport("dockerfileBuilderFrom", MethodName = "from")]
    public static DockerfileStage From(this DockerfileBuilder builder, string image, string? stageName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return stageName is null ? builder.From(image) : builder.From(image, stageName);
    }

    /// <summary>
    /// Adds an ARG statement to a Dockerfile stage
    /// </summary>
    [AspireExport("dockerfileStageArg", MethodName = "arg")]
    public static DockerfileStage Arg(this DockerfileStage stage, string name, string? defaultValue = null)
    {
        ArgumentNullException.ThrowIfNull(stage);

        return defaultValue is null ? stage.Arg(name) : stage.Arg(name, defaultValue);
    }

    /// <summary>
    /// Adds a WORKDIR statement to a Dockerfile stage
    /// </summary>
    [AspireExport]
    public static DockerfileStage WorkDir(this DockerfileStage stage, string path)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return stage.WorkDir(path);
    }

    /// <summary>
    /// Adds a RUN statement to a Dockerfile stage
    /// </summary>
    [AspireExport("dockerfileStageRun", MethodName = "run")]
    public static DockerfileStage Run(this DockerfileStage stage, string command)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return stage.Run(command);
    }

    /// <summary>
    /// Adds a COPY statement to a Dockerfile stage
    /// </summary>
    [AspireExport("dockerfileStageCopy", MethodName = "copy")]
    public static DockerfileStage Copy(this DockerfileStage stage, string source, string destination, string? chown = null)
    {
        ArgumentNullException.ThrowIfNull(stage);

        return chown is null ? stage.Copy(source, destination) : stage.Copy(source, destination, chown);
    }

    /// <summary>
    /// Adds a COPY --from statement to a Dockerfile stage
    /// </summary>
    [AspireExport("dockerfileStageCopyFrom", MethodName = "copyFrom")]
    public static DockerfileStage CopyFrom(this DockerfileStage stage, string from, string source, string destination, string? chown = null)
    {
        ArgumentNullException.ThrowIfNull(stage);

        return chown is null ? stage.CopyFrom(from, source, destination) : stage.CopyFrom(from, source, destination, chown);
    }

    /// <summary>
    /// Adds an ENV statement to a Dockerfile stage
    /// </summary>
    [AspireExport]
    public static DockerfileStage Env(this DockerfileStage stage, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return stage.Env(name, value);
    }

    /// <summary>
    /// Adds an EXPOSE statement to a Dockerfile stage
    /// </summary>
    [AspireExport]
    public static DockerfileStage Expose(this DockerfileStage stage, int port)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return stage.Expose(port);
    }

    /// <summary>
    /// Adds a CMD statement to a Dockerfile stage
    /// </summary>
    [AspireExport]
    public static DockerfileStage Cmd(this DockerfileStage stage, string[] command)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return stage.Cmd(command);
    }

    /// <summary>
    /// Adds an ENTRYPOINT statement to a Dockerfile stage
    /// </summary>
    [AspireExport]
    public static DockerfileStage Entrypoint(this DockerfileStage stage, string[] command)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return stage.Entrypoint(command);
    }

    /// <summary>
    /// Adds a RUN statement with mounts to a Dockerfile stage
    /// </summary>
    [AspireExport]
    public static DockerfileStage RunWithMounts(this DockerfileStage stage, string command, string[] mounts)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return stage.RunWithMounts(command, mounts);
    }

    /// <summary>
    /// Adds a USER statement to a Dockerfile stage
    /// </summary>
    [AspireExport]
    public static DockerfileStage User(this DockerfileStage stage, string user)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return stage.User(user);
    }

    /// <summary>
    /// Adds an empty line to a Dockerfile stage
    /// </summary>
    [AspireExport]
    public static DockerfileStage EmptyLine(this DockerfileStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return stage.EmptyLine();
    }

    /// <summary>
    /// Adds a comment to a Dockerfile stage
    /// </summary>
    [AspireExport]
    public static DockerfileStage Comment(this DockerfileStage stage, string comment)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return stage.Comment(comment);
    }

    /// <summary>
    /// Adds Dockerfile stages for published container files
    /// </summary>
    [AspireExport("dockerfileBuilderAddContainerFilesStages", MethodName = "addContainerFilesStages")]
    public static DockerfileBuilder AddContainerFilesStages(this DockerfileBuilder builder, IResource resource, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resource);

        return ContainerFilesExtensions.AddContainerFilesStages(builder, resource, logger);
    }

    /// <summary>
    /// Adds COPY --from statements for published container files
    /// </summary>
    [AspireExport("dockerfileStageAddContainerFiles", MethodName = "addContainerFiles")]
    public static DockerfileStage AddContainerFiles(this DockerfileStage stage, IResource resource, string rootDestinationPath, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentException.ThrowIfNullOrEmpty(rootDestinationPath);

        return ContainerFilesExtensions.AddContainerFiles(stage, resource, rootDestinationPath, logger);
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel.Docker;

/// <summary>
/// Represents a stage within a multi-stage Dockerfile.
/// </summary>
[AspireExport]
[Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class DockerfileStage : DockerfileStatement
{
    private readonly List<DockerfileStatement> _statements = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="DockerfileStage"/> class.
    /// </summary>
    /// <param name="stageName">The optional stage name.</param>
    /// <param name="imageReference">The base image reference.</param>
    public DockerfileStage(string? stageName, string imageReference)
    {
        StageName = stageName;

        // Add the FROM statement as the first statement
        _statements.Add(new DockerfileFromStatement(imageReference, stageName));
    }

    /// <summary>
    /// Gets the name of the stage.
    /// </summary>
    public string? StageName { get; }

    /// <summary>
    /// Gets the statements for this stage.
    /// </summary>
    public IList<DockerfileStatement> Statements => _statements;

    /// <summary>
    /// Adds an ARG statement to define a build-time variable.
    /// </summary>
    /// <param name="name">The name of the build argument.</param>
    /// <returns>The current stage.</returns>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public DockerfileStage Arg(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _statements.Add(new DockerfileArgStatement(name));
        return this;
    }

    /// <summary>
    /// Adds an ARG statement to define a build-time variable with a default value.
    /// </summary>
    /// <param name="name">The name of the build argument.</param>
    /// <param name="defaultValue">The default value for the build argument.</param>
    /// <returns>The current stage.</returns>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public DockerfileStage Arg(string name, string defaultValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(defaultValue);
        _statements.Add(new DockerfileArgStatement(name, defaultValue));
        return this;
    }

    /// <summary>
    /// Adds a WORKDIR statement to set the working directory.
    /// </summary>
    /// <param name="path">The working directory path.</param>
    /// <returns>The current stage.</returns>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public DockerfileStage WorkDir(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _statements.Add(new DockerfileWorkDirStatement(path));
        return this;
    }

    /// <summary>
    /// Adds a RUN statement to execute a command.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <returns>The current stage.</returns>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public DockerfileStage Run(string command)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        _statements.Add(new DockerfileRunStatement(command));
        return this;
    }

    /// <summary>
    /// Adds a COPY statement to copy files from the build context.
    /// </summary>
    /// <param name="source">The source path or pattern.</param>
    /// <param name="destination">The destination path.</param>
    /// <returns>The current stage.</returns>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public DockerfileStage Copy(string source, string destination)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        _statements.Add(new DockerfileCopyStatement(source, destination));
        return this;
    }

    /// <summary>
    /// Adds a COPY statement to copy files from another stage.
    /// </summary>
    /// <param name="from">The source stage or image name.</param>
    /// <param name="source">The source path in the stage.</param>
    /// <param name="destination">The destination path.</param>
    /// <returns>The current stage.</returns>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public DockerfileStage CopyFrom(string from, string source, string destination)
    {
        ArgumentException.ThrowIfNullOrEmpty(from);
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        _statements.Add(new DockerfileCopyFromStatement(from, source, destination));
        return this;
    }

    /// <summary>
    /// Adds a COPY statement to copy files with ownership change.
    /// </summary>
    /// <param name="source">The source path.</param>
    /// <param name="destination">The destination path.</param>
    /// <param name="chown">The ownership specification (e.g., "user:group").</param>
    /// <returns>The current stage.</returns>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public DockerfileStage Copy(string source, string destination, string chown)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        ArgumentException.ThrowIfNullOrEmpty(chown);
        _statements.Add(new DockerfileCopyWithChownStatement(source, destination, chown));
        return this;
    }

    /// <summary>
    /// Adds a COPY statement to copy files from another stage with ownership change.
    /// </summary>
    /// <param name="stage">The source stage name.</param>
    /// <param name="source">The source path in the stage.</param>
    /// <param name="destination">The destination path.</param>
    /// <param name="chown">The ownership specification (e.g., "user:group").</param>
    /// <returns>The current stage.</returns>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public DockerfileStage CopyFrom(string stage, string source, string destination, string chown)
    {
        ArgumentException.ThrowIfNullOrEmpty(stage);
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        ArgumentException.ThrowIfNullOrEmpty(chown);
        _statements.Add(new DockerfileCopyFromWithChownStatement(stage, source, destination, chown));
        return this;
    }

    /// <summary>
    /// Adds an ENV statement to set an environment variable.
    /// </summary>
    /// <param name="name">The environment variable name.</param>
    /// <param name="value">The environment variable value.</param>
    /// <returns>The current stage.</returns>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public DockerfileStage Env(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        _statements.Add(new DockerfileEnvStatement(name, value));
        return this;
    }

    /// <summary>
    /// Adds an EXPOSE statement to expose a port.
    /// </summary>
    /// <param name="port">The port number to expose.</param>
    /// <returns>The current stage.</returns>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public DockerfileStage Expose(int port)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        _statements.Add(new DockerfileExposeStatement(port));
        return this;
    }

    /// <summary>
    /// Adds a CMD statement to set the default command.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <returns>The current stage.</returns>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public DockerfileStage Cmd(string[] command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Length == 0)
        {
            throw new ArgumentException("Command array cannot be empty.", nameof(command));
        }
        _statements.Add(new DockerfileCmdStatement(command));
        return this;
    }

    /// <summary>
    /// Adds an ENTRYPOINT statement to set the container entrypoint.
    /// </summary>
    /// <param name="command">The entrypoint command to execute.</param>
    /// <returns>The current stage.</returns>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public DockerfileStage Entrypoint(string[] command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Length == 0)
        {
            throw new ArgumentException("Command array cannot be empty.", nameof(command));
        }
        _statements.Add(new DockerfileEntrypointStatement(command));
        return this;
    }

    /// <summary>
    /// Adds a RUN statement with mount options for BuildKit.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <param name="mounts">The mount options (e.g., "type=cache,target=/root/.cache").</param>
    /// <returns>The current stage.</returns>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public DockerfileStage RunWithMounts(string command, params string[] mounts)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        ArgumentNullException.ThrowIfNull(mounts);
        _statements.Add(new DockerfileRunWithMountsStatement(command, mounts));
        return this;
    }

    /// <summary>
    /// Adds a USER statement to set the user for subsequent commands.
    /// </summary>
    /// <param name="user">The user name or UID.</param>
    /// <returns>The current stage.</returns>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public DockerfileStage User(string user)
    {
        ArgumentException.ThrowIfNullOrEmpty(user);
        _statements.Add(new DockerfileUserStatement(user));
        return this;
    }

    /// <summary>
    /// Adds an empty line to the Dockerfile for better readability.
    /// </summary>
    /// <returns>The current stage.</returns>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public DockerfileStage EmptyLine()
    {
        _statements.Add(new DockerfileEmptyLineStatement());
        return this;
    }

    /// <summary>
    /// Adds a comment to the Dockerfile. Multi-line comments are supported.
    /// </summary>
    /// <param name="comment">The comment text. Can be single-line or multi-line.</param>
    /// <returns>The current stage.</returns>
    /// <remarks>
    /// When a multi-line comment is provided, each line will be prefixed with '#'.
    /// Empty lines in multi-line comments are preserved as comment lines.
    /// </remarks>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public DockerfileStage Comment(string comment)
    {
        ArgumentNullException.ThrowIfNull(comment);
        _statements.Add(new DockerfileCommentStatement(comment));
        return this;
    }

    /// <inheritdoc />
    public override async Task WriteStatementAsync(StreamWriter writer, CancellationToken cancellationToken = default)
    {
        foreach (var statement in _statements)
        {
            await statement.WriteStatementAsync(writer, cancellationToken).ConfigureAwait(false);
        }
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Python;

/// <summary>
/// Represents a Python application resource in the distributed application model.
/// </summary>
/// <remarks>
/// <para>
/// This resource allows Python applications (scripts, web servers, APIs, background services) to run as part
/// of a distributed application. The resource manages the Python executable, working directory,
/// and lifecycle of the Python application.
/// </para>
/// <para>
/// Python applications can expose HTTP endpoints, communicate with other services, and participate
/// in service discovery like other Aspire resources. They support automatic OpenTelemetry instrumentation
/// for observability when configured with the appropriate Python packages.
/// </para>
/// <para>
/// This resource supports various Python execution environments including:
/// <list type="bullet">
/// <item>System Python installations</item>
/// <item>Virtual environments (venv)</item>
/// <item>Conda environments</item>
/// <item>UV-based Python environments</item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// Add a Python web application using Flask or FastAPI:
/// <code lang="csharp">
/// var builder = DistributedApplication.CreateBuilder(args);
/// 
/// var python = builder.AddPythonApp("api", "../python-api", "app.py")
///     .WithHttpEndpoint(port: 5000)
///     .WithArgs("--host", "0.0.0.0");
/// 
/// builder.AddProject&lt;Projects.Frontend&gt;("frontend")
///     .WithReference(python);
/// 
/// builder.Build().Run();
/// </code>
/// </example>
/// <param name="name">The name of the resource in the application model.</param>
/// <param name="executablePath">
/// The path to the Python executable. This can be:
/// <list type="bullet">
/// <item>An absolute path: "/usr/bin/python3"</item>
/// <item>A relative path: "./venv/bin/python"</item>
/// <item>A command on the PATH: "python" or "python3"</item>
/// </list>
/// The executable is typically located in a virtual environment's bin (Linux/macOS) or Scripts (Windows) directory.
/// </param>
/// <param name="appDirectory">
/// The working directory for the Python application. Python scripts and modules
/// will be resolved relative to this directory. This is typically the root directory
/// of your Python project containing your main script and any local modules.
/// </param>
public class PythonAppResource(string name, string executablePath, string appDirectory)
    : ExecutableResource(name, executablePath, appDirectory), IResourceWithServiceDiscovery, IContainerFilesDestinationResource
{
    /// <inheritdoc/>
    public override string ServiceDisplayName => "Python";
}

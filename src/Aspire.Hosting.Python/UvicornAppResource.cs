// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Python;

/// <summary>
/// Represents a Uvicorn-based Python application resource that can be managed and executed within a Python environment.
/// </summary>
/// <remarks>
/// <para>
/// This resource is specifically designed for Python web applications that use Uvicorn as their ASGI server,
/// which is commonly used with frameworks like FastAPI, Starlette, and other ASGI applications.
/// </para>
/// <para>
/// The resource automatically configures HTTP endpoints and sets up appropriate Uvicorn-specific
/// command-line arguments for host binding and port configuration.
/// </para>
/// </remarks>
/// <param name="name">The unique name used to identify the Uvicorn application resource.</param>
/// <param name="executablePath">
/// The path to the Python executable. This is typically the Python executable within a virtual environment.
/// </param>
/// <param name="appDirectory">
/// The working directory for the Uvicorn application. This is typically the root directory
/// of your Python project containing your ASGI application module.
/// </param>
public class UvicornAppResource(string name, string executablePath, string appDirectory)
    : PythonAppResource(name, executablePath, appDirectory)
{
    /// <inheritdoc/>
    public override string ServiceDisplayName => "Uvicorn";
}

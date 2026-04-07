// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

builder.AddPythonApp("script-only", "../script_only", "main.py");
builder.AddPythonApp("instrumented-script", "../instrumented_script", "main.py");

builder.AddPythonModule("fastapi-app", "../module_only", "uvicorn")
    .WithArgs("api:app", "--reload", "--host=0.0.0.0", "--port=8000")
    .WithHttpEndpoint(targetPort: 8000)
    .WithUv();

// Run the same app on another port using uvicorn directly
builder.AddPythonExecutable("fastapi-uvicorn-app", "../module_only", "uvicorn")
    .WithDebugging()
    .WithArgs("api:app", "--reload", "--host=0.0.0.0", "--port=8001")
    .WithHttpEndpoint(targetPort: 8001);

// Flask app using Flask module directly
builder.AddPythonModule("flask-app", "../flask_app", "flask")
    .WithEnvironment("FLASK_APP", "app:create_app")
    .WithArgs(c =>
    {
        c.Args.Add("run");
        c.Args.Add("--host=0.0.0.0");
        c.Args.Add("--port=8002");
    })
    .WithHttpEndpoint(targetPort: 8002)
    .WithUv();

// Uvicorn app using the AddUvicornApp method
builder.AddUvicornApp("uvicorn-app", "../uvicorn_app", "app:app")
    .WithUv()
    .WithExternalHttpEndpoints();

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();

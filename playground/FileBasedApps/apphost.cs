// To run this app in this repo use the following command line to ensure latest changes are always picked up:
// $ dotnet apphost.cs --no-cache

// These directives are not required in regular apps, only here in the aspire repo itself
/*
#:sdk Aspire.AppHost.Sdk
*/
#:property IsAspireHost=true
#:property PublishAot=false
#:property UserSecretsId=d858c770-3e70-4307-8be4-90d2f96bf595

var builder = DistributedApplication.CreateBuilder(args);

// Display the runtime UserSecretsId for debugging
var userSecretsId = Environment.GetEnvironmentVariable("DOTNET_USER_SECRETS_ID")
    ?? builder.Configuration["UserSecretsId"]
    ?? "not set";
Console.WriteLine($"UserSecretsId: {userSecretsId}");

// C# File-based app
// NOTE: This is in a sub-folder to ensure it doesn't pickup .razor files from the FrontEnd project
builder.AddCSharpApp("api", "./api/api.cs");

// Traditional C# project added via same API just specifying project directory
builder.AddCSharpApp("frontend", "./FileBasedApps.WebFrontEnd/");

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

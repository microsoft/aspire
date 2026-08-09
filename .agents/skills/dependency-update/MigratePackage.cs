// Triggers and monitors the dotnet-migrate-package Azure DevOps pipeline.
// Usage: dotnet MigratePackage.cs <PackageName> <PackageVersion> [options]
//
// Options:
//   --poll-interval <seconds>   Polling interval (default: 30)
//   --timeout <seconds>         Max wait time (default: 900)
//   --migration-type <type>     Pipeline migration type (default: "New or non-Microsoft")
//   --no-wait                   Trigger only, don't wait for completion
//   --check-prereqs             Check prerequisites and exit
//
// Requires: Azure CLI logged in (`az login`) to a tenant with access to the dnceng Azure DevOps org.

#:package Microsoft.TeamFoundationServer.Client@19.*
#:package Microsoft.VisualStudio.Services.Client@19.*
#:package Azure.Identity@1.*

using System.Diagnostics;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Pipelines.WebApi;
using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.VisualStudio.Services.WebApi;

const string AzDoOrg = "https://dev.azure.com/dnceng";
const string AzDoProject = "internal";
const int PipelineId = 931;
const string AzDoScope = "499b84ac-1321-427f-aa17-267ca6975798/.default";

// Parse arguments
string? packageName = null;
string? packageVersion = null;
string migrationType = "New or non-Microsoft";
int pollInterval = 30;
int timeout = 900;
bool noWait = false;
bool checkPrereqs = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--help" or "-h":
            PrintUsage();
            return;
        case "--check-prereqs":
            checkPrereqs = true;
            break;
        case "--poll-interval":
            pollInterval = int.Parse(args[++i]);
            break;
        case "--timeout":
            timeout = int.Parse(args[++i]);
            break;
        case "--migration-type":
            migrationType = args[++i];
            break;
        case "--no-wait":
            noWait = true;
            break;
        default:
            if (args[i].StartsWith('-'))
            {
                LogError($"Unknown option: {args[i]}");
                PrintUsage();
                return;
            }
            if (packageName is null)
            {
                packageName = args[i];
            }
            else if (packageVersion is null)
            {
                packageVersion = args[i];
            }
            else
            {
                LogError($"Unexpected argument: {args[i]}");
                PrintUsage();
                return;
            }
            break;
    }
}

if (checkPrereqs)
{
    await CheckPrerequisitesAsync(verbose: true);
    return;
}

if (packageName is null || packageVersion is null)
{
    LogError("PackageName and PackageVersion are required.");
    Console.WriteLine();
    PrintUsage();
    return;
}

// Check prerequisites
if (!await CheckPrerequisitesAsync(verbose: true))
{
    return;
}

Console.WriteLine();

// Connect and trigger
var client = await ConnectAsync();
if (client is null)
{
    return;
}

var run = await TriggerPipelineAsync(client, packageName, packageVersion, migrationType);
if (run is null)
{
    return;
}

if (noWait)
{
    LogInfo($"Skipping wait (--no-wait). Monitor at:");
    LogInfo($"  {AzDoOrg}/{AzDoProject}/_build/results?buildId={run.Id}");
    return;
}

Console.WriteLine();

// Poll until completion
await PollPipelineAsync(client, run.Id, pollInterval, timeout);

// --- Functions ---

async Task<PipelinesHttpClient?> ConnectAsync()
{
    try
    {
        var tenantId = await GetAzCliTenantIdAsync();
        var credential = tenantId is not null
            ? new AzureCliCredential(new AzureCliCredentialOptions { TenantId = tenantId })
            : new AzureCliCredential();

        var token = await credential.GetTokenAsync(new TokenRequestContext([AzDoScope]));
        var vssCred = new VssOAuthAccessTokenCredential(token.Token);
        var connection = new VssConnection(new Uri(AzDoOrg), vssCred);
        return connection.GetClient<PipelinesHttpClient>();
    }
    catch (Exception ex)
    {
        LogError($"Failed to connect to Azure DevOps: {ex.Message}");
        LogError("Ensure you are logged in with `az login` to a tenant that has access to the dnceng org.");
        return null;
    }
}

async Task<Run?> TriggerPipelineAsync(PipelinesHttpClient client, string name, string version, string type)
{
    LogInfo("Triggering dotnet-migrate-package pipeline...");
    LogInfo($"  Package:        {name}");
    LogInfo($"  Version:        {version}");
    LogInfo($"  MigrationType:  {type}");

    try
    {
        var parameters = new RunPipelineParameters
        {
            TemplateParameters = new Dictionary<string, string>
            {
                ["PackageNames"] = name,
                ["PackageVersion"] = version,
                ["MigrationType"] = type
            }
        };

        var run = await client.RunPipelineAsync(parameters, AzDoProject, PipelineId);

        LogSuccess("Pipeline triggered successfully");
        LogInfo($"  Run ID: {run.Id}");
        LogInfo($"  URL:    {AzDoOrg}/{AzDoProject}/_build/results?buildId={run.Id}");

        return run;
    }
    catch (Exception ex)
    {
        LogError($"Failed to trigger pipeline: {ex.Message}");
        return null;
    }
}

async Task PollPipelineAsync(PipelinesHttpClient client, int runId, int interval, int maxWait)
{
    LogInfo($"Polling pipeline run {runId} (interval: {interval}s, timeout: {maxWait}s)...");

    var sw = Stopwatch.StartNew();

    while (true)
    {
        if (sw.Elapsed.TotalSeconds >= maxWait)
        {
            LogError($"Timeout after {maxWait}s waiting for pipeline run {runId}");
            return;
        }

        try
        {
            var run = await client.GetRunAsync(AzDoProject, PipelineId, runId);
            var elapsed = sw.Elapsed;
            var elapsedStr = $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds}s";

            if (run.State == RunState.Completed)
            {
                if (run.Result == RunResult.Succeeded)
                {
                    LogSuccess($"Pipeline completed successfully ({elapsedStr})");
                }
                else
                {
                    LogError($"Pipeline completed with result: {run.Result} ({elapsedStr})");
                    LogError($"See: {AzDoOrg}/{AzDoProject}/_build/results?buildId={runId}");
                }
                return;
            }

            LogInfo($"  Status: {run.State} (elapsed: {elapsedStr})...");
        }
        catch (Exception ex)
        {
            LogWarn($"  Poll error (will retry): {ex.Message}");
        }

        await Task.Delay(TimeSpan.FromSeconds(interval));
    }
}

async Task<bool> CheckPrerequisitesAsync(bool verbose)
{
    var ok = true;

    // Check az CLI
    if (!await IsCommandAvailableAsync("az"))
    {
        if (verbose)
        {
            LogError("Azure CLI (az) is not installed.");
            Console.WriteLine("  Install: https://learn.microsoft.com/cli/azure/install-azure-cli");
        }
        ok = false;
    }
    else if (verbose)
    {
        var (_, version) = await RunProcessAsync("az", "version --query \"azure-cli\" -o tsv");
        LogSuccess($"Azure CLI found: {version.Trim()}");
    }

    // Check az login status
    var (loginSuccess, loginOutput) = await RunProcessAsync("az", "account show --query user.name -o tsv");
    if (!loginSuccess)
    {
        if (verbose)
        {
            LogError("Not logged in to Azure CLI.");
            Console.WriteLine("  Login: az login");
        }
        ok = false;
    }
    else if (verbose)
    {
        LogSuccess($"Logged in as: {loginOutput.Trim()}");
    }

    // Check tenant
    if (loginSuccess)
    {
        var tenantId = await GetAzCliTenantIdAsync();
        if (verbose && tenantId is not null)
        {
            LogSuccess($"Tenant: {tenantId}");
        }
    }

    // Try to get a token for Azure DevOps
    if (loginSuccess)
    {
        try
        {
            var tenantId = await GetAzCliTenantIdAsync();
            var credential = tenantId is not null
                ? new AzureCliCredential(new AzureCliCredentialOptions { TenantId = tenantId })
                : new AzureCliCredential();
            await credential.GetTokenAsync(new TokenRequestContext([AzDoScope]));
            if (verbose)
            {
                LogSuccess("Azure DevOps token acquired successfully");
            }
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                LogError($"Failed to acquire Azure DevOps token: {ex.Message}");
                Console.WriteLine("  You may need to log in to the correct tenant: az login --tenant <tenant-id>");
            }
            ok = false;
        }
    }

    if (verbose)
    {
        if (ok)
        {
            LogSuccess("All prerequisites met");
        }
        else
        {
            LogError("Some prerequisites are missing. See above for details.");
        }
    }

    return ok;
}

async Task<string?> GetAzCliTenantIdAsync()
{
    // Discover the tenant from the current az CLI session.
    // The dnceng org is in the Microsoft tenant; if the user is logged into
    // a different tenant we attempt to find the Microsoft corp one.
    var (success, output) = await RunProcessAsync("az", "account show --query tenantId -o tsv");
    if (!success)
    {
        return null;
    }

    var currentTenant = output.Trim();

    // If already on the Microsoft corp tenant, use it directly
    if (string.Equals(currentTenant, "72f988bf-86f1-41af-91ab-2d7cd011db47", global::System.StringComparison.OrdinalIgnoreCase))
    {
        return currentTenant;
    }

    // Check if the Microsoft corp tenant is available in the account list
    var (listOk, listOutput) = await RunProcessAsync("az", "account list --query \"[?tenantId=='72f988bf-86f1-41af-91ab-2d7cd011db47'].tenantId | [0]\" -o tsv");
    if (listOk && !string.IsNullOrWhiteSpace(listOutput))
    {
        return listOutput.Trim();
    }

    return currentTenant;
}

async Task<bool> IsCommandAvailableAsync(string command)
{
    try
    {
        var (success, _) = await RunProcessAsync(command, "--version");
        return success;
    }
    catch
    {
        return false;
    }
}

async Task<(bool Success, string Output)> RunProcessAsync(string fileName, string arguments)
{
    using var process = Process.Start(new ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    });

    if (process is null)
    {
        return (false, string.Empty);
    }

    var output = await process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (process.ExitCode == 0, output);
}

void PrintUsage()
{
    Console.WriteLine("""
        MigratePackage.cs — Trigger and monitor the dotnet-migrate-package Azure DevOps pipeline

        USAGE:
            dotnet MigratePackage.cs <PackageName> <PackageVersion> [OPTIONS]
            dotnet MigratePackage.cs --check-prereqs
            dotnet MigratePackage.cs --help

        ARGUMENTS:
            PackageName       NuGet package ID (e.g., Hex1b)
            PackageVersion    Version to import (e.g., 0.49.0) or "latest"

        OPTIONS:
            --poll-interval <seconds>   Polling interval (default: 30)
            --timeout <seconds>         Max wait time (default: 900)
            --migration-type <type>     Pipeline migration type (default: "New or non-Microsoft")
            --no-wait                   Trigger only, don't wait for completion
            --check-prereqs             Check prerequisites and exit
            --help                      Show this help

        AUTHENTICATION:
            Uses Azure.Identity (AzureCliCredential) to acquire a token for Azure DevOps.
            Ensure you are logged in with: az login
            The script will automatically select the Microsoft corp tenant if available.

        EXAMPLES:
            dotnet MigratePackage.cs Hex1b 0.49.0
            dotnet MigratePackage.cs StackExchange.Redis 2.9.33 --no-wait
            dotnet MigratePackage.cs --check-prereqs
        """);
}

void LogInfo(string message) => Console.WriteLine($"\u001b[36m[INFO]\u001b[0m {message}");
void LogSuccess(string message) => Console.WriteLine($"\u001b[32m[OK]\u001b[0m {message}");
void LogWarn(string message) => Console.WriteLine($"\u001b[33m[WARN]\u001b[0m {message}");
void LogError(string message) => Console.Error.WriteLine($"\u001b[31m[ERROR]\u001b[0m {message}");

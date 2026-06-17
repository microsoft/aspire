#pragma warning disable ASPIREJAVASCRIPT001
#pragma warning disable ASPIREAZURE001

using Aspire.Hosting.Azure;

var builder = DistributedApplication.CreateBuilder(args);

var appService = builder.AddAzureAppServiceEnvironment("appservice");
var nodeIdentity = builder.AddAzureUserAssignedIdentity("node-identity");

var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes")
    .WithUserAssignedIdentity(nodeIdentity);

var gateway = builder.AddAzureConnectorGateway("gateway");
gateway.WithRoleAssignments(sandboxGroup, AzureSandboxGroupBuiltInRole.SandboxGroupDataOwner);

var sharePoint = gateway.AddConnection(
    "sharepoint",
    "sharepointonline",
    displayName: "SharePoint Online",
    connectionName: "sharepoint");

var sharePointMcp = gateway.AddConnection(
    "sharepointmcp",
    "workiqsharepoint",
    displayName: "SharePoint (Work IQ MCP)",
    connectionName: "sharepoint-mcp");

gateway.AddMcpServerConfig(
        "sharepointmcpconfig",
        "SharePoint (Work IQ MCP) - list files, read file content, and upload files.",
        configName: "sharepoint-mcp")
    .WithConnector(
        "workiqsharepoint",
        sharePointMcp,
        "mcp_SharePointRemoteServer",
        displayName: "SharePoint MCP Server",
        description: "Upstream MCP endpoint that proxies JSON-RPC traffic to the Work IQ SharePoint MCP server.");

var storage = builder.AddAzureStorage("storage");
var blobs = storage.AddBlobs("blobs");
var blobContainer = storage.AddBlobContainer("sandboxdata", blobContainerName: "sandbox-all-data");

var backend = builder.AddProject<Projects.SandboxAppServiceMixed_BackendService>("backend", launchProfileName: null)
    .WithHttpEndpoint(targetPort: 8080)
    .WithExternalHttpEndpoints()
    .WithComputeEnvironment(appService);

var node = builder.AddNodeApp("node", "../NodeStorageApp", "server.js")
    .WithNpm()
    .WithAzureUserAssignedIdentity(nodeIdentity)
    .WithHttpEndpoint(targetPort: 8080, env: "PORT")
    .WithExternalHttpEndpoints()
    .WithReference(blobs)
    .WithReference(blobContainer)
    .WaitFor(blobs)
    .WaitFor(blobContainer)
    .WithEnvironment("BLOB_SERVICE_URI", blobs.Resource.UriExpression)
    .WithEnvironment("BLOB_CONTAINER_NAME", "sandbox-all-data")
    .PublishAsSandbox(sandboxGroup, new AzureSandboxOptions
    {
        EgressTrafficInspection = "Partial"
    })
    .PublishAsPackageScript("start");

sharePoint.AddTriggerConfig(
    "newfile",
    "GetOnNewFileItems",
    node.GetEndpoint("http"),
    callbackPath: "/webhook",
    parameters:
    [
        new("dataset", "https://contoso.sharepoint.com/sites/demo"),
        new("table", "Documents")
    ],
    description: "Post new SharePoint file notifications to the sandbox Node listener.",
    triggerName: "new-file");

var frontend = builder.AddViteApp("frontend", "../SandboxAppServiceMixed.Frontend")
    .WithEnvironment("API_BASE_URL", backend.GetEndpoint("http"))
    .WithExternalHttpEndpoints()
    .PublishAsSandbox(sandboxGroup);

if (builder.ExecutionContext.IsPublishMode)
{
    frontend.WithEndpoint("http", endpoint => endpoint.TargetPort = 8080, createIfNotExists: true);
}

builder.Build().Run();

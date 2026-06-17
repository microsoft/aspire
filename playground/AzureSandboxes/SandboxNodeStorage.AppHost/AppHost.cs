#pragma warning disable ASPIREJAVASCRIPT001
#pragma warning disable ASPIREAZURE001

using Aspire.Hosting.Azure;

var builder = DistributedApplication.CreateBuilder(args);

var nodeIdentity = builder.AddAzureUserAssignedIdentity("node-identity");

var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes")
    .WithUserAssignedIdentity(nodeIdentity);

var storage = builder.AddAzureStorage("storage");
var blobs = storage.AddBlobs("blobs");
var blobContainer = storage.AddBlobContainer("sandboxdata", blobContainerName: "sandbox-data");

builder.AddNodeApp("node", "../NodeStorageApp", "server.js")
    .WithNpm()
    .WithAzureUserAssignedIdentity(nodeIdentity)
    .WithHttpEndpoint(targetPort: 8080, env: "PORT")
    .WithExternalHttpEndpoints()
    .WithReference(blobs)
    .WithReference(blobContainer)
    .WaitFor(blobs)
    .WaitFor(blobContainer)
    .WithEnvironment("BLOB_SERVICE_URI", blobs.Resource.UriExpression)
    .WithEnvironment("BLOB_CONTAINER_NAME", "sandbox-data")
    .PublishAsSandbox(sandboxGroup, new AzureSandboxOptions
    {
        // Partial keeps the ADC-managed identity endpoint available while avoiding TLS inspection
        // for normal outbound HTTPS calls to Azure Storage.
        EgressTrafficInspection = "Partial"
    })
    .PublishAsPackageScript("start");

builder.Build().Run();

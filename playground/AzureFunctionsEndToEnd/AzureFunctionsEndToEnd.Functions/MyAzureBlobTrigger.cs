using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AzureFunctionsEndToEnd.Functions;

public class MyAzureBlobTrigger(BlobContainerClient containerClient, ILogger<MyAzureBlobTrigger> logger)
{
    [Function(nameof(MyAzureBlobTrigger))]
    [BlobOutput("test-files/{name}.txt", Connection = "blob")]
    public async Task<string> RunAsync([BlobTrigger("myblobcontainer/{name}", Connection = "blob")] string triggerString, FunctionContext context)
    {
        var blobName = (string)context.BindingContext.BindingData["name"]!;
        await containerClient.UploadBlobAsync(blobName, new BinaryData(triggerString));

        logger.LogInformation("C# blob trigger function invoked for 'myblobcontainer/{source}' with {message}...", blobName, triggerString);
        return triggerString.ToUpperInvariant();
    }
}

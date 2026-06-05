using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AzureFunctionsService;

public sealed class HttpTrigger(ILogger<HttpTrigger> logger)
{
    [Function("hello")]
    public IActionResult Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequest req)
    {
        logger.LogInformation("Azure Functions trigger executed.");
        return new OkObjectResult("Hello from AzureFunctionsService");
    }
}

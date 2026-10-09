using System.Diagnostics;

namespace ActivityGenerator;

internal sealed class ActivityGenerationWorker(IHttpClientFactory httpClientFactory, ILogger<ActivityGenerationWorker> logger) : BackgroundService
{
    internal const string ActivitySourceName = "TestShop.ActivityGenerator";
    private const int WorkerCount = 4;
    private static readonly ActivitySource s_activitySource = new(ActivitySourceName);
    private static readonly (string Resource, string Path)[] s_routes =
    [
        ("frontend", "/"),
        ("frontend", "/?after=8"),
        ("frontend", "/?before=9"),
        ("frontend", "/catalog/images/1"),
        ("frontend", "/catalog/images/2"),
        ("frontend", "/catalog/images/3"),
        ("apigateway", "/catalog/api/v1/catalog/items/type/all/brand?pageSize=8"),
        ("apigateway", "/catalog/api/v1/catalog/items/type/all/brand?after=8&pageSize=8"),
        ("catalogservice", "/api/v1/catalog/items/type/all/brand?pageSize=8")
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Generating read-only TestShop activity with {WorkerCount} workers until stopped.", WorkerCount);

        try
        {
            await Task.WhenAll(Enumerable.Range(0, WorkerCount).Select(worker => GenerateActivityAsync(worker, stoppingToken)));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping the resource cancels both in-flight requests and worker delays.
        }

        logger.LogInformation("Activity generation stopped.");
    }

    private async Task GenerateActivityAsync(int worker, CancellationToken stoppingToken)
    {
        var routeIndex = worker;

        while (!stoppingToken.IsCancellationRequested)
        {
            var route = s_routes[routeIndex];
            await SendRequestAsync(route.Resource, route.Path, stoppingToken);
            routeIndex = (routeIndex + 1) % s_routes.Length;

            await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
        }
    }

    private async Task SendRequestAsync(string resource, string path, CancellationToken stoppingToken)
    {
        using var activity = s_activitySource.StartActivity("Generate activity");
        activity?.SetTag("loadtest.resource", resource);
        activity?.SetTag("loadtest.path", path);
        var start = Stopwatch.GetTimestamp();

        try
        {
            using var client = httpClientFactory.CreateClient(resource);
            using var response = await client.GetAsync(path, stoppingToken);

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("GET {Resource}{Path} returned {StatusCode} in {ElapsedMilliseconds} ms.",
                    resource, path, (int)response.StatusCode, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            else
            {
                activity?.SetStatus(ActivityStatusCode.Error, response.ReasonPhrase);
                logger.LogWarning("GET {Resource}{Path} returned {StatusCode}.", resource, path, (int)response.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogWarning(ex, "GET {Resource}{Path} failed.", resource, path);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Request timed out.");
            logger.LogWarning("GET {Resource}{Path} timed out.", resource, path);
        }
    }
}

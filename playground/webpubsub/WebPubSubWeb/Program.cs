using Azure.Messaging.WebPubSub;

var builder = WebApplication.CreateBuilder(args);

builder.AddKeyedAzureWebPubSubServiceClient(Constants.ChatHubName);
builder.AddKeyedAzureWebPubSubServiceClient(Constants.NotificationHubName);

// add a background service to periodically broadcast messages to the client
builder.Services.AddHostedService<NotificationService>();

// Add services to the container.
builder.Services.AddRazorPages();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();

// return the Client Access URL with negotiate endpoint
app.MapGet("/negotiate/chat", ([FromKeyedServices(Constants.ChatHubName)] WebPubSubServiceClient service) =>
{
    return
        new
        {
            url = service.GetClientAccessUri(roles: ["webpubsub.sendToGroup.group1", "webpubsub.joinLeaveGroup.group1"]).AbsoluteUri
        };
});

app.MapGet("/negotiate/notification", ([FromKeyedServices(Constants.NotificationHubName)] WebPubSubServiceClient service) =>
{
    return
        new
        {
            url = service.GetClientAccessUri().AbsoluteUri
        };
});

// handle events for chat
app.Map($"/eventhandler/{Constants.ChatHubName}", async ([FromKeyedServices(Constants.ChatHubName)] WebPubSubServiceClient service, HttpContext context, ILogger<Program> logger) =>
{
    context.Response.Headers["WebHook-Allowed-Origin"] = "*";
    if (context.Request.Method == "OPTIONS")
    {
        context.Response.StatusCode = 200;
        return;
    }

    if (context.Request.Method != "POST" || !context.Request.Headers.TryGetValue("ce-type", out var eventType))
    {
        context.Response.StatusCode = 400;
        return;
    }
    context.Response.StatusCode = 200;
    var connectionId = context.Request.Headers["ce-connectionId"];
    if (eventType == "azure.webpubsub.sys.connected")
    {
        logger.LogInformation("[SYSTEM] {ConnectionId} joined.", connectionId.ToString());
        await service.SendToAllAsync($"[SYSTEM] {connectionId} joined.");
    }
});

app.Run();

sealed class NotificationService([FromKeyedServices(Constants.NotificationHubName)] WebPubSubServiceClient service) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(5000, stoppingToken);

            // periodically broadcast messages to the client
            await service.SendToAllAsync($"{DateTime.Now}: Hello from background service.");
        }
    }
}

static class Constants
{
    public const string ChatHubName = "ChatForAspire";
    public const string NotificationHubName = "NotificationForAspire";
}

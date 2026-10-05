using BasketService.Repositories;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddGrpc();

builder.AddRedisClient("basketcache");
builder.Services.AddTransient<IBasketRepository, RedisBasketRepository>();

builder.AddRabbitMQClient("messaging", configureSettings: settings => settings.DisableAutoActivation = false);

var app = builder.Build();

app.MapGrpcService<BasketService.BasketService>();
app.MapDefaultEndpoints();

app.Run();

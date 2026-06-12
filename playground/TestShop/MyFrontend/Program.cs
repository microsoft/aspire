using GrpcBasket;
using MyFrontend.Components;
using MyFrontend.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddHttpForwarderWithServiceDiscovery();

builder.Services.AddHttpClient<CatalogServiceClient>(c => c.BaseAddress = new("https+http://catalogservice"));

var isHttps = Environment.GetEnvironmentVariable("DOTNET_LAUNCHPROFILE") == "https";

builder.Services.AddSingleton<BasketServiceClient>()
                .AddGrpcClient<Basket.BasketClient>(o => o.Address = new($"{(isHttps ? "https" : "http")}://basketservice"));

builder.Services.AddRazorComponents();

// Configure antiforgery to work when embedded in the Aspire dashboard iframe (cross-origin).
// SameSite=None allows the cookie to be sent in cross-origin iframe requests.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddAntiforgery(options =>
    {
        options.SuppressXFrameOptionsHeader = true;
        options.Cookie.SameSite = SameSiteMode.None;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseAntiforgery();

app.MapRazorComponents<App>();

app.MapForwarder("/catalog/images/{id}", "https+http://catalogservice", "/api/v1/catalog/items/{id}/image");

app.MapDefaultEndpoints();

app.Run();

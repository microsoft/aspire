using Aspire_StarterApplication._1.Web;
using Aspire_StarterApplication._1.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();
#if (UseRedisCache)
builder.AddRedisOutputCache("cache");
#endif

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

#if (!UseRedisCache)
builder.Services.AddOutputCache();

#endif
#if (HasHttpsProfile)
builder.Services.AddHttpClient<WeatherApiClient>(client =>
    {
        // This URL uses "https+http://" to indicate HTTPS is preferred over HTTP.
        // Learn more about service discovery scheme resolution at https://aka.ms/dotnet/sdschemes.
        client.BaseAddress = new("https+http://apiservice");
    });
#else
builder.Services.AddHttpClient<WeatherApiClient>(client => client.BaseAddress = new("http://apiservice"));
#endif

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
#if (HasHttpsProfile)
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
#endif
}

#if (HasHttpsProfile)
app.UseHttpsRedirection();

#endif
#if (Framework == "net8.0")
app.UseStaticFiles();
#endif
app.UseAntiforgery();

app.UseOutputCache();

#if (Framework != "net8.0")
app.MapStaticAssets();

#endif
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();

using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire components.
builder.AddServiceDefaults();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.MapDefaultEndpoints();

app.UseHttpsRedirection();

app.MapGet("/currenttime", () =>
{
    return new TimeResponse(DateTimeOffset.UtcNow, TimeZoneInfo.Local.DisplayName);
})
.WithName("GetCurrentTime");

app.Run();

sealed record TimeResponse(DateTimeOffset UtcNow, string TimeZone)
{
    public string LocalTime => UtcNow.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    public string Date => UtcNow.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

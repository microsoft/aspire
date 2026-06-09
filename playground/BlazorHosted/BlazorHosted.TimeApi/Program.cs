using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var app = builder.Build();

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

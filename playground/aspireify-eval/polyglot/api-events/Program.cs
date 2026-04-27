var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var events = new[]
{
    new { Id = 1, City = "seattle", Name = "Aspire Community Standup", Date = "2026-04-15" },
    new { Id = 2, City = "new-york", Name = ".NET Conf Local", Date = "2026-05-01" },
    new { Id = 3, City = "san-francisco", Name = "DevOps Days SF", Date = "2026-04-22" },
    new { Id = 4, City = "chicago", Name = "Cloud Native Chicago", Date = "2026-05-10" },
};

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "api-events" }));

app.MapGet("/events", () => events);

app.MapGet("/events/{city}", (string city) =>
    events.Where(e => e.City.Equals(city, StringComparison.OrdinalIgnoreCase)).ToArray());

app.Run();

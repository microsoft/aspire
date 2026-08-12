using BoardData;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Database — same connection string pattern as the API
var dbConnStr = Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? "Host=localhost;Port=5432;Database=boardapp;Username=postgres;Password=localdev123";
builder.Services.AddDbContext<BoardDbContext>(options => options.UseNpgsql(dbConnStr));

// Admin auth token
var adminSecret = Environment.GetEnvironmentVariable("ADMIN_SECRET")
    ?? throw new InvalidOperationException("ADMIN_SECRET must be set");

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();

app.MapGet("/admin/health", () => Results.Ok(new { status = "healthy", role = "admin" }));

app.MapGet("/admin/stats", async (BoardDbContext db) =>
{
    var itemCount = await db.BoardItems.CountAsync();
    var userCount = await db.UserProfiles.CountAsync();
    return Results.Ok(new { items = itemCount, users = userCount });
});

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();

using BoardData;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

var dbConnStr = Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? "Host=localhost;Port=5432;Database=boardapp;Username=postgres;Password=localdev123";
builder.Services.AddDbContext<BoardDbContext>(options => options.UseNpgsql(dbConnStr));

var host = builder.Build();

// Run migrations and seed data, then exit
using var scope = host.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

Console.WriteLine("Running database migrations...");
await db.Database.EnsureCreatedAsync();

// Seed some initial data if empty
if (!await db.BoardItems.AnyAsync())
{
    db.BoardItems.AddRange(
        new BoardItem { Title = "Set up project", Description = "Initial project scaffolding", IsComplete = true },
        new BoardItem { Title = "Add authentication", Description = "Implement user login flow" },
        new BoardItem { Title = "Deploy to production", Description = "Set up CI/CD pipeline" }
    );

    db.UserProfiles.Add(new UserProfile { DisplayName = "Admin", Email = "admin@boardapp.local", Role = "admin" });

    await db.SaveChangesAsync();
    Console.WriteLine("Seeded initial data.");
}

Console.WriteLine("Migrations complete.");

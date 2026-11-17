// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

int insertionRows = builder.Configuration.GetValue<int>("InsertionRows", 1);

builder.AddServiceDefaults();

builder.AddSqlServerDbContext<MyDbContext>("db");

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapGet("/parameters", (IConfiguration configuration) => new
{
    optionalUnset = configuration["OPTIONAL_UNSET"] ?? "(not set)",
    optionalConfigured = configuration["OPTIONAL_CONFIGURED"] ?? "(not set)",
    programmaticValue = configuration["PROGRAMMATIC_VALUE"] ?? "(not set)",
    requiredAfterOptional = configuration["REQUIRED_AFTER_OPTIONAL"] ?? "(not set)",
});
app.MapGet("/", async (MyDbContext context) =>
{
    // You wouldn't normally do this on every call,
    // but doing it here just to make this simple.
    context.Database.EnsureCreated();

    for (var i = 0; i < insertionRows; i++)
    {
        var entry = new Entry();
        await context.Entries.AddAsync(entry);
    }

    await context.SaveChangesAsync();

    var entries = await context.Entries.ToListAsync();

    return new
    {
        totalEntries = entries.Count,
        entries = entries
    };
});

app.Run();

public class MyDbContext(DbContextOptions<MyDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Entry>().HasKey(e => e.Id);
    }

    public DbSet<Entry> Entries { get; set; }
}

public class Entry
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

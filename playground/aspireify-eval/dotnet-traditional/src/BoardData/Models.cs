using Microsoft.EntityFrameworkCore;

namespace BoardData;

public class BoardDbContext(DbContextOptions<BoardDbContext> options) : DbContext(options)
{
    public DbSet<BoardItem> BoardItems => Set<BoardItem>();
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
}

public class BoardItem
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public bool IsComplete { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class UserProfile
{
    public int Id { get; set; }
    public required string DisplayName { get; set; }
    public required string Email { get; set; }
    public string Role { get; set; } = "user";
}

#!/usr/bin/env dotnet

#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net10.0
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:package Npgsql

using System.Text.Json;
using Npgsql;

Console.WriteLine("Starting RPS Arena Game Master...");

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Connection string injected by Aspire via environment variable
var connectionString = app.Configuration.GetConnectionString("gamedb");

// Initialize database on startup
if (connectionString is not null)
{
    await InitializeDatabaseAsync(connectionString);
}

app.MapGet("/health", () => Results.Ok("healthy"));

app.MapGet("/api/leaderboard", async () =>
{
    if (connectionString is null)
    {
        return Results.Problem("Database not configured");
    }

    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();

    await using var cmd = new NpgsqlCommand(
        """
        SELECT player_name,
               COUNT(*) FILTER (WHERE result = 'win') as wins,
               COUNT(*) FILTER (WHERE result = 'loss') as losses,
               COUNT(*) FILTER (WHERE result = 'draw') as draws,
               COUNT(*) as total_rounds
        FROM (
            SELECT player1_name as player_name,
                   CASE
                       WHEN winner = player1_name THEN 'win'
                       WHEN winner = 'draw' THEN 'draw'
                       ELSE 'loss'
                   END as result
            FROM rounds
            UNION ALL
            SELECT player2_name as player_name,
                   CASE
                       WHEN winner = player2_name THEN 'win'
                       WHEN winner = 'draw' THEN 'draw'
                       ELSE 'loss'
                   END as result
            FROM rounds
        ) stats
        GROUP BY player_name
        ORDER BY wins DESC
        """, conn);

    await using var reader = await cmd.ExecuteReaderAsync();
    var leaderboard = new List<LeaderboardEntry>();
    while (await reader.ReadAsync())
    {
        leaderboard.Add(new LeaderboardEntry
        {
            PlayerName = reader.GetString(0),
            Wins = reader.GetInt32(1),
            Losses = reader.GetInt32(2),
            Draws = reader.GetInt32(3),
            TotalRounds = reader.GetInt32(4)
        });
    }

    return Results.Ok(leaderboard);
});

app.MapGet("/api/rounds", async (int? limit) =>
{
    if (connectionString is null)
    {
        return Results.Problem("Database not configured");
    }

    var take = Math.Min(limit ?? 20, 100);
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();

    await using var cmd = new NpgsqlCommand(
        "SELECT id, player1_name, player1_move, player2_name, player2_move, winner, played_at FROM rounds ORDER BY played_at DESC LIMIT @take", conn);
    cmd.Parameters.AddWithValue("take", take);

    await using var reader = await cmd.ExecuteReaderAsync();
    var rounds = new List<RoundResult>();
    while (await reader.ReadAsync())
    {
        rounds.Add(new RoundResult
        {
            Id = reader.GetInt32(0),
            Player1Name = reader.GetString(1),
            Player1Move = reader.GetString(2),
            Player2Name = reader.GetString(3),
            Player2Move = reader.GetString(4),
            Winner = reader.GetString(5),
            PlayedAt = reader.GetDateTime(6)
        });
    }

    return Results.Ok(rounds);
});

app.MapGet("/api/players", async (IConfiguration config) =>
{
    using var httpClient = new HttpClient();

    var pythonPlayerUrl = config.GetValue<string>("PYTHON_PLAYER_HTTP");
    var nodePlayerUrl = config.GetValue<string>("NODE_PLAYER_HTTP");
    var csharpPlayerUrl = config.GetValue<string>("CSHARP_PLAYER_HTTP");

    if (pythonPlayerUrl is null || nodePlayerUrl is null || csharpPlayerUrl is null)
    {
        return Results.Problem("Player services not configured");
    }

    var urls = new[] { pythonPlayerUrl, nodePlayerUrl, csharpPlayerUrl };
    var tasks = urls.Select(async url =>
    {
        try
        {
            var res = await httpClient.GetAsync($"{url}/api/info");
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsStringAsync();
        }
        catch
        {
            return "{}";
        }
    }).ToArray();

    var results = await Task.WhenAll(tasks);
    return Results.Text($"[{string.Join(",", results)}]", "application/json");
});

app.MapDelete("/api/rounds", async () =>
{
    if (connectionString is null)
    {
        return Results.Problem("Database not configured");
    }

    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();

    await using var cmd = new NpgsqlCommand("DELETE FROM rounds", conn);
    var deleted = await cmd.ExecuteNonQueryAsync();

    return Results.Ok(new { message = "History cleared", deletedRounds = deleted });
});

app.MapPost("/api/rounds/play", async (IConfiguration config) =>
{
    if (connectionString is null)
    {
        return Results.Problem("Database not configured");
    }

    using var httpClient = new HttpClient();

    // Get endpoints from Aspire-injected environment variables
    var pythonPlayerUrl = config.GetValue<string>("PYTHON_PLAYER_HTTP");
    var nodePlayerUrl = config.GetValue<string>("NODE_PLAYER_HTTP");
    var csharpPlayerUrl = config.GetValue<string>("CSHARP_PLAYER_HTTP");

    if (pythonPlayerUrl is null || nodePlayerUrl is null || csharpPlayerUrl is null)
    {
        return Results.Problem($"Player services not configured. Python: {pythonPlayerUrl}, Node: {nodePlayerUrl}, C#: {csharpPlayerUrl}");
    }

    // All three players
    var players = new (string Url, string Fallback)[] {
        (pythonPlayerUrl, "Python Serpent"),
        (nodePlayerUrl, "Node Knight"),
        (csharpPlayerUrl, "C# Paladin")
    };

    // Ask all players for their moves simultaneously
    var moveTasks = players.Select(p => GetPlayerMove(httpClient, p.Url, p.Fallback)).ToArray();
    await Task.WhenAll(moveTasks);
    var moves = moveTasks.Select(t => t.Result).ToArray();

    // Round-robin: each pair plays each other (3 matches per round)
    var results = new List<RoundResult>();

    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();

    for (var i = 0; i < moves.Length; i++)
    {
        for (var j = i + 1; j < moves.Length; j++)
        {
            var p1 = moves[i];
            var p2 = moves[j];
            var winner = DetermineWinner(p1.PlayerName, p1.Move, p2.PlayerName, p2.Move);

            await using var cmd = new NpgsqlCommand(
                "INSERT INTO rounds (player1_name, player1_move, player2_name, player2_move, winner) VALUES (@p1name, @p1move, @p2name, @p2move, @winner) RETURNING id, played_at",
                conn);
            cmd.Parameters.AddWithValue("p1name", p1.PlayerName);
            cmd.Parameters.AddWithValue("p1move", p1.Move);
            cmd.Parameters.AddWithValue("p2name", p2.PlayerName);
            cmd.Parameters.AddWithValue("p2move", p2.Move);
            cmd.Parameters.AddWithValue("winner", winner);

            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();

            results.Add(new RoundResult
            {
                Id = reader.GetInt32(0),
                Player1Name = p1.PlayerName,
                Player1Move = p1.Move,
                Player2Name = p2.PlayerName,
                Player2Move = p2.Move,
                Winner = winner,
                PlayedAt = reader.GetDateTime(1)
            });
        }
    }

    return Results.Ok(results);
});

app.Run();

static async Task<PlayerMoveResponse> GetPlayerMove(HttpClient client, string baseUrl, string fallbackName)
{
    var response = await client.PostAsync($"{baseUrl}/api/move", null);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync();
    return JsonSerializer.Deserialize(json, PlayerMoveResponseContext.Default.PlayerMoveResponse)
        ?? new PlayerMoveResponse { PlayerName = fallbackName, Move = "rock", Strategy = "fallback" };
}

static string DetermineWinner(string p1Name, string p1Move, string p2Name, string p2Move)
{
    if (p1Move == p2Move)
    {
        return "draw";
    }

    var p1Wins = (p1Move, p2Move) switch
    {
        ("rock", "scissors") => true,
        ("scissors", "paper") => true,
        ("paper", "rock") => true,
        _ => false
    };

    return p1Wins ? p1Name : p2Name;
}

static async Task InitializeDatabaseAsync(string connectionString)
{
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();

    await using var cmd = new NpgsqlCommand(
        """
        CREATE TABLE IF NOT EXISTS rounds (
            id SERIAL PRIMARY KEY,
            player1_name TEXT NOT NULL,
            player1_move TEXT NOT NULL,
            player2_name TEXT NOT NULL,
            player2_move TEXT NOT NULL,
            winner TEXT NOT NULL,
            played_at TIMESTAMP DEFAULT NOW()
        )
        """, conn);
    await cmd.ExecuteNonQueryAsync();
    Console.WriteLine("Database initialized: rounds table ready");
}

sealed class PlayerMoveResponse
{
    public string PlayerName { get; set; } = "";
    public string Move { get; set; } = "";
    public string Strategy { get; set; } = "";
}

sealed class RoundResult
{
    public int Id { get; set; }
    public string Player1Name { get; set; } = "";
    public string Player1Move { get; set; } = "";
    public string Player2Name { get; set; } = "";
    public string Player2Move { get; set; } = "";
    public string Winner { get; set; } = "";
    public DateTime PlayedAt { get; set; }
}

sealed class LeaderboardEntry
{
    public string PlayerName { get; set; } = "";
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int Draws { get; set; }
    public int TotalRounds { get; set; }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(PlayerMoveResponse))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
sealed partial class PlayerMoveResponseContext : System.Text.Json.Serialization.JsonSerializerContext;

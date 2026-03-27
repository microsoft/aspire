#!/usr/bin/env dotnet

#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net10.0
#:property JsonSerializerIsReflectionEnabledByDefault=true

// Rock Paper Scissors - C# Paladin Player
// A disciplined player that uses probability theory and streak detection.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var playerName = "C# Paladin";
var roundsPlayed = 0;
var opponentHistory = new List<string>();
string[] moves = ["rock", "paper", "scissors"];
var counter = new Dictionary<string, string>
{
    ["rock"] = "paper",
    ["paper"] = "scissors",
    ["scissors"] = "rock"
};

(string Move, string Strategy) ChooseMove()
{
    roundsPlayed++;

    // Strategy 1: First round - "Shield Wall" (always paper - blocks the classic rock opener)
    if (roundsPlayed <= 1)
    {
        return ("paper", "shield-wall");
    }

    // Strategy 2: Streak detection - if opponent repeated last move 2+ times, counter it
    if (opponentHistory.Count >= 2)
    {
        var last = opponentHistory[^1];
        var secondLast = opponentHistory[^2];
        if (last == secondLast)
        {
            // Opponent is on a streak - 70% chance to counter, 30% random
            if (Random.Shared.NextDouble() < 0.7)
            {
                return (counter[last], "streak-breaker");
            }
        }
    }

    // Strategy 3: Bayesian approach - counter least-played move's counter
    // (opponents tend to avoid moves that haven't worked)
    if (opponentHistory.Count >= 5)
    {
        var freq = moves.ToDictionary(m => m, m => opponentHistory.Count(h => h == m));
        var leastPlayed = freq.MinBy(kv => kv.Value).Key;
        // They'll likely play the counter to their least-played, so we counter that
        if (Random.Shared.NextDouble() < 0.5)
        {
            var predicted = counter[leastPlayed];
            return (counter[predicted], "bayesian-counter");
        }
    }

    // Strategy 4: "Holy Random" - pure randomness with divine guidance
    var move = moves[Random.Shared.Next(moves.Length)];
    return (move, "holy-random");
}

app.MapGet("/health", () => Results.Ok("healthy"));

app.MapGet("/api/info", () => Results.Ok(new
{
    playerName,
    language = "C#",
    strategies = new[] { "shield-wall", "streak-breaker", "bayesian-counter", "holy-random" },
    personality = "A disciplined paladin who uses probability theory and divine intuition",
    roundsPlayed,
    opponentsAnalyzed = opponentHistory.Count
}));

app.MapPost("/api/move", () =>
{
    var (move, strategy) = ChooseMove();
    return Results.Ok(new { playerName, move, strategy });
});

app.MapPost("/api/opponent-move", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    if (!string.IsNullOrEmpty(body))
    {
        var doc = System.Text.Json.JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("move", out var moveProp))
        {
            opponentHistory.Add(moveProp.GetString() ?? "");
        }
    }
    return Results.Ok(new { recorded = true });
});

app.Run();

// File-based C# app (`dotnet run ticker.cs`), watched like a project. Edits to Message() are hot reloaded; edits to the
// top-level loop restart it (ENC0118), as with `dotnet watch ticker.cs`.
while (true)
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {Ticker.Message()}");
    await Task.Delay(TimeSpan.FromSeconds(3));
}

static class Ticker
{
    public static string Message() => "ticker v1";
}

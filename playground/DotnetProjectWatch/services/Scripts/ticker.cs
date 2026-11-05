// File-based C# app (`dotnet run ticker.cs`), watched like a project.
while (true)
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ticker v1");
    await Task.Delay(TimeSpan.FromSeconds(3));
}

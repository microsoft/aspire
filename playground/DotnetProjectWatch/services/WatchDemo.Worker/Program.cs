using WatchDemo.Shared;

// Service discovery env injected by WithReference(api) and forwarded through the watch proxy.
var apiUrl = Environment.GetEnvironmentVariable("API_HTTP") ?? Environment.GetEnvironmentVariable("services__api__http__0");
Console.WriteLine($"worker started; api={apiUrl}");

using var http = new HttpClient();
while (true)
{
    string reply;
    try
    {
        reply = apiUrl is null ? "<no api url>" : await http.GetStringAsync(apiUrl);
    }
    catch (Exception ex)
    {
        reply = $"<error: {ex.Message}>";
    }

    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] worker sees: \"{Greeter.Message}\" | api says: \"{reply}\"");
    await Task.Delay(TimeSpan.FromSeconds(5));
}

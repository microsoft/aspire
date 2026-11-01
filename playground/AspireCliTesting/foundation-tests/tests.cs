#:property PublishAot=false

var serviceUrl = Environment.GetEnvironmentVariable("SERVICE_URL")
    ?? throw new InvalidOperationException("SERVICE_URL is required.");

using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var response = await client.GetStringAsync(serviceUrl);
if (response != "Aspire CLI testing")
{
    throw new InvalidOperationException($"Unexpected service response: {response}");
}

if (Environment.GetEnvironmentVariable("TEST_OUTPUT_PATH") is { } outputPath)
{
    var options = new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write
    };
    if (!OperatingSystem.IsWindows())
    {
        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    }

    await using var stream = new FileStream(Path.Combine(outputPath, "probe.txt"), options);
    await using var writer = new StreamWriter(stream);
    await writer.WriteAsync(response);
}

Console.WriteLine("Shared service probe passed.");

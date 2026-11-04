#!/usr/bin/env dotnet
// The recorder: a Playwright for .NET file-based app that records the clips of a video (see references/recording.md).
// Run it from the copy of the skill's scripts in the workspace (<tools>/recorder), outside any repository:
//   dotnet run <tools>/recorder/recorder.cs -- <command>
//
// Commands (VIDEO_ROOT is the workspace, VIDEO_BASE_URL the Aspire dashboard, https://localhost:17010 by default):
//   install                            installs the Chromium build of this Playwright version, once
//   setup                              logs in to the dashboard (VIDEO_LOGIN_URL) and saves the session in state.json
//   probe <path> <script>              runs a script on a page and prints the result (PROBE_SHOT=<png> saves a screenshot)
//   record <clip...>                   records clips into clips/<clip>, "all" for every clip, timed by durations.json
//
// The Playwright version is in ../Directory.Packages.props, with the other pinned versions.
#:package Microsoft.Playwright
#:property Nullable=disable
// File-based apps default to native AOT, which turns off reflection-based JSON: Playwright needs it.
#:property PublishAot=false
// The Playwright driver files exceed MAX_PATH under deep folders on Windows: build into a short path, in the user's
// own profile (a shared temporary folder, such as /tmp, would let other users replace what runs). The hash of the
// folder keeps the recorders of different workspaces apart.
#:property BaseOutputPath="$([System.IO.Path]::Combine($([System.Environment]::GetFolderPath(SpecialFolder.UserProfile)), '.aspire-video', 'recorder', $([MSBuild]::StableStringHash($(MSBuildProjectDirectory)).ToString('x8')), 'bin'))$([System.IO.Path]::DirectorySeparatorChar)"
#:include Scene.cs
#:include VsCode.cs
#:include Dashboard.cs
#:include Scenes.cs

using System.Text.Json;
using Microsoft.Playwright;
using Recorder;

var command = args.Length > 0 ? args[0] : throw new ArgumentException("Usage: recorder.cs install | setup | probe <path> <script> | record <clip...>");

if (command == "install")
{
    // The same as Playwright's "playwright.ps1 install chromium": downloads the browser build this Playwright version
    // is made for, and nothing else. Nothing installs it on its own: without it, the other commands fail and say so.
    return Microsoft.Playwright.Program.Main(["install", "chromium"]);
}

using var playwright = await Playwright.CreateAsync();
IBrowser browser;
try
{
    browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
}
catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.Ordinal))
{
    // Playwright's own message names playwright.ps1, which a file-based app doesn't have.
    Console.Error.WriteLine("The recorder's browser isn't installed: run \"dotnet run <tools>/recorder/recorder.cs -- install\".");
    return 1;
}

await using var _ = browser;

switch (command)
{
    case "setup":
        await SetupAsync(browser);
        break;

    case "probe":
        await ProbeAsync(browser, args[1], args[2]);
        break;

    case "record":
        var durations = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(Path.Combine(Scenes.Root, "durations.json")));
        await Scenes.RecordAsync(browser, Path.Combine(Scenes.Root, "clips"), durations, args.Skip(1).ToArray());
        break;
}

return 0;

static async Task SetupAsync(IBrowser browser)
{
    var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
    var page = await context.NewPageAsync();

    // The login URL that "aspire run" prints, with its token: https://localhost:17010/login?t=<token>.
    // Without it, the dashboard must allow anonymous access (see references/recording.md).
    var url = Scenes.LoginUrl ?? Scenes.BaseUrl + "/";

    // Waits for the dashboard to start, for up to 2 minutes.
    const int Attempts = 60;
    string last = null;

    for (var attempt = 1; ; attempt++)
    {
        try
        {
            var response = await page.GotoAsync(url);

            if (response is { Ok: true })
            {
                break;
            }

            last = response is null ? "no response" : $"HTTP {response.Status}";
        }
        catch (PlaywrightException ex)
        {
            last = ex.Message.Split('\n')[0];
        }

        if (attempt == Attempts)
        {
            throw new InvalidOperationException($"The dashboard at {Scenes.BaseUrl} didn't respond after {Attempts} attempts: {last}.");
        }

        await Task.Delay(2000);
    }

    await page.WaitForURLAsync(current => !current.Contains("/login", StringComparison.OrdinalIgnoreCase));

    await context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = Scenes.StatePath });
    Console.WriteLine($"Logged in to {Scenes.BaseUrl}.");
    await context.CloseAsync();
}

static async Task ProbeAsync(IBrowser browser, string path, string script)
{
    var context = await browser.NewContextAsync(new BrowserNewContextOptions
    {
        StorageStatePath = File.Exists(Scenes.StatePath) ? Scenes.StatePath : null,
        ViewportSize = new ViewportSize { Width = Scene.Width, Height = Scene.Height },
        DeviceScaleFactor = Scene.Scale,
        ColorScheme = ColorScheme.Dark,
        IgnoreHTTPSErrors = true,
    });
    var page = await context.NewPageAsync();
    await page.GotoAsync(Scenes.BaseUrl + path, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
    await Task.Delay(800);
    Console.WriteLine((await page.EvaluateAsync<JsonElement>(script)).ToString());

    if (Environment.GetEnvironmentVariable("PROBE_SHOT") is { Length: > 0 } shot)
    {
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = shot });
    }

    await context.CloseAsync();
}

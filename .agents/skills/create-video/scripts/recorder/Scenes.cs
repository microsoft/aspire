using Microsoft.Playwright;

namespace Recorder;

/// <summary>
/// The clips of the video. Replace the example clip with your own: one method per "clip" of storyboard.json, with
/// one <see cref="Scene.StepAsync"/> per narrated step, in the order of the storyboard.
/// </summary>
public static class Scenes
{
    // The Aspire dashboard of a throwaway sample app, on a fixed port: see references/recording.md.
    public static string BaseUrl { get; } = Environment.GetEnvironmentVariable("VIDEO_BASE_URL") ?? "https://localhost:17010";

    // The login URL that "aspire run" prints, with its token. Not needed when the dashboard allows anonymous access.
    public static string LoginUrl { get; } = Environment.GetEnvironmentVariable("VIDEO_LOGIN_URL");

    public static string Root { get; } = Environment.GetEnvironmentVariable("VIDEO_ROOT") ?? throw new InvalidOperationException("Set VIDEO_ROOT to the workspace.");

    public static string StatePath => Path.Combine(Root, "state.json");

    // The clips, in the order they are recorded: each one starts from the state the previous one left.
    private static readonly (string Name, Func<IBrowser, string, IReadOnlyDictionary<string, double>, Task> Record)[] s_clips =
    [
        ("02-resources", ResourcesAsync),
    ];

    public static async Task RecordAsync(IBrowser browser, string outputRoot, IReadOnlyDictionary<string, double> durations, string[] names)
    {
        var selected = names is ["all"] ? s_clips : s_clips.Where(clip => names.Contains(clip.Name)).ToArray();

        foreach (var (name, record) in selected)
        {
            Console.WriteLine($"Recording {name}");
            await record(browser, Path.Combine(outputRoot, name), durations);
        }
    }

    private static Task<Scene> NewSceneAsync(IBrowser browser, string directory, IReadOnlyDictionary<string, double> durations)
        => Scene.StartAsync(browser, Path.GetFileName(directory), BaseUrl, StatePath, Path.GetDirectoryName(directory), durations);

    // An example clip: points at a resource of the Resources page, then opens its console logs.
    private static async Task ResourcesAsync(IBrowser browser, string directory, IReadOnlyDictionary<string, double> durations)
    {
        await using var scene = await NewSceneAsync(browser, directory, durations);

        // Everything before StartCaptureAsync is not recorded: open the page, and get it ready.
        await scene.OpenAsync("/");
        await scene.StartCaptureAsync();

        // Check selectors with "recorder.cs probe" first: the dashboard's markup can change between versions. The name
        // cell, rather than the whole row: a row spans the page, too wide to zoom on.
        await scene.StepAsync("resources-1", async () =>
        {
            await scene.MoveToAsync(Dashboard.NameCell(scene, "apiservice"));
            await scene.HighlightAsync(Dashboard.NameCell(scene, "apiservice"), "A project resource");
        });

        await scene.ClearHighlightsAsync();

        await scene.StepAsync("resources-2", async () =>
        {
            await scene.ClickAsync(scene.Page.GetByRole(AriaRole.Link, new() { Name = "Console" }), pauseAfter: 900);
        });
    }
}

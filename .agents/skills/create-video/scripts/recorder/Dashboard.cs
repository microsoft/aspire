using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Recorder;

/// <summary>
/// The Aspire dashboard: its resources, and the app behind it. Check every selector with "recorder.cs probe" first:
/// the dashboard's markup changes between versions.
/// </summary>
public static class Dashboard
{
    /// <summary>The row of a resource on the Resources page, by its exact name.</summary>
    public static ILocator Row(Scene scene, string resource)
        => scene.Page.GetByRole(AriaRole.Row)
            // The name, then whitespace or the end of the cell's text: a word boundary (\b) would also match before a
            // hyphen, so "apiservice" would find "apiservice-worker" too.
            .Filter(new() { Has = scene.Page.GetByRole(AriaRole.Gridcell).First.Filter(new() { HasTextRegex = new Regex($"^\\s*{Regex.Escape(resource)}(\\s|$)") }) })
            .First;

    // The columns of the Resources page: Name, State, Start time, Source, URLs, Actions.

    /// <summary>
    /// The name cell of a resource: what to highlight to point at it. A whole row spans the page, too wide to zoom on.
    /// </summary>
    public static ILocator NameCell(Scene scene, string resource) => Row(scene, resource).GetByRole(AriaRole.Gridcell).First;

    /// <summary>The state cell of a resource ("Running", with a green check when it's healthy).</summary>
    public static ILocator StateCell(Scene scene, string resource) => Row(scene, resource).GetByRole(AriaRole.Gridcell).Nth(1);

    /// <summary>
    /// Dismisses the banner that says the endpoints accept anonymous access, which a sample app run with
    /// ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS shows, so no login token appears on camera.
    /// </summary>
    public static Task DismissUnsecuredBannerAsync(Scene scene)
        => scene.Page.EvaluateAsync("""
            () => {
              const bar = [...document.querySelectorAll('div')].find(d => d.textContent.trim().startsWith('Endpoint is unsecured') && d.querySelector('fluent-button, button'));
              const buttons = bar ? bar.querySelectorAll('fluent-button, button') : [];
              if (buttons.length) buttons[buttons.length - 1].click();
            }
            """);

    /// <summary>
    /// Waits until the app's resources are healthy, with "aspire wait" from the app's folder, such as when the app was
    /// started in a clip, by "aspire run" in VS Code's terminal.
    /// </summary>
    public static async Task WaitForHealthyAsync(string appFolder, IEnumerable<string> resources)
    {
        foreach (var resource in resources)
        {
            var (exitCode, output) = await AspireAsync(appFolder, "wait", resource, "--status", "healthy");
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"{resource} didn't become healthy:\n{output}");
            }
        }
    }

    /// <summary>Runs an Aspire CLI command from the app's folder, such as "stop", and returns its exit code and output.</summary>
    public static async Task<(int ExitCode, string Output)> AspireAsync(string appFolder, params string[] arguments)
    {
        var info = new ProcessStartInfo("aspire")
        {
            WorkingDirectory = appFolder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // The Aspire CLI writes UTF-8 (it sets Console.OutputEncoding): decoding it with the console's code page
            // (on Windows, often not UTF-8) would garble its output.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.ArgumentList.Add("--nologo");
        info.ArgumentList.Add("--non-interactive");
        using var process = Process.Start(info)!;

        // Reads both streams at once: a full stderr pipe would block the process while stdout is read.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(output, error, process.WaitForExitAsync());
        return (process.ExitCode, output.Result + error.Result);
    }
}

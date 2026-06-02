// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only.

internal static class InteractionPages
{
    private const string LogoAssetRoute = "aspire-logo.svg";

    public static void Register(IServiceProvider services)
    {
        var interactionService = services.GetRequiredService<IInteractionService>();

        RegisterHelloWorldPage(interactionService);
        RegisterMarkdownPage(interactionService);
        RegisterCounterPage(interactionService);

        interactionService.RegisterMenuButton(new MenuButtonOptions
        {
            IconName = "ThisIconDoesNotExist",
            Text = "Invalid Icon",
            Url = "/pages/hello-world"
        });

        interactionService.RegisterMenuButton(new MenuButtonOptions
        {
            IconName = "Document",
            Text = "This is an extremely long menu button text that should stress test the nav menu layout and overflow behavior",
            Url = "/pages/hello-world"
        });
    }

    private static void RegisterCounterPage(IInteractionService interactionService)
    {
        var resetRequested = 0;

        interactionService.RegisterPage("counter", new ContentPageOptions
        {
            Title = "Counter",
            Actions = new Dictionary<string, Func<ActionContext, Task>>(StringComparer.OrdinalIgnoreCase)
            {
                ["reset-count"] = _ =>
                {
                    Interlocked.Exchange(ref resetRequested, 1);
                    return Task.CompletedTask;
                }
            },
            OnVisit = async visitContext =>
            {
                var count = 0;
                while (!visitContext.CancellationToken.IsCancellationRequested)
                {
                    await visitContext.RenderAsync(
                        $"""
                        # Counter

                        Current count: **{count}**

                        Updates every second.

                        [Reset Counter](type=button action=reset-count)
                        """, visitContext.CancellationToken);

                    await Task.Delay(1000, visitContext.CancellationToken);

                    if (Interlocked.CompareExchange(ref resetRequested, 0, 1) == 1)
                    {
                        count = 0;
                    }
                    else
                    {
                        count++;
                    }
                }
            }
        });

        interactionService.RegisterMenuButton(new MenuButtonOptions
        {
            IconName = "NumberSymbol",
            Text = "Counter",
            Url = "/pages/counter"
        });
    }

    private static void RegisterMarkdownPage(IInteractionService interactionService)
    {
        var markdownShowcase = LoadEmbeddedTextResource("MarkdownShowcase.txt");

        interactionService.RegisterPage("markdown", new ContentPageOptions
        {
            Title = "Markdown Showcase",
            OnVisit = async visitContext =>
            {
                await visitContext.RenderAsync(markdownShowcase, visitContext.CancellationToken);
            }
        });

        interactionService.RegisterMenuButton(new MenuButtonOptions
        {
            IconName = "Document",
            Text = "Markdown",
            Url = "/pages/markdown"
        });
    }

    private static void RegisterHelloWorldPage(IInteractionService interactionService)
    {
        interactionService.RegisterMenuButton(new MenuButtonOptions
        {
            IconName = "Book",
            Text = "Hello world",
            Url = "/pages/hello-world"
        });

        interactionService.RegisterPage("hello-world", new ContentPageOptions
        {
            Title = "Hello world",
            OnVisit = async visitContext =>
            {
                var host = visitContext.Services.GetRequiredService<IHostEnvironment>();
                var appName = host.ApplicationName;

                await visitContext.RenderAsync(
                    $"""
                    Hello **{appName}**

                    ![Aspire](/assets/{LogoAssetRoute})
                    """, visitContext.CancellationToken);
            }
        });

        var logoBytes = LoadEmbeddedBinaryResource("AspireLogo.svg");
        interactionService.RegisterAsset(LogoAssetRoute, "image/svg+xml", logoBytes);
    }

    private static string LoadEmbeddedTextResource(string fileName)
    {
        using var stream = OpenEmbeddedResource(fileName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static byte[] LoadEmbeddedBinaryResource(string fileName)
    {
        using var stream = OpenEmbeddedResource(fileName);
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    private static Stream OpenEmbeddedResource(string fileName)
    {
        var resourceName = $"Stress.AppHost.Resources.{fileName}";
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"{fileName} embedded resource not found.");
    }
}

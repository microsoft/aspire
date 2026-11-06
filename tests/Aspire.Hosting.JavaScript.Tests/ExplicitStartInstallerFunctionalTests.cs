// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.JavaScript.Tests;

[RequiresTools(["node", "npm"])]
public class ExplicitStartInstallerFunctionalTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task ExplicitStartAppDefersInstallerUntilAppIsStarted()
    {
        var appPath = Directory.CreateTempSubdirectory("aspire-js-explicit-start").FullName;
        var installMarkerPath = Path.Combine(appPath, "installs.txt");

        try
        {
            File.WriteAllText(Path.Combine(appPath, "app.js"),
                """
                const http = require('http');
                http.createServer((req, res) => res.end('Hello from npm!')).listen(process.env.PORT);
                """);

            // npm runs the root package's postinstall script, which records each installer run.
            File.WriteAllText(Path.Combine(appPath, "package.json"),
                """
                {
                    "name": "explicit-start-installer-test",
                    "version": "1.0.0",
                    "scripts": {
                        "start": "node app.js",
                        "postinstall": "node -e \"require('fs').appendFileSync('installs.txt', 'x')\""
                    }
                }
                """);

            using var builder = TestDistributedApplicationBuilder.Create()
                .WithTestAndResourceLogging(testOutputHelper);

            var npmApp = builder.AddJavaScriptApp("npmapp", appPath, "start")
                .WithNpm(install: true)
                .WithHttpEndpoint(env: "PORT")
                .WithExplicitStart();

            await using var app = builder.Build();
            var notifications = app.ResourceNotifications;
            var installerName = $"{npmApp.Resource.Name}-installer";
            var token = TestContext.Current.CancellationToken;

            await app.StartAsync(token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

            await notifications.WaitForResourceAsync(npmApp.Resource.Name, KnownResourceStates.NotStarted, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
            var installerEvent = await notifications.WaitForResourceAsync(installerName, e => !string.IsNullOrEmpty(e.Snapshot.State?.Text), token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
            Assert.Equal(KnownResourceStates.NotStarted, installerEvent.Snapshot.State?.Text);
            Assert.False(File.Exists(installMarkerPath));

            var startResult = await app.ResourceCommands.ExecuteCommandAsync(npmApp.Resource, KnownResourceCommands.StartCommand, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
            Assert.True(startResult.Success, startResult.Message);

            await notifications.WaitForResourceAsync(installerName, e => e.Snapshot.State?.Text == KnownResourceStates.Finished && e.Snapshot.ExitCode == 0, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
            await notifications.WaitForResourceAsync(npmApp.Resource.Name, KnownResourceStates.Running, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
            Assert.Equal("x", File.ReadAllText(installMarkerPath));

            using (var client = app.CreateHttpClient(npmApp.Resource.Name, "http"))
            {
                Assert.Equal("Hello from npm!", await client.GetStringAsync("/", token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan));
            }

            // Restarting the app must not rerun the installer that already completed.
            var stopResult = await app.ResourceCommands.ExecuteCommandAsync(npmApp.Resource, KnownResourceCommands.StopCommand, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
            Assert.True(stopResult.Success, stopResult.Message);
            await notifications.WaitForResourceAsync(npmApp.Resource.Name, e => KnownResourceStates.TerminalStates.Contains(e.Snapshot.State?.Text), token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

            startResult = await app.ResourceCommands.ExecuteCommandAsync(npmApp.Resource, KnownResourceCommands.StartCommand, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
            Assert.True(startResult.Success, startResult.Message);
            await notifications.WaitForResourceAsync(npmApp.Resource.Name, KnownResourceStates.Running, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
            Assert.Equal("x", File.ReadAllText(installMarkerPath));

            await app.StopAsync(token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        }
        finally
        {
            try
            {
                Directory.Delete(appPath, recursive: true);
            }
            catch
            {
                // Don't fail the test if the temporary folder can't be cleaned.
            }
        }
    }
}

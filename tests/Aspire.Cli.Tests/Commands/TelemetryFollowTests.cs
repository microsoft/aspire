// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Cli.Tests.Commands;

public class TelemetryFollowTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("logs", false, false)]
    [InlineData("logs", false, true)]
    [InlineData("logs", true, false)]
    [InlineData("logs", true, true)]
    [InlineData("spans", false, false)]
    [InlineData("spans", false, true)]
    [InlineData("spans", true, false)]
    [InlineData("spans", true, true)]
    public async Task Follow_ExpectedDisconnect_ReturnsSuccessAndWritesStatusToStderr(string commandName, bool standalone, bool connectionReset)
    {
        var exception = connectionReset
            ? new IOException("Connection reset.", new SocketException((int)SocketError.ConnectionReset))
            : new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");

        var (exitCode, interactionService) = await InvokeAsync(commandName, standalone, follow: true, exception);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interactionService.DisplayedErrors);
        Assert.Collection(interactionService.DisplayedRawText,
            output =>
            {
                Assert.Equal("[]", output.Text);
                Assert.Equal(ConsoleOutput.Standard, output.ConsoleOverride);
            },
            output =>
            {
                Assert.Equal(TelemetryCommandStrings.DashboardConnectionLost, output.Text);
                Assert.Equal(ConsoleOutput.Error, output.ConsoleOverride);
            });
    }

    [Theory]
    [InlineData("logs", true)]
    [InlineData("logs", false)]
    [InlineData("spans", true)]
    [InlineData("spans", false)]
    public async Task Follow_UnexpectedReadFailure_ReturnsFailure(string commandName, bool invalidResponse)
    {
        IOException exception = invalidResponse
            ? new HttpIOException(HttpRequestError.InvalidResponse, "Invalid response.")
            : new IOException("Unexpected read failure.");

        var (exitCode, interactionService) = await InvokeAsync(commandName, standalone: true, follow: true, exception);

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Single(interactionService.DisplayedErrors);
        Assert.Collection(interactionService.DisplayedRawText,
            output =>
            {
                Assert.Equal("[]", output.Text);
                Assert.Equal(ConsoleOutput.Standard, output.ConsoleOverride);
            });
    }

    [Theory]
    [InlineData("logs")]
    [InlineData("spans")]
    public async Task Snapshot_TruncatedResponse_ReturnsFailure(string commandName)
    {
        var exception = new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");

        var (exitCode, interactionService) = await InvokeAsync(commandName, standalone: true, follow: false, exception);

        Assert.NotEqual(CliExitCodes.Success, exitCode);
        Assert.Single(interactionService.DisplayedErrors);
        Assert.Empty(interactionService.DisplayedRawText);
    }

    [Theory]
    [InlineData("logs")]
    [InlineData("spans")]
    public async Task Follow_InvalidTelemetry_ReturnsFailure(string commandName)
    {
        var exception = new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");

        var (exitCode, interactionService) = await InvokeAsync(commandName, standalone: true, follow: true, exception, "invalid json\n");

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Single(interactionService.DisplayedErrors);
        Assert.Empty(interactionService.DisplayedRawText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadLinesWithDisconnectHandling_Cancellation_Propagates(bool disconnect)
    {
        using var cts = new CancellationTokenSource();
        Exception exception = disconnect
            ? new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.")
            : new OperationCanceledException(cts.Token);
        using var stream = new FaultingReadStream([], exception);
        using var reader = new StreamReader(stream);
        var interactionService = new TestInteractionService();
        await using var enumerator = reader.ReadLinesWithDisconnectHandlingAsync(interactionService, cts.Token).GetAsyncEnumerator();

        if (disconnect)
        {
            cts.Cancel();
            Assert.False(await enumerator.MoveNextAsync());
        }
        else
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        }

        Assert.Empty(interactionService.DisplayedRawText);
    }

    [Fact]
    public async Task ReadLinesWithDisconnectHandling_CompleteResponse_YieldsLinesWithoutDisconnectStatus()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("first\n\nsecond\n"));
        using var reader = new StreamReader(stream);
        var interactionService = new TestInteractionService();
        var lines = new List<string>();

        await foreach (var line in reader.ReadLinesWithDisconnectHandlingAsync(interactionService, CancellationToken.None))
        {
            lines.Add(line);
        }

        Assert.Equal(["first", "second"], lines);
        Assert.Empty(interactionService.DisplayedRawText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadLines_WithoutErrorHandler_PropagatesReadFailure(bool explicitNull)
    {
        var exception = new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
        using var stream = new FaultingReadStream([], exception);
        using var reader = new StreamReader(stream);
        var lines = explicitNull
            ? reader.ReadLinesAsync(onError: null, CancellationToken.None)
            : reader.ReadLinesAsync(CancellationToken.None);
        await using var enumerator = lines.GetAsyncEnumerator();

        var thrown = await Assert.ThrowsAsync<HttpIOException>(async () => await enumerator.MoveNextAsync());

        Assert.Same(exception, thrown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadLines_ErrorHandler_ControlsReadFailureHandling(bool handled)
    {
        var exception = new IOException("Read failure.");
        using var stream = new FaultingReadStream(Encoding.UTF8.GetBytes("first\n\nsecond\n"), exception);
        using var reader = new StreamReader(stream);
        var errors = new List<Exception>();
        await using var enumerator = reader.ReadLinesAsync(ex =>
        {
            errors.Add(ex);
            return handled;
        }, CancellationToken.None).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("first", enumerator.Current);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("second", enumerator.Current);
        Assert.Empty(errors);

        if (handled)
        {
            Assert.False(await enumerator.MoveNextAsync());
            Assert.False(await enumerator.MoveNextAsync());
        }
        else
        {
            var thrown = await Assert.ThrowsAsync<IOException>(async () => await enumerator.MoveNextAsync());
            Assert.Same(exception, thrown);
        }

        Assert.Collection(errors, error => Assert.Same(exception, error));
    }

    [Fact]
    public async Task ReadLines_ErrorHandlerFailure_Propagates()
    {
        using var stream = new FaultingReadStream([], new IOException("Read failure."));
        using var reader = new StreamReader(stream);
        var exception = new InvalidOperationException("Error handler failure.");
        await using var enumerator = reader.ReadLinesAsync(_ => throw exception, CancellationToken.None).GetAsyncEnumerator();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await enumerator.MoveNextAsync());

        Assert.Same(exception, thrown);
    }

    [Fact]
    public async Task ReadLines_CompleteResponse_YieldsLines()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("first\n\nsecond\n"));
        using var reader = new StreamReader(stream);
        var lines = new List<string>();

        await foreach (var line in reader.ReadLinesAsync(CancellationToken.None))
        {
            lines.Add(line);
        }

        Assert.Equal(["first", "second"], lines);
    }

    private async Task<(int ExitCode, TestInteractionService InteractionService)> InvokeAsync(
        string commandName, bool standalone, bool follow, Exception exception, string data = "{}\n")
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService();
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("test", new TestAppHostAuxiliaryBackchannel
        {
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "Test.AppHost.csproj"),
                ProcessId = Environment.ProcessId
            },
            DashboardInfoResponse = new GetDashboardInfoResponse
            {
                ApiBaseUrl = "http://localhost:18888",
                ApiToken = "test-token",
                DashboardUrls = ["http://localhost:18888"],
                IsHealthy = true
            }
        });

        using var handler = new MockHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/telemetry/resources")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json")
                };
            }

            var content = new StreamContent(new FaultingReadStream(Encoding.UTF8.GetBytes(data), exception));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new MockHttpClientFactory(handler)));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var parsed = command.Parse($"otel {commandName} --format Json{(follow ? " --follow" : "")}{(standalone ? " --dashboard-url http://localhost:18888" : "")}");
        var exitCode = await parsed.InvokeAsync().DefaultTimeout();

        return (exitCode, interactionService);
    }
}

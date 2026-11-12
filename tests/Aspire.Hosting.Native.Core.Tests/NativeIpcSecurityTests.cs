// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Aspire.Hosting.Native.Client;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Rpc;
using Microsoft.Extensions.Logging.Abstractions;
using NativeApplicationServer = Aspire.Hosting.Native.Api.NativeApplicationServer;
using static Aspire.Hosting.Native.Core.Tests.TestServices.RpcTestClient;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeIpcSecurityTests
{
    [Fact]
    public async Task AnotherOsUserCannotConnectToAnyPrivateControlChannel()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The cross-user socket probe requires Unix.");
            return;
        }
        var python = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_CROSS_USER_TEST_PYTHON");
        if (python is null)
        {
            Assert.Skip("Requires a Unix Python executable and noninteractive user switching supplied by native Linux validation.");
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var directory = Directory.CreateTempSubdirectory("native-ipc-user-");
        try
        {
            // A public canary proves the second user can make AF_UNIX connections;
            // a missing interpreter or blanket transport failure must not count as denial.
            File.SetUnixFileMode(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            var canaryPath = Path.Combine(directory.FullName, "public.sock");
            using var canary = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            canary.Bind(new UnixDomainSocketEndPoint(canaryPath));
            File.SetUnixFileMode(canaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
            canary.Listen(1);
            using var rpcDirectory = new NativeSocketTestDirectory();
            using var rpc = new NativeRpcListener(new UnixDomainSocketEndPoint(rpcDirectory.SocketPath), Token, NullLogger.Instance);
            using var owner = new NativeSocketTestClient(rpc.EndPoint);
            await using var cli = NativeCliTestHost.Start(new NativeApplicationServer(), new(), () => new());
            Assert.Equal(["baseline.v2", "pipeline-steps.v2"], await cli.Rpc.InvokeAsync<string[]>("GetCapabilitiesAsync"));
            await using var auxiliary = new NativeAuxiliaryTestHost(new NativeApplicationServer());
            using var auxiliaryOwner = new NativeSocketTestClient(new UnixDomainSocketEndPoint(auxiliary.SocketPath));
            const string probe = """
                import os, socket, sys
                if os.geteuid() == os.stat(sys.argv[1]).st_uid:
                    raise RuntimeError("The probe must run as a different OS user")
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
                    client.connect(sys.argv[2])
                for path in sys.argv[3:]:
                    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
                        try:
                            client.connect(path)
                        except PermissionError:
                            continue
                        raise RuntimeError("A private control endpoint accepted another OS user")
                print("private endpoints denied")
                """;
            var start = new ProcessStartInfo(Environment.UserName == "root" ? "runuser" : "sudo")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (start.FileName == "sudo")
            {
                start.ArgumentList.Add("-n");
            }
            foreach (var argument in new[]
            {
                "-u", "nobody", "--", python, "-I", "-c", probe, directory.FullName, canaryPath,
                rpcDirectory.SocketPath, cli.SocketPath, auxiliary.SocketPath
            })
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The cross-user probe did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, await stderr);
                Assert.Equal("private endpoints denied", (await stdout).Trim());
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RealWorkloadsUseHostingEnvironmentInheritance(bool container, bool explicitRuntimeSetting)
    {
        var executable = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_SYMBOLIC_TEST_SERVER");
        var dcp = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_SYMBOLIC_TEST_DCP");
        var node = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_SYMBOLIC_TEST_NODE");
        if (executable is null || dcp is null || node is null)
        {
            Assert.Skip("Requires the published native server, DCP and Node supplied by the native validation lane.");
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        await using var host = await NativeClientTestHost.StartWithWorkloadsAsync(executable, dcp,
            new Dictionary<string, string>
            {
                ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
                ["DOTNET_LAUNCH_PROFILE"] = "host-profile",
                ["ASPNETCORE_ENVIRONMENT"] = "host-environment",
                ["DOTNET_ENVIRONMENT"] = "host-environment",
                ["ASPIRE_LOGLEVEL"] = "Debug",
                ["ASPNETCORE_URLS_EXTRA"] = "application-prefix",
                ["ASPIRE_PARAMETER_TEST_INPUT"] = "application-input",
                ["NATIVE_IPC_AMBIENT"] = "user-setting"
            });
        await using var client = await NativeClient.ConnectAsync(host.Endpoint, host.Token, TimeSpan.FromSeconds(15),
            TimeProvider.System, token);
        var workspace = await client.Server.CreateApplicationWorkspaceAsync(token);
        try
        {
            var revision = await workspace.BeginRevisionAsync(token);
            var resource = await revision.AddResourceAsync("environment-probe", "test/environment-probe", token);
            await workspace.CommitRevisionAsync(revision, token);
            var execution = await workspace.GetApplicationExecutionAsync(token);
            var owner = await client.Server.JoinResourceExecutionAsync(
                await execution.InviteResourceExecutionAsync(resource, token), token);
            // Report names and comparisons only, never ambient environment values.
            const string probe = """
                const http = require('node:http');
                http.createServer((request, response) => {
                    response.setHeader('Content-Type', 'application/json');
                    response.end(JSON.stringify({
                        hostRuntimeVariables: Object.keys(process.env).filter(name =>
                            /^(ASPNETCORE_URLS|DOTNET_LAUNCH_PROFILE|ASPNETCORE_ENVIRONMENT|DOTNET_ENVIRONMENT|ASPIRE_LOGLEVEL)$/i.test(name)).sort(),
                        ambient: process.env.NATIVE_IPC_AMBIENT === 'user-setting',
                        prefix: process.env.ASPNETCORE_URLS_EXTRA === 'application-prefix',
                        parameter: process.env.ASPIRE_PARAMETER_TEST_INPUT === 'application-input',
                        declared: process.env.NATIVE_IPC_DECLARED === 'application-setting',
                        explicitRuntimeSetting: process.env.ASPNETCORE_ENVIRONMENT === 'application-setting',
                        path: Boolean(process.env.PATH)
                    }));
                }).listen(Number(process.env.PORT || 8080), '0.0.0.0');
                """;
            var environment = new List<LaunchEnvironment>
            {
                new() { Name = "NATIVE_IPC_DECLARED", Value = "application-setting" }
            };
            if (explicitRuntimeSetting)
            {
                environment.Add(new() { Name = "ASPNETCORE_ENVIRONMENT", Value = "application-setting" });
            }
            var endpoints = new[]
            {
                new WorkloadEndpointDeclaration
                {
                    Name = "http", Protocol = "TCP", UriScheme = "http", HostPort = 0,
                    TargetPort = container ? 8080 : 0, EnvironmentVariable = container ? "" : "PORT"
                }
            };
            var allocation = container
                ? await owner.StartContainerWorkloadAsync(new ContainerWorkloadLaunch
                {
                    Image = "docker.io/library/node:24-alpine", Entrypoint = "node", Mounts = [],
                    Arguments = ["-e", probe], Environment = environment.ToArray(), Endpoints = endpoints
                }, token)
                : await owner.StartExecutableWorkloadAsync(new ExecutableWorkloadLaunch
                {
                    ExecutablePath = node, WorkingDirectory = AppContext.BaseDirectory,
                    Arguments = ["-e", probe], Environment = environment.ToArray(), Endpoints = endpoints
                }, token);
            var endpoint = Assert.Single(allocation.Endpoints);
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var address = new UriBuilder("http", endpoint.Host, endpoint.Port).Uri;
            while (true)
            {
                try
                {
                    using var response = JsonDocument.Parse(await http.GetStringAsync(address, token));
                    var result = response.RootElement;
                    Assert.Equal(explicitRuntimeSetting ? ["ASPNETCORE_ENVIRONMENT"] : Array.Empty<string>(),
                        result.GetProperty("hostRuntimeVariables").EnumerateArray().Select(value => value.GetString()));
                    Assert.Equal(!container, result.GetProperty("ambient").GetBoolean());
                    Assert.Equal(!container, result.GetProperty("prefix").GetBoolean());
                    Assert.Equal(!container, result.GetProperty("parameter").GetBoolean());
                    Assert.True(result.GetProperty("declared").GetBoolean());
                    Assert.Equal(explicitRuntimeSetting, result.GetProperty("explicitRuntimeSetting").GetBoolean());
                    Assert.True(result.GetProperty("path").GetBoolean());

                    break;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), token);
                }
            }
        }
        finally
        {
            await workspace.RetireApplicationWorkspaceAsync();
        }
    }
}

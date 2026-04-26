// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Regression coverage for <see cref="ScriptHostFixture"/>.
/// </summary>
/// <remarks>
/// Repeatedly initializing and disposing the fixture exercises the bind/teardown sequence
/// the way xUnit class fixtures do across parallel test classes, and would catch a return
/// of the <c>Address already in use</c> race that the original <c>HttpListener</c>-based
/// implementation hit during <c>DisposeAsync</c>. See
/// <see href="https://github.com/microsoft/aspire/issues/...">the tracking issue</see>.
/// </remarks>
public class ScriptHostFixtureTests
{
    [Fact]
    public async Task InitializeAndDispose_Repeatedly_DoesNotThrow()
    {
        for (var i = 0; i < 5; i++)
        {
            var fixture = new ScriptHostFixture();
            await fixture.InitializeAsync();

            // Each iteration should bind to a fresh OS-allocated port.
            Assert.NotEqual(0, fixture.Port);
            Assert.StartsWith("http://127.0.0.1:", fixture.BaseUrl);

            using (var client = new HttpClient())
            {
                using var response = await client.GetAsync($"{fixture.BaseUrl}/get-aspire-cli.sh");
                Assert.True(response.IsSuccessStatusCode, $"Expected 2xx, got {response.StatusCode}");
            }

            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnknownPath_Returns404()
    {
        var fixture = new ScriptHostFixture();
        await fixture.InitializeAsync();
        try
        {
            using var client = new HttpClient();
            using var response = await client.GetAsync($"{fixture.BaseUrl}/does-not-exist.sh");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}

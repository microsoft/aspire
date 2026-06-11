using Microsoft.Extensions.Logging;

namespace Aspire_StarterApplication._1.Tests;

#if (TestFx == "MSTest")
[TestClass]
#endif
public class WebTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

#if (TestFx == "MSTest")
    [TestMethod]
#elif (TestFx == "NUnit")
    [Test]
#elif (TestFx == "xUnit.net")
    [Fact]
#endif
    public async Task GetWebResourceRootReturnsOkStatusCode()
    {
        // Arrange
#if (TestFx == "MSTest")
        var cancellationToken = new CancellationTokenSource(DefaultTimeout).Token;
#elif (TestFx == "NUnit")
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
#elif (XUnitVersion == "v2")
        var cancellationToken = new CancellationTokenSource(DefaultTimeout).Token;
#else // XunitVersion v3 or v3mtp
        var cancellationToken = TestContext.Current.CancellationToken;
#endif

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.GeneratedClassNamePrefix_AppHost>(cancellationToken);
        appHost.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);
            // Override the logging filters from the app's configuration
            logging.AddFilter(appHost.Environment.ApplicationName, LogLevel.Debug);
            logging.AddFilter("Aspire.", LogLevel.Debug);
#if (TestFx == "xUnit.net")
            // To output logs to the xUnit.net ITestOutputHelper, consider adding a package from https://www.nuget.org/packages?q=xunit+logging
#endif
        });
        appHost.Services.ConfigureHttpClientDefaults(clientBuilder =>
        {
            clientBuilder.AddStandardResilienceHandler();
        });

        await using var app = await appHost.BuildAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
        await app.StartAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);

        // Act
        var httpClient = app.CreateHttpClient("webfrontend");
        await app.ResourceNotifications.WaitForResourceHealthyAsync("webfrontend", cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
        var response = await httpClient.GetAsync("/", cancellationToken);

        // Assert
#if (TestFx == "MSTest")
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
#elif (TestFx == "NUnit")
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
#elif (TestFx == "xUnit.net")
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
#endif
    }
}

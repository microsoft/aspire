// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Aspire.Dashboard.Authentication.OtlpApiKey;
using Aspire.Dashboard.Configuration;
using Aspire.Hosting;
using Grpc.Core;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using OpenTelemetry.Proto.Collector.Logs.V1;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration;

public class OtlpGrpcServiceTests
{
    private readonly ITestOutputHelper _testOutputHelper;

    public OtlpGrpcServiceTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    [Fact]
    public async Task CallService_OtlpGrpcEndPoint_Success()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        using var channel = IntegrationTestHelpers.CreateGrpcChannel($"http://{app.OtlpServiceGrpcEndPointAccessor().EndPoint}", _testOutputHelper);
        var client = new LogsService.LogsServiceClient(channel);

        // Act
        var response = client.ExportAsync(new ExportLogsServiceRequest());
        var message = await response.ResponseAsync.DefaultTimeout();
        var headers = await response.ResponseHeadersAsync.DefaultTimeout();

        // Assert
        Assert.Null(headers.GetValue("content-security-policy"));
        Assert.Equal(0, message.PartialSuccess.RejectedLogRecords);
    }

    [Fact]
    public async Task CallService_OtlpGrpcEndPoint_RequiredApiKeyMissing_Failure()
    {
        // Arrange
        var apiKey = "TestKey123!";
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardOtlpAuthModeName.ConfigKey] = OtlpAuthMode.ApiKey.ToString();
            config[DashboardConfigNames.DashboardOtlpPrimaryApiKeyName.ConfigKey] = apiKey;
        });
        await app.StartAsync().DefaultTimeout();

        using var channel = IntegrationTestHelpers.CreateGrpcChannel($"http://{app.OtlpServiceGrpcEndPointAccessor().EndPoint}", _testOutputHelper);
        var client = new LogsService.LogsServiceClient(channel);

        // Act
        var ex = await Assert.ThrowsAsync<RpcException>(() => client.ExportAsync(new ExportLogsServiceRequest()).ResponseAsync).DefaultTimeout();

        // Assert
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task CallService_OtlpGrpcEndPoint_RequiredApiKeyWrong_Failure()
    {
        // Arrange
        var apiKey = "TestKey123!";
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardOtlpAuthModeName.ConfigKey] = OtlpAuthMode.ApiKey.ToString();
            config[DashboardConfigNames.DashboardOtlpPrimaryApiKeyName.ConfigKey] = apiKey;
        });
        await app.StartAsync().DefaultTimeout();

        using var channel = IntegrationTestHelpers.CreateGrpcChannel($"http://{app.OtlpServiceGrpcEndPointAccessor().EndPoint}", _testOutputHelper);
        var client = new LogsService.LogsServiceClient(channel);

        var metadata = new Metadata
        {
            { OtlpApiKeyAuthenticationHandler.ApiKeyHeaderName, "WRONG" }
        };

        // Act
        var ex = await Assert.ThrowsAsync<RpcException>(() => client.ExportAsync(new ExportLogsServiceRequest(), metadata).ResponseAsync).DefaultTimeout();

        // Assert
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task CallService_OtlpGrpcEndPoint_RequiredApiKeySent_Success()
    {
        // Arrange
        var apiKey = "TestKey123!";
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardOtlpAuthModeName.ConfigKey] = OtlpAuthMode.ApiKey.ToString();
            config[DashboardConfigNames.DashboardOtlpPrimaryApiKeyName.ConfigKey] = apiKey;
        });
        await app.StartAsync().DefaultTimeout();

        using var channel = IntegrationTestHelpers.CreateGrpcChannel($"http://{app.OtlpServiceGrpcEndPointAccessor().EndPoint}", _testOutputHelper);
        var client = new LogsService.LogsServiceClient(channel);

        var metadata = new Metadata
        {
            { OtlpApiKeyAuthenticationHandler.ApiKeyHeaderName, apiKey }
        };

        // Act
        var response = await client.ExportAsync(new ExportLogsServiceRequest(), metadata).ResponseAsync.DefaultTimeout();

        // Assert
        Assert.Equal(0, response.PartialSuccess.RejectedLogRecords);
    }

    [Fact]
    public async Task CallService_OtlpGrpcEndPoint_RequiredSecondaryApiKeySent_Success()
    {
        // Arrange
        var apiKey = "TestKey123!";
        var secondaryApiKey = "!321yeKtseT";
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardOtlpAuthModeName.ConfigKey] = OtlpAuthMode.ApiKey.ToString();
            config[DashboardConfigNames.DashboardOtlpPrimaryApiKeyName.ConfigKey] = apiKey;
            config[DashboardConfigNames.DashboardOtlpSecondaryApiKeyName.ConfigKey] = secondaryApiKey;
        });
        await app.StartAsync().DefaultTimeout();

        using var channel = IntegrationTestHelpers.CreateGrpcChannel($"http://{app.OtlpServiceGrpcEndPointAccessor().EndPoint}", _testOutputHelper);
        var client = new LogsService.LogsServiceClient(channel);

        var metadata = new Metadata
        {
            { OtlpApiKeyAuthenticationHandler.ApiKeyHeaderName, secondaryApiKey }
        };

        // Act
        var response = await client.ExportAsync(new ExportLogsServiceRequest(), metadata).ResponseAsync.DefaultTimeout();

        // Assert
        Assert.Equal(0, response.PartialSuccess.RejectedLogRecords);
    }

    [Theory]
    [InlineData(KnownConfigNames.DashboardConfigFilePath)]
    [InlineData(KnownConfigNames.Legacy.DashboardConfigFilePath)]
    public async Task CallService_OtlpGrpcEndPoint_ExternalFile_FileChanged_UseConfiguredKey(string dashboardConfigFilePathNameKey)
    {
        // Arrange
        var testSink = new TestSink();
        using var loggerFactory = IntegrationTestHelpers.CreateLoggerFactory(_testOutputHelper, testSink: testSink);
        var logger = loggerFactory.CreateLogger(GetType());

        var apiKey = "TestKey123!";
        var configPath = Path.GetTempFileName();
        var configJson = new JsonObject
        {
            ["Dashboard"] = new JsonObject
            {
                ["Otlp"] = new JsonObject
                {
                    ["AuthMode"] = OtlpAuthMode.ApiKey.ToString(),
                    ["PrimaryApiKey"] = apiKey
                }
            }
        };
        logger.LogInformation("Writing original JSON file.");
        await File.WriteAllTextAsync(configPath, configJson.ToString()).DefaultTimeout();

        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(loggerFactory, config =>
        {
            config[dashboardConfigFilePathNameKey] = configPath;
        });
        await app.StartAsync().DefaultTimeout();

        using var channel = IntegrationTestHelpers.CreateGrpcChannel($"http://{app.OtlpServiceGrpcEndPointAccessor().EndPoint}", loggerFactory);
        var client = new LogsService.LogsServiceClient(channel);

        var metadata = new Metadata
        {
            { OtlpApiKeyAuthenticationHandler.ApiKeyHeaderName, apiKey }
        };

        // Act 1
        var response1 = await client.ExportAsync(new ExportLogsServiceRequest(), metadata).ResponseAsync.DefaultTimeout();

        // Assert 1
        Assert.Equal(0, response1.PartialSuccess.RejectedLogRecords);

        // Change config file
        var tcs = new TaskCompletionSource<DashboardOptions>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitorRegistration = app.DashboardOptionsMonitor.OnChange((o, n) =>
        {
            // FileSystemWatcher can fire multiple events during a single file write (truncate, write, close).
            // The first event may trigger a config reload that reads stale content. Only complete when
            // the value has actually changed to avoid capturing old options with TrySetResult.
            if (o.Otlp.PrimaryApiKey != apiKey)
            {
                logger.LogInformation("Options changed.");
                tcs.TrySetResult(o);
            }
        });

        configJson = new JsonObject
        {
            ["Dashboard"] = new JsonObject
            {
                ["Otlp"] = new JsonObject
                {
                    ["AuthMode"] = OtlpAuthMode.ApiKey.ToString(),
                    ["PrimaryApiKey"] = "Different"
                }
            }
        };

        logger.LogInformation("Writing new JSON file.");
        await File.WriteAllTextAsync(configPath, configJson.ToString()).DefaultTimeout();

        logger.LogInformation("Waiting for options change.");
        var options = await tcs.Task.DefaultTimeout();

        logger.LogInformation("Assert new API key.");
        Assert.Equal("Different", options.Otlp.PrimaryApiKey);

        // Act 2
        logger.LogInformation("Client sends new request with old API key.");
        var ex = await Assert.ThrowsAsync<RpcException>(() => client.ExportAsync(new ExportLogsServiceRequest(), metadata).ResponseAsync).DefaultTimeout();

        // Assert 2
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task CallService_BrowserEndPoint_Failure()
    {
        // Arrange
        X509Certificate2? clientCallbackCert = null;

        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            // Change dashboard to HTTPS so the caller can negotiate a HTTP/2 connection.
            config[DashboardConfigNames.DashboardFrontendUrlName.ConfigKey] = "https://127.0.0.1:0";
        });
        await app.StartAsync().DefaultTimeout();

        using var channel = IntegrationTestHelpers.CreateGrpcChannel(
            $"https://{app.FrontendSingleEndPointAccessor().EndPoint}",
            _testOutputHelper,
            validationCallback: cert =>
            {
                clientCallbackCert = cert;
            });
        var client = new LogsService.LogsServiceClient(channel);

        // Act
        var ex = await Assert.ThrowsAsync<RpcException>(() => client.ExportAsync(new ExportLogsServiceRequest()).ResponseAsync).DefaultTimeout();

        // Assert
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        Assert.NotNull(clientCallbackCert);
        Assert.Equal(TestCertificateLoader.GetTestCertificate().Thumbprint, clientCallbackCert.Thumbprint);
    }

    [Fact]
    public async Task CallService_OtlpEndpoint_RequiredClientCertificateMissing_Failure()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            // Change dashboard to HTTPS so the caller can negotiate a HTTP/2 connection.
            config[DashboardConfigNames.DashboardOtlpGrpcUrlName.ConfigKey] = "https://127.0.0.1:0";

            config[DashboardConfigNames.DashboardOtlpAuthModeName.ConfigKey] = OtlpAuthMode.ClientCertificate.ToString();
        });
        await app.StartAsync().DefaultTimeout();

        using var channel = IntegrationTestHelpers.CreateGrpcChannel($"https://{app.OtlpServiceGrpcEndPointAccessor().EndPoint}", _testOutputHelper);
        var client = new LogsService.LogsServiceClient(channel);

        // Act
        var ex = await Assert.ThrowsAsync<RpcException>(() => client.ExportAsync(new ExportLogsServiceRequest()).ResponseAsync).DefaultTimeout();

        // Assert
        // StatusCode can change depending upon order of execution inside HttpClient.
        Assert.True(ex.StatusCode is StatusCode.Unavailable or StatusCode.Internal, "gRPC call fails without cert.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("A1D2CE3FA7405B5824F207180EA8201EE8BA01B3C07C54AC44BF927D7666F38B")]
    public async Task CallService_OtlpEndpoint_RequiredClientCertificateValid_Success(string? allowedThumbprint)
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            // Change dashboard to HTTPS so the caller can negotiate a HTTP/2 connection.
            config[DashboardConfigNames.DashboardOtlpGrpcUrlName.ConfigKey] = "https://127.0.0.1:0";

            config[DashboardConfigNames.DashboardOtlpAuthModeName.ConfigKey] = OtlpAuthMode.ClientCertificate.ToString();

            if (allowedThumbprint != null)
            {
                config[$"{DashboardConfigNames.DashboardOtlpAllowedCertificatesName.ConfigKey}:0:Thumbprint"] = allowedThumbprint;
            }

            config["Dashboard:Otlp:CertificateAuthOptions:AllowedCertificateTypes"] = "SelfSigned";
            config["Dashboard:Otlp:CertificateAuthOptions:ValidateValidityPeriod"] = "false";
        });
        await app.StartAsync().DefaultTimeout();

        var clientCertificate = TestCertificateLoader.GetTestCertificate("eku.client.pfx");
        using var channel = IntegrationTestHelpers.CreateGrpcChannel(
            $"https://{app.OtlpServiceGrpcEndPointAccessor().EndPoint}",
            _testOutputHelper,
            clientCertificates: new X509CertificateCollection(new[] { clientCertificate }));

        var client = new LogsService.LogsServiceClient(channel);

        // Act
        var response = await client.ExportAsync(new ExportLogsServiceRequest()).ResponseAsync.DefaultTimeout();

        // Assert
        Assert.Equal(0, response.PartialSuccess.RejectedLogRecords);
    }

    [Fact]
    public async Task CallService_OtlpEndpoint_RequiredClientCertificateSHA1Thumbprint_Failure()
    {
        // Arrange
        var clientCertificate = TestCertificateLoader.GetTestCertificate("eku.client.pfx");
        X509Certificate2? clientCallbackCert = null;

        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            // Change dashboard to HTTPS so the caller can negotiate a HTTP/2 connection.
            config[DashboardConfigNames.DashboardOtlpGrpcUrlName.ConfigKey] = "https://127.0.0.1:0";

            config[DashboardConfigNames.DashboardOtlpAuthModeName.ConfigKey] = OtlpAuthMode.ClientCertificate.ToString();

            // Set SHA1 thumbprint.
            config[$"{DashboardConfigNames.DashboardOtlpAllowedCertificatesName.ConfigKey}:0:Thumbprint"] = clientCertificate.Thumbprint;

            config["Dashboard:Otlp:CertificateAuthOptions:AllowedCertificateTypes"] = "SelfSigned";
            config["Dashboard:Otlp:CertificateAuthOptions:ValidateValidityPeriod"] = "false";
        });
        await app.StartAsync().DefaultTimeout();

        using var channel = IntegrationTestHelpers.CreateGrpcChannel(
            $"https://{app.OtlpServiceGrpcEndPointAccessor().EndPoint}",
            _testOutputHelper,
            validationCallback: cert =>
            {
                clientCallbackCert = cert;
            },
            clientCertificates: new X509CertificateCollection(new[] { clientCertificate }));

        var client = new LogsService.LogsServiceClient(channel);

        // Act
        var ex = await Assert.ThrowsAsync<RpcException>(() => client.ExportAsync(new ExportLogsServiceRequest()).ResponseAsync).DefaultTimeout();

        // Assert
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task CallService_OtlpEndpoint_RequiredClientCertificateValid_NotInAllowedList_Failure()
    {
        // Arrange
        X509Certificate2? clientCallbackCert = null;

        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            // Change dashboard to HTTPS so the caller can negotiate a HTTP/2 connection.
            config[DashboardConfigNames.DashboardOtlpGrpcUrlName.ConfigKey] = "https://127.0.0.1:0";

            config[DashboardConfigNames.DashboardOtlpAuthModeName.ConfigKey] = OtlpAuthMode.ClientCertificate.ToString();

            config[$"{DashboardConfigNames.DashboardOtlpAllowedCertificatesName.ConfigKey}:0:Thumbprint"] = "123";

            config["Authentication:Schemes:Certificate:AllowedCertificateTypes"] = "SelfSigned";
            config["Authentication:Schemes:Certificate:ValidateValidityPeriod"] = "false";
        });
        await app.StartAsync().DefaultTimeout();

        var clientCertificates = new X509CertificateCollection(new[] { TestCertificateLoader.GetTestCertificate("eku.client.pfx") });
        using var channel = IntegrationTestHelpers.CreateGrpcChannel(
            $"https://{app.OtlpServiceGrpcEndPointAccessor().EndPoint}",
            _testOutputHelper,
            validationCallback: cert =>
            {
                clientCallbackCert = cert;
            },
            clientCertificates: clientCertificates);

        var client = new LogsService.LogsServiceClient(channel);

        // Act
        var ex = await Assert.ThrowsAsync<RpcException>(() => client.ExportAsync(new ExportLogsServiceRequest()).ResponseAsync).DefaultTimeout();

        // Assert
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }
}

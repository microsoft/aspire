// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography.X509Certificates;
using Aspire.Dashboard.Configuration;
using Aspire.Hosting;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration;

public static class IntegrationTestHelpers
{
    private static readonly X509Certificate2 s_testCertificate = TestCertificateLoader.GetTestCertificate();

    public static ILoggerFactory CreateLoggerFactory(ITestOutputHelper testOutputHelper, ITestSink? testSink = null)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.AddXunit(testOutputHelper, LogLevel.Trace, DateTimeOffset.UtcNow);
            builder.SetMinimumLevel(LogLevel.Trace);
            if (testSink != null)
            {
                builder.AddProvider(new TestLoggerProvider(testSink));
            }
        });
    }

    public static DashboardWebApplication CreateDashboardWebApplication(
        ITestOutputHelper testOutputHelper,
        Action<Dictionary<string, string?>>? additionalConfiguration = null,
        Action<WebApplicationBuilder>? preConfigureBuilder = null,
        bool? clearLogFilterRules = null,
        ITestSink? testSink = null)
    {
        var loggerFactory = CreateLoggerFactory(testOutputHelper, testSink);

        return CreateDashboardWebApplication(loggerFactory, additionalConfiguration, preConfigureBuilder, clearLogFilterRules);
    }

    public static DashboardWebApplication CreateDashboardWebApplication(
        ILoggerFactory loggerFactory,
        Action<Dictionary<string, string?>>? additionalConfiguration = null,
        Action<WebApplicationBuilder>? preConfigureBuilder = null,
        bool? clearLogFilterRules = null)
    {
        clearLogFilterRules ??= true;

        var initialData = new Dictionary<string, string?>
        {
            [DashboardConfigNames.DashboardFrontendUrlName.ConfigKey] = "http://127.0.0.1:0",
            [DashboardConfigNames.DashboardOtlpGrpcUrlName.ConfigKey] = "http://127.0.0.1:0",
            [DashboardConfigNames.DashboardOtlpHttpUrlName.ConfigKey] = "http://127.0.0.1:0",
            [DashboardConfigNames.DashboardMcpUrlName.ConfigKey] = "http://127.0.0.1:0",
            [DashboardConfigNames.DashboardOtlpAuthModeName.ConfigKey] = nameof(OtlpAuthMode.Unsecured),
            [DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = nameof(FrontendAuthMode.Unsecured),
            [DashboardConfigNames.DashboardApiEnabledName.ConfigKey] = "true",
            // Allow the requirement of HTTPS communication with the OpenIdConnect authority to be relaxed during tests.
            ["Authentication:Schemes:OpenIdConnect:RequireHttpsMetadata"] = "false"
        };

        additionalConfiguration?.Invoke(initialData);

        var config = new ConfigurationManager()
            .AddInMemoryCollection(initialData).Build();

        var dashboardWebApplication = new DashboardWebApplication(builder =>
        {
            preConfigureBuilder?.Invoke(builder);

            // Clear log filter rules by default so all logs are available in test output.
            if (clearLogFilterRules.Value)
            {
                builder.Services.PostConfigure<LoggerFilterOptions>(o =>
                {
                    o.Rules.Clear();
                });
            }

            // Remove environment variable source of configuration.
            var sources = ((IConfigurationBuilder)builder.Configuration).Sources;
            foreach (var item in sources.ToList())
            {
                if (item is EnvironmentVariablesConfigurationSource)
                {
                    sources.Remove(item);
                }
            }
            builder.Configuration.AddConfiguration(config);

            builder.Services.AddSingleton(loggerFactory);
            builder.WebHost.ConfigureKestrel(serverOptions =>
            {
                serverOptions.ConfigureHttpsDefaults(options =>
                {
                    options.ServerCertificate = s_testCertificate;
                });
            });
        });

        return dashboardWebApplication;
    }

    public static HttpClient CreateHttpClient(
        string address,
        Action<X509Certificate2?>? validationCallback = null,
        X509CertificateCollection? clientCertificates = null)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions =
            {
                RemoteCertificateValidationCallback = (message, cert, chain, errors) =>
                {
                    validationCallback?.Invoke((X509Certificate2)cert!);
                    return true;
                }
            }
        };
        if (clientCertificates != null)
        {
            handler.SslOptions.ClientCertificates = clientCertificates;
        }

        return new HttpClient(handler) { BaseAddress = new Uri(address) };
    }

    public static GrpcChannel CreateGrpcChannel(
        string address,
        ITestOutputHelper testOutputHelper,
        Action<X509Certificate2?>? validationCallback = null,
        int? retryCount = null,
        X509CertificateCollection? clientCertificates = null)
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddXunit(testOutputHelper);
            builder.SetMinimumLevel(LogLevel.Trace);
        });

        return CreateGrpcChannel(address, loggerFactory, validationCallback: validationCallback, retryCount: retryCount, clientCertificates: clientCertificates);
    }

    public static GrpcChannel CreateGrpcChannel(
        string address,
        ILoggerFactory loggerFactory,
        Action<X509Certificate2?>? validationCallback = null,
        int? retryCount = null,
        X509CertificateCollection? clientCertificates = null)
    {
        ServiceConfig? serviceConfig = null;
        if (retryCount > 0)
        {
            var defaultMethodConfig = new MethodConfig
            {
                Names = { MethodName.Default },
                RetryPolicy = new RetryPolicy
                {
                    MaxAttempts = retryCount,
                    InitialBackoff = TimeSpan.FromSeconds(1),
                    MaxBackoff = TimeSpan.FromSeconds(5),
                    BackoffMultiplier = 1.5,
                    RetryableStatusCodes = { StatusCode.Unavailable }
                }
            };

            serviceConfig = new ServiceConfig { MethodConfigs = { defaultMethodConfig } };
        }

        var handler = new SocketsHttpHandler
        {
            SslOptions =
            {
                RemoteCertificateValidationCallback = (message, cert, chain, errors) =>
                {
                    validationCallback?.Invoke((X509Certificate2)cert!);
                    return true;
                }
            }
        };
        if (clientCertificates != null)
        {
            handler.SslOptions.ClientCertificates = clientCertificates;
        }

        var channel = GrpcChannel.ForAddress(address, new()
        {
            HttpHandler = handler,
            LoggerFactory = loggerFactory,
            ServiceConfig = serviceConfig
        });
        return channel;
    }
}

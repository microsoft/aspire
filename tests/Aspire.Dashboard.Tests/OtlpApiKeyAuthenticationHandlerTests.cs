// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Encodings.Web;
using Aspire.Dashboard.Authentication.OtlpApiKey;
using Aspire.Dashboard.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class OtlpApiKeyAuthenticationHandlerTests
{
    [Fact]
    public async Task AuthenticateAsync_NoHeader_Failure()
    {
        // Arrange
        var handler = await CreateAuthHandlerAsync(primaryApiKey: "abc", secondaryApiKey: null, otlpApiKeyHeader: null).DefaultTimeout();

        // Act
        var result = await handler.AuthenticateAsync().DefaultTimeout();

        // Assert
        Assert.NotNull(result.Failure);
        Assert.Equal($"API key from '{OtlpApiKeyAuthenticationHandler.ApiKeyHeaderName}' header is missing.", result.Failure.Message);
    }

    [Fact]
    public async Task AuthenticateAsync_BigApiKeys_NoMatch_Failure()
    {
        // Arrange
        var handler = await CreateAuthHandlerAsync(primaryApiKey: new string('!', 1000), secondaryApiKey: null, otlpApiKeyHeader: new string('!', 999)).DefaultTimeout();

        // Act
        var result = await handler.AuthenticateAsync().DefaultTimeout();

        // Assert
        Assert.NotNull(result.Failure);
        Assert.Equal($"Incoming API key from '{OtlpApiKeyAuthenticationHandler.ApiKeyHeaderName}' header doesn't match configured API key.", result.Failure.Message);
    }

    [Fact]
    public async Task AuthenticateAsync_BigApiKeys_Match_Success()
    {
        // Arrange
        var handler = await CreateAuthHandlerAsync(primaryApiKey: new string('!', 1000), secondaryApiKey: null, otlpApiKeyHeader: new string('!', 1000)).DefaultTimeout();

        // Act
        var result = await handler.AuthenticateAsync().DefaultTimeout();

        // Assert
        Assert.Null(result.Failure);
    }

    [Theory]
    [InlineData("abc", null, "abc", true)]
    [InlineData("abcd", null, "abc", false)]
    [InlineData("abc", null, "abcd", false)]
    [InlineData("abc", "abcd", "abcd", true)]
    public async Task AuthenticateAsync_MatchHeader_Success(string primaryApiKey, string? secondaryApiKey, string otlpApiKeyHeader, bool success)
    {
        // Arrange
        var handler = await CreateAuthHandlerAsync(primaryApiKey, secondaryApiKey, otlpApiKeyHeader).DefaultTimeout();

        // Act
        var result = await handler.AuthenticateAsync().DefaultTimeout();

        // Assert
        Assert.Equal(success, result.Failure == null);
    }

    [Fact]
    public async Task AuthenticateAsync_EmptyHeader_Failure()
    {
        // Arrange
        var handler = await CreateAuthHandlerAsync(primaryApiKey: "abc", secondaryApiKey: "", otlpApiKeyHeader: "").DefaultTimeout();

        // Act
        var result = await handler.AuthenticateAsync().DefaultTimeout();

        // Assert
        Assert.NotNull(result.Failure);
        Assert.Equal($"API key from '{OtlpApiKeyAuthenticationHandler.ApiKeyHeaderName}' header is empty.", result.Failure.Message);
    }

    private static async Task<OtlpApiKeyAuthenticationHandler> CreateAuthHandlerAsync(string primaryApiKey, string? secondaryApiKey, string? otlpApiKeyHeader)
    {
        var options = new DashboardOptions
        {
            Otlp =
            {
                GrpcEndpointUrl = "http://localhost",
                PrimaryApiKey = primaryApiKey,
                SecondaryApiKey = secondaryApiKey
            }
        };
        Assert.True(options.Otlp.TryParseOptions(out _));

        var handler = new OtlpApiKeyAuthenticationHandler(
            new TestOptionsMonitor<DashboardOptions>(options),
            new TestOptionsMonitor<OtlpApiKeyAuthenticationHandlerOptions>(new OtlpApiKeyAuthenticationHandlerOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default);

        var httpContext = new DefaultHttpContext();
        if (otlpApiKeyHeader != null)
        {
            httpContext.Request.Headers[OtlpApiKeyAuthenticationHandler.ApiKeyHeaderName] = otlpApiKeyHeader;
        }
        await handler.InitializeAsync(new AuthenticationScheme("Test", "Test", handler.GetType()), httpContext);
        return handler;
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T options) => CurrentValue = options;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string> listener) => throw new NotImplementedException();

        public IDisposable OnChange(Action<T> listener) => throw new NotImplementedException();
    }
}

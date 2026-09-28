// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Serialization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class LocalBrowserStorageTests
{
    [Theory]
    [InlineData(123, "123")]
    [InlineData("Hello world", @"""Hello world""")]
    [InlineData(null, "null")]
    public async Task SetUnprotectedAsync_JSInvokedWithJson(object? value, string result)
    {
        // Arrange
        string? identifier = null;
        object?[]? args = null;

        var testJsonRuntime = new TestJSRuntime();
        testJsonRuntime.OnInvoke = r =>
        {
            (identifier, args) = r;
            return default;
        };
        var localStorage = CreateBrowserLocalStorage(testJsonRuntime);

        // Act
        await (value switch
        {
            int number => localStorage.SetUnprotectedAsync("MyKey", number),
            string text => localStorage.SetUnprotectedAsync("MyKey", text),
            null => localStorage.SetUnprotectedAsync<string?>("MyKey", null),
            _ => throw new InvalidOperationException($"Unexpected test value type: {value.GetType()}")
        }).DefaultTimeout();

        // Assert
        Assert.Equal("localStorage.setItem", identifier);
        Assert.NotNull(args);
        Assert.Equal("MyKey", args[0]);
        Assert.Equal(result, args[1]);
    }

    [Fact]
    public async Task GetUnprotectedAsync_HasValue_Success()
    {
        // Arrange
        string? identifier = null;
        object?[]? args = null;

        var testJsonRuntime = new TestJSRuntime();
        testJsonRuntime.OnInvoke = r =>
        {
            (identifier, args) = r;
            return "123";
        };
        var localStorage = CreateBrowserLocalStorage(testJsonRuntime);

        // Act
        var result = await localStorage.GetUnprotectedAsync<int>("MyKey").DefaultTimeout();

        // Assert
        Assert.True(result.Success);
        Assert.Equal(123, result.Value);
        Assert.Equal("localStorage.getItem", identifier);
        Assert.NotNull(args);
        Assert.Equal("MyKey", args[0]);
    }

    [Fact]
    public async Task GetUnprotectedAsync_NoValue_Failure()
    {
        // Arrange
        string? identifier = null;
        object?[]? args = null;

        var testJsonRuntime = new TestJSRuntime();
        testJsonRuntime.OnInvoke = r =>
        {
            (identifier, args) = r;
            return default;
        };
        var localStorage = CreateBrowserLocalStorage(testJsonRuntime);

        // Act
        var result = await localStorage.GetUnprotectedAsync<int>("MyKey").DefaultTimeout();

        // Assert
        Assert.False(result.Success);
        Assert.Equal("localStorage.getItem", identifier);
        Assert.NotNull(args);
        Assert.Equal("MyKey", args[0]);
    }

    [Fact]
    public async Task GetUnprotectedAsync_InvalidValue_Failure()
    {
        // Arrange
        string? identifier = null;
        object?[]? args = null;

        var testJsonRuntime = new TestJSRuntime();
        testJsonRuntime.OnInvoke = r =>
        {
            (identifier, args) = r;
            return "One";
        };
        var localStorage = CreateBrowserLocalStorage(testJsonRuntime);

        // Act
        var result = await localStorage.GetUnprotectedAsync<int>("MyKey").DefaultTimeout();

        // Assert
        Assert.False(result.Success);
        Assert.Equal("localStorage.getItem", identifier);
        Assert.NotNull(args);
        Assert.Equal("MyKey", args[0]);
    }

    [Fact]
    public async Task GetUnprotectedAsync_DisconnectedCircuit_FailureWithoutLogging()
    {
        var testJsonRuntime = new TestJSRuntime
        {
            OnInvoke = _ => throw new JSDisconnectedException("The circuit disconnected.")
        };
        var sink = new TestSink();
        var logger = new TestLogger<LocalBrowserStorage>(new TestLoggerFactory(sink, enabled: true));
        var localStorage = CreateBrowserLocalStorage(testJsonRuntime, logger: logger);

        var result = await localStorage.GetUnprotectedAsync<int>("MyKey").DefaultTimeout();

        Assert.False(result.Success);
        Assert.Empty(sink.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetAsync_DisconnectedCircuit_FailureWithoutLogging(bool useSessionStorage)
    {
        var testJsonRuntime = new TestJSRuntime
        {
            OnInvoke = _ => throw new JSDisconnectedException("The circuit disconnected.")
        };
        var sink = new TestSink();
        var loggerFactory = new TestLoggerFactory(sink, enabled: true);
        var storage = CreateBrowserStorage(testJsonRuntime, loggerFactory, useSessionStorage);

        var result = await storage.GetAsync<int>("MyKey").DefaultTimeout();

        Assert.False(result.Success);
        Assert.Empty(sink.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetAsync_NoValue_FailureWithoutLogging(bool useSessionStorage)
    {
        var testJsonRuntime = new TestJSRuntime();
        var sink = new TestSink();
        var loggerFactory = new TestLoggerFactory(sink, enabled: true);
        var storage = CreateBrowserStorage(testJsonRuntime, loggerFactory, useSessionStorage);

        var result = await storage.GetAsync<int>("MyKey").DefaultTimeout();

        Assert.False(result.Success);
        Assert.Empty(sink.Writes);
    }

    [Fact]
    public async Task GetUnprotectedAsync_JSFailure_LogsInformation()
    {
        var exception = new JSException("Browser storage unavailable.");
        var testJsonRuntime = new TestJSRuntime { OnInvoke = _ => throw exception };
        var sink = new TestSink();
        var logger = new TestLogger<LocalBrowserStorage>(new TestLoggerFactory(sink, enabled: true));
        var localStorage = CreateBrowserLocalStorage(testJsonRuntime, logger: logger);

        var result = await localStorage.GetUnprotectedAsync<int>("MyKey").DefaultTimeout();

        Assert.False(result.Success);
        var log = Assert.Single(sink.Writes);
        Assert.Equal(LogLevel.Information, log.LogLevel);
        Assert.Same(exception, log.Exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetAsync_JSFailure_LogsInformation(bool useSessionStorage)
    {
        var exception = new JSException("Browser storage unavailable.");
        var testJsonRuntime = new TestJSRuntime { OnInvoke = _ => throw exception };
        var sink = new TestSink();
        var loggerFactory = new TestLoggerFactory(sink, enabled: true);
        var storage = CreateBrowserStorage(testJsonRuntime, loggerFactory, useSessionStorage);

        var result = await storage.GetAsync<int>("MyKey").DefaultTimeout();

        Assert.False(result.Success);
        var log = Assert.Single(sink.Writes);
        Assert.Equal(LogLevel.Information, log.LogLevel);
        Assert.Same(exception, log.Exception);
    }

    [Fact]
    public async Task SetUnprotectedAsync_UsesCircuitOptionsJsonTypeInfoResolvers()
    {
        var resolver = new TrackingJsonTypeInfoResolver();
        var circuitOptions = CreateCircuitOptions(resolver);
        var localStorage = CreateBrowserLocalStorage(new TestJSRuntime(), circuitOptions);

        await localStorage.SetUnprotectedAsync("MyKey", 123).DefaultTimeout();

        Assert.Equal(typeof(int), resolver.RequestedType);
    }

    private static LocalBrowserStorage CreateBrowserLocalStorage(
        TestJSRuntime testJsonRuntime,
        CircuitOptions? circuitOptions = null,
        ILogger<LocalBrowserStorage>? logger = null)
    {
        circuitOptions ??= CreateCircuitOptions(DashboardJsonSerializerContext.Default);

        return new LocalBrowserStorage(
            testJsonRuntime,
            new ProtectedLocalStorage(testJsonRuntime, new TestDataProtector()),
            logger ?? NullLogger<LocalBrowserStorage>.Instance,
            Options.Create(circuitOptions));
    }

    private static IBrowserStorage CreateBrowserStorage(TestJSRuntime testJsonRuntime, TestLoggerFactory loggerFactory, bool useSessionStorage)
        => useSessionStorage
            ? new SessionBrowserStorage(
                new ProtectedSessionStorage(testJsonRuntime, new TestDataProtector()),
                new TestLogger<SessionBrowserStorage>(loggerFactory))
            : CreateBrowserLocalStorage(testJsonRuntime, logger: new TestLogger<LocalBrowserStorage>(loggerFactory));

    private static CircuitOptions CreateCircuitOptions(IJsonTypeInfoResolver resolver)
    {
        var circuitOptions = new CircuitOptions();
#pragma warning disable ASPNETCORE9004 // Native AOT resolver composition is experimental in .NET 11.
        circuitOptions.JsonTypeInfoResolvers.Add(resolver);
#pragma warning restore ASPNETCORE9004
        return circuitOptions;
    }

    private sealed class TrackingJsonTypeInfoResolver : IJsonTypeInfoResolver
    {
        public Type? RequestedType { get; private set; }

        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            RequestedType = type;
            return ((IJsonTypeInfoResolver)DashboardJsonSerializerContext.Default).GetTypeInfo(type, options);
        }
    }

    private sealed class TestJSRuntime : IJSRuntime
    {
        public Func<(string Identifier, object?[]? Args), object?>? OnInvoke { get; set; }

        public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] TValue>(string identifier, object?[]? args)
        {
            if (OnInvoke?.Invoke((identifier, args)) is TValue result)
            {
                return ValueTask.FromResult(result);
            }
            return default;
        }

        public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (OnInvoke?.Invoke((identifier, args)) is TValue result)
            {
                return ValueTask.FromResult(result);
            }
            return default;
        }
    }

    private sealed class TestDataProtector : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose)
        {
            throw new NotImplementedException();
        }

        public byte[] Protect(byte[] plaintext)
        {
            throw new NotImplementedException();
        }

        public byte[] Unprotect(byte[] protectedData)
        {
            throw new NotImplementedException();
        }
    }
}

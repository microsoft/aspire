// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.Native.Runtime;

namespace Aspire.Hosting.Native.Server;

/// <summary>Defines validated server policies with named defaults, not scenario-dependent settings.</summary>
internal sealed class NativeServerOptions
{
    public NativeRuntimeOptions Runtime { get; set; } = new();
    public TimeSpan ControllerStartupTimeout { get; set; } = TimeSpan.FromSeconds(45);
    public TimeSpan ControllerRequestTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan ControllerLogReadTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan CleanupTimeout { get; set; } = TimeSpan.FromSeconds(45);
    public TimeSpan AuthenticationTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan DashboardShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public int MaximumConcurrentRequests { get; set; } = 64;
    public int MaximumConnections { get; set; } = 32;
    public int MaximumRequestBytes { get; set; } = 256 * 1024;
    public int MaximumHandlesPerConnection { get; set; } = 4096;
    public int MaximumCliStreams { get; set; } = 64;
    public int RetainedAppHostLogEntries { get; set; } = 256;

    public static NativeServerOptions Load(string? path)
    {
        NativeServerOptions options;
        if (path is null)
        {
            options = new();
        }
        else
        {
            using var stream = File.OpenRead(path);
            options = JsonSerializer.Deserialize(stream, NativeServerJsonContext.Default.NativeServerOptions)
                ?? throw new InvalidDataException("Server options cannot be null.");
        }
        options.Validate();

        return options;
    }

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Runtime);
        Runtime.Validate();
        foreach (var duration in new[] { ControllerStartupTimeout, ControllerRequestTimeout, ControllerLogReadTimeout, CleanupTimeout,
            AuthenticationTimeout, RetryInterval, DashboardShutdownTimeout })
        {
            if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentException("Server lifecycle intervals must be positive bounded durations.");
            }
        }
        if (RetryInterval > ControllerStartupTimeout || MaximumConcurrentRequests is < 1 or > 4096 ||
            MaximumCliStreams is < 1 or > 4096 || RetainedAppHostLogEntries is < 1 or > 65536 ||
            MaximumConnections is < 1 or > 4096 || MaximumRequestBytes is < 1024 or > 16 * 1024 * 1024 ||
            MaximumHandlesPerConnection is < 1 or > 65536)
        {
            throw new ArgumentException("Invalid native server capacity or retry interval.");
        }
    }
}

[JsonSerializable(typeof(NativeServerOptions))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true)]
internal sealed partial class NativeServerJsonContext : JsonSerializerContext;

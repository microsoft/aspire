// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;

namespace NativeHosting;

// Only scanned offline. These capabilities are implemented by a registered
// TypeScript integration host, never by these CLR declarations.
public static class IntegrationContracts
{
    /// <summary>Adds an authenticated Redis service with a session data volume.</summary>
    [AspireExport]
    public static NativeResource AddRedis(this NativeBuilder builder, string name) => throw new NotSupportedException();

    /// <summary>Adds an authenticated PostgreSQL server with a session data volume.</summary>
    [AspireExport]
    public static NativeResource AddPostgres(this NativeBuilder builder, string name) => throw new NotSupportedException();

    /// <summary>Adds and initializes a logical database under a PostgreSQL server.</summary>
    [AspireExport]
    public static NativeResource AddDatabase(this NativeResource server, string name, string databaseName) => throw new NotSupportedException();

    /// <summary>Adds a Nuxt workload referencing a Redis connection property.</summary>
    [AspireExport]
    public static NativeResource AddNuxt(this NativeBuilder builder, string name, string directory, NativeResource cache) => throw new NotSupportedException();

    /// <summary>Adds a run-only tunnel port with an external CLI executable owner.</summary>
    [AspireExport]
    public static NativeResource AddDevTunnel(this NativeBuilder builder, string name, NativeResource target, string directory, bool allowAnonymous) => throw new NotSupportedException();

    /// <summary>Executes a bounded authenticated Redis operation.</summary>
    [AspireExport]
    public static Task<string> RedisCommand(this NativeResource resource, string operation, string key, string value, bool wrongPassword, CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>Executes a bounded authenticated query against a database child.</summary>
    [AspireExport]
    public static Task<string> Query(this NativeResource resource, string sql, bool wrongPassword, CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>Stops integration controllers and releases session-owned external state.</summary>
    [AspireExport]
    public static Task<bool> ReleaseGraph(this NativeBuilder builder, string[] callbackIds, CancellationToken cancellationToken) => throw new NotSupportedException();
}

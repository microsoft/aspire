// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Migrations;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// A configurable <see cref="IMigration"/> for tests: detection returns the supplied
/// <see cref="MigrationDescriptor"/> (or <see langword="null"/> for "nothing to migrate"), and can
/// optionally throw to exercise the best-effort failure handling shared by the migrate command,
/// the doctor check, and the update advisory.
/// </summary>
internal sealed class TestMigration(string id, int order, MigrationDescriptor? descriptor, bool throwOnDetect = false) : IMigration
{
    public string Id => id;

    public int Order => order;

    public bool ApplyInvoked { get; private set; }

    public MigrationContext? DetectedContext { get; private set; }

    public MigrationContext? AppliedContext { get; private set; }

    public Task<MigrationDescriptor?> DetectAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        DetectedContext = context;

        if (throwOnDetect)
        {
            throw new InvalidOperationException("Detection failed");
        }

        return Task.FromResult(descriptor);
    }

    public Task ApplyAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        ApplyInvoked = true;
        AppliedContext = context;
        return Task.CompletedTask;
    }
}

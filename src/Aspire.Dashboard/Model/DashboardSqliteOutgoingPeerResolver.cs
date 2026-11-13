// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;

namespace Aspire.Dashboard.Model;

internal sealed class DashboardSqliteOutgoingPeerResolver : IOutgoingPeerResolver
{
    private const string SqliteSystemName = "sqlite";

    public bool TryResolvePeer(KeyValuePair<string, string>[] attributes, out string? name, out ResourceViewModel? matchedResource)
    {
        var isDashboardSqlite = string.Equals(attributes.GetValue(OtlpSpan.PeerServiceAttributeKey), DashboardRunStore.DatabaseFileName, StringComparison.Ordinal) &&
            string.Equals(attributes.GetValue("db.system.name"), SqliteSystemName, StringComparison.Ordinal) &&
            string.Equals(attributes.GetValue("db.namespace"), DashboardRunStore.DatabaseFileName, StringComparison.Ordinal);

        name = isDashboardSqlite ? DashboardRunStore.DatabaseFileName : null;
        matchedResource = null;
        return isDashboardSqlite;
    }

    public IDisposable OnPeerChanges(Func<Task> callback) => NullDisposable.Instance;

    private sealed class NullDisposable : IDisposable
    {
        public static NullDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
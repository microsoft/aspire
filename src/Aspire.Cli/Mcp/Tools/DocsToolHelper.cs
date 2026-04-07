// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Mcp.Docs;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Mcp.Tools;

/// <summary>
/// Helper methods for documentation tool operations.
/// </summary>
internal static class DocsToolHelper
{
    /// <summary>
    /// Ensures the documentation index is ready, sending progress notifications if indexing is needed.
    /// </summary>
    public static async ValueTask EnsureIndexedWithNotificationsAsync(
        IDocsIndexService docsIndexService,
        ProgressToken? progressToken,
        IMcpNotifier notifier,
        CancellationToken cancellationToken)
    {
        if (docsIndexService.IsIndexed)
        {
            return;
        }

        if (progressToken != null)
        {
            await notifier.SendNotificationAsync(
                NotificationMethods.ProgressNotification,
                new ProgressNotificationParams
                {
                    ProgressToken = progressToken.Value,
                    Progress = new ProgressNotificationValue
                    {
                        Message = "Indexing Aspire docs...",
                        Progress = 1
                    }
                }, cancellationToken).ConfigureAwait(false);
        }

        await docsIndexService.EnsureIndexedAsync(cancellationToken).ConfigureAwait(false);

        if (progressToken != null)
        {
            await notifier.SendNotificationAsync(
                NotificationMethods.ProgressNotification,
                new ProgressNotificationParams
                {
                    ProgressToken = progressToken.Value,
                    Progress = new ProgressNotificationValue
                    {
                        Message = "Aspire docs indexed",
                        Progress = 2
                    }
                }, cancellationToken).ConfigureAwait(false);
        }
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Represents ATS-compatible Azure Storage roles.
/// </summary>
internal enum AzureStorageRole
{
    ClassicStorageAccountContributor,
    ClassicStorageAccountKeyOperatorServiceRole,
    StorageAccountBackupContributor,
    StorageAccountContributor,
    StorageAccountKeyOperatorServiceRole,
    StorageBlobDataContributor,
    StorageBlobDataOwner,
    StorageBlobDataReader,
    StorageBlobDelegator,
    StorageFileDataPrivilegedContributor,
    StorageFileDataPrivilegedReader,
    StorageFileDataSmbShareContributor,
    StorageFileDataSmbShareReader,
    StorageFileDataSmbShareElevatedContributor,
    StorageQueueDataContributor,
    StorageQueueDataReader,
    StorageQueueDataMessageSender,
    StorageQueueDataMessageProcessor,
    StorageTableDataContributor,
    StorageTableDataReader,
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Milvus;

internal static class MilvusContainerImageTags
{
    /// <remarks>docker.io</remarks>
    public const string Registry = "docker.io";

    /// <remarks>milvusdb/milvus</remarks>
    public const string Image = "milvusdb/milvus";

    // Note that when trying to update to v2.6.0 we hit https://github.com/microsoft/aspire/issues/11184
    /// <remarks>v2.5.27</remarks>
    public const string Tag = "v2.5.27";

    /// <remarks>zilliz/attu</remarks>
    public const string AttuImage = "zilliz/attu";

    /// <remarks>v2.5</remarks>
    public const string AttuTag = "v2.5";
}


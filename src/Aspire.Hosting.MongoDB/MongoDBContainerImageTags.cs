// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.MongoDB;

internal static class MongoDBContainerImageTags
{
    /// <remarks>docker.io</remarks>
    public const string Registry = "docker.io";

    /// <remarks>library/mongo</remarks>
    public const string Image = "library/mongo";

    /// <remarks>8.3</remarks>
    public const string Tag = "8.3";

    /// <remarks>docker.io</remarks>
    public const string MongoExpressRegistry = "docker.io";

    /// <remarks>library/mongo-express</remarks>
    public const string MongoExpressImage = "library/mongo-express";

    /// <remarks>1.0</remarks>
    public const string MongoExpressTag = "1.0";
}

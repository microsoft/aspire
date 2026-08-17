// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Qdrant.Client;

[assembly: ConfigurationSchema("Aspire:Qdrant:Client", typeof(QdrantClientSettings))]
[assembly: ConfigurationSchema("Aspire:Qdrant:Client:*", typeof(QdrantClientSettings))]

[assembly: LoggingCategories("Qdrant.Client")]

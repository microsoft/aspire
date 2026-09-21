// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Kafka.Dekaf;

[assembly: ConfigurationSchema("Aspire:Kafka:Dekaf:Producer", typeof(KafkaProducerSettings))]
[assembly: ConfigurationSchema("Aspire:Kafka:Dekaf:Consumer", typeof(KafkaConsumerSettings))]
[assembly: ConfigurationSchema("Aspire:Kafka:Dekaf:AdminClient", typeof(KafkaAdminClientSettings))]
[assembly: LoggingCategories("Dekaf")]

// Native Dekaf options use init-only properties, which the schema generator does not yet
// describe correctly. Leave Config open rather than advertising an incomplete options schema.

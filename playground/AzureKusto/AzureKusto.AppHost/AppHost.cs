using Kusto.Data.Common;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddAzureContainerAppEnvironment("infra");

var kusto = builder.AddAzureKustoCluster("kusto").RunAsEmulator();
var db = kusto.AddReadWriteDatabase("testdb");

builder.AddProject<Projects.AzureKusto_Worker>("worker")
    .WithReference(db)
    .WaitFor(db);

// Option: Ingest using a control command
//
// Works well for local development and Aspire-based testing of queries, but .ingest
// commands are not recommended for production scenarios.
db.WithControlCommand(
    """
    .execute database script with (ThrowOnErrors=true) <|
        .create-merge table TestTable (Id: int, Name: string)
        .ingest inline into table TestTable <|
            1,"Alice"
            2,"Bob"
            3,"Charlie"
    """
);

// Option: Ingest using streaming ingestion
//
// The Kusto emulator also supports streaming ingestion (see IngestionWorker).
// The emulator does not support ingestion methods that use the dedicated ingestion endpoint. See
// https://learn.microsoft.com/en-us/azure/data-explorer/kusto-emulator-overview#limitations for
// full details.
//
// To use streaming ingestion in the emulator, the target table must have streaming ingestion enabled.
// Additionally, we flush the ingestion cache before starting ingestion to prevent race conditions
// where ingestion may start before the emulator is ready.
db.WithControlCommand(CslCommandGenerator.GenerateTableAlterStreamingIngestionPolicyCommand("TestTable", isEnabled: true));
db.WithControlCommand(CslCommandGenerator.GenerateDatabaseStreamingIngestionCacheClearCommand());

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

var app = builder.Build();
app.Run();

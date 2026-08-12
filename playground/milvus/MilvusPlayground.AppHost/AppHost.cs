var builder = DistributedApplication.CreateBuilder(args);

var milvusdb = builder.AddMilvus("milvus")
    .WithDataVolume("milvus-data")
    .WithAttu();

builder.AddProject<Projects.MilvusPlayground_ApiService>("apiservice")
    .WithReference(milvusdb).WaitFor(milvusdb);

builder.Build().Run();

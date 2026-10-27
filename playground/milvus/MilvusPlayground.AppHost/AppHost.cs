var builder = DistributedApplication.CreateBuilder(args);

var milvusdb = builder.AddMilvus("milvus")
    .WithDataVolume("milvus-data")
    .WithAttu();

builder.AddDotnetProject("apiservice", @"..\MilvusPlayground.ApiService\MilvusPlayground.ApiService.csproj")
    .WithReference(milvusdb).WaitFor(milvusdb);

builder.Build().Run();

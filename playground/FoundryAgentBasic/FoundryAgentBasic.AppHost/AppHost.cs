using Aspire.Hosting.Foundry;

var builder = DistributedApplication.CreateBuilder(args);

var project = builder.AddFoundry("proj-foundry")
    .AddProject("proj");

project.AddModelDeployment("chat", FoundryModel.OpenAI.Gpt41Mini);

builder.Build().Run();

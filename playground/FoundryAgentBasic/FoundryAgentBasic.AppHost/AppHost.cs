using Aspire.Hosting.Foundry;

var builder = DistributedApplication.CreateBuilder(args);

var project = builder.AddFoundry("proj-foundry")
    .AddProject("proj");

project.AddModelDeployment("chat", FoundryModel.OpenAI.Gpt41Mini);

// Add a Foundry Toolbox with a single WebSearch tool. Aspire reconciles the Toolbox on the Foundry
// data plane during local runs and deployments.
project.AddToolbox("field-tools")
    .WithWebSearchTool();

builder.Build().Run();

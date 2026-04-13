using Aspire.Hosting.Foundry;

var builder = DistributedApplication.CreateBuilder(args);

var tenantId = builder.AddParameterFromConfiguration("tenant", "Azure:TenantId");

var foundry = builder.AddFoundry("aif-globalazure");
var project = foundry.AddProject("proj-globalazure");
var chat = project.AddModelDeployment("chat", FoundryModel.OpenAI.Gpt41);

builder.AddPythonApp("weather-hosted-agent", "../app", "main.py")
    .WithUv()
    .WithReference(project).WaitFor(project)
    .WithReference(chat).WaitFor(chat)
    .WithEnvironment("AZURE_TENANT_ID", tenantId)
    .PublishAsHostedAgent(project);

builder.Build().Run();

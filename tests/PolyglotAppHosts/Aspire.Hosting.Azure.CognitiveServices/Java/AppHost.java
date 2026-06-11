import aspire.*;

void main() throws Exception {
        // Aspire TypeScript AppHost
        // For more information, see: https://aspire.dev
        var builder = DistributedApplication.CreateBuilder();
        var openai = builder.addAzureOpenAI("openai");
        openai.addDeployment("chat", "gpt-4o-mini", "2024-07-18");
        var api = builder.addContainer("api", "redis:latest");
        api.withCognitiveServicesRoleAssignments(openai, new AzureOpenAIRole[] { AzureOpenAIRole.COGNITIVE_SERVICES_OPEN_AIUSER });
        builder.build().run();
    }

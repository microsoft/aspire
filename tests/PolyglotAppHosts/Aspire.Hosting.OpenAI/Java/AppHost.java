import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var apiKey = builder.addParameter("openai-api-key", new AddParameterOptions().secret(true));
        var openai = builder.addOpenAI("openai")
            .withEndpoint("https://api.openai.com/v1")
            .withApiKey(apiKey);
        openai.addModel("chat-model", "gpt-4o-mini");
        builder.build().run();
    }

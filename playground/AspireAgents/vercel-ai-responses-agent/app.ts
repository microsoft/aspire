import { createAzure } from "@ai-sdk/azure";
import { DefaultAzureCredential, getBearerTokenProvider } from "@azure/identity";
import { generateText, stepCountIs, tool } from "ai";
import express, { type Request, type Response } from "express";
import { z } from "zod";

const app = express();
app.use(express.json());

const port = process.env.PORT ?? "8080";
const agentName = "vercel-ai-responses-agent";
const azureOpenAiScope = "https://cognitiveservices.azure.com/.default";

interface ResponsesRequest {
  agent?: {
    name?: string;
  };
  input?: ResponsesInput;
}

type ResponsesInput = string | ResponsesInputItem[];

interface ResponsesInputItem {
  role?: string;
  content?: string | ResponsesContentPart[];
}

interface ResponsesContentPart {
  type?: string;
  text?: string;
  content?: string;
}

function parseConnectionString(connectionString: string | undefined): Map<string, string> {
  const values = new Map<string, string>();
  for (const part of (connectionString ?? "").split(";")) {
    const separatorIndex = part.indexOf("=");
    if (separatorIndex > 0) {
      values.set(part.slice(0, separatorIndex).trim(), part.slice(separatorIndex + 1).trim());
    }
  }

  return values;
}

function getChatConnection(): { endpoint: string; deployment: string } {
  const values = parseConnectionString(process.env.ConnectionStrings__chat);
  const endpoint = values.get("Endpoint");
  const deployment = values.get("Deployment");
  if (!endpoint || !deployment) {
    throw new Error("ConnectionStrings__chat must contain Endpoint and Deployment.");
  }

  return { endpoint, deployment };
}

function normalizeAzureBaseUrl(endpoint: string): string {
  let baseUrl = endpoint.replace(/\/+$/, "");
  if (baseUrl.endsWith("/openai/v1")) {
    baseUrl = baseUrl.slice(0, -"/v1".length);
  } else if (!baseUrl.endsWith("/openai")) {
    baseUrl = `${baseUrl}/openai`;
  }

  return baseUrl;
}

function extractPrompt(input: ResponsesInput | undefined): string {
  if (typeof input === "string") {
    return input;
  }

  if (!Array.isArray(input)) {
    return "Hello, what can you do?";
  }

  const messages = input
    .filter((item) => item.role !== "assistant")
    .map((item) => extractContent(item.content))
    .filter((content) => content.length > 0);

  return messages.at(-1) ?? "Hello, what can you do?";
}

function extractContent(content: ResponsesInputItem["content"]): string {
  if (typeof content === "string") {
    return content;
  }

  if (!Array.isArray(content)) {
    return "";
  }

  return content
    .map((part) => part.text ?? part.content ?? "")
    .filter((text) => text.length > 0)
    .join("\n");
}

const { endpoint, deployment } = getChatConnection();
const tokenProvider = getBearerTokenProvider(new DefaultAzureCredential(), azureOpenAiScope);
const azure = createAzure({
  baseURL: normalizeAzureBaseUrl(endpoint),
  tokenProvider,
});

const getWeather = tool({
  description: "Get a weather forecast for a location.",
  inputSchema: z.object({
    location: z.string().describe("The location to get the weather for."),
  }),
  execute: async ({ location }) => `The weather in ${location} is sunny with a high of 22 C.`,
});

app.post("/v1/responses", async (req: Request<object, object, ResponsesRequest>, res: Response) => {
  const prompt = extractPrompt(req.body.input);
  const requestedAgentName = req.body.agent?.name ?? agentName;

  try {
    const result = await generateText({
      model: azure(deployment),
      system: `You are ${requestedAgentName}, a concise weather agent. Use tools when users ask for forecasts.`,
      prompt,
      tools: {
        get_weather: getWeather,
      },
      stopWhen: stepCountIs(5),
      providerOptions: {
        azure: {
          store: false,
        },
      },
    });

    res.json({
      id: result.providerMetadata?.azure?.responseId ?? `resp_${crypto.randomUUID().replaceAll("-", "")}`,
      object: "response",
      created_at: Math.floor(Date.now() / 1000),
      status: "completed",
      model: deployment,
      output: [
        {
          id: `msg_${crypto.randomUUID().replaceAll("-", "")}`,
          type: "message",
          status: "completed",
          role: "assistant",
          content: [
            {
              type: "output_text",
              text: result.text,
            },
          ],
        },
      ],
      output_text: result.text,
      usage: {
        input_tokens: result.usage.inputTokens,
        output_tokens: result.usage.outputTokens,
        total_tokens: result.usage.totalTokens,
      },
    });
  } catch (error) {
    console.error("Failed to invoke Vercel AI SDK agent.", error);
    res.status(500).json({
      error: {
        message: error instanceof Error ? error.message : "Unknown agent error.",
        type: "agent_error",
      },
    });
  }
});

app.get("/health", (_req, res) => {
  res.send("Healthy");
});

app.listen(Number.parseInt(port, 10), () => {
  console.log(`${agentName} listening on port ${port}`);
});

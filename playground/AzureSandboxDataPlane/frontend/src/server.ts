import { DefaultAzureCredential } from "@azure/identity";
import { SandboxGroupClient } from "@azure/containerapps-sandbox";
import express, { type Request, type Response } from "express";

function requiredEnvironmentVariable(name: string): string {
    const value = process.env[name];
    if (!value) {
        throw new Error(`The required environment variable '${name}' was not provided.`);
    }

    return value;
}

const sandboxes = new SandboxGroupClient(
    new DefaultAzureCredential(),
    requiredEnvironmentVariable("SANDBOXES_ENDPOINT"),
    requiredEnvironmentVariable("SANDBOXES_SUBSCRIPTIONID"),
    requiredEnvironmentVariable("SANDBOXES_RESOURCEGROUP"),
    requiredEnvironmentVariable("SANDBOXES_SANDBOXGROUPNAME"));

const app = express();
const port = Number(process.env.PORT ?? "3000");

app.use(express.json());
app.use(express.static(new URL("../public", import.meta.url).pathname));

app.post("/api/sandboxes", async (_request: Request, response: Response) => {
    let sandboxId: string | undefined;
    try {
        const poller = sandboxes.sandboxes.beginCreate({
            sourcesRef: {
                diskImage: {
                    name: "ubuntu",
                    isPublic: true
                }
            },
            resources: {
                cpu: "2000m",
                memory: "4096Mi",
                disk: "40960Mi"
            }
        });
        const sandbox = await poller.pollUntilDone();
        sandboxId = sandbox.id;

        const result = await sandboxes.sandboxes.exec(sandboxId, {
            command: "echo 'Hello from an Azure sandbox' && uname -a"
        });
        const deletePoller = sandboxes.sandboxes.beginDelete(sandboxId);
        await deletePoller.pollUntilDone();
        sandboxId = undefined;

        response.json({
            sandboxId: sandbox.id,
            stdout: result.stdout,
            stderr: result.stderr
        });
    } catch (error) {
        console.error(error);
        let cleanupError: string | undefined;
        if (sandboxId) {
            try {
                const deletePoller = sandboxes.sandboxes.beginDelete(sandboxId);
                await deletePoller.pollUntilDone();
            } catch (cleanupFailure) {
                console.error(cleanupFailure);
                cleanupError = cleanupFailure instanceof Error
                    ? cleanupFailure.message
                    : "Sandbox cleanup failed.";
            }
        }

        response.status(500).json({
            error: error instanceof Error ? error.message : "The sandbox request failed.",
            cleanupError
        });
    }
});

app.listen(port, () => {
    console.log(`Azure Sandbox data-plane playground listening on port ${port}.`);
});

import { existsSync, readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

import { createBuilder } from "./.modules/aspire.js";

const __dirname = dirname(fileURLToPath(import.meta.url));
const keyVaultInfrastructureJsonPath = resolve(__dirname, "azure-cdk-validation", "AspireKeyVaultInfrastructure.json");

type AzureCdkModules = {
    Stack: {
        query: (stacks: unknown[]) => {
            findFirst: <TResource>(descriptor: unknown) => TResource | undefined;
        };
    };
    KeyVault: {
        resourceType: string;
    };
    deserialize: (infrastructureJson: string) => unknown[];
    serialize: (stacks: unknown[]) => unknown;
    resourceDescriptor: <TResource>(resourceType: string, options: { from: (resource: unknown) => TResource }) => unknown;
};

type AzureCdkKeyVault = { properties: { publicNetworkAccess?: "Disabled" | "Enabled" } };

const builder = await createBuilder();

await builder.addAzureKeyVault("mykv").configureInfrastructure(async infrastructure => {
    const cdk = await loadAzureCdkModules();
    const stacks = cdk.deserialize(readFileSync(keyVaultInfrastructureJsonPath, "utf8"));
    const keyVault = cdk.Stack.query(stacks).findFirst<AzureCdkKeyVault>(
        cdk.resourceDescriptor<AzureCdkKeyVault>(cdk.KeyVault.resourceType, {
            from: resource => resource as AzureCdkKeyVault,
        }),
    );

    if (!keyVault) {
        throw new Error("Expected the Aspire Key Vault snapshot to contain a Key Vault resource.");
    }

    keyVault.properties.publicNetworkAccess = "Disabled";

    await infrastructure.useInfrastructureJson(`${JSON.stringify(cdk.serialize(stacks), null, 2)}\n`);
});

await builder.build().run();

async function loadAzureCdkModules(): Promise<AzureCdkModules> {
    const azureCdkDir = process.env.AZURE_CDK_DIR;

    if (!azureCdkDir) {
        throw new Error("Set AZURE_CDK_DIR to the root of a local Azure/azure-cdk clone.");
    }

    const workspaceFilePath = resolve(azureCdkDir, "pnpm-workspace.yaml");
    const demoPackageJsonPath = resolve(azureCdkDir, "packages", "demo", "package.json");

    if (!existsSync(workspaceFilePath) || !existsSync(demoPackageJsonPath)) {
        throw new Error(
            [
                "AZURE_CDK_DIR must point to the root of a local Azure/azure-cdk clone.",
                "Expected to find:",
                `  - ${workspaceFilePath}`,
                `  - ${demoPackageJsonPath}`,
            ].join("\n"),
        );
    }

    const requireFromDemo = createRequire(demoPackageJsonPath);

    const coreModule = await import(pathToFileURL(requireFromDemo.resolve("@azure-cdk/core")).href);
    const keyVaultModule = await import(pathToFileURL(requireFromDemo.resolve("@azure-cdk/keyvault")).href);
    const serializationModule = await import(pathToFileURL(requireFromDemo.resolve("@azure-cdk/serialization")).href);

    return {
        Stack: getNamedExport<AzureCdkModules["Stack"]>(coreModule, "@azure-cdk/core", "Stack"),
        KeyVault: getNamedExport<AzureCdkModules["KeyVault"]>(
            keyVaultModule,
            "@azure-cdk/keyvault",
            "KeyVault",
        ),
        deserialize: getNamedExport<AzureCdkModules["deserialize"]>(
            serializationModule,
            "@azure-cdk/serialization",
            "deserialize",
        ),
        serialize: getNamedExport<AzureCdkModules["serialize"]>(
            serializationModule,
            "@azure-cdk/serialization",
            "serialize",
        ),
        resourceDescriptor: getNamedExport<AzureCdkModules["resourceDescriptor"]>(
            coreModule,
            "@azure-cdk/core",
            "resourceDescriptor",
        ),
    };
}

function getNamedExport<T>(module: unknown, specifier: string, exportName: string): T {
    if (module && typeof module === "object") {
        const exports = module as Record<string, unknown>;
        if (exportName in exports) {
            return exports[exportName] as T;
        }

        const defaultExport = exports.default;
        if (defaultExport && typeof defaultExport === "object") {
            const defaultExports = defaultExport as Record<string, unknown>;
            if (exportName in defaultExports) {
                return defaultExports[exportName] as T;
            }
        }
    }

    throw new Error(`Unable to load '${exportName}' from '${specifier}'.`);
}

# Azure CDK Key Vault property example

Prerequisites: Node.js, Corepack, and a local `https://github.com/Azure/azure-cdk` clone referenced by `AZURE_CDK_DIR`. That directory must contain `pnpm-workspace.yaml` and `packages/demo/package.json`.

Prepare Azure CDK:

```bash
git clone https://github.com/Azure/azure-cdk.git ~/src/azure-cdk
cd ~/src/azure-cdk
corepack enable
corepack pnpm install
corepack pnpm build
```

Validate the sample:

```bash
cd playground/TypeScriptApps/AzureCdkRoundtrip
npm install --ignore-scripts --no-audit --no-fund
AZURE_CDK_DIR=~/src/azure-cdk ../../../dotnet.sh run --project ../../../src/Aspire.Cli/Aspire.Cli.csproj -- restore --non-interactive --apphost apphost.ts
npx tsc --noEmit --project tsconfig.json
```

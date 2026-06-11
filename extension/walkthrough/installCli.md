# Install the Aspire CLI

The Aspire CLI is a cross-platform tool that helps you create, run, and manage Aspire applications.

### Latest release

> [Run install in terminal](command:aspire-vscode.installCliStable)

| Platform | Command |
|----------|---------|
| macOS / Linux | `curl -sSL https://aspire.dev/install.sh \| bash` |
| Windows | `irm https://aspire.dev/install.ps1 \| iex` |

### Daily build (preview)

> [Run daily install in terminal](command:aspire-vscode.installCliDaily)

| Platform | Command |
|----------|---------|
| macOS / Linux | `curl -sSL https://aspire.dev/install.sh \| bash -s -- -q dev` |
| Windows | `iex "& { $(irm https://aspire.dev/install.ps1) } -Quality dev"` |

### Verify

After installing, click **Verify installation** in the sidebar or run `aspire --version`.

> For more details, see the [Aspire CLI installation guide](https://aspire.dev/get-started/install-cli/).

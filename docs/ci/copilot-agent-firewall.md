# Copilot agent firewall probe

This probe records the network access required to restore, build, test, and run Aspire from the GitHub Copilot coding-agent shell. Keep the current setup-time restore in `.github/workflows/copilot-setup-steps.yml` until the required agent-shell firewall entries have been applied by an administrator and validated in at least three fresh Copilot sessions.

Copilot setup steps and the interactive agent shell have different network boundaries. The setup workflow runs before the coding-agent shell is available, and its network access can succeed even when the later agent shell is blocked by the Copilot firewall. This probe must be run from the agent shell without disabling the firewall, editing `NuGet.config`, or changing `global.json`.

## Probe commands

Run these commands from the repository root in a fresh Copilot coding-agent session:

```bash
cd /home/runner/work/aspire/aspire
mkdir -p /tmp/aspire-copilot-firewall-probe

{
  echo "OS"
  uname -a
  cat /etc/os-release
  echo
  echo ".NET"
  dotnet --info
  echo
  echo "NuGet sources"
  dotnet nuget list source
  echo
  echo "Git commit"
  git rev-parse HEAD
} | tee /tmp/aspire-copilot-firewall-probe/00-environment.log

firewall_log="${COPILOT_AGENT_FIREWALL_LOG_FILE:-/home/runner/work/_temp/runtime-logs/fw.jsonl}"
firewall_start_size="$(stat -c%s "$firewall_log")"
printf '%s\n' "$firewall_start_size" > /tmp/aspire-copilot-firewall-probe/firewall-start-size.txt

dotnet nuget locals http-cache --clear
dotnet nuget locals global-packages --clear
dotnet nuget locals temp --clear
dotnet nuget locals plugins-cache --clear
rm -rf artifacts/bin artifacts/obj artifacts/tmp artifacts/package-cache artifacts/packages artifacts/TestResults

./build.sh -restore 2>&1 | tee /tmp/aspire-copilot-firewall-probe/01-build-restore.log
```

Only continue after `./build.sh -restore` succeeds. If it fails, stop and record the exact blocker.

```bash
dotnet build src/Aspire.Hosting/Aspire.Hosting.csproj --no-restore 2>&1 | tee /tmp/aspire-copilot-firewall-probe/02-build-hosting.log

dotnet test --project tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj --no-launch-profile -- \
  --filter-not-trait "quarantined=true" \
  --filter-not-trait "outerloop=true" \
  2>&1 | tee /tmp/aspire-copilot-firewall-probe/03-test-hosting.log

timeout 90s dotnet run --project playground/TestShop/TestShop.AppHost/TestShop.AppHost.csproj --no-launch-profile \
  2>&1 | tee /tmp/aspire-copilot-firewall-probe/04-run-testshop-apphost.log || true

python - <<'PY'
import json
import os

firewall_log = os.environ.get("COPILOT_AGENT_FIREWALL_LOG_FILE", "/home/runner/work/_temp/runtime-logs/fw.jsonl")
with open("/tmp/aspire-copilot-firewall-probe/firewall-start-size.txt", encoding="utf-8") as f:
    start = int(f.read().strip())

with open(firewall_log, "rb") as f:
    f.seek(start)
    for line in f:
        try:
            entry = json.loads(line)
        except json.JSONDecodeError:
            continue

        if entry.get("blocked"):
            print(json.dumps(entry, sort_keys=True))
PY
```

## Observed result

Session evidence from `https://github.com/microsoft/aspire/actions/runs/33795336775` (`COPILOT_AGENT_SESSION_ID=b56fcffe-6721-48da-9e55-fad2b96bfd5c`, commit `952569852f1cab078e655a2cd2cc0716034c4985`) reached the restore step and then stopped at the first required blocked endpoint:

```text
OS: Ubuntu 24.04.4 LTS, Linux 6.17.0-1022-azure x64
.NET SDK: 10.0.400 from /home/runner/work/aspire/aspire/.dotnet/sdk/10.0.400/
NuGet sources: dotnet-public, dotnet-eng, dotnet-tools, dotnet9, dotnet10, dotnet-libraries, dotnet9-transport
```

`./build.sh -restore` failed while restoring the Arcade SDK:

```text
Failed to download package 'Microsoft.DotNet.Arcade.Sdk.10.0.0-beta.26423.1' from
'https://pkgs.dev.azure.com/dnceng/.../_packaging/.../nuget/v3/flat2/microsoft.dotnet.arcade.sdk/10.0.0-beta.26423.1/microsoft.dotnet.arcade.sdk.10.0.0-beta.26423.1.nupkg'.
Resource temporarily unavailable (ukkvsblobprodcus352.vsblob.vsassets.io:443)
```

The firewall log recorded the corresponding DNS block:

```json
{"blocked":true,"blockedAt":"dns","because":"NotInAllowList","domains":"ukkvsblobprodcus352.vsblob.vsassets.io.","msg":"DNS BLOCKED","ruleSource":"NotInAllowList","ruleSourceComment":"Domain doesn't match any allowlist prefixes"}
```

Because restore did not complete, this session did not produce meaningful build, test, or AppHost runtime-download evidence. Do not add speculative firewall entries for those later operations from this run.

## Administrator actions

Required allowlist entry observed by the probe:

| Endpoint | Required for | Triggering command | Dependency or tool | Required? |
| --- | --- | --- | --- | --- |
| `ukkvsblobprodcus352.vsblob.vsassets.io` | DNS resolution and HTTPS download on port 443 | `./build.sh -restore` | Azure Artifacts package blob for `Microsoft.DotNet.Arcade.Sdk.10.0.0-beta.26423.1` | Yes |

The probe also observed these already-allowed or non-build endpoints. They are not proposed as new firewall entries from this evidence:

| Endpoint | Observation | Required? |
| --- | --- | --- |
| `pkgs.dev.azure.com` | Configured NuGet feed metadata and package URL requests reached the proxy before the blob host was blocked. | Already allowed for restore feed access |
| `dnceng.pkgs.visualstudio.com` | Configured NuGet feed metadata requests reached the proxy. | Already allowed for restore feed access |
| `dc.services.visualstudio.com` | Telemetry request observed during the failed restore probe. | Optional telemetry; do not allow solely for restore/build/test/run |
| `telemetry.enterprise.githubcopilot.com` | Copilot agent telemetry request observed. | Agent telemetry/control-plane traffic; not an Aspire dependency |
| `api.enterprise.githubcopilot.com` | Copilot agent control-plane requests observed. | Agent control plane; not an Aspire dependency |

After an administrator applies the required entry, rerun the full probe in a new Copilot session. Record any additional blocked endpoints with the command that triggered them, the package or tool that required them, and whether they are required or optional. Removing setup-time restore is intentionally left to a later issue after administrator changes are applied and the complete restore, build, test, and run probe succeeds in at least three fresh sessions.

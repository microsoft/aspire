#!/usr/bin/env bash
set -euo pipefail

expected_commit="${1:?Expected checkout commit is required}"
packages="${2:?PR NuGet package directory is required}"
fixture_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
diagnostics="$PWD/diagnostics"
app_root="$PWD/NixSmoke"
app_host="$app_root/NixSmoke.AppHost/NixSmoke.AppHost.csproj"
package_root="${NIX_ASPIRE_PACKAGE:?}/lib/aspire-cli"
active=false
mkdir -p "$diagnostics"

cleanup() {
    status=$?
    trap - EXIT
    if [[ "$active" == true ]]; then
        if ! timeout 90 aspire stop --apphost "$app_host" --non-interactive --nologo \
            > "$diagnostics/cleanup.log" 2>&1; then
            echo "ERROR: AppHost cleanup failed; see cleanup.log." >&2
            status=1
        fi
    fi
    if [[ -n "${NIX_SMOKE_PROBE_NAME:-}" ]] && docker inspect "$NIX_SMOKE_PROBE_NAME" >/dev/null 2>&1; then
        docker logs "$NIX_SMOKE_PROBE_NAME" > "$diagnostics/probe-cleanup.log" 2>&1
        docker rm -f "$NIX_SMOKE_PROBE_NAME" >/dev/null
    fi
    if [[ -d "$ASPIRE_HOME/logs" ]]; then
        cp -R "$ASPIRE_HOME/logs" "$diagnostics/cli-logs"
    fi
    exit "$status"
}
trap cleanup EXIT

test "$(id -u)" -ne 0
test -z "${NIX_LD:-}"
test -z "${NIX_LD_LIBRARY_PATH:-}"
test -z "${LD_LIBRARY_PATH:-}"
test ! -w "$NIX_ASPIRE_PACKAGE"
jq -e '.source == "nix"' "$package_root/.aspire-install.json" >/dev/null

cli_version="$(aspire --version)"
printf '%s\n' "$cli_version" | tee "$diagnostics/version.txt"
case "$cli_version" in
    *"${expected_commit:0:7}"*) ;;
    *) echo "ERROR: Installed CLI does not match checkout $expected_commit." >&2; exit 1 ;;
esac
dotnet --info > "$diagnostics/dotnet-info.txt"

# PT_INTERP is normally /lib64/ld-linux-x86-64.so.2 or /lib/ld-linux-aarch64.so.1.
# Requiring that exact interpreter to be absent prevents an FHS loader or nix-ld
# from making the unpatched control work and masking the installation bug.
interpreter="$(patchelf --print-interpreter "$(command -v aspire-unpatched-dcp)")"
printf '%s\n' "$interpreter" > "$diagnostics/unpatched-interpreter.txt"
test ! -e "$interpreter"
control_status=0
aspire-unpatched-dcp version > "$diagnostics/unpatched-dcp.log" 2>&1 || control_status=$?
if [[ "$control_status" != 127 ]]; then
    echo "ERROR: Unpatched DCP did not fail at interpreter loading (exit $control_status)." >&2
    exit 1
fi

template_version="${cli_version%%+*}"
# Use the project-mode empty template from the PR's template package so both
# the AppHost and its service build with the Nix-provided SDK.
features__showAllTemplates=true aspire new aspire --name NixSmoke --output "$app_root" \
    --source "$packages" --version "$template_version" --localhost-tld false \
    --suppress-agent-init --non-interactive --nologo > "$diagnostics/new.log" 2>&1
dotnet new web --framework net10.0 --name NixSmoke.Service --output "$app_root/NixSmoke.Service" \
    --no-restore > "$diagnostics/service-new.log" 2>&1
cp "$fixture_dir/AppHost.cs" "$app_root/NixSmoke.AppHost/AppHost.cs"
cp "$fixture_dir/Service.cs" "$app_root/NixSmoke.Service/Program.cs"

for iteration in 1 2; do
    run_dir="$diagnostics/run-$iteration"
    mkdir -p "$run_dir"
    export NIX_SMOKE_PROBE_NAME="aspire-nix-probe-$RANDOM-$RANDOM"
    docker ps -aq | sort > "$run_dir/containers-before.txt"
    active=true
    timeout 240 aspire start --apphost "$app_host" --launch-profile http --isolated \
        --format json --non-interactive --nologo > "$run_dir/start.json" 2> "$run_dir/start.log"
    aspire wait service --status healthy --timeout 180 --apphost "$app_host" \
        --non-interactive --nologo > "$run_dir/wait.log" 2>&1
    aspire wait probe --status up --timeout 180 --apphost "$app_host" \
        --non-interactive --nologo > "$run_dir/probe-wait.log" 2>&1
    aspire describe --apphost "$app_host" --format json --include-hidden \
        --non-interactive --nologo > "$run_dir/resources.json"
    aspire describe service --apphost "$app_host" --format json \
        --non-interactive --nologo > "$run_dir/service.json"

    dashboard_url="$(jq -er '.dashboardUrl' "$run_dir/start.json")"
    # The startup response is { "appHostPid": 123, "cliPid": 122, "dashboardUrl": "..." }.
    # Do not retain the ephemeral dashboard login token in uploaded diagnostics.
    jq '.dashboardUrl |= split("?")[0]' "$run_dir/start.json" > "$run_dir/start-sanitized.json"
    mv "$run_dir/start-sanitized.json" "$run_dir/start.json"
    jq -e '.resources | length == 1' "$run_dir/service.json" >/dev/null
    service_url="$(jq -er '.resources[0].urls[] | select(.name == "http") | .url' "$run_dir/service.json")"
    test "$(curl --fail --silent --show-error --max-time 10 "$service_url")" = "aspire-nix-runtime-ok"
    curl --fail --silent --show-error --max-time 10 "$dashboard_url" > /dev/null

    docker exec "$NIX_SMOKE_PROBE_NAME" /bin/sh -c \
        'curl --fail --silent --show-error --max-time 15 "$SERVICE_URL"' \
        > "$run_dir/probe-response.txt" 2> "$run_dir/probe.log"
    test "$(cat "$run_dir/probe-response.txt")" = "aspire-nix-runtime-ok"

    dcp="$(readlink -f "$package_root/bundle/dcp/dcp")"
    dashboard="$(readlink -f "$package_root/bundle/dashboard/Aspire.Dashboard")"
    : > "$run_dir/bundle-executables.txt"
    jq -r '.appHostPid, .cliPid' "$run_dir/start.json" > "$run_dir/pids.txt"
    jq -r '.resources[].properties["executable.pid"] // empty' "$run_dir/resources.json" >> "$run_dir/pids.txt"
    # /proc/<pid>/exe must resolve to the package's immutable versions/<id>/ payload.
    # A process can exit while inspecting /proc; only successfully resolved entries are sampled.
    for executable in /proc/[0-9]*/exe; do
        if target="$(readlink "$executable" 2>/dev/null)" && [[ "$target" == "$dcp" || "$target" == "$dashboard" ]]; then
            printf '%s\n' "$target" >> "$run_dir/bundle-executables.txt"
            printf '%s\n' "${executable#/proc/}" | cut -d / -f 1 >> "$run_dir/pids.txt"
        fi
    done
    grep -Fx "$dcp" "$run_dir/bundle-executables.txt"
    grep -Fx "$dashboard" "$run_dir/bundle-executables.txt"
    test ! -e "$ASPIRE_HOME/versions"
    test ! -e "$ASPIRE_HOME/bundle"
    test ! -e "$package_root/bundle/.leases"

    timeout 90 aspire stop --apphost "$app_host" --non-interactive --nologo > "$run_dir/stop.log" 2>&1
    active=false
    # DCP can outlive the AppHost briefly to finish cleanup. Observe both its
    # recorded processes and the disposable runner's container set before restarting.
    for _ in $(seq 1 60); do
        live=false
        while read -r pid; do
            if kill -0 "$pid" 2>/dev/null; then
                live=true
            fi
        done < "$run_dir/pids.txt"
        docker ps -aq | sort > "$run_dir/containers-after.txt"
        if [[ "$live" == false ]] && cmp -s "$run_dir/containers-before.txt" "$run_dir/containers-after.txt"; then
            break
        fi
        sleep 1
    done
    test "$live" == false
    cmp "$run_dir/containers-before.txt" "$run_dir/containers-after.txt"
    echo "Nix runtime startup, tunnel traffic, and cleanup passed (iteration $iteration)."
done

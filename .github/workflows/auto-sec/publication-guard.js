// Harden gh-aw v0.89.17's live streams and post-agent publication steps.
// Apply this pass after compilation until the compiler supports these boundaries:
// https://github.com/github/gh-aw/blob/v0.89.17/pkg/workflow/compiler_yaml_post_agent.go
const fs = require('node:fs');
const path = require('node:path');

const PUBLICATION_GUARD = "steps.auto_sec_scrub.outcome == 'success' && steps.auto_sec_scrub.outputs.publication_ready == 'true'";
const SAFE_OUTPUT_PATHS = ['/tmp/gh-aw/agent_output.json', '/tmp/gh-aw/safeoutputs.jsonl'];
const SAFE_PATCH_PATHS = ['/tmp/gh-aw/aw-auto-sec-security-updates.patch', '/tmp/gh-aw/aw-microsoft-aspire-auto-sec-security-updates.patch'];
const PRIVATE_TELEMETRY_STEPS = new Set([
    'Append agent step summary',
    'Parse agent logs for step summary',
    'Parse MCP Gateway logs for step summary',
    'Print firewall logs',
    'Parse token usage for step summary',
    'Print AWF reflect summary',
    'Generate observability summary',
]);
const PRIVATE_DIAGNOSTIC_STEPS = new Set(['Detect agent errors', 'Redact secrets in logs']);
const PRIVATE_STDIO_PREFIX = 'exec >/dev/null 2>&1';

function withholdTelemetry(step, newline, condition) {
    const name = /^      - name: ([^\r\n]+)\r?$/m.exec(step)?.[1];
    const id = /^        id: ([^\r\n]+)\r?$/m.exec(step)?.[1];
    return `      - name: ${name}${newline}${id ? `        id: ${id}${newline}` : ''}`
        + (condition ? `        if: ${condition}${newline}` : '')
        + `        run: echo "Auto-sec withholds agent-writable telemetry."${newline}`;
}

function suppressPrivateStreams(step, newline, gateway) {
    // Redirect the host shell before launching AWF/the harness, including background
    // gateway children. Deleting logs later cannot retract already forwarded chunks.
    // https://github.com/github/gh-aw/blob/v0.89.17/actions/setup/js/process_runner.cjs
    const runs = [...step.matchAll(/^        run: ([^\r\n]+)\r?$/gm)];
    if (runs.length !== 1 || /^        uses:/m.test(step)) {
        throw new Error('auto-sec publication guard: unexpected private execution step');
    }
    const run = runs[0];
    if (run[1] === '|') {
        const body = /^        run: \|\r?\n(?=          )(?:          [^\r\n]*(?:\r?\n|(?![\s\S]))|[ \t]*\r?\n)+/m.exec(step);
        if (!body) {
            throw new Error('auto-sec publication guard: missing private execution body');
        }
        if (gateway) {
            // Register the trusted, freshly generated gateway credential before
            // silencing the shell. Otherwise later step environment banners can
            // disclose it because the runner never receives the add-mask command.
            const masks = [...step.matchAll(/^          echo "::add-mask::\$\{MCP_GATEWAY_AGENT_ID\}"\r?\n/gm)];
            if (masks.length !== 1) {
                throw new Error('auto-sec publication guard: expected gateway credential mask');
            }
            const afterMask = step.slice(masks[0].index + masks[0][0].length);
            return afterMask.startsWith(`          ${PRIVATE_STDIO_PREFIX}${newline}`)
                ? step
                : step.replace(masks[0][0], `${masks[0][0]}          ${PRIVATE_STDIO_PREFIX}${newline}`);
        }
        if (!body[0].startsWith(`        run: |${newline}          ${PRIVATE_STDIO_PREFIX}${newline}`)) {
            return step.replace(/^        run: \|\r?\n/m,
                `        run: |${newline}          ${PRIVATE_STDIO_PREFIX}${newline}`);
        }
        return step;
    }
    // The pinned compiler emits these helpers as plain one-line shell commands:
    //   run: bash "${RUNNER_TEMP}/gh-aw/actions/stop_mcp_gateway.sh" "$GATEWAY_PID"
    // Other YAML scalar encodings require an explicit review rather than guessing.
    if (!run[1].startsWith('bash ')) {
        throw new Error('auto-sec publication guard: unsupported private execution scalar');
    }
    return step.replace(run[0],
        `        run: |${newline}          ${PRIVATE_STDIO_PREFIX}${newline}          ${run[1]}`);
}

function guardPublications(workflow) {
    // Generated jobs have two-space indentation, steps six, and step properties eight:
    //   agent:
    //     steps:
    //       - name: Scrub auto-sec agent transcript and outputs
    //         id: auto_sec_scrub
    //         if: always()
    // Match only these structural lines, never the indented shell/JavaScript bodies.
    const job = /^  agent:\r?\n([\s\S]*?)(?=^  [A-Za-z_][\w-]*:|(?![\s\S]))/m.exec(workflow);
    if (!job) {
        throw new Error('auto-sec publication guard: missing agent job');
    }
    const steps = [...job[0].matchAll(/^      - [^\r\n]+[\s\S]*?(?=^      - |$(?![\s\S]))/gm)];
    const scrubs = steps.filter(step => /^        id: auto_sec_scrub\r?$/m.test(step[0]));
    if (scrubs.length !== 1) {
        throw new Error('auto-sec publication guard: expected one scrub step');
    }
    const scrubIndex = steps.indexOf(scrubs[0]);
    const executions = steps.filter(step => /^        id: agentic_execution\r?$/m.test(step[0]));
    const gateways = steps.filter(step => /^      - name: Start MCP Gateway\r?$/m.test(step[0]));
    if (executions.length !== 1 || gateways.length !== 1
        || steps.indexOf(gateways[0]) >= steps.indexOf(executions[0]) || steps.indexOf(executions[0]) >= scrubIndex) {
        throw new Error('auto-sec publication guard: expected private execution before scrub');
    }
    for (const name of ['Append agent step summary', 'Ingest agent output', 'Upload agent output fallback artifact', 'Upload agent artifacts']) {
        const matches = steps.filter(step => step[0].startsWith(`      - name: ${name}\n`) || step[0].startsWith(`      - name: ${name}\r\n`));
        if (matches.length !== 1 || steps.indexOf(matches[0]) <= scrubIndex) {
            throw new Error(`auto-sec publication guard: expected ${name} after scrub`);
        }
    }

    let guardedJob = job[0];
    // Guard every remaining step, not just today's known publishers. A new generated
    // summary or upload must not bypass the boundary on a compiler upgrade.
    for (const step of steps.slice(steps.indexOf(gateways[0])).reverse()) {
        if (step === scrubs[0]) {
            continue;
        }
        const condition = /^        if: ([^\r\n]+)\r?$/m.exec(step[0]);
        const newline = /\r?\n/.exec(step[0])?.[0] ?? (job[0].startsWith('  agent:\r\n') ? '\r\n' : '\n');
        let guardedStep = step[0];
        const name = /^      - name: ([^\r\n]+)\r?$/m.exec(step[0])?.[1];
        if (steps.indexOf(step) < scrubIndex) {
            if (PRIVATE_DIAGNOSTIC_STEPS.has(name)) {
                // These parsers can echo agent-written error text before scrubbing.
                // Withhold their classifications too; the runner still records the
                // execution step's real exit status, without parsing private logs.
                guardedStep = withholdTelemetry(step[0], newline, condition?.[1]);
            } else if (name !== 'Mount MCP servers as CLIs' || steps.indexOf(step) >= steps.indexOf(executions[0])) {
                guardedStep = suppressPrivateStreams(step[0], newline, step === gateways[0]);
            }
            guardedJob = guardedJob.slice(0, step.index) + guardedStep + guardedJob.slice(step.index + step[0].length);
            continue;
        }
        if (condition) {
            if (!condition[1].endsWith(` && ${PUBLICATION_GUARD}`)) {
                guardedStep = guardedStep.replace(condition[0], `        if: (${condition[1]}) && ${PUBLICATION_GUARD}${condition[0].endsWith('\r') ? '\r' : ''}`);
            }
        } else {
            // Insert after the header, including a one-line step at EOF. Searching
            // for any newline can instead insert into a mixed-newline script body.
            guardedStep = step[0].replace(/^([^\r\n]+)(?:\r?\n|$)/,
                `$1${newline}        if: (success()) && ${PUBLICATION_GUARD}${newline}`);
        }
        const guardedConditions = [...guardedStep.matchAll(/^        if:([^\r\n]*)\r?$/gm)];
        const guardedCondition = guardedConditions[0]?.[1].trim();
        if (guardedConditions.length !== 1 || !guardedCondition?.endsWith(` && ${PUBLICATION_GUARD}`)) {
            throw new Error('auto-sec publication guard: expected one guarded step condition');
        }
        if (PRIVATE_TELEMETRY_STEPS.has(name)) {
            // Deleted telemetry must not become fabricated zero metrics, nor should
            // framework summaries read agent-writable files outside the artifact root.
            // https://github.com/github/gh-aw/blob/v0.89.17/actions/setup/js/generate_observability_summary.cjs
            guardedStep = withholdTelemetry(step[0], newline, guardedCondition);
        }
        if (/^      - name: Upload agent (?:output fallback artifact|artifacts)\r?$/m.test(step[0])) {
            // The generic compiler upload includes writable prompts, telemetry and logs.
            // Publish only scrubbed outputs and the two authenticated patch filenames.
            const paths = SAFE_OUTPUT_PATHS.concat(step[0].startsWith('      - name: Upload agent artifacts') ? SAFE_PATCH_PATHS : []);
            // Literal scalars can contain unindented blank lines and end at EOF:
            //   path: |\n            /tmp/output\n\n            /tmp/private
            // Consume the complete scalar so a trailing path cannot survive.
            // https://yaml.org/spec/1.2.2/#812-literal-style
            const pathBlock = /^          path: \|\r?\n(?=            )(?:            [^\r\n]*(?:\r?\n|(?![\s\S]))|[ \t]*\r?\n)+/m;
            if (!pathBlock.test(guardedStep)) {
                throw new Error('auto-sec publication guard: expected upload path block');
            }
            guardedStep = guardedStep.replace(pathBlock, `          path: |${newline}${paths.map(file => `            ${file}${newline}`).join('')}`);
        }
        guardedJob = guardedJob.slice(0, step.index) + guardedStep + guardedJob.slice(step.index + step[0].length);
    }
    return workflow.slice(0, job.index) + guardedJob + workflow.slice(job.index + job[0].length);
}

if (require.main === module) {
    const filename = path.join(__dirname, '..', 'auto-sec.lock.yml');
    fs.writeFileSync(filename, guardPublications(fs.readFileSync(filename, 'utf8')));
}

module.exports = { guardPublications, PUBLICATION_GUARD };

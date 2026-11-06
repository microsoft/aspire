// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// @ts-check

const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const reportTemplates = [
    "agent_failure_issue.md",
    "agent_failure_comment.md",
    "failed_jobs_issue.md",
    "missing_data_issue.md",
    "missing_tool_issue.md",
];

/**
 * Installs source-run context in a private copy of gh-aw's operational report templates.
 * @param {Record<string, string | undefined>} environment Trusted conclusion-job environment.
 * @returns {string} The installed template directory.
 */
function prepareReportTemplates(environment) {
    const runnerTemp = environment.RUNNER_TEMP;
    const githubEnv = environment.GITHUB_ENV;
    if (!runnerTemp || !githubEnv || /[\r\n]/.test(runnerTemp)) {
        throw new Error("Report preparation requires RUNNER_TEMP without newlines and a GITHUB_ENV file.");
    }

    const repository = environment.GITHUB_REPOSITORY ?? "";
    if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository)) {
        throw new Error("Report preparation requires a valid GITHUB_REPOSITORY.");
    }
    const server = new URL(environment.GITHUB_SERVER_URL ?? "");
    if (server.protocol !== "https:" || server.username || server.password ||
        server.pathname !== "/" || server.search || server.hash) {
        throw new Error("Report preparation requires an HTTPS GitHub server origin.");
    }
    const event = environment.SOURCE_EVENT;
    if (event !== "workflow_run" && event !== "workflow_dispatch") {
        throw new Error("Report preparation requires a supported analysis event.");
    }

    const id = environment.SOURCE_RUN_ID ?? "";
    const validId = /^[1-9][0-9]{0,18}$/.test(id) && BigInt(id) <= 9223372036854775807n;
    if (!validId && event === "workflow_run") {
        throw new Error("The triggering CI run ID must be canonical positive 64-bit decimal text.");
    }
    let context;
    if (validId) {
        // Dispatch input is only a request: collection may fail before verifying its identity.
        const label = event === "workflow_run" ? "Original CI run" : "Requested CI run";
        context = `[automated] ${label}: [${id}](${server.origin}/${repository}/actions/runs/${id})`;
    } else {
        console.warn("::warning::Cannot link the requested CI run: run_id must be canonical positive 64-bit decimal text.");
        context = "[automated] Requested CI run unavailable: invalid workflow_dispatch run_id.";
    }

    // The pinned runtime explicitly supports GH_AW_PROMPTS_DIR. Keep upstream
    // files untouched, including their analyzer-run links, and select overrides
    // only after every required template has been prepared.
    // https://github.com/github/gh-aw-actions/blob/f3b81cdb3070066a47faa9bdf440f4f1dc78b1f0/setup/js/messages_core.cjs
    const source = environment.GH_AW_PROMPTS_DIR || path.join(runnerTemp, "gh-aw", "prompts");
    const originals = reportTemplates.map(name => [name, fs.readFileSync(path.join(source, name), "utf8")]);
    const destination = fs.mkdtempSync(path.join(runnerTemp, "ci-failure-report-prompts-"));
    fs.cpSync(source, destination, { recursive: true });
    for (const [name, text] of originals) {
        fs.writeFileSync(path.join(destination, name), `${context}${os.EOL}${os.EOL}${text}`);
    }
    fs.appendFileSync(githubEnv, `GH_AW_PROMPTS_DIR=${destination}${os.EOL}`);
    return destination;
}

module.exports = { prepareReportTemplates };

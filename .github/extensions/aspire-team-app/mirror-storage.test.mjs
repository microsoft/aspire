import assert from "node:assert/strict";
import { mkdtemp, readdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

test("mirror state survives module reload, replaces atomically and exposes corrupt state", async () => {
  const directory = await mkdtemp(join(tmpdir(), "aspire-mirror-state-"));
  const originalHome = process.env.COPILOT_HOME;
  process.env.COPILOT_HOME = directory;
  try {
    const first = await import(`./state.mjs?mirror-storage=${Date.now()}`);
    assert.equal(await first.loadMirrorState(), null);
    const record = { level: "warning", firstObservedAt: "2026-09-17T00:00:00Z", notifications: [{ id: "warning-1" }] };
    await first.saveMirrorState(record);
    const second = await import(`./state.mjs?mirror-storage-restart=${Date.now()}`);
    assert.deepEqual(await second.loadMirrorState(), record);
    await second.saveMirrorState({ ...record, level: "critical" });
    assert.equal((await first.loadMirrorState()).level, "critical");
    const artifacts = join(directory, "extensions", "aspire-team-app", "artifacts");
    assert.deepEqual(await readdir(artifacts), ["mirror-main.json"]);
    await writeFile(join(artifacts, "mirror-main.json"), "{invalid");
    await assert.rejects(second.loadMirrorState(), /Could not read mirror monitor state/);
  } finally {
    if (originalHome === undefined) delete process.env.COPILOT_HOME;
    else process.env.COPILOT_HOME = originalHome;
    await rm(directory, { recursive: true, force: true });
  }
});

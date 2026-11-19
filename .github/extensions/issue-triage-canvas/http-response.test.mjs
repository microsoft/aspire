import assert from "node:assert/strict";
import { createServer } from "node:http";
import test from "node:test";

import { respondWithError, toCloseFailure } from "./http-response.mjs";

const leakyError = Object.assign(
  new Error("Command failed: gh api repos/microsoft/aspire/issues/1 at C:\\Users\\secret\\work\\token.txt"),
  { stderr: "internal stderr: /home/secret/path with token ghp_example" });
const leakPattern = /secret|ghp_example|Command failed|internal stderr|\bat .*\.mjs|node_modules/;

async function request(handler) {
  const server = createServer((req, res) => {
    void handler(req, res);
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));

  try {
    const response = await fetch(`http://127.0.0.1:${server.address().port}/`);
    return { status: response.status, headers: response.headers, body: await response.text() };
  }
  finally {
    await new Promise((resolve) => server.close(resolve));
  }
}

test("a throwing request handler returns a 500 that never contains the raw error, path, or stack", async () => {
  const logged = [];
  const response = await request((req, res) => respondWithError(res, leakyError, async (message, options) => logged.push({ message, options })));

  assert.equal(response.status, 500);
  assert.match(response.headers.get("content-type"), /^application\/json/);
  assert.doesNotMatch(response.body, leakPattern);
  assert.ok(!response.body.includes(leakyError.stack), "the stack trace must not be in the response");
  assert.deepEqual(Object.keys(JSON.parse(response.body)), ["error"]);

  // The detail is not lost: it goes to the session log instead.
  assert.equal(logged.length, 1);
  assert.match(logged[0].message, /internal stderr/);
  assert.equal(logged[0].options.level, "error");
});

test("respondWithError ends a response that already started without writing the error", async () => {
  const response = await request(async (req, res) => {
    res.writeHead(200, { "Content-Type": "text/plain" });
    res.write("partial");
    await respondWithError(res, leakyError, async () => {});
  });

  assert.equal(response.status, 200);
  assert.equal(response.body, "partial");
});

test("respondWithError tolerates non-error values", async () => {
  for (const thrown of [undefined, null, "a thrown string", { unexpected: true }]) {
    const response = await request((req, res) => respondWithError(res, thrown, async () => {}));

    assert.equal(response.status, 500);
    assert.doesNotMatch(response.body, /a thrown string|unexpected/);
  }
});

test("close failures returned to the browser never contain the raw error and are logged server-side", async () => {
  const logged = [];

  const failure = await toCloseFailure(42, leakyError, async (message, options) => logged.push({ message, options }));

  assert.equal(failure.number, 42);
  assert.doesNotMatch(JSON.stringify(failure), leakPattern);
  assert.equal(logged.length, 1);
  assert.match(logged[0].message, /#42.*internal stderr/s);
  assert.equal(logged[0].options.level, "warning");
});

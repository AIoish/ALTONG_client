import assert from "node:assert/strict";
import { createHmac } from "node:crypto";
import { DatabaseSync } from "node:sqlite";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { setTimeout as delay } from "node:timers/promises";
import test from "node:test";
import { convertV4MiniflareOptions, Miniflare, Response as MiniflareResponse } from "miniflare";
import { Cipher, hash, randomSecret, verifySlackSignature } from "../src/crypto.js";
import { Relay } from "../src/relay.js";
import { fakeSlack, tokenFor } from "./mock-slack.js";

const config = JSON.parse(readFileSync(new URL("wrangler.jsonc", import.meta.url), "utf8"));
const bindings = config.vars;
const origin = bindings.PUBLIC_URL;
const unixNow = () => Math.floor(Date.now() / 1000);
const eventTs = () => (Date.now() / 1000).toFixed(3) + "123";

function signed(bytes, timestamp = unixNow(), key = bindings.SLACK_SIGNING_SECRET) {
  const signature = createHmac("sha256", key).update("v0:" + timestamp + ":").update(bytes).digest("hex");
  return { "content-type": "application/json", "x-slack-request-timestamp": String(timestamp), "x-slack-signature": "v0=" + signature };
}

function runtime(t, mock = fakeSlack(), useOutboundFetch = false) {
  const mockFetch = async request => {
    const result = await mock.fetch(request);
    return new MiniflareResponse(await result.arrayBuffer(), { status: result.status, headers: result.headers });
  };
  const worker = new Miniflare(convertV4MiniflareOptions({
    compatibilityDate: config.compatibility_date,
    modulesRoot: fileURLToPath(new URL("../src/", import.meta.url)),
    modules: ["index.js", "crypto.js", "relay.js", "store.js"].map(name =>
      ({ type: "ESModule", path: fileURLToPath(new URL("../src/" + name, import.meta.url)) })),
    bindings,
    durableObjects: { RELAY: { className: "SlackRelay", useSQLite: true }, INBOX: { className: "SlackInbox", useSQLite: true } },
    ...(useOutboundFetch ? { outboundService: mockFetch } : { serviceBindings: { SLACK_API: mockFetch } })
  }));
  t.after(() => worker.dispose());
  return (path, options) => worker.dispatchFetch(origin + path, options);
}

function sqliteRelay(t, api = fakeSlack()) {
  const database = new DatabaseSync(":memory:");
  t.after(() => database.close());
  const db = (sql, ...parameters) => database.prepare(sql).all(...parameters.map(value =>
    value instanceof ArrayBuffer ? new Uint8Array(value) : value));
  const tx = action => {
    database.exec("BEGIN");
    try {
      const result = action();
      database.exec("COMMIT");
      return result;
    } catch (error) {
      database.exec("ROLLBACK");
      throw error;
    }
  };
  let now = Date.now();
  const relay = new Relay(bindings, db, tx, request => api.fetch(request), () => now);
  const fetch = (path, options) => relay.fetch(new Request(origin + path, options));
  return { relay, fetch, db, advance: ms => { now += ms; } };
}

async function json(response, status = 200) {
  const result = await response;
  assert.equal(result.status, status);
  return result.json();
}

async function start(fetch) {
  const proof = randomSecret();
  const attempt = await json(fetch("/v1/slack/connections", { method: "POST", body: JSON.stringify({ challenge: await hash(proof) }) }));
  const url = new URL(attempt.authorizationUrl);
  assert.equal(url.hostname + url.pathname, "slack.com/oauth/v2/authorize");
  assert.equal(url.searchParams.get("user_scope"), "im:history,im:read,users:read");
  assert.equal(url.searchParams.get("redirect_uri"), origin + "/oauth/slack/callback");
  assert.equal(url.searchParams.has("scope"), false);
  return { proof, id: attempt.id, state: url.searchParams.get("state") };
}

function callback(fetch, attempt, code) {
  return fetch("/oauth/slack/callback?" + new URLSearchParams({ state: attempt.state, code }));
}

function status(fetch, attempt, proof = attempt.proof, method = "GET") {
  return fetch("/v1/slack/connections/" + attempt.id, { method, headers: { "x-altong-pairing-proof": proof } });
}

async function pair(fetch, code) {
  const attempt = await start(fetch);
  const result = await callback(fetch, attempt, code);
  assert.equal(result.status, 200);
  assert.doesNotMatch(await result.text(), /(?:synthetic-access|synthetic-refresh|U_[A-Z]+)/);
  const pairing = await json(status(fetch, attempt));
  assert.equal(pairing.status, "complete");
  return { ...attempt, ...pairing.session };
}

function pc(fetch, session, path, method = "GET", body) {
  return fetch("/v1/slack/" + path, { method, headers: { authorization: "Bearer " + session.token, "x-altong-short-poll": "1" },
    body: body ? JSON.stringify(body) : undefined });
}

function postEvent(fetch, payload, retry) {
  const body = JSON.stringify(payload);
  const headers = signed(body);
  if (retry) headers["x-slack-retry-num"] = String(retry);
  return fetch("/slack/events", { method: "POST", headers, body });
}

function dm(id, team, sender, candidates, channel = "D_AB") {
  return { type: "event_callback", api_app_id: "A_TEST", event_id: id, team_id: team,
    authorizations: [{ team_id: team, user_id: sender, is_bot: false }],
    event_context: candidates.join(","),
    event: { type: "message", channel_type: "im", channel, user: sender, text: "Synthetic DM &amp; text", ts: eventTs() } };
}

async function until(action, ms = 4500) {
  const deadline = Date.now() + ms;
  while (true) {
    const result = await action();
    if (result) return result;
    if (Date.now() >= deadline) assert.fail("Condition was not reached before the test deadline");
    await delay(30);
  }
}

async function pending(fetch, session) { return (await json(pc(fetch, session, "messages"))).messages; }

async function connectStream(fetch, session) {
  const result = await fetch("/v1/slack/stream", {
    headers: { upgrade: "websocket", authorization: "Bearer " + session.token }
  });
  assert.equal(result.status, 101);
  const socket = result.webSocket;
  const signals = [];
  socket.addEventListener("message", event => signals.push([...new Uint8Array(event.data)]));
  socket.accept();
  return { socket, signals };
}

test("HMAC verifies the original bytes and rejects wrong, stale, or future signatures", async () => {
  const bytes = Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from('{"challenge":"한글"}')]);
  const now = Date.now();
  for (const offset of [0, -301, 301]) {
    const request = new Request(origin, { headers: signed(bytes, Math.floor(now / 1000) + offset) });
    assert.equal(await verifySlackSignature(request, bytes, bindings.SLACK_SIGNING_SECRET, now), offset === 0);
    assert.equal(await verifySlackSignature(request, bytes.subarray(3), bindings.SLACK_SIGNING_SECRET, now), false);
    assert.equal(await verifySlackSignature(request, bytes, "wrong-test-key", now), false);
  }
});

test("AES-GCM authenticates context, supports the previous key and stores no plaintext", async () => {
  const first = new Cipher(bindings.TOKEN_ENCRYPTION_KEY, 1);
  const second = new Cipher(Buffer.alloc(32, 42).toString("base64"), 2, bindings.TOKEN_ENCRYPTION_KEY);
  const encrypted = await first.encrypt("token:one", { accessToken: "synthetic-test-access" });
  assert.equal(Buffer.from(encrypted).includes("synthetic-test-access"), false);
  assert.equal(new DataView(encrypted).getUint32(5), 1);
  assert.equal((await second.decrypt("token:one", encrypted)).accessToken, "synthetic-test-access");
  await assert.rejects(second.decrypt("token:another", encrypted));
  const versionTwo = await second.encrypt("token:one", { body: "two" });
  assert.equal(new DataView(versionTwo).getUint32(5), 2);
  await assert.rejects(first.decrypt("token:one", versionTwo));
});

test("OAuth rejects replay, timeout, a wrong device proof, malformed input and failed exchange", async t => {
  const { fetch, advance } = sqliteRelay(t);
  assert.equal((await fetch("/v1/slack/connections", { method: "POST", body: "{" })).status, 400);
  const first = await start(fetch);
  assert.equal((await status(fetch, first, randomSecret())).status, 404);
  assert.equal((await callback(fetch, first, "T_A.U_A")).status, 200);
  assert.equal((await callback(fetch, first, "T_A.U_A")).status, 400);
  const pairing = await json(status(fetch, first));
  assert.equal(pairing.status, "complete");
  assert.equal((await json(status(fetch, first))).session.token, pairing.session.token);
  const failed = await start(fetch);
  assert.equal((await callback(fetch, failed, "bad-synthetic-code")).status, 400);
  assert.equal((await json(status(fetch, failed))).status, "failed");
  const expired = await start(fetch);
  advance(600001);
  assert.equal((await callback(fetch, expired, "T_A.U_A")).status, 400);
  assert.equal((await status(fetch, expired)).status, 404);
});

test("OAuth cancellation during code exchange prevents a late callback from creating a device", async t => {
  const mock = fakeSlack();
  let exchangeStarted, resumeExchange;
  const started = new Promise(resolve => { exchangeStarted = resolve; });
  const resume = new Promise(resolve => { resumeExchange = resolve; });
  const { fetch, db } = sqliteRelay(t, { async fetch(request) {
    if (new URL(request.url).pathname === "/api/oauth.v2.access") { exchangeStarted(); await resume; }
    return mock.fetch(request);
  } });
  const attempt = await start(fetch);
  const approving = callback(fetch, attempt, "T_A.U_A");
  await started;
  try { assert.equal((await status(fetch, attempt, attempt.proof, "DELETE")).status, 204); }
  finally { resumeExchange(); }
  assert.equal((await approving).status, 400);
  assert.equal((await json(status(fetch, attempt))).status, "cancelled");
  assert.equal(db("SELECT COUNT(*) AS n FROM devices")[0].n, 0);
});

test("OAuth displays a safe explanation for a rejected client secret without revealing Slack response data", async t => {
  const { fetch } = sqliteRelay(t, { async fetch(request) {
    assert.equal(request.headers.get("content-type"), "application/x-www-form-urlencoded");
    return Response.json({ ok: false, error: "bad_client_secret", detail: "synthetic-response-data" }, { status: 401 });
  } });
  const attempt = await start(fetch);
  const rejected = await callback(fetch, attempt, "synthetic-unusable-code");
  assert.equal(rejected.status, 400);
  const message = await rejected.text();
  assert.ok(message.includes("Client Secret이 일치하지 않습니다."));
  assert.ok(!message.includes("synthetic-response-data"));
  assert.ok(!message.includes(attempt.state));
  assert.equal((await json(status(fetch, attempt))).status, "failed");
});

test("SQLite persists device hashes, encrypted grants and messages, and clears unacknowledged data at expiry", async t => {
  const { fetch, db, relay, advance } = sqliteRelay(t);
  const recipient = await pair(fetch, "T_A.U_B");
  const tokenRow = db("SELECT token,created FROM devices")[0];
  assert.equal(tokenRow.token, await hash(recipient.token));
  assert.ok(!Buffer.from(db("SELECT result FROM pairings")[0].result).includes(recipient.token));
  const credential = db("SELECT credential FROM installations")[0].credential;
  assert.equal(Buffer.from(credential).includes("synthetic-access"), false);
  const payload = dm("Ev_store", "T_A", "U_A", ["T_A:U_B"]);
  const received = tokenRow.created + 1;
  await relay.store.enqueue("Ev_store", payload);
  assert.equal(Buffer.from(db("SELECT payload FROM inbox")[0].payload).includes("Synthetic DM"), false);
  await relay.route({ payload, received });
  assert.equal(Buffer.from(db("SELECT payload FROM deliveries")[0].payload).includes("Synthetic DM"), false);
  assert.equal((await pending(fetch, recipient)).length, 1);
  advance(3600002);
  await relay.maintenance();
  assert.equal(db("SELECT COUNT(*) AS n FROM deliveries")[0].n, 0);
  assert.equal(db("SELECT COUNT(*) AS n FROM inbox")[0].n, 0);
});

test("SQLite refreshes a rotating grant once and persists its new access and refresh tokens", async t => {
  const mock = fakeSlack();
  let rotations = 0;
  const { fetch, relay, db, advance } = sqliteRelay(t, { async fetch(request) {
    if (new URL(request.url).pathname === "/api/oauth.v2.access" &&
      (await request.clone().text()).includes("grant_type=refresh_token")) rotations++;
    return mock.fetch(request);
  } });
  const recipient = await pair(fetch, "T_A.U_B.rotating");
  const original = db("SELECT refresh_at FROM installations")[0].refresh_at;
  advance(11000);
  await Promise.all([relay.maintenance(), relay.forUser(recipient.userId, () => {})]);
  assert.equal(rotations, 1);
  const { credential } = await relay.store.installation(recipient.userId);
  assert.equal(credential.accessToken, tokenFor("T_A", "U_B", "refreshed"));
  assert.equal(credential.refreshToken, "synthetic-refresh-next");
  assert.ok(db("SELECT refresh_at FROM installations")[0].refresh_at > original);
});

test("Workers runtime authenticates signed challenges and reports health without secret values", async t => {
  const fetch = runtime(t);
  const health = await json(fetch("/health"));
  assert.equal(health.configured, true);
  assert.deepEqual(health.bindings, { RELAY: true, INBOX: true });
  assert.equal(typeof health.version, "string");
  assert.ok(Object.values(health.secrets).every(value => typeof value === "boolean"));
  const challenge = { type: "url_verification", challenge: "synthetic-한글" };
  assert.deepEqual(await json(postEvent(fetch, challenge)), { challenge: challenge.challenge });
  const body = JSON.stringify(challenge);
  assert.equal((await fetch("/slack/events", { method: "POST", body, headers: signed(body, unixNow() - 360) })).status, 401);
  assert.equal((await fetch("/slack/events", { method: "POST", body, headers: signed(body, unixNow(), "wrong-test-key") })).status, 401);
});

test("Production OAuth reaches Slack through the global Workers fetch", async t => {
  const mock = fakeSlack();
  const methods = [];
  const fetch = runtime(t, { fetch(request) {
    methods.push(new URL(request.url).pathname);
    return mock.fetch(request);
  } }, true);
  const recipient = await pair(fetch, "T_A.U_B");
  assert.ok(recipient.userId);
  assert.deepEqual(methods, ["/api/oauth.v2.access", "/api/auth.test"]);
});

test("Workers routes both DM directions, paginates auth, isolates accounts and deduplicates retries after ACK", async t => {
  const mock = fakeSlack();
  let calls = 0;
  const fetch = runtime(t, { fetch(request) {
    if (new URL(request.url).pathname === "/api/apps.event.authorizations.list") calls++;
    return mock.fetch(request);
  } });
  const a = await pair(fetch, "T_A.U_A");
  const b = await pair(fetch, "T_A.U_B");
  const b2 = await pair(fetch, "T_A.U_B.second");
  const c = await pair(fetch, "T_A.U_C");
  const other = await pair(fetch, "T_OTHER.U_B");
  assert.equal(b.userId, b2.userId);
  assert.notEqual(b.userId, other.userId);
  const ab = dm("Ev_ab", "T_A", "U_A", ["T_A:U_A", "T_A:U_B", "T_A:U_C", "T_OTHER:U_B"]);
  assert.equal((await postEvent(fetch, ab)).status, 200);
  const [delivered] = await until(async () => { const rows = await pending(fetch, b); return rows.length && rows; });
  const [delivered2] = await until(async () => { const rows = await pending(fetch, b2); return rows.length && rows; });
  assert.equal(delivered.message.body, "Synthetic DM & text");
  assert.match(delivered.message.timestamp, /\.\d{6}Z$/);
  assert.notEqual(delivered.id, delivered2.id);
  assert.equal((await pending(fetch, a)).length, 0);
  assert.equal((await pending(fetch, c)).length, 0);
  assert.equal((await pending(fetch, other)).length, 0);
  assert.equal((await pc(fetch, c, "messages/ack", "POST", { ids: [delivered.id] })).status, 204);
  assert.equal((await pending(fetch, b)).length, 1);
  assert.equal((await pc(fetch, b, "messages/ack", "POST", { ids: [delivered.id] })).status, 204);
  const callsBeforeRetry = calls;
  assert.equal((await postEvent(fetch, ab, 1)).status, 200);
  assert.equal((await pending(fetch, b)).length, 0);
  assert.equal(calls, callsBeforeRetry);
  assert.equal((await postEvent(fetch, dm("Ev_ba", "T_A", "U_B", ["T_A:U_B", "T_A:U_A", "T_OTHER:U_B"]))).status, 200);
  await until(async () => (await pending(fetch, a)).length === 1);
  assert.equal((await pending(fetch, b)).length, 0);
  const { pollAfterSeconds } = await json(pc(fetch, b, "messages"));
  assert.equal(pollAfterSeconds, 20);
});

test("An idle Hibernation WebSocket stays silent and wakes only recipient devices, including after reconnect", async t => {
  const fetch = runtime(t);
  const a = await pair(fetch, "T_A.U_A");
  const b = await pair(fetch, "T_A.U_B");
  const second = await pair(fetch, "T_A.U_B.second");
  const outsider = await pair(fetch, "T_OTHER.U_B");
  assert.equal((await json(pc(fetch, b, "session"))).deliveryMode, "websocket");
  assert.equal((await fetch("/v1/slack/stream", { headers: { upgrade: "websocket" } })).status, 401);
  assert.equal((await fetch("/v1/slack/stream", { headers: { authorization: "Bearer " + b.token } })).status, 426);
  const senderStream = await connectStream(fetch, a);
  const recipient = await connectStream(fetch, b);
  const otherWorkspace = await connectStream(fetch, outsider);
  await delay(100);
  assert.deepEqual(recipient.signals, []);
  const event = dm("Ev_websocket", "T_A", "U_A", ["T_A:U_A", "T_A:U_B", "T_OTHER:U_B"]);
  assert.equal((await postEvent(fetch, event)).status, 200);
  await until(() => recipient.signals.length === 1);
  assert.deepEqual(recipient.signals, [[1]]);
  assert.deepEqual(senderStream.signals, []);
  assert.deepEqual(otherWorkspace.signals, []);

  // This PC connects after enqueue, so the persisted backlog must wake it.
  const late = await connectStream(fetch, second);
  await until(() => late.signals.length === 1);
  assert.deepEqual(late.signals, [[1]]);
  recipient.socket.close(1000);
  const resumed = await connectStream(fetch, b);
  await until(() => resumed.signals.length === 1);
  assert.deepEqual(resumed.signals, [[1]]);
  const [message] = await pending(fetch, b);
  assert.equal((await pc(fetch, b, "messages/ack", "POST", { ids: [message.id] })).status, 204);
  assert.equal((await postEvent(fetch, event, 1)).status, 200);
  await delay(100);
  assert.deepEqual(resumed.signals, [[1]]);

  assert.equal((await pc(fetch, b, "devices/" + second.id, "DELETE")).status, 204);
  assert.equal((await fetch("/v1/slack/stream", { headers: { upgrade: "websocket", authorization: "Bearer " + second.token } })).status, 401);
  assert.equal((await pc(fetch, second, "messages")).status, 401);
  senderStream.socket.close(1000);
  resumed.socket.close(1000);
  late.socket.close(1000);
  otherWorkspace.socket.close(1000);
});

test("Workers acknowledges a durable event before Slack's conversation lookup completes", async t => {
  const mock = fakeSlack();
  let releaseLookup;
  let lookupStarted = false;
  const lookup = new Promise(resolve => { releaseLookup = resolve; });
  const fetch = runtime(t, { async fetch(request) {
    if (new URL(request.url).pathname === "/api/conversations.info") {
      lookupStarted = true;
      await lookup;
    }
    return mock.fetch(request);
  } });
  const b = await pair(fetch, "T_A.U_B");
  try {
    const acceptedAt = Date.now();
    const result = await postEvent(fetch, dm("Ev_before_lookup", "T_A", "U_A", ["T_A:U_B"]));
    assert.equal(result.status, 200);
    assert.ok(Date.now() - acceptedAt < 3000);
    await until(() => lookupStarted, 3500);
    assert.equal((await pending(fetch, b)).length, 0);
  } finally { releaseLookup(); }
  await until(async () => (await pending(fetch, b)).length === 1);
});

test("Workers authorizes device revoke, disconnects locally, revokes Slack and allows reapproval", async t => {
  const mock = fakeSlack();
  const fetch = runtime(t, mock);
  const first = await pair(fetch, "T_A.U_B");
  const second = await pair(fetch, "T_A.U_B.second");
  const outsider = await pair(fetch, "T_A.U_A");
  const devices = (await json(pc(fetch, first, "devices"))).devices;
  assert.equal(devices.length, 2);
  assert.equal((await pc(fetch, outsider, "devices/" + second.id, "DELETE")).status, 404);
  assert.equal((await pc(fetch, first, "devices/" + second.id, "DELETE")).status, 204);
  assert.equal((await pc(fetch, second, "session")).status, 401);
  assert.equal((await pc(fetch, first, "session")).status, 200);
  assert.equal(mock.revoked.size, 0);
  assert.equal((await pc(fetch, first, "session", "DELETE")).status, 204);
  assert.equal((await pc(fetch, first, "messages")).status, 401);
  await until(() => mock.revoked.has(tokenFor("T_A", "U_B", "second")));
  const again = await pair(fetch, "T_A.U_B.again");
  assert.equal(first.userId, again.userId);
  assert.notEqual(first.token, again.token);
  assert.equal((await pc(fetch, again, "session")).status, 200);
});

test("Workers handles tokens_revoked and workspace uninstall without removing unrelated or newer grants", async t => {
  const mock = fakeSlack();
  const fetch = runtime(t, mock);
  const a = await pair(fetch, "T_A.U_A");
  const b = await pair(fetch, "T_A.U_B");
  const other = await pair(fetch, "T_OTHER.U_B");
  mock.revoked.add(tokenFor("T_A", "U_A"));
  const revoke = { type: "event_callback", api_app_id: "A_TEST", event_id: "Ev_revoke", team_id: "T_A",
    event: { type: "tokens_revoked", event_ts: eventTs(), tokens: { oauth: ["U_A", "U_B"] } } };
  assert.equal((await postEvent(fetch, revoke)).status, 200);
  await until(async () => (await pc(fetch, a, "session")).status === 401);
  assert.equal((await pc(fetch, b, "session")).status, 200);
  const uninstall = { ...revoke, event_id: "Ev_uninstalled", event: { type: "app_uninstalled", event_ts: eventTs() } };
  assert.equal((await postEvent(fetch, uninstall)).status, 200);
  await until(async () => (await pc(fetch, b, "session")).status === 401);
  assert.equal((await pc(fetch, other, "session")).status, 200);
  const again = await pair(fetch, "T_A.U_B.newgrant");
  assert.equal((await postEvent(fetch, { ...uninstall, event_id: "Ev_delayed_uninstall" })).status, 200);
  await delay(150);
  assert.equal((await pc(fetch, again, "session")).status, 200);
});

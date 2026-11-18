import { DurableObject } from "cloudflare:workers";
import { readBytes, RelayError, verifySlackSignature } from "./crypto.js";
import { configuration, Relay, response } from "./relay.js";
import { createCipher } from "./store.js";

const relayName = "altong-slack-v1";
const workerPaths = /^\/(?:health$|oauth\/slack\/callback$|v1\/slack\/)/;
const workspaceId = /^[A-Za-z0-9_-]{1,128}$/;
const eventId = /^[A-Za-z0-9_-]{1,128}$/;
const decoder = new TextDecoder("utf-8", { fatal: true });
const messageSignal = Uint8Array.of(1);
const deviceTag = id => "device:" + id;
const userTag = id => "user:" + id;

function database(ctx) {
  return (sql, ...values) => ctx.storage.sql.exec(sql, ...values).toArray();
}

async function schedule(storage, next) {
  const current = await storage.getAlarm();
  const due = next();
  if (due !== null && (current === null || current > due)) await storage.setAlarm(due);
}

export class SlackRelay extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    const apiFetch = request => env.SLACK_API ? env.SLACK_API.fetch(request) : fetch(request);
    this.relay = new Relay(env, database(ctx), action => ctx.storage.transactionSync(action), apiFetch, Date.now, {
      notifyDevice: id => this.signalDevice(id),
      closeDevice: id => this.closeStreams(deviceTag(id)),
      closeUser: id => this.closeStreams(userTag(id))
    });
  }

  async fetch(request) {
    if (new URL(request.url).pathname === "/v1/slack/stream") return this.openStream(request);
    const result = await this.relay.fetch(request);
    if (configuration(this.env).configured) await schedule(this.ctx.storage, () => this.relay.store.nextAlarm());
    return result;
  }

  async openStream(request) {
    if (request.method !== "GET" || request.headers.get("upgrade")?.toLowerCase() !== "websocket")
      return response({ error: "websocket_required" }, 426);
    if (!configuration(this.env).configured) return response({ error: "not_configured" }, 503);
    const device = await this.relay.authenticateDevice(request);
    if (!device || !this.relay.store.deviceExists(device)) return response({ error: "unauthorized" }, 401);
    const [client, server] = Object.values(new WebSocketPair());
    this.ctx.acceptWebSocket(server, [deviceTag(device.id), userTag(device.userId)]);
    if (this.relay.store.hasMessages(device)) server.send(messageSignal);
    return new Response(null, { status: 101, webSocket: client });
  }

  signalDevice(id) {
    for (const socket of this.ctx.getWebSockets(deviceTag(id))) {
      if (socket.readyState !== WebSocket.OPEN) continue;
      try { socket.send(messageSignal); }
      catch { this.closeStream(socket, 1011, "Reconnect to resume delivery"); }
    }
  }

  closeStreams(tag) {
    for (const socket of this.ctx.getWebSockets(tag)) this.closeStream(socket, 4401, "Device permission revoked");
  }

  closeStream(socket, code, reason) {
    try { socket.close(code, reason); }
    catch { /* An already-closed connection needs no cleanup. */ }
  }

  webSocketMessage(socket) {
    this.closeStream(socket, 1008, "Application messages are server-only");
  }

  async routeEvent(item) {
    if (!configuration(this.env).configured) return 60000;
    try {
      await this.relay.route(item);
      return 0;
    } catch (error) {
      return Math.max(1000, error.retry ?? 5000);
    } finally {
      await schedule(this.ctx.storage, () => this.relay.store.nextAlarm());
    }
  }

  async alarm() {
    if (!configuration(this.env).configured) return;
    try { await this.relay.maintenance(); }
    finally { await schedule(this.ctx.storage, () => this.relay.store.nextAlarm()); }
  }
}

export class SlackInbox extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    this.db = database(ctx);
    this.db("CREATE TABLE IF NOT EXISTS events(id TEXT PRIMARY KEY,payload BLOB,received INTEGER NOT NULL,retry INTEGER NOT NULL,attempts INTEGER NOT NULL DEFAULT 0)");
    this.db("CREATE INDEX IF NOT EXISTS events_retry ON events(retry,received) WHERE payload IS NOT NULL");
    this.db("CREATE INDEX IF NOT EXISTS events_received ON events(received)");
    this.cipher = createCipher(env);
    this.retention = Number(env.RETENTION_HOURS) * 3600000;
    this.inFlight = null;
  }

  async enqueue(id, event, received) {
    if (this.db("SELECT 1 FROM events WHERE id=?", id).length) {
      await schedule(this.ctx.storage, () => this.nextAlarm());
      return;
    }
    const payload = await this.cipher.encrypt("event:" + id, event);
    this.db("INSERT OR IGNORE INTO events(id,payload,received,retry) VALUES(?,?,?,?)", id, payload, received, received);
    // Confirm to Slack only after the retry alarm has also been persisted.
    await schedule(this.ctx.storage, () => this.nextAlarm());
    this.ctx.waitUntil(this.drain());
  }

  nextAlarm() {
    const { next } = this.db("SELECT MIN(deadline) AS next FROM (SELECT MIN(retry) AS deadline FROM events WHERE payload IS NOT NULL UNION ALL SELECT MIN(received)+? FROM events)", this.retention)[0];
    return next === null ? null : Math.max(Date.now() + 1000, next);
  }

  async drain() {
    if (!this.inFlight) this.inFlight = this.process();
    const running = this.inFlight;
    try { await running; }
    finally { if (this.inFlight === running) this.inFlight = null; }
  }

  async process() {
    const relay = this.env.RELAY.getByName(relayName);
    this.db("DELETE FROM events WHERE received<=?", Date.now() - this.retention);
    try {
      for (let index = 0; index < 10; index++) {
        const row = this.db("SELECT * FROM events WHERE payload IS NOT NULL AND retry<=? ORDER BY retry,received,id LIMIT 1", Date.now())[0];
        if (!row) break;
        let retry;
        try {
          const payload = await this.cipher.decrypt("event:" + row.id, row.payload);
          retry = await relay.routeEvent({ id: row.id, received: row.received, payload });
        } catch { retry = 5000; }
        if (retry === 0) this.db("UPDATE events SET payload=NULL WHERE id=?", row.id);
        else this.db("UPDATE events SET retry=?,attempts=attempts+1 WHERE id=?",
          Date.now() + Math.max(retry, 5000 * 2 ** Math.min(row.attempts, 5)), row.id);
      }
    } finally { await schedule(this.ctx.storage, () => this.nextAlarm()); }
  }

  async alarm() { await this.drain(); }
}

function normalizeEvent(payload, env) {
  if (payload?.type !== "event_callback" || payload.api_app_id !== env.SLACK_APP_ID ||
    typeof payload.event_id !== "string" || !eventId.test(payload.event_id) ||
    typeof payload.team_id !== "string" || !workspaceId.test(payload.team_id)) return null;
  const message = payload.event;
  if (message?.type === "app_uninstalled") return { type: message.type, event_ts: message.event_ts };
  if (message?.type === "tokens_revoked") {
    const users = Array.isArray(message.tokens?.oauth) ? message.tokens.oauth.filter(user => typeof user === "string" && workspaceId.test(user)) : [];
    return { type: message.type, event_ts: message.event_ts, tokens: { oauth: users } };
  }
  if (payload.is_ext_shared_channel === true || message?.type !== "message" || message.channel_type !== "im" ||
    !["", "file_share", "me_message"].includes(message.subtype ?? "") || message.bot_id || message.hidden ||
    typeof message.channel !== "string" || typeof message.user !== "string" || !workspaceId.test(message.channel) ||
    !workspaceId.test(message.user) || typeof message.text !== "string" || !message.text.trim() ||
    message.text.length > 262144 || typeof message.ts !== "string") return null;
  return { type: "message", channel_type: "im", channel: message.channel, user: message.user, text: message.text, ts: message.ts };
}

async function events(request, env) {
  if (request.method !== "POST") return response({ error: "method_not_allowed" }, 405);
  if (!env.SLACK_SIGNING_SECRET) throw new RelayError("not_configured");
  const received = Date.now();
  const bytes = await readBytes(request, 1048576);
  if (!await verifySlackSignature(request, bytes, env.SLACK_SIGNING_SECRET, received)) throw new RelayError("unauthorized", 401);
  let payload;
  try { payload = JSON.parse(decoder.decode(bytes)); }
  catch { throw new RelayError("invalid_json", 400); }
  if (payload?.api_app_id && payload.api_app_id !== env.SLACK_APP_ID) throw new RelayError("wrong_app", 400);
  if (payload?.type === "url_verification" && typeof payload.challenge === "string")
    return response({ challenge: payload.challenge });
  if (!configuration(env).configured) throw new RelayError("not_configured");
  const event = normalizeEvent(payload, env);
  if (event) {
    const authorizations = Array.isArray(payload.authorizations) ? payload.authorizations.filter(
      entry => entry?.is_bot === false && entry.team_id === payload.team_id && typeof entry.user_id === "string")
      .map(({ team_id, user_id }) => ({ team_id, user_id, is_bot: false })) : [];
    await env.INBOX.getByName(payload.team_id).enqueue(payload.event_id, {
      type: payload.type, api_app_id: payload.api_app_id, team_id: payload.team_id,
      event_time: payload.event_time, event_context: typeof payload.event_context === "string" ? payload.event_context : undefined,
      authorizations, event
    }, received);
  }
  return response({ ok: true });
}

export default {
  async fetch(request, env) {
    try {
      const path = new URL(request.url).pathname;
      if (path === "/slack/events") return await events(request, env);
      if (!workerPaths.test(path)) return response({ error: "not_found" }, 404);
      const relay = env.RELAY.getByName(relayName);
      const result = await relay.fetch(request);
      if (result.headers.get("x-altong-empty-poll") !== "1" || request.headers.get("x-altong-short-poll") === "1")
        return result;
      // Older PCs immediately repoll on an empty response. Wait at the Worker edge.
      await result.body?.cancel();
      await new Promise(resolve => setTimeout(resolve, 20000));
      return await relay.fetch(request);
    } catch (error) {
      return response({ error: error instanceof RelayError ? error.code : "service_unavailable" },
        error instanceof RelayError ? error.status : 503);
    }
  }
};

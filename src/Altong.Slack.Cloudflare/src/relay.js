import { Cipher, hash, jsonBody, readBody, RelayError, validSecret } from "./crypto.js";
import { refreshLead, RelayStore } from "./store.js";

const scopes = ["im:history", "im:read", "users:read"];
const terminal = new Set(["invalid_auth", "token_revoked", "token_expired", "account_inactive", "missing_scope", "not_authed", "invalid_refresh_token"]);
const getMethods = new Set(["conversations.info", "users.info"]);
const oauthFailureMessages = new Map([
  ["bad_client_secret", "Slack 앱의 Client Secret이 일치하지 않습니다."],
  ["invalid_client_id", "Slack 앱의 Client ID를 확인해 주세요."],
  ["bad_redirect_uri", "Slack 앱에 저장된 OAuth Redirect URL을 확인해 주세요."],
  ["invalid_code", "Slack 승인 코드가 만료됐거나 사용됐습니다."],
  ["missing_scope", "개인 DM을 받는 데 필요한 사용자 권한이 빠져 있습니다."],
  ["oauth_app_mismatch", "Slack OAuth 응답의 앱 정보가 일치하지 않습니다."],
  ["oauth_org_install", "Slack 조직 전체 대신 연결할 워크스페이스를 선택해 주세요."],
  ["oauth_user_token_missing", "Slack에서 개인 DM용 사용자 권한을 발급하지 않았습니다."],
  ["oauth_identity_missing", "Slack에서 사용자 또는 워크스페이스 정보를 받지 못했습니다."],
  ["invalid_identity", "Slack 승인과 실제 사용자 정보가 일치하지 않습니다."],
  ["account_not_allowed", "이 Slack 워크스페이스는 현재 연결 대상에서 제외돼 있습니다."],
  ["invalid_auth", "Slack이 발급된 사용자 자격 증명을 거부했습니다."],
  ["access_denied", "Slack에서 이 앱의 접근을 허용하지 않았습니다."],
  ["accesslimited", "Slack에서 서버의 API 요청을 제한했습니다."],
  ["rate_limited", "Slack API 요청 한도에 도달했습니다. 잠시 뒤 다시 연결해 주세요."],
  ["slack_unavailable", "Slack API 연결에 일시적으로 실패했습니다."],
  ["pairing_expired", "알통의 연결 요청이 만료됐거나 취소됐습니다."]
]);
const slackErrors = new Set([...terminal, ...oauthFailureMessages.keys(), "channel_not_found", "not_in_channel", "not_allowed_token_type"]);
const text = (value, name) => typeof value?.[name] === "string" ? value[name] : "";
const privateHeaders = { "cache-control": "no-store", "referrer-policy": "no-referrer", "x-content-type-options": "nosniff",
  "content-security-policy": "default-src 'none'; frame-ancestors 'none'" };

export function response(value, status = 200, headers = {}) {
  return new Response(value === null ? null : JSON.stringify(value),
    { status, headers: { ...privateHeaders, "content-type": "application/json; charset=utf-8", ...headers } });
}

function page(value, status = 200) {
  return new Response(value, { status, headers: { ...privateHeaders, "content-type": "text/plain; charset=utf-8" } });
}

export function configuration(env) {
  let origin = false;
  try {
    const url = new URL(env.PUBLIC_URL);
    const loopback = env.DEV_LOOPBACK === "1" && url.protocol === "http:" &&
      ["localhost", "127.0.0.1", "[::1]"].includes(url.hostname);
    origin = (url.protocol === "https:" || loopback) && url.pathname === "/" && !url.search && !url.hash && !url.username && !url.password;
  } catch { /* Missing origin leaves the relay unavailable. */ }
  const keyVersion = Number(env.TOKEN_ENCRYPTION_KEY_VERSION ?? 1);
  const retention = Number(env.RETENTION_HOURS);
  const secrets = {
    SLACK_CLIENT_SECRET: !!env.SLACK_CLIENT_SECRET,
    SLACK_SIGNING_SECRET: !!env.SLACK_SIGNING_SECRET,
    SLACK_APP_TOKEN: !!env.SLACK_APP_TOKEN,
    TOKEN_ENCRYPTION_KEY: !!env.TOKEN_ENCRYPTION_KEY,
    TOKEN_ENCRYPTION_KEY_PREVIOUS: !!env.TOKEN_ENCRYPTION_KEY_PREVIOUS
  };
  const configured = !!(origin && env.SLACK_CLIENT_ID && env.SLACK_APP_ID && secrets.SLACK_CLIENT_SECRET &&
    secrets.SLACK_SIGNING_SECRET && secrets.SLACK_APP_TOKEN && Cipher.validKey(env.TOKEN_ENCRYPTION_KEY) &&
    Number.isInteger(keyVersion) && keyVersion >= 1 && keyVersion <= 0xffffffff &&
    (!secrets.TOKEN_ENCRYPTION_KEY_PREVIOUS || (keyVersion > 1 && Cipher.validKey(env.TOKEN_ENCRYPTION_KEY_PREVIOUS))) &&
    Number.isInteger(retention) && retention >= 1 && retention <= 168);
  return { configured, secrets };
}

function slackTimestamp(value) {
  if (typeof value !== "string") return null;
  const match = /^(\d{1,12})(?:\.(\d{1,6}))?$/.exec(value);
  if (!match) return null;
  const seconds = Number(match[1]);
  const fraction = (match[2] ?? "").padEnd(6, "0");
  if (seconds > 253402300799) return null;
  const iso = new Date(seconds * 1000).toISOString().replace(".000Z", "." + fraction + "Z");
  return { canonical: seconds + "." + fraction, iso, milliseconds: seconds * 1000 + Number(fraction) / 1000 };
}

export class Relay {
  constructor(env, db, transaction, apiFetch = request => fetch(request), now = Date.now, streams = {}) {
    this.env = env;
    this.now = now;
    this.apiFetch = apiFetch;
    this.streams = streams;
    this.store = new RelayStore(db, transaction, env, now);
    this.locks = new Map();
  }

  async lock(key, action) {
    const before = this.locks.get(key) ?? Promise.resolve();
    let release;
    const current = new Promise(resolve => { release = resolve; });
    this.locks.set(key, current);
    await before;
    try { return await action(); }
    finally {
      release();
      if (this.locks.get(key) === current) this.locks.delete(key);
    }
  }

  accountKey(account) { return account.teamId + ":" + account.userId; }

  keyForUser(user) {
    const row = this.store.db("SELECT team,user FROM installations WHERE id=?", user)[0];
    return row ? row.team + ":" + row.user : "user:" + user;
  }

  async slack(method, bearer, fields = {}, team) {
    const partition = team ?? (bearer ? await hash(bearer) : "oauth");
    const key = "cooldown:" + method + ":" + partition;
    const cooldown = Number(this.store.db("SELECT value FROM metadata WHERE id=?", key)[0]?.value ?? 0);
    if (cooldown > this.now()) throw new RelayError("rate_limited", 503, cooldown - this.now());
    const headers = {};
    if (bearer) headers.authorization = "Bearer " + bearer;
    const url = new URL("https://slack.com/api/" + method);
    const params = new URLSearchParams(fields);
    const get = getMethods.has(method);
    if (get) url.search = params.toString();
    else headers["content-type"] = "application/x-www-form-urlencoded";
    const request = new Request(url, { method: get ? "GET" : "POST", headers, body: get ? undefined : params,
      redirect: "manual", signal: AbortSignal.timeout(5000) });
    let result;
    try { result = await this.apiFetch(request); }
    catch { throw new RelayError("slack_unavailable"); }
    const delay = Math.max(1000, Number(result.headers.get("retry-after") ?? 30) * 1000 || 30000);
    const rateLimited = () => {
      this.store.db("INSERT INTO metadata VALUES(?,?) ON CONFLICT(id) DO UPDATE SET value=excluded.value", key, String(this.now() + delay));
      throw new RelayError("rate_limited", 503, delay);
    };
    if (result.status === 429) rateLimited();
    const oauthRejection = method === "oauth.v2.access" && [400, 401, 403].includes(result.status);
    if (!result.ok && !oauthRejection) throw new RelayError("slack_unavailable");
    let body;
    try { body = JSON.parse(await readBody(result, 2097152)); }
    catch { throw new RelayError("invalid_slack_response"); }
    if (result.ok && body?.ok === true) return body;
    if (body?.error === "ratelimited") rateLimited();
    throw new RelayError(slackErrors.has(body?.error) ? body.error : "slack_api_error");
  }

  expiry(seconds) {
    if (!Number.isSafeInteger(seconds) || seconds <= 0 || seconds > 86400) throw new RelayError("invalid_slack_response");
    return this.now() + seconds * 1000;
  }

  async exchange(code) {
    const root = await this.slack("oauth.v2.access", null, { client_id: this.env.SLACK_CLIENT_ID, client_secret: this.env.SLACK_CLIENT_SECRET,
      code, redirect_uri: new URL("/oauth/slack/callback", this.env.PUBLIC_URL).href });
    const user = root.authed_user;
    if (root.app_id !== this.env.SLACK_APP_ID) throw new RelayError("oauth_app_mismatch");
    if (root.is_enterprise_install === true) throw new RelayError("oauth_org_install");
    if (user?.token_type !== "user" || !text(user, "access_token")) throw new RelayError("oauth_user_token_missing");
    if (!text(root.team, "id") || !text(user, "id")) throw new RelayError("oauth_identity_missing");
    const grantedScopes = text(user, "scope").split(",").map(scope => scope.trim());
    if (!scopes.every(scope => grantedScopes.includes(scope))) throw new RelayError("missing_scope");
    const identity = await this.slack("auth.test", user.access_token);
    if (identity.bot_id || identity.user_id !== user.id || identity.team_id !== root.team.id) throw new RelayError("invalid_identity");
    const allowed = (this.env.ALLOWED_TEAMS ?? "").split(",").map(v => v.trim()).filter(Boolean);
    if (allowed.length && !allowed.includes(identity.team_id)) throw new RelayError("account_not_allowed");
    const refreshToken = text(user, "refresh_token") || null;
    const expires = user.expires_in == null ? null : this.expiry(user.expires_in);
    if ((expires === null) !== (refreshToken === null)) throw new RelayError("invalid_oauth_response");
    return { account: { teamId: identity.team_id, userId: identity.user_id, account: text(identity, "user") || identity.user_id,
      workspace: text(identity, "team") || identity.team_id }, accessToken: user.access_token, refreshToken, expires };
  }

  async forUser(user, action) {
    return this.lock(this.keyForUser(user), async () => {
      try {
        const installation = await this.store.installation(user);
        if (!installation) throw new RelayError("token_revoked");
        const credential = installation.credential;
        if (credential.expires !== null && credential.expires <= this.now() + refreshLead) {
          if (!credential.refreshToken) throw new RelayError("invalid_refresh_token");
          const fresh = await this.slack("oauth.v2.access", null, { client_id: this.env.SLACK_CLIENT_ID, client_secret: this.env.SLACK_CLIENT_SECRET,
            grant_type: "refresh_token", refresh_token: credential.refreshToken });
          if (fresh.token_type !== "user" || !text(fresh, "access_token") || !text(fresh, "refresh_token"))
            throw new RelayError("invalid_refresh_token");
          credential.accessToken = fresh.access_token;
          credential.refreshToken = fresh.refresh_token;
          credential.expires = this.expiry(fresh.expires_in);
          await this.store.update(user, credential);
        }
        return await action(credential);
      } catch (error) {
        if (terminal.has(error.code)) this.deactivate(user);
        throw error;
      }
    });
  }

  async disconnect(device) {
    await this.lock(this.keyForUser(device.userId), async () => {
      if (!this.store.deviceExists(device)) return;
      const installation = await this.store.installation(device.userId);
      await this.store.disconnect(device, installation?.credential);
      this.streams.closeDevice?.(device.id);
    });
  }

  deactivate(user) {
    this.store.transaction(() => this.store.deactivate(user));
    this.streams.closeUser?.(user);
  }

  async revoke(row) {
    await this.lock(this.keyForUser(row.user), async () => {
      if (!this.store.db("SELECT 1 FROM revocations WHERE id=?", row.id).length) return;
      try {
        const credential = await this.store.cipher.decrypt("revoke:" + row.id, row.payload);
        await this.slack("auth.revoke", credential);
        this.store.db("DELETE FROM revocations WHERE id=?", row.id);
      } catch (error) {
        if (terminal.has(error.code)) this.store.db("DELETE FROM revocations WHERE id=?", row.id);
        else this.store.db("UPDATE revocations SET retry=? WHERE id=?", this.now() + Math.max(30000, error.retry ?? 0), row.id);
      }
    });
  }

  async authorizations(payload) {
    if (!payload.event_context) return payload.authorizations ?? [];
    const candidates = new Map();
    const cursors = new Set();
    let cursor = "";
    do {
      const result = await this.slack("apps.event.authorizations.list", this.env.SLACK_APP_TOKEN,
        { event_context: payload.event_context, cursor, limit: "200" }, payload.team_id);
      if (!Array.isArray(result.authorizations)) throw new RelayError("invalid_slack_response");
      for (const entry of result.authorizations)
        if (entry?.is_bot === false && entry.team_id === payload.team_id && text(entry, "user_id")) candidates.set(entry.user_id, entry);
      cursor = text(result.response_metadata, "next_cursor");
      if (cursor && cursors.has(cursor)) throw new RelayError("invalid_slack_response");
      cursors.add(cursor);
    } while (cursor);
    return candidates.values();
  }

  async verifyGrant(user) {
    try {
      await this.forUser(user, async credential => {
        const identity = await this.slack("auth.test", credential.accessToken);
        if (identity.team_id !== credential.account.teamId || identity.user_id !== credential.account.userId) throw new RelayError("invalid_auth");
      });
      return true;
    } catch (error) {
      if (!terminal.has(error.code)) throw error;
      return false;
    }
  }

  async route(item) {
    const p = item.payload, m = p?.event;
    if (p?.type !== "event_callback" || p.api_app_id !== this.env.SLACK_APP_ID || !m || !p.team_id) return;
    const preciseTime = slackTimestamp(m.event_ts)?.milliseconds;
    const eventSecond = Number.isSafeInteger(p.event_time) && p.event_time > 0 ? p.event_time * 1000 : undefined;
    const at = Math.min(item.received, preciseTime ?? eventSecond ?? item.received);
    if (m.type === "app_uninstalled") {
      this.store.markRevoked(p.team_id, null, at);
      for (const row of this.store.db("SELECT id FROM installations WHERE team=? AND active=1 AND granted<=?", p.team_id, at)) {
        await this.lock(this.keyForUser(row.id), () => {
          if (this.store.db("SELECT 1 FROM installations WHERE id=? AND active=1 AND granted<=?", row.id, at).length)
            this.deactivate(row.id);
        });
      }
      // Slack uninstall events can carry only second-level precision.
      if (preciseTime === undefined && eventSecond !== undefined) {
        const end = Math.min(item.received, at + 999);
        for (const row of this.store.db("SELECT id FROM installations WHERE team=? AND active=1 AND granted>? AND granted<=?", p.team_id, at, end))
          await this.verifyGrant(row.id);
      }
      return;
    }
    if (m.type === "tokens_revoked") {
      for (const user of m.tokens?.oauth ?? []) {
        const installation = this.store.installationId(p.team_id, user);
        if (!installation || !await this.verifyGrant(installation)) this.store.markRevoked(p.team_id, user, at);
      }
      return;
    }
    if (m.type !== "message" || m.channel_type !== "im" || !["", "file_share", "me_message"].includes(m.subtype ?? "") || m.bot_id || m.hidden ||
      !m.channel || !m.user || typeof m.text !== "string" || !m.text.trim() || m.text.length > 262144) return;
    const timestamp = slackTimestamp(m.ts);
    if (!timestamp) return;
    let deferred;
    for (const auth of await this.authorizations(p)) {
      if (auth?.is_bot !== false || auth.team_id !== p.team_id || !auth.user_id || auth.user_id === m.user) continue;
      const user = this.store.installationId(auth.team_id, auth.user_id);
      if (!user) continue;
      try {
        await this.forUser(user, async credential => {
          const info = await this.slack("conversations.info", credential.accessToken, { channel: m.channel }, auth.team_id);
          const channel = info.channel;
          if (channel?.is_im !== true || channel.id !== m.channel || channel.user !== m.user ||
            (channel.context_team_id && channel.context_team_id !== auth.team_id)) return;
          let sender = m.user;
          try {
            const profile = (await this.slack("users.info", credential.accessToken, { user: m.user }, auth.team_id)).user;
            sender = text(profile?.profile, "display_name").trim() || text(profile?.profile, "real_name").trim() || text(profile, "name").trim() || sender;
          } catch (error) { if (terminal.has(error.code) && error.code !== "missing_scope") throw error; }
          const devices = await this.store.deliver(user, { id: ["slack", auth.team_id, m.channel, timestamp.canonical].join(":"), sender,
            body: m.text.replaceAll("&lt;", "<").replaceAll("&gt;", ">").replaceAll("&amp;", "&"), timestamp: timestamp.iso }, item.received);
          for (const device of devices) this.streams.notifyDevice?.(device);
        });
      } catch (error) {
        if (!terminal.has(error.code) && !["channel_not_found", "not_in_channel"].includes(error.code))
          if (!deferred || (error.retry ?? 0) > (deferred.retry ?? 0)) deferred = error;
      }
    }
    if (deferred) throw deferred;
  }

  async maintenance() {
    return this.lock("maintenance", async () => {
      this.store.cleanup();
      for (let count = 0; count < 10; count++) {
        const item = await this.store.next();
        if (!item) break;
        try { await this.route(item); this.store.finish(item.id); }
        catch (error) { this.store.defer(item.id, Math.max(30000, error.retry ?? 0)); }
      }
      const due = this.store.db("SELECT id FROM installations WHERE active=1 AND refresh_at<=? ORDER BY refresh_at LIMIT 10", this.now());
      for (const { id } of due) {
        try { await this.forUser(id, () => {}); }
        catch (error) {
          if (!terminal.has(error.code)) this.store.db("UPDATE installations SET refresh_at=? WHERE id=? AND active=1 AND refresh_at<=?",
            this.now() + Math.max(30000, error.retry ?? 0), id, this.now());
        }
      }
      const revokes = this.store.db("SELECT * FROM revocations WHERE retry<=? AND expires>? ORDER BY retry LIMIT 10", this.now(), this.now());
      for (const row of revokes) await this.revoke(row);
    });
  }

  async pairingStatus(row, proof) {
    const result = await this.store.pairingResult(row);
    if (!result) return { status: row.status, session: null };
    if (row.status === "claimed" || result.token) {
      const token = result.token ?? await this.store.pairingToken(row.id, proof);
      if (!await this.store.authenticate(token)) throw new RelayError("not_found", 404);
      return { status: "complete", session: { ...result, token } };
    }
    return this.lock(this.accountKey(result.credential.account), async () => {
      const current = await this.store.pairing(row.id, proof);
      if (!current) throw new RelayError("not_found", 404);
      if (current.status !== "complete") return this.pairingStatus(current, proof);
      try {
        const { credential } = result;
        const { teamId, userId } = credential.account;
        const previous = this.store.db("SELECT active,granted FROM installations WHERE team=? AND user=?", teamId, userId)[0];
        if (previous && !previous.active) {
          const identity = await this.slack("auth.test", credential.accessToken);
          if (identity.team_id !== teamId || identity.user_id !== userId || identity.bot_id) throw new RelayError("invalid_auth");
        }
        return { status: "complete", session: await this.store.claim(row.id, proof, result) };
      }
      catch (error) {
        if (!terminal.has(error.code)) throw error;
        this.store.fail(row.id, "failed");
        return { status: "failed", session: null };
      }
    });
  }

  async fetch(request) {
    try { return await this.handle(request); }
    catch (error) { return response({ error: error instanceof RelayError ? error.code : "service_unavailable" }, error instanceof RelayError ? error.status : 503); }
  }

  authenticateDevice(request) {
    const header = request.headers.get("authorization") ?? "";
    return header.startsWith("Bearer ") ? this.store.authenticate(header.slice(7)) : null;
  }

  async handle(request) {
    const url = new URL(request.url), path = url.pathname, method = request.method;
    const settings = configuration(this.env);
    if (path === "/health" && method === "GET") {
      return response({ ...settings, receiverConnected: settings.configured, version: this.env.WORKER_VERSION?.id ?? "local",
        bindings: { RELAY: !!this.env.RELAY, INBOX: !!this.env.INBOX } });
    }
    if (!settings.configured) throw new RelayError("not_configured");
    const id = /^\/v1\/slack\/connections\/([a-zA-Z0-9-]+)$/.exec(path)?.[1];
    const device = await this.authenticateDevice(request);
    const proof = request.headers.get("x-altong-pairing-proof") ?? "";
    const pairing = id ? await this.store.pairing(id, proof) : null;
    const partition = device ? "device:" + device.id : pairing ? "pair:" + id : "ip:" + await hash(request.headers.get("cf-connecting-ip") ?? "unknown");
    this.store.limit(partition, path === "/v1/slack/connections" ? 10 : 240);

    if (path === "/v1/slack/connections" && method === "POST") {
      const body = await jsonBody(request);
      const attempt = await this.store.begin(body?.challenge);
      const authorize = new URL("https://slack.com/oauth/v2/authorize");
      authorize.search = new URLSearchParams({ client_id: this.env.SLACK_CLIENT_ID, user_scope: scopes.join(","), state: attempt.state,
        redirect_uri: new URL("/oauth/slack/callback", this.env.PUBLIC_URL).href }).toString();
      return response({ id: attempt.id, authorizationUrl: authorize.href, expiresAt: new Date(attempt.expires).toISOString() });
    }

    if (path === "/oauth/slack/callback" && method === "GET") {
      const attempt = await this.store.consume(url.searchParams.get("state"));
      if (!attempt) return page("연결 요청이 만료되었거나 유효하지 않습니다. 알통에서 다시 연결해 주세요.", 400);
      if (url.searchParams.has("error")) {
        this.store.fail(attempt, "denied");
        return page("Slack 연결을 취소했습니다. 알통으로 돌아가 주세요.");
      }
      let storingGrant = false;
      try {
        const code = url.searchParams.get("code");
        if (!code || code.length > 4096) throw new RelayError("invalid_code");
        const credential = await this.exchange(code);
        storingGrant = true;
        await this.store.complete(attempt, credential);
        return page("Slack 연결이 완료되었습니다. 이 창을 닫고 알통으로 돌아가 주세요.");
      } catch (error) {
        this.store.fail(attempt, "failed");
        const fallback = storingGrant ? "알통 연결 정보를 저장하지 못했습니다." : "Slack에서 승인 정보를 확인하지 못했습니다.";
        const reason = oauthFailureMessages.get(error instanceof RelayError ? error.code : null) ?? fallback;
        return page("Slack 연결을 완료하지 못했습니다.\n" + reason + "\n알통에서 다시 연결해 주세요.", 400);
      }
    }

    if (id && method === "GET") return pairing ? response(await this.pairingStatus(pairing, proof)) : response({ error: "not_found" }, 404);
    if (id && method === "DELETE") {
      if (!pairing) throw new RelayError("not_found", 404);
      const linked = await this.store.cancel(id, proof);
      if (linked) await this.disconnect(linked);
      return response(null, 204);
    }
    if (path === "/v1/slack/session" && method === "DELETE") {
      if (device) await this.disconnect(device);
      return response(null, 204);
    }
    if (!device) throw new RelayError("unauthorized", 401);
    if (path === "/v1/slack/session" && method === "GET") {
      const installation = await this.store.installation(device.userId);
      if (!installation || !this.store.deviceExists(device)) throw new RelayError("unauthorized", 401);
      const { account, workspace } = installation.credential.account;
      return response({ userId: device.userId, account, workspace, receiverConnected: true, deliveryMode: "websocket" });
    }
    if (path === "/v1/slack/devices" && method === "GET") {
      return response({ devices: this.store.devicesFor(device.userId).map(row =>
        ({ id: row.id, createdAt: new Date(row.created).toISOString(), current: row.id === device.id })) });
    }
    const deviceId = /^\/v1\/slack\/devices\/([a-zA-Z0-9-]+)$/.exec(path)?.[1];
    if (deviceId && method === "DELETE") {
      const selected = { id: deviceId, userId: device.userId };
      if (!this.store.deviceExists(selected)) throw new RelayError("not_found", 404);
      await this.disconnect(selected);
      return response(null, 204);
    }
    if (path === "/v1/slack/messages" && method === "GET") {
      const messages = await this.store.messages(device);
      return messages.length ? response({ receiverConnected: true, messages }) :
        response({ receiverConnected: true, messages, pollAfterSeconds: 20 }, 200, { "x-altong-empty-poll": "1" });
    }
    if (path === "/v1/slack/messages/ack" && method === "POST") {
      const body = await jsonBody(request);
      if (!Array.isArray(body?.ids) || body.ids.length > 100 || !body.ids.every(validSecret)) throw new RelayError("invalid_ack", 400);
      if (!this.store.deviceExists(device)) throw new RelayError("unauthorized", 401);
      this.store.ack(device, body.ids);
      return response(null, 204);
    }
    return response({ error: "not_found" }, 404);
  }
}

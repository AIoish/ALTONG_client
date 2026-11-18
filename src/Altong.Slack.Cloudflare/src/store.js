import { Cipher, equalHex, hash, randomSecret, RelayError, validSecret } from "./crypto.js";

export const refreshLead = 120000;
export const createCipher = env => new Cipher(env.TOKEN_ENCRYPTION_KEY, env.TOKEN_ENCRYPTION_KEY_VERSION ?? 1, env.TOKEN_ENCRYPTION_KEY_PREVIOUS);

// db(sql,...parameters) and transaction(fn) are backed by a SQLite Durable Object in production.
export class RelayStore {
  constructor(db, transaction, env, now = Date.now) {
    this.db = db; this.transaction = transaction; this.cipher = createCipher(env); this.now = now;
    this.retention = Number(env.RETENTION_HOURS) * 3600000;
    for (const statement of [
      "CREATE TABLE IF NOT EXISTS pairings(id TEXT PRIMARY KEY,state TEXT UNIQUE,proof TEXT,expires INTEGER,status TEXT,result BLOB)",
      "CREATE TABLE IF NOT EXISTS installations(id TEXT PRIMARY KEY,team TEXT,user TEXT,credential BLOB,active INTEGER,UNIQUE(team,user))",
      "CREATE TABLE IF NOT EXISTS devices(id TEXT PRIMARY KEY,user TEXT,token TEXT UNIQUE,created INTEGER,active INTEGER DEFAULT 0)",
      "CREATE TABLE IF NOT EXISTS inbox(id TEXT PRIMARY KEY,payload BLOB,received INTEGER,done INTEGER DEFAULT 0,retry INTEGER DEFAULT 0)",
      "CREATE TABLE IF NOT EXISTS deliveries(id TEXT PRIMARY KEY,device TEXT,message TEXT,payload BLOB,created INTEGER,UNIQUE(device,message))",
      "CREATE TABLE IF NOT EXISTS receipts(device TEXT,message TEXT,created INTEGER,PRIMARY KEY(device,message))",
      "CREATE TABLE IF NOT EXISTS limits(id TEXT PRIMARY KEY,window INTEGER,hits INTEGER)",
      "CREATE TABLE IF NOT EXISTS metadata(id TEXT PRIMARY KEY,value TEXT)",
      "CREATE TABLE IF NOT EXISTS revocations(id TEXT PRIMARY KEY,user TEXT,token_hash TEXT,payload BLOB,retry INTEGER,expires INTEGER)",
      "CREATE INDEX IF NOT EXISTS inbox_due ON inbox(done,retry,received)",
      "CREATE INDEX IF NOT EXISTS devices_user ON devices(user)",
      "CREATE INDEX IF NOT EXISTS deliveries_device ON deliveries(device,created)"
    ]) db(statement);
    const columns = new Set(db("PRAGMA table_info(installations)").map(column => column.name));
    if (!columns.has("granted")) db("ALTER TABLE installations ADD COLUMN granted INTEGER NOT NULL DEFAULT 0");
    if (!columns.has("refresh_at")) db("ALTER TABLE installations ADD COLUMN refresh_at INTEGER");
  }
  cleanup() {
    const cutoff = this.now() - this.retention;
    this.db("DELETE FROM pairings WHERE expires<=?", this.now());
    this.db("DELETE FROM devices WHERE active=0 AND created<=?", this.now() - 600000);
    this.db("UPDATE installations SET active=0,credential=NULL,refresh_at=NULL WHERE active=1 AND NOT EXISTS(SELECT 1 FROM devices WHERE devices.user=installations.id)");
    this.db("DELETE FROM inbox WHERE received<?", cutoff);
    this.db("DELETE FROM deliveries WHERE created<?", cutoff);
    this.db("DELETE FROM receipts WHERE created<?", cutoff);
    this.db("DELETE FROM limits WHERE window<?", this.now() - 120000);
    this.db("DELETE FROM revocations WHERE expires<=?", this.now());
    this.db("DELETE FROM metadata WHERE id LIKE 'cooldown:%' AND CAST(value AS INTEGER)<=?", this.now());
  }
  limit(id, maximum = 240) {
    const window = Math.floor(this.now() / 60000) * 60000;
    const row = this.db("INSERT INTO limits VALUES(?,?,1) ON CONFLICT(id) DO UPDATE SET hits=CASE WHEN window=excluded.window THEN hits+1 ELSE 1 END,window=excluded.window RETURNING hits", id, window)[0];
    if (row.hits > maximum) throw new RelayError("rate_limited", 429, 60000);
  }
  async begin(proof) {
    if (!validSecret(proof)) throw new RelayError("invalid_challenge", 400);
    this.cleanup(); const id = crypto.randomUUID(), state = randomSecret(), expires = this.now() + 600000;
    this.db("INSERT INTO pairings VALUES(?,?,?,?,'pending',NULL)", id, await hash(state), proof, expires);
    return { id, state, expires };
  }
  async consume(state) {
    if (!validSecret(state)) return null;
    return this.db("UPDATE pairings SET status='authorizing' WHERE state=? AND status='pending' AND expires>? RETURNING id", await hash(state), this.now())[0]?.id ?? null;
  }
  fail(id, status) { this.db("UPDATE pairings SET status=?,result=NULL WHERE id=? AND status IN ('authorizing','complete')", status, id); }
  async pairing(id, proof) {
    if (!validSecret(proof)) return null;
    const challenge = await hash(proof);
    const row = this.db("SELECT * FROM pairings WHERE id=? AND expires>?", id, this.now())[0];
    return row && equalHex(challenge, row.proof) ? row : null;
  }
  async pairingResult(row) {
    return row.result ? this.cipher.decrypt("pair:" + row.id, row.result) : null;
  }
  pairingToken(id, proof) {
    // Retries use the same bearer. The unpersisted proof is needed to re-derive it.
    return hash("altong:device:v1:" + id + ":" + proof);
  }
  async cancel(id, proof) {
    const row = await this.pairing(id, proof);
    if (!row) throw new RelayError("not_found", 404);
    const current = this.db("SELECT * FROM pairings WHERE id=?", id)[0];
    if (!current) throw new RelayError("not_found", 404);
    this.db("UPDATE pairings SET status='cancelled',result=NULL WHERE id=?", id);
    const result = await this.pairingResult(current);
    if (current.status === "claimed") return { id, userId: result.userId };
    return result?.token ? this.authenticate(result.token) : null;
  }
  async complete(id, credential) {
    const grant = { credential, granted: this.now() };
    const result = await this.cipher.encrypt("pair:" + id, grant);
    if (!this.db("UPDATE pairings SET status='complete',result=? WHERE id=? AND status='authorizing' AND expires>? RETURNING id",
      result, id, this.now()).length) throw new RelayError("pairing_expired", 400);
  }
  async claim(id, proof, grant) {
    const { credential, granted } = grant;
    const account = credential.account;
    const current = this.db("SELECT id,granted,active FROM installations WHERE team=? AND user=?", account.teamId, account.userId)[0];
    const user = current?.id ?? crypto.randomUUID();
    const useGrant = !current?.active || current.granted <= granted;
    const token = await this.pairingToken(id, proof);
    const session = { userId: user, account: account.account, workspace: account.workspace };
    const [encryptedCredential, encryptedSession, tokenHash, accessHash] = await Promise.all([
      useGrant ? this.cipher.encrypt("token:" + user, credential) : null,
      this.cipher.encrypt("pair:" + id, session), hash(token), hash(credential.accessToken)
    ]);
    this.transaction(() => {
      if (!this.db("SELECT id FROM pairings WHERE id=? AND status='complete' AND expires>?", id, this.now()).length)
        throw new RelayError("not_found", 404);
      const workspaceRemoved = Number(this.db("SELECT value FROM metadata WHERE id=?", "workspace_removed:" + account.teamId)[0]?.value ?? 0);
      const grantRevoked = Number(this.db("SELECT value FROM metadata WHERE id=?", "grant_revoked:" + account.teamId + ":" + account.userId)[0]?.value ?? 0);
      if (Math.max(workspaceRemoved, grantRevoked) >= granted) throw new RelayError("token_revoked", 401);
      if (useGrant) {
        this.db("INSERT INTO installations(id,team,user,credential,active,granted,refresh_at) VALUES(?,?,?,?,1,?,?) ON CONFLICT(id) DO UPDATE SET credential=excluded.credential,active=1,granted=excluded.granted,refresh_at=excluded.refresh_at",
          user, account.teamId, account.userId, encryptedCredential, granted, credential.expires === null ? null : credential.expires - refreshLead);
        this.db("DELETE FROM revocations WHERE user=? AND token_hash=?", user, accessHash);
      }
      this.db("INSERT INTO devices VALUES(?,?,?,?,1)", id, user, tokenHash, this.now());
      this.db("UPDATE pairings SET status='claimed',result=? WHERE id=?", encryptedSession, id);
    });
    return { ...session, token };
  }
  async authenticate(token) {
    if (!validSecret(token)) return null;
    const row = this.db("SELECT d.* FROM devices d JOIN installations i ON i.id=d.user WHERE d.token=? AND i.active=1", await hash(token))[0];
    if (!row) return null;
    if (!row.active) this.db("UPDATE devices SET active=1 WHERE id=?", row.id);
    return { id: row.id, userId: row.user };
  }
  async installation(user) {
    const row = this.db("SELECT credential,granted FROM installations WHERE id=? AND active=1", user)[0];
    return row ? { userId: user, granted: row.granted, credential: await this.cipher.decrypt("token:" + user, row.credential) } : null;
  }
  installationId(team, user) {
    return this.db("SELECT id FROM installations WHERE team=? AND user=? AND active=1", team, user)[0]?.id;
  }
  async update(user, credential) {
    const value = await this.cipher.encrypt("token:" + user, credential);
    this.db("UPDATE installations SET credential=?,refresh_at=? WHERE id=? AND active=1", value, credential.expires - refreshLead, user);
  }
  deactivate(user) {
    this.db("DELETE FROM deliveries WHERE device IN(SELECT id FROM devices WHERE user=?)", user);
    this.db("DELETE FROM receipts WHERE device IN(SELECT id FROM devices WHERE user=?)", user);
    this.db("DELETE FROM devices WHERE user=?", user);
    this.db("UPDATE installations SET active=0,credential=NULL,refresh_at=NULL WHERE id=?", user);
  }
  markRevoked(team, user, at) {
    const key = user ? "grant_revoked:" + team + ":" + user : "workspace_removed:" + team;
    this.db("INSERT INTO metadata VALUES(?,?) ON CONFLICT(id) DO UPDATE SET value=MAX(CAST(value AS INTEGER),CAST(excluded.value AS INTEGER))", key, String(at));
  }
  deviceExists(device) {
    return this.db("SELECT 1 FROM devices WHERE id=? AND user=?", device.id, device.userId).length > 0;
  }
  devicesFor(user) {
    return this.db("SELECT id,created FROM devices WHERE user=? AND active=1 ORDER BY created,id", user);
  }
  async disconnect(device, credential) {
    const id = crypto.randomUUID();
    const [payload, tokenHash] = credential ? await Promise.all([
      this.cipher.encrypt("revoke:" + id, credential.accessToken), hash(credential.accessToken)
    ]) : [null, null];
    this.transaction(() => {
      if (!this.db("DELETE FROM devices WHERE id=? AND user=? RETURNING id", device.id, device.userId).length) return;
      this.db("DELETE FROM deliveries WHERE device=?", device.id);
      this.db("DELETE FROM receipts WHERE device=?", device.id);
      if (!this.db("SELECT id FROM devices WHERE user=? LIMIT 1", device.userId).length) {
        this.deactivate(device.userId);
        const now = this.now();
        if (payload) this.db("INSERT INTO revocations VALUES(?,?,?,?,?,?)", id, device.userId, tokenHash, payload, now, now + this.retention);
      }
    });
  }
  async enqueue(id, payload) {
    if (this.db("SELECT 1 FROM inbox WHERE id=?", id).length) return;
    const value = await this.cipher.encrypt("inbox:" + id, payload);
    this.db("INSERT OR IGNORE INTO inbox(id,payload,received) VALUES(?,?,?)", id, value, this.now());
  }
  async next() {
    const row = this.db("SELECT * FROM inbox WHERE done=0 AND retry<=? ORDER BY received,id LIMIT 1", this.now())[0];
    return row ? { id: row.id, received: row.received, payload: await this.cipher.decrypt("inbox:" + row.id, row.payload) } : null;
  }
  finish(id) { this.db("UPDATE inbox SET done=1,payload=NULL WHERE id=?", id); }
  defer(id, delay) { this.db("UPDATE inbox SET retry=? WHERE id=?", this.now() + Math.max(1000, delay), id); }
  nextAlarm() {
    const deadlines = this.db(`SELECT
      (SELECT MIN(retry) FROM inbox WHERE done=0) AS pending,
      (SELECT MIN(received)+? FROM inbox) AS inbox_expiry,
      (SELECT MIN(expires) FROM pairings) AS pairing_expiry,
      (SELECT MIN(created)+? FROM deliveries) AS delivery_expiry,
      (SELECT MIN(created)+? FROM receipts) AS receipt_expiry,
      (SELECT MIN(refresh_at) FROM installations WHERE active=1) AS refresh,
      (SELECT MIN(MIN(retry,expires)) FROM revocations) AS revoke,
      (SELECT MIN(window)+180000 FROM limits) AS limit_expiry`, this.retention + 1, this.retention + 1, this.retention + 1)[0];
    const times = Object.values(deadlines).filter(value => value !== null);
    return times.length ? Math.max(this.now() + 1000, Math.min(...times)) : null;
  }
  async deliver(user, message, received) {
    const devices = this.db("SELECT id FROM devices WHERE user=? AND active=1 AND created<=?", user, received);
    const notified = [];
    for (const device of devices) {
      const id = await hash(device.id + ":" + message.id), payload = await this.cipher.encrypt("delivery:" + id, message);
      // Check device existence again after crypto I/O: disconnect may have happened while awaiting.
      this.db("INSERT OR IGNORE INTO deliveries SELECT ?,?,?,?,? WHERE EXISTS(SELECT 1 FROM devices WHERE id=?) AND NOT EXISTS(SELECT 1 FROM receipts WHERE device=? AND message=?)",
        id, device.id, message.id, payload, received, device.id, device.id, message.id);
      // A resumed event can still signal a delivery stored before the previous attempt stopped.
      if (this.db("SELECT 1 FROM deliveries WHERE id=?", id).length) notified.push(device.id);
    }
    return notified;
  }
  hasMessages(device) {
    return this.db("SELECT 1 FROM deliveries WHERE device=? AND created>=? LIMIT 1", device.id, this.now() - this.retention).length > 0;
  }
  async messages(device) {
    const result = [];
    for (const row of this.db("SELECT id,payload FROM deliveries WHERE device=? AND created>=? ORDER BY created,id LIMIT 10", device.id, this.now() - this.retention))
      result.push({ id: row.id, message: await this.cipher.decrypt("delivery:" + row.id, row.payload) });
    if (!this.deviceExists(device)) throw new RelayError("unauthorized", 401);
    return result;
  }
  ack(device, ids) {
    this.transaction(() => {
      for (const id of ids) {
        this.db("INSERT OR IGNORE INTO receipts SELECT device,message,created FROM deliveries WHERE id=? AND device=?", id, device.id);
        this.db("DELETE FROM deliveries WHERE id=? AND device=?", id, device.id);
      }
    });
  }
}

const encoder = new TextEncoder();
const decoder = new TextDecoder("utf-8", { fatal: true });
const cipherMagic = Uint8Array.of(65, 76, 84, 71, 1);
const headerSize = cipherMagic.length + 4;
export const validSecret = value => typeof value === "string" && /^[0-9a-f]{64}$/i.test(value);
export const hex = bytes => Array.from(new Uint8Array(bytes), b => b.toString(16).padStart(2, "0")).join("").toUpperCase();
export const randomSecret = () => hex(crypto.getRandomValues(new Uint8Array(32)));
export const hash = async value => hex(await crypto.subtle.digest("SHA-256", encoder.encode(value)));
export function equalHex(a, b) {
  if (!validSecret(a) || !validSecret(b)) return false;
  let different = 0;
  for (let i = 0; i < 64; i++) different |= a.toUpperCase().charCodeAt(i) ^ b.toUpperCase().charCodeAt(i);
  return different === 0;
}
export class RelayError extends Error {
  constructor(code, status = 503, retry = 5000) { super(code); this.code = code; this.status = status; this.retry = retry; }
}
export class Cipher {
  constructor(secret, version = 1, previousSecret) {
    this.version = Number(version);
    this.secrets = new Map([[this.version, secret]]);
    if (previousSecret) this.secrets.set(this.version - 1, previousSecret);
    this.keys = new Map();
  }
  static validKey(secret) {
    try { return typeof secret === "string" && atob(secret).length === 32; }
    catch { return false; }
  }
  async key(version) {
    if (!this.keys.has(version)) {
      const secret = this.secrets.get(version);
      if (!Cipher.validKey(secret)) throw new RelayError("encryption_key_unavailable");
      const bytes = Uint8Array.from(atob(secret), c => c.charCodeAt(0));
      this.keys.set(version, crypto.subtle.importKey("raw", bytes, "AES-GCM", false, ["encrypt", "decrypt"]));
      bytes.fill(0);
    }
    return this.keys.get(version);
  }
  async encrypt(context, value) {
    const nonce = crypto.getRandomValues(new Uint8Array(12));
    const plain = encoder.encode(JSON.stringify(value));
    try {
      const ciphertext = new Uint8Array(await crypto.subtle.encrypt(
        { name: "AES-GCM", iv: nonce, additionalData: encoder.encode(this.version + ":" + context) },
        await this.key(this.version), plain));
      const result = new Uint8Array(headerSize + nonce.length + ciphertext.length);
      result.set(cipherMagic);
      new DataView(result.buffer).setUint32(cipherMagic.length, this.version);
      result.set(nonce, headerSize);
      result.set(ciphertext, headerSize + nonce.length);
      return result.buffer;
    } finally { plain.fill(0); }
  }
  async decrypt(context, value) {
    const bytes = new Uint8Array(value);
    const versioned = cipherMagic.every((byte, index) => byte === bytes[index]);
    const version = versioned ? new DataView(bytes.buffer).getUint32(cipherMagic.length) : 1;
    const offset = versioned ? headerSize : 0;
    const additionalData = encoder.encode(versioned ? version + ":" + context : context);
    const plain = new Uint8Array(await crypto.subtle.decrypt(
      { name: "AES-GCM", iv: bytes.slice(offset, offset + 12), additionalData },
      await this.key(version), bytes.slice(offset + 12)));
    try { return JSON.parse(decoder.decode(plain)); }
    finally { plain.fill(0); }
  }
}
export async function verifySlackSignature(request, body, secret, now = Date.now()) {
  const timestamp = request.headers.get("x-slack-request-timestamp") ?? "";
  const signature = request.headers.get("x-slack-signature") ?? "";
  if (!/^\d{10}$/.test(timestamp) || Math.abs(now / 1000 - Number(timestamp)) > 300 || !/^v0=[0-9a-f]{64}$/i.test(signature)) return false;
  const key = await crypto.subtle.importKey("raw", encoder.encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["verify"]);
  const bytes = Uint8Array.from(signature.slice(3).match(/../g), h => parseInt(h, 16));
  const prefix = encoder.encode("v0:" + timestamp + ":");
  const signed = new Uint8Array(prefix.length + body.length);
  signed.set(prefix);
  signed.set(body, prefix.length);
  return crypto.subtle.verify("HMAC", key, bytes, signed);
}
export async function readBytes(request, maximum = 16384) {
  if (Number(request.headers.get("content-length") ?? 0) > maximum) throw new RelayError("too_large", 413);
  if (!request.body) return new Uint8Array();
  const reader = request.body.getReader(); let count = 0; const chunks = [];
  try {
    while (true) {
      const { done, value } = await reader.read(); if (done) break;
      count += value.byteLength; if (count > maximum) { await reader.cancel(); throw new RelayError("too_large", 413); }
      chunks.push(value);
    }
  } finally { reader.releaseLock(); }
  const result = new Uint8Array(count); let offset = 0;
  for (const chunk of chunks) { result.set(chunk, offset); offset += chunk.byteLength; }
  return result;
}
export async function readBody(request, maximum) {
  return decoder.decode(await readBytes(request, maximum));
}
export async function jsonBody(request) {
  try { return JSON.parse(await readBody(request)); }
  catch (error) {
    if (error instanceof RelayError) throw error;
    throw new RelayError("invalid_json", 400);
  }
}

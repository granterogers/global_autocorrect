// D179: the relay between installs that share what they learn and the GitHub job that folds it into the brain.
// It never writes to GitHub: the job (.github/workflows/community-learning.yml) collects from it with a read token, and
// nothing reaches any PC until the Brain gate's tests pass and the owner approves. It keeps one entry per install
// (replaced each time, gone after 60 days without a send) and refuses anything the app wouldn't send: the same checks as
// ShareLearning.Parse in the app (src/GlobalAutocorrect.Core/Learning/ShareLearning.cs; a test keeps the two in step).
// The sender's IP address is only used, hashed with a secret, to limit how much one address can send in a day.

export const STATS = [
  'writes', 'verifyFailures', 'stuck', 'undos', 'arrowFailures', 'retypeFailures', 'lineFailures', 'placeFailures',
  'enterSends', 'placeRefusals', 'suggestShown', 'suggestTaken', 'nextShown', 'nextTaken',
];
export const MAX_APPS = 200;
export const MAX_MISTAKES = 5000;
export const MAX_BYTES = 256 * 1024;
export const MAX_COUNT = 100000;
export const KEEP_DAYS = 60;
export const SENDS_PER_ADDRESS_PER_DAY = 6;
export const NEW_INSTALLS_PER_ADDRESS_PER_DAY = 3;

const ID = /^[A-Za-z0-9-]{8,40}$/;
const KEY = /^[0-9a-f]{32,128}$/;
const VERSION = /^[0-9A-Za-z.+-]{1,32}$/;
const PROGRAM = /^[a-z0-9][a-z0-9._-]{0,63}$/;
const FINGERPRINT = /^[0-9a-f]{16}$/;
const FIELDS = ['format', 'id', 'key', 'version', 'apps', 'mistakes'];

const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const only = (o, allowed) => Object.keys(o).every((k) => allowed.includes(k));

/** The submission if every part of it passes, else null. Nothing is guessed at or trimmed. */
export function check(s) {
  if (!isObject(s) || !only(s, FIELDS) || s.format !== 1) return null;
  if (typeof s.id !== 'string' || !ID.test(s.id) || s.id.startsWith('c-')) return null;
  if (typeof s.key !== 'string' || !KEY.test(s.key)) return null;
  if (typeof s.version !== 'string' || !VERSION.test(s.version)) return null;
  if (!isObject(s.apps) || !isObject(s.mistakes)) return null;
  const apps = Object.entries(s.apps);
  const marks = Object.entries(s.mistakes);
  if (apps.length > MAX_APPS || marks.length > MAX_MISTAKES) return null;
  for (const [name, stats] of apps) {
    if (!PROGRAM.test(name) || !isObject(stats) || !only(stats, STATS)) return null;
    for (const v of Object.values(stats)) if (typeof v !== 'number' || !Number.isFinite(v) || v < 0 || v > MAX_COUNT) return null;
  }
  for (const [mark, n] of marks) if (!FINGERPRINT.test(mark) || !Number.isInteger(n) || n < 1 || n > 1000) return null;
  return s;
}

async function sha256(text) {
  const bytes = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  return [...new Uint8Array(bytes)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

function sameText(a, b) {
  if (typeof a !== 'string' || typeof b !== 'string' || a.length !== b.length) return false;
  let d = 0;
  for (let i = 0; i < a.length; i++) d |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return d === 0;
}

const reply = (status, body) =>
  new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? {} : { 'content-type': 'application/json' },
  });

/** Adds one to a day's counter; false once it has reached the limit. */
async function within(kv, key, limit) {
  const n = Number((await kv.get(key)) ?? 0);
  if (n >= limit) return false;
  await kv.put(key, String(n + 1), { expirationTtl: 2 * 86400 });
  return true;
}

async function receive(request, env, now) {
  const text = await request.text();
  if (text.length > MAX_BYTES) return reply(413);
  let s;
  try { s = check(JSON.parse(text)); } catch { s = null; }
  if (!s) return reply(400);

  const day = new Date(now).toISOString().slice(0, 10);
  const address = (await sha256(`${env.SALT}|${request.headers.get('CF-Connecting-IP') ?? ''}`)).slice(0, 16);
  if (!(await within(env.LEARNING, `rate:${address}:${day}`, SENDS_PER_ADDRESS_PER_DAY))) return reply(429);

  const keyHash = await sha256(s.key);
  const existing = await env.LEARNING.get(`sub:${s.id}`, 'json');
  if (existing && !sameText(existing.keyHash, keyHash)) return reply(403);   // someone else's id
  if (!existing && !(await within(env.LEARNING, `new:${address}:${day}`, NEW_INSTALLS_PER_ADDRESS_PER_DAY))) return reply(429);

  const { key, ...kept } = s;
  await env.LEARNING.put(`sub:${s.id}`, JSON.stringify({ ...kept, keyHash, received: new Date(now).toISOString() }),
    { expirationTtl: KEEP_DAYS * 86400 });
  return reply(204);
}

async function list(request, env) {
  const auth = request.headers.get('Authorization') ?? '';
  if (!env.READ_TOKEN || !sameText(auth, `Bearer ${env.READ_TOKEN}`)) return reply(401);
  const out = [];
  let cursor;
  do {
    const page = await env.LEARNING.list({ prefix: 'sub:', cursor });
    for (const k of page.keys) {
      const v = await env.LEARNING.get(k.name, 'json');
      if (v) { const { keyHash, received, ...sub } = v; out.push(sub); }
    }
    cursor = page.list_complete ? undefined : page.cursor;
  } while (cursor);
  return reply(200, out);
}

export async function handle(request, env, now = Date.now()) {
  const url = new URL(request.url);
  if (url.pathname !== '/v1/learning') return reply(404);
  if (request.method === 'POST') return receive(request, env, now);
  if (request.method === 'GET') return list(request, env);
  return reply(405);
}

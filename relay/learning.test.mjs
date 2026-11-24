// D179: the relay's checks and limits. Run: node --test relay/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { handle, check, MAX_APPS, NEW_INSTALLS_PER_ADDRESS_PER_DAY, SENDS_PER_ADDRESS_PER_DAY } from './learning.mjs';

class FakeKv {
  constructor() { this.data = new Map(); }
  async get(k, type) { const v = this.data.get(k); return v === undefined ? null : type === 'json' ? JSON.parse(v) : v; }
  async put(k, v) { this.data.set(k, v); }
  async list({ prefix }) { return { keys: [...this.data.keys()].filter((k) => k.startsWith(prefix)).map((name) => ({ name })), list_complete: true }; }
}

const env = () => ({ LEARNING: new FakeKv(), READ_TOKEN: 'r'.repeat(40), SALT: 'salt' });
const sub = (over = {}) => ({
  format: 1, id: 'a1b2c3d4e5f6', key: 'f'.repeat(64), version: '1.50.0',
  apps: { notepad: { writes: 12.5, undos: 1 } }, mistakes: { '0123456789abcdef': 1 }, ...over,
});
const post = (body, ip = '1.2.3.4') => new Request('https://relay.example/v1/learning', {
  method: 'POST', body: typeof body === 'string' ? body : JSON.stringify(body), headers: { 'CF-Connecting-IP': ip },
});
const get = (token) => new Request('https://relay.example/v1/learning', { headers: token ? { Authorization: `Bearer ${token}` } : {} });

test('D179: a good submission is kept, and handed on without its key', async () => {
  const e = env();
  assert.equal((await handle(post(sub()), e)).status, 204);
  const r = await handle(get(e.READ_TOKEN), e);
  assert.equal(r.status, 200);
  const all = await r.json();
  assert.equal(all.length, 1);
  assert.equal(all[0].id, 'a1b2c3d4e5f6');
  assert.equal(all[0].key, undefined);
  assert.equal(all[0].keyHash, undefined);
  assert.deepEqual(all[0].apps, { notepad: { writes: 12.5, undos: 1 } });
});

test('D179: anything the app would not send is refused whole', () => {
  assert.ok(check(sub()));
  assert.equal(check(sub({ text: 'hello world' })), null);                       // an unknown field
  assert.equal(check(sub({ apps: { notepad: { typed: 3 } } })), null);           // an unknown count
  assert.equal(check(sub({ apps: { notepad: { writes: -1 } } })), null);
  assert.equal(check(sub({ apps: { notepad: { writes: 1e9 } } })), null);        // trying to outweigh everyone
  assert.equal(check(sub({ apps: { 'My Document.docx': { writes: 1 } } })), null);
  assert.equal(check(sub({ mistakes: { 'teh -> the': 1 } })), null);             // words, not a fingerprint
  assert.equal(check(sub({ id: 'c-a1b2c3d4' })), null);
  assert.equal(check(sub({ key: 'short' })), null);
  const many = Object.fromEntries(Array.from({ length: MAX_APPS + 1 }, (_, i) => [`app${i}`, { writes: 1 }]));
  assert.equal(check(sub({ apps: many })), null);
});

test('D179: bad JSON and the wrong address or method are refused', async () => {
  const e = env();
  assert.equal((await handle(post('{not json'), e)).status, 400);
  assert.equal((await handle(new Request('https://relay.example/other', { method: 'POST', body: '{}' }), e)).status, 404);
  assert.equal((await handle(new Request('https://relay.example/v1/learning', { method: 'DELETE' }), e)).status, 405);
});

test('D179: only the read token lists what was sent', async () => {
  const e = env();
  await handle(post(sub()), e);
  assert.equal((await handle(get(), e)).status, 401);
  assert.equal((await handle(get('x'.repeat(40)), e)).status, 401);
});

test('D179: nobody can send as another install', async () => {
  const e = env();
  assert.equal((await handle(post(sub()), e)).status, 204);
  assert.equal((await handle(post(sub({ key: 'e'.repeat(64), apps: {} }), '5.6.7.8'), e)).status, 403);
  assert.equal((await handle(post(sub({ apps: {} })), e)).status, 204);          // the same install, its own key
  const [kept] = await (await handle(get(e.READ_TOKEN), e)).json();
  assert.deepEqual(kept.apps, {});
});

test('D179: one address can only add a few installs and send a few times a day', async () => {
  const e = env();
  for (let i = 0; i < NEW_INSTALLS_PER_ADDRESS_PER_DAY; i++)
    assert.equal((await handle(post(sub({ id: `install-${i}0000` })), e)).status, 204);
  assert.equal((await handle(post(sub({ id: 'install-x0000' })), e)).status, 429);
  const f = env();
  for (let i = 0; i < SENDS_PER_ADDRESS_PER_DAY; i++) assert.equal((await handle(post(sub()), f)).status, 204);
  assert.equal((await handle(post(sub()), f)).status, 429);
  assert.equal((await handle(post(sub(), '9.9.9.9'), f)).status, 204);            // another address isn't held back
});

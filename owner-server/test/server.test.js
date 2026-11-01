import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { encode } from '@auth/core/jwt';
import { createHash } from 'node:crypto';
import { authConfiguration, createApp } from '../server.js';
import { OWNER_GITHUB_ID, isOwner, validateRequest, preservesControlFields } from '../policy.js';

const baseEnv = { NODE_ENV: 'test', AUTH_SECRET: 'synthetic-test-secret-of-at-least-32-characters',
  GITHUB_CLIENT_ID: 'test-client', GITHUB_CLIENT_SECRET: 'test-client-secret', GITHUB_REPO_TOKEN: 'synthetic-server-only-credential' };
const feed = { schema: 1, revision: 2, policy: { enabled: true }, appearance: { title: 'שלום' }, ads: [], latest: { version: '2.4.0' } };
const sha = 'a'.repeat(40);
const b64 = value => Buffer.from(JSON.stringify(value)).toString('base64');

test('only the stable GitHub owner ID can authenticate', () => {
  assert.equal(isOwner('github', { id: Number(OWNER_GITHUB_ID) }, baseEnv), true);
  assert.equal(isOwner('github', { id: 1, login: 'yosef-chadad', email: 'owner@example.com' }, baseEnv), false);
  assert.equal(isOwner('credentials', { id: OWNER_GITHUB_ID }, baseEnv), false);
});
test('Google is opt-in and requires verified owner email and optional stable sub', () => {
  const profile = { sub: 'google-owner', email: 'owner@gmail.com', email_verified: true };
  assert.equal(isOwner('google', profile, baseEnv), false);
  const env = { ...baseEnv, OWNER_GOOGLE_EMAIL: 'Owner@gmail.com', OWNER_GOOGLE_SUB: 'google-owner' };
  assert.equal(isOwner('google', profile, env), true);
  assert.equal(isOwner('google', { ...profile, email_verified: false }, env), false);
  assert.equal(isOwner('google', { ...profile, sub: 'someone-else' }, env), false);
  assert.equal(isOwner('google', { ...profile, email: 'different@gmail.com' }, env), false);
});
test('JWT/session callbacks expose no provider or repository credentials', async () => {
  const config = authConfiguration(baseEnv);
  const token = await config.callbacks.jwt({ token: {}, account: { provider: 'github', access_token: 'do-not-expose' }, profile: { id: OWNER_GITHUB_ID, name: 'Owner' } });
  assert.equal(token.ownerId, OWNER_GITHUB_ID); assert(!JSON.stringify(token).includes('do-not-expose'));
  const session = await config.callbacks.session({ session: { expires: 'later' }, token });
  assert.equal(session.ownerId, OWNER_GITHUB_ID); assert(!JSON.stringify(session).includes('synthetic-server-only-credential'));
  assert.equal(await config.callbacks.jwt({ token: {}, account: { provider: 'github' }, profile: { id: 1 } }), null);
  const updated = await config.callbacks.jwt({ token, trigger: 'update', session: { ownerId: 'attacker' } });
  assert.equal(updated.ownerId, OWNER_GITHUB_ID);
  assert.equal((await config.callbacks.redirect({ url: 'https://evil.example', baseUrl: 'https://owner.example' })), 'https://owner.example');
});
test('API cannot mutate arbitrary source paths, use other branches, or add a third slot', () => {
  assert.throws(() => validateRequest({ path: '/contents/index.html', method: 'PUT', body: { branch: 'main' } }));
  assert.throws(() => validateRequest({ path: '/contents/feed.json', method: 'PUT', body: { branch: 'other', sha, content: b64(feed) } }));
  assert.throws(() => validateRequest({ path: '/contents/feed.json', method: 'PUT', body: { branch: 'main', sha, content: b64({ ...feed, ads: [{ placement: 'bottom', image: 'https://example.com/a.png' }] }) } }));
  const proposed = { ...feed, revision: 3, ads: [{ placement: 'top', image: 'https://example.com/a.png', link: '' }] };
  assert(preservesControlFields(feed, proposed));
  assert(!preservesControlFields(feed, { ...proposed, policy: { enabled: false } }));
  assert(!preservesControlFields(feed, { ...proposed, latest: { version: '999.0.0' } }));
});

test('real HTTP server protects admin and writes; encrypted login survives requests', async () => {
  const calls = [];
  let app;
  const server = http.createServer((req, res) => app(req, res));
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const origin = `http://127.0.0.1:${server.address().port}`;
  const env = { ...baseEnv, APP_ORIGIN: origin };
  app = createApp(env, { fetch: async (url, options) => {
    calls.push({ url, options });
    assert(url.startsWith('https://api.github.com/repos/yosef-chadad/formatflow-control/contents/'));
    assert.equal(options.headers.Authorization, 'Bearer synthetic-server-only-credential');
    return { ok: true, json: async () => options.method === 'GET' ? { sha, content: b64(feed) } : { saved: true } };
  } });
  const request = (path, options = {}) => fetch(origin + path, { redirect: 'manual', ...options });
  const post = (body, headers = {}) => request('/api/repository', { method: 'POST', headers: { 'Content-Type': 'application/json', Origin: origin, 'X-FormatFlow-Request': '1', ...headers }, body: JSON.stringify(body) });
  try {
    const anon = await request('/'); assert.equal(anon.status, 302); assert.equal(anon.headers.get('location'), '/login');
    assert.equal((await post({ path: '/contents/?ref=main' })).status, 401); assert.equal(calls.length, 0);
    assert.equal((await request('/private/admin.html')).status, 404);
    const login = await request('/login'); const loginHtml = await login.text();
    assert(!loginHtml.includes('synthetic-server-only-credential'));
    const script = loginHtml.match(/<script>([\s\S]*?)<\/script>/)[1];
    assert(login.headers.get('content-security-policy').includes(createHash('sha256').update(script).digest('base64')));
    const jwt = await encode({ token: { sub: OWNER_GITHUB_ID, ownerId: OWNER_GITHUB_ID, provider: 'github', name: 'Owner' }, secret: env.AUTH_SECRET, salt: 'authjs.session-token', maxAge: 2592000 });
    const cookie = 'authjs.session-token=' + jwt;
    const ownerPage = await request('/', { headers: { Cookie: cookie } }); assert.equal(ownerPage.status, 200);
    const html = await ownerPage.text(); assert(html.includes('שמור ופרסם')); assert(!html.includes('tokenInput')); assert(!html.includes('synthetic-server-only-credential'));
    assert.equal(ownerPage.headers.get('cache-control'), 'no-store');
    assert.equal((await request('/api/me', { headers: { Cookie: cookie } })).status, 200);
    const otherJwt = await encode({ token: { sub: 'other', ownerId: 'other', provider: 'github' }, secret: env.AUTH_SECRET, salt: 'authjs.session-token', maxAge: 100 });
    assert.equal((await post({ path: '/contents/?ref=main' }, { Cookie: 'authjs.session-token=' + otherJwt })).status, 401);
    assert.equal((await post({ path: '/contents/?ref=main' }, { Cookie: cookie, Origin: 'https://evil.example' })).status, 403);
    assert.equal((await post({ path: '/contents/?ref=main' }, { Cookie: cookie, 'X-FormatFlow-Request': '' })).status, 403);
    assert.equal((await post({ path: '/contents/?ref=main' }, { Cookie: cookie })).status, 200);
    assert.equal(calls.length, 1);
    const proposed = { ...feed, revision: 3, ads: [{ placement: 'belowSearch', image: 'https://example.com/banner.png', link: '' }] };
    const saved = await post({ path: '/contents/feed.json', method: 'PUT', body: { branch: 'main', sha, content: b64(proposed) } }, { Cookie: cookie });
    assert.equal(saved.status, 200); assert.equal(calls.filter(c => c.options.method === 'PUT').length, 1);
    const changed = { ...proposed, policy: { enabled: false } };
    assert.equal((await post({ path: '/contents/feed.json', method: 'PUT', body: { branch: 'main', sha, content: b64(changed) } }, { Cookie: cookie })).status, 400);
    assert.equal(calls.filter(c => c.options.method === 'PUT').length, 1);
    const csrfResponse = await request('/auth/csrf'); assert.equal(csrfResponse.status, 200);
    const csrf = await csrfResponse.json(); const csrfCookies = csrfResponse.headers.getSetCookie().map(v => v.split(';')[0]).join('; ');
    const signIn = await request('/auth/signin/github', { method: 'POST', headers: { Cookie: csrfCookies, 'Content-Type': 'application/x-www-form-urlencoded', Origin: origin }, body: new URLSearchParams({ csrfToken: csrf.csrfToken, callbackUrl: '/' }).toString() });
    assert.equal(signIn.status, 302); assert.equal(new URL(signIn.headers.get('location')).origin, 'https://github.com');
    assert(new URL(signIn.headers.get('location')).searchParams.get('state'));
    const badCsrf = await request('/auth/signin/github', { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: 'csrfToken=fake' });
    assert(!String(badCsrf.headers.get('location')).startsWith('https://github.com/'));
    const expired = await encode({ token: { ownerId: OWNER_GITHUB_ID, provider: 'github' }, secret: env.AUTH_SECRET, salt: 'authjs.session-token', maxAge: -600 });
    assert.equal((await request('/api/me', { headers: { Cookie: 'authjs.session-token=' + expired } })).status, 401);
  } finally { await new Promise(resolve => server.close(resolve)); }
});

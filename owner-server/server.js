import express from 'express';
import helmet from 'helmet';
import { ExpressAuth, getSession } from '@auth/express';
import GitHub from '@auth/express/providers/github';
import Google from '@auth/express/providers/google';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { REPOSITORY, OWNER_GITHUB_ID, isOwner, googleEnabled, validateRequest, preservesControlFields } from './policy.js';

export function authConfiguration(env) {
  const providers = [GitHub({ checks: ['state', 'pkce'], clientId: env.GITHUB_CLIENT_ID, clientSecret: env.GITHUB_CLIENT_SECRET,
    authorization: { params: { scope: 'read:user user:email' } } })];
  if (googleEnabled(env)) providers.push(Google({ checks: ['state', 'pkce', 'nonce'], clientId: env.GOOGLE_CLIENT_ID, clientSecret: env.GOOGLE_CLIENT_SECRET }));
  return {
    providers, secret: env.AUTH_SECRET, trustHost: true, useSecureCookies: env.NODE_ENV !== 'test', basePath: '/auth',
    session: { strategy: 'jwt', maxAge: 30 * 24 * 60 * 60 },
    pages: { signIn: '/login', error: '/login' },
    logger: { error(error) { console.error('Authentication error:', error.type || 'AuthError'); }, warn(code) { console.warn('Authentication warning:', code); } },
    callbacks: {
      signIn: ({ account, profile }) => isOwner(account?.provider, profile, env),
      jwt({ token, account, profile }) {
        if (!account) return token;
        if (!isOwner(account.provider, profile, env)) return null;
        // OAuth access/refresh/ID tokens are deliberately absent from the session.
        return { sub: OWNER_GITHUB_ID, ownerId: OWNER_GITHUB_ID, provider: account.provider,
          name: profile.name || profile.login || 'FormatFlow Owner', email: profile.email || null,
          googleSub: account.provider === 'google' ? profile.sub : null };
      },
      session({ session, token }) {
        const googleAllowed = token.provider !== 'google' || (googleEnabled(env) &&
          String(token.email || '').toLowerCase() === env.OWNER_GOOGLE_EMAIL.trim().toLowerCase() &&
          (!env.OWNER_GOOGLE_SUB || token.googleSub === env.OWNER_GOOGLE_SUB));
        return { expires: session.expires, ownerId: token.ownerId === OWNER_GITHUB_ID && googleAllowed ? OWNER_GITHUB_ID : null,
          user: { name: token.name, email: token.email } };
      },
      redirect({ url, baseUrl }) {
        try { const target = new URL(url, baseUrl); return target.origin === new URL(baseUrl).origin ? target.href : baseUrl; }
        catch { return baseUrl; }
      }
    }
  };
}

function loadPage(name) { return readFileSync(fileURLToPath(new URL('./private/' + name, import.meta.url)), 'utf8'); }
function scriptsOf(html) { return [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].map(m => "'sha256-" + createHash('sha256').update(m[1]).digest('base64') + "'"); }
function publicError(status) {
  if (status === 401 || status === 403) return 'אין לשרת הרשאת כתיבה תקפה למאגר. יש לבדוק את מפתח השרת בהגדרות האירוח.';
  if (status === 409 || status === 422) return 'הקובץ השתנה או שהשמירה נדחתה. טען מחדש לפני ניסיון נוסף.';
  return 'GitHub לא השלים את הבקשה. טען מחדש את המצב לפני ניסיון נוסף.';
}

export function createApp(env = process.env, services = {}) {
  const missing = ['AUTH_SECRET', 'GITHUB_CLIENT_ID', 'GITHUB_CLIENT_SECRET', 'GITHUB_REPO_TOKEN'].filter(key => !env[key]);
  if (missing.length || (env.AUTH_SECRET || '').length < 32) throw new Error('Server authentication secrets are not configured.');
  const origin = new URL(env.APP_ORIGIN || env.RENDER_EXTERNAL_URL || '');
  if (origin.protocol !== 'https:' && !(env.NODE_ENV === 'test' && origin.hostname === '127.0.0.1')) throw new Error('A canonical HTTPS origin is required.');
  if (origin.username || origin.password || origin.pathname !== '/' || origin.search || origin.hash) throw new Error('Use a bare canonical origin.');
  const config = authConfiguration(env), app = express();
  const admin = loadPage('admin.html'), login = loadPage('login.html').replace('GOOGLE_ENABLED', googleEnabled(env) ? 'true' : 'false');
  const sessionFor = services.sessionFor || (request => getSession(request, config));
  const upstream = services.fetch || fetch;
  app.disable('x-powered-by'); app.set('trust proxy', 1);
  app.use(helmet({ contentSecurityPolicy: { directives: {
    defaultSrc: ["'self'"], scriptSrc: ["'self'", ...scriptsOf(admin), ...scriptsOf(login)],
    styleSrc: ["'self'", "'unsafe-inline'"], imgSrc: ["'self'", 'https:', 'blob:', 'data:'],
    connectSrc: ["'self'"], formAction: ["'self'", "https://github.com", "https://accounts.google.com"], frameAncestors: ["'none'"], objectSrc: ["'none'"], baseUri: ["'none'"]
  } }, referrerPolicy: { policy: 'no-referrer' } }));
  app.use((req, res, next) => { res.set('Cache-Control', 'no-store'); next(); });
  app.get('/health', (req, res) => res.json({ ok: true }));
  app.use((req, res, next) => {
    // Canonicalize the host consumed by the authentication library; never trust a supplied forwarded host.
    if (req.get('host') !== origin.host) return res.status(421).send('Unexpected host');
    req.headers['x-forwarded-host'] = origin.host;
    req.headers['x-forwarded-proto'] = origin.protocol.slice(0, -1);
    next();
  });
  app.use('/auth/*', ExpressAuth(config));
  app.get('/login', (req, res) => res.type('html').send(login));
  const requireOwner = async (req, res, next) => {
    try {
      const session = await sessionFor(req);
      if (session?.ownerId !== OWNER_GITHUB_ID) {
        if (req.path.startsWith('/api/')) return res.status(401).json({ error: 'נדרשת כניסה לחשבון הבעלים.' });
        return res.redirect('/login');
      }
      res.locals.session = session; next();
    } catch { res.status(401).json({ error: 'החיבור פג. יש להיכנס שוב.' }); }
  };
  app.get('/', requireOwner, (req, res) => res.type('html').send(admin));
  app.get('/api/me', requireOwner, (req, res) => res.json({ name: res.locals.session.user?.name || 'בעל החשבון' }));
  app.post('/api/repository', requireOwner, (req, res, next) => {
    if (req.get('origin') !== origin.origin || req.get('X-FormatFlow-Request') !== '1' || !req.is('application/json')) return res.status(403).json({ error: 'מקור הבקשה אינו מורשה.' });
    next();
  }, express.json({ limit: '9mb', strict: true }), async (req, res) => {
    try {
      const input = validateRequest(req.body);
      async function github(path, method = 'GET', body) {
        let response;
        try {
          response = await upstream('https://api.github.com/repos/' + REPOSITORY + path, {
            method, headers: { Authorization: 'Bearer ' + env.GITHUB_REPO_TOKEN, Accept: 'application/vnd.github+json',
              'User-Agent': 'FormatFlow-Owner-Server', 'X-GitHub-Api-Version': '2022-11-28', 'Content-Type': 'application/json' },
            body: body ? JSON.stringify(body) : undefined, signal: AbortSignal.timeout(25000), redirect: 'error'
          });
        } catch { throw Object.assign(new Error('החיבור ל־GitHub נקטע. ייתכן שהשמירה הושלמה; טען מחדש לפני ניסיון נוסף.'), { status: 502 }); }
        if (!response.ok) throw Object.assign(new Error(publicError(response.status)), { status: response.status === 409 ? 409 : 502 });
        return response.json();
      }
      if (input.feed) {
        const current = await github('/contents/feed.json?ref=main');
        if (current.sha !== input.body.sha) throw Object.assign(new Error('ההגדרות השתנו. טען מחדש לפני שמירה.'), { status: 409 });
        const existing = JSON.parse(Buffer.from(current.content, 'base64').toString('utf8'));
        if (!preservesControlFields(existing, input.feed)) throw Object.assign(new Error('מסך הפרסומות רשאי לשנות רק פרסומות ואת מספר העדכון.'), { status: 400 });
      }
      res.json(await github(input.path, input.method, input.body));
    } catch (error) { res.status(error.status || 500).json({ error: error.status ? error.message : 'הבקשה לא הושלמה.' }); }
  });
  app.use((req, res) => res.status(404).send('Not found'));
  app.use((error, req, res, next) => { res.status(error.type === 'entity.too.large' ? 413 : 400).json({ error: 'הבקשה אינה תקינה או גדולה מדי.' }); });
  return app;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  try { createApp().listen(Number(process.env.PORT || 3000), '0.0.0.0'); }
  catch { console.error('Owner server is not configured. Set the required hosting secrets and canonical origin.'); process.exitCode = 1; }
}

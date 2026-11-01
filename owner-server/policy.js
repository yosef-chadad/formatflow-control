export const REPOSITORY = 'yosef-chadad/formatflow-control';
export const OWNER_GITHUB_ID = '239200567';

export function isOwner(provider, profile, env) {
  if (provider === 'github') return String(profile?.id ?? '') === OWNER_GITHUB_ID;
  if (provider !== 'google' || profile?.email_verified !== true || !profile?.sub) return false;
  const expected = (env.OWNER_GOOGLE_EMAIL || '').trim().toLowerCase();
  if (!expected || String(profile.email || '').trim().toLowerCase() !== expected) return false;
  return !env.OWNER_GOOGLE_SUB || profile.sub === env.OWNER_GOOGLE_SUB;
}

export function googleEnabled(env) {
  return Boolean(env.GOOGLE_CLIENT_ID && env.GOOGLE_CLIENT_SECRET && env.OWNER_GOOGLE_EMAIL);
}

export function https(value) {
  if (typeof value !== 'string' || value.length > 2048) return false;
  try { const u = new URL(value); return u.protocol === 'https:' && !u.username && !u.password && !u.port; } catch { return false; }
}

export function imageDimensions(bytes) {
  if (bytes.length < 24 || bytes.length > 6 * 1024 * 1024) return null;
  let width = 0, height = 0;
  if (bytes.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10])) && bytes.toString('ascii', 12, 16) === 'IHDR') {
    width = bytes.readUInt32BE(16); height = bytes.readUInt32BE(20);
  } else if (bytes[0] === 255 && bytes[1] === 216) {
    let p = 2;
    while (p < bytes.length) {
      if (bytes[p++] !== 255) return null;
      while (p < bytes.length && bytes[p] === 255) p++;
      if (p >= bytes.length) return null;
      const marker = bytes[p++];
      if (marker === 217 || marker === 218) break;
      if (marker === 1 || (marker >= 208 && marker <= 215)) continue;
      if (p + 2 > bytes.length) return null;
      const size = bytes.readUInt16BE(p);
      if (size < 2 || p + size > bytes.length) return null;
      if (marker >= 192 && marker <= 207 && ![196, 200, 204].includes(marker)) {
        if (size < 8) return null;
        height = bytes.readUInt16BE(p + 3); width = bytes.readUInt16BE(p + 5); break;
      }
      p += size;
    }
  }
  return width > 0 && height > 0 && width <= 8192 && height <= 4096 && width * height <= 8000000 ? { width, height } : null;
}

function bad() { throw Object.assign(new Error('בקשה לא נתמכת.'), { status: 400 }); }

export function validateRequest(input) {
  if (!input || typeof input !== 'object') bad();
  const method = input.method || 'GET';
  if (method === 'GET' && ['/contents/feed.json?ref=main', '/contents/?ref=main'].includes(input.path)) {
    return { path: input.path, method };
  }
  if (method !== 'PUT' || !input.body || typeof input.body !== 'object' || input.body.branch !== 'main') bad();
  const { path, body } = input;
  if (path === '/contents/feed.json') {
    if (!/^[0-9a-f]{40}$/.test(body.sha || '') || typeof body.content !== 'string' || body.content.length > 350000) bad();
    let feed;
    try { feed = JSON.parse(Buffer.from(body.content, 'base64').toString('utf8')); } catch { bad(); }
    if (feed?.schema !== 1 || !Array.isArray(feed.ads) || feed.ads.length > 2 || !Number.isSafeInteger(feed.revision)) bad();
    const slots = new Set();
    for (const ad of feed.ads) {
      if (!ad || !['top', 'belowSearch'].includes(ad.placement) || slots.has(ad.placement)) bad();
      if (!https(ad.image) || (ad.link && !https(ad.link))) bad();
      slots.add(ad.placement);
    }
    // Other feed fields are checked against the current feed before the write.
    return { path, method, feed, body: { branch: 'main', sha: body.sha, content: body.content, message: 'Update advertisements from owner dashboard' } };
  }
  if (!/^\/contents\/ad-\d{13}-[0-9a-f]{12}\.(png|jpg)$/.test(path || '') || body.sha) bad();
  if (typeof body.content !== 'string' || body.content.length > 8388608 || !/^[A-Za-z0-9+/]*={0,2}$/.test(body.content)) bad();
  const bytes = Buffer.from(body.content, 'base64');
  if (!imageDimensions(bytes)) bad();
  if ((path.endsWith('.png') && bytes[0] !== 137) || (path.endsWith('.jpg') && bytes[0] !== 255)) bad();
  return { path, method, body: { branch: 'main', content: body.content, message: 'Upload advertisement image from owner dashboard' } };
}

export function preservesControlFields(current, proposed) {
  const omitAds = value => {
    const copy = { ...value }; delete copy.ads; delete copy.revision; return copy;
  };
  const canonical = value => {
    if (Array.isArray(value)) return value.map(canonical);
    if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).sort().map(k => [k, canonical(value[k])]));
    return value;
  };
  return JSON.stringify(canonical(omitAds(current))) === JSON.stringify(canonical(omitAds(proposed))) && proposed.revision === current.revision + 1;
}

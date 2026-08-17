const express = require('express');
const bodyParser = require('body-parser');
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');
const jwt = require('jsonwebtoken');
const multer = require('multer');
const ffmpeg = require('fluent-ffmpeg');

const app = express();
app.use(bodyParser.json({ limit: '1mb' }));

// Serve uploaded files and admin UI
// admin auth middleware: supports Basic (ADMIN_BASIC_USER/ADMIN_BASIC_PASS) or Bearer token (ADMIN_TOKEN)
function adminAuth(req, res, next) {
  const basicUser = process.env.ADMIN_BASIC_USER;
  const basicPass = process.env.ADMIN_BASIC_PASS;
  const adminToken = process.env.ADMIN_TOKEN;

  const auth = req.headers['authorization'];
  if (auth && auth.startsWith('Basic ') && basicUser && basicPass) {
    const b = Buffer.from(auth.slice(6), 'base64').toString('utf8');
    const parts = b.split(':');
    if (parts[0] === basicUser && parts[1] === basicPass) return next();
  }

  if (auth && auth.startsWith('Bearer ')) {
    const token = auth.slice(7);
    // direct token
    if (adminToken && token === adminToken) return next();
    // try verify JWT
    if (publicKeyPem) {
      try {
        const decoded = jwt.verify(token, publicKeyPem, { algorithms: ['RS256'] });
        if (decoded && decoded.role === 'admin') return next();
      } catch (e) {
        // fallthrough
      }
    }
  }

  // x-admin-token support
  const xadmin = req.headers['x-admin-token'];
  if (xadmin && adminToken && xadmin === adminToken) return next();

  res.setHeader('WWW-Authenticate', 'Basic realm="Admin"');
  return res.status(401).send('Unauthorized');
}

app.use('/uploads', express.static(path.join(__dirname, 'uploads')));
app.use('/admin', adminAuth, express.static(path.join(__dirname, 'public', 'admin')));

const SERVER_SECRET = process.env.SERVER_SECRET || 'server_demo_secret';
const CLIENT_SECRET = process.env.CLIENT_SECRET || 'local_demo_secret';

function computeHmac(payload, key) {
  return crypto.createHmac('sha256', key).update(payload, 'utf8').digest('base64');
}

app.post('/api/sign', (req, res) => {
  const { payload, clientSignature } = req.body || {};
  if (!payload) return res.status(400).json({ error: 'missing payload' });

  // Optional: verify client signature using CLIENT_SECRET (demo-only)
  let clientValid = false;
  try {
    const expected = computeHmac(payload, CLIENT_SECRET);
    clientValid = (expected === clientSignature);
  } catch (e) {
    clientValid = false;
  }

  const serverSignature = computeHmac(payload, SERVER_SECRET);
  return res.json({ serverSignature, clientValid });
});

app.post('/api/verify', (req, res) => {
  const { payload, serverSignature } = req.body || {};
  if (!payload || !serverSignature) return res.status(400).json({ error: 'missing fields' });
  const expected = computeHmac(payload, SERVER_SECRET);
  return res.json({ valid: expected === serverSignature });
});

const port = process.env.PORT || 3000;
app.listen(port, () => console.log(`Server demo listening on http://localhost:${port}`));

// Manager tokens storage (secure rotation demo)
const managerTokensPath = path.join(__dirname, 'manager_tokens.json');
const managerTokens = new Map(); // managerId -> token

function loadManagerTokens() {
  try {
    if (fs.existsSync(managerTokensPath)) {
      const raw = fs.readFileSync(managerTokensPath, 'utf8');
      const obj = JSON.parse(raw);
      if (obj && Array.isArray(obj.tokens)) {
        obj.tokens.forEach(t => managerTokens.set(t.managerId, t.token));
      }
    }
  } catch (e) { console.warn('Failed to load manager tokens:', e.message); }
}

function saveManagerTokens() {
  try {
    const arr = [];
    for (const [managerId, token] of managerTokens.entries()) arr.push({ managerId, token });
    fs.writeFileSync(managerTokensPath, JSON.stringify({ tokens: arr }, null, 2));
  } catch (e) { console.warn('Failed to save manager tokens:', e.message); }
}

loadManagerTokens();


// --- RSA key loading / generation for JWT demo ---
const keyDir = path.join(__dirname, 'keys');
let privateKeyPem = null;
let publicKeyPem = null;

function ensureKeys()
{
  try {
    const privPath = path.join(keyDir, 'private.pem');
    const pubPath = path.join(keyDir, 'public.pem');
    if (fs.existsSync(privPath) && fs.existsSync(pubPath)) {
      privateKeyPem = fs.readFileSync(privPath, 'utf8');
      publicKeyPem = fs.readFileSync(pubPath, 'utf8');
      console.log('Loaded RSA keys from disk');
      return;
    }
  } catch (e) {
    console.warn('Failed to read key files, will generate new pair:', e.message);
  }

  console.log('Generating RSA keypair for JWT demo...');
  const { publicKey, privateKey } = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
  privateKeyPem = privateKey.export({ type: 'pkcs1', format: 'pem' });
  publicKeyPem = publicKey.export({ type: 'pkcs1', format: 'pem' });

  try {
    if (!fs.existsSync(keyDir)) fs.mkdirSync(keyDir, { recursive: true });
    fs.writeFileSync(path.join(keyDir, 'private.pem'), privateKeyPem);
    fs.writeFileSync(path.join(keyDir, 'public.pem'), publicKeyPem);
    console.log('Wrote generated keys to', keyDir);
  } catch (e) {
    console.warn('Could not write keys to disk:', e.message);
  }
}

ensureKeys();
// --- Prepare JWK and kid ---
let jwk = null;
let jwkKid = null;
try {
  jwk = crypto.createPublicKey(publicKeyPem).export({ format: 'jwk' });
  // compute kid as base64url(sha256(publicKeyPem))
  const hash = crypto.createHash('sha256').update(publicKeyPem, 'utf8').digest();
  jwkKid = hash.toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  jwk.kid = jwkKid;
  // ensure kty is present
  if (!jwk.kty) jwk.kty = 'RSA';
} catch (e) {
  console.warn('Failed to derive JWK from public key:', e.message);
}

// JWT endpoints
app.post('/api/jwt-sign', (req, res) => {
  const { payload, expiresIn } = req.body || {};
  if (!payload) return res.status(400).json({ error: 'missing payload' });
  let payloadObj = payload;
  try { if (typeof payload === 'string') payloadObj = JSON.parse(payload); } catch (e) { /* leave as string */ }
  try {
    const signOpts = { algorithm: 'RS256', expiresIn: expiresIn || '1h' };
    if (jwkKid) signOpts.keyid = jwkKid;
    const token = jwt.sign(payloadObj, privateKeyPem, signOpts);
    return res.json({ token });
  } catch (e) {
    return res.status(500).json({ error: e.message });
  }
});

// --- Waiter creative uploads admin API ---
const waiterUploadsDir = path.join(__dirname, 'uploads', 'waiters');
// ensure base dir
if (!fs.existsSync(waiterUploadsDir)) fs.mkdirSync(waiterUploadsDir, { recursive: true });

// multer storage that places uploads into waiter-specific subfolder
const storage = multer.diskStorage({
  destination: function (req, file, cb) {
    const waiterId = (req.body.waiterId || req.query.waiterId || 'default').replace(/[^a-zA-Z0-9_\-]/g, '_');
    const dest = path.join(waiterUploadsDir, waiterId);
    if (!fs.existsSync(dest)) fs.mkdirSync(dest, { recursive: true });
    cb(null, dest);
  },
  filename: function (req, file, cb) {
    cb(null, file.originalname);
  }
});
const upload = multer({ storage: storage, limits: { fileSize: 50 * 1024 * 1024 } });

// List waiters and their files
app.get('/admin/api/waiters', (req, res) => {
  try {
    const data = {};
    if (!fs.existsSync(waiterUploadsDir)) { return res.json({ waiters: {} }); }
    const waiterDirs = fs.readdirSync(waiterUploadsDir, { withFileTypes: true });
    waiterDirs.forEach(d => {
      if (!d.isDirectory()) return;
      const waiterId = d.name;
      const files = fs.readdirSync(path.join(waiterUploadsDir, waiterId)).filter(f => f.toLowerCase().endsWith('.mp4') || f.toLowerCase().endsWith('.mov') || f.toLowerCase().endsWith('.webm'));
      data[waiterId] = files.map(f => ({ fileName: f, url: `/uploads/waiters/${encodeURIComponent(waiterId)}/${encodeURIComponent(f)}` }));
    });
    return res.json({ waiters: data });
  } catch (e) {
    return res.status(500).json({ error: e.message });
  }
});

// Upload creative file for waiterId (multipart/form-data, field 'file')
app.post('/admin/api/upload', upload.single('file'), (req, res) => {
  try {
    if (!req.file) return res.status(400).json({ error: 'no file' });
    const waiterId = req.body.waiterId || 'default';
    const fileName = req.file.originalname;
    const waiterDir = path.join(waiterUploadsDir, waiterId);
    const filePath = path.join(waiterDir, fileName);

    // create default metadata sidecar: fileName.json
    const meta = {
      creativeId: path.parse(fileName).name,
      waiterId: waiterId,
      videoTitle: path.parse(fileName).name,
      videoUrl: `/uploads/waiters/${encodeURIComponent(waiterId)}/${encodeURIComponent(fileName)}`,
      durationSeconds: 0,
      isExclusiveToPurchasers: false,
      requiredDrinkPurchases: 1,
      thumbnailUrl: null
    };
    const metaPath = filePath + '.json';
    try { fs.writeFileSync(metaPath, JSON.stringify(meta, null, 2)); } catch (e) { console.warn('Failed to write meta sidecar', e.message); }

    // attempt to generate thumbnail at 1s using ffmpeg (if available)
    const thumbName = fileName + '.jpg';
    const thumbPath = path.join(waiterDir, thumbName);
    try {
      ffmpeg(filePath)
        .screenshots({ timestamps: ['00:00:01.000', '00:00:00.500'], filename: fileName + '.jpg', folder: waiterDir, size: '480x?' })
        .on('end', () => {
          try {
            meta.thumbnailUrl = `/uploads/waiters/${encodeURIComponent(waiterId)}/${encodeURIComponent(thumbName)}`;
            fs.writeFileSync(metaPath, JSON.stringify(meta, null, 2));
            console.log('Thumbnail generated for', fileName);
          } catch (e) { console.warn('Failed to update meta with thumbnail', e.message); }
          // also probe for duration
          try {
            ffmpeg.ffprobe(filePath, (err, metadata) => {
              if (!err && metadata && metadata.format && metadata.format.duration) {
                try {
                  meta.durationSeconds = Math.round(metadata.format.duration);
                  fs.writeFileSync(metaPath, JSON.stringify(meta, null, 2));
                } catch (e) { console.warn('Failed to write duration to meta', e.message); }
              }
            });
          } catch (e) { console.warn('ffprobe failed:', e.message); }
        })
        .on('error', (err) => { console.warn('ffmpeg thumbnail generation failed:', err.message); });
    } catch (e) {
      console.warn('Thumbnail generation skipped:', e.message);
    }

    return res.json({ ok: true, waiterId, fileName: fileName, url: meta.videoUrl, meta });
  } catch (e) {
    return res.status(500).json({ error: e.message });
  }
});

// Admin login to issue short-lived admin JWT (RS256)
app.post('/admin/api/login', (req, res) => {
  const { username, password } = req.body || {};
  const basicUser = process.env.ADMIN_BASIC_USER;
  const basicPass = process.env.ADMIN_BASIC_PASS;
  if (!basicUser || !basicPass) return res.status(500).json({ error: 'admin credentials not configured' });
  if (!username || !password) return res.status(400).json({ error: 'missing fields' });
  if (username !== basicUser || password !== basicPass) return res.status(401).json({ error: 'invalid credentials' });
  try {
    const payload = { sub: username, role: 'admin' };
    const token = jwt.sign(payload, privateKeyPem, { algorithm: 'RS256', expiresIn: '1h', keyid: jwkKid });
    return res.json({ ok: true, token });
  } catch (e) {
    return res.status(500).json({ error: e.message });
  }
});

// Delete a file: JSON body { waiterId, fileName }
app.delete('/admin/api/file', (req, res) => {
  try {
    const { waiterId, fileName } = req.body || {};
    if (!waiterId || !fileName) return res.status(400).json({ error: 'missing fields' });
    const filePath = path.join(waiterUploadsDir, waiterId, fileName);
    if (!fs.existsSync(filePath)) return res.status(404).json({ error: 'file not found' });
    fs.unlinkSync(filePath);
    return res.json({ ok: true });
  } catch (e) {
    return res.status(500).json({ error: e.message });
  }
});

// Get metadata for a specific uploaded video
app.get('/admin/api/video-meta', adminAuth, (req, res) => {
  try {
    const waiterId = req.query.waiterId;
    const fileName = req.query.fileName;
    if (!waiterId || !fileName) return res.status(400).json({ error: 'missing fields' });
    const metaPath = path.join(waiterUploadsDir, waiterId, fileName + '.json');
    if (!fs.existsSync(metaPath)) return res.status(404).json({ error: 'meta not found' });
    const raw = fs.readFileSync(metaPath, 'utf8');
    return res.json(JSON.parse(raw));
  } catch (e) { return res.status(500).json({ error: e.message }); }
});

// Update metadata for a specific uploaded video (admin only)
app.post('/admin/api/video-meta', adminAuth, (req, res) => {
  try {
    const body = req.body || {};
    const waiterId = body.waiterId;
    const fileName = body.fileName;
    const updates = body.updates || {};
    if (!waiterId || !fileName) return res.status(400).json({ error: 'missing fields' });
    const metaPath = path.join(waiterUploadsDir, waiterId, fileName + '.json');
    let meta = {};
    if (fs.existsSync(metaPath)) {
      meta = JSON.parse(fs.readFileSync(metaPath, 'utf8'));
    }
    // apply updates shallow
    for (const k of Object.keys(updates)) meta[k] = updates[k];
    fs.writeFileSync(metaPath, JSON.stringify(meta, null, 2));
    return res.json({ ok: true, meta });
  } catch (e) { return res.status(500).json({ error: e.message }); }
});

// Public manifest endpoint: lists all videos and their metadata for Unity to consume
app.get('/api/uploads/creative-manifest', (req, res) => {
  try {
    const creatives = [];
    if (!fs.existsSync(waiterUploadsDir)) return res.json({ creatives });
    const waiterDirs = fs.readdirSync(waiterUploadsDir, { withFileTypes: true });
    waiterDirs.forEach(d => {
      if (!d.isDirectory()) return;
      const waiterId = d.name;
      const files = fs.readdirSync(path.join(waiterUploadsDir, waiterId)).filter(f => f.toLowerCase().endsWith('.mp4'));
      files.forEach(f => {
        const metaPath = path.join(waiterUploadsDir, waiterId, f + '.json');
        let meta = null;
        if (fs.existsSync(metaPath)) {
          try { meta = JSON.parse(fs.readFileSync(metaPath, 'utf8')); } catch (e) { meta = null; }
        }
        const creative = meta || {
          creativeId: path.parse(f).name,
          waiterId,
          videoTitle: path.parse(f).name,
          videoUrl: `/uploads/waiters/${encodeURIComponent(waiterId)}/${encodeURIComponent(f)}`,
          durationSeconds: 0,
          isExclusiveToPurchasers: false,
          requiredDrinkPurchases: 1,
          thumbnailUrl: null
        };
        creatives.push(creative);
      });
    });
    return res.json({ creatives });
  } catch (e) { return res.status(500).json({ error: e.message }); }
});


app.post('/api/jwt-verify', (req, res) => {
  const { token } = req.body || {};
  if (!token) return res.status(400).json({ error: 'missing token' });
  try {
    const decoded = jwt.verify(token, publicKeyPem, { algorithms: ['RS256'] });
    return res.json({ valid: true, decoded });
  } catch (e) {
    return res.json({ valid: false, error: e.message });
  }
});

// Verify an order receipt token against an orderID
app.post('/api/verify-order', (req, res) => {
  const { token, orderID } = req.body || {};
  if (!token || !orderID) return res.status(400).json({ error: 'missing fields' });
  try {
    const decoded = jwt.verify(token, publicKeyPem, { algorithms: ['RS256'] });
    if (decoded && decoded.orderID && decoded.orderID === orderID) return res.json({ valid: true, decoded });
    return res.json({ valid: false, error: 'order mismatch' });
  } catch (e) {
    return res.json({ valid: false, error: e.message });
  }
});

// JWKS endpoint
app.get('/.well-known/jwks.json', (req, res) => {
  if (!jwk) return res.status(500).json({ error: 'no jwk available' });
  return res.json({ keys: [jwk] });
});

// --- Snapchat OAuth demo endpoints (simulation when SNAPCHAT_CLIENT_* not configured) ---
app.get('/auth/snapchat/start', (req, res) => {
  const returnUrl = req.query.returnUrl || '/';
  const clientId = process.env.SNAPCHAT_CLIENT_ID;
  const redirectUri = (process.env.SERVER_BASE_URL || `http://localhost:${port}`) + '/auth/snapchat/callback';

  if (!clientId) {
    // Simulate by redirecting immediately to callback with a demo code
    const callbackUrl = `${redirectUri}?code=demo_code&returnUrl=${encodeURIComponent(returnUrl)}`;
    return res.redirect(callbackUrl);
  }

  const state = Math.random().toString(36).substring(2, 12);
  const authUrl = `https://accounts.snapchat.com/login/oauth2/authorize?response_type=code&client_id=${encodeURIComponent(clientId)}&redirect_uri=${encodeURIComponent(redirectUri)}&scope=openid&state=${encodeURIComponent(state)}`;
  return res.redirect(authUrl);
});

app.get('/auth/snapchat/callback', async (req, res) => {
  const code = req.query.code;
  const returnUrl = req.query.returnUrl || '/';
  if (!code) return res.status(400).send('missing code');

  // If SNAPCHAT_CLIENT_ID/SECRET provided, you could exchange the code with Snapchat's token endpoint here.
  // For demo, we skip the remote call and just issue a JWT that represents the authenticated user.
  const token = jwt.sign({ provider: 'snapchat', code: code, sub: 'snap_demo_user' }, privateKeyPem, { algorithm: 'RS256', expiresIn: '1h' });

  // If returnUrl is present, redirect with token in fragment for client-side consumption
  try {
    const redirectWithToken = `${returnUrl}#token=${encodeURIComponent(token)}`;
    const html = `<html><head><meta charset="utf-8"><title>Snapchat OAuth Demo</title></head><body><script>window.location='${redirectWithToken}';</script><p>Redirecting...</p></body></html>`;
    return res.send(html);
  } catch (e) {
    return res.json({ token });
  }
});

// Simple in-memory order store for demo purposes
const orders = new Map(); // orderID -> { orderID, amount, payID, reference, description, status }
const purchasesPath = path.join(__dirname, 'purchases.json');
const purchases = new Map(); // orderID -> { orderID, amount, status }

function loadPurchases() {
  try {
    if (fs.existsSync(purchasesPath)) {
      const raw = fs.readFileSync(purchasesPath, 'utf8');
      const obj = JSON.parse(raw);
      if (obj && Array.isArray(obj.purchases)) {
        obj.purchases.forEach(p => purchases.set(p.orderID, p));
      }
    }
  } catch (e) { console.warn('Failed to load purchases:', e.message); }
}

function savePurchases() {
  try {
    const arr = [];
    for (const [orderID, p] of purchases.entries()) arr.push(p);
    fs.writeFileSync(purchasesPath, JSON.stringify({ purchases: arr }, null, 2));
  } catch (e) { console.warn('Failed to save purchases:', e.message); }
}

loadPurchases();

app.post('/api/create-order', (req, res) => {
  const { orderID, amount, payID, reference, description } = req.body || {};
  if (!orderID || !amount || !payID) return res.status(400).json({ error: 'missing fields' });
  orders.set(orderID, { orderID, amount, payID, reference, description, status: 'pending' });
  // Optionally issue a signed JWT receipt for the order (demo RS256)
  let token = null;
  try {
    if (privateKeyPem) {
      const payload = { orderID, amount, payID, reference };
      const signOpts = { algorithm: 'RS256', expiresIn: '24h' };
      if (jwkKid) signOpts.keyid = jwkKid;
      token = jwt.sign(payload, privateKeyPem, signOpts);
    }
  } catch (e) {
    console.warn('Failed to sign order JWT:', e.message);
  }
  return res.json({ ok: true, orderID, token });
});

// Confirm purchase: client can ask server for authoritative signed confirmation for an order
app.post('/api/confirm-purchase', (req, res) => {
  const { orderID } = req.body || {};
  if (!orderID) return res.status(400).json({ error: 'missing orderID' });
  const ord = orders.get(orderID) || purchases.get(orderID);
  if (!ord) return res.status(404).json({ error: 'order not found' });
  const status = ord.status || (ord.status = 'unknown');
  if (status !== 'paid') return res.status(412).json({ ok: false, status: status, error: 'order not paid' });

  const payload = { orderID: orderID, amount: ord.amount || 0, status: 'paid' };
  try {
    const signOpts = { algorithm: 'RS256', expiresIn: '1h' };
    if (jwkKid) signOpts.keyid = jwkKid;
    const token = jwt.sign(payload, privateKeyPem, signOpts);
    return res.json({ ok: true, orderID, token, payload });
  } catch (e) {
    return res.status(500).json({ error: e.message });
  }
});

app.post('/api/order-status', (req, res) => {
  const { orderID } = req.body || {};
  if (!orderID) return res.status(400).json({ error: 'missing orderID' });
  const ord = orders.get(orderID);
  if (!ord) return res.json({ orderID, status: 'unknown' });
  return res.json({ orderID, status: ord.status });
});

// Admin/test endpoint to mark an order as paid
app.post('/api/mark-paid', (req, res) => {
  const { orderID } = req.body || {};
  if (!orderID) return res.status(400).json({ error: 'missing orderID' });
  const ord = orders.get(orderID);
  if (!ord) return res.status(404).json({ error: 'order not found' });
  ord.status = 'paid';
  orders.set(orderID, ord);
  // record purchase
  purchases.set(orderID, { orderID, amount: ord.amount, status: 'paid' });
  savePurchases();
  return res.json({ ok: true, orderID, status: 'paid' });
});

// Bank / payment provider webhook adapter
// Expects JSON body: { orderID }
// Requires header: X-Signature: base64(hmac_sha256(body, SERVER_SECRET))
app.post('/api/bank-webhook', (req, res) => {
  const sig = req.header('X-Signature');
  const bodyRaw = JSON.stringify(req.body || {});
  if (!sig) return res.status(400).json({ error: 'missing signature' });
  const expected = computeHmac(bodyRaw, SERVER_SECRET);
  if (expected !== sig) return res.status(403).json({ error: 'invalid signature' });
  const { orderID } = req.body || {};
  if (!orderID) return res.status(400).json({ error: 'missing orderID' });
  const ord = orders.get(orderID);
  if (!ord) return res.status(404).json({ error: 'order not found' });
  ord.status = 'paid';
  orders.set(orderID, ord);
  console.log('Bank webhook marked order paid:', orderID);
  return res.json({ ok: true, orderID, status: 'paid' });
});

// Revolut webhook adapter: verifies HMAC signature (REVOLUT_WEBHOOK_SECRET or SERVER_SECRET), marks order paid, and notifies Unity webhook receiver.
app.post('/api/revolut-webhook', async (req, res) => {
  const revSecret = process.env.REVOLUT_WEBHOOK_SECRET || process.env.SERVER_SECRET || SERVER_SECRET;
  const sig = req.header('X-Signature');
  const bodyRaw = JSON.stringify(req.body || {});
  if (!sig) return res.status(400).json({ error: 'missing signature' });
  const expected = computeHmac(bodyRaw, revSecret);
  if (expected !== sig) return res.status(403).json({ error: 'invalid signature' });

  const { orderID, reference, description } = req.body || {};
  if (!orderID && !reference && !description) return res.status(400).json({ error: 'missing identifying fields' });

  // Try to determine orderID: prefer explicit orderID, else find by reference or description matching stored orders
  let foundOrderId = orderID;
  if (!foundOrderId) {
    for (const [id, o] of orders.entries()) {
      if (reference && (o.reference === reference || o.orderID === reference)) { foundOrderId = id; break; }
      if (description && (o.description === description || o.description === o.description)) { foundOrderId = id; break; }
    }
  }

  if (!foundOrderId) return res.status(404).json({ error: 'order not found' });

  const ord = orders.get(foundOrderId);
  if (!ord) return res.status(404).json({ error: 'order not found' });
  ord.status = 'paid';
  orders.set(foundOrderId, ord);

  // record purchase
  purchases.set(foundOrderId, { orderID: foundOrderId, amount: ord.amount || 0, status: 'paid' });
  savePurchases();

  // Notify Unity webhook receiver if configured
  const unityWebhook = process.env.CLIENT_WEBHOOK_URL || 'http://localhost:8080/webhook';
  try {
    const notifyBody = JSON.stringify({ orderID: foundOrderId, provider: 'revolut' });
    const notifySig = computeHmac(notifyBody, SERVER_SECRET);
    const fetch = require('node-fetch');
    await fetch(unityWebhook, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Signature': notifySig }, body: notifyBody });
  } catch (e) {
    console.warn('Failed to notify Unity webhook:', e.message);
  }

  console.log('Revolut webhook processed and marked order paid:', foundOrderId);
  return res.json({ ok: true, orderID: foundOrderId, status: 'paid' });
});

// Return manager tokens (used by Unity or other trusted services)
app.get('/api/manager/tokens', (req, res) => {
  const arr = [];
  for (const [managerId, token] of managerTokens.entries()) arr.push({ managerId, token });
  return res.json({ tokens: arr });
});

// Update a manager token (protected by SERVER_SECRET via header X-Server-Secret)
app.post('/api/manager/update-token', (req, res) => {
  const provided = req.header('X-Server-Secret') || req.query.secret;
  if (!provided || provided !== SERVER_SECRET) return res.status(403).json({ error: 'forbidden' });
  const { managerId, token } = req.body || {};
  if (!managerId || !token) return res.status(400).json({ error: 'missing fields' });
  managerTokens.set(managerId, token);
  saveManagerTokens();
  return res.json({ ok: true, managerId });
});

// Proxy endpoint to send messages via Snapchat Business API on behalf of managers/admin.
// Protected by X-Server-Secret header. Body: { to, from, message }
app.post('/api/proxy/send_message', async (req, res) => {
  const provided = req.header('X-Server-Secret');
  if (!provided || provided !== SERVER_SECRET) return res.status(403).json({ error: 'forbidden' });
  const { to, from, message } = req.body || {};
  if (!to || !from || !message) return res.status(400).json({ error: 'missing fields' });

  // Resolve token for 'from'
  let token = null;
  if (managerTokens.has(from)) token = managerTokens.get(from);
  if (!token && process.env.SNAPCHAT_ADMIN_TOKEN && from === 'jackontherocks_admin') token = process.env.SNAPCHAT_ADMIN_TOKEN;

  const snapchatApiUrl = process.env.SNAPCHAT_API_URL || null; // optional forwarding
  if (!token)
  {
    return res.status(400).json({ error: 'no token available for sender' });
  }

  if (snapchatApiUrl)
  {
    try {
      const fetch = require('node-fetch');
      const resp = await fetch(snapchatApiUrl, { method: 'POST', headers: { 'Content-Type': 'application/json', 'Authorization': 'Bearer ' + token }, body: JSON.stringify({ to, from, message }) });
      const txt = await resp.text();
      return res.status(resp.status).send(txt);
    } catch (e) {
      console.warn('Proxy send_message failed:', e.message);
      return res.status(500).json({ error: e.message });
    }
  }

  // Demo-only: log and return ok
  console.log(`Proxy send_message (demo): from=${from} to=${to} message=${message}`);
  return res.json({ ok: true, from, to });
});

// Creative manifest example endpoint (returns sample creatives)
app.get('/api/creative-manifest', (req, res) => {
  const sample = {
    creatives: [
      { creativeId: 'Waiter_Alex_Bourbon_10s_v1', waiterName: 'Alex', durationSeconds: 10, videoUrl: 'https://cdn.example.com/creatives/alex_bourbon_10s.mp4', targetDrink: 'BourbonOnTheRocks', totalImpressions:0, totalClicks:0, completedPurchases:0 },
      { creativeId: 'Waitress_Eva_Vodka_5s_v1', waiterName: 'Eva', durationSeconds: 5, videoUrl: 'https://cdn.example.com/creatives/eva_vodka_5s.mp4', targetDrink: 'VodkaOnTheRocks', totalImpressions:0, totalClicks:0, completedPurchases:0 }
    ]
  };
  return res.json(sample);
});

// Analytics ingestion endpoint for uploads from Unity
app.post('/api/creative-analytics', (req, res) => {
  const payload = req.body || {};
  // In demo, we simply log incoming payload and return ok. In production persist to analytics store.
  console.log('Received creative analytics payload:', JSON.stringify(payload).substring(0, 1000));
  return res.json({ ok: true });
});

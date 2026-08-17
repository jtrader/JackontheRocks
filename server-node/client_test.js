const http = require('http');
const https = require('https');
const fs = require('fs');
const { URL } = require('url');

function postJson(urlStr, obj, extraHeaders = {}) {
  return new Promise((resolve, reject) => {
    const url = new URL(urlStr);
    const data = Buffer.from(JSON.stringify(obj), 'utf8');
    const opts = {
      method: 'POST',
      hostname: url.hostname,
      port: url.port || (url.protocol === 'https:' ? 443 : 80),
      path: url.pathname + (url.search || ''),
      headers: {
        'Content-Type': 'application/json',
        'Content-Length': data.length,
        ...extraHeaders
      }
    };

    const lib = url.protocol === 'https:' ? https : http;
    const req = lib.request(opts, (res) => {
      let body = '';
      res.setEncoding('utf8');
      res.on('data', (chunk) => body += chunk);
      res.on('end', () => {
        try { resolve(JSON.parse(body)); } catch (e) { resolve(body); }
      });
    });

    req.on('error', reject);
    req.write(data);
    req.end();
  });
}

async function run() {
  const serverUrl = process.env.SERVER_URL || 'http://localhost:3000';
  const webhookUrl = process.env.WEBHOOK_URL || '';
  const ci = process.env.CI === '1' || process.argv.includes('--ci');
  console.log('Using server:', serverUrl, 'webhook:', webhookUrl, 'ci:', ci);

  const payload = { rocks: 123, diamonds: 7 };
  console.log('Requesting JWT for payload:', payload);

  const signResp = await postJson(serverUrl + '/api/jwt-sign', { payload: payload, expiresIn: '1h' });
  if (!signResp || !signResp.token) {
    console.error('Failed to get token', signResp);
    process.exit(1);
  }

  const token = signResp.token;
  console.log('Received token:', token);
  fs.writeFileSync(__dirname + '/token.txt', token, 'utf8');
  console.log('Saved token to token.txt');

  // Optionally POST token back to Unity/webhook
  if (webhookUrl) {
    try {
      const headers = {};
      const webhookSecret = process.env.WEBHOOK_SECRET || '';
      if (webhookSecret) headers['X-Webhook-Secret'] = webhookSecret;
      const webhookResp = await postJson(webhookUrl, { token: token, payload: payload }, headers);
      console.log('Webhook response:', webhookResp);
    } catch (e) {
      console.error('Webhook POST failed:', e.message || e);
    }
  }

  console.log('Verifying token via /api/jwt-verify');
  const verifyResp = await postJson(serverUrl + '/api/jwt-verify', { token: token });
  console.log('Verify response:', verifyResp);

  if (ci) {
    if (!verifyResp || !verifyResp.valid) {
      console.error('CI check failed: token invalid');
      process.exit(2);
    }
    console.log('CI check passed: token valid');
  }
}

run().catch(err => { console.error('Error:', err); process.exit(1); });

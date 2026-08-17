Server demo README

This workspace contains two minimal demo servers for receipt signing/verification:

- `server-node/` — Node.js + Express demo
- `server-python/` — Python Flask demo

Both servers are demo-only and use HMAC-SHA256 with an in-code secret. Do NOT use in production.

Node.js (quick run)

1. Install dependencies:

```bash
cd server-node
npm install
```

2. Run the server:

```bash
npm start
```

By default it listens on `http://localhost:3000`. Endpoints:
- `POST /api/sign` — body: `{ payload: string, clientSignature?: string }` returns `{ serverSignature, clientValid }`
- `POST /api/verify` — body: `{ payload: string, serverSignature }` returns `{ valid }`

JWT endpoints (Node demo supports RSA JWT signing)

- `POST /api/jwt-sign` — body: `{ payload: object|string, expiresIn?: string }` returns `{ token }` (RS256-signed JWT)
- `POST /api/jwt-verify` — body: `{ token }` returns `{ valid, decoded | error }`

JWKS

- `GET /.well-known/jwks.json` — returns the server public keys in JWK format for RS256 token verification. The JWTs returned by `/api/jwt-sign` include a `kid` header; clients can fetch the matching JWK from this endpoint.


Example curl (sign):

```bash
curl -X POST http://localhost:3000/api/jwt-sign \
  -H "Content-Type: application/json" \
  -d '{"payload":{"rocks":100,"diamonds":5}}'
```

Example curl (verify):

```bash
curl -X POST http://localhost:3000/api/jwt-verify \
  -H "Content-Type: application/json" \
  -d '{"token":"<PASTE_TOKEN_HERE>"}'
```

Snapchat OAuth demo

- `GET /auth/snapchat/start?returnUrl=<url>` — starts the OAuth demo flow. If `SNAPCHAT_CLIENT_ID` is configured the endpoint will redirect to Snapchat's authorize URL; otherwise it simulates a login and redirects immediately to the callback with a demo code.
- `GET /auth/snapchat/callback?code=<code>&returnUrl=<url>` — callback that issues an RS256 JWT representing the logged-in user and redirects to `returnUrl#token=<JWT>`.

Quick test (simulated demo mode):

1. Start the Node server (no SNAPCHAT_* env vars required):

```bash
cd server-node
npm start
```

2. Open in a browser:

```
http://localhost:3000/auth/snapchat/start?returnUrl=http://localhost:3000/done
```

The server will simulate OAuth and redirect to `/auth/snapchat/callback`, which issues a JWT and redirects to `http://localhost:3000/done#token=<JWT>`.

3. Use `/api/jwt-verify` to validate the returned token.

If you have real Snapchat credentials, set `SNAPCHAT_CLIENT_ID` and `SNAPCHAT_CLIENT_SECRET` and `SERVER_BASE_URL` (publicly accessible redirect URI) before starting the server. The code includes a placeholder for exchanging the code with Snapchat's token endpoint but the demo currently simulates the exchange when creds are missing.

Unity webhook endpoint

The Unity example scene includes a local webhook receiver on port `8080` at path `/webhook` when created via the editor menu. To have `client_test.js` POST tokens to Unity, run Unity Play Mode with the example scene and then run:

```bash
WEBHOOK_URL=http://localhost:8080/webhook npm run client-test
```

The Unity `WebhookReceiver` will accept the POST and automatically apply the received token and payload to the running `SaveManager`.

Shared secret

To prevent unauthorized posts to the Unity webhook, you can set a shared secret. In Unity, set the `WebhookReceiver.requiredSecret` field on the `WebhookReceiver` GameObject (created by the example scene). When set, incoming requests must include the header `X-Webhook-Secret` with the same value.

To have `client_test.js` include the header, set the env var `WEBHOOK_SECRET` when running the client test:

```bash
WEBHOOK_URL=http://localhost:8080/webhook WEBHOOK_SECRET=mysecret npm run client-test
```

Python (quick run)

1. Create a virtualenv and install:

```bash
cd server-python
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
```

2. Run the server:

```bash
python server.py
```

By default it listens on `http://localhost:5000` with the same endpoints as above.

Integration notes

- Set `SERVER_SECRET` and `CLIENT_SECRET` in environment variables for both client and server during testing.
- Switch `SaveManager.SubmitReceiptToServerCoroutine` to call one of these endpoints (`/api/sign`) via `UnityWebRequest` instead of calling the `ServerStub` directly.

Order lifecycle (demo)

- `POST /api/create-order` — body: `{ orderID, amount, payID, reference, description }` creates an in-memory order with status `pending`.
- `POST /api/order-status` — body: `{ orderID }` returns `{ orderID, status }` where status is `pending` or `paid`.
- `POST /api/mark-paid` — body: `{ orderID }` marks an order as `paid` (testing only).

To simulate a payment for an order created by Unity, run:

```bash
curl -X POST http://localhost:3000/api/mark-paid -H "Content-Type: application/json" -d '{"orderID":"<ORDER_ID>"}'
```

Then Unity's polling endpoint `/api/order-status` will report `paid`.

Revolut webhook (demo):

The demo server exposes `/api/revolut-webhook` which expects a JSON body describing the transaction and an `X-Signature` header computed as `base64(hmac_sha256(body, REVOLUT_WEBHOOK_SECRET))` (falls back to `SERVER_SECRET` if not set).

Example notify Unity after verifying and marking paid:

```bash
BODY='{"orderID":"<ORDER_ID>"}'
SIG=$(echo -n "$BODY" | openssl dgst -sha256 -hmac 'server_demo_secret' -binary | base64)
curl -X POST http://localhost:3000/api/revolut-webhook -H "Content-Type: application/json" -H "X-Signature: $SIG" -d "$BODY"
```

Manager token rotation endpoints:

GET `/api/manager/tokens` — returns `{ tokens: [{ managerId, token }, ...] }`.

POST `/api/manager/update-token` — requires header `X-Server-Secret: <SERVER_SECRET>` and body `{ managerId, token }` to update a manager's Snapchat API token.

Example update token:

```bash
curl -X POST http://localhost:3000/api/manager/update-token -H "Content-Type: application/json" -H "X-Server-Secret: server_demo_secret" -d '{"managerId":"sydney_mgr","token":"NEW_TOKEN"}'
```

Unity clients can fetch tokens via `/api/manager/tokens` and populate manager credentials at runtime. For production, store tokens in a secure KMS-backed secret store and authenticate clients before returning tokens.

Security

- These demos keep secrets in code/environment and are NOT secure. For production, use:
  - TLS/HTTPS
  - Server-side key management (HSM/KMS)
  - Authenticated client sessions
  - Signed JWTs or public-key signatures, not shared HMAC keys embedded in clients.

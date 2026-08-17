from flask import Flask, request, jsonify
import hmac
import hashlib
import os

app = Flask(__name__)

SERVER_SECRET = os.environ.get('SERVER_SECRET', 'server_demo_secret')
CLIENT_SECRET = os.environ.get('CLIENT_SECRET', 'local_demo_secret')

def compute_hmac(payload: str, key: str) -> str:
    return hmac.new(key.encode('utf-8'), payload.encode('utf-8'), hashlib.sha256).digest().hex()

@app.route('/api/sign', methods=['POST'])
def sign():
    body = request.get_json(force=True)
    payload = body.get('payload')
    client_signature = body.get('clientSignature')
    if not payload:
        return jsonify({'error': 'missing payload'}), 400

    client_valid = False
    try:
        expected = compute_hmac(payload, CLIENT_SECRET)
        client_valid = (expected == client_signature)
    except Exception:
        client_valid = False

    server_sig = compute_hmac(payload, SERVER_SECRET)
    return jsonify({'serverSignature': server_sig, 'clientValid': client_valid})

@app.route('/api/verify', methods=['POST'])
def verify():
    body = request.get_json(force=True)
    payload = body.get('payload')
    server_signature = body.get('serverSignature')
    if not payload or not server_signature:
        return jsonify({'error': 'missing fields'}), 400
    expected = compute_hmac(payload, SERVER_SECRET)
    return jsonify({'valid': expected == server_signature})

if __name__ == '__main__':
    app.run(port=int(os.environ.get('PORT', 5000)), debug=True)

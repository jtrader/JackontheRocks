using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace JackOnTheRocks
{
    /// <summary>
    /// Simple local persistence manager that saves signed JSON to disk and simulates submitting receipts to a server stub.
    /// This is a demo stub — replace server calls with real HTTPS endpoints and proper key management for production.
    /// </summary>
    public class SaveManager : MonoBehaviour
    {
        [Header("Persistence")]
        [SerializeField] private string localSecret = "local_demo_secret"; // demo only
        [Tooltip("Optional: set to http://localhost:3000 or http://localhost:5000 to use remote server demo")]
        [SerializeField] private string serverUrl = "";

        private string SaveFilePath => Path.Combine(Application.persistentDataPath, "save_signed.json");
        private string SaveTokenPath => Path.Combine(Application.persistentDataPath, "save_token.txt");

        [Serializable]
        public class SaveData
        {
            public int rocks;
            public int diamonds;
            public int currentBet;
            public long timestamp;
        }

        [Serializable]
        public class SignedContainer
        {
            public SaveData data;
            public string signature;
        }

        /// <summary>
        /// Save the given data to disk with a local HMAC signature.
        /// </summary>
        public void SaveToDisk(SaveData data)
        {
            data.timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var container = new SignedContainer { data = data };
            string json = JsonUtility.ToJson(data);
            container.signature = ComputeHMAC(json, localSecret);
            string outJson = JsonUtility.ToJson(container);
            File.WriteAllText(SaveFilePath, outJson, Encoding.UTF8);
            Debug.Log("Saved signed state to " + SaveFilePath);
        }

        /// <summary>
        /// Save data and include the server JWT token as the signature field.
        /// </summary>
        private void SaveToDiskWithServerToken(SaveData data, string serverToken)
        {
            data.timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var container = new SignedContainer { data = data, signature = serverToken };
            string outJson = JsonUtility.ToJson(container);
            File.WriteAllText(SaveFilePath, outJson, Encoding.UTF8);
            Debug.Log("Saved server-signed state to " + SaveFilePath);
        }

        /// <summary>
        /// Load saved data from disk and verify local signature. Returns null if invalid or missing.
        /// </summary>
        public SaveData LoadFromDisk()
        {
            if (!File.Exists(SaveFilePath)) return null;
            try
            {
                string text = File.ReadAllText(SaveFilePath, Encoding.UTF8);
                var container = JsonUtility.FromJson<SignedContainer>(text);
                if (container == null || container.data == null) return null;
                // If signature looks like a JWT (has two dots), verify via server if configured
                if (!string.IsNullOrEmpty(container.signature) && container.signature.Split('.').Length == 3)
                {
                    if (string.IsNullOrEmpty(serverUrl))
                    {
                        Debug.LogWarning("Found JWT save but no serverUrl configured — cannot verify token.");
                        return null;
                    }

                    // Call server /api/jwt-verify
                    try
                    {
                        var url = serverUrl.TrimEnd('/') + "/api/jwt-verify";
                        var payload = JsonUtility.ToJson(new { token = container.signature });
                        using (var uwr = UnityWebRequest.PostWwwForm(url, ""))
                        {
                            byte[] bodyRaw = Encoding.UTF8.GetBytes(payload);
                            uwr.uploadHandler = new UploadHandlerRaw(bodyRaw);
                            uwr.downloadHandler = new DownloadHandlerBuffer();
                            uwr.SetRequestHeader("Content-Type", "application/json");
                            var operation = uwr.SendWebRequest();
                            while (!operation.isDone) { } // synchronous wait (small quick call)
                            if (uwr.result != UnityWebRequest.Result.Success)
                            {
                                Debug.LogWarning($"JWT verify failed: {uwr.error}");
                                return null;
                            }
                            var resp = uwr.downloadHandler.text;
                            JwtVerifyResponse verify = null;
                            try { verify = JsonUtility.FromJson<JwtVerifyResponse>(resp); } catch { }
                            if (verify == null || !verify.valid)
                            {
                                Debug.LogWarning("Server reports JWT invalid");
                                return null;
                            }
                            return container.data;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("Failed to verify JWT: " + ex.Message);
                        return null;
                    }
                }

                // Fallback to local HMAC check
                string json = JsonUtility.ToJson(container.data);
                string check = ComputeHMAC(json, localSecret);
                if (check != container.signature)
                {
                    Debug.LogWarning("Local save signature mismatch — possible tampering.");
                    return null;
                }
                return container.data;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Failed to load save: " + ex.Message);
                return null;
            }
        }

        [Serializable]
        private class JwtVerifyResponse
        {
            public bool valid;
            public string error;
        }

        /// <summary>
        /// Create a receipt from current manager state and submit to server stub asynchronously.
        /// Callback receives (success, serverSignature).
        /// </summary>
        public IEnumerator SubmitReceiptToServerCoroutine(SaveData data, Action<bool, string> callback)
        {
            // Create local signature as proof-of-origin
            string payload = JsonUtility.ToJson(data);
            string localSig = ComputeHMAC(payload, localSecret);

            // If a server URL is configured, request a JWT from /api/jwt-sign and store it with the save
            if (!string.IsNullOrEmpty(serverUrl))
            {
                var jwtReq = new JwtSignRequest { payload = payload, expiresIn = "1h" };
                string reqJson = JsonUtility.ToJson(jwtReq);
                var url = serverUrl.TrimEnd('/') + "/api/jwt-sign";
                using (var uwr = new UnityWebRequest(url, "POST"))
                {
                    byte[] bodyRaw = Encoding.UTF8.GetBytes(reqJson);
                    uwr.uploadHandler = new UploadHandlerRaw(bodyRaw);
                    uwr.downloadHandler = new DownloadHandlerBuffer();
                    uwr.SetRequestHeader("Content-Type", "application/json");

                    yield return uwr.SendWebRequest();

                    if (uwr.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogWarning($"JWT request failed: {uwr.error}");
                        callback?.Invoke(false, null);
                        yield break;
                    }

                    string resp = uwr.downloadHandler.text;
                    JwtSignResponse signResp = null;
                    try { signResp = JsonUtility.FromJson<JwtSignResponse>(resp); } catch { }
                    if (signResp == null || string.IsNullOrEmpty(signResp.token))
                    {
                        Debug.LogWarning("Invalid JWT response from server");
                        callback?.Invoke(false, null);
                        yield break;
                    }

                    // Save data with server token and write token file
                    SaveToDiskWithServerToken(data, signResp.token);
                    try
                    {
                        File.WriteAllText(SaveTokenPath, signResp.token, Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("Failed to write token file: " + ex.Message);
                    }
                    onServerTokenUpdated?.Invoke(signResp.token);
                    callback?.Invoke(true, signResp.token);
                    yield break;
                }
            }

            // Fallback: simulate network latency and use local ServerStub
            yield return new WaitForSeconds(0.5f);
            string serverSig = ServerStub.ReceiveAndSign(payload, localSig);
            bool verifiedFallback = ServerStub.VerifySignature(payload, serverSig);
            callback?.Invoke(verifiedFallback, serverSig);
        }

        [Serializable]
        private class SignRequest
        {
            public string payload;
            public string clientSignature;
        }

        [Serializable]
        private class SignResponse
        {
            public string serverSignature;
            public bool clientValid;
        }

        [Serializable]
        private class JwtSignRequest
        {
            public string payload;
            public string expiresIn;
        }

        [Serializable]
        private class JwtSignResponse
        {
            public string token;
        }

        // Event fired when server token is stored/updated
        public event Action<string> onServerTokenUpdated;

        public string GetStoredServerToken()
        {
            try
            {
                if (File.Exists(SaveTokenPath)) return File.ReadAllText(SaveTokenPath, Encoding.UTF8);
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Apply a server-issued receipt (JWT) along with the original payload JSON.
        /// This method stores the token, saves the data locally, and applies it to the manager.
        /// </summary>
        public void ApplyServerReceipt(string token, string payloadJson)
        {
            if (string.IsNullOrEmpty(token)) return;

            // Try to parse payload JSON into SaveData
            SaveData data = null;
            try
            {
                if (!string.IsNullOrEmpty(payloadJson))
                {
                    data = JsonUtility.FromJson<SaveData>(payloadJson);
                }
            }
            catch { data = null; }

            // If no data parsed, create a minimal SaveData with timestamp
            if (data == null) data = new SaveData { rocks = 0, diamonds = 0, currentBet = 0, timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };

            // Save to disk with token
            SaveToDiskWithServerToken(data, token);
            try
            {
                File.WriteAllText(SaveTokenPath, token, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Failed to write token file: " + ex.Message);
            }

            onServerTokenUpdated?.Invoke(token);

            // Apply into running manager
            ApplySaveDataToManager(data);
        }

        /// <summary>
        /// Apply a loaded SaveData into the running manager instance.
        /// </summary>
        public void ApplySaveDataToManager(SaveData data)
        {
            if (data == null || JackOnTheRocksManager.Instance == null) return;
            // Apply via manager API
            JackOnTheRocksManager.Instance.SetBalances(data.rocks, data.diamonds, data.currentBet);
            Debug.Log("Applied save data to manager");
        }

        private static string ComputeHMAC(string message, string key)
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key)))
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                var hash = hmac.ComputeHash(bytes);
                return Convert.ToBase64String(hash);
            }
        }
    }
}

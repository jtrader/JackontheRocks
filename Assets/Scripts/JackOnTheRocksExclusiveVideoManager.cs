using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Video;

namespace JackOnTheRocks
{
    [Serializable]
    public class PlayerPurchaseRecord
    {
        public string userSnapchatId;
        public int totalDrinksPurchased = 0;
        public float totalSpentUSD = 0f;
        public System.DateTime lastPurchaseTimestamp = System.DateTime.MinValue;
        public List<string> unlockedExclusiveVideoIds = new List<string>();
    }

    [Serializable]
    class PlayerPurchaseRecordCollection
    {
        public List<PlayerPurchaseRecord> records = new List<PlayerPurchaseRecord>();
    }

    public class JackOnTheRocksExclusiveVideoManager : MonoBehaviour
    {
        public static JackOnTheRocksExclusiveVideoManager Instance { get; private set; }

        [Header("Library")]
        public List<VideoCreative> masterLibrary = new List<VideoCreative>();

        [Header("Persistence")]
        [SerializeField]
        private string playerPrefsKey = "JOR_ExclusiveVideoRecords";
        [SerializeField]
        private bool useServerSyncIfAvailable = true;

        [Header("Admin")]
        [SerializeField]
        private bool adminBypassLocks = false;

        // Events for UI
        public Action<VideoCreative, bool> onExclusiveVideoSelected;
        public Action<VideoCreative> onPaywallOverlayTriggered;
        public Action<List<VideoCreative>> onExclusiveContentUnlocked;
        public Action<PlayerPurchaseRecord> onPurchaseRecordUpdated;

        // in-memory player records
        private Dictionary<string, PlayerPurchaseRecord> playerRecords = new Dictionary<string, PlayerPurchaseRecord>();

        // currently selected video
        private VideoCreative selectedCreative = null;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            LoadRecords();
        }

        #region Persistence (encrypted PlayerPrefs)
        private void LoadRecords()
        {
            try
            {
                if (!PlayerPrefs.HasKey(playerPrefsKey)) return;
                var cipher = PlayerPrefs.GetString(playerPrefsKey);
                if (string.IsNullOrEmpty(cipher)) return;
                var json = DecryptString(cipher);
                var coll = JsonUtility.FromJson<PlayerPurchaseRecordCollection>(json);
                playerRecords.Clear();
                if (coll != null && coll.records != null)
                {
                    foreach (var r in coll.records) playerRecords[r.userSnapchatId] = r;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Failed to load purchase records: " + ex.Message);
            }
        }

        private void SaveRecords()
        {
            try
            {
                var coll = new PlayerPurchaseRecordCollection();
                coll.records = new List<PlayerPurchaseRecord>(playerRecords.Values);
                var json = JsonUtility.ToJson(coll);
                var cipher = EncryptString(json);
                PlayerPrefs.SetString(playerPrefsKey, cipher);
                PlayerPrefs.Save();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Failed to save purchase records: " + ex.Message);
            }
        }

        private byte[] GetAesKey()
        {
            // Derive a per-install key from product + identifier — note: for high-security use a server-backed key or platform keystore
            var seed = Application.identifier + "|" + Application.productName + "|JackOnTheRocksExclusiveV1";
            using (var sha = SHA256.Create()) return sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
        }

        private string EncryptString(string plain)
        {
            var key = GetAesKey();
            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.GenerateIV();
                var iv = aes.IV;
                using (var encryptor = aes.CreateEncryptor())
                {
                    var plainBytes = Encoding.UTF8.GetBytes(plain);
                    var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                    var combined = new byte[iv.Length + cipherBytes.Length];
                    Buffer.BlockCopy(iv, 0, combined, 0, iv.Length);
                    Buffer.BlockCopy(cipherBytes, 0, combined, iv.Length, cipherBytes.Length);
                    return Convert.ToBase64String(combined);
                }
            }
        }

        private string DecryptString(string base64)
        {
            var data = Convert.FromBase64String(base64);
            var key = GetAesKey();
            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                var iv = new byte[16];
                Buffer.BlockCopy(data, 0, iv, 0, 16);
                var cipherBytes = new byte[data.Length - 16];
                Buffer.BlockCopy(data, 16, cipherBytes, 0, cipherBytes.Length);
                aes.IV = iv;
                using (var decryptor = aes.CreateDecryptor())
                {
                    var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                    return Encoding.UTF8.GetString(plainBytes);
                }
            }
        }
        #endregion

        #region Core Logic
        public bool CanPlayerWatchVideo(VideoCreative creative, PlayerPurchaseRecord playerRecord)
        {
            if (creative == null) return false;
            if (!creative.isExclusiveToPurchasers) return true;
            if (adminBypassLocks) return true;
            if (playerRecord == null) return false;
            if (playerRecord.unlockedExclusiveVideoIds != null && playerRecord.unlockedExclusiveVideoIds.Contains(creative.creativeId)) return true;
            return playerRecord.totalDrinksPurchased >= creative.requiredDrinkPurchases;
        }

        public void OnPaymentConfirmed(string userSnapchatId, int drinksCount, float amountUSD = 0f)
        {
            if (string.IsNullOrEmpty(userSnapchatId)) return;
            if (!playerRecords.ContainsKey(userSnapchatId)) playerRecords[userSnapchatId] = new PlayerPurchaseRecord { userSnapchatId = userSnapchatId };
            var rec = playerRecords[userSnapchatId];
            rec.totalDrinksPurchased += drinksCount;
            rec.totalSpentUSD += amountUSD;
            rec.lastPurchaseTimestamp = DateTime.UtcNow;

            var newlyUnlocked = new List<VideoCreative>();
            foreach (var c in masterLibrary)
            {
                if (!c.isExclusiveToPurchasers) continue;
                if (rec.unlockedExclusiveVideoIds.Contains(c.creativeId)) continue;
                if (rec.totalDrinksPurchased >= c.requiredDrinkPurchases)
                {
                    rec.unlockedExclusiveVideoIds.Add(c.creativeId);
                    newlyUnlocked.Add(c);
                }
            }

            playerRecords[userSnapchatId] = rec;
            SaveRecords();
            onPurchaseRecordUpdated?.Invoke(rec);
            if (newlyUnlocked.Count > 0)
            {
                onExclusiveContentUnlocked?.Invoke(newlyUnlocked);
            }
        }

        public void PlayVideoRequested(VideoCreative creative, VideoPlayer targetPlayer = null)
        {
            if (creative == null) return;
            // For selection UI
            var rec = GetCurrentPlayerRecord();
            bool unlocked = CanPlayerWatchVideo(creative, rec);
            onExclusiveVideoSelected?.Invoke(creative, unlocked);
            if (!unlocked)
            {
                onPaywallOverlayTriggered?.Invoke(creative);
                return;
            }

            // play via provided VideoPlayer or create a temp one
            VideoPlayer vp = targetPlayer;
            bool created = false;
            if (vp == null)
            {
                var go = new GameObject("ExclusiveVideoPlayer");
                DontDestroyOnLoad(go);
                vp = go.AddComponent<VideoPlayer>();
                created = true;
            }

            try
            {
                vp.playOnAwake = false;
                vp.source = VideoSource.Url;
                vp.url = creative.videoUrl;
                vp.isLooping = false;
                vp.audioOutputMode = VideoAudioOutputMode.Direct;
                vp.Prepare();
                vp.prepareCompleted += (source) => { vp.Play(); };
                vp.errorReceived += (source, message) => { Debug.LogWarning("VideoPlayer error: " + message); };
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Failed to play video: " + ex.Message);
                if (created && vp != null) Destroy(vp.gameObject);
            }
        }

        public void OnSelectVideoFromGallery(string creativeId)
        {
            var creative = masterLibrary.Find(c => c.creativeId == creativeId);
            if (creative == null) return;
            selectedCreative = creative;
            var rec = GetCurrentPlayerRecord();
            bool unlocked = CanPlayerWatchVideo(creative, rec);
            onExclusiveVideoSelected?.Invoke(creative, unlocked);
        }

        public void OnBuyDrinkToUnlockClicked()
        {
            // UI should subscribe to onPaywallOverlayTriggered and trigger the purchase flow.
            // Here we simply log; in production this should call your payment flow (Revolut PayID) and upon confirmation call OnPaymentConfirmed.
            Debug.Log("User requested to buy drink to unlock exclusive content. Trigger your Revolut/PayID flow and call OnPaymentConfirmed on success.");
        }

        public void OnAdminToggleExclusivity(string creativeId, bool makeExclusive)
        {
            var creative = masterLibrary.Find(c => c.creativeId == creativeId);
            if (creative == null) return;
            creative.isExclusiveToPurchasers = makeExclusive;
            // Persist change locally to masterLibrary if using remote manifest sync you'd update server
            // For now simply notify
        }

        private PlayerPurchaseRecord GetCurrentPlayerRecord()
        {
            // Integration point: your game should set the current player's Snapchat ID somewhere accessible.
            // For demo, attempt to read from PlayerPrefs 'JOR_CurrentSnapId'
            var snapId = PlayerPrefs.GetString("JOR_CurrentSnapId", "");
            if (string.IsNullOrEmpty(snapId)) return null;
            if (!playerRecords.ContainsKey(snapId)) playerRecords[snapId] = new PlayerPurchaseRecord { userSnapchatId = snapId };
            return playerRecords[snapId];
        }

        // Helper to allow server sync of purchase record (optional)
        public void SyncRecordToServer(PlayerPurchaseRecord record, string serverUrl)
        {
            // Best-effort: send record to server for authoritative persistence. Implement per your backend API.
            if (string.IsNullOrEmpty(serverUrl) || record == null) return;
            try
            {
                var json = JsonUtility.ToJson(record);
                var uwr = new UnityEngine.Networking.UnityWebRequest(serverUrl, "POST");
                var body = Encoding.UTF8.GetBytes(json);
                uwr.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(body);
                uwr.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                uwr.SetRequestHeader("Content-Type", "application/json");
                var op = uwr.SendWebRequest();
                op.completed += (asyncOp) => {
                    if (uwr.result != UnityEngine.Networking.UnityWebRequest.Result.Success) Debug.LogWarning("SyncRecordToServer failed: " + uwr.error);
                    else Debug.Log("Purchase record synced to server");
                };
            }
            catch (Exception ex) { Debug.LogWarning("SyncRecordToServer error: " + ex.Message); }
        }

        // Coroutine: fetch creative manifest from server and populate masterLibrary
        public System.Collections.IEnumerator FetchManifestCoroutine(string manifestUrl)
        {
            if (string.IsNullOrEmpty(manifestUrl)) yield break;
            using (var uwr = UnityEngine.Networking.UnityWebRequest.Get(manifestUrl))
            {
                uwr.SetRequestHeader("Accept", "application/json");
                var op = uwr.SendWebRequest();
                while (!op.isDone) yield return null;
                if (uwr.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning("Failed to fetch manifest: " + uwr.error);
                    yield break;
                }
                var txt = uwr.downloadHandler.text;
                try
                {
                    var wrapper = JsonUtility.FromJson<CreativeManifestWrapper>(txt);
                    if (wrapper != null && wrapper.creatives != null)
                    {
                        masterLibrary = wrapper.creatives;
                    }
                    else
                    {
                        // fallback to manual parse
                        Debug.Log("Manifest parsed but empty");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("Failed parsing manifest JSON: " + ex.Message);
                }
            }
        }

        // Coroutine: ask server for authoritative confirmation for an orderID, then apply as a purchase
        public System.Collections.IEnumerator FetchPurchaseConfirmationCoroutine(string serverConfirmUrl, string orderID, string userSnapchatId = null)
        {
            if (string.IsNullOrEmpty(serverConfirmUrl) || string.IsNullOrEmpty(orderID)) yield break;
            var body = JsonUtility.ToJson(new { orderID = orderID });
            var uwr = new UnityEngine.Networking.UnityWebRequest(serverConfirmUrl, "POST");
            var bytes = Encoding.UTF8.GetBytes(body);
            uwr.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(bytes);
            uwr.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
            uwr.SetRequestHeader("Content-Type", "application/json");
            var op = uwr.SendWebRequest();
            while (!op.isDone) yield return null;
            if (uwr.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("FetchPurchaseConfirmation failed: " + uwr.error);
                yield break;
            }
            try
            {
                var txt = uwr.downloadHandler.text;
                var resp = JsonUtility.FromJson<ConfirmPurchaseResponse>(txt);
                if (resp != null && resp.ok && resp.payload != null && resp.payload.status == "paid")
                {
                    // For now assume 1 drink per order; you can extend server to include drinksCount
                    int drinks = 1;
                    float amount = resp.payload.amount;
                    OnPaymentConfirmed(userSnapchatId ?? PlayerPrefs.GetString("JOR_CurrentSnapId", ""), drinks, amount);
                }
            }
            catch (Exception ex) { Debug.LogWarning("Failed parse confirm response: " + ex.Message); }
        }

        [Serializable]
        private class CreativeManifestWrapper { public List<VideoCreative> creatives; }

        [Serializable]
        private class ConfirmPurchaseResponse { public bool ok; public string orderID; public string token; public ConfirmPayload payload; }
        [Serializable]
        private class ConfirmPayload { public string orderID; public float amount; public string status; }

        #endregion
    }
}

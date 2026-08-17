using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.Video;

namespace JackOnTheRocks
{
    [Serializable]
    public class VideoCreative
    {
        public string creativeId;
        public string waiterName;
        public int durationSeconds; // 5,10,15
        public string videoUrl; // CDN MP4
        public JackOnTheRocksPaymentManager.DrinkType targetDrink;
        public int totalImpressions;
        public int totalClicks;
        public int completedPurchases;
    }

    [Serializable]
    public struct CreativeStats
    {
        public float clickThroughRate; // CTR = clicks / impressions * 100
        public float conversionRate; // CVR = purchases / impressions * 100
        public float totalRevenueGenerated;
    }

    [Serializable]
    internal class VideoCreativeList { public List<VideoCreative> creatives = new List<VideoCreative>(); }

    /// <summary>
    /// Singleton that manages a remote video creative library, streams short waiter prompts,
    /// tracks impressions/clicks/conversions, and syncs analytics with a backend.
    /// Designed for WebGL (streaming URLs) with fallback to static avatars.
    /// </summary>
    public class JackOnTheRocksCreativeManager : MonoBehaviour
    {
        public static JackOnTheRocksCreativeManager Instance { get; private set; }

        [Header("Creative Library")]
        [Tooltip("Backend endpoint to fetch creative manifest JSON (returns { creatives: [...] })")]
        public string creativeManifestUrl = "";

        [Tooltip("Backend endpoint to upload local analytics data")]
        public string analyticsUploadUrl = "";

        [Header("Playback UI")]
        [Tooltip("RawImage that will host the video render texture overlay; created at runtime if null")]
        public RawImage videoOverlay;

        [Tooltip("Fallback static sprite used if video stream fails or WebGL can't play")]
        public Sprite fallbackAvatarSprite;

        [Tooltip("Mute video by default to satisfy autoplay policies; user unmute required for audio")]
        public bool muteByDefault = true;

        [Header("Local storage")]
        [Tooltip("Filename under Application.persistentDataPath to persist creatives and counters")]
        public string persistFileName = "creative_library.json";

        // Events for UI
        public event Action<VideoCreative> onVideoPlaybackStarted;
        public event Action<string> onVideoPlaybackEnded;
        public event Action<string, float> onCreativeConverted; // creativeId, revenue
        public event Action<List<VideoCreative>> onLibraryAnalyticsUpdated;

        private Dictionary<string, VideoCreative> creatives = new Dictionary<string, VideoCreative>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, float> creativeRevenue = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        // Runtime playback
        private VideoPlayer videoPlayer;
        private RenderTexture activeRenderTexture;
        private string currentCreativeId = null;
        private double currentCreativeEndTimeUtc = 0;

        // Click window (seconds after video ends) to attribute clicks to creative
        private const float clickAttributionWindow = 5f;

        private string persistPath => Path.Combine(Application.persistentDataPath, persistFileName);

        // Expose read-only active creative id for attribution
        public string ActiveCreativeId => currentCreativeId;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            LoadLibraryFromDisk();
        }

        private void OnDestroy()
        {
            if (videoPlayer != null)
            {
                videoPlayer.errorReceived -= OnVideoError;
                videoPlayer.loopPointReached -= OnVideoEnded;
            }
            if (activeRenderTexture != null) Destroy(activeRenderTexture);
        }

        #region Playback API

        /// <summary>
        /// Play a waiter offer video (streams via URL). onVideoFinished invoked when playback completes or user interaction ends it.
        /// </summary>
        public void PlayWaiterOfferVideo(VideoCreative creative, Action onVideoFinished)
        {
            if (creative == null) { Debug.LogWarning("PlayWaiterOfferVideo: creative null"); onVideoFinished?.Invoke(); return; }
            StartCoroutine(PlayWaiterOfferVideoCoroutine(creative, onVideoFinished));
        }

        private IEnumerator PlayWaiterOfferVideoCoroutine(VideoCreative creative, Action onVideoFinished)
        {
            // Safety checks
            if (string.IsNullOrEmpty(creative.videoUrl)) { Debug.LogWarning("Creative has no videoUrl"); ShowFallbackAvatar(); onVideoFinished?.Invoke(); yield break; }

            PrepareVideoPlayerIfNeeded();

            // increment impression
            creative.totalImpressions = creative.totalImpressions + 1;
            creatives[creative.creativeId] = creative;
            SaveLibraryToDisk();

            // Setup render target
            if (activeRenderTexture != null) Destroy(activeRenderTexture);
            activeRenderTexture = new RenderTexture(1024, 576, 0);
            videoPlayer.targetTexture = activeRenderTexture;
            if (videoOverlay != null) videoOverlay.texture = activeRenderTexture;

            videoPlayer.source = VideoSource.Url;
            videoPlayer.url = creative.videoUrl;
            videoPlayer.isLooping = false;
            videoPlayer.playOnAwake = false;
            videoPlayer.Prepare();

            // wait for prepare with timeout
            float wait = 0f; float timeout = 10f;
            while (!videoPlayer.isPrepared && wait < timeout)
            {
                wait += 0.1f; yield return new WaitForSeconds(0.1f);
            }

            if (!videoPlayer.isPrepared)
            {
                Debug.LogWarning("Video not prepared or timed out: " + creative.videoUrl);
                ShowFallbackAvatar();
                onVideoFinished?.Invoke();
                yield break;
            }

            // Mute by default for autoplay compatibility
            try
            {
                if (muteByDefault) videoPlayer.SetDirectAudioMute(0, true);
            }
            catch { /* platform may not support audio */ }

            currentCreativeId = creative.creativeId;
            currentCreativeEndTimeUtc = DateTime.UtcNow.AddSeconds(creative.durationSeconds).ToUniversalTime().Ticks;

            videoPlayer.Play();
            onVideoPlaybackStarted?.Invoke(creative);

            // Wait until playback finishes or user interrupts
            bool finished = false;
            float elapsed = 0f;
            while (videoPlayer.isPlaying)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            // Allow a small buffer for attribution window
            currentCreativeEndTimeUtc = (DateTime.UtcNow + TimeSpan.FromSeconds(clickAttributionWindow)).ToUniversalTime().Ticks;

            // Inform finished
            onVideoPlaybackEnded?.Invoke(creative.creativeId);

            // cleanup
            videoPlayer.targetTexture = null;
            if (videoOverlay != null) videoOverlay.texture = null;
            currentCreativeId = null;

            SaveLibraryToDisk();
            onVideoFinished?.Invoke();
        }

        private void PrepareVideoPlayerIfNeeded()
        {
            if (videoPlayer == null)
            {
                var go = new GameObject("CreativeVideoPlayer");
                go.transform.SetParent(transform, false);
                videoPlayer = go.AddComponent<VideoPlayer>();
                videoPlayer.playOnAwake = false;
                videoPlayer.renderMode = VideoRenderMode.RenderTexture;
                videoPlayer.audioOutputMode = VideoAudioOutputMode.Direct;
                videoPlayer.errorReceived += OnVideoError;
                videoPlayer.loopPointReached += OnVideoEnded;
            }
        }

        private void OnVideoError(VideoPlayer source, string message)
        {
            Debug.LogWarning("VideoPlayer error: " + message);
            ShowFallbackAvatar();
        }

        private void OnVideoEnded(VideoPlayer source)
        {
            // handled by coroutine loop; ensure event fired
            Debug.Log("Video ended");
        }

        private void ShowFallbackAvatar()
        {
            if (videoOverlay != null && fallbackAvatarSprite != null) videoOverlay.texture = fallbackAvatarSprite.texture;
        }

        #endregion

        #region Tracking API

        public void TrackCreativeClick(string creativeId)
        {
            if (string.IsNullOrEmpty(creativeId)) return;
            if (!creatives.ContainsKey(creativeId)) { Debug.LogWarning("TrackCreativeClick: unknown creative " + creativeId); return; }
            creatives[creativeId].totalClicks++;
            SaveLibraryToDisk();
        }

        public void TrackCreativeConversion(string creativeId, float purchaseAmountUSD)
        {
            if (string.IsNullOrEmpty(creativeId)) return;
            if (!creatives.ContainsKey(creativeId)) { Debug.LogWarning("TrackCreativeConversion: unknown creative " + creativeId); return; }
            creatives[creativeId].completedPurchases++;
            if (!creativeRevenue.ContainsKey(creativeId)) creativeRevenue[creativeId] = 0f;
            creativeRevenue[creativeId] += purchaseAmountUSD;
            SaveLibraryToDisk();
            onCreativeConverted?.Invoke(creativeId, purchaseAmountUSD);
        }

        public List<VideoCreative> GetTopPerformingCreatives(int topCount)
        {
            var list = creatives.Values.ToList();
            var ranked = list.OrderByDescending(c => ComputeConversionScore(c)).Take(topCount).ToList();
            onLibraryAnalyticsUpdated?.Invoke(ranked);
            return ranked;
        }

        private float ComputeConversionScore(VideoCreative c)
        {
            // Use CVR primarily, fallback to CTR. CVR = purchases/impressions
            if (c.totalImpressions <= 0) return 0f;
            float cvr = (float)c.completedPurchases / c.totalImpressions;
            float ctr = (float)c.totalClicks / c.totalImpressions;
            // weighted score: 70% CVR, 30% CTR
            return cvr * 0.7f + ctr * 0.3f;
        }

        public CreativeStats ComputeStats(VideoCreative c)
        {
            var stats = new CreativeStats();
            if (c.totalImpressions <= 0)
            {
                stats.clickThroughRate = 0f; stats.conversionRate = 0f; stats.totalRevenueGenerated = creativeRevenue.ContainsKey(c.creativeId) ? creativeRevenue[c.creativeId] : 0f; return stats;
            }
            stats.clickThroughRate = (float)c.totalClicks / c.totalImpressions * 100f;
            stats.conversionRate = (float)c.completedPurchases / c.totalImpressions * 100f;
            stats.totalRevenueGenerated = creativeRevenue.ContainsKey(c.creativeId) ? creativeRevenue[c.creativeId] : 0f;
            return stats;
        }

        #endregion

        #region Sync & Persistence

        public IEnumerator SyncCreativeLibrary()
        {
            if (string.IsNullOrEmpty(creativeManifestUrl)) { Debug.LogWarning("SyncCreativeLibrary: no manifest URL configured"); yield break; }
            using (var uwr = UnityWebRequest.Get(creativeManifestUrl))
            {
                uwr.SetRequestHeader("Accept", "application/json");
                yield return uwr.SendWebRequest();
                if (uwr.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning("SyncCreativeLibrary failed: " + uwr.error);
                    yield break;
                }
                try
                {
                    var txt = uwr.downloadHandler.text;
                    var wrapper = JsonUtility.FromJson<VideoCreativeList>(txt);
                    if (wrapper?.creatives != null)
                    {
                        foreach (var c in wrapper.creatives)
                        {
                            if (string.IsNullOrEmpty(c.creativeId)) continue;
                            if (!creatives.ContainsKey(c.creativeId)) creatives[c.creativeId] = c;
                            else
                            {
                                // update metadata (url, duration, name, target drink) but preserve counters
                                var existing = creatives[c.creativeId];
                                existing.videoUrl = c.videoUrl ?? existing.videoUrl;
                                existing.waiterName = c.waiterName ?? existing.waiterName;
                                existing.durationSeconds = c.durationSeconds > 0 ? c.durationSeconds : existing.durationSeconds;
                                existing.targetDrink = c.targetDrink;
                                creatives[c.creativeId] = existing;
                            }
                        }
                        SaveLibraryToDisk();
                        onLibraryAnalyticsUpdated?.Invoke(creatives.Values.ToList());
                    }
                }
                catch (Exception ex) { Debug.LogWarning("Failed to parse creative manifest: " + ex.Message); }
            }
        }

        public IEnumerator UploadCreativeAnalytics()
        {
            if (string.IsNullOrEmpty(analyticsUploadUrl)) { Debug.LogWarning("UploadCreativeAnalytics: no upload URL configured"); yield break; }
            var wrapper = new VideoCreativeList() { creatives = creatives.Values.ToList() };
            var json = JsonUtility.ToJson(wrapper);
            using (var uwr = new UnityWebRequest(analyticsUploadUrl, "POST"))
            {
                byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
                uwr.uploadHandler = new UploadHandlerRaw(bodyRaw);
                uwr.downloadHandler = new DownloadHandlerBuffer();
                uwr.SetRequestHeader("Content-Type", "application/json");
                yield return uwr.SendWebRequest();
                if (uwr.result != UnityWebRequest.Result.Success) Debug.LogWarning("UploadCreativeAnalytics failed: " + uwr.error);
            }
        }

        private void SaveLibraryToDisk()
        {
            try
            {
                var wrapper = new VideoCreativeList() { creatives = creatives.Values.ToList() };
                var json = JsonUtility.ToJson(wrapper);
                File.WriteAllText(persistPath, json);
            }
            catch (Exception ex) { Debug.LogWarning("Failed to save creative library: " + ex.Message); }
        }

        private void LoadLibraryFromDisk()
        {
            try
            {
                if (!File.Exists(persistPath)) return;
                var raw = File.ReadAllText(persistPath);
                var wrapper = JsonUtility.FromJson<VideoCreativeList>(raw);
                if (wrapper?.creatives == null) return;
                creatives.Clear();
                foreach (var c in wrapper.creatives) creatives[c.creativeId] = c;
            }
            catch (Exception ex) { Debug.LogWarning("Failed to load creative library: " + ex.Message); }
        }

        #endregion

        #region UI Helpers & Bindings

        // Called by UI to watch a video prompt — picks top-performing creative for the target drink
        public void OnWatchVideoPrompt(JackOnTheRocksPaymentManager.DrinkType targetDrink)
        {
            var best = creatives.Values.Where(c => c.targetDrink == targetDrink).OrderByDescending(c => ComputeConversionScore(c)).FirstOrDefault();
            if (best == null) { Debug.LogWarning("No creative found for drink " + targetDrink); return; }
            PlayWaiterOfferVideo(best, () => { /* no-op */ });
        }

        // Called by UI when user accepts the drink offer (Buy Drink button in creative overlay)
        public void OnAcceptDrinkOffer()
        {
            if (string.IsNullOrEmpty(currentCreativeId)) { Debug.Log("No active creative to attribute click"); return; }
            // Allow attribution if within window
            TrackCreativeClick(currentCreativeId);
            // Trigger PayID order flow (best-effort)
            var paymentMgr = JackOnTheRocksPaymentManager.Instance;
            if (paymentMgr != null)
            {
                // create a basic PayID order for target drink if available
                if (creatives.TryGetValue(currentCreativeId, out var c))
                {
                    var order = paymentMgr.CreatePayIDOrder(c.targetDrink, 0);
                    if (order.HasValue)
                    {
                        // record click already done; further conversion will be recorded via TrackCreativeConversion when payment confirmed
                    }
                }
            }
        }

        public void OnDeclineDrinkOffer()
        {
            // simply stop playback and clear overlay
            if (videoPlayer != null && videoPlayer.isPlaying) videoPlayer.Stop();
            if (videoOverlay != null) videoOverlay.texture = null;
            currentCreativeId = null;
        }

        public void OnForceFetchCreativeStats()
        {
            StartCoroutine(SyncCreativeLibrary());
        }

        #endregion
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

namespace JackOnTheRocks
{
    [Serializable]
    public class WaiterVideoEntry
    {
        public string waiterId;
        public string videoId;
        public string displayName;
        public string fileName;
        public string videoUrl;
        public float durationSeconds;
        public Texture2D thumbnail;
    }

    public class JackOnTheRocksWaiterLibraryManager : MonoBehaviour
    {
        public static JackOnTheRocksWaiterLibraryManager Instance { get; private set; }

        // library: waiterId -> list of videos
        private Dictionary<string, List<WaiterVideoEntry>> library = new Dictionary<string, List<WaiterVideoEntry>>();

        [Header("Runtime Settings")]
        public bool autoScanOnStart = true;
        // relative to Application.streamingAssetsPath
        public string waitersFolder = "Waiters";

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            if (autoScanOnStart)
            {
                StartCoroutine(ScanLibraryCoroutine());
            }
        }

        public IEnumerator ScanLibraryCoroutine()
        {
            library.Clear();
            var basePath = Path.Combine(Application.streamingAssetsPath, waitersFolder);
            if (!Directory.Exists(basePath))
            {
                Debug.Log("Waiter library folder not found: " + basePath);
                yield break;
            }

            var waiterDirs = Directory.GetDirectories(basePath);
            foreach (var dir in waiterDirs)
            {
                var waiterId = Path.GetFileName(dir);
                var files = Directory.GetFiles(dir, "*.mp4");
                var list = new List<WaiterVideoEntry>();
                foreach (var f in files)
                {
                    var entry = new WaiterVideoEntry();
                    entry.waiterId = waiterId;
                    entry.fileName = Path.GetFileName(f);
                    entry.videoId = Path.GetFileNameWithoutExtension(f);
                    entry.displayName = entry.videoId.Replace('_', ' ');
                    entry.videoUrl = GetVideoURL(f);
                    entry.durationSeconds = 0f; // will be filled later if needed
                    list.Add(entry);
                }
                library[waiterId] = list;
            }

            // Also support a flat folder with mp4s named by waiter prefix
            var flatFiles = Directory.GetFiles(basePath, "*.mp4");
            foreach (var f in flatFiles)
            {
                // try to parse waiterId_video.mp4 or WaiterId - name.mp4
                var file = Path.GetFileName(f);
                var parts = file.Split(new[] { '_', '-' }, 2);
                string waiterId = parts.Length > 1 ? parts[0].Trim() : "default";
                if (!library.ContainsKey(waiterId)) library[waiterId] = new List<WaiterVideoEntry>();
                var entry = new WaiterVideoEntry();
                entry.waiterId = waiterId;
                entry.fileName = file;
                entry.videoId = Path.GetFileNameWithoutExtension(file);
                entry.displayName = entry.videoId.Replace('_', ' ');
                entry.videoUrl = GetVideoURL(f);
                entry.durationSeconds = 0f;
                library[waiterId].Add(entry);
            }

            yield return null;
        }

        private string GetVideoURL(string absolutePath)
        {
            if (absolutePath.StartsWith("http://") || absolutePath.StartsWith("https://")) return absolutePath;
            if (Application.platform == RuntimePlatform.WebGLPlayer)
            {
                // streamingAssetsPath is already a URL in WebGL builds
                return absolutePath;
            }
            return "file://" + absolutePath;
        }

        public List<WaiterVideoEntry> GetVideosForWaiter(string waiterId)
        {
            if (string.IsNullOrEmpty(waiterId)) waiterId = "default";
            if (library.TryGetValue(waiterId, out var list)) return list;
            return new List<WaiterVideoEntry>();
        }

        // Play a waiter video into a RawImage UI element. Handles creation of VideoPlayer and RenderTexture.
        public IEnumerator PlayVideoToRawImage(string waiterId, string videoId, RawImage target, bool loop = false, bool mute = true)
        {
            if (target == null) yield break;
            var videos = GetVideosForWaiter(waiterId);
            var entry = videos.Find(v => v.videoId == videoId || v.fileName == videoId || v.displayName == videoId);
            if (entry == null)
            {
                Debug.LogWarning($"Video {videoId} for waiter {waiterId} not found.");
                yield break;
            }

            // Clean up any existing VideoPlayer on target
            var existing = target.GetComponent<VideoPlayer>();
            if (existing != null) { existing.Stop(); Destroy(existing); }

            var vp = target.gameObject.AddComponent<VideoPlayer>();
            var audio = target.gameObject.GetComponent<AudioSource>();
            if (audio == null) audio = target.gameObject.AddComponent<AudioSource>();
            vp.playOnAwake = false;
            vp.source = VideoSource.Url;
            vp.url = entry.videoUrl;
            vp.renderMode = VideoRenderMode.APIOnly;
            vp.audioOutputMode = VideoAudioOutputMode.AudioSource;
            vp.SetTargetAudioSource(0, audio);
            audio.playOnAwake = false;
            audio.mute = mute;

            // create RenderTexture sized to target rect
            int width = Mathf.Max(256, (int)target.rectTransform.rect.width);
            int height = Mathf.Max(144, (int)target.rectTransform.rect.height);
            var rt = new RenderTexture(width, height, 0);
            vp.targetTexture = rt;
            target.texture = rt;

            vp.isLooping = loop;

            vp.Prepare();
            while (!vp.isPrepared)
            {
                yield return null;
            }

            // attempt to populate duration
            try { entry.durationSeconds = (float)vp.length; } catch { }

            vp.Play();
            if (!mute) audio.Play();

            // wait until playback finishes (if not looping)
            while (vp.isPlaying || loop)
            {
                if (!vp.isPlaying && loop) vp.Play();
                if (!loop && !vp.isPlaying) break;
                yield return null;
            }

            // cleanup if not looping
            if (!loop)
            {
                vp.Stop();
                Destroy(vp);
                // keep rendertexture for snapshot; do not destroy it automatically so UI can read it
            }
        }

        public void StopPlaybackOnTarget(RawImage target)
        {
            if (target == null) return;
            var vp = target.GetComponent<VideoPlayer>();
            if (vp != null) { vp.Stop(); Destroy(vp); }
            var audio = target.GetComponent<AudioSource>();
            if (audio != null) { audio.Stop(); }
            // optionally clear texture
            // target.texture = null;
        }

        // Lightweight thumbnail capture (captures first prepared frame)
        public IEnumerator CaptureThumbnail(WaiterVideoEntry entry, Action<Texture2D> onComplete, int width = 320, int height = 180)
        {
            if (entry == null || onComplete == null) yield break;

            var go = new GameObject("_temp_video_capture");
            var vp = go.AddComponent<VideoPlayer>();
            vp.source = VideoSource.Url;
            vp.url = entry.videoUrl;
            vp.renderMode = VideoRenderMode.APIOnly;
            var rt = new RenderTexture(width, height, 0);
            vp.targetTexture = rt;
            vp.Prepare();
            while (!vp.isPrepared) yield return null;
            vp.Play();
            // wait a frame to ensure content
            yield return null;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            vp.Stop();
            Destroy(go);
            entry.thumbnail = tex;
            onComplete(tex);
        }
    }
}

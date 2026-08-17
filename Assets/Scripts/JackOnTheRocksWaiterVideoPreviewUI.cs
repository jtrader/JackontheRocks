using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

namespace JackOnTheRocks
{
    // Simple runtime UI to browse waiter video library and preview videos in a RawImage
    public class JackOnTheRocksWaiterVideoPreviewUI : MonoBehaviour
    {
        [Header("Bindings")]
        public Dropdown waiterDropdown;
        public RectTransform videoListContent; // content under a ScrollRect with VerticalLayoutGroup
        public Button refreshButton;
        public RawImage previewTarget;
        public Button stopButton;
        public GameObject videoButtonPrefab; // simple Button with Text child

        private string selectedWaiterId = string.Empty;
        private List<GameObject> spawnedButtons = new List<GameObject>();

        private void Start()
        {
            if (refreshButton != null) refreshButton.onClick.AddListener(OnRefreshClicked);
            if (stopButton != null) stopButton.onClick.AddListener(OnStopClicked);
            PopulateWaiters();
        }

        public void OnRefreshClicked()
        {
            StartCoroutine(RefreshAndPopulate());
        }

        private IEnumerator RefreshAndPopulate()
        {
            if (JackOnTheRocksWaiterLibraryManager.Instance != null)
            {
                yield return JackOnTheRocksWaiterLibraryManager.Instance.ScanLibraryCoroutine();
                PopulateWaiters();
            }
        }

        private void PopulateWaiters()
        {
            ClearSpawnedButtons();
            if (waiterDropdown == null) return;
            waiterDropdown.options.Clear();

            // collect waiter ids from manager
            var ids = new List<string>();
            var manager = JackOnTheRocksWaiterLibraryManager.Instance;
            if (manager == null) return;

            // Gather keys via reflection of internal library (use public API GetVideosForWaiter for common ids)
            // We'll look for common waiter ids in manifest folder
            var basePath = System.IO.Path.Combine(Application.streamingAssetsPath, manager.waitersFolder);
            if (System.IO.Directory.Exists(basePath))
            {
                var dirs = System.IO.Directory.GetDirectories(basePath);
                foreach (var d in dirs) ids.Add(System.IO.Path.GetFileName(d));
                var flat = System.IO.Directory.GetFiles(basePath, "*.mp4");
                foreach (var f in flat)
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(f);
                    // try parse prefix
                    var parts = name.Split(new[] { '_', '-' }, 2);
                    var waiter = parts.Length > 1 ? parts[0] : "default";
                    if (!ids.Contains(waiter)) ids.Add(waiter);
                }
            }

            if (ids.Count == 0)
            {
                waiterDropdown.options.Add(new Dropdown.OptionData("(no waiters found)"));
                waiterDropdown.value = 0;
                waiterDropdown.onValueChanged.AddListener((i) => { selectedWaiterId = string.Empty; PopulateVideoList(); });
                return;
            }

            foreach (var id in ids) waiterDropdown.options.Add(new Dropdown.OptionData(id));
            waiterDropdown.onValueChanged.RemoveAllListeners();
            waiterDropdown.onValueChanged.AddListener(OnWaiterSelectionChanged);
            waiterDropdown.value = 0;
            selectedWaiterId = waiterDropdown.options[0].text;
            PopulateVideoList();
        }

        private void OnWaiterSelectionChanged(int idx)
        {
            if (waiterDropdown == null) return;
            selectedWaiterId = waiterDropdown.options[idx].text;
            PopulateVideoList();
        }

        private void PopulateVideoList()
        {
            ClearSpawnedButtons();
            if (string.IsNullOrEmpty(selectedWaiterId)) return;
            var videos = JackOnTheRocksWaiterLibraryManager.Instance.GetVideosForWaiter(selectedWaiterId);
            if (videoButtonPrefab == null || videoListContent == null)
            {
                Debug.LogWarning("Video button prefab or content not assigned.");
                return;
            }

            foreach (var v in videos)
            {
                var go = Instantiate(videoButtonPrefab, videoListContent);
                spawnedButtons.Add(go);
                var btn = go.GetComponent<Button>();
                var txt = go.GetComponentInChildren<Text>();
                if (txt != null) txt.text = v.displayName ?? v.videoId;
                if (btn != null)
                {
                    var vid = v.videoId;
                    btn.onClick.AddListener(() => { StartCoroutine(JackOnTheRocksWaiterLibraryManager.Instance.PlayVideoToRawImage(selectedWaiterId, vid, previewTarget, loop:false, mute:true)); });
                }
            }
        }

        private void ClearSpawnedButtons()
        {
            foreach (var g in spawnedButtons) if (g != null) Destroy(g);
            spawnedButtons.Clear();
        }

        private void OnStopClicked()
        {
            JackOnTheRocksWaiterLibraryManager.Instance.StopPlaybackOnTarget(previewTarget);
        }

        private void OnDestroy()
        {
            if (refreshButton != null) refreshButton.onClick.RemoveListener(OnRefreshClicked);
            if (stopButton != null) stopButton.onClick.RemoveListener(OnStopClicked);
        }
    }
}

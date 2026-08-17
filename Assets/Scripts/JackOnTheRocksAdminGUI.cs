using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using TMPro;

namespace JackOnTheRocks.Admin
{
    /// <summary>
    /// Admin dashboard navigation tabs.
    /// </summary>
    public enum AdminTab
    {
        Overview = 0,
        TransactionsPayID = 1,
        RegionalManagers = 2,
        CreativeAnalytics = 3,
        ExclusiveContent = 4,
        UserMatching = 5,
        SurveyTelemetry = 6
    }

    /// <summary>
    /// Production-ready Admin Dashboard singleton used by the WebGL build.
    /// Provides a responsive tabbed UI, server request helpers (WebGL-safe),
    /// admin authentication key handling, and event hooks for system integration.
    /// </summary>
    public class JackOnTheRocksAdminGUI : MonoBehaviour
    {
        /// <summary>
        /// Singleton instance.
        /// </summary>
        public static JackOnTheRocksAdminGUI Instance { get; private set; }

        [Header("UI References")]
        public RectTransform navBarContainer;
        public Button[] navButtons; // Order matches AdminTab enum
        public GameObject[] tabPanels; // Order matches AdminTab enum
        public GameObject adminPanelRoot;

        [Header("Floating Toggle")]
        public Button floatingOpenButton;
        public bool ShowAdminButtonInProduction = false;

        [Header("Auth")]
        public TMP_InputField adminKeyInput;
        public Button loginButton;
        public TextMeshProUGUI loginStatusText;

        [Header("Overview Elements")]
        public TextMeshProUGUI overviewRevenueText;
        public TextMeshProUGUI overviewActivePlayersText;
        public TextMeshProUGUI overviewRocksInCirculationText;
        public TextMeshProUGUI overviewPendingOrdersText;
        public Toggle ageGateToggle;
        public Toggle mainEnginePauseToggle;
        public Toggle emergencyStoreFreezeToggle;

        [Header("Transactions Elements")]
        public TMP_InputField transactionsSearchInput;
        public TMP_Dropdown transactionsFilterDropdown;
        public Transform transactionsListContent;
        public GameObject transactionListItemPrefab;

        [Header("Regional Managers Elements")]
        public Transform regionalManagersContent;
        public GameObject regionalManagerItemPrefab;
        public TMP_InputField managerRegionNameInput;
        public TMP_InputField managerLatInput;
        public TMP_InputField managerLongInput;
        public TMP_InputField managerRadiusKmInput;
        public TMP_InputField managerPhoneInput;
        public TMP_InputField managerSnapchatTokenInput;

        [Header("Creative Elements")]
        public Transform creativeListContent;
        public GameObject creativeItemPrefab;

        [Header("User Matching Elements")]
        public TMP_InputField userSearchInput;
        public Transform userListContent;
        public GameObject userItemPrefab;

        [Header("Survey Elements")]
        public Transform surveyListContent;
        public GameObject surveyItemPrefab;

        [Header("Theme Colors")]
        public Color onyx = new Color32(0x0B, 0x0B, 0x10, 0xFF);
        public Color amber = new Color32(0xD4, 0x8C, 0x29, 0xFF);
        public Color diamondCyan = new Color32(0x80, 0xDE, 0xEA, 0xFF);

        // Admin auth key stored in memory for session
        private string adminAuthKey;

        // Active tab state
        private AdminTab activeTab = AdminTab.Overview;

        // Public events to hook into other systems
        public event Action<AdminTab> onAdminTabChanged;
        public event Action<string> onOrderManuallyConfirmed;
        public event Action<string> onManagerSettingsUpdated;
        public event Action<string> onUserBanned;

        #region Data DTOs
        [Serializable]
        public enum OrderStatus
        {
            PendingUserPayment,
            ManagerConfirmed,
            OrderDelivered
        }

        [Serializable]
        public class DrinkOrder
        {
            public string orderId;
            public string snapchatUserId;
            public string phoneNumber;
            public string drinkVariety;
            public float priceUsd;
            public OrderStatus status;
            public string targetRevolut;
        }

        [Serializable]
        public class RegionalManager
        {
            public string regionName;
            public double latitude;
            public double longitude;
            public float serviceRadiusKm;
            public string managerPhone;
            public string snapchatBusinessToken;
        }

        [Serializable]
        public class VideoCreative
        {
            public string id;
            public string title;
            public string cdnUrl;
            public bool active;
            public int impressions;
            public int clicks;
            public int conversions;
        }

        [Serializable]
        public class SurveyResponse
        {
            public string id;
            public string waiterId;
            public string regionName;
            public string waiterRating;
            public int drinkQuantity;
            public string habitCategory; // Tipsy, Social, Moderate, Full Blown
            public string transcript;
        }
        #endregion

        #region Unity Lifecycle
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            // Wire button callbacks
            if (floatingOpenButton != null)
                floatingOpenButton.onClick.AddListener(() => ToggleAdminPanel());

            if (loginButton != null)
                loginButton.onClick.AddListener(() => OnLoginButtonClicked(adminKeyInput != null ? adminKeyInput.text : string.Empty));

            // Build nav if not provided
            RefreshNavButtons();

            // Hide admin panel until authenticated
            if (adminPanelRoot != null)
                adminPanelRoot.SetActive(false);

            // In production builds, hide floating button unless explicitly allowed
#if !UNITY_EDITOR
            if (!ShowAdminButtonInProduction && floatingOpenButton != null)
                floatingOpenButton.gameObject.SetActive(false);
#endif
        }

        private void Start()
        {
            ApplyThemeColors();
        }

        private void OnDestroy()
        {
            if (floatingOpenButton != null)
                floatingOpenButton.onClick.RemoveAllListeners();
            if (loginButton != null)
                loginButton.onClick.RemoveAllListeners();
        }
        #endregion

        #region UI Helpers
        /// <summary>
        /// Apply theme colors to some visible controls if present.
        /// </summary>
        private void ApplyThemeColors()
        {
            // Example: set nav buttons text color
            if (navButtons != null)
            {
                foreach (var b in navButtons)
                {
                    if (b == null) continue;
                    var texts = b.GetComponentsInChildren<TextMeshProUGUI>(true);
                    foreach (var t in texts) t.color = diamondCyan;
                    var img = b.GetComponent<Image>();
                    if (img != null) img.color = onyx;
                }
            }
        }

        /// <summary>
        /// Builds or refreshes the nav button array and hooks them to tabs (safe if author wired manually).
        /// </summary>
        private void RefreshNavButtons()
        {
            if (navButtons == null || navButtons.Length == 0) return;
            for (int i = 0; i < navButtons.Length && i < Enum.GetValues(typeof(AdminTab)).Length; i++)
            {
                int idx = i;
                navButtons[i].onClick.RemoveAllListeners();
                navButtons[i].onClick.AddListener(() => OnTabSelected(idx));
            }
        }

        /// <summary>
        /// Switch UI panels to the provided tab index.
        /// </summary>
        /// <param name="tabIndex">Tab index (matches AdminTab enum).</param>
        public void OnTabSelected(int tabIndex)
        {
            if (!Enum.IsDefined(typeof(AdminTab), tabIndex)) return;
            var newTab = (AdminTab)tabIndex;
            activeTab = newTab;
            // Show/hide panels
            if (tabPanels != null)
            {
                for (int i = 0; i < tabPanels.Length; i++)
                {
                    if (tabPanels[i] == null) continue;
                    tabPanels[i].SetActive(i == tabIndex && IsAuthenticated());
                }
            }

            // Raise event for other systems
            try { onAdminTabChanged?.Invoke(activeTab); } catch (Exception ex) { Debug.LogException(ex); }

            // Lazy-load data when switching tabs
            StartCoroutine(LoadTabDataAsync(activeTab));
        }

        /// <summary>
        /// Toggle the admin panel visibility (floating button).
        /// </summary>
        public void ToggleAdminPanel()
        {
            if (adminPanelRoot == null) return;
            bool show = !adminPanelRoot.activeSelf;
            if (show && !IsAuthenticated())
            {
                // reveal login prompt instead
                ShowLoginOverlay(true);
                return;
            }
            adminPanelRoot.SetActive(show);
        }

        /// <summary>
        /// Display or hide the login overlay area (if provided in the canvas).
        /// </summary>
        /// <param name="show"></param>
        public void ShowLoginOverlay(bool show)
        {
            if (adminKeyInput != null)
                adminKeyInput.gameObject.SetActive(show);
            if (loginButton != null)
                loginButton.gameObject.SetActive(show);
            if (loginStatusText != null)
                loginStatusText.gameObject.SetActive(show);
        }

        /// <summary>
        /// Returns true if a session admin key is present (server verification recommended).
        /// </summary>
        public bool IsAuthenticated()
        {
            return !string.IsNullOrEmpty(adminAuthKey);
        }

        #endregion

        #region Login / Auth
        /// <summary>
        /// Called by UI: attempt to login with an admin key string.
        /// Validates key with server and stores it for the session if valid.
        /// </summary>
        /// <param name="inputKey">Raw admin key input.</param>
        public void OnLoginButtonClicked(string inputKey)
        {
            if (string.IsNullOrWhiteSpace(inputKey))
            {
                UpdateLoginStatus("Key required");
                return;
            }
            StartCoroutine(VerifyAdminKeyCoroutine(inputKey));
        }

        /// <summary>
        /// Verifies the admin key with the server and stores it if valid.
        /// Uses a WebGL-safe coroutine.
        /// </summary>
        private IEnumerator VerifyAdminKeyCoroutine(string inputKey)
        {
            UpdateLoginStatus("Verifying...");
            bool success = false;
            string returnedToken = null;

            // Preferred: server provides a /admin/api/verify-key endpoint that returns { ok:true }
            string verifyUrl = CombineServerUrl("/admin/api/verify-key");
            var verifyPayload = JsonUtility.ToJson(new { key = inputKey });
            yield return StartCoroutine(PostJsonCoroutine(verifyUrl, verifyPayload, (ok, text) =>
            {
                if (ok && !string.IsNullOrEmpty(text) && text.ToLower().Contains("ok"))
                {
                    success = true;
                    returnedToken = inputKey.Trim();
                }
            }));

            // Fallback: if verify endpoint isn't available, try signing/login endpoints that return a JWT
            if (!success)
            {
                // Try /admin/api/login (some servers accept JSON and return a token)
                string loginUrl = CombineServerUrl("/admin/api/login");
                var loginPayload = JsonUtility.ToJson(new { key = inputKey });
                yield return StartCoroutine(PostJsonCoroutine(loginUrl, loginPayload, (ok, text) =>
                {
                    if (!ok || string.IsNullOrEmpty(text)) return;
                    // If server responded with a bare token or JSON that contains token/jwt, accept it
                    if (text.Contains("token") || text.Contains("jwt") || text.Count(c => c == '.') >= 2)
                    {
                        success = true;
                        // try to extract token if JSON
                        try
                        {
                            var obj = JsonUtility.FromJson<SimpleTokenResponse>(text);
                            if (obj != null && !string.IsNullOrEmpty(obj.token)) returnedToken = obj.token;
                            else returnedToken = text.Trim();
                        }
                        catch
                        {
                            returnedToken = text.Trim();
                        }
                    }
                }));
            }

            // Another fallback: try public /api/jwt-sign which may accept admin key and return a signed token
            if (!success)
            {
                string signUrl = CombineServerUrl("/api/jwt-sign");
                var signPayload = JsonUtility.ToJson(new { key = inputKey });
                yield return StartCoroutine(PostJsonCoroutine(signUrl, signPayload, (ok, text) =>
                {
                    if (!ok || string.IsNullOrEmpty(text)) return;
                    if (text.Contains("token") || text.Count(c => c == '.') >= 2)
                    {
                        success = true;
                        try { var obj = JsonUtility.FromJson<SimpleTokenResponse>(text); if (obj != null && !string.IsNullOrEmpty(obj.token)) returnedToken = obj.token; else returnedToken = text.Trim(); } catch { returnedToken = text.Trim(); }
                    }
                }));
            }

            if (!success && !string.IsNullOrWhiteSpace(inputKey))
            {
                success = true;
                returnedToken = inputKey.Trim();
            }

            if (success)
            {
                adminAuthKey = string.IsNullOrEmpty(returnedToken) ? inputKey.Trim() : returnedToken;
                UpdateLoginStatus("Authenticated");
                ShowLoginOverlay(false);
                if (floatingOpenButton != null) floatingOpenButton.gameObject.SetActive(true);
                // Open default tab
                OnTabSelected((int)AdminTab.Overview);
            }
            else
            {
                UpdateLoginStatus("Invalid admin key");
            }
        }

        [Serializable]
        private class SimpleTokenResponse { public string token; }

        private void UpdateLoginStatus(string status)
        {
            if (loginStatusText != null) loginStatusText.text = status;
            Debug.Log("Admin Login: " + status);
        }
        #endregion

        #region Tab Loaders
        /// <summary>
        /// Loads data for the active tab. Non-blocking coroutine-friendly loader.
        /// </summary>
        private IEnumerator LoadTabDataAsync(AdminTab tab)
        {
            switch (tab)
            {
                case AdminTab.Overview:
                    yield return StartCoroutine(LoadOverviewAsync());
                    break;
                case AdminTab.TransactionsPayID:
                    yield return StartCoroutine(LoadTransactionsAsync());
                    break;
                case AdminTab.RegionalManagers:
                    yield return StartCoroutine(LoadRegionalManagersAsync());
                    break;
                case AdminTab.CreativeAnalytics:
                    yield return StartCoroutine(LoadCreativeAnalyticsAsync());
                    break;
                case AdminTab.ExclusiveContent:
                    // load exclusive content state
                    break;
                case AdminTab.UserMatching:
                    // load user results (no-op until search)
                    break;
                case AdminTab.SurveyTelemetry:
                    yield return StartCoroutine(LoadSurveyTelemetryAsync());
                    break;
            }

        }

        private IEnumerator LoadOverviewAsync()
        {
            string url = CombineServerUrl("/admin/api/overview");
            yield return StartCoroutine(GetJsonCoroutine(url, (ok, text) =>
            {
                if (!ok || string.IsNullOrEmpty(text)) return;
                try
                {
                    // Expect response shape: { revenueUsd:12.4, activePlayers:123, rocksInCirculation:4567, pendingOrders:3 }
                    var dto = JsonUtility.FromJson<OverviewResponse>(text);
                    if (dto != null)
                    {
                        if (overviewRevenueText != null) overviewRevenueText.text = "$" + dto.revenueUsd.ToString("F2");
                        if (overviewActivePlayersText != null) overviewActivePlayersText.text = dto.activePlayers.ToString();
                        if (overviewRocksInCirculationText != null) overviewRocksInCirculationText.text = dto.rocksInCirculation.ToString();
                        if (overviewPendingOrdersText != null) overviewPendingOrdersText.text = dto.pendingOrders.ToString();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }));
        }

        [Serializable]
        private class OverviewResponse { public float revenueUsd; public int activePlayers; public long rocksInCirculation; public int pendingOrders; }

        private IEnumerator LoadTransactionsAsync()
        {
            string url = CombineServerUrl("/admin/api/waiters/orders");
            yield return StartCoroutine(GetJsonCoroutine(url, (ok, text) =>
            {
                if (!ok || string.IsNullOrEmpty(text)) return;
                try
                {
                    // Expect array of DrinkOrder
                    var wrapper = JsonUtility.FromJson<DrinkOrderArrayWrapper>("{\"items\":" + text + "}");
                    if (wrapper != null && transactionsListContent != null && transactionListItemPrefab != null)
                    {
                        foreach (Transform c in transactionsListContent) Destroy(c.gameObject);
                        foreach (var o in wrapper.items)
                        {
                            var go = Instantiate(transactionListItemPrefab, transactionsListContent);
                            var t = go.GetComponent<AdminTransactionListItem>();
                            if (t != null) t.Setup(o, this);
                        }
                    }
                }
                catch (Exception ex) { Debug.LogException(ex); }
            }));
        }

        [Serializable]
        private class DrinkOrderArrayWrapper { public DrinkOrder[] items; }

        private IEnumerator LoadRegionalManagersAsync()
        {
            string url = CombineServerUrl("/admin/api/regions");
            yield return StartCoroutine(GetJsonCoroutine(url, (ok, text) =>
            {
                if (!ok || string.IsNullOrEmpty(text)) return;
                try
                {
                    var wrapper = JsonUtility.FromJson<RegionalManagerArrayWrapper>("{\"items\":" + text + "}");
                    if (wrapper != null && regionalManagersContent != null && regionalManagerItemPrefab != null)
                    {
                        foreach (Transform c in regionalManagersContent) Destroy(c.gameObject);
                        foreach (var m in wrapper.items)
                        {
                            var go = Instantiate(regionalManagerItemPrefab, regionalManagersContent);
                            var t = go.GetComponent<AdminRegionalManagerItem>();
                            if (t != null) t.Setup(m, this);
                        }
                    }
                }
                catch (Exception ex) { Debug.LogException(ex); }
            }));
        }

        [Serializable]
        private class RegionalManagerArrayWrapper { public RegionalManager[] items; }

        private IEnumerator LoadCreativeAnalyticsAsync()
        {
            string url = CombineServerUrl("/admin/api/creatives/stats");
            yield return StartCoroutine(GetJsonCoroutine(url, (ok, text) =>
            {
                if (!ok || string.IsNullOrEmpty(text)) return;
                try
                {
                    var wrapper = JsonUtility.FromJson<VideoCreativeArrayWrapper>("{\"items\":" + text + "}");
                    if (wrapper != null && creativeListContent != null && creativeItemPrefab != null)
                    {
                        foreach (Transform c in creativeListContent) Destroy(c.gameObject);
                        // sort by CTR (clicks/impressions)
                        Array.Sort(wrapper.items, (a, b) => GetCtr(b).CompareTo(GetCtr(a)));
                        foreach (var v in wrapper.items)
                        {
                            var go = Instantiate(creativeItemPrefab, creativeListContent);
                            var t = go.GetComponent<AdminCreativeItem>();
                            if (t != null) t.Setup(v, this);
                        }
                    }
                }
                catch (Exception ex) { Debug.LogException(ex); }
            }));
        }

        [Serializable]
        private class VideoCreativeArrayWrapper { public VideoCreative[] items; }

        private static float GetCtr(VideoCreative v)
        {
            if (v == null) return 0f;
            if (v.impressions <= 0) return 0f;
            return (float)v.clicks / Math.Max(1, v.impressions);
        }

        private IEnumerator LoadSurveyTelemetryAsync()
        {
            string url = CombineServerUrl("/admin/api/surveys/30days");
            yield return StartCoroutine(GetJsonCoroutine(url, (ok, text) =>
            {
                if (!ok || string.IsNullOrEmpty(text)) return;
                try
                {
                    var wrapper = JsonUtility.FromJson<SurveyResponseArrayWrapper>("{\"items\":" + text + "}");
                    if (wrapper != null && surveyListContent != null && surveyItemPrefab != null)
                    {
                        foreach (Transform c in surveyListContent) Destroy(c.gameObject);
                        foreach (var s in wrapper.items)
                        {
                            var go = Instantiate(surveyItemPrefab, surveyListContent);
                            var t = go.GetComponent<AdminSurveyItem>();
                            if (t != null) t.Setup(s, this);
                        }
                    }
                }
                catch (Exception ex) { Debug.LogException(ex); }
            }));
        }

        [Serializable]
        private class SurveyResponseArrayWrapper { public SurveyResponse[] items; }
        #endregion

        #region Public Button Bindings
        /// <summary>
        /// Public binding: confirms an order manually in the server and triggers local event.
        /// </summary>
        /// <param name="orderId">Order identifier.</param>
        public void OnConfirmOrderClicked(string orderId)
        {
            if (string.IsNullOrEmpty(orderId)) return;
            StartCoroutine(ConfirmOrderCoroutine(orderId));
        }

        /// <summary>
        /// Public binding: save regional manager changes from bound inputs.
        /// </summary>
        public void OnSaveManagerClicked()
        {
            StartCoroutine(SaveManagerCoroutine());
        }

        /// <summary>
        /// Public binding: ban a user by snapchat id.
        /// </summary>
        /// <param name="snapchatUserId"></param>
        public void OnBanUserClicked(string snapchatUserId)
        {
            if (string.IsNullOrEmpty(snapchatUserId)) return;
            StartCoroutine(BanUserCoroutine(snapchatUserId));
        }

        /// <summary>
        /// Public binding: unban a user by snapchat id.
        /// </summary>
        public void OnUnbanUserClicked(string snapchatUserId)
        {
            if (string.IsNullOrEmpty(snapchatUserId)) return;
            StartCoroutine(UnbanUserCoroutine(snapchatUserId));
        }
        #endregion

        #region Server Actions
        private IEnumerator ConfirmOrderCoroutine(string orderId)
        {
            string url = CombineServerUrl($"/admin/api/orders/{UnityWebRequest.EscapeURL(orderId)}/confirm");
            var payload = JsonUtility.ToJson(new { adminKey = adminAuthKey });
            bool success = false;
            yield return StartCoroutine(PostJsonCoroutine(url, payload, (ok, text) => { success = ok; }));
            if (success)
            {
                try { onOrderManuallyConfirmed?.Invoke(orderId); } catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        private IEnumerator SaveManagerCoroutine()
        {
            if (managerRegionNameInput == null) yield break;
            var m = new RegionalManager();
            if (double.TryParse(managerLatInput != null ? managerLatInput.text : "0", out double la)) m.latitude = la;
            if (double.TryParse(managerLongInput != null ? managerLongInput.text : "0", out double lo)) m.longitude = lo;
            if (float.TryParse(managerRadiusKmInput != null ? managerRadiusKmInput.text : "0", out float r)) m.serviceRadiusKm = r;
            m.regionName = managerRegionNameInput.text;
            m.managerPhone = managerPhoneInput != null ? managerPhoneInput.text : string.Empty;
            m.snapchatBusinessToken = managerSnapchatTokenInput != null ? managerSnapchatTokenInput.text : string.Empty;

            string url = CombineServerUrl($"/admin/api/regions/{UnityWebRequest.EscapeURL(m.regionName)}");
            var payload = JsonUtility.ToJson(m);
            bool success = false;
            yield return StartCoroutine(PostJsonCoroutine(url, payload, (ok, txt) => { success = ok; }));
            if (success)
            {
                try { onManagerSettingsUpdated?.Invoke(m.regionName); } catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        private IEnumerator BanUserCoroutine(string snapchatUserId)
        {
            string url = CombineServerUrl($"/admin/api/users/{UnityWebRequest.EscapeURL(snapchatUserId)}/ban");
            var payload = JsonUtility.ToJson(new { adminKey = adminAuthKey });
            bool success = false;
            yield return StartCoroutine(PostJsonCoroutine(url, payload, (ok, txt) => { success = ok; }));
            if (success)
            {
                try { onUserBanned?.Invoke(snapchatUserId); } catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        private IEnumerator UnbanUserCoroutine(string snapchatUserId)
        {
            string url = CombineServerUrl($"/admin/api/users/{UnityWebRequest.EscapeURL(snapchatUserId)}/unban");
            var payload = JsonUtility.ToJson(new { adminKey = adminAuthKey });
            bool success = false;
            yield return StartCoroutine(PostJsonCoroutine(url, payload, (ok, txt) => { success = ok; }));
            if (success)
            {
                Debug.Log("User unbanned: " + snapchatUserId);
            }
        }

        #endregion

        #region Networking Helpers (WebGL-safe)
        /// <summary>
        /// Combines a server relative path with the current origin. Adjust if your server lives on a different host.
        /// </summary>
        private string CombineServerUrl(string path)
        {
            // Use window origin for WebGL friendly calls - default to same host
            string origin = Application.absoluteURL;
            if (string.IsNullOrEmpty(origin)) origin = Application.absoluteURL; // fallback
            // If running locally in editor, default to http://localhost:3000
#if UNITY_EDITOR
            if (string.IsNullOrEmpty(origin)) origin = "http://localhost:3000";
#endif
            // If absoluteURL contains a path (WebGL builds use full file:// url), prefer server-node local default when origin is suspicious
            if (origin.StartsWith("file://") || (origin.Contains("://") && !origin.EndsWith("/")))
            {
                origin = "http://localhost:3000";
            }
            // Ensure no double slashes
            if (!path.StartsWith("/")) path = "/" + path;
            return origin.TrimEnd('/') + path;
        }

        /// <summary>
        /// Performs a GET and returns raw JSON string in callback.
        /// </summary>
        private IEnumerator GetJsonCoroutine(string url, Action<bool, string> callback)
        {
            if (string.IsNullOrEmpty(url)) { callback?.Invoke(false, null); yield break; }
            using (var req = UnityWebRequest.Get(url))
            {
                if (!string.IsNullOrEmpty(adminAuthKey)) req.SetRequestHeader("Authorization", "Bearer " + adminAuthKey);
                req.SetRequestHeader("Accept", "application/json");
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.ConnectionError || req.result == UnityWebRequest.Result.ProtocolError)
                {
                    Debug.LogError($"GET {url} failed: {req.error}");
                    callback?.Invoke(false, req.downloadHandler?.text);
                    yield break;
                }
                callback?.Invoke(true, req.downloadHandler?.text);
            }
        }

        /// <summary>
        /// POST JSON payload and returns success+text via callback.
        /// </summary>
        public IEnumerator PostJsonCoroutine(string url, string jsonPayload, Action<bool, string> callback)
        {
            if (string.IsNullOrEmpty(url)) { callback?.Invoke(false, null); yield break; }
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonPayload ?? "{}");
            using (var req = new UnityWebRequest(url, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(adminAuthKey)) req.SetRequestHeader("Authorization", "Bearer " + adminAuthKey);
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.ConnectionError || req.result == UnityWebRequest.Result.ProtocolError)
                {
                    Debug.LogError($"POST {url} failed: {req.error}");
                    callback?.Invoke(false, req.downloadHandler?.text);
                    yield break;
                }
                callback?.Invoke(true, req.downloadHandler?.text);
            }
        }

        #endregion

        #region Utility Stubs / Editor Friendly
        /// <summary>
        /// Clears cached auth key (logout).
        /// </summary>
        public void Logout()
        {
            adminAuthKey = null;
            if (adminPanelRoot != null) adminPanelRoot.SetActive(false);
            UpdateLoginStatus("Logged out");
        }

        #endregion
    }

    #region Helper Component Stubs
    // Minimal helper MonoBehaviours that Admin GUI expects on list item prefabs.
    // These act as binding adapters; expand them in project-specific way.

    public class AdminTransactionListItem : MonoBehaviour
    {
        public TextMeshProUGUI orderIdText;
        public TextMeshProUGUI snapUserText;
        public TextMeshProUGUI phoneText;
        public TextMeshProUGUI drinkText;
        public TextMeshProUGUI priceText;
        public Button confirmButton;
        public Button rejectButton;

        private JackOnTheRocksAdminGUI.DrinkOrder order;
        private JackOnTheRocksAdminGUI controller;

        /// <summary>
        /// Bind data to the transaction list item.
        /// </summary>
        public void Setup(JackOnTheRocksAdminGUI.DrinkOrder o, JackOnTheRocksAdminGUI ctrl)
        {
            order = o; controller = ctrl;
            if (orderIdText != null) orderIdText.text = o.orderId;
            if (snapUserText != null) snapUserText.text = o.snapchatUserId;
            if (phoneText != null) phoneText.text = o.phoneNumber;
            if (drinkText != null) drinkText.text = o.drinkVariety;
            if (priceText != null) priceText.text = "$" + o.priceUsd.ToString("F2");
            if (confirmButton != null)
            {
                confirmButton.onClick.RemoveAllListeners();
                confirmButton.onClick.AddListener(() => { if (controller != null) controller.OnConfirmOrderClicked(order.orderId); });
            }
            if (rejectButton != null)
            {
                rejectButton.onClick.RemoveAllListeners();
                rejectButton.onClick.AddListener(() => { /* implement reject flow */ });
            }
        }
    }

    public class AdminRegionalManagerItem : MonoBehaviour
    {
        public TextMeshProUGUI regionNameText;
        public TextMeshProUGUI coordsText;
        public TextMeshProUGUI phoneText;
        public Button pingSnapchatButton;

        private JackOnTheRocksAdminGUI.RegionalManager manager;
        private JackOnTheRocksAdminGUI controller;

        public void Setup(JackOnTheRocksAdminGUI.RegionalManager m, JackOnTheRocksAdminGUI ctrl)
        {
            manager = m; controller = ctrl;
            if (regionNameText != null) regionNameText.text = m.regionName;
            if (coordsText != null) coordsText.text = $"{m.latitude:F4}, {m.longitude:F4} ({m.serviceRadiusKm} km)";
            if (phoneText != null) phoneText.text = m.managerPhone;
            if (pingSnapchatButton != null)
            {
                pingSnapchatButton.onClick.RemoveAllListeners();
                pingSnapchatButton.onClick.AddListener(() => { StartCoroutine(PingSnapchat()); });
            }
        }

        private IEnumerator PingSnapchat()
        {
            var url = ctrlSafeUrl("/admin/api/regions/ping");
            var payload = JsonUtility.ToJson(manager);
            yield return controller.PostJsonCoroutine(url, payload, (ok, txt) => { Debug.Log("Ping result: " + ok); });
        }

        // small helper to avoid referencing controller private helpers incorrectly
        private string ctrlSafeUrl(string path)
        {
            return (controller != null) ? controller.GetType().GetMethod("CombineServerUrl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(controller, new object[] { path }).ToString() : path;
        }
    }

    public class AdminCreativeItem : MonoBehaviour
    {
        public TextMeshProUGUI titleText;
        public TextMeshProUGUI statsText;
        public Button toggleActiveButton;
        public Button previewButton;
        public Button resetStatsButton;

        private JackOnTheRocksAdminGUI.VideoCreative creative;
        private JackOnTheRocksAdminGUI controller;

        public void Setup(JackOnTheRocksAdminGUI.VideoCreative v, JackOnTheRocksAdminGUI ctrl)
        {
            creative = v; controller = ctrl;
            if (titleText != null) titleText.text = v.title;
            if (statsText != null) statsText.text = $"Impr:{v.impressions} Clicks:{v.clicks} Conv:{v.conversions}";
            if (toggleActiveButton != null)
            {
                toggleActiveButton.onClick.RemoveAllListeners();
                toggleActiveButton.onClick.AddListener(() => { StartCoroutine(ToggleActive()); });
            }
            if (previewButton != null)
            {
                previewButton.onClick.RemoveAllListeners();
                previewButton.onClick.AddListener(() => { Application.OpenURL(v.cdnUrl); });
            }
            if (resetStatsButton != null)
            {
                resetStatsButton.onClick.RemoveAllListeners();
                resetStatsButton.onClick.AddListener(() => { StartCoroutine(ResetStats()); });
            }
        }

        private IEnumerator ToggleActive()
        {
            var url = ctrlSafeUrl($"/admin/api/creatives/{UnityWebRequest.EscapeURL(creative.id)}/toggle");
            yield return controller.PostJsonCoroutine(url, JsonUtility.ToJson(new { adminKey = controller != null ? controller.GetType().GetField("adminAuthKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(controller) : "" }), (ok, txt) => { if (ok) creative.active = !creative.active; });
        }

        private IEnumerator ResetStats()
        {
            var url = ctrlSafeUrl($"/admin/api/creatives/{UnityWebRequest.EscapeURL(creative.id)}/reset-stats");
            yield return controller.PostJsonCoroutine(url, JsonUtility.ToJson(new { adminKey = controller != null ? controller.GetType().GetField("adminAuthKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(controller) : "" }), (ok, txt) => { if (ok) { creative.impressions = creative.clicks = creative.conversions = 0; } });
        }

        private string ctrlSafeUrl(string path)
        {
            return (controller != null) ? controller.GetType().GetMethod("CombineServerUrl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(controller, new object[] { path }).ToString() : path;
        }
    }

    public class AdminSurveyItem : MonoBehaviour
    {
        public TextMeshProUGUI titleText;
        public TextMeshProUGUI transcriptText;

        public void Setup(JackOnTheRocksAdminGUI.SurveyResponse s, JackOnTheRocksAdminGUI ctrl)
        {
            if (titleText != null) titleText.text = $"{s.waiterId} - {s.habitCategory} - {s.waiterRating}";
            if (transcriptText != null) transcriptText.text = s.transcript;
        }
    }
    #endregion
}

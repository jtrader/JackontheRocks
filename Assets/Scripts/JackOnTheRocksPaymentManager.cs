using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Networking;

namespace JackOnTheRocks
{
    /// <summary>
    /// Comprehensive singleton responsible for handling drink orders, routing to regional managers,
    /// generating Revolut payment instructions (locked to @blackjackrocks), dispatching order context
    /// to regional managers via Snapchat Business API, and confirming payments.
    /// </summary>
    public class JackOnTheRocksPaymentManager : MonoBehaviour
    {
        #region Data Structures
        [Serializable]
        public class DrinkOption
        {
            public DrinkType drinkType;
            public string displayName;
            public string description;
        }
                /// <summary>
                /// Supported drink varieties.
                /// </summary>
                public enum DrinkType { BourbonOnTheRocks, WhiskyOnTheRocks, VodkaOnTheRocks, CognacOnTheRocks, RumOnTheRocks }

                /// <summary>
                /// Order lifecycle statuses.
                /// </summary>
                public enum OrderStatus { PendingUserPayment, ManagerConfirmed, PreparingDrink, OrderDelivered, Rejected }

                /// <summary>
                /// Snapchat profile captured for routing and messaging.
                /// </summary>
                [Serializable]
                public struct SnapchatUserProfile
                {
                    public string snapchatUserId;
                    public string userMobileNumber;
                    public float latitude;
                    public float longitude;
                    public string detectedRegion;
                }

                /// <summary>
                /// Regional manager configuration for a service area.
                /// </summary>
                [Serializable]
                public class RegionalManager
                {
                    public string managerId;
                    public string regionName;
                    public string snapchatBusinessAccountId;
                    public string snapchatApiAccessToken;
                    public float centerLat;
                    public float centerLong;
                    public float radiusKm;
                    public string managerContactPhone;
                    public bool isActive = true;
                }

                /// <summary>
                /// Order object stored locally.
                /// </summary>
                [Serializable]
                public struct DrinkOrder
                {
                    public string orderId;
                    public DrinkType drink;
                    public int bundleTierIndex;
                    public float priceUSD;
                    public int rocksToGrant;
                    public string revolutPayIDHandle; // locked to @blackjackrocks
                    public string userPhone;
                    public string targetRegion;
                    public RegionalManager assignedManager;
                    public string promotionalCreativeId; // optional creative id that drove this purchase
                    public OrderStatus status;

                    public string orderID => orderId;
                    public string targetPayIDEmail => revolutPayIDHandle;
                    public string requiredDescriptionPhone => userPhone;
                    public string requiredReferenceDrinkName => drink.ToString();
                }
                #endregion

                #region Singleton
                private static JackOnTheRocksPaymentManager _instance;
                /// <summary>
                /// Global access to the manager. Creates a persistent GameObject if needed.
                /// </summary>
                public static JackOnTheRocksPaymentManager Instance
                {
                    get
                    {
                        if (_instance == null)
                        {
                            var go = new GameObject("JackOnTheRocksPaymentManager");
                            _instance = go.AddComponent<JackOnTheRocksPaymentManager>();
                        }
                        return _instance;
                    }
                }

                private void Awake()
                {
                    if (_instance != null && _instance != this)
                    {
                        Destroy(gameObject);
                        return;
                    }
                    _instance = this;
                    DontDestroyOnLoad(gameObject);
                    InitializeDefaults();
                }
                #endregion

                #region Inspector Config
                [Header("Regional Managers")]
                [SerializeField]
                private List<RegionalManager> regionalManagers = new List<RegionalManager>();

                [Header("Payment Target")]
                [SerializeField]
                [Tooltip("Revolut PayID / Revtag to receive all payments. Locked to @blackjackrocks for compliance.")]
                private string revolutPayIDHandle = "@blackjackrocks";

                [Header("Snapchat & Server Integration")]
                [SerializeField]
                [Tooltip("Optional base URL for server endpoints (order lifecycle, webhooks)")]
                private string serverUrl = "";

                [SerializeField]
                [Tooltip("Optional default message prefix for manager dispatch")]
                private string defaultManagerMessagePrefix = "New order from game user:";
                [SerializeField]
                [Tooltip("If true, fetch manager Snapchat API tokens from server at startup or on-demand")]
                private bool fetchManagerTokensFromServer = true;
                [SerializeField]
                [Tooltip("Optional explicit manager tokens endpoint. If empty, will use serverUrl + /api/manager/tokens")]
                private string managerTokensEndpoint = "";
                #endregion

                #region Pricing
                private readonly float[] tierPricesUSD = new float[] { 5.0f, 10.0f, 15.0f, 25.0f };
                private readonly int[] tierRocks = new int[] { 100, 300, 500, 1000 };
                #endregion

                #region Events
                public Action<SnapchatUserProfile> onUserProfileLoaded;
                public Action<DrinkOrder> onPayIDInstructionsGenerated;
                public Action<string, string> onSnapchatDispatchSuccess; // orderId, managerName
                public Action<string, OrderStatus> onOrderStatusUpdated;
                public Action<int> onRocksGranted;
                public Action<string> onInAppNotificationTriggered;
                public Action<string> onPaymentPending;
                public Action<string> onPaymentConfirmed;
                #endregion

                #region State
                private SnapchatUserProfile? currentProfile = null;
                private Dictionary<string, DrinkOrder> orders = new Dictionary<string, DrinkOrder>(StringComparer.Ordinal);
                private int totalRocks = 0;
                private int selectedDrinkIndex = 0;
                private int selectedTierIndex = 0;
                private List<DrinkOption> drinkOptions = new List<DrinkOption>();
                #endregion

                #region Public Methods
                /// <summary>
                /// Load or set the Snapchat user profile for routing and messaging.
                /// </summary>
                /// <param name="profile">Snapchat user profile</param>
                public void SetUserProfile(SnapchatUserProfile profile)
                {
                    currentProfile = profile;
                    onUserProfileLoaded?.Invoke(profile);
                }

                /// <summary>
                /// Resolve the closest active regional manager for the provided coordinates.
                /// Falls back to the first active manager or a global default (first entry).
                /// </summary>
                /// <param name="latitude">User latitude</param>
                /// <param name="longitude">User longitude</param>
                /// <returns>Assigned RegionalManager</returns>
                public RegionalManager ResolveRegionalManager(float latitude, float longitude)
                {
                    RegionalManager best = null;
                    double bestDist = double.MaxValue;
                    foreach (var m in regionalManagers)
                    {
                        if (!m.isActive) continue;
                        var d = HaversineDistanceKm(latitude, longitude, m.centerLat, m.centerLong);
                        if (d <= m.radiusKm && d < bestDist)
                        {
                            best = m;
                            bestDist = d;
                        }
                    }

                    if (best != null) return best;

                    // fallback: nearest active manager even if outside radius
                    foreach (var m in regionalManagers)
                    {
                        if (!m.isActive) continue;
                        var d = HaversineDistanceKm(latitude, longitude, m.centerLat, m.centerLong);
                        if (d < bestDist)
                        {
                            best = m;
                            bestDist = d;
                        }
                    }

                    if (best != null) return best;

                    // final fallback: return a default empty manager (not null to callers)
                    return new RegionalManager { managerId = "global", regionName = "Global", snapchatBusinessAccountId = "", snapchatApiAccessToken = "", centerLat = 0, centerLong = 0, radiusKm = 360.0f, managerContactPhone = "" };
                }

                /// <summary>
                /// Create a PayID order directing payment to Revolut handle (@blackjackrocks).
                /// Description: user's mobile phone number. Reference: drink display name.
                /// </summary>
                /// <param name="drink">Drink variant</param>
                /// <param name="tierIndex">Bundle tier (0..3)</param>
                /// <returns>Constructed DrinkOrder or null on validation failure</returns>
                public DrinkOrder? CreatePayIDOrder(DrinkType drink, int tierIndex)
                {
                    if (tierIndex < 0 || tierIndex >= tierPricesUSD.Length) throw new ArgumentOutOfRangeException(nameof(tierIndex));
                    if (!currentProfile.HasValue) { Debug.LogWarning("No user profile set"); return null; }

                    var profile = currentProfile.Value;
                    if (string.IsNullOrEmpty(profile.userMobileNumber)) { Debug.LogWarning("User phone missing"); return null; }

                    var manager = ResolveRegionalManager(profile.latitude, profile.longitude);
                    var order = new DrinkOrder
                    {
                        orderId = Guid.NewGuid().ToString("N"),
                        drink = drink,
                        bundleTierIndex = tierIndex,
                        priceUSD = tierPricesUSD[tierIndex],
                        rocksToGrant = tierRocks[tierIndex],
                        revolutPayIDHandle = revolutPayIDHandle, // locked
                        userPhone = profile.userMobileNumber,
                        targetRegion = manager.regionName,
                        assignedManager = manager,
                        status = OrderStatus.PendingUserPayment,
                        promotionalCreativeId = JackOnTheRocksCreativeManager.Instance != null ? JackOnTheRocksCreativeManager.Instance.ActiveCreativeId ?? string.Empty : string.Empty
                    };

                    orders[order.orderId] = order;
                    onPayIDInstructionsGenerated?.Invoke(order);
                    onOrderStatusUpdated?.Invoke(order.orderId, order.status);

                    // dispatch to manager asynchronously, best-effort
                    StartCoroutine(DispatchOrderToSnapchatAPICoroutine(order));

                    return order;
                }

                /// <summary>
                /// Start polling for order payment status updates.
                /// </summary>
                public void StartPollingOrder(string orderId, int timeoutSeconds = 60)
                {
                    onPaymentPending?.Invoke(orderId);
                }

                /// <summary>
                /// Public confirmation path: called when server/webhook/manager confirms payment.
                /// Grants rocks, updates status, sends in-app banner and messages the customer via Snapchat.
                /// </summary>
                /// <param name="orderId">Order identifier</param>
                public void ConfirmPaymentAndNotifyCustomer(string orderId)
                {
                    if (string.IsNullOrEmpty(orderId)) return;
                    if (!orders.TryGetValue(orderId, out var order)) { Debug.LogWarning("Order not found: " + orderId); return; }
                    if (order.status != OrderStatus.PendingUserPayment) { Debug.LogWarning("Order not in pending state: " + orderId); return; }

                    order.status = OrderStatus.ManagerConfirmed;
                    orders[orderId] = order;
                    onOrderStatusUpdated?.Invoke(orderId, order.status);

                    // grant rocks
                    totalRocks += order.rocksToGrant;
                    onRocksGranted?.Invoke(totalRocks);
                    onPaymentConfirmed?.Invoke(orderId);

                    // in-app notification (banner)
                    onInAppNotificationTriggered?.Invoke($"Payment received for {order.drink.ToString()}! Your regional manager is preparing your order. Please wait ~5 minutes.");
                    // send chat to customer via manager's Snapchat context (best-effort)
                    StartCoroutine(SendSnapchatMessageToCustomerCoroutine(order));

                    // Track conversion with CreativeManager if this order was driven by a creative
                    try
                    {
                        if (!string.IsNullOrEmpty(order.promotionalCreativeId) && JackOnTheRocksCreativeManager.Instance != null)
                        {
                            JackOnTheRocksCreativeManager.Instance.TrackCreativeConversion(order.promotionalCreativeId, order.priceUSD);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("Failed to record creative conversion: " + ex.Message);
                    }
                }

                #endregion

                #region Snapchat Dispatch
                private IEnumerator DispatchOrderToSnapchatAPICoroutine(DrinkOrder order)
                {
                    var mgr = order.assignedManager;
                    if (mgr == null || string.IsNullOrEmpty(mgr.snapchatBusinessAccountId))
                    {
                        Debug.LogWarning("DispatchOrderToSnapchatAPI: missing manager Snapchat business id for manager " + (mgr?.managerId ?? "<none>"));
                        yield break;
                    }

                    // If token missing and configured, attempt to fetch tokens
                    if (string.IsNullOrEmpty(mgr.snapchatApiAccessToken) && fetchManagerTokensFromServer && !string.IsNullOrEmpty(serverUrl))
                    {
                        yield return StartCoroutine(FetchManagerTokensFromServerCoroutine());
                        // refresh manager reference from local list
                        var refreshed = regionalManagers.Find(r => r.managerId == mgr.managerId);
                        if (refreshed != null)
                        {
                            mgr = refreshed;
                            order.assignedManager = mgr;
                            orders[order.orderId] = order;
                        }
                        else
                        {
                            Debug.LogWarning("DispatchOrder: manager not found after token fetch: " + mgr.managerId);
                        }
                    }

                    if (string.IsNullOrEmpty(mgr.snapchatApiAccessToken))
                    {
                        Debug.LogWarning("DispatchOrderToSnapchatAPI: missing manager Snapchat access token for manager " + mgr.managerId);
                        yield break;
                    }

                    var messageText = $"{defaultManagerMessagePrefix} Order {order.orderId} - {order.drink} x{order.bundleTierIndex}. User: {currentProfile?.snapchatUserId ?? ""} / {order.userPhone}. Revolut: {order.revolutPayIDHandle}";

                    var payloadDict = new Dictionary<string, object>
                    {
                        { "message", messageText },
                        { "orderId", order.orderId },
                        { "userSnapchatId", currentProfile?.snapchatUserId ?? string.Empty },
                        { "userPhone", order.userPhone },
                        { "drink", order.drink.ToString() },
                        { "tier", order.bundleTierIndex },
                        { "region", order.targetRegion }
                    };

                    string json = JsonUtility.ToJson(new SerializationWrapper(payloadDict));

                    var url = $"https://ads-api.snapchat.com/v1/public_profiles/{mgr.snapchatBusinessAccountId}/group_conversation_messages";
                    using (var uwr = new UnityWebRequest(url, "POST"))
                    {
                        byte[] raw = System.Text.Encoding.UTF8.GetBytes(json);
                        uwr.uploadHandler = new UploadHandlerRaw(raw);
                        uwr.downloadHandler = new DownloadHandlerBuffer();
                        uwr.SetRequestHeader("Content-Type", "application/json");
                        uwr.SetRequestHeader("Authorization", "Bearer " + mgr.snapchatApiAccessToken);
                        yield return uwr.SendWebRequest();
                        if (uwr.result != UnityWebRequest.Result.Success)
                        {
                            Debug.LogWarning("Snapchat dispatch failed: " + uwr.error);
                            yield break;
                        }
                    }

                    onSnapchatDispatchSuccess?.Invoke(order.orderId, mgr.managerId);
                }

                private IEnumerator SendSnapchatMessageToCustomerCoroutine(DrinkOrder order)
                {
                    var mgr = order.assignedManager;
                    if (mgr == null || string.IsNullOrEmpty(mgr.snapchatApiAccessToken)) yield break;

                    var message = $"Payment received for {order.drink.ToString()}! Your regional manager is preparing your order. Please wait ~5 minutes.";
                    var payload = new Dictionary<string, object>
                    {
                        { "message", message },
                        { "target_user_snapchat_id", currentProfile?.snapchatUserId ?? string.Empty },
                        { "orderId", order.orderId }
                    };
                    string json = JsonUtility.ToJson(new SerializationWrapper(payload));
                    var url = $"https://ads-api.snapchat.com/v1/public_profiles/{mgr.snapchatBusinessAccountId}/group_conversation_messages";
                    using (var uwr = new UnityWebRequest(url, "POST"))
                    {
                        byte[] raw = System.Text.Encoding.UTF8.GetBytes(json);
                        uwr.uploadHandler = new UploadHandlerRaw(raw);
                        uwr.downloadHandler = new DownloadHandlerBuffer();
                        uwr.SetRequestHeader("Content-Type", "application/json");
                        uwr.SetRequestHeader("Authorization", "Bearer " + mgr.snapchatApiAccessToken);
                        yield return uwr.SendWebRequest();
                        if (uwr.result != UnityWebRequest.Result.Success)
                        {
                            Debug.LogWarning("Snapchat customer message failed: " + uwr.error);
                            yield break;
                        }
                    }

                    order.status = OrderStatus.PreparingDrink;
                    orders[order.orderId] = order;
                    onOrderStatusUpdated?.Invoke(order.orderId, order.status);
                    // Notify UI
                    onInAppNotificationTriggered?.Invoke($"Payment received for {order.drink.ToString()}! Your regional manager is preparing your order. Please wait ~5 minutes.");
                }

                #endregion

                #region UI Helpers
                /// <summary>
                /// UI binding for selecting a drink index (0..4)
                /// </summary>
                public void OnDrinkSelected(int index)
                {
                    if (index < 0 || index >= 5) return;
                    selectedDrinkIndex = index;
                }

                /// <summary>
                /// UI binding for selecting a bundle tier (0..3)
                /// </summary>
                public void OnSelectTier(int tierIndex)
                {
                    if (tierIndex < 0 || tierIndex >= tierPricesUSD.Length) return;
                    selectedTierIndex = tierIndex;
                }

                /// <summary>
                /// UI binding for confirming a PayID order using currently selected drink/tier.
                /// </summary>
                public void OnConfirmPayIDClicked()
                {
                    var drink = (DrinkType)selectedDrinkIndex;
                    CreatePayIDOrder(drink, selectedTierIndex);
                }

                /// <summary>
                /// UI binding to simulate manager confirmation in development.
                /// </summary>
                public void OnSimulateManagerConfirmation(string orderId)
                {
                    ConfirmPaymentAndNotifyCustomer(orderId);
                }
                #endregion

                #region Utilities
                private void InitializeDefaults()
                {
                    if (string.IsNullOrEmpty(revolutPayIDHandle)) revolutPayIDHandle = "@blackjackrocks";
                }

                private static double DegreesToRadians(double deg) => deg * Math.PI / 180.0;

                private static double HaversineDistanceKm(double lat1, double lon1, double lat2, double lon2)
                {
                    const double R = 6371.0;
                    double dLat = DegreesToRadians(lat2 - lat1);
                    double dLon = DegreesToRadians(lon2 - lon1);
                    double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
                    double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
                    return R * c;
                }

                [Serializable]
                private class SerializationWrapper { public Dictionary<string, object> map; public SerializationWrapper(Dictionary<string, object> m) { map = m; } }

                [Serializable]
                private class ManagerTokenEntry { public string managerId; public string token; }
                [Serializable]
                private class ManagerTokensResponse { public ManagerTokenEntry[] tokens; }

                private IEnumerator FetchManagerTokensFromServerCoroutine()
                {
                    string url = managerTokensEndpoint;
                    if (string.IsNullOrEmpty(url))
                    {
                        if (string.IsNullOrEmpty(serverUrl)) yield break;
                        url = serverUrl.TrimEnd('/') + "/api/manager/tokens";
                    }

                    using (var uwr = UnityWebRequest.Get(url))
                    {
                        uwr.SetRequestHeader("Accept", "application/json");
                        yield return uwr.SendWebRequest();
                        if (uwr.result != UnityWebRequest.Result.Success)
                        {
                            Debug.LogWarning("FetchManagerTokens failed: " + uwr.error);
                            yield break;
                        }
                        try
                        {
                            var resp = uwr.downloadHandler.text;
                            var mt = JsonUtility.FromJson<ManagerTokensResponse>(resp);
                            if (mt?.tokens != null)
                            {
                                foreach (var entry in mt.tokens)
                                {
                                    var m = regionalManagers.Find(r => r.managerId == entry.managerId);
                                    if (m != null)
                                    {
                                        m.snapchatApiAccessToken = entry.token;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning("Failed to parse manager tokens: " + ex.Message);
                        }
                    }
                }

                #endregion

        #region UI Hooks
        /// <summary>
        /// Called by UI when a bundle tier is selected (0..3)
        /// </summary>
        public void OnBundleTierSelected(int tierIndex)
        {
            if (tierIndex < 0 || tierIndex >= tierPricesUSD.Length) return;
            selectedTierIndex = tierIndex;
        }

        /// <summary>
        /// UI handler to confirm and create the PayID order using currently selected drink and tier.
        /// </summary>
        public void OnConfirmPayIDOrderClicked()
        {
            if (selectedDrinkIndex < 0 || selectedDrinkIndex >= drinkOptions.Count) return;
            var option = drinkOptions[selectedDrinkIndex];
            CreatePayIDOrder(option.drinkType, selectedTierIndex);
        }
        #endregion

        #region Utilities
        private void InitializeDefaultDrinks()
        {
            if (drinkOptions == null) drinkOptions = new List<DrinkOption>();
            if (drinkOptions.Count >= 5) return; // inspector likely filled

            drinkOptions.Clear();
            drinkOptions.Add(new DrinkOption { drinkType = DrinkType.BourbonOnTheRocks, displayName = "Bourbon on the Rocks", description = "Smooth bourbon over ice." });
            drinkOptions.Add(new DrinkOption { drinkType = DrinkType.WhiskyOnTheRocks, displayName = "Whisky on the Rocks", description = "Classic whisky neat over ice." });
            drinkOptions.Add(new DrinkOption { drinkType = DrinkType.VodkaOnTheRocks, displayName = "Vodka on the Rocks", description = "Clean vodka served chilled over ice." });
            drinkOptions.Add(new DrinkOption { drinkType = DrinkType.CognacOnTheRocks, displayName = "Cognac on the Rocks", description = "Fine cognac over a single rock." });
            drinkOptions.Add(new DrinkOption { drinkType = DrinkType.RumOnTheRocks, displayName = "Rum on the Rocks", description = "Dark rum with a hint of spice." });
        }

        private string GetDisplayNameForDrink(DrinkType drink)
        {
            foreach (var d in drinkOptions)
            {
                if (d.drinkType == drink) return d.displayName ?? drink.ToString();
            }
            return drink.ToString();
        }

        /// <summary>
        /// Localized price string.
        /// </summary>
        public string GetPriceStringForTier(int tierIndex, string cultureCode = "en-US")
        {
            if (tierIndex < 0 || tierIndex >= tierPricesUSD.Length) return "-$0.00";
            try { return tierPricesUSD[tierIndex].ToString("C2", CultureInfo.GetCultureInfo(cultureCode)); }
            catch { return tierPricesUSD[tierIndex].ToString("C2", CultureInfo.InvariantCulture); }
        }

        /// <summary>
        /// Normalize phone number to digits and attempt to add leading +country for known regions.
        /// </summary>
        public string NormalizePhoneNumber(string raw, string regionName = null)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            var digits = new System.Text.StringBuilder();
            foreach (var c in raw)
            {
                if (char.IsDigit(c)) digits.Append(c);
                else if (c == '+') digits.Append('+');
            }
            var s = digits.ToString();
            if (s.StartsWith("+")) return s;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Australia", "+61" }, { "AU", "+61" }, { "United States", "+1" }, { "US", "+1" }
            };
            if (!string.IsNullOrEmpty(regionName))
            {
                foreach (var kv in map)
                {
                    if (regionName.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (s.StartsWith("0")) s = s.Substring(1);
                        return kv.Value + s;
                    }
                }
            }
            return s;
        }

        /// <summary>
        /// Returns rocks amount for tier
        /// </summary>
        public int GetRocksForTier(int tierIndex)
        {
            if (tierIndex < 0 || tierIndex >= tierRocks.Length) return 0;
            return tierRocks[tierIndex];
        }

        #endregion
    }
}

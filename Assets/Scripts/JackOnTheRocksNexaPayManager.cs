using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace JackOnTheRocks
{
    /// <summary>Five drink varieties available from the NexaPay menu.</summary>
    public enum DrinkType
    {
        BourbonOnTheRocks,
        WhiskyOnTheRocks,
        VodkaOnTheRocks,
        CognacOnTheRocks,
        RumOnTheRocks
    }

    /// <summary>Lifecycle of an order paid through NexaPay.</summary>
    public enum OrderStatus
    {
        PendingCheckout,
        PaymentProcessing,
        Confirmed,
        OrderDelivered,
        Expired,
        Failed
    }

    /// <summary>Snapchat identity, mobile number, and order-time geolocation.</summary>
    [Serializable]
    public struct SnapchatUserProfile
    {
        /// <summary>Backend-verified Snapchat external identifier.</summary>
        public string snapchatUserId;
        /// <summary>Customer mobile number used for fulfillment communication.</summary>
        public string userMobileNumber;
        /// <summary>Order-time latitude.</summary>
        public float latitude;
        /// <summary>Order-time longitude.</summary>
        public float longitude;
        /// <summary>Human-readable region detected by the backend or geocoder.</summary>
        public string detectedRegion;
    }

    /// <summary>Regional fulfillment and Snapchat Business routing record.</summary>
    [Serializable]
    public class RegionalManager
    {
        /// <summary>Application manager identifier.</summary>
        public string managerId;
        /// <summary>Service-region name.</summary>
        public string regionName;
        /// <summary>Snapchat Business public-profile identifier.</summary>
        public string snapchatBusinessAccountId;
        /// <summary>
        /// Legacy runtime token slot. Never serialize or populate this in a WebGL build; the
        /// backend secret store must provide Snapchat credentials during dispatch.
        /// </summary>
        [NonSerialized] public string snapchatApiAccessToken;
        /// <summary>Service-zone center latitude.</summary>
        public float centerLat;
        /// <summary>Service-zone center longitude.</summary>
        public float centerLong;
        /// <summary>Maximum service radius in kilometres.</summary>
        public float radiusKm = 5f;
        /// <summary>Manager contact number used by the backend SMS integration.</summary>
        public string managerContactPhone;
        /// <summary>Whether this routing record may receive new orders.</summary>
        public bool isActive = true;
    }

    /// <summary>Client-side view of a hosted NexaPay checkout session.</summary>
    [Serializable]
    public struct NexaPayCheckoutSession
    {
        /// <summary>Idempotent application order identifier.</summary>
        public string orderId;
        /// <summary>Selected drink.</summary>
        public DrinkType drink;
        /// <summary>Bundle tier index from zero through three.</summary>
        public int bundleTierIndex;
        /// <summary>Fiat checkout price in USD.</summary>
        public float priceUSD;
        /// <summary>Rocks granted after verified settlement.</summary>
        public int rocksToGrant;
        /// <summary>NexaPay-hosted checkout URL.</summary>
        public string checkoutUrl;
        /// <summary>Configured USDC/USDT settlement destination.</summary>
        public string merchantWalletAddress;
        /// <summary>Customer mobile number.</summary>
        public string userPhone;
        /// <summary>Regional fulfillment manager.</summary>
        public RegionalManager assignedManager;
        /// <summary>Current order status.</summary>
        public OrderStatus status;
    }

    /// <summary>
    /// Persistent singleton for NexaPay checkout creation, regional routing, backend-verified
    /// webhook consumption, Rocks grants, and server-mediated Snapchat customer/staff dispatch.
    /// Provider API keys, webhook secrets, wallet authority, and Snapchat tokens remain on the
    /// backend; serialized key fields are secret-manager references, never raw credentials.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class JackOnTheRocksNexaPayManager : MonoBehaviour
    {
        private const string CustomerConfirmationTemplate =
            "Payment confirmed via NexaPay! Your regional manager is preparing your {0}. Please wait ~5 minutes.";
        private const double EarthMeanRadiusKm = 6371.0d;

        private static readonly float[] TierPricesUsd = { 5f, 10f, 15f, 25f };
        private static readonly int[] TierRocks = { 10, 30, 500, 100 };
        private static readonly int[] TierDrinkCounts = { 1, 3, 5, 10 };

        private static JackOnTheRocksNexaPayManager instance;

        /// <summary>Global NexaPay manager, created on first access.</summary>
        public static JackOnTheRocksNexaPayManager Instance
        {
            get
            {
                if (instance == null)
                {
                    GameObject host = new GameObject(nameof(JackOnTheRocksNexaPayManager));
                    instance = host.AddComponent<JackOnTheRocksNexaPayManager>();
                }
                return instance;
            }
        }

        [Header("Backend NexaPay Proxy")]
        [SerializeField, Tooltip("Leave empty for same-origin WebGL requests; Editor defaults to localhost:3000.")]
        private string backendBaseUrl = string.Empty;
        [SerializeField] private string checkoutProxyPath = "/api/nexapay/checkout";
        [SerializeField] private string verifiedOrderPath = "/api/nexapay/orders/{orderId}";
        [SerializeField] private string staffDispatchPath = "/api/nexapay/orders/{orderId}/dispatch";
        [SerializeField] private string checkoutReturnUrl = string.Empty;
        [SerializeField, Tooltip("Secret-manager key name only. Never paste cg_live_ credentials into Unity.")]
        private string nexaPayMerchantApiKeyReference = "NEXAPAY_API_KEY";

        [Header("Settlement")]
        [SerializeField] private string targetSettlementAsset = "USDC";
        [SerializeField] private string targetMerchantWalletAddress = string.Empty;

        [Header("Regional Manager Database")]
        [SerializeField] private List<RegionalManager> regionalManagers = new List<RegionalManager>();
        [SerializeField] private RegionalManager defaultGlobalManager = new RegionalManager
        {
            managerId = "global",
            regionName = "Global",
            radiusKm = 20037.5f,
            isActive = true
        };
        [SerializeField, Min(0.1f)] private float defaultServiceRadiusKm = 5f;

        [Header("Menu UI State")]
        [SerializeField, Range(0, 4)] private int selectedDrinkIndex;
        [SerializeField, Range(0, 3)] private int selectedTierIndex;

        private readonly Dictionary<string, NexaPayCheckoutSession> orders =
            new Dictionary<string, NexaPayCheckoutSession>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> transactionHashes =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> grantedOrderIds = new HashSet<string>(StringComparer.Ordinal);
        private SnapchatUserProfile? activeUserProfile;
        private string playerBearerToken = string.Empty;
        private int totalRocks;

        /// <summary>Raised after a valid Snapchat profile is loaded.</summary>
        public event Action<SnapchatUserProfile> onUserProfileLoaded;
        /// <summary>Raised after the backend creates a hosted checkout session.</summary>
        public event Action<NexaPayCheckoutSession> onNexaPayCheckoutGenerated;
        /// <summary>Raised whenever an order status changes.</summary>
        public event Action<string, OrderStatus> onOrderStatusUpdated;
        /// <summary>Raised after an idempotent Rocks grant, with the new local total.</summary>
        public event Action<int> onRocksGranted;
        /// <summary>Raised after the backend accepts the Snapchat/SMS staff dispatch.</summary>
        public event Action<string, string> onStaffDispatched;
        /// <summary>Raised for profile, checkout, verification, popup, or dispatch errors.</summary>
        public event Action<string> onNexaPayError;
        /// <summary>Raised when a drink button resolves the current menu selection.</summary>
        public event Action<DrinkType> onDrinkSelected;

        /// <summary>Current local Rocks total maintained by this payment flow.</summary>
        public int TotalRocks => totalRocks;
        /// <summary>Currently selected drink.</summary>
        public DrinkType SelectedDrink => (DrinkType)Mathf.Clamp(selectedDrinkIndex, 0, 4);

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern int JOTR_NexaPay_OpenCheckout(
            string checkoutUrl, string allowedReturnOrigin, string targetObject,
            string returnMethod, string errorMethod);
        [DllImport("__Internal")] private static extern void JOTR_NexaPay_CloseCheckout();
#endif

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);
            JackOnTheRocksManager core = JackOnTheRocksManager.Instance;
            if (core != null) totalRocks = core.totalRocks;
        }

        private void OnDestroy()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            JOTR_NexaPay_CloseCheckout();
#endif
            if (instance == this) instance = null;
        }

        /// <summary>Loads the authenticated player and emits the profile event.</summary>
        public void SetUserProfile(SnapchatUserProfile profile, string bearerToken = "")
        {
            if (string.IsNullOrWhiteSpace(profile.snapchatUserId))
            { ReportError("A backend-verified Snapchat user ID is required."); return; }
            if (!IsPlausiblePhone(profile.userMobileNumber))
            { ReportError("A valid customer mobile number is required."); return; }
            if (!CoordinatesValid(profile.latitude, profile.longitude, true))
            { ReportError("Valid Snapchat geolocation coordinates are required."); return; }
            activeUserProfile = profile;
            playerBearerToken = bearerToken ?? string.Empty;
            onUserProfileLoaded?.Invoke(profile);
        }

        /// <summary>Sets the selected menu drink index from a dropdown or button group.</summary>
        public void SetSelectedDrinkIndex(int index)
        {
            if (index >= 0 && index < 5) selectedDrinkIndex = index;
        }

        /// <summary>Sets the selected price/bundle tier from zero through three.</summary>
        public void SetSelectedTierIndex(int index)
        {
            if (index >= 0 && index < TierPricesUsd.Length) selectedTierIndex = index;
        }

        /// <summary>
        /// Finds the nearest active manager whose service circle contains the player. If no circle
        /// contains the location, a detached copy of the configured global fallback is returned.
        /// </summary>
        public RegionalManager ResolveRegionalManager(float latitude, float longitude)
        {
            if (!CoordinatesValid(latitude, longitude, false)) return CloneManager(defaultGlobalManager);
            RegionalManager nearest = null;
            double nearestDistance = double.PositiveInfinity;
            if (regionalManagers != null)
            {
                for (int i = 0; i < regionalManagers.Count; i++)
                {
                    RegionalManager manager = regionalManagers[i];
                    if (!ManagerCanReceiveOrders(manager)) continue;
                    double distance = HaversineKm(latitude, longitude, manager.centerLat, manager.centerLong);
                    float radius = manager.radiusKm > 0f ? manager.radiusKm : defaultServiceRadiusKm;
                    if (distance <= radius && distance < nearestDistance)
                    {
                        nearest = manager;
                        nearestDistance = distance;
                    }
                }
            }
            return CloneManager(nearest ?? defaultGlobalManager);
        }

        /// <summary>
        /// Starts an asynchronous same-origin backend request that creates a NexaPay payment. The
        /// backend uses its secret key with the documented /api/v1/payments provider endpoint.
        /// </summary>
        public void CreateNexaPayCheckout(DrinkType drink, int tierIndex)
        {
            if (!activeUserProfile.HasValue)
            { ReportError("Load an authenticated Snapchat user profile before checkout."); return; }
            if (!Enum.IsDefined(typeof(DrinkType), drink) || tierIndex < 0 || tierIndex >= TierPricesUsd.Length)
            { ReportError("The selected drink or pricing tier is invalid."); return; }
            SnapchatUserProfile profile = activeUserProfile.Value;
            if (!IsPlausiblePhone(profile.userMobileNumber))
            { ReportError("A valid mobile number is required for checkout."); return; }
            if (string.IsNullOrWhiteSpace(targetMerchantWalletAddress))
            { ReportError("The merchant settlement wallet has not been configured."); return; }
            string settlementAsset = (targetSettlementAsset ?? string.Empty).Trim().ToUpperInvariant();
            if (settlementAsset != "USDC" && settlementAsset != "USDT")
            { ReportError("Settlement asset must be USDC or USDT."); return; }
            RegionalManager manager = ResolveRegionalManager(profile.latitude, profile.longitude);
            if (!ManagerCanReceiveOrders(manager))
            { ReportError("No regional or global fulfillment manager is configured."); return; }

            string orderId = Guid.NewGuid().ToString("N");
            NexaPayCheckoutSession session = new NexaPayCheckoutSession
            {
                orderId = orderId,
                drink = drink,
                bundleTierIndex = tierIndex,
                priceUSD = TierPricesUsd[tierIndex],
                rocksToGrant = TierRocks[tierIndex],
                merchantWalletAddress = targetMerchantWalletAddress,
                userPhone = profile.userMobileNumber,
                assignedManager = manager,
                status = OrderStatus.PendingCheckout
            };
            orders[orderId] = session;
            CheckoutRequestDto request = new CheckoutRequestDto
            {
                amountUSD = session.priceUSD,
                currency = "USD",
                settlementAsset = settlementAsset,
                merchantWalletAddress = targetMerchantWalletAddress,
                merchantApiKeyReference = nexaPayMerchantApiKeyReference,
                orderId = orderId,
                customerPhone = profile.userMobileNumber,
                customerSnapchatId = profile.snapchatUserId,
                returnUrl = ResolveReturnUrl(),
                drink = drink.ToString(),
                bundleTierIndex = tierIndex,
                bundleDrinkCount = TierDrinkCounts[tierIndex],
                assignedManagerId = manager.managerId,
                allowedPaymentMethods = new[] { "visa", "mastercard", "apple_pay", "google_pay" }
            };
            StartCoroutine(CreateCheckoutCoroutine(session, request));
        }

        /// <summary>
        /// Consumes a backend webhook notification signal. The payload itself is never trusted;
        /// the manager extracts only the order ID and fetches server-verified settlement state.
        /// </summary>
        public void OnNexaPayWebhookReceived(string jsonPayload)
        {
            WebhookSignalDto signal;
            try { signal = JsonUtility.FromJson<WebhookSignalDto>(jsonPayload); }
            catch { signal = null; }
            if (signal == null || string.IsNullOrWhiteSpace(signal.orderId) || !orders.ContainsKey(signal.orderId))
            { ReportError("The NexaPay webhook signal did not reference a known order."); return; }
            StartCoroutine(VerifyOrderWithBackend(signal.orderId));
        }

        /// <summary>
        /// Sends fulfillment context to the backend. The backend retrieves Snapchat credentials,
        /// posts to /v1/public_profiles/{profile_id}/messages, and sends the customer confirmation.
        /// </summary>
        public void DispatchOrderToSnapchatAPI(NexaPayCheckoutSession session)
        {
            if (session.assignedManager == null ||
                string.IsNullOrWhiteSpace(session.assignedManager.managerId) ||
                string.IsNullOrWhiteSpace(session.assignedManager.snapchatBusinessAccountId))
            { ReportError("The assigned manager has no Snapchat Business routing credentials."); return; }
            if (!activeUserProfile.HasValue)
            { ReportError("Customer context is unavailable for staff dispatch."); return; }
            string transactionHash;
            if (!transactionHashes.TryGetValue(session.orderId, out transactionHash) ||
                string.IsNullOrWhiteSpace(transactionHash))
            { ReportError("A verified settlement transaction hash is required for dispatch."); return; }

            SnapchatUserProfile profile = activeUserProfile.Value;
            DispatchRequestDto request = new DispatchRequestDto
            {
                orderId = session.orderId,
                managerId = session.assignedManager.managerId,
                managerSnapchatProfileId = session.assignedManager.snapchatBusinessAccountId,
                snapchatEndpoint = "/v1/public_profiles/" +
                    session.assignedManager.snapchatBusinessAccountId + "/messages",
                userSnapchatId = profile.snapchatUserId,
                userMobilePhone = session.userPhone,
                drink = session.drink.ToString(),
                bundleTierIndex = session.bundleTierIndex,
                bundleDrinkCount = TierDrinkCounts[Mathf.Clamp(session.bundleTierIndex, 0, 3)],
                rocksGranted = session.rocksToGrant,
                settledTransactionHash = transactionHash,
                customerMessage = string.Format(CultureInfo.InvariantCulture,
                    CustomerConfirmationTemplate, DisplayDrinkName(session.drink))
            };
            string path = staffDispatchPath.Replace("{orderId}", UnityWebRequest.EscapeURL(session.orderId));
            StartCoroutine(SendJson("POST", BuildUrl(path), JsonUtility.ToJson(request), playerBearerToken,
                (ok, json) =>
                {
                    if (!ok) { ReportError(ReadServerError(json, "Regional staff dispatch failed.")); return; }
                    onStaffDispatched?.Invoke(session.orderId,
                        string.IsNullOrWhiteSpace(session.assignedManager.regionName)
                            ? session.assignedManager.managerId
                            : session.assignedManager.regionName);
                }));
        }

        /// <summary>Unity button helper that confirms the currently selected drink.</summary>
        public void OnSelectDrink()
        {
            selectedDrinkIndex = Mathf.Clamp(selectedDrinkIndex, 0, 4);
            onDrinkSelected?.Invoke((DrinkType)selectedDrinkIndex);
        }

        /// <summary>Unity button helper that creates checkout for the current drink and tier.</summary>
        public void OnPayWithNexaPayClicked()
        {
            CreateNexaPayCheckout((DrinkType)Mathf.Clamp(selectedDrinkIndex, 0, 4),
                Mathf.Clamp(selectedTierIndex, 0, 3));
        }

        /// <summary>
        /// Development-only helper that simulates a backend-verified successful settlement.
        /// Release builds refuse this path.
        /// </summary>
        public void OnSimulateNexaPayWebhook(string orderId)
        {
            if (!Debug.isDebugBuild)
            { ReportError("NexaPay webhook simulation is disabled in release builds."); return; }
            NexaPayCheckoutSession session;
            if (!orders.TryGetValue(orderId ?? string.Empty, out session))
            { ReportError("The simulated order does not exist."); return; }
            VerifiedOrderDto verified = new VerifiedOrderDto
            {
                orderId = orderId,
                status = "confirmed",
                backendVerified = true,
                settledAmount = session.priceUSD,
                settlementAsset = targetSettlementAsset,
                transactionHash = "development-" + Guid.NewGuid().ToString("N")
            };
            ApplyVerifiedOrder(verified);
        }

        /// <summary>WebGL checkout-return callback. It triggers backend verification only.</summary>
        public void OnCheckoutWindowReturned(string json)
        {
            WebhookSignalDto signal;
            try { signal = JsonUtility.FromJson<WebhookSignalDto>(json); }
            catch { signal = null; }
            if (signal != null && !string.IsNullOrWhiteSpace(signal.orderId) && orders.ContainsKey(signal.orderId))
                StartCoroutine(VerifyOrderWithBackend(signal.orderId));
        }

        /// <summary>WebGL popup error callback.</summary>
        public void OnCheckoutWindowError(string error)
        {
            ReportError(string.IsNullOrWhiteSpace(error) ? "NexaPay checkout could not be opened." : error);
        }

        private IEnumerator CreateCheckoutCoroutine(NexaPayCheckoutSession session, CheckoutRequestDto payload)
        {
            bool successful = false;
            string response = null;
            yield return SendJson("POST", BuildUrl(checkoutProxyPath), JsonUtility.ToJson(payload),
                playerBearerToken, (ok, json) => { successful = ok; response = json; });
            if (!successful)
            {
                SetOrderStatus(session.orderId, OrderStatus.Failed);
                ReportError(ReadServerError(response, "NexaPay checkout creation failed."));
                yield break;
            }
            CheckoutResponseDto checkout;
            try { checkout = JsonUtility.FromJson<CheckoutResponseDto>(response); }
            catch { checkout = null; }
            if (checkout == null || !checkout.success || checkout.orderId != session.orderId ||
                !IsSecureCheckoutUrl(checkout.checkoutUrl))
            {
                SetOrderStatus(session.orderId, OrderStatus.Failed);
                ReportError("The backend returned an invalid NexaPay checkout session.");
                yield break;
            }
            session.checkoutUrl = checkout.checkoutUrl;
            session.status = OrderStatus.PendingCheckout;
            orders[session.orderId] = session;
            onNexaPayCheckoutGenerated?.Invoke(session);
            onOrderStatusUpdated?.Invoke(session.orderId, session.status);
            OpenCheckout(session);
        }

        private void OpenCheckout(NexaPayCheckoutSession session)
        {
            SetOrderStatus(session.orderId, OrderStatus.PaymentProcessing);
#if UNITY_WEBGL && !UNITY_EDITOR
            int opened = JOTR_NexaPay_OpenCheckout(session.checkoutUrl, GetOrigin(ResolveReturnUrl()),
                gameObject.name, nameof(OnCheckoutWindowReturned), nameof(OnCheckoutWindowError));
            if (opened == 0)
            {
                SetOrderStatus(session.orderId, OrderStatus.Failed);
                ReportError("Allow pop-ups to continue to NexaPay checkout.");
            }
#else
            Application.OpenURL(session.checkoutUrl);
#endif
        }

        private IEnumerator VerifyOrderWithBackend(string orderId)
        {
            string path = verifiedOrderPath.Replace("{orderId}", UnityWebRequest.EscapeURL(orderId));
            bool successful = false;
            string response = null;
            yield return SendJson("GET", BuildUrl(path), null, playerBearerToken,
                (ok, json) => { successful = ok; response = json; });
            if (!successful)
            { ReportError(ReadServerError(response, "NexaPay settlement could not be verified.")); yield break; }
            VerifiedOrderDto verified;
            try { verified = JsonUtility.FromJson<VerifiedOrderDto>(response); }
            catch { verified = null; }
            if (verified == null || !verified.backendVerified || verified.orderId != orderId)
            { ReportError("The backend returned an invalid settlement verification."); yield break; }
            ApplyVerifiedOrder(verified);
        }

        private void ApplyVerifiedOrder(VerifiedOrderDto verified)
        {
            NexaPayCheckoutSession session;
            if (verified == null || !orders.TryGetValue(verified.orderId ?? string.Empty, out session)) return;
            string state = (verified.status ?? string.Empty).Trim().ToLowerInvariant();
            if (state == "expired") { SetOrderStatus(session.orderId, OrderStatus.Expired); return; }
            if (state == "failed" || state == "cancelled")
            { SetOrderStatus(session.orderId, OrderStatus.Failed); return; }
            if (state != "confirmed" && state != "paid" && state != "completed") return;
            if (!verified.backendVerified || verified.settledAmount <= 0d ||
                string.IsNullOrWhiteSpace(verified.transactionHash))
            { ReportError("Confirmed settlement data was incomplete."); return; }
            if (!string.Equals(verified.settlementAsset, targetSettlementAsset,
                StringComparison.OrdinalIgnoreCase))
            { ReportError("The settled crypto asset did not match this order."); return; }

            transactionHashes[session.orderId] = verified.transactionHash;
            SetOrderStatus(session.orderId, OrderStatus.Confirmed);
            if (grantedOrderIds.Add(session.orderId)) GrantRocks(session.rocksToGrant);
            DispatchOrderToSnapchatAPI(orders[session.orderId]);
#if UNITY_WEBGL && !UNITY_EDITOR
            JOTR_NexaPay_CloseCheckout();
#endif
        }

        private void GrantRocks(int amount)
        {
            if (amount <= 0) return;
            totalRocks += amount;
            JackOnTheRocksManager core = JackOnTheRocksManager.Instance;
            if (core != null)
            {
                int target = core.totalRocks + amount;
                core.SetBalances(target, core.totalDiamonds, core.currentBet);
                totalRocks = target;
            }
            onRocksGranted?.Invoke(totalRocks);
        }

        private void SetOrderStatus(string orderId, OrderStatus status)
        {
            NexaPayCheckoutSession session;
            if (!orders.TryGetValue(orderId, out session)) return;
            session.status = status;
            orders[orderId] = session;
            onOrderStatusUpdated?.Invoke(orderId, status);
        }

        private IEnumerator SendJson(string method, string url, string json, string bearerToken,
            Action<bool, string> callback)
        {
            using (UnityWebRequest request = new UnityWebRequest(url, method))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                if (json != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                    request.SetRequestHeader("Content-Type", "application/json");
                }
                request.SetRequestHeader("Accept", "application/json");
                request.SetRequestHeader("Idempotency-Key", ExtractIdempotencyKey(json));
                if (!string.IsNullOrWhiteSpace(bearerToken))
                    request.SetRequestHeader("Authorization", "Bearer " + bearerToken);
                yield return request.SendWebRequest();
                callback?.Invoke(request.result == UnityWebRequest.Result.Success,
                    request.downloadHandler?.text);
            }
        }

        private string ResolveReturnUrl()
        {
            if (!string.IsNullOrWhiteSpace(checkoutReturnUrl)) return checkoutReturnUrl;
            string origin = GetOrigin(Application.absoluteURL);
            return string.IsNullOrWhiteSpace(origin) ? BuildUrl("/nexapay/return") : origin + "/nexapay/return";
        }

        private string BuildUrl(string path)
        {
            Uri absolute;
            if (Uri.TryCreate(path, UriKind.Absolute, out absolute)) return absolute.ToString();
            string origin = backendBaseUrl?.Trim();
            if (string.IsNullOrWhiteSpace(origin))
            {
#if UNITY_EDITOR
                origin = "http://localhost:3000";
#else
                origin = GetOrigin(Application.absoluteURL);
#endif
            }
            return origin.TrimEnd('/') + "/" + (path ?? string.Empty).TrimStart('/');
        }

        private static string GetOrigin(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri)
                ? uri.GetLeftPart(UriPartial.Authority)
                : string.Empty;
        }

        private static bool IsSecureCheckoutUrl(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                   string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                   (string.Equals(uri.Host, "nexapay.one", StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.EndsWith(".nexapay.one", StringComparison.OrdinalIgnoreCase));
        }

        private static bool ManagerCanReceiveOrders(RegionalManager manager)
        {
            return manager != null && manager.isActive && !string.IsNullOrWhiteSpace(manager.managerId) &&
                   !string.IsNullOrWhiteSpace(manager.snapchatBusinessAccountId) &&
                   !string.IsNullOrWhiteSpace(manager.managerContactPhone);
        }

        private static RegionalManager CloneManager(RegionalManager value)
        {
            if (value == null) return null;
            return new RegionalManager
            {
                managerId = value.managerId,
                regionName = value.regionName,
                snapchatBusinessAccountId = value.snapchatBusinessAccountId,
                centerLat = value.centerLat,
                centerLong = value.centerLong,
                radiusKm = value.radiusKm,
                managerContactPhone = value.managerContactPhone,
                isActive = value.isActive
            };
        }

        private static bool CoordinatesValid(float latitude, float longitude, bool rejectNullIsland)
        {
            if (float.IsNaN(latitude) || float.IsNaN(longitude) || float.IsInfinity(latitude) ||
                float.IsInfinity(longitude) || latitude < -90f || latitude > 90f ||
                longitude < -180f || longitude > 180f) return false;
            return !rejectNullIsland || Math.Abs(latitude) > float.Epsilon || Math.Abs(longitude) > float.Epsilon;
        }

        private static double HaversineKm(double latitude1, double longitude1,
            double latitude2, double longitude2)
        {
            double phi1 = latitude1 * Math.PI / 180d;
            double phi2 = latitude2 * Math.PI / 180d;
            double deltaPhi = (latitude2 - latitude1) * Math.PI / 180d;
            double deltaLambda = (longitude2 - longitude1) * Math.PI / 180d;
            double sinPhi = Math.Sin(deltaPhi / 2d);
            double sinLambda = Math.Sin(deltaLambda / 2d);
            double a = sinPhi * sinPhi + Math.Cos(phi1) * Math.Cos(phi2) * sinLambda * sinLambda;
            a = Math.Max(0d, Math.Min(1d, a));
            return 2d * EarthMeanRadiusKm * Math.Asin(Math.Sqrt(a));
        }

        private static bool IsPlausiblePhone(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            int digits = 0;
            foreach (char character in value)
            {
                if (char.IsDigit(character)) digits++;
                else if (character != '+' && character != ' ' && character != '-' &&
                         character != '(' && character != ')') return false;
            }
            return digits >= 8 && digits <= 15;
        }

        private static string DisplayDrinkName(DrinkType drink)
        {
            switch (drink)
            {
                case DrinkType.BourbonOnTheRocks: return "Bourbon on the Rocks";
                case DrinkType.WhiskyOnTheRocks: return "Whisky on the Rocks";
                case DrinkType.VodkaOnTheRocks: return "Vodka on the Rocks";
                case DrinkType.CognacOnTheRocks: return "Cognac on the Rocks";
                case DrinkType.RumOnTheRocks: return "Rum on the Rocks";
                default: return drink.ToString();
            }
        }

        private static string ExtractIdempotencyKey(string json)
        {
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    IdempotencyDto value = JsonUtility.FromJson<IdempotencyDto>(json);
                    if (!string.IsNullOrWhiteSpace(value?.orderId)) return value.orderId;
                }
                catch { }
            }
            return Guid.NewGuid().ToString("N");
        }

        private void ReportError(string message)
        {
            Debug.LogWarning("NexaPay: " + message);
            onNexaPayError?.Invoke(message);
        }

        private static string ReadServerError(string json, string fallback)
        {
            try
            {
                ErrorDto error = JsonUtility.FromJson<ErrorDto>(json);
                if (!string.IsNullOrWhiteSpace(error?.error)) return error.error;
                if (!string.IsNullOrWhiteSpace(error?.message)) return error.message;
            }
            catch { }
            return fallback;
        }

        [Serializable] private class ErrorDto { public string error; public string message; }
        [Serializable] private class IdempotencyDto { public string orderId; }
        [Serializable] private class WebhookSignalDto { public string orderId; }
        [Serializable]
        private class CheckoutRequestDto
        {
            public float amountUSD;
            public string currency;
            public string settlementAsset;
            public string merchantWalletAddress;
            public string merchantApiKeyReference;
            public string orderId;
            public string customerPhone;
            public string customerSnapchatId;
            public string returnUrl;
            public string drink;
            public int bundleTierIndex;
            public int bundleDrinkCount;
            public string assignedManagerId;
            public string[] allowedPaymentMethods;
        }
        [Serializable]
        private class CheckoutResponseDto
        {
            public bool success;
            public string orderId;
            public string checkoutUrl;
        }
        [Serializable]
        private class VerifiedOrderDto
        {
            public string orderId;
            public string status;
            public bool backendVerified;
            public double settledAmount;
            public string settlementAsset;
            public string transactionHash;
        }
        [Serializable]
        private class DispatchRequestDto
        {
            public string orderId;
            public string managerId;
            public string managerSnapchatProfileId;
            public string snapchatEndpoint;
            public string userSnapchatId;
            public string userMobilePhone;
            public string drink;
            public int bundleTierIndex;
            public int bundleDrinkCount;
            public int rocksGranted;
            public string settledTransactionHash;
            public string customerMessage;
        }
    }
}

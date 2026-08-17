using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using PaymentDrinkOrder = JackOnTheRocks.JackOnTheRocksPaymentManager.DrinkOrder;
using PaymentDrinkType = JackOnTheRocks.JackOnTheRocksPaymentManager.DrinkType;
using PaymentOrderStatus = JackOnTheRocks.JackOnTheRocksPaymentManager.OrderStatus;
using PaymentRegionalManager = JackOnTheRocks.JackOnTheRocksPaymentManager.RegionalManager;
using PaymentSnapchatUserProfile = JackOnTheRocks.JackOnTheRocksPaymentManager.SnapchatUserProfile;

namespace JackOnTheRocks
{
    /// <summary>Reasons why a spatial order dispatch could not be completed.</summary>
    public enum DispatchFailureReason
    {
        NoStaffInRadius,
        AllStaffOffline,
        LocationPermissionDenied,
        OrderRejectedByManager
    }

    /// <summary>Distance and coverage result for one staff member.</summary>
    [Serializable]
    public struct StaffProximityMatch
    {
        /// <summary>The evaluated staff account.</summary>
        public StaffMember staff;
        /// <summary>Great-circle distance from the player in kilometres.</summary>
        public float distanceKm;
        /// <summary>Whether the player falls inside this staff member's configured radius.</summary>
        public bool isWithinServiceRadius;
    }

    /// <summary>Complete assignment context emitted after a successful backend dispatch.</summary>
    [Serializable]
    public struct MatchedOrderPayload
    {
        /// <summary>The drink order being routed.</summary>
        public PaymentDrinkOrder orderDetails;
        /// <summary>The player profile and order-time coordinates.</summary>
        public PaymentSnapchatUserProfile playerProfile;
        /// <summary>The waiter, area manager, or configured global administrator assigned.</summary>
        public StaffMember assignedStaff;
        /// <summary>The calculated player-to-staff distance in kilometres.</summary>
        public float calculatedDistanceKm;
        /// <summary>UTC timestamp at which the assignment was created.</summary>
        public DateTime assignmentTimestamp;
    }

    /// <summary>Aggregate online-staff counts around a location.</summary>
    [Serializable]
    public struct CoverageMetrics
    {
        /// <summary>Number of online staff at most one kilometre away.</summary>
        public int within1Km;
        /// <summary>Number of online staff at most five kilometres away.</summary>
        public int within5Km;
        /// <summary>Number of online staff at most ten kilometres away.</summary>
        public int within10Km;
        /// <summary>Total number of active staff with a fresh location ping.</summary>
        public int totalOnlineStaff;
    }

    /// <summary>
    /// Persistent singleton that ranks fresh staff locations using exact spherical distance,
    /// enforces each candidate's service radius, and asks a trusted backend to dispatch
    /// Snapchat and SMS notifications. Tokens and provider credentials never enter WebGL.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class JackOnTheRocksSpatialOrderMatcher : MonoBehaviour
    {
        private const double EarthMeanRadiusKm = 6371.0d;
        private const float DefaultServiceRadiusKm = 5.0f;
        private static readonly TimeSpan FreshLocationWindow = TimeSpan.FromMinutes(5);

        private static JackOnTheRocksSpatialOrderMatcher instance;

        /// <summary>
        /// Global matcher instance. A persistent GameObject is created on first access.
        /// Access this property from Unity's main thread.
        /// </summary>
        public static JackOnTheRocksSpatialOrderMatcher Instance
        {
            get
            {
                if (instance == null)
                {
                    GameObject host = new GameObject(nameof(JackOnTheRocksSpatialOrderMatcher));
                    instance = host.AddComponent<JackOnTheRocksSpatialOrderMatcher>();
                }
                return instance;
            }
        }

        [Header("Backend Dispatch")]
        [SerializeField, Tooltip("Leave empty to use the WebGL page origin; Editor defaults to localhost:3000.")]
        private string backendBaseUrl = string.Empty;
        [SerializeField] private string dispatchPath = "/api/orders/spatial-dispatch";
        [SerializeField, Min(1f)] private float dispatchTimeoutSeconds = 20f;

        [Header("Failover")]
        [SerializeField, Tooltip("Optional backend-managed global admin used only when no local staff is in range.")]
        private StaffMember defaultGlobalSystemAdmin;
        [SerializeField] private bool useGlobalAdminFailover;

        [Header("Development Testing")]
        [SerializeField] private int simulatedBundleTierIndex;

        private readonly object staffPoolLock = new object();
        private readonly object matchedOrdersLock = new object();
        private readonly List<StaffMember> activeStaffPool = new List<StaffMember>();
        private readonly Dictionary<string, MatchedOrderPayload> matchedOrders =
            new Dictionary<string, MatchedOrderPayload>(StringComparer.Ordinal);
        private string orderBearerToken = string.Empty;
        private JackOnTheRocksStaffManager observedStaffManager;

        /// <summary>Raised only after the backend accepts the Snapchat/SMS dispatch.</summary>
        public event Action<MatchedOrderPayload> onOrderSuccessfullyMatched;
        /// <summary>Raised when matching, failover, reassignment, or dispatch cannot complete.</summary>
        public event Action<PaymentDrinkOrder, DispatchFailureReason, string> onOrderMatchingFailed;
        /// <summary>Raised with the sorted proximity audit produced by the latest search.</summary>
        public event Action<List<StaffProximityMatch>> onProximityListUpdated;

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            TryAttachStaffManager();
        }

        private void OnDestroy()
        {
            if (observedStaffManager != null)
                observedStaffManager.onMapMarkersRefreshed -= HandleStaffSnapshotUpdated;
            if (instance == this) instance = null;
        }

        /// <summary>
        /// Sets the short-lived order session token returned by the backend. The token remains
        /// in memory and should have permission only to submit and reassign the current user's orders.
        /// </summary>
        public void SetOrderBearerToken(string token)
        {
            orderBearerToken = token ?? string.Empty;
        }

        /// <summary>
        /// Replaces the spatial candidate pool with a detached, null-filtered snapshot.
        /// This is useful when staff locations arrive through a websocket or polling service.
        /// </summary>
        public void SetActiveStaffPool(IEnumerable<StaffMember> staff)
        {
            lock (staffPoolLock)
            {
                activeStaffPool.Clear();
                if (staff == null) return;
                foreach (StaffMember member in staff)
                    if (member != null) activeStaffPool.Add(member);
            }
        }

        /// <summary>
        /// Calculates the great-circle distance using the Haversine formula and Earth's mean
        /// radius of 6371.0 km. The method is pure, allocation-free, and thread-safe.
        /// </summary>
        /// <param name="lat1">First latitude in degrees.</param>
        /// <param name="lon1">First longitude in degrees.</param>
        /// <param name="lat2">Second latitude in degrees.</param>
        /// <param name="lng2">Second longitude in degrees.</param>
        /// <returns>Distance in kilometres, or positive infinity for invalid coordinates.</returns>
        public static float CalculateHaversineDistance(float lat1, float lon1, float lat2, float lng2)
        {
            if (!AreCoordinatesValid(lat1, lon1, false) || !AreCoordinatesValid(lat2, lng2, false))
                return float.PositiveInfinity;

            double phi1 = DegreesToRadians(lat1);
            double phi2 = DegreesToRadians(lat2);
            double deltaPhi = DegreesToRadians(lat2 - lat1);
            double deltaLambda = DegreesToRadians(lng2 - lon1);
            double sinPhi = Math.Sin(deltaPhi * 0.5d);
            double sinLambda = Math.Sin(deltaLambda * 0.5d);
            double haversine = sinPhi * sinPhi +
                               Math.Cos(phi1) * Math.Cos(phi2) * sinLambda * sinLambda;
            // Floating-point rounding close to antipodal points can move the value outside [0,1].
            haversine = Math.Max(0d, Math.Min(1d, haversine));
            double distance = 2d * EarthMeanRadiusKm * Math.Asin(Math.Sqrt(haversine));
            return (float)distance;
        }

        /// <summary>
        /// Filters Active staff to location pings less than five minutes old, calculates every
        /// spherical distance, sorts ascending, emits the audit list, and returns the nearest
        /// candidate whose distance is within their configured radius.
        /// </summary>
        /// <param name="playerLat">Player latitude in degrees.</param>
        /// <param name="playerLng">Player longitude in degrees.</param>
        /// <param name="staffPool">Candidate staff snapshot; null entries are ignored.</param>
        /// <returns>The nearest valid match, or null when none is eligible and in range.</returns>
        public StaffProximityMatch? FindNearestStaffMember(
            float playerLat, float playerLng, List<StaffMember> staffPool)
        {
            List<StaffProximityMatch> matches = BuildSortedProximityList(playerLat, playerLng, staffPool);
            onProximityListUpdated?.Invoke(new List<StaffProximityMatch>(matches));
            for (int i = 0; i < matches.Count; i++)
                if (matches[i].isWithinServiceRadius) return matches[i];
            return null;
        }

        /// <summary>
        /// Matches an order against the current staff snapshot and dispatches it to the nearest
        /// eligible staff member. Invalid player coordinates fail as a location-permission error.
        /// </summary>
        public void ProcessDrinkOrderMatch(PaymentDrinkOrder order, PaymentSnapchatUserProfile player)
        {
            TryAttachStaffManager();
            if (string.IsNullOrWhiteSpace(order.orderId))
            {
                Fail(order, DispatchFailureReason.OrderRejectedByManager, "The order has no valid identifier.");
                return;
            }
            if (!AreCoordinatesValid(player.latitude, player.longitude, true))
            {
                Fail(order, DispatchFailureReason.LocationPermissionDenied,
                    "Location permission is required before a drink order can be matched.");
                return;
            }

            List<StaffMember> snapshot = GetStaffSnapshot();
            StaffProximityMatch? nearest = FindNearestStaffMember(player.latitude, player.longitude, snapshot);
            if (nearest.HasValue)
            {
                AssignAndDispatch(order, player, nearest.Value.staff, nearest.Value.distanceKm);
                return;
            }

            bool anyFreshActive = HasFreshActiveStaff(snapshot);
            if (useGlobalAdminFailover && IsGlobalAdminConfigured(defaultGlobalSystemAdmin))
            {
                float distance = CalculateHaversineDistance(player.latitude, player.longitude,
                    defaultGlobalSystemAdmin.currentLatitude, defaultGlobalSystemAdmin.currentLongitude);
                AssignAndDispatch(order, player, defaultGlobalSystemAdmin, distance);
                return;
            }

            if (!anyFreshActive)
            {
                Fail(order, DispatchFailureReason.AllStaffOffline,
                    "All waiters and area managers are currently offline. Please try again shortly.");
                return;
            }

            Fail(order, DispatchFailureReason.NoStaffInRadius,
                "No waiter or area manager is currently within 5km of your location.");
        }

        /// <summary>
        /// Returns cumulative counts of Active, fresh-location staff within one, five, and ten
        /// kilometres of a point. Counts are independent of each staff member's service radius.
        /// </summary>
        public CoverageMetrics GetCoverageMetrics(float playerLat, float playerLng)
        {
            CoverageMetrics metrics = new CoverageMetrics();
            if (!AreCoordinatesValid(playerLat, playerLng, false)) return metrics;

            List<StaffMember> snapshot = GetStaffSnapshot();
            DateTime now = DateTime.UtcNow;
            for (int i = 0; i < snapshot.Count; i++)
            {
                StaffMember staff = snapshot[i];
                if (!IsFreshActiveStaff(staff, now)) continue;
                float distance = CalculateHaversineDistance(playerLat, playerLng,
                    staff.currentLatitude, staff.currentLongitude);
                if (float.IsInfinity(distance)) continue;
                metrics.totalOnlineStaff++;
                if (distance <= 10f) metrics.within10Km++;
                if (distance <= 5f) metrics.within5Km++;
                if (distance <= 1f) metrics.within1Km++;
            }
            return metrics;
        }

        /// <summary>
        /// Development UI hook that constructs and processes a one-drink test order at the
        /// supplied coordinates. It is disabled in non-development players.
        /// </summary>
        public void OnSimulateOrderMatch(int drinkIndex, float testLat, float testLng)
        {
            if (!Debug.isDebugBuild)
            {
                Debug.LogWarning("Spatial order simulation is disabled in release builds.");
                return;
            }
            if (!Enum.IsDefined(typeof(PaymentDrinkType), drinkIndex))
            {
                Debug.LogWarning("Invalid simulated drink index: " + drinkIndex);
                return;
            }

            int tier = Mathf.Clamp(simulatedBundleTierIndex, 0, 3);
            float[] prices = { 5f, 10f, 15f, 25f };
            int[] rocks = { 100, 300, 500, 1000 };
            PaymentSnapchatUserProfile player = new PaymentSnapchatUserProfile
            {
                snapchatUserId = "development-spatial-test",
                userMobileNumber = "development-only",
                latitude = testLat,
                longitude = testLng,
                detectedRegion = "Spatial Test"
            };
            PaymentDrinkOrder order = new PaymentDrinkOrder
            {
                orderId = "spatial-test-" + Guid.NewGuid().ToString("N"),
                drink = (PaymentDrinkType)drinkIndex,
                bundleTierIndex = tier,
                priceUSD = prices[tier],
                rocksToGrant = rocks[tier],
                revolutPayIDHandle = "@blackjackrocks",
                userPhone = player.userMobileNumber,
                targetRegion = player.detectedRegion,
                status = PaymentOrderStatus.PendingUserPayment
            };
            ProcessDrinkOrderMatch(order, player);
        }

        /// <summary>
        /// Admin UI hook that force-reassigns a previously matched order to a specific Active
        /// staff member. Radius is intentionally bypassed, but freshness and Active status are not.
        /// </summary>
        public void OnForceReassignOrder(string orderId, string targetStaffId)
        {
            if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(targetStaffId)) return;

            MatchedOrderPayload existing;
            lock (matchedOrdersLock)
            {
                if (!matchedOrders.TryGetValue(orderId, out existing))
                {
                    Debug.LogWarning("Cannot reassign unknown spatial order: " + orderId);
                    return;
                }
            }

            StaffMember target = null;
            List<StaffMember> snapshot = GetStaffSnapshot();
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (snapshot[i] != null && string.Equals(snapshot[i].staffId, targetStaffId, StringComparison.Ordinal))
                {
                    target = snapshot[i];
                    break;
                }
            }

            if (!IsFreshActiveStaff(target, DateTime.UtcNow))
            {
                Fail(existing.orderDetails, DispatchFailureReason.AllStaffOffline,
                    "The selected staff member is not Active with a fresh location.");
                return;
            }

            float distance = CalculateHaversineDistance(existing.playerProfile.latitude,
                existing.playerProfile.longitude, target.currentLatitude, target.currentLongitude);
            AssignAndDispatch(existing.orderDetails, existing.playerProfile, target, distance);
        }

        /// <summary>
        /// Sends a completed match to the backend dispatch endpoint. The backend is responsible
        /// for authorization, idempotency, Snapchat delivery, SMS delivery, and audit logging.
        /// </summary>
        public void DispatchOrderToSnapchatAPI(MatchedOrderPayload match)
        {
            StartCoroutine(DispatchOrderCoroutine(match));
        }

        private void AssignAndDispatch(PaymentDrinkOrder order, PaymentSnapchatUserProfile player,
            StaffMember staff, float distanceKm)
        {
            order.assignedStaff = staff;
            order.assignedManager = BuildLegacyManagerAdapter(staff);
            order.targetRegion = string.IsNullOrWhiteSpace(staff.assignedRegionId)
                ? order.targetRegion
                : staff.assignedRegionId;

            MatchedOrderPayload match = new MatchedOrderPayload
            {
                orderDetails = order,
                playerProfile = player,
                assignedStaff = staff,
                calculatedDistanceKm = distanceKm,
                assignmentTimestamp = DateTime.UtcNow
            };
            lock (matchedOrdersLock) matchedOrders[order.orderId] = match;
            DispatchOrderToSnapchatAPI(match);
        }

        private IEnumerator DispatchOrderCoroutine(MatchedOrderPayload match)
        {
            DispatchRequestDto payload = DispatchRequestDto.FromMatch(match);
            string json = JsonUtility.ToJson(payload);
            string url = BuildUrl(dispatchPath);
            using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = Mathf.Max(1, Mathf.RoundToInt(dispatchTimeoutSeconds));
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Accept", "application/json");
                request.SetRequestHeader("Idempotency-Key", match.orderDetails.orderId + ":" + match.assignedStaff.staffId);
                if (!string.IsNullOrWhiteSpace(orderBearerToken))
                    request.SetRequestHeader("Authorization", "Bearer " + orderBearerToken);

                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    string message = ReadServerError(request.downloadHandler?.text,
                        "The selected staff member could not accept the order dispatch.");
                    Fail(match.orderDetails, DispatchFailureReason.OrderRejectedByManager, message);
                    yield break;
                }
            }
            onOrderSuccessfullyMatched?.Invoke(match);
        }

        private List<StaffProximityMatch> BuildSortedProximityList(
            float playerLat, float playerLng, List<StaffMember> staffPool)
        {
            List<StaffProximityMatch> matches = new List<StaffProximityMatch>();
            if (!AreCoordinatesValid(playerLat, playerLng, false) || staffPool == null) return matches;
            StaffMember[] snapshot;
            lock (staffPool)
            {
                snapshot = staffPool.ToArray();
            }

            DateTime now = DateTime.UtcNow;
            for (int i = 0; i < snapshot.Length; i++)
            {
                StaffMember staff = snapshot[i];
                if (!IsFreshActiveStaff(staff, now)) continue;
                float distance = CalculateHaversineDistance(playerLat, playerLng,
                    staff.currentLatitude, staff.currentLongitude);
                if (float.IsInfinity(distance) || float.IsNaN(distance)) continue;
                float radius = staff.serviceRadiusKm > 0f ? staff.serviceRadiusKm : DefaultServiceRadiusKm;
                matches.Add(new StaffProximityMatch
                {
                    staff = staff,
                    distanceKm = distance,
                    isWithinServiceRadius = distance <= radius
                });
            }
            matches.Sort((left, right) => left.distanceKm.CompareTo(right.distanceKm));
            return matches;
        }

        private List<StaffMember> GetStaffSnapshot()
        {
            lock (staffPoolLock) return new List<StaffMember>(activeStaffPool);
        }

        private void TryAttachStaffManager()
        {
            JackOnTheRocksStaffManager manager = JackOnTheRocksStaffManager.Instance;
            if (manager == null || manager == observedStaffManager) return;
            if (observedStaffManager != null)
                observedStaffManager.onMapMarkersRefreshed -= HandleStaffSnapshotUpdated;
            observedStaffManager = manager;
            observedStaffManager.onMapMarkersRefreshed += HandleStaffSnapshotUpdated;
            SetActiveStaffPool(observedStaffManager.GetStaffSnapshot());
        }

        private void HandleStaffSnapshotUpdated(List<StaffMember> staff)
        {
            SetActiveStaffPool(staff);
        }

        private static bool HasFreshActiveStaff(List<StaffMember> staff)
        {
            if (staff == null) return false;
            DateTime now = DateTime.UtcNow;
            for (int i = 0; i < staff.Count; i++)
                if (IsFreshActiveStaff(staff[i], now)) return true;
            return false;
        }

        private static bool IsFreshActiveStaff(StaffMember staff, DateTime nowUtc)
        {
            if (staff == null || staff.status != OnboardingStatus.Active ||
                !staff.isLocationPermissionGranted || staff.lastLocationPing == default(DateTime) ||
                !AreCoordinatesValid(staff.currentLatitude, staff.currentLongitude, false)) return false;
            DateTime pingUtc = staff.lastLocationPing.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(staff.lastLocationPing, DateTimeKind.Utc)
                : staff.lastLocationPing.ToUniversalTime();
            TimeSpan age = nowUtc - pingUtc;
            return age >= TimeSpan.Zero && age < FreshLocationWindow;
        }

        private static bool IsGlobalAdminConfigured(StaffMember staff)
        {
            return staff != null && staff.role == StaffRole.Admin &&
                   staff.status == OnboardingStatus.Active && !string.IsNullOrWhiteSpace(staff.staffId);
        }

        private static PaymentRegionalManager BuildLegacyManagerAdapter(StaffMember staff)
        {
            return new PaymentRegionalManager
            {
                managerId = staff.staffId,
                regionName = staff.assignedRegionId ?? string.Empty,
                snapchatBusinessAccountId = staff.snapchatUserId ?? string.Empty,
                // Deliberately empty: WebGL must never receive provider access tokens.
                snapchatApiAccessToken = string.Empty,
                centerLat = staff.serviceCentreLatitude,
                centerLong = staff.serviceCentreLongitude,
                radiusKm = staff.serviceRadiusKm > 0f ? staff.serviceRadiusKm : DefaultServiceRadiusKm,
                managerContactPhone = staff.mobileNumber ?? string.Empty,
                isActive = staff.status == OnboardingStatus.Active
            };
        }

        private void Fail(PaymentDrinkOrder order, DispatchFailureReason reason, string message)
        {
            onOrderMatchingFailed?.Invoke(order, reason, message);
        }

        private string BuildUrl(string path)
        {
            Uri absolute;
            if (Uri.TryCreate(path, UriKind.Absolute, out absolute)) return absolute.ToString();
            string origin = backendBaseUrl?.Trim();
            if (string.IsNullOrEmpty(origin))
            {
#if UNITY_EDITOR
                origin = "http://localhost:3000";
#else
                Uri page;
                origin = Uri.TryCreate(Application.absoluteURL, UriKind.Absolute, out page)
                    ? page.GetLeftPart(UriPartial.Authority)
                    : string.Empty;
#endif
            }
            return origin.TrimEnd('/') + "/" + (path ?? string.Empty).TrimStart('/');
        }

        private static bool AreCoordinatesValid(float latitude, float longitude, bool rejectNullIsland)
        {
            if (float.IsNaN(latitude) || float.IsNaN(longitude) ||
                float.IsInfinity(latitude) || float.IsInfinity(longitude) ||
                latitude < -90f || latitude > 90f || longitude < -180f || longitude > 180f) return false;
            return !rejectNullIsland || Math.Abs(latitude) > float.Epsilon || Math.Abs(longitude) > float.Epsilon;
        }

        private static double DegreesToRadians(double degrees) { return degrees * Math.PI / 180d; }

        private static string ReadServerError(string json, string fallback)
        {
            if (string.IsNullOrWhiteSpace(json)) return fallback;
            try
            {
                ErrorDto error = JsonUtility.FromJson<ErrorDto>(json);
                if (!string.IsNullOrWhiteSpace(error?.error)) return error.error;
                if (!string.IsNullOrWhiteSpace(error?.message)) return error.message;
            }
            catch { }
            return fallback;
        }

        [Serializable]
        private class ErrorDto { public string error; public string message; }

        [Serializable]
        private class DispatchRequestDto
        {
            public string orderId;
            public string drink;
            public int bundleTierIndex;
            public float priceUSD;
            public string playerSnapchatUserId;
            public string playerMobileNumber;
            public float playerLatitude;
            public float playerLongitude;
            public string assignedStaffId;
            public string assignedStaffRole;
            public float calculatedDistanceKm;
            public string assignmentTimestampUtc;
            public string[] channels;

            public static DispatchRequestDto FromMatch(MatchedOrderPayload match)
            {
                return new DispatchRequestDto
                {
                    orderId = match.orderDetails.orderId,
                    drink = match.orderDetails.drink.ToString(),
                    bundleTierIndex = match.orderDetails.bundleTierIndex,
                    priceUSD = match.orderDetails.priceUSD,
                    playerSnapchatUserId = match.playerProfile.snapchatUserId,
                    playerMobileNumber = match.playerProfile.userMobileNumber,
                    playerLatitude = match.playerProfile.latitude,
                    playerLongitude = match.playerProfile.longitude,
                    assignedStaffId = match.assignedStaff.staffId,
                    assignedStaffRole = match.assignedStaff.role.ToString(),
                    calculatedDistanceKm = match.calculatedDistanceKm,
                    assignmentTimestampUtc = match.assignmentTimestamp.ToString("O", CultureInfo.InvariantCulture),
                    channels = new[] { "snapchat", "sms" }
                };
            }
        }
    }
}

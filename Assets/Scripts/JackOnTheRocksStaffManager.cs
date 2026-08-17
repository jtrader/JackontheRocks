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
    /// <summary>Roles that may be assigned to an authenticated staff account.</summary>
    public enum StaffRole { Waiter, AreaManager, Admin }

    /// <summary>Server-authoritative states in the staff onboarding lifecycle.</summary>
    public enum OnboardingStatus
    {
        PendingOAuth,
        ProfileIncomplete,
        UnderageBlocked,
        PendingApproval,
        Active,
        Suspended
    }

    /// <summary>
    /// Serializable staff model used by registration, availability, and the admin map.
    /// Dates are converted to ISO-8601 strings by private transport DTOs because Unity's
    /// JsonUtility does not reliably serialize <see cref="DateTime"/>.
    /// </summary>
    [Serializable]
    public class StaffMember
    {
        public string staffId;
        public string snapchatUserId;
        public string displayName;
        public string mobileNumber;
        public DateTime dateOfBirth;
        public StaffRole role;
        public OnboardingStatus status;
        public float currentLatitude;
        public float currentLongitude;
        public float serviceRadiusKm = 5.0f;
        public DateTime lastLocationPing;
        public bool isLocationPermissionGranted;

        // A service bubble needs a fixed centre; these fields are also used for region reassignment.
        public string assignedRegionId;
        public float serviceCentreLatitude;
        public float serviceCentreLongitude;
        public bool isWithinServiceRadius;
        public bool isAvailable;

        /// <summary>Creates a staff record with a five-kilometre service radius.</summary>
        public StaffMember() { serviceRadiusKm = 5.0f; }
    }

    /// <summary>
    /// Persistent singleton responsible for staff OAuth onboarding, the 18+ gate,
    /// continuous WebGL geolocation, backend synchronisation, and the Leaflet admin map.
    ///
    /// OAuth code exchange, age evidence, approval, access control, and profile deletion
    /// must be enforced by the backend. This component never stores a Snapchat client
    /// secret or treats a client-side status change as authoritative.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class JackOnTheRocksStaffManager : MonoBehaviour
    {
        private const float DefaultServiceRadiusKm = 5.0f;
        private const string LocationRequiredMessage =
            "Location must remain set to Always On to receive drink orders and serve table zones.";

        /// <summary>Current singleton instance.</summary>
        public static JackOnTheRocksStaffManager Instance { get; private set; }

        [Header("Backend (OAuth exchange and authorization stay server-side)")]
        [SerializeField, Tooltip("Leave empty to use the WebGL page origin; Editor defaults to localhost:3000.")]
        private string backendBaseUrl = string.Empty;
        [SerializeField] private string snapchatOAuthStartPath = "/auth/snapchat/staff/start";
        [SerializeField] private string onboardingPath = "/api/staff/onboarding";
        [SerializeField] private string locationPath = "/api/staff/location";
        [SerializeField] private string adminStaffPath = "/admin/api/staff";

        [Header("Location")]
        [SerializeField, Min(1f)] private float locationUploadIntervalSeconds = 10f;
        [SerializeField, Min(10f)] private float offlineAfterSeconds = 90f;
        [SerializeField, Range(0f, 100f)] private float maximumServiceRadiusKm = 50f;

        [Header("Registration UI Model")]
        [SerializeField] private StaffRole requestedRegistrationRole = StaffRole.Waiter;
        [SerializeField] private string registrationMobileNumber = string.Empty;

        [Header("Leaflet Admin Map")]
        [SerializeField] private string mapContainerId = "jotr-staff-map";
        [SerializeField] private double initialMapLatitude = -37.8136;
        [SerializeField] private double initialMapLongitude = 144.9631;
        [SerializeField, Range(1, 19)] private int initialMapZoom = 12;

        private readonly Dictionary<string, StaffMember> staffById =
            new Dictionary<string, StaffMember>(StringComparer.Ordinal);
        private StaffMember onboardingStaff;
        private StaffMember currentStaff;
        private StaffMember selectedAdminStaff;
        private string staffBearerToken;
        private string adminBearerToken;
        private float lastLocationUploadRealtime = float.NegativeInfinity;
        private bool locationUploadInFlight;
        private bool mapInitialized;
        private bool applicationWorkflowLocked;

        /// <summary>Raised after a registration is accepted by the backend.</summary>
        public event Action<StaffMember> onStaffOnboardingSuccess;
        /// <summary>Raised when onboarding cannot continue.</summary>
        public event Action<string> onStaffOnboardingFailed;
        /// <summary>Raised after a valid browser location update is received.</summary>
        public event Action<string, float, float> onStaffLocationUpdated;
        /// <summary>Raised whenever the active/offline map data is rebuilt.</summary>
        public event Action<List<StaffMember>> onMapMarkersRefreshed;
        /// <summary>Raised whenever a staff status changes locally or from the server.</summary>
        public event Action<string, OnboardingStatus> onStaffStatusChanged;

        /// <summary>The locally authenticated staff record, if any.</summary>
        public StaffMember CurrentStaff => currentStaff;
        /// <summary>The staff record selected from the admin map, if any.</summary>
        public StaffMember SelectedAdminStaff => selectedAdminStaff;
        /// <summary>True after an underage or invalid-age response locks registration.</summary>
        public bool IsApplicationWorkflowLocked => applicationWorkflowLocked;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void JOTR_Staff_StartSnapchatOAuth(
            string url, string targetObject, string callbackMethod, string allowedOrigin);
        [DllImport("__Internal")] private static extern int JOTR_Staff_StartLocationWatch(
            string targetObject, string successMethod, string errorMethod);
        [DllImport("__Internal")] private static extern void JOTR_Staff_StopLocationWatch(int watchId);
        [DllImport("__Internal")] private static extern void JOTR_Staff_ShowAlert(string message);
        [DllImport("__Internal")] private static extern void JOTR_Staff_LeafletInit(
            string containerId, string targetObject, string clickMethod, double lat, double lng, int zoom);
        [DllImport("__Internal")] private static extern void JOTR_Staff_LeafletRefresh(string markersJson);
        [DllImport("__Internal")] private static extern void JOTR_Staff_LeafletSetVisible(int visible);
        [DllImport("__Internal")] private static extern void JOTR_Staff_LeafletDestroy();
#endif

        private int geolocationWatchId = -1;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            if (!mapInitialized || Time.frameCount % 60 != 0) return;

            bool changed = false;
            DateTime now = DateTime.UtcNow;
            foreach (StaffMember staff in staffById.Values)
            {
                bool available = IsStaffOnline(staff, now);
                if (staff.isAvailable == available) continue;
                staff.isAvailable = available;
                changed = true;
            }

            if (changed) RefreshMapMarkers();
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            StopContinuousGeolocation();
#if UNITY_WEBGL && !UNITY_EDITOR
            if (mapInitialized) JOTR_Staff_LeafletDestroy();
#endif
            Instance = null;
        }

        /// <summary>Sets the role selected by a registration UI dropdown.</summary>
        public void SetRequestedRegistrationRole(int roleValue)
        {
            if (Enum.IsDefined(typeof(StaffRole), roleValue))
                requestedRegistrationRole = (StaffRole)roleValue;
        }

        /// <summary>Sets the phone number entered by the registration UI.</summary>
        public void SetRegistrationMobileNumber(string mobileNumber)
        {
            registrationMobileNumber = (mobileNumber ?? string.Empty).Trim();
        }

        /// <summary>
        /// Supplies an access token obtained from the trusted backend. It is kept in memory only.
        /// </summary>
        public void SetStaffBearerToken(string token) { staffBearerToken = token ?? string.Empty; }

        /// <summary>Supplies the current admin bearer token in memory for admin CRUD calls.</summary>
        public void SetAdminBearerToken(string token) { adminBearerToken = token ?? string.Empty; }

        /// <summary>
        /// Restores a backend-authenticated staff session without persisting credentials locally.
        /// Call this after the host application's session refresh endpoint has validated the token.
        /// </summary>
        public void SetCurrentStaffSession(StaffMember staff, string bearerToken)
        {
            currentStaff = staff;
            staffBearerToken = bearerToken ?? string.Empty;
            if (staff != null && !string.IsNullOrEmpty(staff.staffId))
            {
                staff.serviceRadiusKm = ClampRadius(staff.serviceRadiusKm);
                staffById[staff.staffId] = staff;
            }
        }

        /// <summary>Unity button hook that starts Snapchat onboarding for the selected role.</summary>
        public void OnStartSnapchatLogin() { InitiateSnapchatOnboarding(requestedRegistrationRole); }

        /// <summary>
        /// Starts server-mediated Snapchat OAuth. The backend must use Authorization Code + PKCE,
        /// validate state/nonce, exchange the code, and return verified profile/age evidence to
        /// <see cref="OnSnapchatOAuthResult"/> via the bridge's postMessage contract.
        /// </summary>
        public void InitiateSnapchatOnboarding(StaffRole requestedRole)
        {
            if (requestedRole == StaffRole.Admin)
            {
                FailOnboarding("Admin accounts cannot be created through staff self-registration.");
                return;
            }

            applicationWorkflowLocked = false;
            requestedRegistrationRole = requestedRole;
            onboardingStaff = new StaffMember
            {
                role = requestedRole,
                status = OnboardingStatus.PendingOAuth,
                serviceRadiusKm = DefaultServiceRadiusKm
            };

            string startUrl = BuildUrl(snapchatOAuthStartPath) +
                "?role=" + UnityWebRequest.EscapeURL(requestedRole.ToString()) +
                "&scope=" + UnityWebRequest.EscapeURL("openid profile user.display_name user.bitmoji.avatar");

#if UNITY_WEBGL && !UNITY_EDITOR
            JOTR_Staff_StartSnapchatOAuth(startUrl, gameObject.name, nameof(OnSnapchatOAuthResult), GetOrigin(startUrl));
#else
            Debug.Log($"Snapchat OAuth is available in WebGL builds. Start URL: {startUrl}");
            FailOnboarding("Snapchat OAuth requires a WebGL browser build.");
#endif
        }

        /// <summary>
        /// Receives a verified OAuth result from the WebGL bridge. Expected JSON fields are
        /// success, error, accessToken, staffId, snapchatUserId, displayName, dateOfBirth,
        /// and ageVerified. Date of birth must be supplied/verified by the backend or a
        /// compliant age-assurance provider; Snapchat profile metadata alone is not an age proof.
        /// </summary>
        public void OnSnapchatOAuthResult(string json)
        {
            OAuthResultDto result;
            try { result = JsonUtility.FromJson<OAuthResultDto>(json); }
            catch (Exception ex)
            {
                Debug.LogWarning("Invalid OAuth callback: " + ex.Message);
                FailOnboarding("Snapchat sign-in returned an invalid response.");
                return;
            }

            if (result == null || !result.success)
            {
                FailOnboarding(string.IsNullOrWhiteSpace(result?.error)
                    ? "Snapchat sign-in was cancelled or failed."
                    : result.error);
                return;
            }

            if (onboardingStaff == null) onboardingStaff = new StaffMember();
            onboardingStaff.staffId = result.staffId ?? string.Empty;
            onboardingStaff.snapchatUserId = result.snapchatUserId ?? string.Empty;
            onboardingStaff.displayName = result.displayName ?? string.Empty;
            onboardingStaff.role = requestedRegistrationRole;
            onboardingStaff.serviceRadiusKm = DefaultServiceRadiusKm;
            staffBearerToken = result.accessToken ?? string.Empty;

            DateTime dob;
            if (!result.ageVerified || !TryParseDate(result.dateOfBirth, out dob))
            {
                onboardingStaff.status = OnboardingStatus.ProfileIncomplete;
                onStaffStatusChanged?.Invoke(onboardingStaff.staffId, onboardingStaff.status);
                FailOnboarding("A verified date of birth is required to continue.", false);
                return;
            }

            onboardingStaff.dateOfBirth = dob.Date;
            ValidateStaffAge(dob);
        }

        /// <summary>
        /// Enforces an exact calendar-based 18+ check. Underage applicants are locked locally;
        /// the backend must independently enforce the same rule.
        /// </summary>
        /// <returns>True only when the applicant is at least 18 today (UTC).</returns>
        public bool ValidateStaffAge(DateTime dob)
        {
            if (onboardingStaff == null) onboardingStaff = new StaffMember();
            DateTime today = DateTime.UtcNow.Date;
            DateTime birthDate = dob.Date;
            if (birthDate == DateTime.MinValue.Date || birthDate > today)
            {
                onboardingStaff.status = OnboardingStatus.ProfileIncomplete;
                onStaffStatusChanged?.Invoke(onboardingStaff.staffId, onboardingStaff.status);
                FailOnboarding("A valid, verified date of birth is required.", false);
                return false;
            }

            int age = today.Year - birthDate.Year;
            if (birthDate > today.AddYears(-age)) age--;
            if (age < 18)
            {
                onboardingStaff.status = OnboardingStatus.UnderageBlocked;
                applicationWorkflowLocked = true;
                onStaffStatusChanged?.Invoke(onboardingStaff.staffId, onboardingStaff.status);
                onStaffOnboardingFailed?.Invoke("Staff members must be 18+ years old.");
                return false;
            }

            onboardingStaff.dateOfBirth = birthDate;
            onboardingStaff.status = OnboardingStatus.PendingApproval;
            applicationWorkflowLocked = false;
            onStaffStatusChanged?.Invoke(onboardingStaff.staffId, onboardingStaff.status);
            return true;
        }

        /// <summary>
        /// Unity button hook that submits the OAuth profile and phone number to the backend.
        /// Successful submission leaves the profile PendingApproval until an admin approves it.
        /// </summary>
        public void OnSubmitStaffRegistration()
        {
            if (applicationWorkflowLocked || onboardingStaff == null ||
                onboardingStaff.status != OnboardingStatus.PendingApproval)
            {
                FailOnboarding("Complete Snapchat sign-in and the 18+ age check first.", false);
                return;
            }

            if (!IsPlausiblePhone(registrationMobileNumber))
            {
                FailOnboarding("Enter a valid mobile phone number.", false);
                return;
            }

            onboardingStaff.mobileNumber = registrationMobileNumber;
            StartCoroutine(SendJson("POST", BuildUrl(onboardingPath),
                JsonUtility.ToJson(StaffDto.FromModel(onboardingStaff)), staffBearerToken,
                (ok, response) =>
                {
                    if (!ok)
                    {
                        FailOnboarding(ReadServerError(response, "Registration could not be submitted."), false);
                        return;
                    }

                    StaffMember canonical = ParseStaff(response) ?? onboardingStaff;
                    canonical.status = OnboardingStatus.PendingApproval;
                    canonical.serviceRadiusKm = ClampRadius(canonical.serviceRadiusKm);
                    onboardingStaff = currentStaff = canonical;
                    if (!string.IsNullOrEmpty(canonical.staffId)) staffById[canonical.staffId] = canonical;
                    onStaffOnboardingSuccess?.Invoke(canonical);
                    onStaffStatusChanged?.Invoke(canonical.staffId, canonical.status);
                }));
        }

        /// <summary>Requests continuous high-accuracy location updates from the mobile browser.</summary>
        public void RequestContinuousGeolocation()
        {
            if (currentStaff == null || currentStaff.status != OnboardingStatus.Active)
            {
                HandleLocationUnavailable("Staff approval and Active status are required before going on duty.", false);
                return;
            }

            StopContinuousGeolocation();
#if UNITY_WEBGL && !UNITY_EDITOR
            geolocationWatchId = JOTR_Staff_StartLocationWatch(
                gameObject.name, nameof(OnBrowserLocationSuccess), nameof(OnBrowserLocationError));
            if (geolocationWatchId < 0) HandleLocationUnavailable(LocationRequiredMessage, true);
#else
            HandleLocationUnavailable("Continuous browser geolocation requires a WebGL build.", false);
#endif
        }

        /// <summary>Stops the active browser watchPosition subscription.</summary>
        public void StopContinuousGeolocation()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (geolocationWatchId >= 0) JOTR_Staff_StopLocationWatch(geolocationWatchId);
#endif
            geolocationWatchId = -1;
        }

        /// <summary>WebGL callback for navigator.geolocation.watchPosition successes.</summary>
        public void OnBrowserLocationSuccess(string json)
        {
            LocationDto value;
            try { value = JsonUtility.FromJson<LocationDto>(json); }
            catch { return; }
            if (value == null || !IsCoordinateValid(value.latitude, value.longitude)) return;
            UpdateStaffCoordinates((float)value.latitude, (float)value.longitude);
        }

        /// <summary>WebGL callback for denied/unavailable/timed-out geolocation.</summary>
        public void OnBrowserLocationError(string json)
        {
            LocationErrorDto error = null;
            try { error = JsonUtility.FromJson<LocationErrorDto>(json); } catch { }
            Debug.LogWarning("Browser geolocation unavailable: " + (error?.message ?? "unknown error"));
            HandleLocationUnavailable(LocationRequiredMessage, true);
        }

        /// <summary>
        /// Updates current coordinates, evaluates the assigned service bubble with the
        /// Haversine formula, raises UI events, and throttles backend uploads.
        /// </summary>
        public void UpdateStaffCoordinates(float lat, float lng)
        {
            if (currentStaff == null || !IsCoordinateValid(lat, lng)) return;

            currentStaff.currentLatitude = lat;
            currentStaff.currentLongitude = lng;
            currentStaff.lastLocationPing = DateTime.UtcNow;
            currentStaff.isLocationPermissionGranted = true;
            currentStaff.isWithinServiceRadius = EvaluateWithinServiceRadius(currentStaff, lat, lng);
            currentStaff.isAvailable = currentStaff.status == OnboardingStatus.Active &&
                                       currentStaff.isWithinServiceRadius;
            onStaffLocationUpdated?.Invoke(currentStaff.staffId, lat, lng);

            if (!string.IsNullOrEmpty(currentStaff.staffId)) staffById[currentStaff.staffId] = currentStaff;
            if (mapInitialized) RefreshMapMarkers();

            if (locationUploadInFlight ||
                Time.realtimeSinceStartup - lastLocationUploadRealtime < locationUploadIntervalSeconds) return;

            lastLocationUploadRealtime = Time.realtimeSinceStartup;
            locationUploadInFlight = true;
            LocationUploadDto payload = new LocationUploadDto
            {
                staffId = currentStaff.staffId,
                latitude = lat,
                longitude = lng,
                capturedAtUtc = currentStaff.lastLocationPing.ToString("O", CultureInfo.InvariantCulture)
            };
            StartCoroutine(SendJson("POST", BuildUrl(locationPath), JsonUtility.ToJson(payload), staffBearerToken,
                (ok, response) =>
                {
                    locationUploadInFlight = false;
                    if (!ok) Debug.LogWarning(ReadServerError(response, "Staff location upload failed."));
                }));
        }

        /// <summary>Returns whether coordinates fall inside a staff member's assigned region radius.</summary>
        public bool EvaluateWithinServiceRadius(StaffMember staff, float latitude, float longitude)
        {
            if (staff == null || staff.serviceRadiusKm <= 0f) return false;
            if (!IsCoordinateValid(staff.serviceCentreLatitude, staff.serviceCentreLongitude)) return false;
            double distance = HaversineKm(latitude, longitude,
                staff.serviceCentreLatitude, staff.serviceCentreLongitude);
            return distance <= staff.serviceRadiusKm;
        }

        /// <summary>Creates and displays the Leaflet/OpenStreetMap admin overlay.</summary>
        public void InitializeAdminMap()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            JOTR_Staff_LeafletInit(mapContainerId, gameObject.name, nameof(OnAdminSelectMapPin),
                initialMapLatitude, initialMapLongitude, initialMapZoom);
#else
            Debug.Log("Leaflet admin map is available in a WebGL browser build.");
#endif
        }

        /// <summary>WebGL callback fired after Leaflet and its OpenStreetMap layer are ready.</summary>
        public void OnLeafletMapReady(string unused)
        {
            mapInitialized = true;
            RefreshMapMarkers();
        }

        /// <summary>Shows or hides the HTML map without destroying its marker state.</summary>
        public void SetAdminMapVisible(bool visible)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!mapInitialized && visible) InitializeAdminMap();
            JOTR_Staff_LeafletSetVisible(visible ? 1 : 0);
#endif
        }

        /// <summary>Loads all staff visible to the authenticated admin and refreshes map pins.</summary>
        public void RefreshAdminStaff()
        {
            StartCoroutine(SendJson("GET", BuildUrl(adminStaffPath), null, adminBearerToken,
                (ok, response) =>
                {
                    if (!ok)
                    {
                        Debug.LogWarning(ReadServerError(response, "Unable to load staff."));
                        return;
                    }

                    StaffListDto list;
                    try { list = JsonUtility.FromJson<StaffListDto>(response); }
                    catch { list = null; }
                    staffById.Clear();
                    if (list?.staff != null)
                    {
                        foreach (StaffDto item in list.staff)
                        {
                            StaffMember staff = item?.ToModel();
                            if (staff != null && !string.IsNullOrEmpty(staff.staffId))
                                staffById[staff.staffId] = staff;
                        }
                    }
                    RefreshMapMarkers();
                }));
        }

        /// <summary>Unity/Leaflet hook that selects a map pin for the CRUD editor.</summary>
        public void OnAdminSelectMapPin(string staffId)
        {
            selectedAdminStaff = null;
            if (!string.IsNullOrEmpty(staffId)) staffById.TryGetValue(staffId, out selectedAdminStaff);
        }

        /// <summary>Unity slider/input hook that updates the selected staff member's radius.</summary>
        public void OnUpdateServiceRadius(float newRadius)
        {
            if (selectedAdminStaff == null) return;
            float prior = selectedAdminStaff.serviceRadiusKm;
            selectedAdminStaff.serviceRadiusKm = ClampRadius(newRadius);
            PatchAdminStaff(selectedAdminStaff, ok =>
            {
                if (!ok) selectedAdminStaff.serviceRadiusKm = prior;
                RefreshMapMarkers();
            });
        }

        /// <summary>Admin Create operation for manually registering a waiter or area manager.</summary>
        public void AdminCreateStaff(StaffMember staff, Action<bool> completed = null)
        {
            if (staff == null || staff.role == StaffRole.Admin || string.IsNullOrWhiteSpace(staff.snapchatUserId))
            {
                completed?.Invoke(false);
                return;
            }
            staff.serviceRadiusKm = ClampRadius(staff.serviceRadiusKm);
            StartCoroutine(SendJson("POST", BuildUrl(adminStaffPath),
                JsonUtility.ToJson(StaffDto.FromModel(staff)), adminBearerToken, (ok, response) =>
                {
                    StaffMember created = ok ? ParseStaff(response) : null;
                    if (created != null && !string.IsNullOrEmpty(created.staffId))
                        staffById[created.staffId] = created;
                    RefreshMapMarkers();
                    completed?.Invoke(ok && created != null);
                }));
        }

        /// <summary>Admin Update operation for phone, region, role, radius, and status fields.</summary>
        public void AdminUpdateStaff(StaffMember staff, Action<bool> completed = null)
        {
            if (staff == null || string.IsNullOrEmpty(staff.staffId))
            {
                completed?.Invoke(false);
                return;
            }
            PatchAdminStaff(staff, completed);
        }

        /// <summary>Convenience method to activate or suspend the selected staff account.</summary>
        public void AdminSetSelectedStaffSuspended(bool suspended)
        {
            if (selectedAdminStaff == null) return;
            OnboardingStatus previous = selectedAdminStaff.status;
            selectedAdminStaff.status = suspended ? OnboardingStatus.Suspended : OnboardingStatus.Active;
            PatchAdminStaff(selectedAdminStaff, ok =>
            {
                if (!ok) selectedAdminStaff.status = previous;
                else onStaffStatusChanged?.Invoke(selectedAdminStaff.staffId, selectedAdminStaff.status);
                RefreshMapMarkers();
            });
        }

        /// <summary>Admin Delete operation. The backend must revoke tokens and purge retained PII.</summary>
        public void AdminDeleteStaff(string staffId, Action<bool> completed = null)
        {
            if (string.IsNullOrWhiteSpace(staffId))
            {
                completed?.Invoke(false);
                return;
            }
            string url = BuildUrl(adminStaffPath) + "/" + UnityWebRequest.EscapeURL(staffId);
            StartCoroutine(SendJson("DELETE", url, null, adminBearerToken, (ok, response) =>
            {
                if (ok)
                {
                    staffById.Remove(staffId);
                    if (selectedAdminStaff?.staffId == staffId) selectedAdminStaff = null;
                    RefreshMapMarkers();
                }
                completed?.Invoke(ok);
            }));
        }

        private void PatchAdminStaff(StaffMember staff, Action<bool> completed)
        {
            staff.serviceRadiusKm = ClampRadius(staff.serviceRadiusKm);
            string url = BuildUrl(adminStaffPath) + "/" + UnityWebRequest.EscapeURL(staff.staffId);
            StartCoroutine(SendJson("PATCH", url, JsonUtility.ToJson(StaffDto.FromModel(staff)),
                adminBearerToken, (ok, response) =>
                {
                    if (ok)
                    {
                        StaffMember canonical = ParseStaff(response) ?? staff;
                        staffById[staff.staffId] = canonical;
                        if (selectedAdminStaff?.staffId == staff.staffId) selectedAdminStaff = canonical;
                    }
                    completed?.Invoke(ok);
                }));
        }

        private void RefreshMapMarkers()
        {
            DateTime now = DateTime.UtcNow;
            MapMarkerListDto wrapper = new MapMarkerListDto();
            List<StaffMember> snapshot = new List<StaffMember>();
            foreach (StaffMember staff in staffById.Values)
            {
                if (staff == null || string.IsNullOrEmpty(staff.staffId)) continue;
                staff.isAvailable = IsStaffOnline(staff, now);
                snapshot.Add(staff);
                wrapper.markers.Add(MapMarkerDto.FromModel(staff));
            }
#if UNITY_WEBGL && !UNITY_EDITOR
            if (mapInitialized) JOTR_Staff_LeafletRefresh(JsonUtility.ToJson(wrapper));
#endif
            onMapMarkersRefreshed?.Invoke(snapshot);
        }

        private bool IsStaffOnline(StaffMember staff, DateTime now)
        {
            return staff != null && staff.status == OnboardingStatus.Active &&
                   staff.isLocationPermissionGranted && staff.lastLocationPing != default(DateTime) &&
                   (now - staff.lastLocationPing.ToUniversalTime()).TotalSeconds <= offlineAfterSeconds;
        }

        private void HandleLocationUnavailable(string reason, bool showBrowserAlert)
        {
            if (currentStaff != null)
            {
                currentStaff.isLocationPermissionGranted = false;
                currentStaff.isAvailable = false;
                if (!string.IsNullOrEmpty(currentStaff.staffId)) staffById[currentStaff.staffId] = currentStaff;
            }
#if UNITY_WEBGL && !UNITY_EDITOR
            if (showBrowserAlert) JOTR_Staff_ShowAlert(LocationRequiredMessage);
#endif
            Debug.LogWarning(reason);
            if (mapInitialized) RefreshMapMarkers();
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
                if (!string.IsNullOrWhiteSpace(bearerToken))
                    request.SetRequestHeader("Authorization", "Bearer " + bearerToken);
                yield return request.SendWebRequest();
                bool ok = request.result == UnityWebRequest.Result.Success;
                callback?.Invoke(ok, request.downloadHandler?.text);
            }
        }

        private string BuildUrl(string path)
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out _)) return path;
            string origin = backendBaseUrl?.Trim();
            if (string.IsNullOrEmpty(origin))
            {
#if UNITY_EDITOR
                origin = "http://localhost:3000";
#else
                origin = GetOrigin(Application.absoluteURL);
#endif
            }
            return origin.TrimEnd('/') + "/" + (path ?? string.Empty).TrimStart('/');
        }

        private static string GetOrigin(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri)
                ? uri.GetLeftPart(UriPartial.Authority)
                : string.Empty;
        }

        private float ClampRadius(float radius)
        {
            if (radius <= 0f) radius = DefaultServiceRadiusKm;
            return Mathf.Clamp(radius, 0.1f, Mathf.Max(0.1f, maximumServiceRadiusKm));
        }

        private void FailOnboarding(string reason, bool clearProfile = true)
        {
            if (clearProfile && onboardingStaff != null &&
                onboardingStaff.status != OnboardingStatus.UnderageBlocked)
                onboardingStaff = null;
            onStaffOnboardingFailed?.Invoke(reason);
        }

        private static bool IsPlausiblePhone(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            int digits = 0;
            foreach (char c in value)
            {
                if (char.IsDigit(c)) digits++;
                else if (c != '+' && c != ' ' && c != '-' && c != '(' && c != ')') return false;
            }
            return digits >= 8 && digits <= 15;
        }

        private static bool IsCoordinateValid(double lat, double lng)
        {
            return !double.IsNaN(lat) && !double.IsNaN(lng) && !double.IsInfinity(lat) &&
                   !double.IsInfinity(lng) && lat >= -90d && lat <= 90d && lng >= -180d && lng <= 180d &&
                   !(Math.Abs(lat) < double.Epsilon && Math.Abs(lng) < double.Epsilon);
        }

        private static bool TryParseDate(string input, out DateTime result)
        {
            return DateTime.TryParseExact(input, new[] { "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ssZ", "O" },
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out result);
        }

        private static double HaversineKm(double lat1, double lng1, double lat2, double lng2)
        {
            const double earthRadiusKm = 6371.0088d;
            double dLat = (lat2 - lat1) * Math.PI / 180d;
            double dLng = (lng2 - lng1) * Math.PI / 180d;
            double a = Math.Sin(dLat / 2d) * Math.Sin(dLat / 2d) +
                       Math.Cos(lat1 * Math.PI / 180d) * Math.Cos(lat2 * Math.PI / 180d) *
                       Math.Sin(dLng / 2d) * Math.Sin(dLng / 2d);
            return earthRadiusKm * 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
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

        private static StaffMember ParseStaff(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                StaffEnvelopeDto envelope = JsonUtility.FromJson<StaffEnvelopeDto>(json);
                if (envelope?.staff != null) return envelope.staff.ToModel();
                return JsonUtility.FromJson<StaffDto>(json)?.ToModel();
            }
            catch { return null; }
        }

        [Serializable] private class OAuthResultDto
        {
            public bool success;
            public string error;
            public string accessToken;
            public string staffId;
            public string snapchatUserId;
            public string displayName;
            public string dateOfBirth;
            public bool ageVerified;
        }

        [Serializable] private class LocationDto
        {
            public double latitude;
            public double longitude;
            public double accuracy;
            public double timestamp;
        }

        [Serializable] private class LocationErrorDto { public int code; public string message; }
        [Serializable] private class LocationUploadDto
        {
            public string staffId;
            public float latitude;
            public float longitude;
            public string capturedAtUtc;
        }
        [Serializable] private class ErrorDto { public string error; public string message; }
        [Serializable] private class StaffEnvelopeDto { public StaffDto staff; }
        [Serializable] private class StaffListDto { public List<StaffDto> staff = new List<StaffDto>(); }

        [Serializable]
        private class StaffDto
        {
            public string staffId;
            public string snapchatUserId;
            public string displayName;
            public string mobileNumber;
            public string dateOfBirth;
            public string role;
            public string status;
            public float currentLatitude;
            public float currentLongitude;
            public float serviceRadiusKm;
            public string lastLocationPing;
            public bool isLocationPermissionGranted;
            public string assignedRegionId;
            public float serviceCentreLatitude;
            public float serviceCentreLongitude;
            public bool isWithinServiceRadius;
            public bool isAvailable;

            public static StaffDto FromModel(StaffMember value)
            {
                return new StaffDto
                {
                    staffId = value.staffId, snapchatUserId = value.snapchatUserId,
                    displayName = value.displayName, mobileNumber = value.mobileNumber,
                    dateOfBirth = value.dateOfBirth == default(DateTime) ? null : value.dateOfBirth.ToString("yyyy-MM-dd"),
                    role = value.role.ToString(), status = value.status.ToString(),
                    currentLatitude = value.currentLatitude, currentLongitude = value.currentLongitude,
                    serviceRadiusKm = value.serviceRadiusKm,
                    lastLocationPing = value.lastLocationPing == default(DateTime) ? null : value.lastLocationPing.ToUniversalTime().ToString("O"),
                    isLocationPermissionGranted = value.isLocationPermissionGranted,
                    assignedRegionId = value.assignedRegionId,
                    serviceCentreLatitude = value.serviceCentreLatitude,
                    serviceCentreLongitude = value.serviceCentreLongitude,
                    isWithinServiceRadius = value.isWithinServiceRadius, isAvailable = value.isAvailable
                };
            }

            public StaffMember ToModel()
            {
                DateTime dob, ping;
                TryParseDate(dateOfBirth, out dob);
                TryParseDate(lastLocationPing, out ping);
                StaffRole parsedRole;
                OnboardingStatus parsedStatus;
                if (!Enum.TryParse(role, true, out parsedRole)) parsedRole = StaffRole.Waiter;
                if (!Enum.TryParse(status, true, out parsedStatus)) parsedStatus = OnboardingStatus.PendingOAuth;
                return new StaffMember
                {
                    staffId = staffId, snapchatUserId = snapchatUserId, displayName = displayName,
                    mobileNumber = mobileNumber, dateOfBirth = dob, role = parsedRole, status = parsedStatus,
                    currentLatitude = currentLatitude, currentLongitude = currentLongitude,
                    serviceRadiusKm = serviceRadiusKm <= 0f ? DefaultServiceRadiusKm : serviceRadiusKm,
                    lastLocationPing = ping, isLocationPermissionGranted = isLocationPermissionGranted,
                    assignedRegionId = assignedRegionId, serviceCentreLatitude = serviceCentreLatitude,
                    serviceCentreLongitude = serviceCentreLongitude,
                    isWithinServiceRadius = isWithinServiceRadius, isAvailable = isAvailable
                };
            }
        }

        [Serializable] private class MapMarkerListDto { public List<MapMarkerDto> markers = new List<MapMarkerDto>(); }
        [Serializable]
        private class MapMarkerDto
        {
            public string staffId;
            public string displayName;
            public string role;
            public string status;
            public float latitude;
            public float longitude;
            public float radiusKm;
            public bool online;

            public static MapMarkerDto FromModel(StaffMember staff)
            {
                return new MapMarkerDto
                {
                    staffId = staff.staffId, displayName = staff.displayName,
                    role = staff.role.ToString(), status = staff.status.ToString(),
                    latitude = staff.currentLatitude, longitude = staff.currentLongitude,
                    radiusKm = staff.serviceRadiusKm, online = staff.isAvailable
                };
            }
        }
    }
}

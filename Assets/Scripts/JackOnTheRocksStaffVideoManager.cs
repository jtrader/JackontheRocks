using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.Video;

namespace JackOnTheRocks
{
    /// <summary>Supported visual presentation genders for staff profiles.</summary>
    public enum StaffGender { Male, Female }

    /// <summary>Administrative staff-to-video assignment model.</summary>
    [Serializable]
    public class StaffVisualProfile
    {
        /// <summary>Identifier of the corresponding authenticated staff account.</summary>
        public string staffId;
        /// <summary>Name rendered in player and admin interfaces.</summary>
        public string displayName;
        /// <summary>Male or female visual presentation.</summary>
        public StaffGender gender;
        /// <summary>Waiter or Area Manager role.</summary>
        public StaffRole role;
        /// <summary>Assigned free prompt creative.</summary>
        public string primaryPromptVideoId;
        /// <summary>VIP creatives available for this staff member.</summary>
        public List<string> assignedVipVideoIds = new List<string>();
        /// <summary>Local fallback portrait shown when streaming fails.</summary>
        public Sprite defaultAvatarSprite;
    }

    /// <summary>
    /// Persistent singleton for staff visual-profile CRUD, short MP4 creative CRUD and assignment,
    /// backend-authoritative Revolut purchase gating, playback, and prompt attribution analytics.
    /// VIP media is played only from a short-lived signed URL returned after server-side entitlement
    /// verification; permanent exclusive CDN URLs should never be returned to player clients.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class JackOnTheRocksStaffVideoManager : MonoBehaviour
    {
        private const string VipGateMessage =
            "EXCLUSIVE VIP SHOW: This exclusive video is reserved for drink buyers! " +
            "Purchase any drink (starting at $5.00 via Revolut @blackjackrocks) to unlock instantly.";
        private static readonly TimeSpan PurchaseCacheLifetime = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan PromptConversionWindow = TimeSpan.FromSeconds(30);

        private static JackOnTheRocksStaffVideoManager instance;

        /// <summary>Global staff video manager, created on first access.</summary>
        public static JackOnTheRocksStaffVideoManager Instance
        {
            get
            {
                if (instance == null)
                {
                    GameObject host = new GameObject(nameof(JackOnTheRocksStaffVideoManager));
                    instance = host.AddComponent<JackOnTheRocksStaffVideoManager>();
                }
                return instance;
            }
        }

        [Header("Backend")]
        [SerializeField, Tooltip("Leave empty for the WebGL page origin; Editor defaults to localhost:3000.")]
        private string backendBaseUrl = string.Empty;
        [SerializeField] private string staffProfilesPath = "/admin/api/staff-visual-profiles";
        [SerializeField] private string videoLibraryPath = "/admin/api/video-creatives";
        [SerializeField] private string purchaseRecordPath = "/api/players/{userId}/purchase-record";
        [SerializeField] private string vipPlaybackGrantPath = "/api/vip/videos/{videoId}/playback-grant";
        [SerializeField] private string analyticsPath = "/api/video-creatives/{videoId}/{metric}";

        [Header("Video Player UI")]
        [SerializeField] private VideoPlayer videoPlayer;
        [SerializeField] private RawImage videoCanvas;
        [SerializeField] private Image fallbackAvatarImage;
        [SerializeField] private bool muteInitially = true;
        [SerializeField, Min(3f)] private float prepareTimeoutSeconds = 15f;
        [SerializeField, Range(256, 1920)] private int renderTextureWidth = 1280;
        [SerializeField, Range(144, 1080)] private int renderTextureHeight = 720;

        [Header("Admin UI Drafts")]
        [SerializeField] private StaffVisualProfile adminStaffDraft = new StaffVisualProfile();
        [SerializeField] private VideoCreative adminVideoDraft = new VideoCreative();
        [SerializeField] private string adminAssignmentStaffId = string.Empty;
        [SerializeField] private string adminAssignmentVideoId = string.Empty;
        [SerializeField] private bool adminAssignmentIsVip;

        [Header("Player UI Context")]
        [SerializeField] private string currentPlayerSnapchatId = string.Empty;
        [SerializeField] private string currentlySelectedStaffId = string.Empty;

        private readonly Dictionary<string, StaffVisualProfile> staffProfiles =
            new Dictionary<string, StaffVisualProfile>(StringComparer.Ordinal);
        private readonly Dictionary<string, VideoCreative> videoLibrary =
            new Dictionary<string, VideoCreative>(StringComparer.Ordinal);
        private readonly Dictionary<string, CachedPurchaseRecord> purchaseCache =
            new Dictionary<string, CachedPurchaseRecord>(StringComparer.Ordinal);
        private string adminBearerToken = string.Empty;
        private string playerBearerToken = string.Empty;
        private RenderTexture renderTexture;
        private Coroutine prepareCoroutine;
        private VideoCreative pendingVideo;
        private StaffVisualProfile pendingStaff;
        private bool pendingRequiresExplicitTap;
        private string activePromptVideoId = string.Empty;
        private DateTime promptAttributionExpiresUtc;
        private bool impressionRecordedForCurrentPlayback;
        private JackOnTheRocksPaymentManager observedPaymentManager;

        /// <summary>Raised after a staff profile is created or updated.</summary>
        public event Action<StaffVisualProfile> onStaffProfileUpdated;
        /// <summary>Raised whenever the local video-library snapshot changes.</summary>
        public event Action<List<VideoCreative>> onVideoLibraryRefreshed;
        /// <summary>Raised when a standard prompt is selected for playback.</summary>
        public event Action<VideoCreative, StaffVisualProfile> onPromptVideoTriggered;
        /// <summary>Raised when the backend-confirmed purchase count does not unlock a VIP creative.</summary>
        public event Action<VideoCreative> onVipAccessDenied;
        /// <summary>Raised after the backend grants a signed VIP playback URL.</summary>
        public event Action<VideoCreative> onVipAccessGranted;
        /// <summary>Raised when a prepared mobile video requires another explicit play tap.</summary>
        public event Action<VideoCreative> onVideoReadyForTap;
        /// <summary>Raised for validation, persistence, entitlement, and playback errors.</summary>
        public event Action<string> onStaffVideoError;

        /// <summary>Message to render in the VIP gating modal.</summary>
        public string VipAccessDeniedMessage => VipGateMessage;
        /// <summary>Video currently prepared or playing.</summary>
        public VideoCreative CurrentVideo => pendingVideo;

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);
            EnsureVideoPlayer();
            observedPaymentManager = JackOnTheRocksPaymentManager.Instance;
            if (observedPaymentManager != null)
                observedPaymentManager.onPaymentConfirmed += HandleConfirmedPayment;
        }

        private void OnDestroy()
        {
            if (videoPlayer != null)
            {
                videoPlayer.started -= HandleVideoStarted;
                videoPlayer.loopPointReached -= HandleVideoEnded;
                videoPlayer.errorReceived -= HandleVideoError;
            }
            if (observedPaymentManager != null)
                observedPaymentManager.onPaymentConfirmed -= HandleConfirmedPayment;
            if (renderTexture != null)
            {
                if (videoPlayer != null) videoPlayer.targetTexture = null;
                renderTexture.Release();
                Destroy(renderTexture);
            }
            if (instance == this) instance = null;
        }

        /// <summary>Sets the in-memory admin token used for administrative CRUD endpoints.</summary>
        public void SetAdminBearerToken(string token) { adminBearerToken = token ?? string.Empty; }

        /// <summary>Sets the authenticated player identity and short-lived application token.</summary>
        public void SetPlayerSession(string snapchatUserId, string bearerToken)
        {
            currentPlayerSnapchatId = (snapchatUserId ?? string.Empty).Trim();
            playerBearerToken = bearerToken ?? string.Empty;
        }

        /// <summary>Sets the staff profile selected in the table or VIP gallery.</summary>
        public void SetSelectedStaffId(string staffId) { currentlySelectedStaffId = (staffId ?? string.Empty).Trim(); }

        /// <summary>Replaces the admin staff form draft used by <see cref="OnAdminCreateStaff"/>.</summary>
        public void SetAdminStaffDraft(StaffVisualProfile profile) { adminStaffDraft = profile; }

        /// <summary>Replaces the admin video form draft used by <see cref="OnAdminUploadVideo"/>.</summary>
        public void SetAdminVideoDraft(VideoCreative video) { adminVideoDraft = video; }

        /// <summary>Sets assignment-form identifiers and category.</summary>
        public void SetAdminAssignment(string staffId, string videoId, bool isVip)
        {
            adminAssignmentStaffId = staffId ?? string.Empty;
            adminAssignmentVideoId = videoId ?? string.Empty;
            adminAssignmentIsVip = isVip;
        }

        /// <summary>Loads authoritative staff and video snapshots from the admin backend.</summary>
        public void RefreshAdministrativeLibrary()
        {
            StartCoroutine(FetchStaffProfiles());
            StartCoroutine(FetchVideoLibrary());
        }

        /// <summary>Creates a Waiter or Area Manager visual profile and persists it asynchronously.</summary>
        public void CreateStaffProfile(StaffVisualProfile profile)
        {
            string error;
            if (!ValidateStaffProfile(profile, false, out error)) { ReportError(error); return; }
            StaffVisualProfile submitted = CloneStaff(profile);
            StartCoroutine(SendJson("POST", BuildUrl(staffProfilesPath),
                JsonUtility.ToJson(StaffProfileDto.FromModel(submitted)), adminBearerToken, (ok, json) =>
                {
                    if (!ok) { ReportError(ReadServerError(json, "Staff profile creation failed.")); return; }
                    StaffVisualProfile canonical = ParseStaffProfile(json, submitted.defaultAvatarSprite) ?? submitted;
                    staffProfiles[canonical.staffId] = canonical;
                    onStaffProfileUpdated?.Invoke(CloneStaff(canonical));
                }));
        }

        /// <summary>Returns detached staff profiles matching the requested gender and role.</summary>
        public List<StaffVisualProfile> ReadStaffProfiles(StaffGender genderFilter, StaffRole roleFilter)
        {
            List<StaffVisualProfile> result = new List<StaffVisualProfile>();
            foreach (StaffVisualProfile profile in staffProfiles.Values)
                if (profile != null && profile.gender == genderFilter && profile.role == roleFilter)
                    result.Add(CloneStaff(profile));
            result.Sort((left, right) => string.Compare(left.displayName, right.displayName,
                StringComparison.OrdinalIgnoreCase));
            return result;
        }

        /// <summary>Updates an existing staff visual profile using server-authoritative persistence.</summary>
        public void UpdateStaffProfile(string staffId, StaffVisualProfile updatedProfile)
        {
            if (updatedProfile == null) { ReportError("Updated staff profile is required."); return; }
            updatedProfile = CloneStaff(updatedProfile);
            updatedProfile.staffId = staffId;
            string error;
            if (!ValidateStaffProfile(updatedProfile, true, out error)) { ReportError(error); return; }
            string url = BuildUrl(staffProfilesPath) + "/" + UnityWebRequest.EscapeURL(staffId);
            StartCoroutine(SendJson("PUT", url, JsonUtility.ToJson(StaffProfileDto.FromModel(updatedProfile)),
                adminBearerToken, (ok, json) =>
                {
                    if (!ok) { ReportError(ReadServerError(json, "Staff profile update failed.")); return; }
                    StaffVisualProfile canonical = ParseStaffProfile(json, updatedProfile.defaultAvatarSprite) ?? updatedProfile;
                    staffProfiles[staffId] = canonical;
                    onStaffProfileUpdated?.Invoke(CloneStaff(canonical));
                }));
        }

        /// <summary>Deletes a staff visual profile after the backend revokes the record.</summary>
        public void DeleteStaffProfile(string staffId)
        {
            if (string.IsNullOrWhiteSpace(staffId)) { ReportError("Staff ID is required."); return; }
            string url = BuildUrl(staffProfilesPath) + "/" + UnityWebRequest.EscapeURL(staffId);
            StartCoroutine(SendJson("DELETE", url, null, adminBearerToken, (ok, json) =>
            {
                if (!ok) { ReportError(ReadServerError(json, "Staff profile deletion failed.")); return; }
                staffProfiles.Remove(staffId);
                onStaffProfileUpdated?.Invoke(null);
            }));
        }

        /// <summary>Registers CDN-hosted MP4 metadata for a 5, 10, or 15 second creative.</summary>
        public void UploadVideoCreative(VideoCreative newVideo)
        {
            if (newVideo == null) { ReportError("Video metadata is required."); return; }
            VideoCreative submitted = NormalizeVideo(CloneVideo(newVideo));
            if (string.IsNullOrWhiteSpace(submitted.videoId)) submitted.videoId = Guid.NewGuid().ToString("N");
            string error;
            if (!ValidateVideo(submitted, out error)) { ReportError(error); return; }
            StartCoroutine(SendJson("POST", BuildUrl(videoLibraryPath),
                JsonUtility.ToJson(VideoCreativeDto.FromModel(submitted)), adminBearerToken, (ok, json) =>
                {
                    if (!ok) { ReportError(ReadServerError(json, "Video registration failed.")); return; }
                    VideoCreative canonical = ParseVideo(json) ?? submitted;
                    canonical = NormalizeVideo(canonical);
                    videoLibrary[canonical.videoId] = canonical;
                    PublishVideoLibrary();
                }));
        }

        /// <summary>Returns detached creatives matching both access type and duration.</summary>
        public List<VideoCreative> ReadVideoLibrary(VideoType typeFilter, VideoDuration durationFilter)
        {
            List<VideoCreative> result = new List<VideoCreative>();
            foreach (VideoCreative video in videoLibrary.Values)
            {
                VideoCreative normalized = NormalizeVideo(video);
                if (normalized.category == typeFilter && normalized.duration == durationFilter)
                    result.Add(CloneVideo(normalized));
            }
            result.Sort((left, right) => string.Compare(left.title, right.title, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        /// <summary>Updates creative metadata while the backend preserves authoritative analytics counters.</summary>
        public void UpdateVideoMetaData(string videoId, VideoCreative updatedVideo)
        {
            if (updatedVideo == null) { ReportError("Updated video metadata is required."); return; }
            VideoCreative submitted = NormalizeVideo(CloneVideo(updatedVideo));
            submitted.videoId = videoId;
            string error;
            if (!ValidateVideo(submitted, out error)) { ReportError(error); return; }
            string url = BuildUrl(videoLibraryPath) + "/" + UnityWebRequest.EscapeURL(videoId);
            StartCoroutine(SendJson("PUT", url, JsonUtility.ToJson(VideoCreativeDto.FromModel(submitted)),
                adminBearerToken, (ok, json) =>
                {
                    if (!ok) { ReportError(ReadServerError(json, "Video metadata update failed.")); return; }
                    VideoCreative canonical = NormalizeVideo(ParseVideo(json) ?? submitted);
                    videoLibrary[videoId] = canonical;
                    PublishVideoLibrary();
                }));
        }

        /// <summary>Deletes a creative and removes its local staff assignments after backend success.</summary>
        public void DeleteVideoCreative(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId)) { ReportError("Video ID is required."); return; }
            string url = BuildUrl(videoLibraryPath) + "/" + UnityWebRequest.EscapeURL(videoId);
            StartCoroutine(SendJson("DELETE", url, null, adminBearerToken, (ok, json) =>
            {
                if (!ok) { ReportError(ReadServerError(json, "Video deletion failed.")); return; }
                videoLibrary.Remove(videoId);
                foreach (StaffVisualProfile staff in staffProfiles.Values)
                {
                    if (staff == null) continue;
                    if (staff.primaryPromptVideoId == videoId) staff.primaryPromptVideoId = string.Empty;
                    staff.assignedVipVideoIds?.RemoveAll(id => id == videoId);
                }
                PublishVideoLibrary();
            }));
        }

        /// <summary>Assigns a category-compatible standard or VIP creative to a staff profile.</summary>
        public void AssignVideoToStaff(string staffId, string videoId, bool isVipMedia)
        {
            StaffVisualProfile staff;
            VideoCreative video;
            if (!staffProfiles.TryGetValue(staffId ?? string.Empty, out staff) || staff == null)
            { ReportError("The selected staff profile does not exist."); return; }
            if (!videoLibrary.TryGetValue(videoId ?? string.Empty, out video) || video == null)
            { ReportError("The selected video does not exist."); return; }
            video = NormalizeVideo(video);
            VideoType expected = isVipMedia ? VideoType.VIPExclusive : VideoType.StandardPrompt;
            if (video.category != expected)
            { ReportError("The video category does not match the requested assignment type."); return; }

            AssignmentDto payload = new AssignmentDto { staffId = staffId, videoId = videoId, isVipMedia = isVipMedia };
            string url = BuildUrl(staffProfilesPath) + "/" + UnityWebRequest.EscapeURL(staffId) + "/videos";
            StartCoroutine(SendJson("POST", url, JsonUtility.ToJson(payload), adminBearerToken, (ok, json) =>
            {
                if (!ok) { ReportError(ReadServerError(json, "Video assignment failed.")); return; }
                if (isVipMedia)
                {
                    if (staff.assignedVipVideoIds == null) staff.assignedVipVideoIds = new List<string>();
                    if (!staff.assignedVipVideoIds.Contains(videoId)) staff.assignedVipVideoIds.Add(videoId);
                }
                else staff.primaryPromptVideoId = videoId;
                onStaffProfileUpdated?.Invoke(CloneStaff(staff));
            }));
        }

        /// <summary>Returns the staff member's assigned standard prompt, or null if invalid/unassigned.</summary>
        public VideoCreative GetRandomStaffPromptVideo(string staffId)
        {
            StaffVisualProfile staff;
            VideoCreative video;
            if (!staffProfiles.TryGetValue(staffId ?? string.Empty, out staff) || staff == null ||
                string.IsNullOrWhiteSpace(staff.primaryPromptVideoId) ||
                !videoLibrary.TryGetValue(staff.primaryPromptVideoId, out video)) return null;
            video = NormalizeVideo(video);
            return video.category == VideoType.StandardPrompt ? CloneVideo(video) : null;
        }

        /// <summary>Selects and starts the standard table prompt assigned to a staff member.</summary>
        public void TriggerStaffPromptVideo(string staffId)
        {
            VideoCreative video = GetRandomStaffPromptVideo(staffId);
            StaffVisualProfile staff;
            if (video == null || !staffProfiles.TryGetValue(staffId ?? string.Empty, out staff))
            { ReportError("No standard prompt is assigned to this staff member."); return; }
            onPromptVideoTriggered?.Invoke(CloneVideo(video), CloneStaff(staff));
            PrepareVideoPlayback(video, staff, video.cdnUrl, false);
        }

        /// <summary>
        /// Requests VIP playback after checking the backend PlayerPurchaseRecord and obtaining a
        /// short-lived signed MP4 URL. Local counters or PlayerPrefs never grant VIP access.
        /// </summary>
        public void RequestVipVideoPlayback(string userSnapchatId, string staffId, string videoId)
        {
            if (string.IsNullOrWhiteSpace(userSnapchatId) || string.IsNullOrWhiteSpace(playerBearerToken))
            { ReportError("An authenticated player session is required for VIP playback."); return; }
            StaffVisualProfile staff;
            VideoCreative video;
            if (!staffProfiles.TryGetValue(staffId ?? string.Empty, out staff) || staff == null ||
                staff.assignedVipVideoIds == null || !staff.assignedVipVideoIds.Contains(videoId) ||
                !videoLibrary.TryGetValue(videoId ?? string.Empty, out video))
            { ReportError("This VIP video is not assigned to the selected staff member."); return; }
            video = NormalizeVideo(video);
            if (video.category != VideoType.VIPExclusive)
            { ReportError("The requested creative is not VIP-exclusive."); return; }
            StartCoroutine(VerifyPurchaseAndRequestGrant(userSnapchatId, staff, video));
        }

        /// <summary>Explicit mobile UI tap that starts a prepared video.</summary>
        public void OnTapToPlayVideo()
        {
            if (videoPlayer == null || !videoPlayer.isPrepared || pendingVideo == null)
            { ReportError("The video is not ready yet."); return; }
            pendingRequiresExplicitTap = false;
            ApplyAudioMute(muteInitially);
            videoPlayer.Play();
        }

        /// <summary>Explicit mobile UI tap that unmutes the current video after audio-context unlock.</summary>
        public void OnUnmuteVideoClicked()
        {
            if (videoPlayer == null) return;
            ApplyAudioMute(false);
            if (videoPlayer.isPrepared && !videoPlayer.isPlaying) videoPlayer.Play();
        }

        /// <summary>
        /// Records a backend-confirmed purchase for prompt conversion attribution and invalidates
        /// the player's cached entitlement. Call only after the server confirms Revolut payment.
        /// </summary>
        public void NotifyDrinkPurchaseConfirmed(string userSnapchatId, int drinksPurchased)
        {
            if (string.IsNullOrWhiteSpace(userSnapchatId) || drinksPurchased <= 0) return;
            purchaseCache.Remove(userSnapchatId);
            if (string.IsNullOrWhiteSpace(activePromptVideoId) || DateTime.UtcNow > promptAttributionExpiresUtc) return;
            VideoCreative video;
            if (!videoLibrary.TryGetValue(activePromptVideoId, out video)) return;
            video.conversionCount++;
            NormalizeVideo(video);
            PostMetric(video.videoId, "conversion");
            PublishVideoLibrary();
        }

        /// <summary>Unity button hook that submits the current staff-profile draft.</summary>
        public void OnAdminCreateStaff() { CreateStaffProfile(adminStaffDraft); }

        /// <summary>Unity button hook that submits the current video-creative draft.</summary>
        public void OnAdminUploadVideo() { UploadVideoCreative(adminVideoDraft); }

        /// <summary>Unity button hook that applies the current staff/video assignment draft.</summary>
        public void OnAdminAssignVideo()
        {
            AssignVideoToStaff(adminAssignmentStaffId, adminAssignmentVideoId, adminAssignmentIsVip);
        }

        /// <summary>Unity VIP-gallery button hook using the authenticated player and selected staff.</summary>
        public void OnPlayerWatchVipVideo(string videoId)
        {
            RequestVipVideoPlayback(currentPlayerSnapchatId, currentlySelectedStaffId, videoId);
        }

        private IEnumerator VerifyPurchaseAndRequestGrant(string userId, StaffVisualProfile staff, VideoCreative video)
        {
            PlayerPurchaseRecord record = null;
            CachedPurchaseRecord cached;
            if (purchaseCache.TryGetValue(userId, out cached) &&
                DateTime.UtcNow - cached.fetchedAtUtc <= PurchaseCacheLifetime)
                record = cached.record;
            else
            {
                string path = purchaseRecordPath.Replace("{userId}", UnityWebRequest.EscapeURL(userId));
                bool complete = false;
                bool success = false;
                string response = null;
                yield return SendJson("GET", BuildUrl(path), null, playerBearerToken, (ok, json) =>
                { success = ok; response = json; complete = true; });
                if (!complete || !success)
                { ReportError(ReadServerError(response, "Purchase entitlement could not be verified.")); yield break; }
                record = ParsePurchaseRecord(response);
                if (record == null || !string.Equals(record.userSnapchatId, userId, StringComparison.Ordinal))
                { ReportError("The backend returned an invalid purchase record."); yield break; }
                purchaseCache[userId] = new CachedPurchaseRecord { record = record, fetchedAtUtc = DateTime.UtcNow };
            }

            int required = Mathf.Max(1, video.minDrinksRequiredToUnlock);
            if (record.totalDrinksPurchased < required)
            {
                onVipAccessDenied?.Invoke(CloneVideo(video));
                yield break;
            }

            string grantPath = vipPlaybackGrantPath.Replace("{videoId}", UnityWebRequest.EscapeURL(video.videoId));
            PlaybackGrantRequestDto requestBody = new PlaybackGrantRequestDto
            { userSnapchatId = userId, staffId = staff.staffId, videoId = video.videoId };
            bool grantComplete = false;
            bool grantSuccess = false;
            string grantJson = null;
            yield return SendJson("POST", BuildUrl(grantPath), JsonUtility.ToJson(requestBody), playerBearerToken,
                (ok, json) => { grantSuccess = ok; grantJson = json; grantComplete = true; });
            if (!grantComplete || !grantSuccess)
            {
                if (IsAccessDeniedResponse(grantJson)) onVipAccessDenied?.Invoke(CloneVideo(video));
                else ReportError(ReadServerError(grantJson, "VIP playback authorization failed."));
                yield break;
            }
            PlaybackGrantDto grant;
            try { grant = JsonUtility.FromJson<PlaybackGrantDto>(grantJson); }
            catch { grant = null; }
            DateTime grantExpiry;
            bool expiryValid = DateTime.TryParse(grant?.expiresAtUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out grantExpiry) &&
                grantExpiry > DateTime.UtcNow;
            if (grant == null || !grant.granted || !expiryValid || !IsSecureMp4Url(grant.signedUrl))
            { ReportError("The backend returned an invalid VIP playback grant."); yield break; }

            onVipAccessGranted?.Invoke(CloneVideo(video));
            PrepareVideoPlayback(video, staff, grant.signedUrl, true);
        }

        private void PrepareVideoPlayback(VideoCreative video, StaffVisualProfile staff,
            string playbackUrl, bool requireExplicitTap)
        {
            if (!IsSecureMp4Url(playbackUrl)) { ReportError("A secure MP4 playback URL is required."); return; }
            EnsureVideoPlayer();
            if (prepareCoroutine != null) StopCoroutine(prepareCoroutine);
            if (videoPlayer.isPlaying) videoPlayer.Stop();
            pendingVideo = video;
            pendingStaff = staff;
            pendingRequiresExplicitTap = requireExplicitTap;
            impressionRecordedForCurrentPlayback = false;
            videoPlayer.source = VideoSource.Url;
            videoPlayer.url = playbackUrl;
            videoPlayer.isLooping = false;
            videoPlayer.Prepare();
            prepareCoroutine = StartCoroutine(WaitForVideoPreparation());
        }

        private IEnumerator WaitForVideoPreparation()
        {
            float elapsed = 0f;
            while (videoPlayer != null && !videoPlayer.isPrepared && elapsed < prepareTimeoutSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
            prepareCoroutine = null;
            if (videoPlayer == null || !videoPlayer.isPrepared)
            {
                ShowFallbackAvatar();
                ReportError("The MP4 stream could not be prepared before timeout.");
                yield break;
            }
            ApplyAudioMute(true);
            if (pendingRequiresExplicitTap) onVideoReadyForTap?.Invoke(CloneVideo(pendingVideo));
            else videoPlayer.Play();
        }

        private void EnsureVideoPlayer()
        {
            if (videoPlayer == null)
            {
                GameObject host = new GameObject("StaffVideoPlayer");
                host.transform.SetParent(transform, false);
                videoPlayer = host.AddComponent<VideoPlayer>();
            }
            videoPlayer.playOnAwake = false;
            videoPlayer.audioOutputMode = VideoAudioOutputMode.Direct;
            videoPlayer.waitForFirstFrame = true;
            videoPlayer.skipOnDrop = true;
            if (videoCanvas != null)
            {
                if (renderTexture == null)
                {
                    renderTexture = new RenderTexture(renderTextureWidth, renderTextureHeight, 0,
                        RenderTextureFormat.ARGB32) { name = "JOTR Staff Video" };
                    renderTexture.Create();
                }
                videoPlayer.renderMode = VideoRenderMode.RenderTexture;
                videoPlayer.targetTexture = renderTexture;
                videoCanvas.texture = renderTexture;
            }
            else
            {
                videoPlayer.renderMode = VideoRenderMode.CameraNearPlane;
                videoPlayer.targetCamera = Camera.main;
            }
            videoPlayer.started -= HandleVideoStarted;
            videoPlayer.loopPointReached -= HandleVideoEnded;
            videoPlayer.errorReceived -= HandleVideoError;
            videoPlayer.started += HandleVideoStarted;
            videoPlayer.loopPointReached += HandleVideoEnded;
            videoPlayer.errorReceived += HandleVideoError;
        }

        private void HandleVideoStarted(VideoPlayer source)
        {
            if (pendingVideo == null || impressionRecordedForCurrentPlayback) return;
            impressionRecordedForCurrentPlayback = true;
            pendingVideo.impressionCount++;
            NormalizeVideo(pendingVideo);
            videoLibrary[pendingVideo.videoId] = pendingVideo;
            if (pendingVideo.category == VideoType.StandardPrompt)
            {
                activePromptVideoId = pendingVideo.videoId;
                promptAttributionExpiresUtc = DateTime.UtcNow
                    .AddSeconds((int)pendingVideo.duration)
                    .Add(PromptConversionWindow);
            }
            PostMetric(pendingVideo.videoId, "impression");
            PublishVideoLibrary();
        }

        private void HandleVideoEnded(VideoPlayer source)
        {
            if (pendingVideo != null && pendingVideo.category == VideoType.StandardPrompt)
                promptAttributionExpiresUtc = DateTime.UtcNow.Add(PromptConversionWindow);
        }

        private void HandleVideoError(VideoPlayer source, string message)
        {
            ShowFallbackAvatar();
            ReportError("Video playback failed: " + message);
        }

        private void HandleConfirmedPayment(string orderId)
        {
            if (!string.IsNullOrWhiteSpace(currentPlayerSnapchatId))
                NotifyDrinkPurchaseConfirmed(currentPlayerSnapchatId, 1);
        }

        private void ApplyAudioMute(bool muted)
        {
            if (videoPlayer == null) return;
            try
            {
                ushort tracks = videoPlayer.audioTrackCount;
                for (ushort i = 0; i < tracks; i++) videoPlayer.SetDirectAudioMute(i, muted);
            }
            catch (Exception ex) { Debug.LogWarning("Video mute could not be changed: " + ex.Message); }
        }

        private void ShowFallbackAvatar()
        {
            if (fallbackAvatarImage == null) return;
            fallbackAvatarImage.sprite = pendingStaff?.defaultAvatarSprite;
            fallbackAvatarImage.gameObject.SetActive(fallbackAvatarImage.sprite != null);
        }

        private void PostMetric(string videoId, string metric)
        {
            if (string.IsNullOrWhiteSpace(videoId)) return;
            string path = analyticsPath.Replace("{videoId}", UnityWebRequest.EscapeURL(videoId))
                .Replace("{metric}", UnityWebRequest.EscapeURL(metric));
            StartCoroutine(SendJson("POST", BuildUrl(path), "{}", playerBearerToken, (ok, json) =>
            {
                if (!ok) Debug.LogWarning(ReadServerError(json, "Video analytics update failed."));
            }));
        }

        private IEnumerator FetchStaffProfiles()
        {
            yield return SendJson("GET", BuildUrl(staffProfilesPath), null, adminBearerToken, (ok, json) =>
            {
                if (!ok) { ReportError(ReadServerError(json, "Staff profiles could not be loaded.")); return; }
                StaffProfileListDto wrapper;
                try { wrapper = JsonUtility.FromJson<StaffProfileListDto>(json); }
                catch { wrapper = null; }
                if (wrapper?.staff == null) { ReportError("The staff profile response was invalid."); return; }
                Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();
                foreach (KeyValuePair<string, StaffVisualProfile> pair in staffProfiles)
                    sprites[pair.Key] = pair.Value?.defaultAvatarSprite;
                staffProfiles.Clear();
                foreach (StaffProfileDto dto in wrapper.staff)
                {
                    StaffVisualProfile profile = dto?.ToModel();
                    if (profile == null || string.IsNullOrWhiteSpace(profile.staffId)) continue;
                    Sprite sprite;
                    if (sprites.TryGetValue(profile.staffId, out sprite)) profile.defaultAvatarSprite = sprite;
                    staffProfiles[profile.staffId] = profile;
                }
            });
        }

        private IEnumerator FetchVideoLibrary()
        {
            yield return SendJson("GET", BuildUrl(videoLibraryPath), null, adminBearerToken, (ok, json) =>
            {
                if (!ok) { ReportError(ReadServerError(json, "Video library could not be loaded.")); return; }
                VideoCreativeListDto wrapper;
                try { wrapper = JsonUtility.FromJson<VideoCreativeListDto>(json); }
                catch { wrapper = null; }
                if (wrapper?.videos == null) { ReportError("The video library response was invalid."); return; }
                videoLibrary.Clear();
                foreach (VideoCreativeDto dto in wrapper.videos)
                {
                    VideoCreative video = dto?.ToModel();
                    if (video == null || string.IsNullOrWhiteSpace(video.videoId)) continue;
                    videoLibrary[video.videoId] = NormalizeVideo(video);
                }
                PublishVideoLibrary();
            });
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
                callback?.Invoke(request.result == UnityWebRequest.Result.Success,
                    request.downloadHandler?.text);
            }
        }

        private void PublishVideoLibrary()
        {
            List<VideoCreative> snapshot = new List<VideoCreative>();
            foreach (VideoCreative video in videoLibrary.Values) snapshot.Add(CloneVideo(NormalizeVideo(video)));
            snapshot.Sort((left, right) => string.Compare(left.title, right.title, StringComparison.OrdinalIgnoreCase));
            onVideoLibraryRefreshed?.Invoke(snapshot);
        }

        private static bool ValidateStaffProfile(StaffVisualProfile profile, bool isUpdate, out string error)
        {
            if (profile == null) { error = "Staff profile is required."; return false; }
            if (string.IsNullOrWhiteSpace(profile.staffId)) { error = "Staff ID is required."; return false; }
            if (string.IsNullOrWhiteSpace(profile.displayName)) { error = "Staff display name is required."; return false; }
            if (profile.role != StaffRole.Waiter && profile.role != StaffRole.AreaManager)
            { error = "Visual profiles may only represent Waiters or Area Managers."; return false; }
            error = null;
            return true;
        }

        private static bool ValidateVideo(VideoCreative video, out string error)
        {
            if (video == null) { error = "Video metadata is required."; return false; }
            if (string.IsNullOrWhiteSpace(video.videoId)) { error = "Video ID is required."; return false; }
            if (string.IsNullOrWhiteSpace(video.title)) { error = "Video title is required."; return false; }
            if (!IsSecureMp4Url(video.cdnUrl)) { error = "A secure HTTPS MP4 CDN URL is required."; return false; }
            if (video.duration != VideoDuration.Sec5 && video.duration != VideoDuration.Sec10 &&
                video.duration != VideoDuration.Sec15)
            { error = "Video duration must be 5, 10, or 15 seconds."; return false; }
            if (video.category == VideoType.VIPExclusive && video.minDrinksRequiredToUnlock < 1)
                video.minDrinksRequiredToUnlock = 1;
            error = null;
            return true;
        }

        private static bool IsSecureMp4Url(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
            return uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
        }

        private static VideoCreative NormalizeVideo(VideoCreative video)
        {
            if (video == null) return null;
            if (string.IsNullOrWhiteSpace(video.videoId)) video.videoId = video.creativeId;
            if (string.IsNullOrWhiteSpace(video.creativeId)) video.creativeId = video.videoId;
            if (string.IsNullOrWhiteSpace(video.title)) video.title = video.videoTitle;
            if (string.IsNullOrWhiteSpace(video.videoTitle)) video.videoTitle = video.title;
            if (string.IsNullOrWhiteSpace(video.cdnUrl)) video.cdnUrl = video.videoUrl;
            if (string.IsNullOrWhiteSpace(video.videoUrl)) video.videoUrl = video.cdnUrl;
            if (string.IsNullOrWhiteSpace(video.targetDrinkType))
                video.targetDrinkType = video.targetDrink.ToString();
            JackOnTheRocksPaymentManager.DrinkType parsedDrink;
            if (Enum.TryParse(video.targetDrinkType?.Replace(" ", string.Empty), true, out parsedDrink))
                video.targetDrink = parsedDrink;
            if ((int)video.duration == 0 && (video.durationSeconds == 5 || video.durationSeconds == 10 || video.durationSeconds == 15))
                video.duration = (VideoDuration)video.durationSeconds;
            if (video.durationSeconds == 0) video.durationSeconds = (int)video.duration;
            if (video.isExclusiveToPurchasers) video.category = VideoType.VIPExclusive;
            video.isExclusiveToPurchasers = video.category == VideoType.VIPExclusive;
            int required = Mathf.Max(1, Mathf.Max(video.minDrinksRequiredToUnlock, video.requiredDrinkPurchases));
            video.minDrinksRequiredToUnlock = required;
            video.requiredDrinkPurchases = required;
            int impressions = Mathf.Max(video.impressionCount, video.totalImpressions);
            video.impressionCount = video.totalImpressions = impressions;
            int conversions = Mathf.Max(video.conversionCount, video.completedPurchases);
            video.conversionCount = video.completedPurchases = conversions;
            return video;
        }

        private static StaffVisualProfile CloneStaff(StaffVisualProfile profile)
        {
            if (profile == null) return null;
            return new StaffVisualProfile
            {
                staffId = profile.staffId, displayName = profile.displayName, gender = profile.gender,
                role = profile.role, primaryPromptVideoId = profile.primaryPromptVideoId,
                assignedVipVideoIds = profile.assignedVipVideoIds == null
                    ? new List<string>() : new List<string>(profile.assignedVipVideoIds),
                defaultAvatarSprite = profile.defaultAvatarSprite
            };
        }

        private static VideoCreative CloneVideo(VideoCreative value)
        {
            if (value == null) return null;
            return new VideoCreative
            {
                videoId = value.videoId, title = value.title, cdnUrl = value.cdnUrl,
                duration = value.duration, category = value.category, targetDrinkType = value.targetDrinkType,
                minDrinksRequiredToUnlock = value.minDrinksRequiredToUnlock,
                impressionCount = value.impressionCount, conversionCount = value.conversionCount,
                creativeId = value.creativeId, waiterName = value.waiterName, waiterId = value.waiterId,
                videoTitle = value.videoTitle, durationSeconds = value.durationSeconds, videoUrl = value.videoUrl,
                targetDrink = value.targetDrink, totalImpressions = value.totalImpressions,
                totalClicks = value.totalClicks, completedPurchases = value.completedPurchases,
                isExclusiveToPurchasers = value.isExclusiveToPurchasers,
                requiredDrinkPurchases = value.requiredDrinkPurchases
            };
        }

        private static StaffVisualProfile ParseStaffProfile(string json, Sprite fallbackSprite)
        {
            try
            {
                StaffProfileEnvelopeDto envelope = JsonUtility.FromJson<StaffProfileEnvelopeDto>(json);
                StaffVisualProfile value = envelope?.staff?.ToModel() ?? JsonUtility.FromJson<StaffProfileDto>(json)?.ToModel();
                if (value != null) value.defaultAvatarSprite = fallbackSprite;
                return value;
            }
            catch { return null; }
        }

        private static VideoCreative ParseVideo(string json)
        {
            try
            {
                VideoCreativeEnvelopeDto envelope = JsonUtility.FromJson<VideoCreativeEnvelopeDto>(json);
                return envelope?.video?.ToModel() ?? JsonUtility.FromJson<VideoCreativeDto>(json)?.ToModel();
            }
            catch { return null; }
        }

        private static PlayerPurchaseRecord ParsePurchaseRecord(string json)
        {
            try
            {
                PurchaseRecordEnvelopeDto envelope = JsonUtility.FromJson<PurchaseRecordEnvelopeDto>(json);
                PurchaseRecordDto dto = envelope?.record ?? JsonUtility.FromJson<PurchaseRecordDto>(json);
                return dto?.ToModel();
            }
            catch { return null; }
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
                    ? page.GetLeftPart(UriPartial.Authority) : string.Empty;
#endif
            }
            return origin.TrimEnd('/') + "/" + (path ?? string.Empty).TrimStart('/');
        }

        private void ReportError(string message)
        {
            Debug.LogWarning("StaffVideo: " + message);
            onStaffVideoError?.Invoke(message);
        }

        private static string ReadServerError(string json, string fallback)
        {
            try
            {
                ErrorDto value = JsonUtility.FromJson<ErrorDto>(json);
                if (!string.IsNullOrWhiteSpace(value?.error)) return value.error;
                if (!string.IsNullOrWhiteSpace(value?.message)) return value.message;
            }
            catch { }
            return fallback;
        }

        private static bool IsAccessDeniedResponse(string json)
        {
            try
            {
                ErrorDto value = JsonUtility.FromJson<ErrorDto>(json);
                return value != null && (value.status == 401 || value.status == 403 || value.code == "VIP_ACCESS_DENIED");
            }
            catch { return false; }
        }

        [Serializable] private class CachedPurchaseRecord
        { public PlayerPurchaseRecord record; public DateTime fetchedAtUtc; }
        [Serializable] private class ErrorDto
        { public string error; public string message; public string code; public int status; }
        [Serializable] private class AssignmentDto
        { public string staffId; public string videoId; public bool isVipMedia; }
        [Serializable] private class PlaybackGrantRequestDto
        { public string userSnapchatId; public string staffId; public string videoId; }
        [Serializable] private class PlaybackGrantDto
        { public bool granted; public string signedUrl; public string expiresAtUtc; }
        [Serializable] private class StaffProfileEnvelopeDto { public StaffProfileDto staff; }
        [Serializable] private class VideoCreativeEnvelopeDto { public VideoCreativeDto video; }
        [Serializable] private class PurchaseRecordEnvelopeDto { public PurchaseRecordDto record; }
        [Serializable] private class StaffProfileListDto { public List<StaffProfileDto> staff; }
        [Serializable] private class VideoCreativeListDto { public List<VideoCreativeDto> videos; }

        [Serializable]
        private class StaffProfileDto
        {
            public string staffId;
            public string displayName;
            public string gender;
            public string role;
            public string primaryPromptVideoId;
            public List<string> assignedVipVideoIds;

            public static StaffProfileDto FromModel(StaffVisualProfile value)
            {
                return new StaffProfileDto
                {
                    staffId = value.staffId, displayName = value.displayName,
                    gender = value.gender.ToString(), role = value.role.ToString(),
                    primaryPromptVideoId = value.primaryPromptVideoId,
                    assignedVipVideoIds = value.assignedVipVideoIds ?? new List<string>()
                };
            }

            public StaffVisualProfile ToModel()
            {
                StaffGender parsedGender;
                StaffRole parsedRole;
                if (!Enum.TryParse(gender, true, out parsedGender)) parsedGender = StaffGender.Female;
                if (!Enum.TryParse(role, true, out parsedRole)) parsedRole = StaffRole.Waiter;
                return new StaffVisualProfile
                {
                    staffId = staffId, displayName = displayName, gender = parsedGender, role = parsedRole,
                    primaryPromptVideoId = primaryPromptVideoId,
                    assignedVipVideoIds = assignedVipVideoIds ?? new List<string>()
                };
            }
        }

        [Serializable]
        private class VideoCreativeDto
        {
            public string videoId;
            public string title;
            public string cdnUrl;
            public string duration;
            public string category;
            public string targetDrinkType;
            public int minDrinksRequiredToUnlock;
            public int impressionCount;
            public int conversionCount;

            public static VideoCreativeDto FromModel(VideoCreative value)
            {
                value = NormalizeVideo(value);
                return new VideoCreativeDto
                {
                    videoId = value.videoId, title = value.title, cdnUrl = value.cdnUrl,
                    duration = value.duration.ToString(), category = value.category.ToString(),
                    targetDrinkType = value.targetDrinkType,
                    minDrinksRequiredToUnlock = value.minDrinksRequiredToUnlock,
                    impressionCount = value.impressionCount, conversionCount = value.conversionCount
                };
            }

            public VideoCreative ToModel()
            {
                VideoDuration parsedDuration;
                VideoType parsedCategory;
                if (!Enum.TryParse(duration, true, out parsedDuration)) parsedDuration = VideoDuration.Sec5;
                if (!Enum.TryParse(category, true, out parsedCategory)) parsedCategory = VideoType.StandardPrompt;
                return NormalizeVideo(new VideoCreative
                {
                    videoId = videoId, title = title, cdnUrl = cdnUrl, duration = parsedDuration,
                    category = parsedCategory, targetDrinkType = targetDrinkType,
                    minDrinksRequiredToUnlock = Mathf.Max(1, minDrinksRequiredToUnlock),
                    impressionCount = impressionCount, conversionCount = conversionCount
                });
            }
        }

        [Serializable]
        private class PurchaseRecordDto
        {
            public string userSnapchatId;
            public int totalDrinksPurchased;
            public float totalSpentUSD;
            public string lastPurchaseTimestampUtc;
            public List<string> unlockedExclusiveVideoIds;

            public PlayerPurchaseRecord ToModel()
            {
                DateTime timestamp;
                DateTime.TryParse(lastPurchaseTimestampUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);
                return new PlayerPurchaseRecord
                {
                    userSnapchatId = userSnapchatId,
                    totalDrinksPurchased = totalDrinksPurchased,
                    totalSpentUSD = totalSpentUSD,
                    lastPurchaseTimestamp = timestamp,
                    unlockedExclusiveVideoIds = unlockedExclusiveVideoIds ?? new List<string>()
                };
            }
        }
    }
}

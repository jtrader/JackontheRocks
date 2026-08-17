using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace JackOnTheRocks
{
    public enum PlayerGender { Female, Male, NonBinary, Unspecified }
    public enum SexualOrientation { Straight, Gay, Bisexual, Unspecified }
    public enum WaiterGender { Female, Male }

    [Serializable]
    public struct ParsedSnapchatProfile
    {
        public string snapchatUserId;
        public string userPhone;
        public System.DateTime dateOfBirth;
        public PlayerGender gender;
        public SexualOrientation orientation;
        public bool isAgeVerified;
    }

    [Serializable]
    public class ClothingLevel
    {
        public int level;
        public string name;
        public Sprite sprite;
    }

    [Serializable]
    public class WaiterProfile
    {
        public string waiterId;
        public string waiterName;
        public WaiterGender gender;
        public List<ClothingLevel> availableOutfits = new List<ClothingLevel>();
        public Sprite portraitSprite;
    }

    public class JackOnTheRocksUserMatchingManager : MonoBehaviour
    {
        public static JackOnTheRocksUserMatchingManager Instance { get; private set; }

        [Header("Waiter Library")]
        public List<WaiterProfile> waiterLibrary = new List<WaiterProfile>();

        [Header("Compliance")]
        public string complianceExitUrl = "https://example.com/compliance-exit";

        // Events
        public event Action<ParsedSnapchatProfile> onUserAccessVerified;
        public event Action<string> onUserAccessBlocked;
        public event Action<WaiterProfile> onWaiterAssignedToTable;
        public event Action<SexualOrientation> onOrientationPreferenceUpdated;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        #region Age Gate
        public void ValidateUserAge(ParsedSnapchatProfile profile)
        {
            try
            {
                var now = System.DateTime.UtcNow;
                int age = now.Year - profile.dateOfBirth.Year;
                if (profile.dateOfBirth > now.AddYears(-age)) age--;

                if (age < 18)
                {
                    // Block access
                    onUserAccessBlocked?.Invoke("ACCESS DENIED: You must be at least 18 years old to enter Jack on the Rocks.");
                    // Attempt to disable interactive buttons
                    DisableInteractiveButtons();
                    // Optionally unload table assets
                    UnloadTableAssets();
                    // Redirect to compliance page
                    try { Application.OpenURL(complianceExitUrl); } catch (Exception) { }
                    return;
                }

                profile.isAgeVerified = true;
                onUserAccessVerified?.Invoke(profile);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("ValidateUserAge failed: " + ex.Message);
                onUserAccessBlocked?.Invoke("ACCESS DENIED: Age verification failed.");
            }
        }

        private void DisableInteractiveButtons()
        {
            try
            {
                var buttons = FindObjectsOfType<UnityEngine.UI.Button>();
                foreach (var b in buttons) b.interactable = false;
            }
            catch { }
        }

        private void UnloadTableAssets()
        {
            try
            {
                var table = GameObject.Find("Table");
                if (table != null) table.SetActive(false);
            }
            catch { }
        }
        #endregion

        #region Matching Engine
        public WaiterGender DetermineTargetWaiterGender(ParsedSnapchatProfile profile)
        {
            // Apply rules
            var gender = profile.gender;
            var orientation = profile.orientation;

            if (orientation == SexualOrientation.Bisexual)
            {
                // Random assignment
                return (UnityEngine.Random.value > 0.5f) ? WaiterGender.Male : WaiterGender.Female;
            }

            if (profile.gender == PlayerGender.Female)
            {
                if (orientation == SexualOrientation.Gay) return WaiterGender.Female; // Rule B
                return WaiterGender.Male; // Rule A default
            }
            else if (profile.gender == PlayerGender.Male)
            {
                if (orientation == SexualOrientation.Gay) return WaiterGender.Male;
                return WaiterGender.Female;
            }

            // NonBinary or Unspecified: default to opposite-gender heuristic (fallback to Female waiter)
            return WaiterGender.Female;
        }

        public void AssignWaiterToTable(ParsedSnapchatProfile profile)
        {
            try
            {
                var target = DetermineTargetWaiterGender(profile);
                WaiterProfile selected = null;
                foreach (var w in waiterLibrary)
                {
                    if (w == null) continue;
                    if (target == WaiterGender.Female && w.gender == WaiterGender.Female) { selected = w; break; }
                    if (target == WaiterGender.Male && w.gender == WaiterGender.Male) { selected = w; break; }
                }
                if (selected == null && waiterLibrary.Count > 0) selected = waiterLibrary[0];

                // Broadcast assignment for UI to spawn/display
                onWaiterAssignedToTable?.Invoke(selected);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("AssignWaiterToTable failed: " + ex.Message);
            }
        }
        #endregion

        #region Parsing Bridge
        [Serializable]
        private class SnapchatPayload
        {
            public string id;
            public string phone;
            public string birthdate; // e.g., 1990-05-20
            public string gender; // male/female/other
            public string bio; // may contain orientation tags
        }

        public ParsedSnapchatProfile ParseSnapchatAuthPayload(string jsonPayload)
        {
            var result = new ParsedSnapchatProfile();
            try
            {
                if (string.IsNullOrEmpty(jsonPayload)) return result;
                var p = JsonUtility.FromJson<SnapchatPayload>(jsonPayload);
                result.snapchatUserId = p.id ?? string.Empty;
                result.userPhone = p.phone ?? string.Empty;
                DateTime dob;
                if (!DateTime.TryParse(p.birthdate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dob))
                {
                    // try common formats
                    DateTime.TryParseExact(p.birthdate, new[] { "yyyy-MM-dd", "MM/dd/yyyy", "dd-MM-yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out dob);
                }
                result.dateOfBirth = dob == default(DateTime) ? DateTime.MinValue : dob;
                result.gender = MapGenderString(p.gender);
                result.orientation = ParseOrientationFromBio(p.bio);
                result.isAgeVerified = false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("ParseSnapchatAuthPayload failed: " + ex.Message);
            }
            return result;
        }

        private PlayerGender MapGenderString(string g)
        {
            if (string.IsNullOrEmpty(g)) return PlayerGender.Unspecified;
            g = g.ToLowerInvariant();
            if (g.Contains("female") || g.Contains("f")) return PlayerGender.Female;
            if (g.Contains("male") || g.Contains("m")) return PlayerGender.Male;
            return PlayerGender.Unspecified;
        }

        private SexualOrientation ParseOrientationFromBio(string bio)
        {
            if (string.IsNullOrEmpty(bio)) return SexualOrientation.Unspecified;
            var b = bio.ToLowerInvariant();
            if (b.Contains("gay") || b.Contains("lesbian") || b.Contains("homosexual")) return SexualOrientation.Gay;
            if (b.Contains("bisexual") || b.Contains("bi")) return SexualOrientation.Bisexual;
            if (b.Contains("straight") || b.Contains("hetero")) return SexualOrientation.Straight;
            return SexualOrientation.Unspecified;
        }
        #endregion

        #region UI Testing Helpers
        public void OnSimulateSnapchatLogin(string jsonSample)
        {
            var profile = ParseSnapchatAuthPayload(jsonSample);
            ValidateUserAge(profile);
            if (profile.isAgeVerified)
            {
                onUserAccessVerified?.Invoke(profile);
                AssignWaiterToTable(profile);
            }
        }

        public void OnOverrideOrientationClicked(int orientationIndex)
        {
            SexualOrientation s = SexualOrientation.Unspecified;
            switch (orientationIndex)
            {
                case 0: s = SexualOrientation.Straight; break;
                case 1: s = SexualOrientation.Gay; break;
                case 2: s = SexualOrientation.Bisexual; break;
                default: s = SexualOrientation.Unspecified; break;
            }
            onOrientationPreferenceUpdated?.Invoke(s);
        }
        #endregion
    }
}

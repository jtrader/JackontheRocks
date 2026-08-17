using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace JackOnTheRocks
{
    [Serializable]
    public enum DrinkingHabitCategory { Tipsy, Social, ModerateAlcoholic, FullBlownAlcoholic }

    [Serializable]
    public struct SurveyTask
    {
        public string orderId;
        public string userSnapchatId;
        public string userPhone;
        public string assignedManagerSnapchatId;
        public System.DateTime initialPurchaseDate;
        public System.DateTime scheduledSurveyDate; // 30 days after purchase
        public bool isCompleted;
    }

    [Serializable]
    public struct SurveyResponsePayload
    {
        public string orderId;
        public string userSnapchatId;
        public string userPhone;
        public string assignedManagerId;
        public int waiterRatingStars; // 1-5
        public int estimatedDrinksOrdered;
        public DrinkingHabitCategory selfReportedHabit;
        public System.DateTime submissionTimestamp;
    }

    [Serializable]
    internal class SurveyTaskList { public List<SurveyTask> tasks = new List<SurveyTask>(); }

    public class JackOnTheRocksSurveyManager : MonoBehaviour
    {
        // Singleton
        private static JackOnTheRocksSurveyManager _instance;
        public static JackOnTheRocksSurveyManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("JackOnTheRocksSurveyManager");
                    _instance = go.AddComponent<JackOnTheRocksSurveyManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        // --- Events for UI/testing
        public event Action<string, System.DateTime> onSurveyScheduled;
        public event Action<string> onSurveyTriggered;
        public event Action<SurveyResponsePayload> onSurveyCompleted;
        public event Action<string> onTranscriptDispatched;

        // Admin Snap handle (hardcoded per requirements)
        public const string AdminSnapHandle = "jackontherocks_admin";

        [Header("Survey Scheduler")]
        [Tooltip("Poll interval in seconds to check pending surveys (default 60s)")]
        public int pollIntervalSeconds = 60;

        [Header("Snapchat API")]
        [Tooltip("Optional base URL for Snapchat Business API proxy (server recommended)")]
        public string snapchatApiBaseUrl = ""; // e.g. server proxy

        [Tooltip("Optional admin access token for Snapchat Business API (not recommended in production)")]
        public string adminSnapAccessToken = "";

        [Tooltip("If true attempt to fetch runtime tokens from a trusted server endpoint before dispatch")]
        public bool fetchTokensFromServer = true;
        [Tooltip("If set, used to fetch manager/admin tokens (e.g. https://example.com/api/manager/tokens)")]
        public string managerTokensEndpoint = "";

        private Dictionary<string, SurveyTask> _tasks = new Dictionary<string, SurveyTask>(StringComparer.OrdinalIgnoreCase);
        private string _persistPath;
        private HashSet<string> _welcomedUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);

            _persistPath = Path.Combine(Application.persistentDataPath, "jackontherocks_survey_tasks.json");
            LoadTasksFromDisk();
            StartCoroutine(CheckPendingSurveysLoop());
        }

        #region Scheduling & Persistence

        public void SchedulePostPurchaseSurvey(object orderObj)
        {
            // Accept DrinkOrder or minimal object with required fields
            try
            {
                // Try to read common properties via dynamic cast/reflection when possible
                string orderId = null;
                DateTime purchaseTs = DateTime.UtcNow;
                string userSnap = null;
                string userPhone = null;
                string managerSnap = null;

                // Best-effort: if caller passes a DrinkOrder type we expect fields
                var type = orderObj?.GetType();
                if (type != null)
                {
                    var idProp = type.GetProperty("orderId") ?? type.GetProperty("OrderId");
                    var purchaseProp = type.GetProperty("purchaseTimestamp") ?? type.GetProperty("purchaseTime") ?? type.GetProperty("PurchaseTimestamp");
                    var userSnapProp = type.GetProperty("userSnapchatId") ?? type.GetProperty("customerSnapId");
                    var phoneProp = type.GetProperty("userPhone") ?? type.GetProperty("customerPhone");
                    var managerProp = type.GetProperty("assignedManager") ?? type.GetProperty("assignedManagerSnapchatId");

                    if (idProp != null) orderId = idProp.GetValue(orderObj)?.ToString();
                    if (purchaseProp != null)
                    {
                        var val = purchaseProp.GetValue(orderObj);
                        if (val is DateTime dt) purchaseTs = dt;
                        else
                        {
                            DateTime.TryParse(val?.ToString(), out purchaseTs);
                        }
                    }
                    if (userSnapProp != null) userSnap = userSnapProp.GetValue(orderObj)?.ToString();
                    if (phoneProp != null) userPhone = phoneProp.GetValue(orderObj)?.ToString();
                    if (managerProp != null)
                    {
                        var mgrVal = managerProp.GetValue(orderObj);
                        if (mgrVal != null)
                        {
                            // manager could be an object; attempt to get managerId or snapchat id
                            var mgrType = mgrVal.GetType();
                            var mgrIdProp = mgrType.GetProperty("managerId") ?? mgrType.GetProperty("snapchatBusinessAccountId") ?? mgrType.GetProperty("snapchatId");
                            if (mgrIdProp != null) managerSnap = mgrIdProp.GetValue(mgrVal)?.ToString();
                            else managerSnap = mgrVal.ToString();
                        }
                    }
                }

                if (string.IsNullOrEmpty(orderId)) orderId = Guid.NewGuid().ToString();

                var task = new SurveyTask()
                {
                    orderId = orderId,
                    userSnapchatId = userSnap ?? "",
                    userPhone = userPhone ?? "",
                    assignedManagerSnapchatId = managerSnap ?? "",
                    initialPurchaseDate = purchaseTs,
                    scheduledSurveyDate = purchaseTs.AddMonths(1),
                    isCompleted = false
                };

                _tasks[task.orderId] = task;
                SaveTasksToDisk();
                onSurveyScheduled?.Invoke(task.orderId, task.scheduledSurveyDate);
                // Track welcome for this user if available
                if (!string.IsNullOrEmpty(task.userSnapchatId) && !_welcomedUsers.Contains(task.userSnapchatId))
                {
                    // do not send welcome here; leave to TriggerWelcomeForOrder which can be called explicitly
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("SchedulePostPurchaseSurvey failed: " + ex.Message);
            }
        }

        private void SaveTasksToDisk()
        {
            try
            {
                var list = new SurveyTaskList();
                foreach (var kv in _tasks) list.tasks.Add(kv.Value);
                var json = JsonUtility.ToJson(list);
                File.WriteAllText(_persistPath, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Failed to save survey tasks: " + ex.Message);
            }
        }

        private void LoadTasksFromDisk()
        {
            try
            {
                if (!File.Exists(_persistPath)) return;
                var raw = File.ReadAllText(_persistPath);
                var list = JsonUtility.FromJson<SurveyTaskList>(raw);
                if (list?.tasks == null) return;
                _tasks.Clear();
                foreach (var t in list.tasks) _tasks[t.orderId] = t;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Failed to load survey tasks: " + ex.Message);
            }
        }

        #endregion

        #region Scheduler Loop

        private IEnumerator CheckPendingSurveysLoop()
        {
            while (true)
            {
                try
                {
                    CheckPendingSurveys();
                }
                catch (Exception e)
                {
                    Debug.LogWarning("CheckPendingSurveysLoop error: " + e.Message);
                }
                yield return new WaitForSeconds(Mathf.Max(5, pollIntervalSeconds));
            }
        }

        public void CheckPendingSurveys()
        {
            var now = DateTime.UtcNow;
            var due = new List<SurveyTask>();
            foreach (var kv in _tasks)
            {
                var t = kv.Value;
                if (!t.isCompleted && t.scheduledSurveyDate <= now)
                {
                    due.Add(t);
                }
            }

            foreach (var task in due)
            {
                // mark we triggered to avoid double sends
                var updated = task;
                updated.isCompleted = false; // we'll mark completed after receipt
                _tasks[task.orderId] = updated;
                SaveTasksToDisk();
                onSurveyTriggered?.Invoke(task.userSnapchatId);
                StartCoroutine(SendSurveyToUserCoroutine(task));
            }
        }

        #endregion

        #region Snapchat Dispatch

        private IEnumerator SendSurveyToUserCoroutine(SurveyTask task)
        {
            if (string.IsNullOrEmpty(task.userSnapchatId))
            {
                Debug.LogWarning("SendSurveyToUser: missing user snap id for order " + task.orderId);
                yield break;
            }

            // Build combined message sequence
            var greeting = "Hey from Jack on the Rocks! It’s been 1 month since your first drink purchase. Help us keep our casino service top-tier by answering 3 quick questions:";
            var q1 = "1) How would you rate the service by your waiter/area manager over the month? (1-5 Stars)";
            var q2 = "2) Roughly how many drinks have you ordered from the waiter since then?";
            var q3 = "3) How would you rate your drinking habits: Tipsy, Social, Moderate alcoholic, or Full blown alcoholic?";

            // Attempt best-effort send via snapchatApiBaseUrl or direct API placeholder
            string endpoint = snapchatApiBaseUrl.TrimEnd('/') + "/send_message";
            if (string.IsNullOrEmpty(snapchatApiBaseUrl)) endpoint = "https://api.snapchat.com/business/v1/messages"; // placeholder

            // Attempt to use admin token; if absent and configured, attempt to fetch from managerTokensEndpoint
            string token = adminSnapAccessToken;
            if (string.IsNullOrEmpty(token) && fetchTokensFromServer && !string.IsNullOrEmpty(managerTokensEndpoint))
            {
                yield return StartCoroutine(FetchAdminTokenCoroutine());
                token = adminSnapAccessToken; // FetchAdminTokenCoroutine should set this if present
            }

            if (string.IsNullOrEmpty(token))
            {
                Debug.LogWarning("No admin Snapchat token available; survey message not sent to " + task.userSnapchatId);
                yield break;
            }

            // send greeting + questions as a single payload (best-effort)
            var payloadObj = new Dictionary<string, object>() {
                { "to", task.userSnapchatId },
                { "from", AdminSnapHandle },
                { "message", greeting + "\n\n" + q1 + "\n" + q2 + "\n" + q3 },
                { "metadata", new Dictionary<string, object>{ { "orderId", task.orderId } } }
            };
            var json = SerializeToJson(payloadObj);

            using (var uwr = new UnityWebRequest(endpoint, "POST"))
            {
                byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
                uwr.uploadHandler = new UploadHandlerRaw(bodyRaw);
                uwr.downloadHandler = new DownloadHandlerBuffer();
                uwr.SetRequestHeader("Content-Type", "application/json");
                uwr.SetRequestHeader("Authorization", "Bearer " + token);
                yield return uwr.SendWebRequest();
                if (uwr.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning("SendSurveyToUser failed: " + uwr.error + " - " + uwr.downloadHandler.text);
                }
                else
                {
                    Debug.Log("Survey message sent to " + task.userSnapchatId + " for order " + task.orderId);
                }
            }
        }

        private IEnumerator FetchAdminTokenCoroutine()
        {
            if (string.IsNullOrEmpty(managerTokensEndpoint)) yield break;
            using (var uwr = UnityWebRequest.Get(managerTokensEndpoint))
            {
                uwr.SetRequestHeader("Accept", "application/json");
                yield return uwr.SendWebRequest();
                if (uwr.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning("FetchAdminToken failed: " + uwr.error);
                    yield break;
                }
                try
                {
                    var txt = uwr.downloadHandler.text;
                    var resp = JsonUtility.FromJson<ManagerTokensResponse>(txt);
                    if (resp?.tokens != null)
                    {
                        foreach (var t in resp.tokens)
                        {
                            if (t.managerId == AdminSnapHandle) { adminSnapAccessToken = t.token; break; }
                        }
                    }
                }
                catch (Exception ex) { Debug.LogWarning("Failed to parse manager tokens: " + ex.Message); }
            }
        }

        [Serializable]
        private class ManagerTokenEntry { public string managerId; public string token; }
        [Serializable]
        private class ManagerTokensResponse { public ManagerTokenEntry[] tokens; }

        [Serializable]
        private class SerializationWrapper { public Dictionary<string, object> map; public SerializationWrapper(Dictionary<string, object> m) { map = m; } }

        // Minimal JSON serializer for simple payloads (strings, numbers, nested dictionaries)
        private string SerializeToJson(object obj)
        {
            if (obj == null) return "null";
            if (obj is string s) return JsonEscape(s);
            if (obj is bool b) return b ? "true" : "false";
            if (obj is int || obj is long || obj is float || obj is double || obj is decimal) return Convert.ToString(obj, System.Globalization.CultureInfo.InvariantCulture);
            if (obj is DateTime dt) return JsonEscape(dt.ToString("o"));
            if (obj is Dictionary<string, object> dict)
            {
                var parts = new List<string>();
                foreach (var kv in dict)
                {
                    parts.Add(JsonEscape(kv.Key) + ":" + SerializeToJson(kv.Value));
                }
                return "{" + string.Join(",", parts) + "}";
            }
            if (obj is IEnumerable<object> list)
            {
                var parts = new List<string>();
                foreach (var e in list) parts.Add(SerializeToJson(e));
                return "[" + string.Join(",", parts) + "]";
            }
            // fallback to ToString
            return JsonEscape(obj.ToString());
        }

        private string JsonEscape(string s)
        {
            if (s == null) return "\"\"";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
        }

        #endregion

        #region Responses & Transcript Dispatch

        public void SubmitSurveyResponses(SurveyResponsePayload response)
        {
            try
            {
                response.submissionTimestamp = DateTime.UtcNow;
                // compile transcript
                var transcript = BuildTranscript(response);

                // mark task completed if exists
                if (!string.IsNullOrEmpty(response.orderId) && _tasks.ContainsKey(response.orderId))
                {
                    var t = _tasks[response.orderId];
                    t.isCompleted = true;
                    _tasks[response.orderId] = t;
                    SaveTasksToDisk();
                }

                onSurveyCompleted?.Invoke(response);

                // dispatch to assigned manager
                if (!string.IsNullOrEmpty(response.assignedManagerId))
                    StartCoroutine(SendTranscriptCoroutine(response.assignedManagerId, transcript));

                // dispatch to admin
                StartCoroutine(SendTranscriptCoroutine(AdminSnapHandle, transcript));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("SubmitSurveyResponses failed: " + ex.Message);
            }
        }

        private string BuildTranscript(SurveyResponsePayload r)
        {
            return
$"===============================================\nJACK ON THE ROCKS - 30-DAY SERVICE & HABIT AUDIT\n===============================================\nOrder ID: {r.orderId}\nCustomer Snap ID: {r.userSnapchatId} | Phone: {r.userPhone}\nAssigned Waiter/Manager: {r.assignedManagerId}\nDate of Audit: {r.submissionTimestamp:u}\n-----------------------------------------------\nQ1 Rating: {r.waiterRatingStars} / 5 Stars\nQ2 Drinks Count: ~{r.estimatedDrinksOrdered} drinks\nQ3 Self-Rated Habit: {r.selfReportedHabit}\n===============================================\n";
        }

        private IEnumerator SendTranscriptCoroutine(string destinationSnapId, string transcript)
        {
            if (string.IsNullOrEmpty(destinationSnapId))
            {
                Debug.LogWarning("SendTranscript: missing destination snap id");
                yield break;
            }

            string endpoint = snapchatApiBaseUrl.TrimEnd('/') + "/send_message";
            if (string.IsNullOrEmpty(snapchatApiBaseUrl)) endpoint = "https://api.snapchat.com/business/v1/messages";

            string token = adminSnapAccessToken;
            if (string.IsNullOrEmpty(token) && fetchTokensFromServer && !string.IsNullOrEmpty(managerTokensEndpoint))
            {
                yield return StartCoroutine(FetchAdminTokenCoroutine());
                token = adminSnapAccessToken;
            }

            if (string.IsNullOrEmpty(token))
            {
                Debug.LogWarning("No admin token available to send transcript to " + destinationSnapId);
                yield break;
            }

            var payload = new Dictionary<string, object>() {
                { "to", destinationSnapId },
                { "from", AdminSnapHandle },
                { "message", transcript }
            };
            var json = SerializeToJson(payload);

            using (var uwr = new UnityWebRequest(endpoint, "POST"))
            {
                byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
                uwr.uploadHandler = new UploadHandlerRaw(bodyRaw);
                uwr.downloadHandler = new DownloadHandlerBuffer();
                uwr.SetRequestHeader("Content-Type", "application/json");
                uwr.SetRequestHeader("Authorization", "Bearer " + token);
                yield return uwr.SendWebRequest();
                if (uwr.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning("SendTranscript failed: " + uwr.error + " - " + uwr.downloadHandler.text);
                }
                else
                {
                    onTranscriptDispatched?.Invoke(destinationSnapId);
                    Debug.Log("Transcript dispatched to " + destinationSnapId);
                }
            }
        }

        // Send an ad-hoc message from a given sender handle (fromHandle). If token not available, attempt to fetch.
        private IEnumerator SendMessageCoroutine(string destinationSnapId, string fromHandle, string message)
        {
            if (string.IsNullOrEmpty(destinationSnapId)) yield break;
            if (string.IsNullOrEmpty(fromHandle)) fromHandle = AdminSnapHandle;

            string endpoint = snapchatApiBaseUrl.TrimEnd('/') + "/send_message";
            if (string.IsNullOrEmpty(snapchatApiBaseUrl)) endpoint = "https://api.snapchat.com/business/v1/messages";

            string token = null;
            if (fromHandle == AdminSnapHandle) token = adminSnapAccessToken;

            if (string.IsNullOrEmpty(token) && fetchTokensFromServer && !string.IsNullOrEmpty(managerTokensEndpoint))
            {
                using (var uwr = UnityWebRequest.Get(managerTokensEndpoint))
                {
                    uwr.SetRequestHeader("Accept", "application/json");
                    yield return uwr.SendWebRequest();
                    if (uwr.result == UnityWebRequest.Result.Success)
                    {
                        try
                        {
                            var txt = uwr.downloadHandler.text;
                            var resp = JsonUtility.FromJson<ManagerTokensResponse>(txt);
                            if (resp?.tokens != null)
                            {
                                foreach (var t in resp.tokens)
                                {
                                    if (t.managerId == fromHandle) { token = t.token; break; }
                                    if (fromHandle == AdminSnapHandle && t.managerId == AdminSnapHandle) { adminSnapAccessToken = t.token; token = t.token; break; }
                                }
                            }
                        }
                        catch (Exception) { }
                    }
                }
            }

            if (string.IsNullOrEmpty(token)) { Debug.LogWarning("No token for sending message from " + fromHandle); yield break; }

            var payload = new Dictionary<string, object>() { { "to", destinationSnapId }, { "from", fromHandle }, { "message", message } };
            var json = SerializeToJson(payload);

            using (var uwr = new UnityWebRequest(endpoint, "POST"))
            {
                byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
                uwr.uploadHandler = new UploadHandlerRaw(bodyRaw);
                uwr.downloadHandler = new DownloadHandlerBuffer();
                uwr.SetRequestHeader("Content-Type", "application/json");
                uwr.SetRequestHeader("Authorization", "Bearer " + token);
                yield return uwr.SendWebRequest();
                if (uwr.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning("SendMessage failed: " + uwr.error + " - " + uwr.downloadHandler.text);
                }
                else
                {
                    Debug.Log($"Message from {fromHandle} sent to {destinationSnapId}");
                }
            }
        }

        /// <summary>
        /// When a user orders their first drink, this will ensure Admin and the assigned manager send a short welcome message.
        /// Accepts the same minimal order-like object as SchedulePostPurchaseSurvey.
        /// </summary>
        public void TriggerWelcomeForOrder(object orderObj)
        {
            try
            {
                string userSnap = null;
                string managerSnap = null;
                string waiterName = null;
                var type = orderObj?.GetType();
                if (type != null)
                {
                    var userSnapProp = type.GetProperty("userSnapchatId") ?? type.GetProperty("customerSnapId");
                    var managerProp = type.GetProperty("assignedManagerSnapchatId") ?? type.GetProperty("assignedManager");
                    var waiterProp = type.GetProperty("waiterName") ?? type.GetProperty("assignedWaiterName");
                    if (userSnapProp != null) userSnap = userSnapProp.GetValue(orderObj)?.ToString();
                    if (managerProp != null) managerSnap = managerProp.GetValue(orderObj)?.ToString();
                    if (waiterProp != null) waiterName = waiterProp.GetValue(orderObj)?.ToString();
                }

                if (string.IsNullOrEmpty(userSnap)) { Debug.LogWarning("TriggerWelcomeForOrder: missing userSnapchatId"); return; }
                if (_welcomedUsers.Contains(userSnap)) { Debug.Log("User already welcomed: " + userSnap); return; }

                // Admin welcome
                var adminMsg = $"Welcome to Jack on the Rocks! Congrats on your first drink — your waiter{(string.IsNullOrEmpty(waiterName)?"":" " + waiterName)} will take great care of you. Say hi to them in chat!";
                StartCoroutine(SendMessageCoroutine(userSnap, AdminSnapHandle, adminMsg));

                // Manager welcome (best-effort). If managerSnap missing, skip.
                if (!string.IsNullOrEmpty(managerSnap))
                {
                    var mgrMsg = $"Hi — I'm your area manager. If you need anything, message me here. Enjoy your time!";
                    StartCoroutine(SendMessageCoroutine(userSnap, managerSnap, mgrMsg));
                }

                _welcomedUsers.Add(userSnap);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("TriggerWelcomeForOrder failed: " + ex.Message);
            }
        }

        #endregion

        #region Testing Helpers

        // Immediately trigger survey for an order (simulate 30 days passing)
        public void OnSimulate30DaysPassed(string orderId)
        {
            if (string.IsNullOrEmpty(orderId)) return;
            if (!_tasks.ContainsKey(orderId)) return;
            var t = _tasks[orderId];
            t.scheduledSurveyDate = DateTime.UtcNow.AddSeconds(-1);
            _tasks[orderId] = t;
            SaveTasksToDisk();
            CheckPendingSurveys();
        }

        // Force submit a test survey payload to exercise transcript dispatch
        public void OnForceSubmitTestSurvey()
        {
            var payload = new SurveyResponsePayload()
            {
                orderId = Guid.NewGuid().ToString(),
                userSnapchatId = "test_user_snap",
                userPhone = "000-000-0000",
                assignedManagerId = "test_manager_snap",
                waiterRatingStars = 5,
                estimatedDrinksOrdered = 3,
                selfReportedHabit = DrinkingHabitCategory.Social,
                submissionTimestamp = DateTime.UtcNow
            };
            SubmitSurveyResponses(payload);
        }

        #endregion
    }
}

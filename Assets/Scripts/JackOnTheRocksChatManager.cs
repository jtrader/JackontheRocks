using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using PaymentDrinkOrder = JackOnTheRocks.JackOnTheRocksPaymentManager.DrinkOrder;
using PaymentOrderStatus = JackOnTheRocks.JackOnTheRocksPaymentManager.OrderStatus;

namespace JackOnTheRocks
{
    /// <summary>Supported real-time chat routing contexts.</summary>
    public enum ChatChannelType
    {
        TableGroup,
        PrivatePlayerToWaiter,
        StaffBroadcast,
        SystemNotification
    }

    /// <summary>Authenticated identity returned by the backend after Snapchat OAuth and JWT validation.</summary>
    [Serializable]
    public struct SnapchatAuthenticatedUser
    {
        /// <summary>Stable Snapchat external identifier.</summary>
        public string snapExternalId;
        /// <summary>Snapchat display name.</summary>
        public string displayName;
        /// <summary>URL of the user's Bitmoji avatar, when one is available.</summary>
        public string bitmojiAvatarUrl;
        /// <summary>Short-lived application JWT issued and verified by the backend.</summary>
        public string jwtAccessToken;
        /// <summary>Backend-authorized application role. Client-requested roles are not trusted.</summary>
        public StaffRole userRole;
        /// <summary>True only after the backend has validated the OAuth exchange and application JWT.</summary>
        public bool isAuthenticated;
    }

    /// <summary>Serializable chat message used by table, private, staff, and system channels.</summary>
    [Serializable]
    public struct ChatMessage
    {
        /// <summary>Globally unique idempotency identifier.</summary>
        public string messageId;
        /// <summary>Message routing channel.</summary>
        public ChatChannelType channelType;
        /// <summary>Blackjack table/group identifier.</summary>
        public string tableId;
        /// <summary>Snapchat external ID of the sender.</summary>
        public string senderSnapId;
        /// <summary>Display name shown beside the chat bubble.</summary>
        public string senderDisplayName;
        /// <summary>Bitmoji URL shown beside the chat bubble.</summary>
        public string senderBitmojiUrl;
        /// <summary>Snapchat external ID of the direct-message recipient.</summary>
        public string recipientSnapId;
        /// <summary>Plain-text message body. Transport encryption is provided by WSS.</summary>
        public string messageText;
        /// <summary>Optional order identifier associated with the conversation.</summary>
        public string attachedOrderId;
        /// <summary>UTC creation timestamp.</summary>
        public DateTime timestamp;
    }

    /// <summary>State and bounded history for one player-to-staff conversation.</summary>
    [Serializable]
    public class PrivateChatSession
    {
        /// <summary>Stable conversation identifier.</summary>
        public string sessionId;
        /// <summary>Player's Snapchat external ID.</summary>
        public string playerSnapId;
        /// <summary>Waiter or area manager's Snapchat external ID.</summary>
        public string waiterSnapId;
        /// <summary>Order associated with this chat, when applicable.</summary>
        public PaymentDrinkOrder associatedOrder;
        /// <summary>Chronological in-memory message history.</summary>
        public List<ChatMessage> messageHistory = new List<ChatMessage>();
    }

    /// <summary>
    /// View model for a card in the Waiter Chat &amp; Order Desk active-order queue.
    /// UI code can render the player identity, Bitmoji, distance, order, and chat button.
    /// </summary>
    [Serializable]
    public class WaiterOrderDeskItem
    {
        /// <summary>Active drink order.</summary>
        public PaymentDrinkOrder order;
        /// <summary>Player Snapchat external ID.</summary>
        public string playerSnapId;
        /// <summary>Player display name.</summary>
        public string playerDisplayName;
        /// <summary>Player Bitmoji avatar URL.</summary>
        public string playerBitmojiUrl;
        /// <summary>Player-to-assigned-staff distance in kilometres.</summary>
        public float distanceKm;
    }

    /// <summary>
    /// Persistent WebGL chat singleton. It enforces a backend-validated Snapchat session before
    /// releasing Unity canvases or obtaining a one-time WebSocket ticket, routes table and private
    /// messages, and supplies the Waiter Chat &amp; Order Desk view models and quick responses.
    ///
    /// Browser WebSocket APIs cannot set arbitrary Authorization headers. This manager therefore
    /// submits the JWT over HTTPS to obtain a single-use, short-lived ticket, then connects over WSS.
    /// The hosting page must run the companion preflight before createUnityInstance if the HTML
    /// canvas itself must not be created until OAuth completes.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class JackOnTheRocksChatManager : MonoBehaviour
    {
        private const int MaximumMessageLength = 500;
        private const int MaximumSessionHistory = 200;
        private const float HeartbeatIntervalSeconds = 25f;
        private const string RequiredScopes =
            "https://auth.snapchat.com/oauth2/api/user.external_id user.display_name user.bitmoji.avatar";

        private enum SocketState { Disconnected, FetchingTicket, Connecting, Connected }

        private static JackOnTheRocksChatManager instance;

        /// <summary>Global chat manager, created before the first scene loads.</summary>
        public static JackOnTheRocksChatManager Instance
        {
            get
            {
                if (instance == null)
                {
                    GameObject host = new GameObject(nameof(JackOnTheRocksChatManager));
                    instance = host.AddComponent<JackOnTheRocksChatManager>();
                }
                return instance;
            }
        }

        [Header("OAuth and Backend")]
        [SerializeField, Tooltip("Leave empty for the WebGL page origin; Editor defaults to localhost:3000.")]
        private string backendBaseUrl = string.Empty;
        [SerializeField] private string oauthStartPath = "/auth/snapchat/chat/start";
        [SerializeField] private string socketTicketPath = "/api/chat/socket-ticket";
        [SerializeField] private StaffRole requestedLoginRole = StaffRole.Player;

        [Header("Canvas Authentication Gate")]
        [SerializeField, Tooltip("Optional login-only Unity canvas that remains enabled while game canvases are gated.")]
        private Canvas authenticationCanvas;

        [Header("Chat UI Bindings (Optional)")]
        [SerializeField] private TMP_InputField tableMessageInput;
        [SerializeField] private TMP_InputField privateMessageInput;
        [SerializeField] private string activeTableId = string.Empty;
        [SerializeField] private string activePrivateRecipientSnapId = string.Empty;

        [Header("Connection Recovery")]
        [SerializeField, Min(1f)] private float initialReconnectDelaySeconds = 2f;
        [SerializeField, Min(2f)] private float maximumReconnectDelaySeconds = 30f;

        private readonly List<Canvas> gatedCanvases = new List<Canvas>();
        private readonly Dictionary<string, PrivateChatSession> privateSessions =
            new Dictionary<string, PrivateChatSession>(StringComparer.Ordinal);
        private readonly Dictionary<string, WaiterOrderDeskItem> orderDeskItems =
            new Dictionary<string, WaiterOrderDeskItem>(StringComparer.Ordinal);
        private SnapchatAuthenticatedUser currentUser;
        private PrivateChatSession activePrivateSession;
        private SocketState socketState = SocketState.Disconnected;
        private int socketHandle = -1;
        private int reconnectAttempt;
        private Coroutine reconnectCoroutine;
        private float lastHeartbeatRealtime;
        private bool canvasGateReleased;
        private string tableMessageDraft = string.Empty;
        private string privateMessageDraft = string.Empty;
        private JackOnTheRocksSpatialOrderMatcher observedOrderMatcher;

        /// <summary>Raised after backend OAuth/JWT validation succeeds.</summary>
        public event Action<SnapchatAuthenticatedUser> onAuthenticationValidated;
        /// <summary>Raised when a table-group message is received.</summary>
        public event Action<ChatMessage> onTableMessageReceived;
        /// <summary>Raised when a private player/staff message is received.</summary>
        public event Action<ChatMessage> onPrivateMessageReceived;
        /// <summary>Raised when the Waiter Order Desk opens a customer conversation.</summary>
        public event Action<PrivateChatSession> onWaiterOrderChatOpened;
        /// <summary>Raised for authentication, authorization, validation, or socket errors.</summary>
        public event Action<string> onChatError;
        /// <summary>Raised whenever the Waiter Chat &amp; Order Desk queue changes.</summary>
        public event Action<List<WaiterOrderDeskItem>> onWaiterOrderQueueUpdated;
        /// <summary>Raised when the socket connection state changes.</summary>
        public event Action<bool> onChatConnectionChanged;

        /// <summary>The currently authenticated user.</summary>
        public SnapchatAuthenticatedUser CurrentUser => currentUser;
        /// <summary>True only while the WebGL WebSocket is open.</summary>
        public bool IsChatConnected => socketState == SocketState.Connected;
        /// <summary>The private session currently displayed by the UI.</summary>
        public PrivateChatSession ActivePrivateSession => activePrivateSession;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void JOTR_Chat_StartOAuth(
            string url, string targetObject, string callbackMethod, string allowedOrigin);
        [DllImport("__Internal")] private static extern int JOTR_Chat_WebSocketConnect(
            string url, string targetObject, string openMethod, string messageMethod,
            string errorMethod, string closeMethod);
        [DllImport("__Internal")] private static extern int JOTR_Chat_WebSocketSend(int handle, string message);
        [DllImport("__Internal")] private static extern void JOTR_Chat_WebSocketClose(int handle, int code, string reason);
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void BootstrapBeforeSceneLoad()
        {
            if (instance == null)
            {
                GameObject host = new GameObject(nameof(JackOnTheRocksChatManager));
                host.AddComponent<JackOnTheRocksChatManager>();
            }
        }

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                instance.AdoptSerializedConfiguration(this);
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);
            SceneManager.sceneLoaded += HandleSceneLoaded;
            Canvas.willRenderCanvases += EnforceCanvasGateBeforeRender;
            GateUnauthenticatedCanvases();
        }

        private void Start()
        {
            observedOrderMatcher = JackOnTheRocksSpatialOrderMatcher.Instance;
            if (observedOrderMatcher != null)
                observedOrderMatcher.onOrderSuccessfullyMatched += HandleSpatialOrderMatched;
        }

        private void Update()
        {
            if (socketState != SocketState.Connected ||
                Time.realtimeSinceStartup - lastHeartbeatRealtime < HeartbeatIntervalSeconds) return;
            lastHeartbeatRealtime = Time.realtimeSinceStartup;
            SendSocketEnvelope(new OutboundEnvelopeDto { type = "ping" });
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            Canvas.willRenderCanvases -= EnforceCanvasGateBeforeRender;
            if (observedOrderMatcher != null)
                observedOrderMatcher.onOrderSuccessfullyMatched -= HandleSpatialOrderMatched;
            CloseSocket(1000, "Chat manager destroyed");
            if (instance == this) instance = null;
        }

        private void OnApplicationFocus(bool focused)
        {
            if (focused && currentUser.isAuthenticated && socketState == SocketState.Disconnected)
                ScheduleReconnect();
        }

        /// <summary>Sets the backend-authorized role requested by the login UI.</summary>
        public void SetRequestedLoginRole(int roleValue)
        {
            if (Enum.IsDefined(typeof(StaffRole), roleValue)) requestedLoginRole = (StaffRole)roleValue;
        }

        /// <summary>Sets the active table used by the no-argument group-message button hook.</summary>
        public void SetActiveTableId(string tableId) { activeTableId = (tableId ?? string.Empty).Trim(); }

        /// <summary>Sets the direct-message recipient selected by the private-message tabs.</summary>
        public void SetActivePrivateRecipient(string snapExternalId)
        {
            activePrivateRecipientSnapId = (snapExternalId ?? string.Empty).Trim();
        }

        /// <summary>Sets the group message draft when no TMP input field is assigned.</summary>
        public void SetTableMessageDraft(string text) { tableMessageDraft = text ?? string.Empty; }

        /// <summary>Sets the private message draft when no TMP input field is assigned.</summary>
        public void SetPrivateMessageDraft(string text) { privateMessageDraft = text ?? string.Empty; }

        /// <summary>
        /// Assigns optional Unity UI bindings to the pre-scene bootstrap instance. This is useful
        /// when a scene installer configures the persistent manager at runtime.
        /// </summary>
        public void ConfigureUiBindings(Canvas loginCanvas, TMP_InputField tableInput, TMP_InputField privateInput)
        {
            authenticationCanvas = loginCanvas;
            tableMessageInput = tableInput;
            privateMessageInput = privateInput;
            if (!canvasGateReleased) GateUnauthenticatedCanvases();
        }

        /// <summary>Unity button hook that begins mandatory Snapchat OAuth with PKCE on the backend.</summary>
        public void OnLoginWithSnapchatClicked()
        {
            string url = BuildUrl(oauthStartPath) +
                "?role=" + UnityWebRequest.EscapeURL(requestedLoginRole.ToString()) +
                "&scope=" + UnityWebRequest.EscapeURL(RequiredScopes);
#if UNITY_WEBGL && !UNITY_EDITOR
            JOTR_Chat_StartOAuth(url, gameObject.name, nameof(OnSnapchatOAuthResult), GetOrigin(url));
#else
            ReportError("Snapchat OAuth requires a WebGL browser build.");
#endif
        }

        /// <summary>
        /// Receives the backend-validated OAuth result from the JavaScript bridge. The backend must
        /// validate authorization code, PKCE verifier, state, nonce, token signature, issuer,
        /// audience, expiry, and role before setting isAuthenticated.
        /// </summary>
        public void OnSnapchatOAuthResult(string json)
        {
            AuthResultDto result;
            try { result = JsonUtility.FromJson<AuthResultDto>(json); }
            catch { result = null; }
            StaffRole role;
            if (result == null || !result.success || !result.isAuthenticated ||
                string.IsNullOrWhiteSpace(result.snapExternalId) ||
                string.IsNullOrWhiteSpace(result.jwtAccessToken) ||
                !Enum.TryParse(result.userRole, true, out role))
            {
                ReportError(string.IsNullOrWhiteSpace(result?.error)
                    ? "Snapchat authentication could not be validated."
                    : result.error);
                return;
            }

            currentUser = new SnapchatAuthenticatedUser
            {
                snapExternalId = result.snapExternalId,
                displayName = result.displayName ?? string.Empty,
                bitmojiAvatarUrl = result.bitmojiAvatarUrl ?? string.Empty,
                jwtAccessToken = result.jwtAccessToken,
                userRole = role,
                isAuthenticated = true
            };
            ReleaseCanvasGate();
            onAuthenticationValidated?.Invoke(currentUser);
            InitializeWebSocketChat(currentUser);
        }

        /// <summary>
        /// Obtains a one-time WebSocket ticket for an authenticated Snapchat session and opens WSS.
        /// Unauthenticated users are rejected before any network socket is created.
        /// </summary>
        public void InitializeWebSocketChat(SnapchatAuthenticatedUser authenticatedUser)
        {
            if (!authenticatedUser.isAuthenticated || string.IsNullOrWhiteSpace(authenticatedUser.jwtAccessToken) ||
                string.IsNullOrWhiteSpace(authenticatedUser.snapExternalId))
            {
                ReportError("Snapchat authentication is required before chat can connect.");
                return;
            }
            if (socketState == SocketState.Connected || socketState == SocketState.Connecting ||
                socketState == SocketState.FetchingTicket) return;
            currentUser = authenticatedUser;
            StartCoroutine(FetchSocketTicketAndConnect());
        }

        /// <summary>Sends a group message to sockets authorized for the same Blackjack table.</summary>
        public void SendTableGroupMessage(string tableId, string messageText)
        {
            if (!ValidateSend(tableId, messageText, "table")) return;
            ChatMessage message = CreateMessage(ChatChannelType.TableGroup, tableId, string.Empty,
                messageText, string.Empty);
            SendChatMessage(message);
        }

        /// <summary>
        /// Sends a WSS-encrypted direct message. The backend must verify that the sender and
        /// recipient share an assigned order/table relationship before routing it.
        /// </summary>
        public void SendPrivateMessage(string recipientSnapId, string messageText, string orderId = "")
        {
            if (!ValidateSend(recipientSnapId, messageText, "recipient")) return;
            if (string.Equals(recipientSnapId, currentUser.snapExternalId, StringComparison.Ordinal))
            {
                ReportError("You cannot send a private message to yourself.");
                return;
            }
            ChatMessage message = CreateMessage(ChatChannelType.PrivatePlayerToWaiter, string.Empty,
                recipientSnapId, messageText, orderId);
            AppendPrivateMessage(message);
            SendChatMessage(message);
        }

        /// <summary>Unity button hook that sends the current group-chat input and clears it on enqueue.</summary>
        public void OnSendTableMessage()
        {
            string draft = tableMessageInput != null ? tableMessageInput.text : tableMessageDraft;
            SendTableGroupMessage(activeTableId, draft);
            if (socketState == SocketState.Connected)
            {
                if (tableMessageInput != null) tableMessageInput.text = string.Empty;
                tableMessageDraft = string.Empty;
            }
        }

        /// <summary>Unity button hook that sends the current direct-message input.</summary>
        public void OnSendPrivateMessage()
        {
            string draft = privateMessageInput != null ? privateMessageInput.text : privateMessageDraft;
            string orderId = activePrivateSession?.associatedOrder.orderId ?? string.Empty;
            SendPrivateMessage(activePrivateRecipientSnapId, draft, orderId);
            if (socketState == SocketState.Connected)
            {
                if (privateMessageInput != null) privateMessageInput.text = string.Empty;
                privateMessageDraft = string.Empty;
            }
        }

        /// <summary>
        /// Sends one of the Waiter Desk automated responses: 0 = preparing, 1 = on my way,
        /// 2 = delivered. The backend may use attachedOrderId to update the authoritative order state.
        /// </summary>
        public void OnWaiterQuickResponseClicked(int responseType)
        {
            if (!IsStaffRole(currentUser.userRole) || activePrivateSession == null)
            {
                ReportError("Open an assigned customer chat before using a quick response.");
                return;
            }
            string response;
            switch (responseType)
            {
                case 0: response = "Preparing Drink (5 min)"; break;
                case 1: response = "On My Way to Table"; break;
                case 2: response = "Drink Delivered! Enjoy"; break;
                default: ReportError("Unknown waiter quick-response type."); return;
            }
            SendPrivateMessage(activePrivateSession.playerSnapId, response,
                activePrivateSession.associatedOrder.orderId ?? string.Empty);
        }

        /// <summary>
        /// Opens the private customer thread for an order-card Chat with Customer button.
        /// The backend remains responsible for verifying assignment ownership.
        /// </summary>
        public void OpenWaiterOrderChat(string orderId)
        {
            if (!IsStaffRole(currentUser.userRole) || string.IsNullOrWhiteSpace(orderId))
            {
                ReportError("A staff login and valid order are required to open customer chat.");
                return;
            }
            WaiterOrderDeskItem item;
            if (!orderDeskItems.TryGetValue(orderId, out item) || item == null)
            {
                ReportError("The selected active order is no longer available.");
                return;
            }
            string sessionId = BuildSessionId(item.playerSnapId, currentUser.snapExternalId, orderId);
            PrivateChatSession session;
            if (!privateSessions.TryGetValue(sessionId, out session))
            {
                session = new PrivateChatSession
                {
                    sessionId = sessionId,
                    playerSnapId = item.playerSnapId,
                    waiterSnapId = currentUser.snapExternalId,
                    associatedOrder = item.order
                };
                privateSessions[sessionId] = session;
            }
            activePrivateSession = session;
            activePrivateRecipientSnapId = item.playerSnapId;
            onWaiterOrderChatOpened?.Invoke(session);
        }

        /// <summary>Adds or refreshes an active-order card in the Waiter Chat &amp; Order Desk.</summary>
        public void UpsertWaiterOrderDeskItem(WaiterOrderDeskItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.order.orderId) ||
                string.IsNullOrWhiteSpace(item.playerSnapId)) return;
            if (item.order.status == PaymentOrderStatus.OrderDelivered || item.order.status == PaymentOrderStatus.Rejected)
                orderDeskItems.Remove(item.order.orderId);
            else
                orderDeskItems[item.order.orderId] = item;
            PublishOrderDeskQueue();
        }

        /// <summary>Removes an order card after delivery, rejection, cancellation, or reassignment.</summary>
        public void RemoveWaiterOrderDeskItem(string orderId)
        {
            if (!string.IsNullOrWhiteSpace(orderId) && orderDeskItems.Remove(orderId))
                PublishOrderDeskQueue();
        }

        /// <summary>Closes the socket, clears credentials and histories, and re-enables the canvas gate.</summary>
        public void Logout()
        {
            CloseSocket(1000, "User logout");
            currentUser = default(SnapchatAuthenticatedUser);
            privateSessions.Clear();
            orderDeskItems.Clear();
            activePrivateSession = null;
            canvasGateReleased = false;
            GateUnauthenticatedCanvases();
        }

        /// <summary>WebGL callback invoked after the browser WebSocket opens.</summary>
        public void OnWebSocketOpened(string unused)
        {
            socketState = SocketState.Connected;
            reconnectAttempt = 0;
            lastHeartbeatRealtime = Time.realtimeSinceStartup;
            onChatConnectionChanged?.Invoke(true);
        }

        /// <summary>WebGL callback for a UTF-8 JSON WebSocket message.</summary>
        public void OnWebSocketMessage(string json)
        {
            InboundEnvelopeDto envelope;
            try { envelope = JsonUtility.FromJson<InboundEnvelopeDto>(json); }
            catch { envelope = null; }
            if (envelope == null)
            {
                ReportError("The chat server returned malformed data.");
                return;
            }
            if (string.Equals(envelope.type, "auth_error", StringComparison.Ordinal))
            {
                ReportError("The chat session expired. Sign in again.");
                Logout();
                return;
            }
            if (string.Equals(envelope.type, "order.assigned", StringComparison.Ordinal) &&
                envelope.orderDeskItem != null)
            {
                UpsertWaiterOrderDeskItem(envelope.orderDeskItem);
                return;
            }
            if (string.Equals(envelope.type, "order.removed", StringComparison.Ordinal))
            {
                RemoveWaiterOrderDeskItem(envelope.orderId);
                return;
            }
            if (!string.Equals(envelope.type, "chat.message", StringComparison.Ordinal) || envelope.message == null)
                return;

            ChatMessage message = envelope.message.ToModel();
            if (string.IsNullOrWhiteSpace(message.messageId) || string.IsNullOrWhiteSpace(message.senderSnapId))
                return;
            if (message.channelType == ChatChannelType.TableGroup)
                onTableMessageReceived?.Invoke(message);
            else if (message.channelType == ChatChannelType.PrivatePlayerToWaiter)
            {
                AppendPrivateMessage(message);
                onPrivateMessageReceived?.Invoke(message);
            }
        }

        /// <summary>WebGL callback for a browser socket error.</summary>
        public void OnWebSocketError(string reason)
        {
            ReportError(string.IsNullOrWhiteSpace(reason) ? "The chat connection failed." : reason);
            CloseSocket(1011, "WebSocket error");
            if (currentUser.isAuthenticated) ScheduleReconnect();
        }

        /// <summary>WebGL callback after the browser socket closes.</summary>
        public void OnWebSocketClosed(string closeJson)
        {
            socketHandle = -1;
            socketState = SocketState.Disconnected;
            onChatConnectionChanged?.Invoke(false);
            if (currentUser.isAuthenticated) ScheduleReconnect();
        }

        private IEnumerator FetchSocketTicketAndConnect()
        {
            socketState = SocketState.FetchingTicket;
            byte[] body = Encoding.UTF8.GetBytes("{}");
            using (UnityWebRequest request = new UnityWebRequest(BuildUrl(socketTicketPath), "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(body);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Accept", "application/json");
                request.SetRequestHeader("Authorization", "Bearer " + currentUser.jwtAccessToken);
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    socketState = SocketState.Disconnected;
                    ReportError(ReadServerError(request.downloadHandler?.text,
                        "The authenticated chat ticket could not be created."));
                    yield break;
                }
                SocketTicketDto ticket;
                try { ticket = JsonUtility.FromJson<SocketTicketDto>(request.downloadHandler.text); }
                catch { ticket = null; }
                if (ticket == null || string.IsNullOrWhiteSpace(ticket.ticket) ||
                    string.IsNullOrWhiteSpace(ticket.wssUrl) ||
                    !ticket.wssUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                {
                    socketState = SocketState.Disconnected;
                    ReportError("The backend returned an invalid secure chat ticket.");
                    yield break;
                }
                string separator = ticket.wssUrl.Contains("?") ? "&" : "?";
                string url = ticket.wssUrl + separator + "ticket=" + UnityWebRequest.EscapeURL(ticket.ticket);
                socketState = SocketState.Connecting;
#if UNITY_WEBGL && !UNITY_EDITOR
                socketHandle = JOTR_Chat_WebSocketConnect(url, gameObject.name,
                    nameof(OnWebSocketOpened), nameof(OnWebSocketMessage),
                    nameof(OnWebSocketError), nameof(OnWebSocketClosed));
                if (socketHandle < 0)
                {
                    socketState = SocketState.Disconnected;
                    ReportError("The browser could not create a secure WebSocket.");
                }
#else
                socketState = SocketState.Disconnected;
                ReportError("Real-time browser chat requires a WebGL build.");
#endif
            }
        }

        private void SendChatMessage(ChatMessage message)
        {
            if (socketState != SocketState.Connected)
            {
                ReportError("Chat is reconnecting. Please send the message again when connected.");
                return;
            }
            OutboundEnvelopeDto envelope = new OutboundEnvelopeDto
            {
                type = "chat.send",
                message = ChatMessageDto.FromModel(message)
            };
            SendSocketEnvelope(envelope);
        }

        private void SendSocketEnvelope(OutboundEnvelopeDto envelope)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            string json = JsonUtility.ToJson(envelope);
            if (socketHandle < 0 || JOTR_Chat_WebSocketSend(socketHandle, json) == 0)
                ReportError("The message could not be written to the chat socket.");
#endif
        }

        private bool ValidateSend(string routingId, string text, string routingLabel)
        {
            if (!currentUser.isAuthenticated)
            {
                ReportError("Snapchat authentication is required before sending chat messages.");
                return false;
            }
            if (socketState != SocketState.Connected)
            {
                ReportError("Chat is not connected.");
                return false;
            }
            if (string.IsNullOrWhiteSpace(routingId))
            {
                ReportError("A valid " + routingLabel + " is required.");
                return false;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                ReportError("Message text cannot be empty.");
                return false;
            }
            if (text.Trim().Length > MaximumMessageLength)
            {
                ReportError("Messages are limited to " + MaximumMessageLength + " characters.");
                return false;
            }
            return true;
        }

        private ChatMessage CreateMessage(ChatChannelType channel, string tableId,
            string recipient, string text, string orderId)
        {
            return new ChatMessage
            {
                messageId = Guid.NewGuid().ToString("N"),
                channelType = channel,
                tableId = tableId ?? string.Empty,
                senderSnapId = currentUser.snapExternalId,
                senderDisplayName = currentUser.displayName,
                senderBitmojiUrl = currentUser.bitmojiAvatarUrl,
                recipientSnapId = recipient ?? string.Empty,
                messageText = text.Trim(),
                attachedOrderId = orderId ?? string.Empty,
                timestamp = DateTime.UtcNow
            };
        }

        private void AppendPrivateMessage(ChatMessage message)
        {
            string otherParty = string.Equals(message.senderSnapId, currentUser.snapExternalId, StringComparison.Ordinal)
                ? message.recipientSnapId
                : message.senderSnapId;
            bool currentIsStaff = IsStaffRole(currentUser.userRole);
            string player = currentIsStaff ? otherParty : currentUser.snapExternalId;
            string waiter = currentIsStaff ? currentUser.snapExternalId : otherParty;
            string sessionId = FindExistingSessionId(player, waiter, message.attachedOrderId);
            PrivateChatSession session;
            if (!privateSessions.TryGetValue(sessionId, out session))
            {
                session = new PrivateChatSession
                {
                    sessionId = sessionId,
                    playerSnapId = player,
                    waiterSnapId = waiter,
                    associatedOrder = FindOrder(message.attachedOrderId)
                };
                privateSessions[sessionId] = session;
            }
            bool duplicate = session.messageHistory.Exists(m =>
                string.Equals(m.messageId, message.messageId, StringComparison.Ordinal));
            if (!duplicate) session.messageHistory.Add(message);
            while (session.messageHistory.Count > MaximumSessionHistory) session.messageHistory.RemoveAt(0);
            if (activePrivateSession != null && activePrivateSession.sessionId == sessionId)
                activePrivateSession = session;
        }

        private PaymentDrinkOrder FindOrder(string orderId)
        {
            WaiterOrderDeskItem item;
            return !string.IsNullOrWhiteSpace(orderId) && orderDeskItems.TryGetValue(orderId, out item) && item != null
                ? item.order
                : default(PaymentDrinkOrder);
        }

        private string FindExistingSessionId(string player, string waiter, string orderId)
        {
            string exact = BuildSessionId(player, waiter, orderId);
            if (privateSessions.ContainsKey(exact) || !string.IsNullOrWhiteSpace(orderId)) return exact;
            foreach (KeyValuePair<string, PrivateChatSession> pair in privateSessions)
            {
                PrivateChatSession session = pair.Value;
                if (session != null && string.Equals(session.playerSnapId, player, StringComparison.Ordinal) &&
                    string.Equals(session.waiterSnapId, waiter, StringComparison.Ordinal)) return pair.Key;
            }
            return exact;
        }

        private void HandleSpatialOrderMatched(MatchedOrderPayload match)
        {
            if (!IsStaffRole(currentUser.userRole) || match.assignedStaff == null ||
                !string.Equals(match.assignedStaff.snapchatUserId, currentUser.snapExternalId,
                    StringComparison.Ordinal)) return;
            UpsertWaiterOrderDeskItem(new WaiterOrderDeskItem
            {
                order = match.orderDetails,
                playerSnapId = match.playerProfile.snapchatUserId,
                playerDisplayName = match.playerProfile.snapchatUserId,
                playerBitmojiUrl = string.Empty,
                distanceKm = match.calculatedDistanceKm
            });
        }

        private void PublishOrderDeskQueue()
        {
            List<WaiterOrderDeskItem> queue = new List<WaiterOrderDeskItem>(orderDeskItems.Values);
            queue.Sort((a, b) => a.distanceKm.CompareTo(b.distanceKm));
            onWaiterOrderQueueUpdated?.Invoke(queue);
        }

        private void ScheduleReconnect()
        {
            if (reconnectCoroutine != null || !currentUser.isAuthenticated) return;
            reconnectCoroutine = StartCoroutine(ReconnectAfterDelay());
        }

        private IEnumerator ReconnectAfterDelay()
        {
            float delay = Mathf.Min(maximumReconnectDelaySeconds,
                initialReconnectDelaySeconds * Mathf.Pow(2f, reconnectAttempt++));
            yield return new WaitForSecondsRealtime(delay);
            reconnectCoroutine = null;
            if (currentUser.isAuthenticated && socketState == SocketState.Disconnected)
                InitializeWebSocketChat(currentUser);
        }

        private void CloseSocket(int code, string reason)
        {
            if (reconnectCoroutine != null)
            {
                StopCoroutine(reconnectCoroutine);
                reconnectCoroutine = null;
            }
#if UNITY_WEBGL && !UNITY_EDITOR
            if (socketHandle >= 0) JOTR_Chat_WebSocketClose(socketHandle, code, reason ?? string.Empty);
#endif
            socketHandle = -1;
            socketState = SocketState.Disconnected;
        }

        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!canvasGateReleased) GateUnauthenticatedCanvases();
        }

        private void AdoptSerializedConfiguration(JackOnTheRocksChatManager source)
        {
            if (source == null) return;
            if (!string.IsNullOrWhiteSpace(source.backendBaseUrl)) backendBaseUrl = source.backendBaseUrl;
            if (!string.IsNullOrWhiteSpace(source.oauthStartPath)) oauthStartPath = source.oauthStartPath;
            if (!string.IsNullOrWhiteSpace(source.socketTicketPath)) socketTicketPath = source.socketTicketPath;
            requestedLoginRole = source.requestedLoginRole;
            if (source.authenticationCanvas != null) authenticationCanvas = source.authenticationCanvas;
            if (source.tableMessageInput != null) tableMessageInput = source.tableMessageInput;
            if (source.privateMessageInput != null) privateMessageInput = source.privateMessageInput;
            if (!string.IsNullOrWhiteSpace(source.activeTableId)) activeTableId = source.activeTableId;
            initialReconnectDelaySeconds = source.initialReconnectDelaySeconds;
            maximumReconnectDelaySeconds = source.maximumReconnectDelaySeconds;
            if (!canvasGateReleased) GateUnauthenticatedCanvases();
        }

        private void EnforceCanvasGateBeforeRender()
        {
            if (!canvasGateReleased) GateUnauthenticatedCanvases();
        }

        private void GateUnauthenticatedCanvases()
        {
            Canvas[] canvases = FindObjectsOfType<Canvas>(true);
            for (int i = 0; i < canvases.Length; i++)
            {
                Canvas canvas = canvases[i];
                if (canvas == null || canvas == authenticationCanvas || !canvas.enabled) continue;
                canvas.enabled = false;
                if (!gatedCanvases.Contains(canvas)) gatedCanvases.Add(canvas);
            }
            if (authenticationCanvas != null) authenticationCanvas.enabled = true;
        }

        private void ReleaseCanvasGate()
        {
            canvasGateReleased = true;
            for (int i = 0; i < gatedCanvases.Count; i++)
                if (gatedCanvases[i] != null) gatedCanvases[i].enabled = true;
            gatedCanvases.Clear();
        }

        private void ReportError(string reason)
        {
            Debug.LogWarning("Chat: " + reason);
            onChatError?.Invoke(reason);
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

        private static string BuildSessionId(string player, string waiter, string orderId)
        {
            return (player ?? string.Empty) + "|" + (waiter ?? string.Empty) + "|" + (orderId ?? string.Empty);
        }

        private static bool IsStaffRole(StaffRole role)
        {
            return role == StaffRole.Waiter || role == StaffRole.AreaManager || role == StaffRole.Admin;
        }

        private static bool TryParseUtc(string value, out DateTime timestamp)
        {
            return DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);
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
        [Serializable] private class SocketTicketDto { public string ticket; public string wssUrl; }
        [Serializable]
        private class AuthResultDto
        {
            public bool success;
            public string error;
            public string snapExternalId;
            public string displayName;
            public string bitmojiAvatarUrl;
            public string jwtAccessToken;
            public string userRole;
            public bool isAuthenticated;
        }

        [Serializable] private class OutboundEnvelopeDto
        {
            public string type;
            public ChatMessageDto message;
        }

        [Serializable] private class InboundEnvelopeDto
        {
            public string type;
            public ChatMessageDto message;
            public WaiterOrderDeskItem orderDeskItem;
            public string orderId;
        }

        [Serializable]
        private class ChatMessageDto
        {
            public string messageId;
            public string channelType;
            public string tableId;
            public string senderSnapId;
            public string senderDisplayName;
            public string senderBitmojiUrl;
            public string recipientSnapId;
            public string messageText;
            public string attachedOrderId;
            public string timestampUtc;

            public static ChatMessageDto FromModel(ChatMessage value)
            {
                return new ChatMessageDto
                {
                    messageId = value.messageId,
                    channelType = value.channelType.ToString(),
                    tableId = value.tableId,
                    senderSnapId = value.senderSnapId,
                    senderDisplayName = value.senderDisplayName,
                    senderBitmojiUrl = value.senderBitmojiUrl,
                    recipientSnapId = value.recipientSnapId,
                    messageText = value.messageText,
                    attachedOrderId = value.attachedOrderId,
                    timestampUtc = value.timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                };
            }

            public ChatMessage ToModel()
            {
                ChatChannelType parsedChannel;
                DateTime parsedTimestamp;
                if (!Enum.TryParse(channelType, true, out parsedChannel))
                    parsedChannel = ChatChannelType.SystemNotification;
                if (!TryParseUtc(timestampUtc, out parsedTimestamp)) parsedTimestamp = DateTime.UtcNow;
                return new ChatMessage
                {
                    messageId = messageId,
                    channelType = parsedChannel,
                    tableId = tableId,
                    senderSnapId = senderSnapId,
                    senderDisplayName = senderDisplayName,
                    senderBitmojiUrl = senderBitmojiUrl,
                    recipientSnapId = recipientSnapId,
                    messageText = messageText,
                    attachedOrderId = attachedOrderId,
                    timestamp = parsedTimestamp
                };
            }
        }
    }
}

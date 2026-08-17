using UnityEngine;

namespace JackOnTheRocks
{
    /// <summary>
    /// Simple demo helper that subscribes to manager events and exposes methods
    /// that can be wired to UI buttons in the Editor.
    /// </summary>
    public class JackOnTheRocksDemoUI : MonoBehaviour
    {
        [SerializeField] private int demoBet = 50;
        [SerializeField] private int demoWaiterIndex = 0;

        private JackOnTheRocksPaymentManager.SnapchatUserProfile? currentUserProfile = null;

        private void OnEnable()
        {
            if (JackOnTheRocksManager.Instance == null) return;
            JackOnTheRocksManager.Instance.onGameStateChanged += HandleGameStateChanged;
            JackOnTheRocksManager.Instance.onCardDealt += HandleCardDealt;
            JackOnTheRocksManager.Instance.onScoreUpdated += HandleScoreUpdated;
            JackOnTheRocksManager.Instance.onRocksBalanceUpdated += HandleBalanceUpdated;
            JackOnTheRocksManager.Instance.onCurrenciesUpdated += HandleCurrenciesUpdated;
            JackOnTheRocksManager.Instance.onGameResult += HandleGameResult;
            JackOnTheRocksManager.Instance.onDrinkPurchased += HandleDrinkPurchased;
            JackOnTheRocksManager.Instance.onWaiterStateChanged += HandleWaiterStateChanged;
            JackOnTheRocksPaymentManager.Instance.onPayIDInstructionsGenerated += HandlePayIDInstructions;
            JackOnTheRocksPaymentManager.Instance.onPaymentPending += HandlePaymentPending;
            JackOnTheRocksPaymentManager.Instance.onPaymentConfirmed += HandlePaymentConfirmed;
            JackOnTheRocksPaymentManager.Instance.onInAppNotificationTriggered += HandleInAppNotification;
            JackOnTheRocksPaymentManager.Instance.onUserProfileLoaded += HandleUserProfileLoaded;
        }

        private void OnDisable()
        {
            if (JackOnTheRocksManager.Instance == null) return;
            JackOnTheRocksManager.Instance.onGameStateChanged -= HandleGameStateChanged;
            JackOnTheRocksManager.Instance.onCardDealt -= HandleCardDealt;
            JackOnTheRocksManager.Instance.onScoreUpdated -= HandleScoreUpdated;
            JackOnTheRocksManager.Instance.onRocksBalanceUpdated -= HandleBalanceUpdated;
            JackOnTheRocksManager.Instance.onCurrenciesUpdated -= HandleCurrenciesUpdated;
            JackOnTheRocksManager.Instance.onGameResult -= HandleGameResult;
            JackOnTheRocksManager.Instance.onDrinkPurchased -= HandleDrinkPurchased;
            JackOnTheRocksManager.Instance.onWaiterStateChanged -= HandleWaiterStateChanged;
            if (JackOnTheRocksPaymentManager.Instance != null)
            {
                JackOnTheRocksPaymentManager.Instance.onPayIDInstructionsGenerated -= HandlePayIDInstructions;
                JackOnTheRocksPaymentManager.Instance.onPaymentPending -= HandlePaymentPending;
                JackOnTheRocksPaymentManager.Instance.onPaymentConfirmed -= HandlePaymentConfirmed;
                JackOnTheRocksPaymentManager.Instance.onInAppNotificationTriggered -= HandleInAppNotification;
                JackOnTheRocksPaymentManager.Instance.onUserProfileLoaded -= HandleUserProfileLoaded;
            }
        }

        private JackOnTheRocks.JackOnTheRocksPaymentManager.DrinkOrder? lastOrder = null;

        #region UI Methods (wire these to buttons)
        public void StartRound() => JackOnTheRocksManager.Instance?.StartRound(demoBet);
        public void HitButton() => JackOnTheRocksManager.Instance?.OnHitButtonClicked();
        public void StandButton() => JackOnTheRocksManager.Instance?.OnStandButtonClicked();
        public void DoubleDownButton() => JackOnTheRocksManager.Instance?.OnDoubleDownButtonClicked();
        public void BuyDrink(int index) => JackOnTheRocksManager.Instance?.OnBuyDrinkClicked(index);
        public void TipWaiter_Tip() => JackOnTheRocksManager.Instance?.OnTipWaiterClicked(demoWaiterIndex, (int)JackOnTheRocksManager.InteractionType.Tip);
        public void TipWaiter_RequestDance() => JackOnTheRocksManager.Instance?.OnTipWaiterClicked(demoWaiterIndex, (int)JackOnTheRocksManager.InteractionType.RequestDance);
        public void TipWaiter_Strip() => JackOnTheRocksManager.Instance?.OnTipWaiterClicked(demoWaiterIndex, (int)JackOnTheRocksManager.InteractionType.StripClothingItem);
        public void SaveState()
        {
            var mgr = JackOnTheRocksManager.Instance;
            if (mgr == null) return;
            var save = new SaveManager.SaveData { rocks = mgr.totalRocks, diamonds = mgr.totalDiamonds, currentBet = mgr.currentBet };
            var saver = FindObjectOfType<SaveManager>();
            if (saver == null) { Debug.LogWarning("SaveManager not found in scene"); return; }
            saver.SaveToDisk(save);
        }

        public void LoadState()
        {
            var saver = FindObjectOfType<SaveManager>();
            if (saver == null) { Debug.LogWarning("SaveManager not found in scene"); return; }
            var data = saver.LoadFromDisk();
            if (data == null) { Debug.Log("No valid save found"); return; }
            saver.ApplySaveDataToManager(data);
        }

        public void SendReceipt()
        {
            var saver = FindObjectOfType<SaveManager>();
            var mgr = JackOnTheRocksManager.Instance;
            if (saver == null || mgr == null) { Debug.LogWarning("Missing SaveManager or Manager"); return; }
            var save = new SaveManager.SaveData { rocks = mgr.totalRocks, diamonds = mgr.totalDiamonds, currentBet = mgr.currentBet };
            StartCoroutine(saver.SubmitReceiptToServerCoroutine(save, (success, serverSig) =>
            {
                Debug.Log($"Server receipt verification: {success}. ServerSig={serverSig}");
            }));
        }

        public void CopyPayIDEmail()
        {
            if (lastOrder == null) return;
            GUIUtility.systemCopyBuffer = lastOrder.Value.targetPayIDEmail;
            Debug.Log("Copied PayID email to clipboard: " + lastOrder.Value.targetPayIDEmail);
        }

        public void IHaveSentPayment()
        {
            if (lastOrder == null) { Debug.LogWarning("No active order"); return; }
            // Start polling order status via manager helper
            JackOnTheRocksPaymentManager.Instance.StartPollingOrder(lastOrder.Value.orderID, 60);
            Debug.Log("Started polling for order: " + lastOrder.Value.orderID);
        }
        #endregion

        #region Event Handlers
        private void HandleGameStateChanged(JackOnTheRocksManager.HandState state)
        {
            Debug.Log("GameState: " + state);
        }

        private void HandleCardDealt(JackOnTheRocksManager.Card card, bool isPlayer, bool isFaceDown)
        {
            Debug.Log($"Card Dealt: {card?.rank} of {card?.suit} to {(isPlayer?"Player":"Dealer")} faceDown={isFaceDown}");
        }

        private void HandleScoreUpdated(int player, int dealer)
        {
            Debug.Log($"Scores - Player: {player} Dealer: {dealer}");
        }

        private void HandleBalanceUpdated(int rocks, int bet)
        {
            Debug.Log($"Balance: {rocks} Rocks | Current Bet: {bet}");
        }

        private void HandleCurrenciesUpdated(int rocks, int diamonds, int bet)
        {
            Debug.Log($"Currencies - Rocks: {rocks} Diamonds: {diamonds} Bet: {bet}");
        }

        // Demo helpers to test Diamonds
        public void GrantDiamonds(int amount) => JackOnTheRocksManager.Instance?.AddDiamonds(amount);
        public void SpendDiamondTest(int amount) => JackOnTheRocksManager.Instance?.SpendDiamonds(amount);

        private void HandleGameResult(string msg, int rocks)
        {
            Debug.Log($"Result: {msg} ({rocks})");
        }

        private void HandleDrinkPurchased(string name, int rocksAdded)
        {
            Debug.Log($"Purchased {name} for +{rocksAdded} Rocks");
            // Schedule survey and trigger welcome on first purchase
            try
            {
                var purchaseObj = new
                {
                    orderId = System.Guid.NewGuid().ToString(),
                    purchaseTimestamp = System.DateTime.UtcNow,
                    userSnapchatId = currentUserProfile?.snapchatUserId ?? string.Empty,
                    userPhone = currentUserProfile?.userMobileNumber ?? string.Empty,
                    assignedManagerSnapchatId = "",
                    waiterName = name
                };

                // attempt to resolve manager via PaymentManager if available
                var payMgr = JackOnTheRocksPaymentManager.Instance;
                if (payMgr != null && currentUserProfile.HasValue)
                {
                    var profile = currentUserProfile.Value;
                    var mgr = payMgr.ResolveRegionalManager(profile.latitude, profile.longitude);
                    if (mgr != null) purchaseObj = new {
                        orderId = purchaseObj.orderId,
                        purchaseTimestamp = purchaseObj.purchaseTimestamp,
                        userSnapchatId = purchaseObj.userSnapchatId,
                        userPhone = purchaseObj.userPhone,
                        assignedManagerSnapchatId = mgr.snapchatBusinessAccountId ?? string.Empty,
                        waiterName = purchaseObj.waiterName
                    };
                }

                JackOnTheRocks.JackOnTheRocksSurveyManager.Instance.SchedulePostPurchaseSurvey(purchaseObj);
                JackOnTheRocks.JackOnTheRocksSurveyManager.Instance.TriggerWelcomeForOrder(purchaseObj);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("Failed to schedule welcome/survey: " + ex.Message);
            }
        }

        private void HandleUserProfileLoaded(JackOnTheRocksPaymentManager.SnapchatUserProfile profile)
        {
            currentUserProfile = profile;
        }

        private void HandleWaiterStateChanged(JackOnTheRocksManager.WaiterNPC waiter, JackOnTheRocksManager.InteractionType type, int tier)
        {
            Debug.Log($"Waiter {waiter?.waiterName} action {type} clothingTier={tier}");
        }

        private void HandlePayIDInstructions(JackOnTheRocksPaymentManager.DrinkOrder order)
        {
            lastOrder = order;
            // populate UI fields if present
            var emailTxt = GameObject.Find("PayIDEmailText")?.GetComponent<UnityEngine.UI.Text>();
            var descTxt = GameObject.Find("PayIDDescriptionText")?.GetComponent<UnityEngine.UI.Text>();
            var refTxt = GameObject.Find("PayIDReferenceText")?.GetComponent<UnityEngine.UI.Text>();
            var orderLabel = GameObject.Find("PayIDOrderLabel")?.GetComponent<UnityEngine.UI.Text>();
            if (emailTxt != null) emailTxt.text = "Email: " + order.targetPayIDEmail;
            if (descTxt != null) descTxt.text = "Description: " + order.requiredDescriptionPhone;
            if (refTxt != null) refTxt.text = "Reference: " + order.requiredReferenceDrinkName;
            if (orderLabel != null) orderLabel.text = $"Selected: {order.bundleTierIndex}x {order.requiredReferenceDrinkName} ({order.priceUSD.ToString("C2") } -> {order.rocksToGrant} Rocks)";
        }

        private void HandlePaymentPending(string orderID)
        {
            Debug.Log("Payment pending for order " + orderID);
        }

        private void HandlePaymentConfirmed(string orderID)
        {
            Debug.Log("Payment confirmed for order " + orderID);
            var confirmTxt = GameObject.Find("PayIDConfirmText")?.GetComponent<UnityEngine.UI.Text>();
            if (confirmTxt != null) confirmTxt.text = "Payment confirmed for order " + orderID;
        }

        private void HandleInAppNotification(string message)
        {
            var banner = GameObject.Find("BannerText")?.GetComponent<UnityEngine.UI.Text>();
            if (banner != null) banner.text = message;
        }
        #endregion
    }
}

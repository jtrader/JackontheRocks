using System;
using System.Collections.Generic;
using UnityEngine;

namespace JackOnTheRocks
{
    /// <summary>
    /// Core singleton manager for Blackjack game logic, economy, drink purchases and waiter interactions.
    /// Implements shoe/deck management, player/dealer hands, payouts in Rocks, and UI events.
    /// </summary>
    public class JackOnTheRocksManager : MonoBehaviour
    {
        #region Enums & Data

        public enum HandState { WaitingToStart, Betting, PlayerTurn, DealerTurn, GameOver }
        public enum CardSuit { Hearts, Diamonds, Clubs, Spades }
        public enum CardRank { Two = 2, Three, Four, Five, Six, Seven, Eight, Nine, Ten, Jack, Queen, King, Ace }
        public enum WaiterGender { Female, Male }
        public enum InteractionType { Tip, RequestDance, StripClothingItem }

        /// <summary>
        /// Serializable card representation.
        /// </summary>
        [Serializable]
        public class Card
        {
            public CardSuit suit;
            public CardRank rank;
            public int value;
            public Sprite sprite;

            public Card() { }

            public Card(CardSuit suit, CardRank rank, Sprite sprite = null)
            {
                this.suit = suit;
                this.rank = rank;
                this.sprite = sprite;
                this.value = GetBaseValue(rank);
            }

            private static int GetBaseValue(CardRank r)
            {
                if (r >= CardRank.Two && r <= CardRank.Ten) return (int)r;
                if (r == CardRank.Jack || r == CardRank.Queen || r == CardRank.King) return 10;
                return 11; // Ace default as 11, Hand logic will handle soft->hard
            }
        }

        /// <summary>
        /// Hand container with Ace soft/hard evaluation.
        /// </summary>
        [Serializable]
        public class Hand
        {
            public List<Card> cards = new List<Card>();

            public void Clear() => cards.Clear();

            public void AddCard(Card c) { if (c == null) return; cards.Add(c); }

            /// <summary>
            /// Returns the best score <=21 if possible, otherwise lowest bust value.
            /// </summary>
            public int GetBestScore()
            {
                int total = 0;
                int aces = 0;
                foreach (var c in cards)
                {
                    if (c == null) continue;
                    if (c.rank == CardRank.Ace) { aces++; total += 11; }
                    else total += c.value;
                }

                while (total > 21 && aces > 0)
                {
                    total -= 10; // convert an Ace from 11 to 1
                    aces--;
                }

                return total;
            }

            public bool IsNaturalBlackjack()
            {
                return cards.Count == 2 && GetBestScore() == 21;
            }

            public bool IsBusted() => GetBestScore() > 21;
        }

        /// <summary>
        /// Drink bundle configuration posted to inspector.
        /// </summary>
        [Serializable]
        public struct DrinkBundle
        {
            public string bundleName;
            public float priceUSD;
            public int rocksGranted;
            public int drinksCount;
        }

        /// <summary>
        /// Waiter NPC representation and state.
        /// </summary>
        [Serializable]
        public class WaiterNPC
        {
            public string waiterName;
            public WaiterGender gender;
            public int currentClothingTier; // 0 = fully clothed
            public int maxClothingTier = 2; // limits to non-explicit tiers
            public bool isPoleDancing;
            public int requiredRocksPerInteraction = 50;
            /// <summary>
            /// Optional rapport callback for tipping/positive interactions.
            /// </summary>
            public Action IncreaseRapport;

            public WaiterNPC() { }
            public WaiterNPC(string name, WaiterGender gender, int tier = 0, int cost = 50)
            {
                waiterName = name; this.gender = gender; currentClothingTier = tier; requiredRocksPerInteraction = cost;
            }
        }

        #endregion

        /// <summary>
        /// Set balances and current bet from a trusted source (demo-only API).
        /// </summary>
        public void SetBalances(int rocks, int diamonds, int bet)
        {
            totalRocks = Mathf.Max(0, rocks);
            totalDiamonds = Mathf.Max(0, diamonds);
            currentBet = Mathf.Max(0, bet);
            BroadcastBalance();
        }

        #region Singleton

        public static JackOnTheRocksManager Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        #endregion

        #region Serialized Config

        [Header("Economic Settings")]
        [SerializeField] private int startingRocks = 1000;
        [SerializeField] private int startingDiamonds = 10;
        [SerializeField] private int defaultBet = 50;

        [Header("Deck / Shoe")]
        [SerializeField, Range(1, 6)] private int deckCount = 6;
        [SerializeField, Tooltip("Reshuffle when shoe capacity falls below this percent.")]
        private float reshuffleThresholdPercent = 0.2f;

        [Header("Drink Bundles")]
        [SerializeField] private List<DrinkBundle> drinkBundles = new List<DrinkBundle>()
        {
            new DrinkBundle(){ bundleName = "1 Drink", priceUSD = 5.0f, rocksGranted = 100, drinksCount = 1 },
            new DrinkBundle(){ bundleName = "3 Drinks", priceUSD = 10.0f, rocksGranted = 300, drinksCount = 3 },
            new DrinkBundle(){ bundleName = "5 Drinks", priceUSD = 15.0f, rocksGranted = 500, drinksCount = 5 },
            new DrinkBundle(){ bundleName = "10 Drinks", priceUSD = 25.0f, rocksGranted = 1000, drinksCount = 10 }
        };

        #endregion

        #region Runtime State

        public HandState CurrentState { get; private set; } = HandState.WaitingToStart;

        public Hand playerHand = new Hand();
        public Hand dealerHand = new Hand();

        private List<Card> shoe = new List<Card>();
        private int initialShoeSize = 0;
        private System.Random rng = new System.Random();

        /// <summary>
        /// Player's Rocks balance (primary in-game currency).
        /// </summary>
        public int totalRocks { get; private set; }

        /// <summary>
        /// Player's Diamonds balance (premium currency).
        /// </summary>
        public int totalDiamonds { get; private set; }
        public int currentBet { get; private set; }

        [SerializeField] private List<WaiterNPC> activeWaiters = new List<WaiterNPC>();

        #endregion

        #region Events / UI Hooks

        public event Action<HandState> onGameStateChanged;
        public event Action<Card, bool, bool> onCardDealt; // card, isPlayer, isFaceDown
        public event Action<int, int> onScoreUpdated; // playerScore, dealerScore
        /// <summary>
        /// Invoked when currency balances change. Parameters: currentRocks, currentDiamonds, currentBet
        /// </summary>
        public event Action<int, int, int> onCurrenciesUpdated;
        [Obsolete("onRocksBalanceUpdated is deprecated, use onCurrenciesUpdated instead.")]
        public event Action<int, int> onRocksBalanceUpdated; // currentRocks, currentBet
        public event Action<string, int> onGameResult; // message, rocksWon
        public event Action<string, int> onDrinkPurchased; // bundleName, rocksAdded
        public event Action<WaiterNPC, InteractionType, int> onWaiterStateChanged; // waiter, action, clothingTier

        #endregion

        #region Initialization

        private void Start()
        {
            totalRocks = startingRocks;
            totalDiamonds = startingDiamonds;
            BuildShoe();
            BroadcastBalance();
            CurrentState = HandState.WaitingToStart;
            onGameStateChanged?.Invoke(CurrentState);
        }

        private void BuildShoe()
        {
            shoe.Clear();
            for (int d = 0; d < deckCount; d++)
            {
                foreach (CardSuit s in Enum.GetValues(typeof(CardSuit)))
                {
                    foreach (CardRank r in Enum.GetValues(typeof(CardRank)))
                    {
                        shoe.Add(new Card(s, r, null));
                    }
                }
            }

            initialShoeSize = shoe.Count;
            ShuffleShoe();
        }

        private void ShuffleShoe()
        {
            int n = shoe.Count;
            for (int i = n - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                var tmp = shoe[i]; shoe[i] = shoe[j]; shoe[j] = tmp;
            }
        }

        private Card DrawCard(bool removeFromShoe = true)
        {
            if (shoe.Count == 0) { BuildShoe(); }
            if (shoe.Count == 0) return null;
            var c = shoe[0];
            if (removeFromShoe) shoe.RemoveAt(0);
            if (shoe.Count <= Mathf.CeilToInt(initialShoeSize * reshuffleThresholdPercent))
            {
                BuildShoe();
            }
            return c;
        }

        #endregion

        #region Game Flow

        /// <summary>
        /// Start a new round with a bet in Rocks.
        /// </summary>
        public bool StartRound(int bet)
        {
            if (bet <= 0) bet = defaultBet;
            if (CurrentState != HandState.WaitingToStart && CurrentState != HandState.GameOver && CurrentState != HandState.Betting) return false;
            if (totalRocks < bet) { onGameResult?.Invoke("Insufficient Rocks for bet", 0); return false; }

            currentBet = bet;
            totalRocks -= bet;
            BroadcastBalance();

            playerHand.Clear(); dealerHand.Clear();
            // Deal: player 2, dealer 2 (one face down)
            var p1 = DrawCard(); playerHand.AddCard(p1); onCardDealt?.Invoke(p1, true, false);
            var d1 = DrawCard(); dealerHand.AddCard(d1); onCardDealt?.Invoke(d1, false, false);
            var p2 = DrawCard(); playerHand.AddCard(p2); onCardDealt?.Invoke(p2, true, false);
            var d2 = DrawCard(); dealerHand.AddCard(d2); onCardDealt?.Invoke(d2, false, true); // face-down

            CurrentState = HandState.PlayerTurn;
            onGameStateChanged?.Invoke(CurrentState);
            onScoreUpdated?.Invoke(playerHand.GetBestScore(), dealerHand.GetBestScore());

            // Check natural blackjack
            bool playerBJ = playerHand.IsNaturalBlackjack();
            bool dealerBJ = dealerHand.IsNaturalBlackjack();
            if (playerBJ || dealerBJ)
            {
                ResolveNaturals(playerBJ, dealerBJ);
            }

            return true;
        }

        private void ResolveNaturals(bool playerBJ, bool dealerBJ)
        {
            // Reveal dealer card
            onCardDealt?.Invoke(dealerHand.cards[1], false, false);

            if (playerBJ && dealerBJ)
            {
                // Push
                totalRocks += currentBet; // refund
                onGameResult?.Invoke("Push: both have Blackjack", 0);
            }
            else if (playerBJ)
            {
                int payout = Mathf.FloorToInt(currentBet * 1.5f + currentBet); // return bet + 3:2
                totalRocks += payout;
                onGameResult?.Invoke("Player Blackjack! Paid 3:2", payout - currentBet);
            }
            else
            {
                onGameResult?.Invoke("Dealer Blackjack. Player loses.", 0);
            }

            BroadcastBalance();
            CurrentState = HandState.GameOver;
            onGameStateChanged?.Invoke(CurrentState);
        }

        public void Hit()
        {
            if (CurrentState != HandState.PlayerTurn) return;
            var c = DrawCard(); playerHand.AddCard(c); onCardDealt?.Invoke(c, true, false);
            onScoreUpdated?.Invoke(playerHand.GetBestScore(), dealerHand.GetBestScore());
            if (playerHand.IsBusted())
            {
                EndRound(false);
            }
            else if (playerHand.GetBestScore() == 21)
            {
                Stand();
            }
        }

        public void Stand()
        {
            if (CurrentState != HandState.PlayerTurn) return;
            CurrentState = HandState.DealerTurn; onGameStateChanged?.Invoke(CurrentState);
            // Reveal dealer hidden card
            if (dealerHand.cards.Count > 1) onCardDealt?.Invoke(dealerHand.cards[1], false, false);
            DealerPlay();
        }

        public void DoubleDown()
        {
            if (CurrentState != HandState.PlayerTurn) return;
            if (totalRocks < currentBet) { onGameResult?.Invoke("Insufficient Rocks to Double Down", 0); return; }
            totalRocks -= currentBet; currentBet *= 2; BroadcastBalance();
            var c = DrawCard(); playerHand.AddCard(c); onCardDealt?.Invoke(c, true, false);
            onScoreUpdated?.Invoke(playerHand.GetBestScore(), dealerHand.GetBestScore());
            if (playerHand.IsBusted()) { EndRound(false); return; }
            Stand();
        }

        private void DealerPlay()
        {
            // Dealer hits on soft 17
            bool dealerDone = false;
            while (!dealerDone)
            {
                int dealerScore = dealerHand.GetBestScore();
                bool hasSoftAce = DealerHasSoft17();
                if (dealerScore < 17 || (dealerScore == 17 && hasSoftAce))
                {
                    var c = DrawCard(); dealerHand.AddCard(c); onCardDealt?.Invoke(c, false, false);
                }
                else dealerDone = true;
            }

            // Evaluate
            EvaluateRound();
        }

        private bool DealerHasSoft17()
        {
            int total = 0; int aces = 0;
            foreach (var c in dealerHand.cards)
            {
                if (c.rank == CardRank.Ace) { aces++; total += 11; }
                else total += c.value;
            }
            // If any ace counted as 11 and total==17 -> soft 17
            while (total > 21 && aces > 0) { total -= 10; aces--; }
            return total == 17 && aces > 0;
        }

        private void EvaluateRound()
        {
            int playerScore = playerHand.GetBestScore();
            int dealerScore = dealerHand.GetBestScore();
            onScoreUpdated?.Invoke(playerScore, dealerScore);

            if (playerHand.IsBusted()) { EndRound(false); return; }
            if (dealerHand.IsBusted()) { EndRound(true); return; }

            if (playerScore > dealerScore) EndRound(true);
            else if (playerScore == dealerScore) { // push
                totalRocks += currentBet; onGameResult?.Invoke("Push", 0);
            }
            else EndRound(false);
        }

        private void EndRound(bool playerWon)
        {
            int rocksWon = 0;
            if (playerWon)
            {
                rocksWon = currentBet * 2; // return bet + winnings 1:1
                totalRocks += rocksWon;
                onGameResult?.Invoke("Player Wins", currentBet);
            }
            else
            {
                // bet already deducted
                onGameResult?.Invoke("Player Loses", 0);
            }

            BroadcastBalance();
            CurrentState = HandState.GameOver; onGameStateChanged?.Invoke(CurrentState);
        }

        private void BroadcastBalance()
        {
            onRocksBalanceUpdated?.Invoke(totalRocks, currentBet);
            onCurrenciesUpdated?.Invoke(totalRocks, totalDiamonds, currentBet);
        }

        #endregion

        #region Economy / Drinks / Waiter Interactions

        /// <summary>
        /// Simulates purchase of a drink bundle (no real-money gateway). Validates index and grants Rocks.
        /// </summary>
        public void PurchaseDrinkBundle(int bundleID)
        {
            if (bundleID < 0 || bundleID >= drinkBundles.Count) { onGameResult?.Invoke("Invalid drink bundle", 0); return; }
            var bundle = drinkBundles[bundleID];
            // In production, here you'd invoke platform purchase flow. We simulate and grant Rocks.
            totalRocks += bundle.rocksGranted;
            BroadcastBalance();
            onDrinkPurchased?.Invoke(bundle.bundleName, bundle.rocksGranted);
        }

        /// <summary>
        /// Adds Diamonds to the player's balance.
        /// </summary>
        public void AddDiamonds(int amount)
        {
            if (amount <= 0) return;
            totalDiamonds += amount;
            BroadcastBalance();
        }

        /// <summary>
        /// Attempts to spend Diamonds. Returns true if successful.
        /// </summary>
        public bool SpendDiamonds(int amount)
        {
            if (amount <= 0) return false;
            if (totalDiamonds < amount) return false;
            totalDiamonds -= amount;
            BroadcastBalance();
            return true;
        }

        /// <summary>
        /// Interact with a waiter NPC using an interaction type.
        /// </summary>
        public void InteractWithWaiter(int waiterIndex, InteractionType type)
        {
            if (waiterIndex < 0 || waiterIndex >= activeWaiters.Count) { onGameResult?.Invoke("Invalid waiter", 0); return; }
            var waiter = activeWaiters[waiterIndex];
            if (waiter == null) { onGameResult?.Invoke("Invalid waiter", 0); return; }

            int cost = waiter.requiredRocksPerInteraction;
            if (totalRocks < cost) { onGameResult?.Invoke("Insufficient Rocks for interaction", 0); return; }
            totalRocks -= cost; BroadcastBalance();

            switch (type)
            {
                case InteractionType.Tip:
                    // Increase rapport — placeholder for future system
                    waiter.IncreaseRapport?.Invoke();
                    onWaiterStateChanged?.Invoke(waiter, type, waiter.currentClothingTier);
                    onGameResult?.Invoke($"Tipped {waiter.waiterName}", -cost);
                    break;
                case InteractionType.RequestDance:
                    waiter.isPoleDancing = true;
                    onWaiterStateChanged?.Invoke(waiter, type, waiter.currentClothingTier);
                    onGameResult?.Invoke($"{waiter.waiterName} started dancing", -cost);
                    break;
                case InteractionType.StripClothingItem:
                    // Non-explicit: remove accessory or move to a more revealing but non-explicit tier.
                    waiter.currentClothingTier = Mathf.Min(waiter.currentClothingTier + 1, waiter.maxClothingTier);
                    onWaiterStateChanged?.Invoke(waiter, type, waiter.currentClothingTier);
                    onGameResult?.Invoke($"{waiter.waiterName} changed outfit tier", -cost);
                    break;
            }

        }

        #endregion

        #region UI Utility Methods

        public void OnHitButtonClicked() => Hit();
        public void OnStandButtonClicked() => Stand();
        public void OnDoubleDownButtonClicked() => DoubleDown();
        public void OnBuyDrinkClicked(int index) => PurchaseDrinkBundle(index);
        public void OnTipWaiterClicked(int waiterID, int interactionType)
        {
            if (!Enum.IsDefined(typeof(InteractionType), interactionType)) { onGameResult?.Invoke("Invalid interaction", 0); return; }
            InteractWithWaiter(waiterID, (InteractionType)interactionType);
        }

        #endregion

        #region Editor Helpers

        /// <summary>
        /// Add a waiter instance (editor or runtime utility).
        /// </summary>
        public void AddWaiter(WaiterNPC waiter)
        {
            if (waiter == null) return; activeWaiters.Add(waiter);
        }

        #endregion
    }
}

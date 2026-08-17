using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace JackOnTheRocks
{
    #region Enums & Data Structures
    /// <summary>
    /// Lifecycle states for a Blackjack round.
    /// </summary>
    public enum HandState
    {
        WaitingToStart,
        Betting,
        PlayerTurn,
        DealerTurn,
        GameOver
    }

    /// <summary>
    /// Card suits in a standard deck.
    /// </summary>
    public enum CardSuit
    {
        Hearts,
        Diamonds,
        Clubs,
        Spades
    }

    /// <summary>
    /// Card rank values from Two to Ace.
    /// </summary>
    public enum CardRank
    {
        Two = 2,
        Three = 3,
        Four = 4,
        Five = 5,
        Six = 6,
        Seven = 7,
        Eight = 8,
        Nine = 9,
        Ten = 10,
        Jack = 10,
        Queen = 10,
        King = 10,
        Ace = 11
    }

    /// <summary>
    /// Attire tier levels for the AI avatar.
    /// </summary>
    public enum ClothingLevel
    {
        FullyClothed,
        Lingerie
    }

    /// <summary>
    /// Represents an individual playing card in the Blackjack shoe.
    /// </summary>
    [Serializable]
    public class Card
    {
        public CardSuit suit;
        public CardRank rank;
        public int value;
        public Sprite cardSprite;

        public Card(CardSuit suit, CardRank rank, Sprite cardSprite = null)
        {
            this.suit = suit;
            this.rank = rank;
            this.value = (int)rank;
            this.cardSprite = cardSprite;
        }

        public override string ToString()
        {
            return $"{rank} of {suit}";
        }
    }

    /// <summary>
    /// Manages a collection of cards in a hand and evaluates totals, Aces, and Blackjack state.
    /// </summary>
    [Serializable]
    public class Hand
    {
        public List<Card> cards = new List<Card>();

        /// <summary>
        /// Clears all cards from the hand.
        /// </summary>
        public void Clear()
        {
            cards.Clear();
        }

        /// <summary>
        /// Adds a card to the hand.
        /// </summary>
        public void AddCard(Card card)
        {
            if (card != null)
            {
                cards.Add(card);
            }
        }

        /// <summary>
        /// Calculates the total score of the hand, dynamically reducing Ace values from 11 to 1 on bust.
        /// </summary>
        public int CalculateScore()
        {
            int total = 0;
            int aceCount = 0;

            foreach (var card in cards)
            {
                int val = card.value;
                if (card.rank == CardRank.Ace)
                {
                    aceCount++;
                    val = 11;
                }
                total += val;
            }

            while (total > 21 && aceCount > 0)
            {
                total -= 10;
                aceCount--;
            }

            return total;
        }

        /// <summary>
        /// Returns true if the hand total is a soft 17 (contains an Ace counted as 11 making total 17).
        /// </summary>
        public bool IsSoft17()
        {
            int rawTotal = 0;
            int aceCount = 0;

            foreach (var card in cards)
            {
                if (card.rank == CardRank.Ace)
                {
                    aceCount++;
                    rawTotal += 11;
                }
                else
                {
                    rawTotal += card.value;
                }
            }

            while (rawTotal > 21 && aceCount > 0)
            {
                rawTotal -= 10;
                aceCount--;
            }

            return rawTotal == 17 && aceCount > 0;
        }

        /// <summary>
        /// Returns true if the initial two cards equal 21 (Natural Blackjack).
        /// </summary>
        public bool IsNaturalBlackjack()
        {
            return cards.Count == 2 && CalculateScore() == 21;
        }

        /// <summary>
        /// Returns true if hand total exceeds 21.
        /// </summary>
        public bool IsBust()
        {
            return CalculateScore() > 21;
        }
    }

    /// <summary>
    /// Configuration parameter set for the dynamic AI 2D avatar generator.
    /// </summary>
    [Serializable]
    public struct AIAvatarConfig
    {
        public string characterPromptTags;
        public ClothingLevel currentClothing;
        public bool isPoleDancing;
        public string baseSeed;
        public WaiterGender gender;

        public AIAvatarConfig(string promptTags, ClothingLevel clothing = ClothingLevel.FullyClothed, bool poleDancing = false, string seed = "12345", WaiterGender gender = WaiterGender.Female)
        {
            this.characterPromptTags = promptTags;
            this.currentClothing = clothing;
            this.isPoleDancing = poleDancing;
            this.baseSeed = seed;
            this.gender = gender;
        }
    }

    #region JSON Transfer Objects
    [Serializable]
    internal class SDTxt2ImgRequest
    {
        public string prompt;
        public string negative_prompt;
        public int steps = 20;
        public int width = 512;
        public int height = 512;
        public long seed = -1;
    }

    [Serializable]
    internal class SDTxt2ImgResponse
    {
        public string[] images;
    }
    #endregion
    #endregion

    /// <summary>
    /// Production-ready singleton manager handling 2D AI avatar streaming, core Blackjack game logic,
    /// dual-currency economy (Rocks/Diamonds), and drink microtransactions.
    /// </summary>
    public class JackOnTheRocks2DAIManager : MonoBehaviour
    {
        #region Singleton Instance
        private static JackOnTheRocks2DAIManager _instance;

        /// <summary>
        /// Global singleton accessor. Instantiates persistent GameObject if missing.
        /// </summary>
        public static JackOnTheRocks2DAIManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindFirstObjectByType<JackOnTheRocks2DAIManager>();
                    if (_instance == null)
                    {
                        var go = new GameObject("JackOnTheRocks2DAIManager");
                        _instance = go.AddComponent<JackOnTheRocks2DAIManager>();
                        DontDestroyOnLoad(go);
                    }
                }
                return _instance;
            }
        }
        #endregion

        #region Serialized Inspector Settings
        [Header("AI Image Generation Settings")]
        [SerializeField]
        [Tooltip("HTTP endpoint for Automatic1111 or WebGPU txt2img API.")]
        private string sdApiUrl = "http://localhost:7860/sdapi/v1/txt2img";

        [SerializeField]
        [Tooltip("Initial base prompt describing the waiter/waitress avatar.")]
        private string initialCharacterPrompt = "sexy waitress, gorgeous face, detailed portrait, masterpiece, high quality";

        [Header("Blackjack Game Configuration")]
        [SerializeField]
        [Range(1, 6)]
        [Tooltip("Number of 52-card decks in the shoe.")]
        private int numberOfDecks = 4;

        [SerializeField]
        [Tooltip("Default bet amount in Rocks.")]
        private int defaultBet = 50;

        [Header("Economy Balances")]
        [SerializeField]
        private int totalRocks = 1000;

        [SerializeField]
        private int totalDiamonds = 10;
        #endregion

        #region Events
        public Action<HandState> onGameStateChanged;
        public Action<Card, bool, bool> onCardDealt; // card, isPlayer, isFaceDown
        public Action<int> onRocksBalanceUpdated;
        public Action<int, int> onCurrenciesUpdated; // rocks, diamonds
        public Action<string, int> onGameResult; // message, payoutInRocks
        public Action onAIAvatarLoadingStarted;
        public Action<Sprite> onAIAvatarSpriteUpdated;
        #endregion

        #region State Fields
        private HandState currentGameState = HandState.WaitingToStart;
        private List<Card> shoe = new List<Card>();
        private Hand playerHand = new Hand();
        private Hand dealerHand = new Hand();
        private Card dealerHiddenCard = null;
        private int currentBet = 0;

        private AIAvatarConfig activeAvatarConfig;
        private Sprite cachedAvatarSprite = null;
        #endregion

        #region Properties
        public HandState CurrentGameState => currentGameState;
        public int TotalRocks => totalRocks;
        public int TotalDiamonds => totalDiamonds;
        public int CurrentBet => currentBet;
        public Hand PlayerHand => playerHand;
        public Hand DealerHand => dealerHand;
        public AIAvatarConfig ActiveAvatarConfig => activeAvatarConfig;
        public Sprite CachedAvatarSprite => cachedAvatarSprite;
        #endregion

        #region Unity Lifecycle
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);

            InitializeGame();
        }

        private void Start()
        {
            onRocksBalanceUpdated?.Invoke(totalRocks);
            onCurrenciesUpdated?.Invoke(totalRocks, totalDiamonds);

            // Trigger initial avatar frame render
            RequestAIAvatarSprite(activeAvatarConfig, (sprite) =>
            {
                cachedAvatarSprite = sprite;
            });
        }
        #endregion

        #region Initialization & Shoe Management
        private void InitializeGame()
        {
            activeAvatarConfig = new AIAvatarConfig(initialCharacterPrompt, ClothingLevel.FullyClothed, false, "42", WaiterGender.Female);
            InitializeShoe();
        }

        /// <summary>
        /// Populates shoe with the specified number of decks and performs a Fisher-Yates shuffle.
        /// </summary>
        public void InitializeShoe()
        {
            shoe.Clear();
            for (int d = 0; d < numberOfDecks; d++)
            {
                foreach (CardSuit suit in Enum.GetValues(typeof(CardSuit)))
                {
                    foreach (CardRank rank in Enum.GetValues(typeof(CardRank)))
                    {
                        shoe.Add(new Card(suit, rank));
                    }
                }
            }
            ShuffleShoe();
        }

        /// <summary>
        /// Shuffles the card shoe using Fisher-Yates algorithm.
        /// </summary>
        public void ShuffleShoe()
        {
            var rng = new System.Random();
            int n = shoe.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                var value = shoe[k];
                shoe[k] = shoe[n];
                shoe[n] = value;
            }
        }

        private Card DrawCard()
        {
            int totalCapacity = numberOfDecks * 52;
            if (shoe.Count < totalCapacity * 0.2f)
            {
                InitializeShoe();
            }

            if (shoe.Count == 0)
            {
                InitializeShoe();
            }

            Card c = shoe[0];
            shoe.RemoveAt(0);
            return c;
        }
        #endregion

        #region Economy & Drink Store Microtransactions
        /// <summary>
        /// Drink Store bundle purchasing system.
        /// Bundle 0: 1 Drink  -> $5.00 -> 100 Rocks
        /// Bundle 1: 3 Drinks -> $10.00 -> 300 Rocks
        /// Bundle 2: 5 Drinks -> $15.00 -> 500 Rocks
        /// Bundle 3: 10 Drinks -> $25.00 -> 1,000 Rocks ("Shout the Table")
        /// </summary>
        /// <param name="bundleID">Bundle index (0..3)</param>
        public void PurchaseDrinkBundle(int bundleID)
        {
            int rocksToAdd = 0;
            switch (bundleID)
            {
                case 0: rocksToAdd = 100; break;
                case 1: rocksToAdd = 300; break;
                case 2: rocksToAdd = 500; break;
                case 3: rocksToAdd = 1000; break;
                default:
                    Debug.LogWarning("Invalid drink bundle ID: " + bundleID);
                    return;
            }

            totalRocks += rocksToAdd;
            onRocksBalanceUpdated?.Invoke(totalRocks);
            onCurrenciesUpdated?.Invoke(totalRocks, totalDiamonds);
            Debug.Log($"Purchased Drink Bundle {bundleID}: +{rocksToAdd} Rocks. New total: {totalRocks}");
        }

        /// <summary>
        /// Adds specified Rocks to player balance.
        /// </summary>
        public void AddRocks(int amount)
        {
            if (amount <= 0) return;
            totalRocks += amount;
            onRocksBalanceUpdated?.Invoke(totalRocks);
            onCurrenciesUpdated?.Invoke(totalRocks, totalDiamonds);
        }

        /// <summary>
        /// Adds specified Diamonds to player balance.
        /// </summary>
        public void AddDiamonds(int amount)
        {
            if (amount <= 0) return;
            totalDiamonds += amount;
            onCurrenciesUpdated?.Invoke(totalRocks, totalDiamonds);
        }
        #endregion

        #region Core Blackjack Game Logic
        /// <summary>
        /// Begins a new Blackjack round with the specified bet amount.
        /// </summary>
        public void StartRound(int betAmount = -1)
        {
            if (currentGameState == HandState.PlayerTurn || currentGameState == HandState.DealerTurn)
            {
                Debug.LogWarning("Round already in progress.");
                return;
            }

            int bet = betAmount > 0 ? betAmount : defaultBet;
            if (totalRocks < bet)
            {
                onGameResult?.Invoke("Insufficient Rocks to place bet!", 0);
                return;
            }

            totalRocks -= bet;
            currentBet = bet;
            onRocksBalanceUpdated?.Invoke(totalRocks);
            onCurrenciesUpdated?.Invoke(totalRocks, totalDiamonds);

            playerHand.Clear();
            dealerHand.Clear();
            dealerHiddenCard = null;

            SetGameState(HandState.Betting);

            // Deal 2 cards to Player
            Card p1 = DrawCard();
            playerHand.AddCard(p1);
            onCardDealt?.Invoke(p1, true, false);

            Card p2 = DrawCard();
            playerHand.AddCard(p2);
            onCardDealt?.Invoke(p2, true, false);

            // Deal 2 cards to Dealer (1 face-up, 1 face-down)
            Card d1 = DrawCard();
            dealerHand.AddCard(d1);
            onCardDealt?.Invoke(d1, false, false);

            dealerHiddenCard = DrawCard();
            dealerHand.AddCard(dealerHiddenCard);
            onCardDealt?.Invoke(dealerHiddenCard, false, true);

            // Check Natural Blackjack
            if (playerHand.IsNaturalBlackjack())
            {
                SetGameState(HandState.DealerTurn);
                onCardDealt?.Invoke(dealerHiddenCard, false, false); // reveal

                if (dealerHand.IsNaturalBlackjack())
                {
                    // Push
                    totalRocks += currentBet;
                    onRocksBalanceUpdated?.Invoke(totalRocks);
                    onCurrenciesUpdated?.Invoke(totalRocks, totalDiamonds);
                    EndRound("Push! Both hit Natural Blackjack.", currentBet);
                }
                else
                {
                    // 3:2 payout (bet + 1.5 * bet)
                    int payout = currentBet + Mathf.RoundToInt(currentBet * 1.5f);
                    totalRocks += payout;
                    onRocksBalanceUpdated?.Invoke(totalRocks);
                    onCurrenciesUpdated?.Invoke(totalRocks, totalDiamonds);
                    EndRound($"Natural Blackjack! Paid 3:2 (+{payout} Rocks)", payout);
                }
                return;
            }

            SetGameState(HandState.PlayerTurn);
        }

        /// <summary>
        /// Player action: Hit (draw additional card).
        /// </summary>
        public void Hit()
        {
            if (currentGameState != HandState.PlayerTurn) return;

            Card c = DrawCard();
            playerHand.AddCard(c);
            onCardDealt?.Invoke(c, true, false);

            if (playerHand.IsBust())
            {
                EndRound($"Player Busts with {playerHand.CalculateScore()}!", 0);
            }
        }

        /// <summary>
        /// Player action: Stand (end player turn and resolve dealer AI).
        /// </summary>
        public void Stand()
        {
            if (currentGameState != HandState.PlayerTurn) return;

            SetGameState(HandState.DealerTurn);
            ResolveDealerTurn();
        }

        /// <summary>
        /// Player action: Double Down (double bet, draw 1 card, then resolve dealer turn).
        /// </summary>
        public void DoubleDown()
        {
            if (currentGameState != HandState.PlayerTurn) return;
            if (totalRocks < currentBet)
            {
                Debug.LogWarning("Insufficient Rocks to Double Down.");
                return;
            }

            totalRocks -= currentBet;
            currentBet *= 2;
            onRocksBalanceUpdated?.Invoke(totalRocks);
            onCurrenciesUpdated?.Invoke(totalRocks, totalDiamonds);

            Card c = DrawCard();
            playerHand.AddCard(c);
            onCardDealt?.Invoke(c, true, false);

            if (playerHand.IsBust())
            {
                EndRound($"Bust on Double Down with {playerHand.CalculateScore()}!", 0);
            }
            else
            {
                SetGameState(HandState.DealerTurn);
                ResolveDealerTurn();
            }
        }

        private void ResolveDealerTurn()
        {
            // Reveal hidden card
            onCardDealt?.Invoke(dealerHiddenCard, false, false);

            // Dealer AI: hit on soft 17 or total < 17, stand on hard 17+
            while (dealerHand.CalculateScore() < 17 || (dealerHand.CalculateScore() == 17 && dealerHand.IsSoft17()))
            {
                Card c = DrawCard();
                dealerHand.AddCard(c);
                onCardDealt?.Invoke(c, false, false);
            }

            int pScore = playerHand.CalculateScore();
            int dScore = dealerHand.CalculateScore();

            if (dealerHand.IsBust())
            {
                int payout = currentBet * 2;
                totalRocks += payout;
                onRocksBalanceUpdated?.Invoke(totalRocks);
                onCurrenciesUpdated?.Invoke(totalRocks, totalDiamonds);
                EndRound($"Dealer Busts with {dScore}! You Win (+{payout} Rocks)!", payout);
            }
            else if (pScore > dScore)
            {
                int payout = currentBet * 2;
                totalRocks += payout;
                onRocksBalanceUpdated?.Invoke(totalRocks);
                onCurrenciesUpdated?.Invoke(totalRocks, totalDiamonds);
                EndRound($"You Win! {pScore} vs Dealer {dScore} (+{payout} Rocks)!", payout);
            }
            else if (pScore < dScore)
            {
                EndRound($"Dealer Wins {dScore} vs {pScore}.", 0);
            }
            else
            {
                // Push
                totalRocks += currentBet;
                onRocksBalanceUpdated?.Invoke(totalRocks);
                onCurrenciesUpdated?.Invoke(totalRocks, totalDiamonds);
                EndRound($"Push! Tie at {pScore}.", currentBet);
            }
        }

        private void EndRound(string resultMessage, int payoutText)
        {
            SetGameState(HandState.GameOver);
            onGameResult?.Invoke(resultMessage, payoutText);
        }

        private void SetGameState(HandState state)
        {
            currentGameState = state;
            onGameStateChanged?.Invoke(state);
        }
        #endregion

        #region AI Character Interactivity & Prompt State Machine
        /// <summary>
        /// Interacts with the AI waiter/waitress avatar.
        /// Type 0 (Tip): Deduct 20 Rocks, append happy expression tags, stream new frame.
        /// Type 1 (Dance): Deduct 50 Rocks, set isPoleDancing = true, stream dance pose.
        /// Type 2 (Strip): Deduct 100 Rocks, degrade ClothingLevel enum tier, rebuild tags, stream explicit sprite.
        /// </summary>
        /// <param name="interactionType">0=Tip, 1=Dance, 2=Strip</param>
        public void InteractWithAIAvatar(int interactionType)
        {
            int cost = 0;
            switch (interactionType)
            {
                case 0: cost = 20; break;
                case 1: cost = 50; break;
                case 2: cost = 100; break;
                default: break;
            }

            if (totalRocks < cost)
            {
                Debug.LogWarning($"Insufficient Rocks for interaction type {interactionType}. Cost: {cost}");
                return;
            }

            totalRocks -= cost;
            onRocksBalanceUpdated?.Invoke(totalRocks);
            onCurrenciesUpdated?.Invoke(totalRocks, totalDiamonds);

            switch (interactionType)
            {
                case 0: // Tip
                    activeAvatarConfig.characterPromptTags += ", happy, smiling, flirty, blushing";
                    break;
                case 1: // Dance
                    activeAvatarConfig.isPoleDancing = true;
                    activeAvatarConfig.characterPromptTags += ", pole dancing, athletic pose, motion blur, glowing neon lights";
                    break;
                case 2: // Strip
                    if (activeAvatarConfig.currentClothing == ClothingLevel.FullyClothed)
                    {
                        activeAvatarConfig.currentClothing = ClothingLevel.Lingerie;
                    }
                    activeAvatarConfig.characterPromptTags += ", seductive, (lingerie:1.2), sheer silk";
                    break;
            }

            // Stream new AI image sprite
            RequestAIAvatarSprite(activeAvatarConfig, (sprite) =>
            {
                cachedAvatarSprite = sprite;
            });
        }
        #endregion

        #region Async AI Image API Streamer
        /// <summary>
        /// Asynchronously sends a POST request to the external AI image generation endpoint (Automatic1111/WebGPU)
        /// and converts the returned base64 image into a Unity Sprite.
        /// </summary>
        /// <param name="config">AIAvatarConfig prompt settings</param>
        /// <param name="onSpriteReady">Callback invoked upon frame render completion</param>
        public void RequestAIAvatarSprite(AIAvatarConfig config, Action<Sprite> onSpriteReady)
        {
            StartCoroutine(RequestAIAvatarSpriteCoroutine(config, onSpriteReady));
        }

        private IEnumerator RequestAIAvatarSpriteCoroutine(AIAvatarConfig config, Action<Sprite> onSpriteReady)
        {
            onAIAvatarLoadingStarted?.Invoke();

            string positivePrompt = BuildPositivePrompt(config);
            string negativePrompt = "naked, (nude:1.3), ugly, deformed, extra limbs, bad anatomy, low quality, blurred";

            long seed = -1;
            long.TryParse(config.baseSeed, out seed);

            var reqData = new SDTxt2ImgRequest
            {
                prompt = positivePrompt,
                negative_prompt = negativePrompt,
                steps = 20,
                width = 512,
                height = 512,
                seed = seed
            };

            string jsonPayload = JsonUtility.ToJson(reqData);

            using (UnityWebRequest uwr = new UnityWebRequest(sdApiUrl, "POST"))
            {
                byte[] raw = System.Text.Encoding.UTF8.GetBytes(jsonPayload);
                uwr.uploadHandler = new UploadHandlerRaw(raw);
                uwr.downloadHandler = new DownloadHandlerBuffer();
                uwr.SetRequestHeader("Content-Type", "application/json");

                yield return uwr.SendWebRequest();

                if (uwr.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"AI Image API Request Failed ({uwr.error}). Using fallback procedural avatar sprite.");
                    Sprite fallback = CreateFallbackSprite();
                    onSpriteReady?.Invoke(fallback);
                    onAIAvatarSpriteUpdated?.Invoke(fallback);
                    yield break;
                }

                string jsonResponse = uwr.downloadHandler.text;
                Sprite generatedSprite = null;

                try
                {
                    var resp = JsonUtility.FromJson<SDTxt2ImgResponse>(jsonResponse);
                    if (resp != null && resp.images != null && resp.images.Length > 0)
                    {
                        string base64 = resp.images[0];
                        byte[] imageBytes = Convert.FromBase64String(base64);

                        Texture2D tex = new Texture2D(512, 512, TextureFormat.RGBA32, false);
                        if (tex.LoadImage(imageBytes))
                        {
                            generatedSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("Failed to parse base64 AI image response: " + ex.Message);
                }

                if (generatedSprite == null)
                {
                    generatedSprite = CreateFallbackSprite();
                }

                onSpriteReady?.Invoke(generatedSprite);
                onAIAvatarSpriteUpdated?.Invoke(generatedSprite);
            }
        }

        private string BuildPositivePrompt(AIAvatarConfig config)
        {
            string genderStr = config.gender.ToString().ToLower();
            string prompt = $"masterpiece, best quality, portrait of a sexy {genderStr} waiter, {config.characterPromptTags}";

            if (config.currentClothing == ClothingLevel.Lingerie)
            {
                prompt += ", (lingerie:1.2), lace, silk, seductive pose";
            }
            else
            {
                prompt += ", formal waiter outfit, stylish vest, bow tie";
            }

            if (config.isPoleDancing)
            {
                prompt += ", pole dancing, dynamic athletic pose, bar background";
            }

            return prompt;
        }

        private Sprite CreateFallbackSprite()
        {
            int w = 256, h = 256;
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            Color[] pixels = new Color[w * h];
            Color c1 = activeAvatarConfig.currentClothing == ClothingLevel.Lingerie ? new Color(0.8f, 0.2f, 0.4f) : new Color(0.1f, 0.3f, 0.6f);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; w > x; x++)
                {
                    float factor = (float)y / h;
                    pixels[y * w + x] = Color.Lerp(c1, Color.black, factor * 0.5f);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f));
        }
        #endregion
    }
}

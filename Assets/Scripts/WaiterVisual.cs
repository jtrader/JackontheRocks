using UnityEngine;
using UnityEngine.UI;

namespace JackOnTheRocks
{
    /// <summary>
    /// Visual helper for a waiter placeholder in the demo scene. Registers a WaiterNPC with the manager and
    /// updates UI visuals when the waiter state changes.
    /// </summary>
    public class WaiterVisual : MonoBehaviour
    {
        public string waiterName = "Waiter";
        public JackOnTheRocksManager.WaiterGender gender = JackOnTheRocksManager.WaiterGender.Female;
        public int initialClothingTier = 0;
        public int maxClothingTier = 2;

        [Header("Visuals")]
        [Tooltip("Optional sprites per clothing tier. Index 0 => tier 0, etc.")]
        public Sprite[] clothingTierSprites;
        [Tooltip("Optional animator to drive dance/clothing transitions.")]
        public Animator waiterAnimator;

        private JackOnTheRocksManager.WaiterNPC waiterModel;
        private Image image;

        private void Awake()
        {
            image = GetComponent<Image>();
            waiterModel = new JackOnTheRocksManager.WaiterNPC(waiterName, gender, initialClothingTier, 50);
            waiterModel.maxClothingTier = maxClothingTier;
            waiterModel.IncreaseRapport = () => { /* placeholder for rapport logic */ };
            JackOnTheRocksManager.Instance?.AddWaiter(waiterModel);
            JackOnTheRocksManager.Instance.onWaiterStateChanged += OnWaiterStateChanged;
            // Try to find an Animator on this object if none assigned
            if (waiterAnimator == null) waiterAnimator = GetComponent<Animator>();
            UpdateVisual(waiterModel.currentClothingTier);
        }

        private void OnDestroy()
        {
            if (JackOnTheRocksManager.Instance != null)
                JackOnTheRocksManager.Instance.onWaiterStateChanged -= OnWaiterStateChanged;
        }

        private void OnWaiterStateChanged(JackOnTheRocksManager.WaiterNPC model, JackOnTheRocksManager.InteractionType type, int tier)
        {
            if (model != waiterModel) return;
            UpdateVisual(tier);
        }

        private void UpdateVisual(int tier)
        {
            if (image == null) image = GetComponent<Image>();
            // If sprites are assigned, use corresponding sprite; otherwise fallback to color tint.
            if (clothingTierSprites != null && clothingTierSprites.Length > tier && clothingTierSprites[tier] != null)
            {
                image.sprite = clothingTierSprites[tier];
                image.color = Color.white;
            }
            else
            {
                switch (tier)
                {
                    case 0: image.color = Color.white; break; // fully clothed
                    case 1: image.color = new Color(1f, 0.9f, 0.6f); break; // lighter tint
                    case 2: image.color = new Color(1f, 0.6f, 0.6f); break; // stronger tint
                    default: image.color = Color.gray; break;
                }
            }

            // Update animator parameters if available
            if (waiterAnimator != null)
            {
                waiterAnimator.SetInteger("ClothingTier", tier);
                waiterAnimator.SetTrigger("ChangeClothing");
            }
            // Update label if present
            var label = transform.Find("Label")?.GetComponent<Text>();
            if (label != null) label.text = waiterName + " (Tier " + tier + ")";
        }

        /// <summary>
        /// External call to trigger dance visuals/animations.
        /// </summary>
        public void SetDancing(bool dancing)
        {
            if (waiterAnimator != null) waiterAnimator.SetBool("isDancing", dancing);
            // We can also tint or play a sprite animation here.
            if (image != null) image.transform.localScale = dancing ? Vector3.one * 1.02f : Vector3.one;
        }
    }
}

using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JackOnTheRocks.UI
{
    /// <summary>Sprite, branding, glow, and pricing configuration for one drink icon.</summary>
    [Serializable]
    public struct DrinkIconConfig
    {
        /// <summary>Stable drink identifier, such as JackDaniels_Rocks.</summary>
        public string drinkId;
        /// <summary>Brand name rendered in the Store modal.</summary>
        public string brandName;
        /// <summary>Transparent PNG sprite imported by Unity.</summary>
        public Sprite transparentIconSprite;
        /// <summary>Per-icon glow color. An unset color defaults to #80DEEA.</summary>
        public Color glowColor;
        /// <summary>Displayed price in USD.</summary>
        public float priceUSD;
    }

    /// <summary>
    /// Lightweight persistent controller for the five transparent Store drink icons. It performs
    /// allocation-free array lookup, reuses one runtime material, updates the active display, and
    /// animates selection with a single Update-driven pulse suitable for mobile WebGL.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class JackOnTheRocksDrinkIconManager : MonoBehaviour
    {
        private static readonly int GlowIntensityId = Shader.PropertyToID("_GlowIntensity");
        private static readonly int GlowColorId = Shader.PropertyToID("_GlowColor");
        private static readonly Color DiamondCyan = new Color32(0x80, 0xDE, 0xEA, 0xFF);
        private static readonly Color TitleWhite = Color.white;
        private static readonly Color PriceAmber = new Color32(0xD4, 0x8C, 0x29, 0xFF);

        /// <summary>Global drink-icon manager instance.</summary>
        public static JackOnTheRocksDrinkIconManager Instance { get; private set; }

        [Header("Five Drink Icons")]
        [SerializeField] private DrinkIconConfig[] drinkIcons = new DrinkIconConfig[5];

        [Header("Drink Selection Panel")]
        [SerializeField] private Image activeDrinkGlowOverlay;
        [SerializeField] private Image activeDrinkDisplayImage;
        [SerializeField] private TextMeshProUGUI activeDrinkTitleText;
        [SerializeField] private TextMeshProUGUI activeDrinkPriceText;

        [Header("Frosted Cyan Glow")]
        [SerializeField, Tooltip("Material using the Jack On The Rocks/UI/Frosted Glow shader.")]
        private Material frostedGlowMaterial;
        [SerializeField, Min(0f)] private float selectedGlowIntensity = 1f;
        [SerializeField, Range(1f, 1.25f)] private float selectedPulseScale = 1.1f;
        [SerializeField, Range(1f, 1.2f)] private float glowOverlayScale = 1.08f;
        [SerializeField, Min(0.01f)] private float pulseDurationSeconds = 0.18f;

        private Material runtimeGlowMaterial;
        private RectTransform pulseTarget;
        private Vector3 pulseBaseScale = Vector3.one;
        private float pulseElapsed;
        private bool pulseActive;
        private string selectedDrinkId = string.Empty;
        private DrinkIconConfig selectedConfig;

        /// <summary>Raised after a valid drink icon is selected.</summary>
        public event Action<DrinkIconConfig> onDrinkIconSelected;
        /// <summary>Raised when configuration or UI binding prevents a selection.</summary>
        public event Action<string> onDrinkIconError;

        /// <summary>Identifier of the selected drink, or an empty string before selection.</summary>
        public string SelectedDrinkId => selectedDrinkId;
        /// <summary>Current selected configuration.</summary>
        public DrinkIconConfig SelectedConfig => selectedConfig;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            CreateSharedRuntimeMaterial();
            ConfigureStaticUiState();
        }

        private void Update()
        {
            if (!pulseActive || pulseTarget == null) return;

            pulseElapsed += Time.unscaledDeltaTime;
            float normalized = Mathf.Clamp01(pulseElapsed / Mathf.Max(0.01f, pulseDurationSeconds));
            // One sine arc grows to +10% at the midpoint and returns exactly to the base scale.
            float multiplier = 1f + (selectedPulseScale - 1f) * Mathf.Sin(normalized * Mathf.PI);
            pulseTarget.localScale = pulseBaseScale * multiplier;
            if (normalized < 1f) return;

            pulseTarget.localScale = pulseBaseScale;
            pulseTarget = null;
            pulseActive = false;
        }

        private void OnDisable()
        {
            ResetPulse();
        }

        private void OnDestroy()
        {
            ResetPulse();
            if (runtimeGlowMaterial != null) Destroy(runtimeGlowMaterial);
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Replaces the five-icon registry at runtime. The caller retains ownership of its source
        /// array; a compact defensive copy prevents later external mutation.
        /// </summary>
        public void LoadDrinkIcons(DrinkIconConfig[] configurations)
        {
            if (configurations == null || configurations.Length == 0)
            {
                ReportError("At least one drink icon configuration is required.");
                return;
            }

            int count = Mathf.Min(5, configurations.Length);
            DrinkIconConfig[] replacement = new DrinkIconConfig[count];
            Array.Copy(configurations, replacement, count);
            drinkIcons = replacement;
        }

        /// <summary>
        /// Applies selection scale and updates the shared material's frosted glow properties.
        /// One material instance is reused for the manager's active UI images.
        /// </summary>
        /// <param name="iconImage">UI Image to update.</param>
        /// <param name="isSelected">Whether this image is the active Store choice.</param>
        public void ApplyFrostedGlowEffect(Image iconImage, bool isSelected)
        {
            if (iconImage == null) return;

            Color glow = ResolveGlowColor(selectedConfig.glowColor);
            Material material = runtimeGlowMaterial;
            if (material != null)
            {
                if (material.HasProperty(GlowColorId)) material.SetColor(GlowColorId, glow);
                if (material.HasProperty(GlowIntensityId))
                    material.SetFloat(GlowIntensityId, isSelected ? selectedGlowIntensity : 0f);
                iconImage.material = material;
            }

            RectTransform rect = iconImage.rectTransform;
            if (rect == null) return;
            if (!isSelected)
            {
                if (pulseTarget == rect) ResetPulse();
                rect.localScale = Vector3.one;
                return;
            }

            ResetPulse();
            pulseTarget = rect;
            pulseBaseScale = Vector3.one;
            pulseElapsed = 0f;
            pulseActive = true;
        }

        /// <summary>Selects a registered drink and updates sprite, glow, title, and price UI.</summary>
        public bool SelectDrinkIcon(string drinkId)
        {
            if (string.IsNullOrWhiteSpace(drinkId))
            {
                ReportError("Drink ID is required.");
                return false;
            }

            DrinkIconConfig config;
            if (!TryFindConfig(drinkId, out config))
            {
                ReportError("Drink icon was not found: " + drinkId);
                return false;
            }
            if (config.transparentIconSprite == null)
            {
                ReportError("Drink icon has no transparent sprite: " + drinkId);
                return false;
            }

            selectedDrinkId = config.drinkId;
            selectedConfig = config;
            Color glow = ResolveGlowColor(config.glowColor);

            if (activeDrinkDisplayImage != null)
            {
                activeDrinkDisplayImage.sprite = config.transparentIconSprite;
                activeDrinkDisplayImage.preserveAspect = true;
                activeDrinkDisplayImage.enabled = true;
                ApplyFrostedGlowEffect(activeDrinkDisplayImage, true);
            }

            if (activeDrinkGlowOverlay != null)
            {
                activeDrinkGlowOverlay.sprite = config.transparentIconSprite;
                activeDrinkGlowOverlay.preserveAspect = true;
                activeDrinkGlowOverlay.color = new Color(glow.r, glow.g, glow.b, 0.8f);
                activeDrinkGlowOverlay.rectTransform.localScale = Vector3.one * glowOverlayScale;
                activeDrinkGlowOverlay.gameObject.SetActive(true);
                ApplyGlowMaterial(activeDrinkGlowOverlay, glow, true);
            }

            if (activeDrinkTitleText != null)
            {
                activeDrinkTitleText.text = string.IsNullOrWhiteSpace(config.brandName)
                    ? config.drinkId
                    : config.brandName;
                activeDrinkTitleText.color = TitleWhite;
            }
            if (activeDrinkPriceText != null)
            {
                activeDrinkPriceText.SetText("${0:0.00} USD", Mathf.Max(0f, config.priceUSD));
                activeDrinkPriceText.color = PriceAmber;
            }

            onDrinkIconSelected?.Invoke(config);
            return true;
        }

        /// <summary>Public Unity button binding for a configured drink identifier.</summary>
        public void OnDrinkIconClicked(string drinkId)
        {
            SelectDrinkIcon(drinkId);
        }

        private void CreateSharedRuntimeMaterial()
        {
            Material sourceMaterial = frostedGlowMaterial;
            bool ownsSourceMaterial = false;
            if (sourceMaterial == null)
            {
                Shader glowShader = Shader.Find("Jack On The Rocks/UI/Frosted Glow");
                if (glowShader != null)
                {
                    sourceMaterial = new Material(glowShader);
                    ownsSourceMaterial = true;
                }
            }

            if (sourceMaterial == null)
            {
                ReportError("Frosted glow material is not assigned and the UI glow shader could not be found.");
                return;
            }

            runtimeGlowMaterial = new Material(sourceMaterial)
            {
                name = "JackOnTheRocks Frosted Glow (Drink Icons Runtime)",
                hideFlags = HideFlags.DontSave
            };

            if (ownsSourceMaterial) Destroy(sourceMaterial);
        }

        private void ConfigureStaticUiState()
        {
            if (activeDrinkGlowOverlay != null)
            {
                activeDrinkGlowOverlay.color = new Color(DiamondCyan.r, DiamondCyan.g, DiamondCyan.b, 0.8f);
                activeDrinkGlowOverlay.rectTransform.localScale = Vector3.one * glowOverlayScale;
                activeDrinkGlowOverlay.gameObject.SetActive(false);
            }
            if (activeDrinkTitleText != null) activeDrinkTitleText.color = TitleWhite;
            if (activeDrinkPriceText != null) activeDrinkPriceText.color = PriceAmber;
        }

        private void ApplyGlowMaterial(Image image, Color color, bool enabled)
        {
            if (image == null || runtimeGlowMaterial == null) return;
            if (runtimeGlowMaterial.HasProperty(GlowColorId))
                runtimeGlowMaterial.SetColor(GlowColorId, color);
            if (runtimeGlowMaterial.HasProperty(GlowIntensityId))
                runtimeGlowMaterial.SetFloat(GlowIntensityId, enabled ? selectedGlowIntensity : 0f);
            image.material = runtimeGlowMaterial;
        }

        private bool TryFindConfig(string drinkId, out DrinkIconConfig config)
        {
            if (drinkIcons != null)
            {
                for (int i = 0; i < drinkIcons.Length; i++)
                {
                    if (!string.Equals(drinkIcons[i].drinkId, drinkId,
                        StringComparison.OrdinalIgnoreCase)) continue;
                    config = drinkIcons[i];
                    return true;
                }
            }
            config = default(DrinkIconConfig);
            return false;
        }

        private void ResetPulse()
        {
            if (pulseTarget != null) pulseTarget.localScale = pulseBaseScale;
            pulseTarget = null;
            pulseElapsed = 0f;
            pulseActive = false;
        }

        private static Color ResolveGlowColor(Color configured)
        {
            return configured.a <= 0f ? DiamondCyan : configured;
        }

        private void ReportError(string message)
        {
            Debug.LogWarning("DrinkIconManager: " + message);
            onDrinkIconError?.Invoke(message);
        }
    }
}

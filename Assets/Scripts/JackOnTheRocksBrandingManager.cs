using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace JackOnTheRocks
{
    [Serializable]
    public class BrandingData
    {
        public string primaryColor = "#1f2a36";
        public string accentColor = "#ff5a5f";
        public string mutedColor = "#7a8a95";
        public string[] backgroundGradient = new string[] { "#0f1113", "#071018" };
    }

    public class JackOnTheRocksBrandingManager : MonoBehaviour
    {
        public static JackOnTheRocksBrandingManager Instance { get; private set; }

        public BrandingData data = new BrandingData();
        public Sprite logoSprite;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this; DontDestroyOnLoad(gameObject);
            StartCoroutine(LoadBrandingCoroutine());
        }

        private IEnumerator LoadBrandingCoroutine()
        {
            var basePath = Path.Combine(Application.streamingAssetsPath, "branding");
            var jsonPath = Path.Combine(basePath, "branding.json");
            var logoPath = Path.Combine(basePath, "logo.png");
            if (File.Exists(jsonPath))
            {
                try
                {
                    var txt = File.ReadAllText(jsonPath);
                    data = JsonUtility.FromJson<BrandingData>(txt);
                }
                catch (Exception) { }
            }

            if (File.Exists(logoPath))
            {
                var url = "file://" + logoPath;
                var uwr = UnityEngine.Networking.UnityWebRequestTexture.GetTexture(url);
                var op = uwr.SendWebRequest();
                while (!op.isDone) yield return null;
                if (uwr.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    var tex = UnityEngine.Networking.DownloadHandlerTexture.GetContent(uwr);
                    logoSprite = Sprite.Create(tex, new Rect(0,0,tex.width,tex.height), new Vector2(0.5f,0.5f));
                }
            }
            yield break;
        }

        public void ApplyToImage(Image img)
        {
            if (img == null) return;
            if (logoSprite != null) img.sprite = logoSprite;
        }

        public Color GetPrimaryColor() { return ParseColor(data.primaryColor, Color.white); }
        public Color GetAccentColor() { return ParseColor(data.accentColor, Color.white); }
        public Color GetMutedColor() { return ParseColor(data.mutedColor, Color.gray); }

        private Color ParseColor(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            if (ColorUtility.TryParseHtmlString(hex, out var c)) return c;
            return fallback;
        }
    }
}

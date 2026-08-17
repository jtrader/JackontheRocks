using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Creates simple placeholder PNG sprites for waiter clothing tiers.
/// Menu: JackOnTheRocks/Create Placeholder Sprites
/// </summary>
public static class CreatePlaceholderSprites
{
    [MenuItem("JackOnTheRocks/Create Placeholder Sprites")]
    public static void CreateSprites()
    {
        string folder = "Assets/Textures/WaiterPlaceholders";
        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

        // Tier 0: neutral outfit
        CreateProceduralWaiter(folder + "/tier0.png", 120, 160, new Color(0.9f, 0.95f, 1f), new Color(0.2f, 0.2f, 0.2f), new Color(0.2f, 0.5f, 0.8f));
        // Tier 1: patterned outfit (subtle)
        CreateProceduralWaiter(folder + "/tier1.png", 120, 160, new Color(1f, 0.95f, 0.9f), new Color(0.15f, 0.15f, 0.15f), new Color(0.95f, 0.6f, 0.25f));
        // Tier 2: vivid outfit
        CreateProceduralWaiter(folder + "/tier2.png", 120, 160, new Color(1f, 0.92f, 0.92f), new Color(0.12f, 0.12f, 0.12f), new Color(0.9f, 0.3f, 0.5f));

        AssetDatabase.Refresh();
        // Configure imported textures as Sprites
        foreach (var file in new[] { "tier0.png", "tier1.png", "tier2.png" })
        {
            var path = folder + "/" + file;
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                importer.SaveAndReimport();
            }
        }

        Debug.Log("Created procedural placeholder sprites in " + folder);
    }

    private static void CreateProceduralWaiter(string path, int w, int h, Color bgTop, Color borderColor, Color outfitColor)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);

        // Gradient background (vertical)
        for (int y = 0; y < h; y++)
        {
            float t = (float)y / (h - 1);
            Color rowCol = Color.Lerp(bgTop, Color.white * 0.95f, 1f - t);
            for (int x = 0; x < w; x++) tex.SetPixel(x, y, rowCol);
        }

        // Draw border
        int border = 3;
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                if (x < border || y < border || x >= w - border || y >= h - border)
                {
                    tex.SetPixel(x, y, borderColor * 0.95f);
                }
            }

        // Draw simple silhouette: head (circle) and torso (rounded rect)
        int cx = w / 2;
        int headY = Mathf.RoundToInt(h * 0.72f);
        int headR = Mathf.RoundToInt(w * 0.14f);
        DrawFilledCircle(tex, cx, headY, headR, new Color(0.96f, 0.84f, 0.7f));

        // Torso
        int torsoW = Mathf.RoundToInt(w * 0.5f);
        int torsoH = Mathf.RoundToInt(h * 0.35f);
        int torsoX = cx - torsoW / 2;
        int torsoY = headY - headR - torsoH / 2;
        DrawRoundedRect(tex, torsoX, torsoY, torsoW, torsoH, 8, outfitColor);

        // Add subtle pattern to outfit (diagonal stripes)
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                if (IsInsideRect(x, y, torsoX, torsoY, torsoW, torsoH))
                {
                    if (((x + y) % 10) < 3)
                    {
                        var baseCol = tex.GetPixel(x, y);
                        tex.SetPixel(x, y, Color.Lerp(baseCol, Color.white, 0.06f));
                    }
                }
            }

        tex.Apply();
        var bytes = tex.EncodeToPNG();
        File.WriteAllBytes(path, bytes);
        Object.DestroyImmediate(tex);
    }

    private static void DrawFilledCircle(Texture2D tex, int cx, int cy, int r, Color col)
    {
        int r2 = r * r;
        for (int x = -r; x <= r; x++)
            for (int y = -r; y <= r; y++)
            {
                if (x * x + y * y <= r2)
                {
                    int px = cx + x; int py = cy + y;
                    if (px >= 0 && px < tex.width && py >= 0 && py < tex.height) tex.SetPixel(px, py, col);
                }
            }
    }

    private static void DrawRoundedRect(Texture2D tex, int x, int y, int w, int h, int radius, Color col)
    {
        for (int px = x; px < x + w; px++)
            for (int py = y; py < y + h; py++)
            {
                if (px >= 0 && px < tex.width && py >= 0 && py < tex.height)
                {
                    tex.SetPixel(px, py, col);
                }
            }
        // corners: simple circle cut
        DrawFilledCircle(tex, x + radius, y + radius, radius, col);
        DrawFilledCircle(tex, x + w - radius - 1, y + radius, radius, col);
        DrawFilledCircle(tex, x + radius, y + h - radius - 1, radius, col);
        DrawFilledCircle(tex, x + w - radius - 1, y + h - radius - 1, radius, col);
    }

    private static bool IsInsideRect(int px, int py, int x, int y, int w, int h)
    {
        return px >= x && px < x + w && py >= y && py < y + h;
    }
}

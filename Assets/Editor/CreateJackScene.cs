using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using JackOnTheRocks;

/// <summary>
/// Editor helper to create an example scene with UI buttons wired to the demo component.
/// Use menu: JackOnTheRocks/Create Example Scene
/// </summary>
public static class CreateJackScene
{
    [MenuItem("JackOnTheRocks/Create Example Scene")]
    public static void CreateExampleScene()
    {
        // Ensure placeholder assets and animator exist before scene creation
        CreatePlaceholderSprites.CreateSprites();
        CreateWaiterAnimator.CreateController();

        // Create new scene
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        // Create manager GameObject
        var managerGO = new GameObject("JackOnTheRocksManager");
        managerGO.AddComponent<JackOnTheRocks.JackOnTheRocksManager>();
        // Add SaveManager component for persistence
        managerGO.AddComponent<JackOnTheRocks.SaveManager>();

        // Create DemoUI GameObject
        var demoGO = new GameObject("DemoUI");
        demoGO.AddComponent<JackOnTheRocks.JackOnTheRocksDemoUI>();

        // Create Canvas
        var canvasGO = new GameObject("Canvas");
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasGO.AddComponent<CanvasScaler>();
        canvasGO.AddComponent<GraphicRaycaster>();

        // Create status text fields
        GameObject CreateText(string name, Vector2 anchoredPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(canvasGO.transform, false);
            var txt = go.AddComponent<Text>();
            txt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            txt.fontSize = 18;
            txt.color = Color.white;
            txt.alignment = TextAnchor.MiddleLeft;
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(400, 30);
            rt.anchoredPosition = anchoredPos;
            return go;
        }

        var rocksText = CreateText("RocksText", new Vector2(-250, 220));
        var diamondsText = CreateText("DiamondsText", new Vector2(-250, 190));
        var stateText = CreateText("GameStateText", new Vector2(-250, 160));
        var waiterStatus = CreateText("WaiterStatusText", new Vector2(-250, 130));
        var serverTokenText = CreateText("ServerTokenText", new Vector2(-250, 100));
        var bannerText = CreateText("BannerText", new Vector2(0, 260));
        bannerText.GetComponent<Text>().alignment = TextAnchor.MiddleCenter;

        // PayID instruction panel
        GameObject payPanel = new GameObject("PayIDPanel");
        payPanel.transform.SetParent(canvasGO.transform, false);
        var panelImg = payPanel.AddComponent<Image>();
        panelImg.color = new Color(0.15f, 0.15f, 0.15f, 0.9f);
        var panelRt = payPanel.GetComponent<RectTransform>();
        panelRt.sizeDelta = new Vector2(420, 180);
        panelRt.anchoredPosition = new Vector2(250, -120);

        GameObject CreateSmallText(string name, Vector2 anchoredPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(payPanel.transform, false);
            var txt = go.AddComponent<Text>();
            txt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            txt.fontSize = 14;
            txt.color = Color.white;
            txt.alignment = TextAnchor.MiddleLeft;
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(380, 24);
            rt.anchoredPosition = anchoredPos;
            return go;
        }

        var orderLabel = CreateSmallText("PayIDOrderLabel", new Vector2(0, 60));
        var emailText = CreateSmallText("PayIDEmailText", new Vector2(0, 25));
        var descText = CreateSmallText("PayIDDescriptionText", new Vector2(0, -5));
        var refText = CreateSmallText("PayIDReferenceText", new Vector2(0, -35));
        var confirmText = CreateSmallText("PayIDConfirmText", new Vector2(0, -65));

        // Buttons for copy and confirm
        void CreateSmallButton(string name, Vector2 anchoredPos)
        {
            var btnGO = new GameObject(name);
            btnGO.transform.SetParent(payPanel.transform, false);
            var img = btnGO.AddComponent<Image>();
            img.color = Color.white;
            var rect = btnGO.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(160, 32);
            rect.anchoredPosition = anchoredPos;
            var button = btnGO.AddComponent<Button>();
            var txtGO = new GameObject("Text");
            txtGO.transform.SetParent(btnGO.transform, false);
            var txt = txtGO.AddComponent<Text>();
            txt.text = name;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.color = Color.black;
            txt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            var txtRect = txtGO.GetComponent<RectTransform>();
            txtRect.anchorMin = Vector2.zero; txtRect.anchorMax = Vector2.one; txtRect.offsetMin = Vector2.zero; txtRect.offsetMax = Vector2.zero;
        }

        CreateSmallButton("CopyPayIDEmail", new Vector2(-80, -105));
        CreateSmallButton("IHaveSentPayment", new Vector2(80, -105));

        // Create EventSystem
        var es = new GameObject("EventSystem");
        es.AddComponent<UnityEngine.EventSystems.EventSystem>();
        es.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();

        // Helper to create a button
        void CreateButton(string name, Vector2 anchoredPos)
        {
            var btnGO = new GameObject(name);
            btnGO.transform.SetParent(canvasGO.transform, false);
            var img = btnGO.AddComponent<Image>();
            img.color = Color.white;
            var rect = btnGO.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(200, 40);
            rect.anchoredPosition = anchoredPos;
            var button = btnGO.AddComponent<Button>();
            var txtGO = new GameObject("Text");
            txtGO.transform.SetParent(btnGO.transform, false);
            var txt = txtGO.AddComponent<Text>();
            txt.text = name;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.color = Color.black;
            txt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            var txtRect = txtGO.GetComponent<RectTransform>();
            txtRect.anchorMin = Vector2.zero; txtRect.anchorMax = Vector2.one; txtRect.offsetMin = Vector2.zero; txtRect.offsetMax = Vector2.zero;
        }

        // Create several buttons
        CreateButton("StartRound", new Vector2(-250, 150));
        CreateButton("Hit", new Vector2(-250, 90));
        CreateButton("Stand", new Vector2(-250, 30));
        CreateButton("DoubleDown", new Vector2(-250, -30));
        CreateButton("BuyDrink", new Vector2(0, 150));
        CreateButton("TipWaiter_Tip", new Vector2(0, 90));
        CreateButton("TipWaiter_RequestDance", new Vector2(0, 30));
        CreateButton("TipWaiter_Strip", new Vector2(0, -30));
        CreateButton("GrantDiamonds", new Vector2(250, 150));
        CreateButton("SaveState", new Vector2(250, 90));
        CreateButton("LoadState", new Vector2(250, 30));
        CreateButton("SendReceipt", new Vector2(250, -30));
        CreateButton("Simulate30Days", new Vector2(0, -80));
        CreateButton("ForceSubmitSurvey", new Vector2(0, -130));

        // Create placeholder waiters (UI image panels) and attach WaiterVisual
        GameObject CreateWaiterPlaceholder(string name, Vector2 pos, string waiterName)
        {
            var go = new GameObject(name);
            go.transform.SetParent(canvasGO.transform, false);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.8f, 0.8f, 0.8f);
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(120, 160);
            rt.anchoredPosition = pos;
            var labelGO = new GameObject("Label");
            labelGO.transform.SetParent(go.transform, false);
            var lbl = labelGO.AddComponent<Text>();
            lbl.text = waiterName;
            lbl.alignment = TextAnchor.LowerCenter;
            lbl.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            lbl.color = Color.black;
            var lblRt = labelGO.GetComponent<RectTransform>();
            lblRt.anchorMin = new Vector2(0, 0); lblRt.anchorMax = new Vector2(1, 0.2f);
            lblRt.offsetMin = Vector2.zero; lblRt.offsetMax = Vector2.zero;

            var visual = go.AddComponent<WaiterVisual>();
            visual.waiterName = waiterName;
            visual.initialClothingTier = 0;
            visual.maxClothingTier = 2;
            // Add Animator component to allow developers to assign controllers later
            var animator = go.AddComponent<Animator>();
            // If controller exists, assign it so the placeholder plays immediately
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/Animations/WaiterController.controller");
            if (controller != null) animator.runtimeAnimatorController = controller;
            visual.waiterAnimator = animator;
            // Load placeholder sprites if available and assign to visual
            var sprite0 = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Textures/WaiterPlaceholders/tier0.png");
            var sprite1 = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Textures/WaiterPlaceholders/tier1.png");
            var sprite2 = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Textures/WaiterPlaceholders/tier2.png");
            visual.clothingTierSprites = new Sprite[] { sprite0, sprite1, sprite2 };
            return go;
        }

        CreateWaiterPlaceholder("Waiter_1", new Vector2(200, -50), "Ava");
        CreateWaiterPlaceholder("Waiter_2", new Vector2(350, -50), "Liam");

        // Attach SceneSetup component that wires the buttons at runtime
        var setupGO = new GameObject("SceneSetup");
        setupGO.AddComponent<SceneSetup>();

        // Add WebhookReceiver for integration tests
        var webhookGo = new GameObject("WebhookReceiver");
        var receiver = webhookGo.AddComponent<WebhookReceiver>();
        receiver.port = 8080;
        receiver.path = "/webhook";

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveOpenScenes();

        Debug.Log("JackOnTheRocks example scene created. Press Play to test.");
    }
}

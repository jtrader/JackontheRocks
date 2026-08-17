using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;
using JackOnTheRocks.Admin;

/// <summary>
/// Editor utility to auto-wire a scene's Admin Canvas to the JackOnTheRocksAdminGUI component.
/// Place this under Assets/Editor and run via Tools -> JackOnTheRocks -> Auto Wire Admin GUI.
/// It looks for GameObjects by name and assigns them where possible. Names are convention-based.
/// </summary>
public class AutoWireAdminGUI : EditorWindow
{
    [MenuItem("Tools/JackOnTheRocks/Auto Wire Admin GUI")]
    public static void AutoWire()
    {
        var activeScene = SceneManager.GetActiveScene();
        if (!activeScene.IsValid || activeScene.rootCount == 0)
        {
            activeScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Debug.Log("No scene was open, so a new empty scene was created for the admin dashboard.");
        }

        var gui = GameObject.FindObjectOfType<JackOnTheRocksAdminGUI>();
        if (gui == null)
        {
            var root = new GameObject("JackOnTheRocksAdminGUI");
            root.transform.SetAsLastSibling();
            gui = root.AddComponent<JackOnTheRocksAdminGUI>();

            // Make a minimal canvas so the scene is immediately usable
            var canvasGO = new GameObject("AdminCanvas");
            canvasGO.transform.SetParent(root.transform);
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGO.AddComponent<CanvasScaler>();
            canvasGO.AddComponent<GraphicRaycaster>();

            if (Object.FindObjectOfType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<StandaloneInputModule>();
            }

            Debug.LogWarning("Created a new admin dashboard root because there was no scene open. Assign the missing references from the warning dialog or from the Inspector.");
        }

        Undo.RecordObject(gui, "Auto Wire Admin GUI");
        var missing = new System.Collections.Generic.List<string>();

        // Nav buttons and panels by AdminTab name
        var tabs = System.Enum.GetNames(typeof(AdminTab));
        var navButtonsList = new System.Collections.Generic.List<Button>();
        var tabPanelsList = new System.Collections.Generic.List<GameObject>();
        foreach (var t in tabs)
        {
            var navName = "NavButton_" + t;
            var navGO = GameObject.Find(navName);
            Button b = null;
            if (navGO != null) b = navGO.GetComponent<Button>();
            else missing.Add(navName);
            navButtonsList.Add(b);

            var panelName = "TabPanel_" + t;
            var panelGO = GameObject.Find(panelName);
            if (panelGO == null) missing.Add(panelName);
            tabPanelsList.Add(panelGO);
        }

        gui.navButtons = navButtonsList.ToArray();
        gui.tabPanels = tabPanelsList.ToArray();

        // Common named elements
        gui.floatingOpenButton = AssignOrLog<Button>("FloatingOpenButton", missing);
        gui.adminKeyInput = AssignOrLog<TMP_InputField>("AdminKeyInput", missing);
        gui.loginButton = AssignOrLog<Button>("LoginButton", missing);
        gui.loginStatusText = AssignOrLog<TextMeshProUGUI>("LoginStatusText", missing);

        gui.overviewRevenueText = AssignOrLog<TextMeshProUGUI>("OverviewRevenueText", missing);
        gui.overviewActivePlayersText = AssignOrLog<TextMeshProUGUI>("OverviewActivePlayersText", missing);
        gui.overviewRocksInCirculationText = AssignOrLog<TextMeshProUGUI>("OverviewRocksInCirculationText", missing);
        gui.overviewPendingOrdersText = AssignOrLog<TextMeshProUGUI>("OverviewPendingOrdersText", missing);
        gui.ageGateToggle = AssignOrLog<Toggle>("AgeGateToggle", missing);
        gui.mainEnginePauseToggle = AssignOrLog<Toggle>("MainEnginePauseToggle", missing);
        gui.emergencyStoreFreezeToggle = AssignOrLog<Toggle>("EmergencyStoreFreezeToggle", missing);

        gui.transactionsSearchInput = AssignOrLog<TMP_InputField>("TransactionsSearchInput", missing);
        gui.transactionsFilterDropdown = AssignOrLog<TMP_Dropdown>("TransactionsFilterDropdown", missing);
        gui.transactionsListContent = FindGameObject("TransactionsListContent")?.transform;
        if (gui.transactionsListContent == null) missing.Add("TransactionsListContent");
        gui.transactionListItemPrefab = FindGameObject("TransactionListItemPrefab");
        if (gui.transactionListItemPrefab == null) missing.Add("TransactionListItemPrefab");

        gui.regionalManagersContent = FindGameObject("RegionalManagersContent")?.transform;
        if (gui.regionalManagersContent == null) missing.Add("RegionalManagersContent");
        gui.regionalManagerItemPrefab = FindGameObject("RegionalManagerItemPrefab");
        if (gui.regionalManagerItemPrefab == null) missing.Add("RegionalManagerItemPrefab");
        gui.managerRegionNameInput = AssignOrLog<TMP_InputField>("ManagerRegionNameInput", missing);
        gui.managerLatInput = AssignOrLog<TMP_InputField>("ManagerLatInput", missing);
        gui.managerLongInput = AssignOrLog<TMP_InputField>("ManagerLongInput", missing);
        gui.managerRadiusKmInput = AssignOrLog<TMP_InputField>("ManagerRadiusKmInput", missing);
        gui.managerPhoneInput = AssignOrLog<TMP_InputField>("ManagerPhoneInput", missing);
        gui.managerSnapchatTokenInput = AssignOrLog<TMP_InputField>("ManagerSnapchatTokenInput", missing);

        gui.creativeListContent = FindGameObject("CreativeListContent")?.transform;
        if (gui.creativeListContent == null) missing.Add("CreativeListContent");
        gui.creativeItemPrefab = FindGameObject("CreativeItemPrefab");
        if (gui.creativeItemPrefab == null) missing.Add("CreativeItemPrefab");

        gui.userSearchInput = AssignOrLog<TMP_InputField>("UserSearchInput", missing);
        gui.userListContent = FindGameObject("UserListContent")?.transform;
        if (gui.userListContent == null) missing.Add("UserListContent");
        gui.userItemPrefab = FindGameObject("UserItemPrefab");
        if (gui.userItemPrefab == null) missing.Add("UserItemPrefab");

        gui.surveyListContent = FindGameObject("SurveyListContent")?.transform;
        if (gui.surveyListContent == null) missing.Add("SurveyListContent");
        gui.surveyItemPrefab = FindGameObject("SurveyItemPrefab");
        if (gui.surveyItemPrefab == null) missing.Add("SurveyItemPrefab");

        EditorUtility.SetDirty(gui);

        if (missing.Count > 0)
        {
            var msg = "Auto-wiring completed with missing references:\n- " + string.Join("\n- ", missing.ToArray());
            Debug.LogWarning(msg);
            EditorUtility.DisplayDialog("JackOnTheRocks Admin GUI", msg + "\n\nRename objects or assign references manually in the Inspector.", "OK");
        }
        else
        {
            Debug.Log("Auto-wired JackOnTheRocksAdminGUI fields successfully. Review the assigned references in the inspector and save the scene.");
        }
    }

    private static T AssignOrLog<T>(string name, System.Collections.Generic.List<string> missing) where T : Component
    {
        var component = FindComponentByName<T>(name);
        if (component == null) missing.Add(name);
        return component;
    }

    private static T FindComponentByName<T>(string name) where T : Component
    {
        var go = GameObject.Find(name);
        if (go == null) return null;
        return go.GetComponent<T>();
    }

    private static GameObject FindGameObject(string name)
    {
        return GameObject.Find(name);
    }
}

using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Runtime helper that wires UI Buttons to methods on `JackOnTheRocksDemoUI`.
/// This is used by the editor scene generator so button wiring is handled automatically at runtime.
/// </summary>
public class SceneSetup : MonoBehaviour
{
    private JackOnTheRocks.JackOnTheRocksDemoUI demo;

    private void Awake()
    {
        demo = FindObjectOfType<JackOnTheRocks.JackOnTheRocksDemoUI>();
        if (demo == null)
        {
            Debug.LogWarning("Demo UI not found in scene. Please add JackOnTheRocksDemoUI to a GameObject named DemoUI.");
            return;
        }

        WireButton("StartRound", () => demo.StartRound());
        WireButton("Hit", () => demo.HitButton());
        WireButton("Stand", () => demo.StandButton());
        WireButton("DoubleDown", () => demo.DoubleDownButton());
        WireButton("BuyDrink", () => demo.BuyDrink(0));
        WireButton("TipWaiter_Tip", () => demo.TipWaiter_Tip());
        WireButton("TipWaiter_RequestDance", () => demo.TipWaiter_RequestDance());
        WireButton("TipWaiter_Strip", () => demo.TipWaiter_Strip());
        WireButton("GrantDiamonds", () => demo.GrantDiamonds(5));
        WireButton("SaveState", () => demo.SaveState());
        WireButton("LoadState", () => demo.LoadState());
        WireButton("SendReceipt", () => demo.SendReceipt());
        WireButton("CopyPayIDEmail", () => demo.CopyPayIDEmail());
        WireButton("IHaveSentPayment", () => demo.IHaveSentPayment());
        // Survey test buttons
        WireButton("Simulate30Days", () => JackOnTheRocks.JackOnTheRocksSurveyManager.Instance.OnForceSubmitTestSurvey());
        WireButton("ForceSubmitSurvey", () => JackOnTheRocks.JackOnTheRocksSurveyManager.Instance.OnForceSubmitTestSurvey());
    }

    private void Start()
    {
        var mgr = JackOnTheRocks.JackOnTheRocksManager.Instance;
        if (mgr == null) return;

        // Wire on-screen text fields if present
        var rocksTxt = GameObject.Find("RocksText")?.GetComponent<Text>();
        var diamondsTxt = GameObject.Find("DiamondsText")?.GetComponent<Text>();
        var stateTxt = GameObject.Find("GameStateText")?.GetComponent<Text>();
        var waiterTxt = GameObject.Find("WaiterStatusText")?.GetComponent<Text>();
        var serverTokenTxt = GameObject.Find("ServerTokenText")?.GetComponent<Text>();

        if (rocksTxt != null || diamondsTxt != null)
        {
            mgr.onCurrenciesUpdated += (r, d, b) =>
            {
                if (rocksTxt != null) rocksTxt.text = $"Rocks: {r}";
                if (diamondsTxt != null) diamondsTxt.text = $"Diamonds: {d}";
            };
        }

        if (stateTxt != null)
        {
            mgr.onGameStateChanged += (s) => { stateTxt.text = "State: " + s.ToString(); };
        }

        if (waiterTxt != null)
        {
            mgr.onWaiterStateChanged += (w, t, tier) =>
            {
                waiterTxt.text = $"Waiter: {w.waiterName} Action:{t} Tier:{tier}";
            };
        }

        // Wire SaveManager token display
        var saver = FindObjectOfType<JackOnTheRocks.SaveManager>();
        if (serverTokenTxt != null && saver != null)
        {
            // populate existing token
            var existing = saver.GetStoredServerToken();
            if (!string.IsNullOrEmpty(existing)) serverTokenTxt.text = "ServerToken: " + existing;

            saver.onServerTokenUpdated += (tok) =>
            {
                serverTokenTxt.text = "ServerToken: " + tok;
            };
        }
    }

    private void WireButton(string name, UnityEngine.Events.UnityAction action)
    {
        var go = GameObject.Find(name);
        if (go == null) return;
        var btn = go.GetComponent<Button>();
        if (btn == null) return;
        btn.onClick.RemoveAllListeners();
        btn.onClick.AddListener(action);
    }
}

using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Quarto pannello: indici climatici "window" (heritage e tourism) dell'API FutureClimate
/// alla data della simulazione. Due viste interne:
///  - paramsView : imposta ampiezza finestra (window_days), modo (centered/backward/forward)
///                 e profilo turistico; due pulsanti Heritage / Tourism.
///  - resultsView: mostra gli indici in un testo scrollabile verticalmente, con un tasto Back
///                 che riporta a paramsView.
/// La data e lo scenario vengono dal controller sincronizzato; le coordinate dal client
/// climatico (che le legge dal SunPositioner). Tutta la messaggistica e' in inglese.
/// </summary>
public class ClimateIndicesPanel : MonoBehaviour
{
    [Header("Riferimenti")]
    public ThermalSimNetworkController controller;
    public FutureClimateClient climateClient;

    [Header("Viste (figli di questo pannello)")]
    public GameObject paramsView;
    public GameObject resultsView;

    [Header("Parametri finestra")]
    public TMP_Text windowDaysLabel;
    public Button windowDaysMinus;
    public Button windowDaysPlus;
    [Tooltip("Ampiezza iniziale della finestra (giorni). Limiti API: 1..366.")]
    public int windowDays = 30;
    public int windowDaysStep = 5;

    [Tooltip("3 pulsanti: 0=centered, 1=backward, 2=forward.")]
    public Button[] windowModeButtons;
    [Tooltip("Pulsanti dei profili turistici, in numero e ordine liberi.")]
    public Button[] tourismProfileButtons;
    [Tooltip("ID API dei profili, uno per pulsante (stesso ordine di Tourism Profile Buttons).\n" +
             "Validi: urban_cultural, historic_garden, walking_outdoor.")]
    public string[] tourismProfileIds = { "urban_cultural", "walking_outdoor" };

    public Color optionNormalColor = new Color(0.15f, 0.15f, 0.15f, 1f);
    public Color optionSelectedColor = new Color(0.20f, 0.55f, 0.95f, 1f);

    [Header("Tipo di indice (selettore)")]
    [Tooltip("2 pulsanti selettore: 0=Heritage, 1=Tourism.")]
    public Button[] indexTypeButtons;
    [Tooltip("Sezione del profilo turistico: mostrata SOLO quando il tipo indice e' Tourism.")]
    public GameObject tourismProfileSection;

    [Header("Azione")]
    [Tooltip("Avvia il calcolo degli indici e mostra i risultati.")]
    public Button applyButton;

    [Header("Risultati")]
    public TMP_Text resultsTitle;
    [Tooltip("Testo (dentro il Content dello Scroll View) che ospita gli indici.")]
    public TMP_Text resultsText;
    [Tooltip("Riga di stato/errore sopra i risultati.")]
    public TMP_Text resultsStatus;
    public Button backButton;
    [Tooltip("Per riportare lo scroll in cima ad ogni nuova richiesta.")]
    public ScrollRect resultsScroll;

    private static readonly string[] WindowModes = { "centered", "backward", "forward" };

    private int _windowMode;    // 0..2
    private int _profile;       // 0..2
    private int _indexType;     // 0=heritage, 1=tourism
    private bool _busy;

    private void Awake()
    {
        Wire(windowDaysMinus, () => { windowDays = Mathf.Clamp(windowDays - windowDaysStep, 1, 366); RefreshParamLabels(); });
        Wire(windowDaysPlus, () => { windowDays = Mathf.Clamp(windowDays + windowDaysStep, 1, 366); RefreshParamLabels(); });

        WireOptions(windowModeButtons, i => { _windowMode = i; RefreshParamLabels(); });
        WireOptions(tourismProfileButtons, i => { _profile = i; RefreshParamLabels(); });
        WireOptions(indexTypeButtons, SetIndexType);

        Wire(applyButton, () => Fetch(_indexType == 1));
        Wire(backButton, ShowParams);
    }

    private void OnEnable()
    {
        ShowParams();
        SetIndexType(_indexType);   // imposta visibilita' profilo + evidenziazioni
    }

    // Heritage (0) o Tourism (1). Il profilo turistico serve solo a Tourism.
    private void SetIndexType(int i)
    {
        _indexType = Mathf.Clamp(i, 0, 1);
        if (tourismProfileSection != null)
            tourismProfileSection.SetActive(_indexType == 1);
        RefreshParamLabels();
    }

    // ---------- Viste ----------
    private void ShowParams()
    {
        if (paramsView != null) paramsView.SetActive(true);
        if (resultsView != null) resultsView.SetActive(false);
    }

    private void ShowResults()
    {
        if (paramsView != null) paramsView.SetActive(false);
        if (resultsView != null) resultsView.SetActive(true);
    }

    // ---------- Richiesta ----------
    private void Fetch(bool tourism)
    {
        if (_busy) return;
        if (controller == null || climateClient == null)
        {
            ShowResults();
            SetText(resultsStatus, "Controller or climate client not assigned.");
            SetText(resultsTitle, tourism ? "Tourism" : "Heritage");
            SetText(resultsText, "");
            return;
        }

        ShowResults();
        SetText(resultsTitle, tourism ? "Tourism" : "Heritage");
        SetText(resultsStatus, "Loading...");
        SetText(resultsText, "");
        StartCoroutine(FetchRoutine(tourism));
    }

    private System.Collections.IEnumerator FetchRoutine(bool tourism)
    {
        _busy = true;

        ThermalSimNetworkController.DayOfYearToDate(controller.Year, controller.DayOfYear,
                                                    out int month, out int day);
        string scenario = ThermalSimNetworkController.ScenarioApiId(controller.ScenarioIndex);
        string mode = WindowModes[Mathf.Clamp(_windowMode, 0, WindowModes.Length - 1)];

        FutureClimateClient.WindowResult res = null;

        if (tourism)
        {
            string profile = ResolveProfileId(_profile);
            yield return climateClient.FetchTourismWindow(scenario, controller.Year, month, day,
                                                          windowDays, mode, profile, r => res = r);
        }
        else
        {
            yield return climateClient.FetchHeritageWindow(scenario, controller.Year, month, day,
                                                           windowDays, mode, r => res = r);
        }

        if (res == null || !res.ok)
        {
            SetText(resultsStatus, res != null ? res.error : "No response from the climate API.");
            SetText(resultsText, "");
        }
        else
        {
            SetText(resultsStatus, "");
            SetText(resultsTitle, res.headerLine);
            SetText(resultsText, res.body);
            // riporta lo scroll in cima
            if (resultsScroll != null) resultsScroll.verticalNormalizedPosition = 1f;
        }

        _busy = false;
    }

    // ---------- Etichette / evidenziazioni ----------
    private string ResolveProfileId(int index)
    {
        if (tourismProfileIds == null || tourismProfileIds.Length == 0)
            return "urban_cultural";   // default API se non configurato
        index = Mathf.Clamp(index, 0, tourismProfileIds.Length - 1);
        string id = tourismProfileIds[index];
        return string.IsNullOrEmpty(id) ? "urban_cultural" : id;
    }

    private void RefreshParamLabels()
    {
        if (windowDaysLabel != null) windowDaysLabel.text = "Window: " + windowDays + " days";
        Highlight(indexTypeButtons, _indexType);
        Highlight(windowModeButtons, _windowMode);
        Highlight(tourismProfileButtons, _profile);
    }

    private void Highlight(Button[] buttons, int selected)
    {
        if (buttons == null) return;
        for (int i = 0; i < buttons.Length; i++)
        {
            if (buttons[i] == null) continue;
            Image img = buttons[i].image;
            if (img != null) img.color = (i == selected) ? optionSelectedColor : optionNormalColor;
        }
    }

    // ---------- Utility di wiring ----------
    private static void Wire(Button b, UnityEngine.Events.UnityAction action)
    {
        if (b != null && action != null) b.onClick.AddListener(action);
    }

    private void WireOptions(Button[] buttons, System.Action<int> onSelect)
    {
        if (buttons == null) return;
        for (int i = 0; i < buttons.Length; i++)
        {
            int idx = i;   // cattura per closure
            Wire(buttons[i], () => onSelect(idx));
        }
    }

    private static void SetText(TMP_Text t, string s) { if (t != null) t.text = s; }
}

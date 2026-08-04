using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// UI VR-native per i pannelli world-space, collegata al ThermalSimNetworkController.
///
/// Niente tastiera: i valori si impostano con controlli che si toccano col poke o si
/// puntano col ray (gli interactor del template funzionano con le normali UI di Unity):
///   - Data: stepper +/- per anno, mese, giorno.
///   - Ore multiple: griglia di Toggle 0..23 (la stringa CSV "8,14" la costruisce lo script).
///   - Colore: Slider per min/max, Toggle "Auto range", pulsanti < / > per la ramp.
///
/// I valori mostrati riflettono i NetworkVariable del controller: quando un giocatore
/// applica una modifica, gli altri vedono i controlli aggiornarsi (evento OnStateChanged).
/// Apertura/chiusura pannelli e' locale; valori e colori risultanti sono condivisi.
///
/// I pulsanti riusati dai giochi del template chiamano, nel loro onClick,
/// OpenDateTimePanel() / OpenColorPanel().
/// </summary>
public class ThermalSimPanelUI : MonoBehaviour
{
    [Header("Controller di rete")]
    public ThermalSimNetworkController controller;

    [Header("Pannelli (root da mostrare/nascondere)")]
    public GameObject dateTimePanel;
    public GameObject colorPanel;
    public GameObject infoPanel;        // terzo pannello (info + legenda + sonda)
    public GameObject indicesPanel;     // quarto pannello (indici climatici window)

    [Header("Pannello 1 - Stepper Data")]
    public TMP_Text yearLabel;
    public Button yearMinus, yearPlus;
    public TMP_Text monthLabel;
    public Button monthMinus, monthPlus;
    public TMP_Text dayLabel;
    public Button dayMinus, dayPlus;
    public int minYear = 2015;   // periodo disponibile dell'API climatica
    public int maxYear = 2099;

    [Header("Pannello 1 - Ore (griglia 0..23 generata a runtime)")]
    [Tooltip("Contenitore con un GridLayoutGroup (es. 6x4 o 8x3).")]
    public Transform hourGridContainer;
    [Tooltip("Prefab di un Toggle con un TMP_Text figlio per il numero dell'ora.")]
    public Toggle hourTogglePrefab;
    [Tooltip("Colore della cella ora NON selezionata.")]
    public Color hourNormalColor = new Color(0.15f, 0.15f, 0.15f, 1f);
    [Tooltip("Colore della cella ora selezionata.")]
    public Color hourSelectedColor = new Color(0.20f, 0.55f, 0.95f, 1f);

    [Header("Pannello 1 - Scenario climatico (sostituisce lo slider)")]
    [Tooltip("Tre pulsanti in ordine: 0=ssp126, 1=ssp245, 2=ssp585. onClick collegati a runtime.")]
    public Button[] scenarioButtons;
    public Color scenarioNormalColor = new Color(0.15f, 0.15f, 0.15f, 1f);
    public Color scenarioSelectedColor = new Color(0.20f, 0.55f, 0.95f, 1f);
    [Tooltip("Readout: temperatura dell'aria derivata dall'API climatica.")]
    public TMP_Text airTempLabel;
    [Tooltip("Messaggio di errore dell'API climatica (in inglese).")]
    public TMP_Text climateErrorLabel;

    [Header("Pannello 1 - Apply")]
    public Button applyDateTimeButton;

    [Header("Pannello 2 - Colore")]
    public Toggle autoRangeToggle;
    public Slider minSlider;
    public TMP_Text minLabel;
    public Slider maxSlider;
    public TMP_Text maxLabel;
    public Button rampPrev, rampNext;
    public TMP_Text rampLabel;
    public Button applyColorButton;

    // Stato locale in editing (committato solo al click di Applica)
    private int _year, _month, _day, _rampIndex;
    private readonly List<Toggle> _hourToggles = new();
    private readonly HashSet<int> _selectedHours = new() { 14 };   // selezione MULTIPLA
    private bool _updatingHours;        // guardia contro la rientranza negli eventi toggle

    private void Awake()
    {
        Wire(yearMinus,  () => { _year = Clamp(_year - 1, minYear, maxYear); RefreshDateLabels(); });
        Wire(yearPlus,   () => { _year = Clamp(_year + 1, minYear, maxYear); ClampDay(); RefreshDateLabels(); });
        Wire(monthMinus, () => { _month = Wrap(_month - 1, 1, 12); ClampDay(); RefreshDateLabels(); });
        Wire(monthPlus,  () => { _month = Wrap(_month + 1, 1, 12); ClampDay(); RefreshDateLabels(); });
        Wire(dayMinus,   () => { _day = Wrap(_day - 1, 1, DaysInMonth()); RefreshDateLabels(); });
        Wire(dayPlus,    () => { _day = Wrap(_day + 1, 1, DaysInMonth()); RefreshDateLabels(); });

        Wire(rampPrev, () => { _rampIndex = Wrap(_rampIndex - 1, 0, PaletteMax()); RefreshRampLabel(); });
        Wire(rampNext, () => { _rampIndex = Wrap(_rampIndex + 1, 0, PaletteMax()); RefreshRampLabel(); });

        if (applyDateTimeButton != null) applyDateTimeButton.onClick.AddListener(ApplyDateTime);
        if (applyColorButton != null) applyColorButton.onClick.AddListener(ApplyColor);
        if (autoRangeToggle != null) autoRangeToggle.onValueChanged.AddListener(OnAutoToggle);

        if (minSlider != null) minSlider.onValueChanged.AddListener(_ => { EnforceSliderOrder(true); RefreshSliderLabels(); });
        if (maxSlider != null) maxSlider.onValueChanged.AddListener(_ => { EnforceSliderOrder(false); RefreshSliderLabels(); });

        // Pulsanti scenario: collega ciascuno al proprio indice (0=ssp126, 1=ssp245, 2=ssp585).
        if (scenarioButtons != null)
        {
            for (int i = 0; i < scenarioButtons.Length; i++)
            {
                int idx = i;   // cattura per closure
                Wire(scenarioButtons[i], () => { if (controller != null) controller.RequestSetScenario(idx); });
            }
        }

        BuildHourGrid();

        if (dateTimePanel != null) dateTimePanel.SetActive(false);
        if (colorPanel != null) colorPanel.SetActive(false);
    }

    private void OnEnable()
    {
        if (controller != null)
        {
            controller.OnStateChanged += RefreshFromState;
            controller.OnClimateError += ShowClimateError;
            RefreshFromState();   // allinea subito allo stato corrente
        }
    }
    private void OnDisable()
    {
        if (controller != null)
        {
            controller.OnStateChanged -= RefreshFromState;
            controller.OnClimateError -= ShowClimateError;
        }
    }
    private void Start()     => RefreshFromState();

    // ---------- Apertura pannelli (LOCALE, dai pulsanti del modello) ----------
    // I pannelli sono locali a ogni giocatore (niente rete). Mutuamente esclusivi:
    // aprirne uno chiude gli altri.
    public void OpenDateTimePanel()
    {
        // Attiva PRIMA il pannello: i toggle delle ore devono essere attivi quando
        // SetHoursFromCsv imposta la selezione, altrimenti la grafica non si ridisegna.
        SetPanels(true, false, false, false);
        RefreshFromState();
    }

    public void OpenColorPanel()
    {
        SetPanels(false, true, false, false);
        RefreshFromState();
    }

    public void OpenInfoPanel()
    {
        SetPanels(false, false, true, false);
        RefreshFromState();
    }

    public void OpenIndicesPanel()
    {
        SetPanels(false, false, false, true);
    }

    public void CloseAll()
    {
        SetPanels(false, false, false, false);
    }

    private void SetPanels(bool dateTime, bool color, bool info, bool indices)
    {
        if (dateTimePanel != null) dateTimePanel.SetActive(dateTime);
        if (colorPanel != null) colorPanel.SetActive(color);
        if (infoPanel != null) infoPanel.SetActive(info);
        if (indicesPanel != null) indicesPanel.SetActive(indices);
    }

    // ---------- Apply ----------
    private void ApplyDateTime()
    {
        if (controller == null) return;
        if (climateErrorLabel != null) climateErrorLabel.text = "";   // pulisci errori precedenti
        int dayOfYear = ThermalSimNetworkController.DateToDayOfYear(_year, _month, _day);
        controller.RequestSetDateTime(_year, dayOfYear, BuildHoursCsv());
    }

    private void ApplyColor()
    {
        if (controller == null) return;
        bool auto = autoRangeToggle != null ? autoRangeToggle.isOn : controller.AutoRange;
        float min = minSlider != null ? minSlider.value : controller.ColorMin;
        float max = maxSlider != null ? maxSlider.value : controller.ColorMax;
        if (max < min) (min, max) = (max, min);
        controller.RequestSetColor(auto, min, max, _rampIndex);
    }

    private void OnAutoToggle(bool isOn)
    {
        if (minSlider != null) minSlider.interactable = !isOn;
        if (maxSlider != null) maxSlider.interactable = !isOn;
    }

    // ---------- Refresh dai valori sincronizzati ----------
    private void RefreshFromState()
    {
        if (controller == null) return;

        BuildHourGrid();   // assicura i 24 toggle PRIMA di applicare la selezione

        _year = Clamp(controller.Year, minYear, maxYear);
        ThermalSimNetworkController.DayOfYearToDate(controller.Year, controller.DayOfYear, out _month, out _day);
        RefreshDateLabels();

        SetHoursFromCsv(controller.HoursCsv);

        UpdateScenarioVisuals();
        RefreshAirTempLabel();

        if (autoRangeToggle != null) autoRangeToggle.SetIsOnWithoutNotify(controller.AutoRange);
        if (minSlider != null) { minSlider.SetValueWithoutNotify(controller.ColorMin); minSlider.interactable = !controller.AutoRange; }
        if (maxSlider != null) { maxSlider.SetValueWithoutNotify(controller.ColorMax); maxSlider.interactable = !controller.AutoRange; }
        RefreshSliderLabels();

        _rampIndex = Mathf.Clamp(controller.RampIndex, 0, PaletteMax());
        RefreshRampLabel();
    }

    // ---------- Etichette ----------
    private void RefreshDateLabels()
    {
        if (yearLabel != null) yearLabel.text = _year.ToString();
        if (monthLabel != null) monthLabel.text = _month.ToString("00");
        if (dayLabel != null) dayLabel.text = _day.ToString("00");
    }

    private void RefreshRampLabel()
    {
        if (rampLabel != null) rampLabel.text = ThermalSimNetworkController.PaletteNames[_rampIndex];
    }

    private void UpdateScenarioVisuals()
    {
        if (scenarioButtons == null) return;
        int sel = controller.ScenarioIndex;
        for (int i = 0; i < scenarioButtons.Length; i++)
        {
            if (scenarioButtons[i] == null) continue;
            Image img = scenarioButtons[i].image;   // Target Graphic del Button
            if (img != null) img.color = (i == sel) ? scenarioSelectedColor : scenarioNormalColor;
        }
    }

    private void RefreshAirTempLabel()
    {
        if (airTempLabel == null) return;
        if (controller.HasClimate)
        {
            string scn = ThermalSimNetworkController.ScenarioNames[
                Mathf.Clamp(controller.ScenarioIndex, 0, 2)];
            airTempLabel.text = $"Air: {controller.LastAirTempC:0.0} C  ({scn})";
        }
        else
        {
            airTempLabel.text = "Air: -- (press Apply)";
        }
    }

    private void ShowClimateError(string msg)
    {
        if (climateErrorLabel != null) climateErrorLabel.text = msg;
    }

    private void RefreshSliderLabels()
    {
        if (minLabel != null && minSlider != null) minLabel.text = minSlider.value.ToString("0.0") + " C";
        if (maxLabel != null && maxSlider != null) maxLabel.text = maxSlider.value.ToString("0.0") + " C";
    }

    private void EnforceSliderOrder(bool minChanged)
    {
        if (minSlider == null || maxSlider == null) return;
        if (minSlider.value > maxSlider.value)
        {
            if (minChanged) maxSlider.SetValueWithoutNotify(minSlider.value);
            else minSlider.SetValueWithoutNotify(maxSlider.value);
        }
    }

    // ---------- Data ----------
    private int DaysInMonth() =>
        System.DateTime.DaysInMonth(Mathf.Clamp(_year, minYear, maxYear), Mathf.Clamp(_month, 1, 12));

    private void ClampDay()
    {
        int dim = DaysInMonth();
        if (_day > dim) _day = dim;
        if (_day < 1) _day = 1;
    }

    // ---------- Griglia ore (selezione MULTIPLA) ----------
    private void BuildHourGrid()
    {
        if (_hourToggles.Count > 0) return;   // gia' costruita: evita doppioni
        if (hourGridContainer == null || hourTogglePrefab == null) return;

        for (int h = 0; h < 24; h++)
        {
            Toggle t = Instantiate(hourTogglePrefab, hourGridContainer);

            // Gestiamo NOI il colore della cella: niente transizioni automatiche che si
            // resettano al cambio di focus (era la causa dell'evidenziazione che spariva).
            t.transition = Selectable.Transition.None;
            t.toggleTransition = Toggle.ToggleTransition.None;
            t.graphic = null;
            t.SetIsOnWithoutNotify(false);

            TMP_Text lbl = t.GetComponentInChildren<TMP_Text>();
            if (lbl != null) lbl.text = h.ToString();

            int hour = h; // cattura per la closure
            t.onValueChanged.AddListener(on => ToggleHour(hour, on));
            _hourToggles.Add(t);
        }

        SyncTogglesToSelection();   // riflette la selezione corrente
    }

    // Accende/spegne un'ora. Mantiene almeno un'ora sempre selezionata.
    private void ToggleHour(int hour, bool on)
    {
        if (_updatingHours) return;
        hour = Mathf.Clamp(hour, 0, 23);

        if (on) _selectedHours.Add(hour);
        else _selectedHours.Remove(hour);

        // Non permettere selezione vuota: se hai appena spento l'ultima, riaccendila.
        if (_selectedHours.Count == 0)
        {
            _selectedHours.Add(hour);
            _updatingHours = true;
            if (hour < _hourToggles.Count) _hourToggles[hour].SetIsOnWithoutNotify(true);
            _updatingHours = false;
        }

        UpdateHourVisuals();
    }

    private void SyncTogglesToSelection()
    {
        _updatingHours = true;
        for (int h = 0; h < _hourToggles.Count; h++)
            _hourToggles[h].SetIsOnWithoutNotify(_selectedHours.Contains(h));
        _updatingHours = false;
        UpdateHourVisuals();
    }

    private void UpdateHourVisuals()
    {
        for (int h = 0; h < _hourToggles.Count; h++)
        {
            Graphic g = _hourToggles[h].targetGraphic;
            if (g != null)
                g.color = _selectedHours.Contains(h) ? hourSelectedColor : hourNormalColor;
        }
    }

    private string BuildHoursCsv()
    {
        var list = new List<int>(_selectedHours);
        list.Sort();
        return string.Join(",", list);   // es. "8,12,15"
    }

    private void SetHoursFromCsv(string csv)
    {
        _selectedHours.Clear();
        if (!string.IsNullOrEmpty(csv))
            foreach (string p in csv.Split(','))
                if (int.TryParse(p.Trim(), out int h))
                    _selectedHours.Add(Mathf.Clamp(h, 0, 23));

        if (_selectedHours.Count == 0) _selectedHours.Add(14);   // almeno un'ora

        SyncTogglesToSelection();
    }

    // ---------- Utility ----------
    private static int PaletteMax() => ThermalSimNetworkController.PaletteCount - 1;
    private static int Clamp(int v, int lo, int hi) => Mathf.Clamp(v, lo, hi);
    private static int Wrap(int v, int lo, int hi) { int n = hi - lo + 1; return ((v - lo) % n + n) % n + lo; }
    private static void Wire(Button b, UnityAction action) { if (b != null) b.onClick.AddListener(action); }
}

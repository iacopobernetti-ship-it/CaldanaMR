using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Terzo pannello - parte informativa:
///  (a) statistiche della simulazione lette dal JSON di risposta (Tmin, Tmedia, Tmax, radiazione media);
///  (b) legenda grafica della rampa colori selezionata con i riferimenti min/max.
///
/// Si aggiorna ad ogni cambio di stato del controller (nuova simulazione, cambio rampa/range),
/// perche' OnStateChanged viene emesso DOPO che i nuovi dati/colori sono stati applicati.
/// </summary>
public class ThermalSimInfoPanel : MonoBehaviour
{
    [Header("Riferimenti")]
    public ThermalSimNetworkController controller;

    [Tooltip("Usati solo come ripiego quando il controller non ha borghi configurati. " +
             "Con piu' borghi il pannello segue da se' quello attivo: i campi qui sotto " +
             "vengono ignorati.")]
    public TemperatureApiClient apiClient;
    public TriangleTemperatureColorizer colorizer;

    // Con piu' borghi sul tavolo i riferimenti fissi mostravano sempre i numeri dello
    // stesso modello: le statistiche di Caldana restavano a fianco dei colori di Bibbona,
    // e la legenda riportava un intervallo che non era quello disegnato. Qui si chiede al
    // controller qual e' il borgo attivo, invece di fidarsi di cio' che c'e' in Inspector.
    private TemperatureApiClient ActiveApi =>
        (controller != null && controller.ActiveApiClient != null)
            ? controller.ActiveApiClient
            : apiClient;

    private TriangleTemperatureColorizer ActiveColorizer =>
        (controller != null && controller.ActiveTriangleColorizer != null)
            ? controller.ActiveTriangleColorizer
            : colorizer;

    [Header("(a) Info simulazione (TMP)")]
    public TMP_Text tMinLabel;
    public TMP_Text tMeanLabel;
    public TMP_Text tMaxLabel;
    public TMP_Text meanRadiationLabel;

    [Header("(b) Legenda rampa colori")]
    [Tooltip("Barra che mostra il gradiente. Usa una RawImage (mostra una Texture2D generata).")]
    public RawImage rampImage;
    public TMP_Text rampMinLabel;   // valore all'estremo 'min'
    public TMP_Text rampMaxLabel;   // valore all'estremo 'max'
    [Tooltip("Risoluzione orizzontale della barra gradiente (px).")]
    public int rampResolution = 128;

    private Texture2D _rampTex;

    private void OnEnable()
    {
        if (controller != null) controller.OnStateChanged += Refresh;
        Refresh();
    }

    private void OnDisable()
    {
        if (controller != null) controller.OnStateChanged -= Refresh;
    }

    public void Refresh()
    {
        RefreshInfo();
        RefreshRamp();
    }

    // (a) ---------------------------------------------------------------
    private void RefreshInfo()
    {
        TemperatureApiClient api = ActiveApi;
        MetadataData m = (api != null && api.responseData != null)
                       ? api.responseData.metadata : null;

        if (m == null)
        {
            SetText(tMinLabel, "T min: --");
            SetText(tMeanLabel, "T mean: --");
            SetText(tMaxLabel, "T max: --");
            SetText(meanRadiationLabel, "Mean radiation: --");
            return;
        }

        SetText(tMinLabel,  $"T min: {m.temperature_min_c:0.0} \u00B0C");
        SetText(tMeanLabel, $"T mean: {m.temperature_mean_c:0.0} \u00B0C");
        SetText(tMaxLabel,  $"T max: {m.temperature_max_c:0.0} \u00B0C");
        SetText(meanRadiationLabel, $"Mean radiation: {m.mean_radiation_reference_w_m2:0} W/m\u00B2");
    }

    // (b) ---------------------------------------------------------------
    private void RefreshRamp()
    {
        TriangleTemperatureColorizer target = ActiveColorizer;
        if (target == null) return;

        // Estremi della legenda = intervallo EFFETTIVAMENTE applicato ai colori.
        // Con lo stiramento ai percentili e' piu' stretto degli estremi del dato, che
        // restano visibili qui sopra come "T min" e "T max": i due numeri non devono
        // coincidere, e la differenza fra loro e' quanto si e' tagliato alle code.
        float min = target.HasAppliedRange ? target.AppliedColorMin : target.minTemp;
        float max = target.HasAppliedRange ? target.AppliedColorMax : target.maxTemp;
        SetText(rampMinLabel, $"{min:0.0} \u00B0C");
        SetText(rampMaxLabel, $"{max:0.0} \u00B0C");

        if (rampImage == null) return;

        int w = Mathf.Max(2, rampResolution);
        if (_rampTex == null || _rampTex.width != w)
        {
            _rampTex = new Texture2D(w, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
        }

        Color[] pixels = new Color[w];
        for (int x = 0; x < w; x++)
        {
            float t01 = (float)x / (w - 1);
            pixels[x] = target.EvaluateRamp(t01);
        }
        _rampTex.SetPixels(pixels);
        _rampTex.Apply(false);
        rampImage.texture = _rampTex;
    }

    private static void SetText(TMP_Text t, string s) { if (t != null) t.text = s; }
}

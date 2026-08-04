using System;
using System.Collections;
using System.Globalization;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Sincronizza in rete i parametri di simulazione (data/ora) e di colorazione
/// (range min/max + ramp) e pilota TemperatureApiClient + TriangleTemperatureColorizer.
///
/// Modello scelto:
/// - Si sincronizzano SOLO i parametri (piccoli), non l'array di temperature.
/// - Ogni client richiama l'API con gli stessi parametri: stesso input -> stesso
///   risultato -> colori identici per tutti, senza spedire array pesanti in rete.
/// - La data/ora richiede un rilancio della simulazione (cambia le temperature).
/// - Il colore (min/max + ramp) e' locale: ri-colora le temperature gia' scaricate,
///   senza richiamare l'API. Molto piu' veloce. Vedi 'forceResimOnColorChange'.
///
/// Va messo su un GameObject con NetworkObject, figlio del tavolo (Vector3.zero),
/// cosi' vive nello spazio condiviso del template.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class ThermalSimNetworkController : NetworkBehaviour
{
    [Header("Riferimenti")]
    public TemperatureApiClient apiClient;
    public TriangleTemperatureColorizer colorizer;
    [Tooltip("Client dell'API climatica (porta 8004) da cui derivare la temperatura dell'aria.")]
    public FutureClimateClient climateClient;

    [Header("Scenario climatico")]
    [Tooltip("Scenario iniziale: 0=ssp126, 1=ssp245, 2=ssp585.")]
    public int defaultScenarioIndex = 2;   // ssp585

    [Header("Comportamento")]
    [Tooltip("Se true, anche un cambio di colore rilancia la simulazione remota (sconsigliato).")]
    public bool forceResimOnColorChange = false;

    // --- Parametri di simulazione (data/ora) ---
    private readonly NetworkVariable<int> _year =
        new(2025, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    private readonly NetworkVariable<int> _dayOfYear =
        new(227, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    // Ore multiple separate da virgola senza spazi, es. "8,14". Default: 14 (sole alto).
    private readonly NetworkVariable<FixedString128Bytes> _hoursCsv =
        new("14", NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    private readonly NetworkVariable<FixedString32Bytes> _timezone =
        new("Europe/Rome", NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    // Scenario climatico CMIP6: 0=ssp126, 1=ssp245, 2=ssp585. La temperatura dell'aria
    // NON si sincronizza: ogni client la ricava da se' dall'API climatica (deterministico).
    private readonly NetworkVariable<int> _scenarioIndex =
        new(2, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // --- Parametri di colorazione ---
    private readonly NetworkVariable<bool> _autoRange =
        new(true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    private readonly NetworkVariable<float> _colorMin =
        new(15f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    private readonly NetworkVariable<float> _colorMax =
        new(45f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    private readonly NetworkVariable<int> _rampIndex =
        new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // --- Generazioni: bump per innescare azioni su tutti i client ---
    private readonly NetworkVariable<int> _simGeneration =
        new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    private readonly NetworkVariable<int> _colorGeneration =
        new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    /// <summary>Evento per aggiornare la UI quando un parametro cambia (su tutti i client).</summary>
    public event Action OnStateChanged;

    /// <summary>true = simulazione (chiamata API) in corso -> indicatore Wait/rosso.</summary>
    public event Action<bool> OnSimulationBusyChanged;
    public bool IsSimulationBusy => _simBusy;

    /// <summary>Errore dall'API climatica (messaggio in inglese) -> mostrato nel pannello.</summary>
    public event Action<string> OnClimateError;

    private bool _faceDebugReady;
    private Coroutine _runningSim;
    private bool _simBusy;

    // Ultimo esito climatico (per il readout nel pannello).
    private float _lastAirTempC;
    private float _lastTminC;
    private float _lastTmaxC;
    private bool _hasClimate;

    // ---------- Letture comode per la UI ----------
    public int Year => _year.Value;
    public int DayOfYear => _dayOfYear.Value;
    public string HoursCsv => _hoursCsv.Value.ToString();
    public int ScenarioIndex => _scenarioIndex.Value;
    // Ultima temperatura dell'aria derivata dall'API climatica e i suoi estremi giornalieri.
    public float LastAirTempC => _lastAirTempC;
    public float LastTminC => _lastTminC;
    public float LastTmaxC => _lastTmaxC;
    public bool HasClimate => _hasClimate;
    public bool AutoRange => _autoRange.Value;
    public float ColorMin => _colorMin.Value;
    public float ColorMax => _colorMax.Value;
    public int RampIndex => _rampIndex.Value;

    public override void OnNetworkSpawn()
    {
        // Tutti i client reagiscono ai cambi di parametro per aggiornare la UI.
        _year.OnValueChanged += OnAnyParamChanged;
        _dayOfYear.OnValueChanged += OnAnyParamChanged;
        _hoursCsv.OnValueChanged += OnAnyParamChanged;
        _scenarioIndex.OnValueChanged += OnAnyParamChanged;
        _autoRange.OnValueChanged += OnAnyParamChanged;
        _colorMin.OnValueChanged += OnAnyParamChanged;
        _colorMax.OnValueChanged += OnAnyParamChanged;
        _rampIndex.OnValueChanged += OnAnyParamChanged;

        // Le generazioni innescano le azioni vere e proprie.
        _simGeneration.OnValueChanged += OnSimGenerationChanged;
        _colorGeneration.OnValueChanged += OnColorGenerationChanged;

        // L'owner inizializza i NetworkVariable dai valori in Inspector dell'apiClient,
        // cosi' il pannello parte allineato alla configurazione del progetto.
        if (IsOwner && apiClient != null)
        {
            _year.Value = apiClient.year;
            _dayOfYear.Value = apiClient.dayOfYear;
            _hoursCsv.Value = IntArrayToCsv(apiClient.timesOfDay);
            _timezone.Value = apiClient.timezoneStr ?? "Europe/Rome";
            _scenarioIndex.Value = Mathf.Clamp(defaultScenarioIndex, 0, 2);
        }

        // Ogni client (incluso chi entra in ritardo) scarica la geometria una volta
        // e poi esegue la simulazione corrente, per allinearsi ai colori condivisi.
        StartCoroutine(InitialSyncRoutine());

        // Allinea subito la UI allo stato sincronizzato (valori + pannello aperto):
        // serve soprattutto al late joiner, per cui OnValueChanged non scatta all'avvio.
        OnStateChanged?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        _year.OnValueChanged -= OnAnyParamChanged;
        _dayOfYear.OnValueChanged -= OnAnyParamChanged;
        _hoursCsv.OnValueChanged -= OnAnyParamChanged;
        _scenarioIndex.OnValueChanged -= OnAnyParamChanged;
        _autoRange.OnValueChanged -= OnAnyParamChanged;
        _colorMin.OnValueChanged -= OnAnyParamChanged;
        _colorMax.OnValueChanged -= OnAnyParamChanged;
        _rampIndex.OnValueChanged -= OnAnyParamChanged;
        _simGeneration.OnValueChanged -= OnSimGenerationChanged;
        _colorGeneration.OnValueChanged -= OnColorGenerationChanged;
    }

    // ======================================================================
    //  API pubblica chiamata dalla UI (da QUALSIASI client)
    // ======================================================================

    /// <summary>Pulsante 1: imposta data/ora e rilancia la simulazione per tutti.</summary>
    public void RequestSetDateTime(int year, int dayOfYear, string hoursCsv)
    {
        SetDateTimeRpc(year, dayOfYear, new FixedString128Bytes(SanitizeCsv(hoursCsv)));
    }

    /// <summary>Riga scenario: imposta lo scenario climatico (0/1/2) e rilancia la simulazione.</summary>
    public void RequestSetScenario(int scenarioIndex) => SetScenarioRpc(Mathf.Clamp(scenarioIndex, 0, 2));

    /// <summary>Pulsante 2: imposta i parametri di colorazione (ri-colora per tutti).</summary>
    public void RequestSetColor(bool autoRange, float min, float max, int rampIndex)
    {
        SetColorRpc(autoRange, min, max, rampIndex);
    }

    /// <summary>Rilancia la simulazione con i parametri correnti (es. pulsante "Aggiorna").</summary>
    public void RequestRelaunch() => RelaunchRpc();

    // ======================================================================
    //  RPC: vengono eseguiti dall'owner, che scrive i NetworkVariable
    // ======================================================================

    [Rpc(SendTo.Owner)]
    private void SetDateTimeRpc(int year, int dayOfYear, FixedString128Bytes hoursCsv)
    {
        _year.Value = year;
        _dayOfYear.Value = Mathf.Clamp(dayOfYear, 1, 366);
        _hoursCsv.Value = hoursCsv;
        _simGeneration.Value++;  // innesca il rilancio su tutti
    }

    [Rpc(SendTo.Owner)]
    private void SetScenarioRpc(int scenarioIndex)
    {
        _scenarioIndex.Value = scenarioIndex;
        _simGeneration.Value++;  // cambia la temperatura dell'aria -> rilancia
    }

    [Rpc(SendTo.Owner)]
    private void SetColorRpc(bool autoRange, float min, float max, int rampIndex)
    {
        _autoRange.Value = autoRange;
        _colorMin.Value = min;
        _colorMax.Value = max;
        _rampIndex.Value = rampIndex;

        if (forceResimOnColorChange)
            _simGeneration.Value++;     // rilancio completo (sconsigliato)
        else
            _colorGeneration.Value++;   // sola ri-colorazione locale
    }

    [Rpc(SendTo.Owner)]
    private void RelaunchRpc() => _simGeneration.Value++;

    // ======================================================================
    //  Reazioni (su tutti i client)
    // ======================================================================

    private void OnAnyParamChanged<T>(T previous, T current) => OnStateChanged?.Invoke();

    private void OnSimGenerationChanged(int previous, int current)
    {
        if (_runningSim != null) StopCoroutine(_runningSim);
        _runningSim = StartCoroutine(RunSimulationRoutine());
    }

    private void OnColorGenerationChanged(int previous, int current)
    {
        ApplyColorParamsAndRecolor();
    }

    // ======================================================================
    //  Routine
    // ======================================================================

    private IEnumerator InitialSyncRoutine()
    {
        // Nessuna simulazione automatica all'avvio: il modello resta non colorato
        // finche' un giocatore non imposta i parametri dal pannello e preme Applica.
        // Eccezione (late joiner): se una simulazione e' gia' stata lanciata da
        // qualcuno (simGeneration > 0), il nuovo arrivato si allinea allo stato
        // condiviso eseguendola con i parametri correnti.
        if (_simGeneration.Value > 0)
            yield return RunSimulationRoutine();
    }

    private IEnumerator EnsureFaceDebug()
    {
        if (_faceDebugReady || apiClient == null) yield break;

        // La geometria (face-debug) non dipende da data/ora: si scarica una volta sola.
        yield return apiClient.DownloadFaceDebug();

        _faceDebugReady = apiClient.faceDebugData != null
                          && apiClient.faceDebugData.face_debug != null
                          && apiClient.faceDebugData.face_debug.Length > 0;
    }

    private IEnumerator RunSimulationRoutine()
    {
        if (apiClient == null) yield break;

        SetSimBusy(true);   // -> Wait / rosso
        try
        {
            yield return EnsureFaceDebug();
            if (!_faceDebugReady) yield break;

            // Spinge i parametri sincronizzati nell'apiClient prima della chiamata.
            apiClient.year = _year.Value;
            apiClient.dayOfYear = _dayOfYear.Value;
            apiClient.timesOfDay = CsvToIntArray(_hoursCsv.Value.ToString());
            // Rete di sicurezza: se nessuna ora e' selezionata, usa le 14 di default
            // (evita un payload con times_of_day vuoto -> 400 Bad Request dal server).
            if (apiClient.timesOfDay == null || apiClient.timesOfDay.Length == 0)
                apiClient.timesOfDay = new int[] { 14 };
            apiClient.timezoneStr = _timezone.Value.ToString();

            // --- Temperatura dell'aria dall'API climatica (porta 8004) ---
            // Ogni client la ricava da se' con data+scenario+coordinate identici: deterministico,
            // niente valori sfasati tra giocatori. Su errore/timeout: blocco, nessun colore aggiornato.
            if (climateClient == null)
            {
                Debug.LogError("ThermalSimNetworkController: climateClient non assegnato.");
                OnClimateError?.Invoke("Climate client not assigned.");
                yield break;
            }

            DayOfYearToDate(_year.Value, _dayOfYear.Value, out int cMonth, out int cDay);
            string scenarioId = ScenarioApiId(_scenarioIndex.Value);

            FutureClimateClient.ClimateResult climate = null;
            yield return climateClient.FetchDayMinMax(scenarioId, _year.Value, cMonth, cDay,
                                                      r => climate = r);

            if (climate == null || !climate.ok)
            {
                string msg = climate != null ? climate.error : "No response from the climate API.";
                Debug.LogError("Climate API error: " + msg);
                OnClimateError?.Invoke(msg);
                yield break;   // il finally rimette Ready; i colori restano quelli precedenti
            }

            // L'API aggrega la radiazione a MASSIMO tra le ore e usa UN solo valore d'aria
            // (meteo_station_temperature_max = massimo giornaliero). Coerentemente, come aria
            // uso il MASSIMO di T(h) tra le ore selezionate (il picco). Fallback: ora 14.
            float airTemp = float.NegativeInfinity;
            if (apiClient.timesOfDay != null && apiClient.timesOfDay.Length > 0)
            {
                foreach (int h in apiClient.timesOfDay)
                {
                    float t = FutureClimateClient.HourlyTemperature(climate.tminC, climate.tmaxC, h);
                    if (t > airTemp) airTemp = t;
                }
            }
            else
            {
                airTemp = FutureClimateClient.HourlyTemperature(climate.tminC, climate.tmaxC, 14f);
            }

            _lastTminC = climate.tminC;
            _lastTmaxC = climate.tmaxC;
            _lastAirTempC = airTemp;
            _hasClimate = true;
            apiClient.meteoStationTemperatureMax = airTemp;
            OnStateChanged?.Invoke();   // aggiorna il readout della temperatura nel pannello

            yield return apiClient.RunSimulation();

            if (apiClient.responseData == null
                || apiClient.responseData.face_temperatures == null
                || apiClient.responseData.face_temperatures.temperatures == null)
            {
                Debug.LogError("ThermalSimNetworkController: simulazione senza dati di temperatura.");
                yield break;
            }

            ApplyColorParamsAndRecolor();
        }
        finally
        {
            // Eseguito sempre: completamento, uscita anticipata o coroutine fermata.
            // Nota: sul primo build il colorizer resta "busy" finche' la mappatura non finisce;
            // l'indicatore combina i due stati, quindi non torna verde troppo presto.
            SetSimBusy(false);   // -> Ready / verde
            _runningSim = null;
        }
    }

    private void SetSimBusy(bool busy)
    {
        _simBusy = busy;
        OnSimulationBusyChanged?.Invoke(busy);
    }

    /// <summary>Applica i parametri di colore correnti e ri-colora la mesh (nessuna API).</summary>
    private void ApplyColorParamsAndRecolor()
    {
        if (colorizer == null) return;

        if (_autoRange.Value)
        {
            // Range automatico dai metadati della risposta.
            colorizer.useDynamicRangeFromResponse = true;
            colorizer.usePercentileStretch = false;
        }
        else
        {
            // Range definito dall'utente: disattiva gli override automatici.
            colorizer.useDynamicRangeFromResponse = false;
            colorizer.usePercentileStretch = false;
            colorizer.minTemp = _colorMin.Value;
            colorizer.maxTemp = _colorMax.Value;
        }

        // Ramp: usa una delle palette preimpostate (no gradient custom).
        colorizer.useCustomGradient = false;
        colorizer.palettePreset = (TemperaturePalette)Mathf.Clamp(
            _rampIndex.Value, 0, Enum.GetValues(typeof(TemperaturePalette)).Length - 1);

        colorizer.ApplyColors();
        OnStateChanged?.Invoke();
    }

    // ======================================================================
    //  Utility
    // ======================================================================

    public static int PaletteCount => Enum.GetValues(typeof(TemperaturePalette)).Length;
    public static string[] PaletteNames => Enum.GetNames(typeof(TemperaturePalette));

    /// <summary>Nomi degli scenari CMIP6 nell'ordine dei pulsanti (0,1,2).</summary>
    public static readonly string[] ScenarioNames = { "ssp126", "ssp245", "ssp585" };

    /// <summary>Identificativo scenario per l'API climatica, dato l'indice del pulsante.</summary>
    public static string ScenarioApiId(int index) =>
        ScenarioNames[Mathf.Clamp(index, 0, ScenarioNames.Length - 1)];

    /// <summary>Converte una data calendario in giorno dell'anno (1..366).</summary>
    public static int DateToDayOfYear(int year, int month, int day)
    {
        month = Mathf.Clamp(month, 1, 12);
        int maxDay = DateTime.DaysInMonth(year, month);
        day = Mathf.Clamp(day, 1, maxDay);
        return new DateTime(year, month, day).DayOfYear;
    }

    /// <summary>Converte (anno, giorno dell'anno) in mese e giorno calendario.</summary>
    public static void DayOfYearToDate(int year, int dayOfYear, out int month, out int day)
    {
        int max = DateTime.IsLeapYear(year) ? 366 : 365;
        dayOfYear = Mathf.Clamp(dayOfYear, 1, max);
        DateTime d = new DateTime(year, 1, 1).AddDays(dayOfYear - 1);
        month = d.Month;
        day = d.Day;
    }

    private static string SanitizeCsv(string csv)
    {
        if (string.IsNullOrEmpty(csv)) return "";
        // Rimuove spazi: "8, 14" -> "8,14"
        return csv.Replace(" ", "").Trim(',');
    }

    private static int[] CsvToIntArray(string csv)
    {
        csv = SanitizeCsv(csv);
        if (string.IsNullOrEmpty(csv)) return Array.Empty<int>();

        string[] parts = csv.Split(',');
        var list = new System.Collections.Generic.List<int>(parts.Length);
        foreach (string p in parts)
        {
            if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h))
                list.Add(Mathf.Clamp(h, 0, 23));
        }
        return list.ToArray();
    }

    private static string IntArrayToCsv(int[] values)
    {
        if (values == null || values.Length == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < values.Length; i++)
        {
            sb.Append(values[i]);
            if (i < values.Length - 1) sb.Append(',');
        }
        return sb.ToString();
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Descrive un borgo selezionabile sul tavolo.
///
/// Ogni borgo ha il suo modello sul servizio 8006 (user_id + model_id), la sua mesh in
/// scena con il proprio colorizer, e le proprie coordinate. Tenere un TemperatureApiClient
/// per borgo, invece di riusarne uno solo cambiandogli il model_id, serve a conservare la
/// cache: la geometria (face-debug) e l'ultima risposta restano in memoria, quindi tornare
/// al borgo precedente e' immediato e non ripassa dalla rete.
/// </summary>
[Serializable]
public class VillageSlot
{
    [Tooltip("Nome mostrato nei pulsanti e nel pannello informazioni.")]
    public string displayName = "Caldana";

    [Tooltip("Radice del modello 3D in scena: viene attivata solo quando il borgo e' selezionato.")]
    public GameObject root;

    [Tooltip("Client dell'API temperature superficiali configurato con il model_id di QUESTO borgo.")]
    public TemperatureApiClient apiClient;

    [Tooltip("Colorizer sulla mesh di questo borgo.")]
    public TriangleTemperatureColorizer colorizer;

    [Header("Sito")]
    [Tooltip("Latitudine del borgo: guida il Sole e la richiesta all'API climatica.")]
    public double latitude = 42.9340;

    [Tooltip("Longitudine del borgo.")]
    public double longitude = 10.8580;

    [Tooltip("Rotazione da applicare per allineare il Nord reale al +Z del modello importato. " +
             "E' una proprieta' della mesh, non del luogo: due rilievi diversi hanno quasi " +
             "sempre valori diversi.")]
    public float northOffsetDeg = 0f;

    // ------------------------------------------------------------------
    //  Cache di runtime (non serializzata: vive solo nella sessione)
    //
    //  Firma dei parametri con cui e' stata calcolata la risposta che questo
    //  slot ha in pancia. Se allo switch la firma coincide, la chiamata
    //  /simulate si salta e si ri-colora e basta: e' il caso dell'avanti e
    //  indietro fra due borghi sulla stessa data, cioe' il gesto del confronto.
    // ------------------------------------------------------------------
    [NonSerialized] public string cachedSignature;

    // Il readout climatico va memorizzato insieme alla risposta: dipende dalle
    // coordinate, quindi e' diverso per ogni borgo. Senza questo, saltando la
    // chiamata il pannello mostrerebbe la temperatura dell'altro borgo.
    [NonSerialized] public float cachedTminC;
    [NonSerialized] public float cachedTmaxC;
    [NonSerialized] public float cachedAirC;
}

/// <summary>
/// Sincronizza in rete i parametri di simulazione (borgo, data/ora, scenario) e di colorazione
/// (range min/max + ramp) e pilota TemperatureApiClient + TriangleTemperatureColorizer.
///
/// Modello scelto:
/// - Si sincronizzano SOLO i parametri (piccoli), non l'array di temperature.
/// - Ogni client richiama l'API con gli stessi parametri: stesso input -> stesso
///   risultato -> colori identici per tutti, senza spedire array pesanti in rete.
/// - La data/ora richiede un rilancio della simulazione (cambia le temperature).
/// - Il colore (min/max + ramp) e' locale: ri-colora le temperature gia' scaricate,
///   senza richiamare l'API. Molto piu' veloce. Vedi 'forceResimOnColorChange'.
/// - Il borgo attivo e' anch'esso sincronizzato: cambiarlo rilancia la simulazione con
///   data, ora e scenario invariati, che e' il confronto che interessa.
///
/// Va messo su un GameObject con NetworkObject, figlio del tavolo (Vector3.zero),
/// cosi' vive nello spazio condiviso del template.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class ThermalSimNetworkController : NetworkBehaviour
{
    [Header("Borghi selezionabili")]
    [Tooltip("Uno slot per borgo. Il primo e' quello attivo all'avvio. " +
             "Se l'array e' vuoto si usano i riferimenti singoli qui sotto (modalita' a un borgo).")]
    public VillageSlot[] villages = new VillageSlot[0];

    [Header("Riferimenti condivisi")]
    [Tooltip("Sole prototipo: allo switch ne aggiorno coordinate e orientamento del Nord.")]
    public SunPositioner sun;

    [Tooltip("Stato di rete dei marker della sonda. Allo switch i marker vengono azzerati, " +
             "perche' le loro coordinate sono locali al modello e su un altro borgo " +
             "indicherebbero un punto che nessuno ha mai misurato.")]
    public ProbeMarkerNetwork markerNetwork;

    [Tooltip("Client dell'API climatica (porta 8004) da cui derivare la temperatura dell'aria.")]
    public FutureClimateClient climateClient;

    [Header("Riferimenti singoli (solo se 'villages' e' vuoto)")]
    [Tooltip("Usati come ripiego quando non sono configurati gli slot: tengono in vita " +
             "la scena esistente finche' non si popola l'array dei borghi.")]
    public TemperatureApiClient apiClient;
    public TriangleTemperatureColorizer colorizer;

    [Header("Scenario climatico")]
    [Tooltip("Scenario iniziale: 0=ssp126, 1=ssp245, 2=ssp585.")]
    public int defaultScenarioIndex = 2;   // ssp585

    [Header("Comportamento")]
    [Tooltip("Se true, anche un cambio di colore rilancia la simulazione remota (sconsigliato).")]
    public bool forceResimOnColorChange = false;

    // --- Borgo attivo ---
    private readonly NetworkVariable<int> _villageIndex =
        new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

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
    // ATTENZIONE ALL'ORDINE DI DICHIARAZIONE: i NetworkVariable dello stesso
    // NetworkBehaviour viaggiano in un unico messaggio e vengono applicati
    // nell'ordine in cui sono dichiarati. _cacheEpoch sta PRIMA di _simGeneration
    // perche' quando "Aggiorna" li incrementa entrambi, l'epoca dev'essere gia'
    // cambiata nel momento in cui la simulazione parte: altrimenti la cache
    // risponderebbe con il risultato vecchio e il pulsante non aggiornerebbe nulla.
    private readonly NetworkVariable<int> _cacheEpoch =
        new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
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

    // Preferenza di stiramento ai percentili come configurata in Inspector su ciascun
    // colorizer.
    //
    // Va ricordata qui perche' la modalita' a range manuale deve spegnere lo stiramento,
    // e spegnerlo significa SCRIVERE false nel componente: da quel momento il valore che
    // avevi scelto in Inspector e' perduto, e tornando in Auto lo stiramento non riparte
    // piu' su quel borgo. E' il motivo per cui funzionava su un modello e non sull'altro.
    private readonly Dictionary<TriangleTemperatureColorizer, bool> _stretchPreference =
        new Dictionary<TriangleTemperatureColorizer, bool>();

    // Stesso discorso per il gradiente personalizzato: il controller lo forzava a false
    // per imporre una palette preimpostata, cancellando il gradiente disegnato a mano.
    private readonly Dictionary<TriangleTemperatureColorizer, bool> _customGradientPreference =
        new Dictionary<TriangleTemperatureColorizer, bool>();

    private Coroutine _runningSim;
    private bool _simBusy;
    private int _appliedVillageIndex = -1;   // -1 = selezione non ancora applicata in scena

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

    /// <summary>Indice del borgo attivo, sincronizzato fra i giocatori.</summary>
    public int VillageIndex => ClampVillage(_villageIndex.Value);

    /// <summary>Quanti borghi sono configurati (0 = modalita' a un borgo con i riferimenti singoli).</summary>
    public int VillageCount => villages != null ? villages.Length : 0;

    /// <summary>Nome del borgo attivo, per i pannelli.</summary>
    public string ActiveVillageName
    {
        get
        {
            VillageSlot s = ActiveSlot;
            return s != null && !string.IsNullOrEmpty(s.displayName) ? s.displayName : "";
        }
    }

    /// <summary>
    /// Client API del borgo attivo. Serve ai pannelli che leggono i metadati della
    /// risposta (minime, medie, massime, radiazione): senza questo leggerebbero sempre
    /// il borgo collegato in Inspector, mostrando i numeri di Caldana accanto ai colori
    /// di Bibbona.
    /// </summary>
    public TemperatureApiClient ActiveApiClient => ActiveApi;

    /// <summary>Colorizer del borgo attivo, per la legenda della rampa.</summary>
    public TriangleTemperatureColorizer ActiveTriangleColorizer => ActiveColorizer;

    /// <summary>Nome dell'i-esimo borgo, per etichettare i pulsanti.</summary>
    public string VillageNameAt(int i)
    {
        if (villages == null || i < 0 || i >= villages.Length || villages[i] == null) return "";
        return villages[i].displayName;
    }

    // ---------- Risoluzione dello slot attivo ----------

    private int ClampVillage(int i)
    {
        if (villages == null || villages.Length == 0) return 0;
        return Mathf.Clamp(i, 0, villages.Length - 1);
    }

    private VillageSlot ActiveSlot
    {
        get
        {
            if (villages == null || villages.Length == 0) return null;
            return villages[ClampVillage(_villageIndex.Value)];
        }
    }

    // Tutto il resto del controller passa da qui, invece di usare i campi singoli:
    // cosi' la logica e' una sola, con o senza slot configurati.
    private TemperatureApiClient ActiveApi
    {
        get { VillageSlot s = ActiveSlot; return s != null && s.apiClient != null ? s.apiClient : apiClient; }
    }

    private TriangleTemperatureColorizer ActiveColorizer
    {
        get { VillageSlot s = ActiveSlot; return s != null && s.colorizer != null ? s.colorizer : colorizer; }
    }

    public override void OnNetworkSpawn()
    {
        // Tutti i client reagiscono ai cambi di parametro per aggiornare la UI.
        _villageIndex.OnValueChanged += OnVillageChanged;
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

        // L'owner inizializza i NetworkVariable dai valori in Inspector dell'apiClient attivo,
        // cosi' il pannello parte allineato alla configurazione del progetto.
        TemperatureApiClient api = ActiveApi;
        if (IsOwner && api != null)
        {
            _year.Value = api.year;
            _dayOfYear.Value = api.dayOfYear;
            _hoursCsv.Value = IntArrayToCsv(api.timesOfDay);
            _timezone.Value = api.timezoneStr ?? "Europe/Rome";
            _scenarioIndex.Value = Mathf.Clamp(defaultScenarioIndex, 0, 2);
        }

        // La palette iniziale la decide l'Inspector del colorizer, non il valore di
        // partenza del NetworkVariable.
        //
        // _rampIndex nasce a 0, e ApplyColorParamsAndRecolor scrive quell'indice dentro
        // palettePreset a ogni colorazione: senza questa riga la scelta fatta in scena
        // veniva sostituita dalla prima voce dell'enum e non si vedeva mai, su nessuno
        // dei due borghi.
        if (IsOwner)
        {
            TriangleTemperatureColorizer refColorizer = ActiveColorizer;
            if (refColorizer != null)
                _rampIndex.Value = Mathf.Clamp((int)refColorizer.palettePreset, 0, PaletteCount - 1);
        }

        // Mostra subito il borgo giusto, anche a chi entra a sessione avviata.
        ApplyVillageSelection(force: true);

        // Ogni client (incluso chi entra in ritardo) scarica la geometria una volta
        // e poi esegue la simulazione corrente, per allinearsi ai colori condivisi.
        StartCoroutine(InitialSyncRoutine());

        // Allinea subito la UI allo stato sincronizzato (valori + pannello aperto):
        // serve soprattutto al late joiner, per cui OnValueChanged non scatta all'avvio.
        OnStateChanged?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        _villageIndex.OnValueChanged -= OnVillageChanged;
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

    /// <summary>Selettore borgo: cambia modello per tutti e rilancia con gli stessi parametri.</summary>
    public void RequestSetVillage(int villageIndex)
    {
        if (villages == null || villages.Length == 0) return;
        int target = Mathf.Clamp(villageIndex, 0, villages.Length - 1);
        if (target == _villageIndex.Value) return;   // gia' attivo: niente rilancio inutile
        SetVillageRpc(target);
    }

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
    private void SetVillageRpc(int villageIndex)
    {
        _villageIndex.Value = villageIndex;

        // I marker sono ancorati al frame locale del modello: su un altro borgo
        // finirebbero su una superficie che nessuno ha misurato. Si azzerano.
        if (markerNetwork != null) markerNetwork.RequestClearAll();

        _simGeneration.Value++;  // stessa data, stessa ora, stesso scenario: cambia solo il modello
    }

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
    private void RelaunchRpc()
    {
        // "Aggiorna" deve rinterrogare davvero i servizi, altrimenti non servirebbe
        // a niente: cambiando epoca tutte le firme in cache diventano obsolete su
        // tutti i client, e la simulazione riparte dalla rete.
        _cacheEpoch.Value++;
        _simGeneration.Value++;
    }

    // ======================================================================
    //  Reazioni (su tutti i client)
    // ======================================================================

    private void OnAnyParamChanged<T>(T previous, T current) => OnStateChanged?.Invoke();

    private void OnVillageChanged(int previous, int current)
    {
        ApplyVillageSelection();
        OnStateChanged?.Invoke();
    }

    private void OnSimGenerationChanged(int previous, int current)
    {
        // L'ordine di arrivo dei NetworkVariable non e' garantito: si riapplica la
        // selezione prima di simulare, cosi' la simulazione parte sul modello giusto
        // anche se l'indice del borgo e' arrivato nello stesso frame.
        ApplyVillageSelection();

        if (_runningSim != null) StopCoroutine(_runningSim);
        _runningSim = StartCoroutine(RunSimulationRoutine());
    }

    private void OnColorGenerationChanged(int previous, int current)
    {
        ApplyColorParamsAndRecolor();
    }

    /// <summary>
    /// Accende il modello del borgo attivo, spegne gli altri e allinea il Sole al sito.
    /// E' un'operazione puramente locale: ogni client la esegue per conto suo a partire
    /// dall'indice sincronizzato.
    /// </summary>
    private void ApplyVillageSelection(bool force = false)
    {
        if (villages == null || villages.Length == 0) return;

        int idx = ClampVillage(_villageIndex.Value);
        if (!force && idx == _appliedVillageIndex) return;
        _appliedVillageIndex = idx;

        for (int i = 0; i < villages.Length; i++)
        {
            VillageSlot s = villages[i];
            if (s == null || s.root == null) continue;
            s.root.SetActive(i == idx);
        }

        VillageSlot active = villages[idx];
        if (active != null && sun != null)
        {
            // Il Sole resta dov'e' nella gerarchia (fratello dei modelli, sotto il
            // contenitore comune): cambiano solo sito e orientamento del Nord, che
            // sono proprieta' del borgo e della sua mesh.
            sun.latitude = active.latitude;
            sun.longitude = active.longitude;
            sun.northOffsetDeg = active.northOffsetDeg;
            sun.UpdateSun();
        }

        // climateClient legge latitudine e longitudine dal SunPositioner, quindi
        // interrogando l'API climatica usera' da solo le coordinate del borgo attivo.
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

    /// <summary>true se la geometria di questo client e' gia' stata scaricata per quel modello.</summary>
    private static bool HasFaceDebug(TemperatureApiClient c) =>
        c != null && c.faceDebugData != null &&
        c.faceDebugData.face_debug != null && c.faceDebugData.face_debug.Length > 0;

    /// <summary>true se il client ha gia' in pancia un array di temperature valido.</summary>
    private static bool HasTemperatures(TemperatureApiClient c) =>
        c != null && c.responseData != null &&
        c.responseData.face_temperatures != null &&
        c.responseData.face_temperatures.temperatures != null;

    /// <summary>
    /// Firma dei parametri che determinano il risultato della simulazione.
    /// Non comprende i parametri di colore (ri-colorazione locale) ne' il borgo
    /// (ogni slot ha la propria firma), ma comprende l'epoca della cache, che
    /// "Aggiorna" incrementa per forzare una rilettura dai servizi.
    /// </summary>
    private string BuildSignature()
    {
        return string.Concat(
            _cacheEpoch.Value.ToString(CultureInfo.InvariantCulture), "|",
            _year.Value.ToString(CultureInfo.InvariantCulture), "|",
            _dayOfYear.Value.ToString(CultureInfo.InvariantCulture), "|",
            _hoursCsv.Value.ToString(), "|",
            _scenarioIndex.Value.ToString(CultureInfo.InvariantCulture));
    }

    private IEnumerator EnsureFaceDebug(TemperatureApiClient api)
    {
        // La geometria non dipende da data/ora e viene tenuta nel client del borgo:
        // si scarica una volta per borgo, non una volta per applicazione. Tornare
        // indietro non ripassa dalla rete.
        if (api == null || HasFaceDebug(api)) yield break;
        yield return api.DownloadFaceDebug();
    }

    private IEnumerator RunSimulationRoutine()
    {
        TemperatureApiClient api = ActiveApi;
        if (api == null) yield break;

        VillageSlot slot = ActiveSlot;
        string signature = BuildSignature();

        SetSimBusy(true);   // -> Wait / rosso
        try
        {
            // --- Cache: questo borgo e' gia' stato calcolato con questi parametri? ---
            // La firma non comprende i parametri di colore, perche' la ri-colorazione
            // e' locale e non richiede alcuna chiamata: cambiare palette o range non
            // deve invalidare nulla.
            if (slot != null && slot.cachedSignature == signature
                && HasFaceDebug(api) && HasTemperatures(api))
            {
                _lastTminC = slot.cachedTminC;
                _lastTmaxC = slot.cachedTmaxC;
                _lastAirTempC = slot.cachedAirC;
                _hasClimate = true;

                ApplyColorParamsAndRecolor();   // ri-colora e notifica la UI
                yield break;                    // il finally rimette Ready
            }

            yield return EnsureFaceDebug(api);
            if (!HasFaceDebug(api)) yield break;

            // Spinge i parametri sincronizzati nell'apiClient prima della chiamata.
            api.year = _year.Value;
            api.dayOfYear = _dayOfYear.Value;
            api.timesOfDay = CsvToIntArray(_hoursCsv.Value.ToString());
            // Rete di sicurezza: se nessuna ora e' selezionata, usa le 14 di default
            // (evita un payload con times_of_day vuoto -> 400 Bad Request dal server).
            if (api.timesOfDay == null || api.timesOfDay.Length == 0)
                api.timesOfDay = new int[] { 14 };
            api.timezoneStr = _timezone.Value.ToString();

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
            if (api.timesOfDay != null && api.timesOfDay.Length > 0)
            {
                foreach (int h in api.timesOfDay)
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
            api.meteoStationTemperatureMax = airTemp;
            OnStateChanged?.Invoke();   // aggiorna il readout della temperatura nel pannello

            yield return api.RunSimulation();

            if (!HasTemperatures(api))
            {
                Debug.LogError("ThermalSimNetworkController: simulazione senza dati di temperatura.");
                yield break;
            }

            // Si registra la firma solo adesso, a risultato buono in mano: una firma
            // scritta prima trasformerebbe un errore di rete in un risultato "valido"
            // che la cache servirebbe per tutto il resto della sessione.
            if (slot != null)
            {
                slot.cachedSignature = signature;
                slot.cachedTminC = _lastTminC;
                slot.cachedTmaxC = _lastTmaxC;
                slot.cachedAirC = _lastAirTempC;
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
        TriangleTemperatureColorizer target = ActiveColorizer;
        if (target == null) return;

        // Si fotografa la preferenza la prima volta che si incontra questo colorizer,
        // cioe' prima di averla potuta sovrascrivere.
        if (!_stretchPreference.ContainsKey(target))
            _stretchPreference[target] = target.usePercentileStretch;
        if (!_customGradientPreference.ContainsKey(target))
            _customGradientPreference[target] = target.useCustomGradient;

        if (_autoRange.Value)
        {
            // Range automatico ricavato dalla risposta DI QUESTO borgo: due modelli
            // alla stessa data e ora ottengono quindi due scale diverse, ciascuna
            // centrata sulla propria distribuzione. E' voluto: nel confronto interessa
            // dove sono le zone calde e fredde dentro ciascun tessuto urbano, non di
            // quanto un paese sia complessivamente piu' caldo dell'altro - quella
            // differenza si legge nei valori, non nei colori.
            target.useDynamicRangeFromResponse = true;

            // Si RIPRISTINA la preferenza di Inspector, invece di limitarsi a non
            // toccarla: se una sessione precedente e' passata dal range manuale, il
            // campo e' gia' stato azzerato e lasciarlo com'e' non basterebbe.
            //
            // Lo stiramento serve proprio qui: poche facce estreme, una falda quasi
            // parallela ai raggi o uno spigolo molto esposto, schiacciano tutto il resto
            // della rampa in una fascia strettissima e rendono illeggibile la
            // distribuzione che si vuole leggere.
            target.usePercentileStretch = _stretchPreference[target];
        }
        else
        {
            // Range definito dall'utente: qui invece lo stiramento va spento, altrimenti
            // i valori min/max scelti a mano verrebbero ignorati.
            target.useDynamicRangeFromResponse = false;
            target.usePercentileStretch = false;
            target.minTemp = _colorMin.Value;
            target.maxTemp = _colorMax.Value;
        }

        // Rampa. Se il colorizer era configurato con un gradiente personalizzato, quello
        // comanda e la palette preimpostata non viene toccata: imporla significherebbe
        // buttare via un gradiente disegnato a mano, che e' una scelta di interpretazione
        // del dato, non un valore di comodo.
        target.useCustomGradient = _customGradientPreference[target];
        if (!target.useCustomGradient)
        {
            target.palettePreset = (TemperaturePalette)Mathf.Clamp(
                _rampIndex.Value, 0, Enum.GetValues(typeof(TemperaturePalette)).Length - 1);
        }

        target.ApplyColors();
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

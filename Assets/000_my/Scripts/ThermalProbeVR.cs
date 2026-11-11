using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Terzo pannello - sonda temperatura:
///  - un pulsante attiva/disattiva la modalita' mira (crocetta stile FPS);
///  - mentre miri, una crocetta (reticle) si posiziona sul punto del modello e mostra la
///    temperatura in tempo reale;
///  - "Spara" (Fire) congela la temperatura del punto mirato nel pannello e lascia un marcatore.
///
/// 'aimSource' decide il tipo di mira:
///   - Main Camera  -> mira con lo sguardo (sensazione FPS, crocetta dove guardi);
///   - un controller -> mira con il puntatore del controller.
///
/// La temperatura e' deterministica (uguale per tutti), quindi la sonda e' LOCALE: ogni utente
/// puo' interrogare i propri punti senza networking.
///
/// PIU' BORGHI SUL TAVOLO
/// La sonda non e' legata a un modello preciso: accetta QUALSIASI colorizer colpisca e si
/// ricorda quale fosse al momento della mira. In precedenza confrontava il colpo con un unico
/// colorizer assegnato in Inspector, e sul secondo borgo il confronto falliva in silenzio:
/// niente crocetta, niente marker, nessun messaggio.
///
/// NOTA: per ottenere il triangolo colpito il MeshCollider del modello deve essere NON convex
/// (hit.triangleIndex e' valido solo sui mesh collider non convessi).
/// </summary>
public class ThermalProbeVR : MonoBehaviour
{
    [Header("Riferimenti")]
    [Tooltip("Origine del raggio di mira: la Main Camera (sguardo) o un controller (puntatore).")]
    public Transform aimSource;

    [Tooltip("Colorizer di partenza. Con piu' borghi non e' piu' vincolante: la sonda usa " +
             "quello che colpisce. Si puo' lasciare assegnato al primo borgo o lasciare vuoto.")]
    public TriangleTemperatureColorizer colorizer;

    [Tooltip("Crocetta di mira: spostata sul punto colpito mentre miri.")]
    public Transform reticle;
    [Tooltip("Marcatore opzionale lasciato nell'ultimo punto 'sparato'.")]
    public Transform shotMarker;

    [Tooltip("Se assegnato, lo sparo crea un marker CONDIVISO (uno per utente, colorato). " +
             "In tal caso il marcatore locale sopra viene ignorato.")]
    public ProbeMarkerNetwork markerNetwork;

    [Tooltip("Trasformazione in cui vengono espressi i marker condivisi. Deve essere LO STESSO " +
             "oggetto assegnato come 'Model Root' su ProbeMarkerRenderer, altrimenti i marker " +
             "compaiono spostati: il mittente e il destinatario userebbero due sistemi di " +
             "riferimento diversi. Con piu' borghi e' il contenitore comune (Villages). " +
             "Se lasciato vuoto si usa il modello colpito, come nella versione a un borgo solo.")]
    public Transform markerSpace;

    [Header("Output (TMP)")]
    [Tooltip("Temperatura in tempo reale del punto mirato (opzionale).")]
    public TMP_Text liveLabel;
    [Tooltip("Temperatura 'sparata', congelata fino al prossimo sparo.")]
    public TMP_Text resultLabel;

    [Header("Parametri")]
    public float maxDistance = 50f;
    [Tooltip("Allinea la crocetta alla superficie colpita.")]
    public bool alignReticleToSurface = true;

    [Header("Sparo - opzioni (entrambe utilizzabili)")]
    [Tooltip("Opzione A - Pulsante UI: collega l'onClick del pulsante 'Spara' al metodo Fire().")]
    public bool fireViaUiButton = true;   // informativo: il wiring del pulsante e' nell'editor
    [Tooltip("Opzione B - Trigger controller: assegna qui un'azione (es. activate/trigger). " +
             "Lascia vuoto per non usare il trigger.")]
    public InputActionProperty fireAction;

    private bool _active;
    private bool _hasAim;            // mira valida nel frame corrente (per crocetta + label live)
    private bool _hasCapturedAim;    // almeno un punto valido catturato da quando la sonda e' attiva
    private Vector3 _aimPoint;
    private Vector3 _aimNormal = Vector3.up;
    private int _aimTriangle = -1;
    private float _aimTemp;

    // Colorizer colpito al momento dell'ultima mira valida. Va ricordato insieme al punto:
    // sparando con il pulsante UI possono passare dei frame, e nel frattempo lo sguardo
    // potrebbe essere altrove o il borgo attivo potrebbe essere cambiato.
    private TriangleTemperatureColorizer _aimColorizer;

    private const int IgnoreRaycastLayer = 2;   // layer predefinito di Unity escluso dai raycast

    public bool IsActive => _active;

    private void Awake()
    {
        // Crocetta e marcatore NON devono intercettare il raggio della sonda:
        // stanno nello stesso spazio del modello e, se colpiti, falserebbero mira e posizione.
        // Mettendoli su "Ignore Raycast" il Physics.Raycast di default li ignora.
        SetLayerRecursive(reticle, IgnoreRaycastLayer);
        SetLayerRecursive(shotMarker, IgnoreRaycastLayer);
    }

    private void Start()
    {
        ResolveMarkerSpace();
    }

    /// <summary>
    /// Allinea il sistema di riferimento dei marker a quello che usa davvero il renderer.
    ///
    /// Mittente e destinatario devono misurare nello stesso spazio: la sonda invia
    /// coordinate locali, il renderer le interpreta rispetto al proprio 'Model Root'.
    /// Lasciare che li si imposti a mano in due punti diversi dell'Inspector significa
    /// che prima o poi divergono - ed e' esattamente cio' che accade quando il marker
    /// non compare su un borgo e compare sull'altro: le coordinate di un modello in scala
    /// 0.003 lette come se fossero del contenitore finiscono sepolte vicino all'origine.
    /// Qui il valore si prende dalla fonte, cosi' non possono piu' disaccordarsi.
    /// </summary>
    private void ResolveMarkerSpace()
    {
        ProbeMarkerRenderer rendererComponent =
            FindFirstObjectByType<ProbeMarkerRenderer>(FindObjectsInactive.Include);

        if (rendererComponent == null || rendererComponent.modelRoot == null) return;

        if (markerSpace == null)
        {
            markerSpace = rendererComponent.modelRoot;
            return;
        }

        if (markerSpace != rendererComponent.modelRoot)
        {
            Debug.LogWarning(
                $"ThermalProbeVR: 'Marker Space' ({markerSpace.name}) e il 'Model Root' del " +
                $"ProbeMarkerRenderer ({rendererComponent.modelRoot.name}) sono oggetti diversi. " +
                "I marker comparirebbero spostati: uso il Model Root del renderer.");
            markerSpace = rendererComponent.modelRoot;
        }
    }

    private static void SetLayerRecursive(Transform t, int layer)
    {
        if (t == null) return;
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++)
            SetLayerRecursive(t.GetChild(i), layer);
    }

    private void OnEnable()
    {
        InputAction a = fireAction.action;
        if (a != null)
        {
            a.performed += OnFirePerformed;
            if (!a.enabled) a.Enable();
        }
    }

    private void OnDisable()
    {
        InputAction a = fireAction.action;
        if (a != null)
            a.performed -= OnFirePerformed;
    }

    private void OnFirePerformed(InputAction.CallbackContext ctx) => Fire();

    // Da collegare al pulsante "Sonda" del pannello.
    public void ToggleProbe() => SetProbe(!_active);

    public void SetProbe(bool on)
    {
        _active = on;
        if (reticle != null) reticle.gameObject.SetActive(on);
        if (!on)
        {
            _hasAim = false;
            _hasCapturedAim = false;
            _aimColorizer = null;
            SetText(liveLabel, "");
        }
    }

    private void Update()
    {
        // Il colorizer non e' piu' un prerequisito: con piu' borghi quello buono e' quello
        // che si colpisce, e si scopre solo dopo il raycast.
        if (!_active || aimSource == null) return;
        UpdateAim();
    }

    private void UpdateAim()
    {
        _hasAim = false;
        Ray ray = new Ray(aimSource.position, aimSource.forward);

        if (Physics.Raycast(ray, out RaycastHit hit, maxDistance))
        {
            // Si accetta il collider che porta UN colorizer, qualunque borgo sia
            // (non un figlio: GetComponent, non GetComponentInParent).
            TriangleTemperatureColorizer c = hit.collider.GetComponent<TriangleTemperatureColorizer>();
            if (c != null)
            {
                if (reticle != null)
                {
                    reticle.gameObject.SetActive(true);
                    reticle.position = hit.point;
                    if (alignReticleToSurface && hit.normal.sqrMagnitude > 0f)
                        reticle.rotation = Quaternion.LookRotation(hit.normal);
                }

                if (hit.triangleIndex >= 0 &&
                    c.TryGetTriangleDebugInfo(hit.triangleIndex, out TriangleDebugInfo info))
                {
                    // Punto valido: aggiorna lo stato corrente E l'ultimo punto catturato.
                    _aimPoint = hit.point;
                    _aimNormal = hit.normal;
                    _aimTriangle = hit.triangleIndex;
                    _aimTemp = info.temperature;
                    _aimColorizer = c;
                    _hasAim = true;
                    _hasCapturedAim = true;
                    SetText(liveLabel, $"{_aimTemp:0.0} °C");
                }
            }
        }

        if (!_hasAim)
        {
            if (reticle != null) reticle.gameObject.SetActive(false);
            SetText(liveLabel, "—");
        }
    }

    /// <summary>"Spara": congela nel pannello la temperatura del punto mirato e piazza il marcatore.
    /// Collegalo a un pulsante "Spara" o al trigger di un controller.</summary>
    public void Fire()
    {
        // Usa l'ULTIMO punto valido mirato, non quello del frame corrente: col pulsante UI
        // il controller potrebbe non essere piu' sul modello nell'istante del click.
        if (!_active || !_hasCapturedAim)
        {
            SetText(resultLabel, "Aim at the model and try again");
            return;
        }

        SetText(resultLabel, $"{_aimTemp:0.0} °C");

        TriangleTemperatureColorizer target = _aimColorizer != null ? _aimColorizer : colorizer;

        // Marker CONDIVISO (uno per utente): si invia il punto in coordinate LOCALI,
        // cosi' resta ancorato correttamente su tutti i client (regge la colocation).
        if (markerNetwork != null)
        {
            Transform space = markerSpace != null
                ? markerSpace
                : (target != null ? target.transform : null);

            if (space != null)
            {
                Vector3 local = space.InverseTransformPoint(_aimPoint);
                Vector3 localN = space.InverseTransformDirection(_aimNormal).normalized;
                markerNetwork.RequestPlaceMarker(local, localN, _aimTemp);
                return;   // il marker (anche il proprio) lo disegna il renderer di rete
            }
        }

        // Fallback single-player: marcatore locale.
        if (shotMarker != null)
        {
            shotMarker.gameObject.SetActive(true);
            // Aggancia il marcatore al modello colpito, cosi' resta sul punto anche se
            // il modello si sposta - e sparisce insieme a lui quando si cambia borgo.
            if (target != null) shotMarker.SetParent(target.transform, true);
            shotMarker.position = _aimPoint;
        }
    }

    private static void SetText(TMP_Text t, string s) { if (t != null) t.text = s; }
}

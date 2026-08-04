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
/// NOTA: per ottenere il triangolo colpito il MeshCollider del modello deve essere NON convex
/// (hit.triangleIndex e' valido solo sui mesh collider non convessi).
/// </summary>
public class ThermalProbeVR : MonoBehaviour
{
    [Header("Riferimenti")]
    [Tooltip("Origine del raggio di mira: la Main Camera (sguardo) o un controller (puntatore).")]
    public Transform aimSource;
    public TriangleTemperatureColorizer colorizer;
    [Tooltip("Crocetta di mira: spostata sul punto colpito mentre miri.")]
    public Transform reticle;
    [Tooltip("Marcatore opzionale lasciato nell'ultimo punto 'sparato'.")]
    public Transform shotMarker;

    [Tooltip("Se assegnato, lo sparo crea un marker CONDIVISO (uno per utente, colorato). " +
             "In tal caso il marcatore locale sopra viene ignorato.")]
    public ProbeMarkerNetwork markerNetwork;

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

    private const int IgnoreRaycastLayer = 2;   // layer predefinito di Unity escluso dai raycast

    public bool IsActive => _active;

    private void Awake()
    {
        // Crocetta e marcatore NON devono intercettare il raggio della sonda:
        // sono figli del modello e, se colpiti, falserebbero mira e posizione.
        // Mettendoli su "Ignore Raycast" il Physics.Raycast di default li ignora.
        SetLayerRecursive(reticle, IgnoreRaycastLayer);
        SetLayerRecursive(shotMarker, IgnoreRaycastLayer);
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
            SetText(liveLabel, "");
        }
    }

    private void Update()
    {
        if (!_active || aimSource == null || colorizer == null) return;
        UpdateAim();
    }

    private void UpdateAim()
    {
        _hasAim = false;
        Ray ray = new Ray(aimSource.position, aimSource.forward);

        if (Physics.Raycast(ray, out RaycastHit hit, maxDistance))
        {
            // Accetta solo il collider che porta IL colorizer (non un figlio: GetComponent, non InParent).
            TriangleTemperatureColorizer c = hit.collider.GetComponent<TriangleTemperatureColorizer>();
            if (c == colorizer)
            {
                if (reticle != null)
                {
                    reticle.gameObject.SetActive(true);
                    reticle.position = hit.point;
                    if (alignReticleToSurface && hit.normal.sqrMagnitude > 0f)
                        reticle.rotation = Quaternion.LookRotation(hit.normal);
                }

                if (hit.triangleIndex >= 0 &&
                    colorizer.TryGetTriangleDebugInfo(hit.triangleIndex, out TriangleDebugInfo info))
                {
                    // Punto valido: aggiorna lo stato corrente E l'ultimo punto catturato.
                    _aimPoint = hit.point;
                    _aimNormal = hit.normal;
                    _aimTriangle = hit.triangleIndex;
                    _aimTemp = info.temperature;
                    _hasAim = true;
                    _hasCapturedAim = true;
                    SetText(liveLabel, $"{_aimTemp:0.0} \u00B0C");
                }
            }
        }

        if (!_hasAim)
        {
            if (reticle != null) reticle.gameObject.SetActive(false);
            SetText(liveLabel, "\u2014");
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

        SetText(resultLabel, $"{_aimTemp:0.0} \u00B0C");

        // Marker CONDIVISO (uno per utente): invia il punto in coordinate LOCALI del modello,
        // cosi' resta ancorato correttamente su tutti i client (regge la colocation).
        if (markerNetwork != null && colorizer != null)
        {
            Vector3 local = colorizer.transform.InverseTransformPoint(_aimPoint);
            Vector3 localN = colorizer.transform.InverseTransformDirection(_aimNormal).normalized;
            markerNetwork.RequestPlaceMarker(local, localN, _aimTemp);
            return;   // il marker (anche il proprio) lo disegna il renderer di rete
        }

        // Fallback single-player: marcatore locale.
        if (shotMarker != null)
        {
            shotMarker.gameObject.SetActive(true);
            // Aggancia il marcatore al modello cosi' resta sul punto anche se il modello si sposta.
            if (colorizer != null) shotMarker.SetParent(colorizer.transform, true);
            shotMarker.position = _aimPoint;
        }
    }

    private static void SetText(TMP_Text t, string s) { if (t != null) t.text = s; }
}

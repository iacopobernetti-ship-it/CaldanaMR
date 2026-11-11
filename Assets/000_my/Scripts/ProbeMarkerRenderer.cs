using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Disegna i marker condivisi della sonda leggendo ProbeMarkerNetwork: un marker per utente,
/// colorato in base al clientId, ancorato al modello (coordinate locali) con una piccola label
/// che mostra la temperatura. Le label fanno billboard verso la camera locale.
///
/// Il prefab del marker deve avere: un Renderer (per il colore) e un TMP_Text figlio (per il
/// valore). Viene istanziato come figlio di modelRoot, cosi' segue il modello con la colocation.
/// </summary>
public class ProbeMarkerRenderer : MonoBehaviour
{
    [Header("Riferimenti")]
    public ProbeMarkerNetwork markerNetwork;
    [Tooltip("Radice del modello (Caldana3D): i marker diventano suoi figli.")]
    public Transform modelRoot;
    [Tooltip("Prefab del marker: Renderer + TMP_Text figlio.")]
    public GameObject markerPrefab;

    [Header("Aspetto")]
    [Tooltip("Colori per utente (indicizzati per clientId).")]
    public Color[] palette = new Color[]
    {
        new Color(0.20f, 0.55f, 0.95f), // blu
        new Color(0.95f, 0.35f, 0.25f), // rosso
        new Color(0.30f, 0.80f, 0.40f), // verde
        new Color(0.95f, 0.75f, 0.20f), // giallo
        new Color(0.70f, 0.40f, 0.90f), // viola
        new Color(0.20f, 0.80f, 0.80f), // ciano
    };
    [Tooltip("Colora anche il testo della label col colore utente.")]
    public bool tintLabel = true;

    [Header("Orientamento")]
    [Tooltip("Allinea il marker alla normale della superficie (come il reticle), parallelo alla mesh.")]
    public bool alignToSurface = true;
    [Tooltip("Stacco del marker dalla superficie lungo la normale, in METRI di mondo. " +
             "Viene convertito nello spazio di modelRoot, quindi non cambia se cambi " +
             "il contenitore di riferimento.")]
    public float surfaceOffset = 0.05f;
    [Tooltip("Se alignToSurface e' OFF, la label fa billboard verso questa camera (vuoto = Camera.main).")]
    public Camera billboardCamera;

    private readonly List<GameObject> _pool = new();

    /// <summary>
    /// Fattore che riporta i marker alla dimensione progettata nel prefab, qualunque sia
    /// la scala di modelRoot.
    ///
    /// I marker nascono figli di modelRoot e ne ereditano la scala. Finche' modelRoot era
    /// il modello del borgo (scala ~0.0035) il prefab andava disegnato circa trecento volte
    /// piu' grande del vero; spostando modelRoot sul contenitore comune, che ha scala 1,
    /// gli stessi marker diventavano trecento volte troppo grandi - una sfera cosi' grande
    /// da contenere la camera, quindi invisibile, che sembra un marker non istanziato.
    /// Compensando qui, la dimensione del prefab e' sempre quella che si vede, e cambiare
    /// il riferimento non obbliga piu' a ritarare nulla.
    /// </summary>
    private float RootScaleCompensation
    {
        get
        {
            if (modelRoot == null) return 1f;
            float s = modelRoot.lossyScale.x;   // si assume scala uniforme, come in scena
            return Mathf.Approximately(s, 0f) ? 1f : 1f / s;
        }
    }

    private void OnEnable()
    {
        if (markerNetwork != null)
        {
            markerNetwork.OnMarkersChanged += Rebuild;
            Rebuild();
        }
    }

    private void OnDisable()
    {
        if (markerNetwork != null) markerNetwork.OnMarkersChanged -= Rebuild;
    }

    private void Rebuild()
    {
        if (markerNetwork == null || markerPrefab == null || modelRoot == null) return;

        int n = markerNetwork.Count;
        float comp = RootScaleCompensation;
        Vector3 prefabScale = markerPrefab.transform.localScale;

        // Espandi il pool se servono piu' marker.
        while (_pool.Count < n)
            _pool.Add(Instantiate(markerPrefab, modelRoot));

        for (int i = 0; i < _pool.Count; i++)
        {
            bool used = i < n;
            if (_pool[i] == null) continue;
            _pool[i].SetActive(used);
            if (!used) continue;

            ProbeMarker m = markerNetwork.Get(i);
            Vector3 pos = new Vector3(m.x, m.y, m.z);
            Vector3 nrm = new Vector3(m.nx, m.ny, m.nz);

            Transform tr = _pool[i].transform;
            tr.localScale = prefabScale * comp;

            if (alignToSurface && nrm.sqrMagnitude > 1e-6f)
            {
                nrm.Normalize();
                // Stacca dalla superficie lungo la normale. Orienta il marker in modo che il
                // testo (fronte TMP = +Z) sia leggibile guardando da FUORI: +Z verso l'interno
                // (-normale), cosi' non appare specchiato.
                // Lo stacco e' espresso in metri di mondo e convertito qui nello spazio
                // di modelRoot, cosi' resta lo stesso distacco visibile su entrambi i
                // borghi, che hanno scale diverse.
                tr.localPosition = pos + nrm * (surfaceOffset * comp);
                tr.localRotation = Quaternion.LookRotation(-nrm);
            }
            else
            {
                tr.localPosition = pos;
                tr.localRotation = Quaternion.identity;
            }

            Color c = ColorFor(m.clientId);
            ApplyColor(_pool[i], c);
            SetLabel(_pool[i], $"{m.tempC:0.0} \u00B0C", c);
        }
    }

    private Color ColorFor(ulong clientId)
    {
        if (palette == null || palette.Length == 0) return Color.red;
        return palette[(int)(clientId % (ulong)palette.Length)];
    }

    private void ApplyColor(GameObject go, Color c)
    {
        Renderer r = go.GetComponentInChildren<Renderer>();
        if (r == null) return;
        Material mat = r.material;   // istanza per-marker
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);  // URP/HDRP
        else mat.color = c;                                                // Built-in
    }

    private void SetLabel(GameObject go, string s, Color c)
    {
        TMP_Text t = go.GetComponentInChildren<TMP_Text>();
        if (t == null) return;
        t.text = s;
        if (tintLabel) t.color = c;
    }

    private void LateUpdate()
    {
        if (alignToSurface) return;   // orientamento gestito in Rebuild (parallelo alla mesh)

        Camera cam = billboardCamera != null ? billboardCamera : Camera.main;
        if (cam == null) return;

        for (int i = 0; i < _pool.Count; i++)
        {
            if (_pool[i] == null || !_pool[i].activeSelf) continue;
            TMP_Text t = _pool[i].GetComponentInChildren<TMP_Text>();
            if (t == null) continue;
            Vector3 dir = t.transform.position - cam.transform.position;
            if (dir.sqrMagnitude > 1e-6f)
                t.transform.rotation = Quaternion.LookRotation(dir);
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Mostra un Sole per OGNI ora selezionata nella simulazione: clona un Sole "prototipo"
/// (SunPositioner) tante volte quante sono le ore e posiziona ciascun clone alla posizione
/// solare della sua ora. La simulazione termica aggrega le ore a massimo per faccia; qui
/// invece i soli restano distinti lungo l'arco, in modo "informativo".
///
/// Legge data e ore dai NetworkVariable del ThermalSimNetworkController (gia' sincronizzati),
/// quindi ogni client calcola localmente le stesse posizioni: nessun networking aggiuntivo.
///
/// I cloni sono figli dello stesso genitore del prototipo (il modello), cosi' seguono
/// spostamenti/rotazioni/scala del modello (colocation) usando la posizione LOCALE.
/// </summary>
public class SunSimBridge : MonoBehaviour
{
    [Header("Riferimenti")]
    public ThermalSimNetworkController controller;
    [Tooltip("Sole 'prototipo': viene clonato una volta per ogni ora selezionata. " +
             "Solo il prototipo guida l'eventuale luce direzionale; i cloni no.")]
    public SunPositioner sun;

    [Header("Soli multipli")]
    [Tooltip("Numero massimo di soli istanziabili (limite di sicurezza).")]
    public int maxSuns = 24;
    [Tooltip("Ora usata se nessuna e' selezionata (0-23).")]
    [Range(0, 23)] public int fallbackHour = 12;

    [Header("Fuso orario")]
    [Tooltip("Se attivo, calcola da solo l'ora legale dell'Europa centrale (CET/CEST).")]
    public bool autoEuropeanDst = true;
    [Tooltip("Offset standard dall'UTC in ore, senza DST. Europa centrale = 1.")]
    public double standardUtcOffset = 1.0;
    [Tooltip("Se attivo, ignora i due campi sopra e usa un offset fisso.")]
    public bool manualOffset = false;
    public double manualUtcOffsetHours = 2.0;

    // Pool: _suns[0] e' il prototipo; gli altri sono cloni riutilizzati.
    private readonly List<SunPositioner> _suns = new();

    private void OnEnable()
    {
        if (controller != null) controller.OnStateChanged += Refresh;
        Refresh();
    }

    private void OnDisable()
    {
        if (controller != null) controller.OnStateChanged -= Refresh;
    }

    /// <summary>Rilegge data/ore dal controller e riposiziona un Sole per ora.</summary>
    public void Refresh()
    {
        if (controller == null || sun == null) return;

        int year = controller.Year;
        int dayOfYear = controller.DayOfYear;
        double offset = ResolveOffset(year, dayOfYear);

        List<int> hours = ParseHours(controller.HoursCsv);
        if (hours.Count == 0) hours.Add(Mathf.Clamp(fallbackHour, 0, 23));
        if (hours.Count > maxSuns) hours.RemoveRange(maxSuns, hours.Count - maxSuns);

        EnsurePool(hours.Count);

        // Attiva e posiziona un Sole per ogni ora.
        for (int i = 0; i < _suns.Count; i++)
        {
            bool used = i < hours.Count;
            if (_suns[i] == null) continue;
            _suns[i].gameObject.SetActive(used);
            if (used) _suns[i].SetDateTime(year, dayOfYear, hours[i], offset);
        }
    }

    private void EnsurePool(int count)
    {
        if (_suns.Count == 0) _suns.Add(sun);   // element 0 = prototipo

        int target = Mathf.Min(count, Mathf.Max(1, maxSuns));
        while (_suns.Count < target)
        {
            GameObject go = Instantiate(sun.gameObject, sun.transform.parent);
            go.name = "Sun_Clone_" + _suns.Count;
            SunPositioner sp = go.GetComponent<SunPositioner>();
            if (sp != null)
            {
                sp.directionalLight = null;      // solo il prototipo guida la luce di scena
                sp.sunTransform = go.transform;  // ogni clone posiziona SE STESSO
                sp.modelRoot = sun.modelRoot;    // stesso frame di riferimento del prototipo
                _suns.Add(sp);
            }
            else
            {
                Destroy(go);
                break;   // prototipo senza SunPositioner: evita loop
            }
        }
    }

    // Ore uniche e ordinate dal CSV sincronizzato (es. "8,12,15").
    private static List<int> ParseHours(string csv)
    {
        var set = new HashSet<int>();
        if (!string.IsNullOrEmpty(csv))
            foreach (string p in csv.Split(','))
                if (int.TryParse(p.Trim(), out int v))
                    set.Add(Mathf.Clamp(v, 0, 23));

        var list = new List<int>(set);
        list.Sort();
        return list;
    }

    private double ResolveOffset(int year, int dayOfYear)
    {
        if (manualOffset) return manualUtcOffsetHours;
        if (autoEuropeanDst && IsCentralEuropeDst(year, dayOfYear))
            return standardUtcOffset + 1.0;
        return standardUtcOffset;
    }

    // Ora legale Europa centrale: dall'ultima domenica di marzo all'ultima di ottobre.
    private static bool IsCentralEuropeDst(int year, int dayOfYear)
    {
        int marLast = new System.DateTime(year, 3, LastSunday(year, 3)).DayOfYear;
        int octLast = new System.DateTime(year, 10, LastSunday(year, 10)).DayOfYear;
        return dayOfYear >= marLast && dayOfYear < octLast;
    }

    private static int LastSunday(int year, int month)
    {
        int daysInMonth = System.DateTime.DaysInMonth(year, month);
        for (int d = daysInMonth; d >= 1; d--)
            if (new System.DateTime(year, month, d).DayOfWeek == System.DayOfWeek.Sunday)
                return d;
        return daysInMonth;
    }

#if UNITY_EDITOR
    private void OnValidate() { if (isActiveAndEnabled && Application.isPlaying) Refresh(); }
#endif
}

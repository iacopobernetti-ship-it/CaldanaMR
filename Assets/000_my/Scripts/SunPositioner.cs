using System;
using UnityEngine;

/// <summary>
/// Posiziona un oggetto "Sole" nella scena in base alla posizione solare reale
/// (azimut/elevazione) calcolata da latitudine, longitudine, data e ora.
/// Algoritmo: NOAA Solar Position (accuratezza ~frazione di grado).
///
/// CONVENZIONE DEL MODELLO 3D: asse +Z = Nord, +Y = Alto, +X = Est (left-handed Unity).
/// Il calcolo viene fatto nel sistema LOCALE di 'modelRoot': se sposti o ruoti il modello
/// sul tavolo, il Sole lo segue mantenendo la corretta geografia.
///
/// DETERMINISTICO: dando a tutti i client la stessa data/ora/posizione, ognuno calcola
/// localmente la stessa posizione del Sole. Nessun networking necessario: basta alimentarlo
/// con i valori gia' sincronizzati dal ThermalSimNetworkController.
/// </summary>
public class SunPositioner : MonoBehaviour
{
    [Header("Posizione geografica")]
    [Tooltip("Latitudine in gradi (+ = Nord).")]
    public double latitude = 43.30;
    [Tooltip("Longitudine in gradi (+ = Est).")]
    public double longitude = 11.75;
    [Tooltip("Quota in metri. NON usata nel calcolo: l'effetto sulla direzione apparente del " +
             "Sole e' trascurabile (sub-arcminuto). Influisce solo sull'orario esatto di alba/tramonto.")]
    public double altitudeMeters = 250;

    [Header("Data e ora (locali)")]
    public int year = 2026;
    public int month = 6;
    public int day = 21;
    [Tooltip("Ora locale in formato decimale (es. 14.5 = 14:30).")]
    public double localHour = 12.0;
    [Tooltip("Offset del fuso dall'UTC in ore, DST incluso (Italia: 1 in inverno, 2 in estate).")]
    public double utcOffsetHours = 2.0;

    [Header("Scena")]
    [Tooltip("Transform del modello: il suo +Z e' Nord, +Y Alto. Se nullo usa il padre di questo oggetto.")]
    public Transform modelRoot;
    [Tooltip("L'oggetto Sole da posizionare. Se nullo usa questo transform.")]
    public Transform sunTransform;
    [Tooltip("Distanza del Sole dal centro del modello, in unita' LOCALI del modello " +
             "(scala con esso). Indicativamente 1-2 volte il raggio del modello.")]
    public float distance = 10f;
    [Tooltip("Luce direzionale opzionale, orientata come i raggi solari.")]
    public Light directionalLight;
    [Tooltip("Applica la rifrazione atmosferica (posizione 'apparente'). Rilevante solo vicino all'orizzonte.")]
    public bool applyRefraction = true;

    [Header("Allineamento Nord del modello")]
    [Tooltip("Offset in gradi se il Nord del modello NON coincide con +Z locale (capita con " +
             "l'import OBJ che rimappa gli assi). Ruota la direzione del Sole nel piano orizzontale. " +
             "Nord/Sud invertito -> 180. Regolalo finche' il Sole non cade sul versante corretto.")]
    public float northOffsetDeg = 0f;

    [Header("Output (sola lettura)")]
    public float azimuthDeg;    // da Nord, senso orario verso Est
    public float elevationDeg;  // sopra l'orizzonte (negativa = Sole sotto l'orizzonte)

    /// <summary>Ricalcola e riposiziona il Sole con i valori correnti dei campi.</summary>
    public void UpdateSun()
    {
        SolarPosition(latitude, longitude, year, month, day, localHour, utcOffsetHours,
                      applyRefraction, out double az, out double elev);
        azimuthDeg = (float)az;       // azimut astronomico reale (sola lettura)
        elevationDeg = (float)elev;

        // Direzione (dal modello verso il Sole) nel frame LOCALE: X=Est, Y=Alto, Z=Nord.
        // Si aggiunge northOffsetDeg per allineare il Nord reale al +Z del modello importato.
        double azr = (az + northOffsetDeg) * Math.PI / 180.0;
        double elr = elev * Math.PI / 180.0;
        double ce = Math.Cos(elr);
        Vector3 dirLocal = new Vector3(
            (float)(ce * Math.Sin(azr)),   // Est  (+X)
            (float)(Math.Sin(elr)),        // Alto (+Y)
            (float)(ce * Math.Cos(azr))    // Nord (+Z)
        );
        if (dirLocal.sqrMagnitude > 1e-12f) dirLocal.Normalize();

        Transform root = modelRoot != null ? modelRoot
                        : (transform.parent != null ? transform.parent : transform);
        Transform sun = sunTransform != null ? sunTransform : transform;

        Vector3 offsetLocal = dirLocal * distance;

        if (sun.parent == root)
        {
            // Caso consigliato: il Sole e' FIGLIO del modello -> posizione LOCALE.
            // Cosi' segue sempre il modello anche quando il networking / la colocation lo
            // spostano, ruotano o scalano alla connessione: nessuna dipendenza dalla posa
            // mondo nel momento esatto del calcolo.
            sun.localPosition = offsetLocal;
        }
        else
        {
            // Fallback: il Sole non e' figlio del modello -> calcola la posizione mondo.
            // (Meno robusto se il modello viene riposizionato dopo questo calcolo.)
            sun.position = root.TransformPoint(offsetLocal);
        }

        // Direzione dei raggi nel mondo (tiene conto di rotazione/scala del modello).
        Vector3 dirWorld = root.TransformDirection(dirLocal).normalized;
        if (directionalLight != null)
            directionalLight.transform.rotation = Quaternion.LookRotation(-dirWorld, Vector3.up);
    }

    /// <summary>Comodita': imposta data da (anno, giorno-dell'anno) e ora, poi aggiorna.</summary>
    public void SetDateTime(int yearValue, int dayOfYear, double hourLocal, double utcOffset)
    {
        DayOfYearToMonthDay(yearValue, dayOfYear, out int m, out int d);
        year = yearValue; month = m; day = d;
        localHour = hourLocal; utcOffsetHours = utcOffset;
        UpdateSun();
    }

    private void OnEnable() => UpdateSun();
#if UNITY_EDITOR
    private void OnValidate() { if (isActiveAndEnabled) UpdateSun(); }
#endif

    // =====================================================================
    //  Algoritmo NOAA: azimut (0=N, 90=E, 180=S, 270=O) ed elevazione.
    // =====================================================================
    public static void SolarPosition(double latDeg, double lonDeg, int year, int month, int day,
                                     double localHour, double tzOffsetHours, bool refraction,
                                     out double azimuthDeg, out double elevationDeg)
    {
        // Giorno giuliano per l'istante in UT.
        double utHour = localHour - tzOffsetHours;
        double jd = JulianDay(year, month, day + utHour / 24.0);
        double T = (jd - 2451545.0) / 36525.0;

        double L0 = Mod(280.46646 + T * (36000.76983 + 0.0003032 * T), 360.0);   // long. media
        double M = 357.52911 + T * (35999.05029 - 0.0001537 * T);                // anomalia media
        double e = 0.016708634 - T * (0.000042037 + 0.0000001267 * T);           // eccentricita'
        double Mr = D2R(M);
        double C = Math.Sin(Mr) * (1.914602 - T * (0.004817 + 0.000014 * T))
                 + Math.Sin(2 * Mr) * (0.019993 - 0.000101 * T)
                 + Math.Sin(3 * Mr) * 0.000289;                                   // eq. del centro
        double trueLong = L0 + C;
        double omega = 125.04 - 1934.136 * T;
        double lambda = trueLong - 0.00569 - 0.00478 * Math.Sin(D2R(omega));      // long. apparente
        double seconds = 21.448 - T * (46.8150 + T * (0.00059 - T * 0.001813));
        double eps0 = 23.0 + (26.0 + seconds / 60.0) / 60.0;                      // obliquita' media
        double eps = eps0 + 0.00256 * Math.Cos(D2R(omega));                       // + correzione
        double delta = R2D(Math.Asin(Math.Sin(D2R(eps)) * Math.Sin(D2R(lambda)))); // declinazione

        // Equazione del tempo (minuti)
        double y = Math.Tan(D2R(eps / 2.0)); y *= y;
        double L0r = D2R(L0);
        double eot = 4.0 * R2D(
            y * Math.Sin(2 * L0r)
            - 2 * e * Math.Sin(Mr)
            + 4 * e * y * Math.Sin(Mr) * Math.Cos(2 * L0r)
            - 0.5 * y * y * Math.Sin(4 * L0r)
            - 1.25 * e * e * Math.Sin(2 * Mr));

        // Tempo solare vero (minuti) -> angolo orario (gradi)
        double trueSolarMin = Mod(localHour * 60.0 + eot + 4.0 * lonDeg - 60.0 * tzOffsetHours, 1440.0);
        double ha = trueSolarMin / 4.0;
        ha = (ha < 0) ? ha + 180.0 : ha - 180.0;

        double latr = D2R(latDeg);
        double deltar = D2R(delta);
        double har = D2R(ha);

        double cosZen = Math.Sin(latr) * Math.Sin(deltar)
                      + Math.Cos(latr) * Math.Cos(deltar) * Math.Cos(har);
        cosZen = Clamp(cosZen, -1.0, 1.0);
        double zenith = R2D(Math.Acos(cosZen));
        double elev = 90.0 - zenith;

        // Azimut da Nord, senso orario
        double denom = Math.Cos(latr) * Math.Sin(D2R(zenith));
        double az;
        if (Math.Abs(denom) < 1e-9)
        {
            az = (latDeg > delta) ? 180.0 : 0.0; // Sole allo zenit/nadir: caso degenere
        }
        else
        {
            double arg = (Math.Sin(latr) * Math.Cos(D2R(zenith)) - Math.Sin(deltar)) / denom;
            arg = Clamp(arg, -1.0, 1.0);
            double a = R2D(Math.Acos(arg));
            az = (ha > 0) ? Mod(a + 180.0, 360.0) : Mod(540.0 - a, 360.0);
        }

        if (refraction) elev += AtmosphericRefraction(elev);

        azimuthDeg = az;
        elevationDeg = elev;
    }

    private static double JulianDay(int year, int month, double day)
    {
        if (month <= 2) { year -= 1; month += 12; }
        double A = Math.Floor(year / 100.0);
        double B = 2 - A + Math.Floor(A / 4.0);
        return Math.Floor(365.25 * (year + 4716)) + Math.Floor(30.6001 * (month + 1)) + day + B - 1524.5;
    }

    // Rifrazione atmosferica approssimata (gradi), modello NOAA.
    private static double AtmosphericRefraction(double elevDeg)
    {
        if (elevDeg > 85.0) return 0.0;
        double te = Math.Tan(D2R(elevDeg));
        double r;
        if (elevDeg > 5.0)
            r = 58.1 / te - 0.07 / (te * te * te) + 0.000086 / (te * te * te * te * te);
        else if (elevDeg > -0.575)
            r = 1735.0 + elevDeg * (-518.2 + elevDeg * (103.4 + elevDeg * (-12.79 + elevDeg * 0.711)));
        else
            r = -20.772 / te;
        return r / 3600.0; // arcsec -> gradi
    }

    private static void DayOfYearToMonthDay(int year, int dayOfYear, out int month, out int day)
    {
        bool leap = (year % 4 == 0 && year % 100 != 0) || (year % 400 == 0);
        int[] dim = { 31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
        dayOfYear = Math.Max(1, Math.Min(dayOfYear, leap ? 366 : 365));
        month = 0;
        while (dayOfYear > dim[month]) { dayOfYear -= dim[month]; month++; }
        month += 1;
        day = dayOfYear;
    }

    private static double D2R(double d) => d * Math.PI / 180.0;
    private static double R2D(double r) => r * 180.0 / Math.PI;
    private static double Mod(double a, double m) { double r = a % m; return r < 0 ? r + m : r; }
    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
}

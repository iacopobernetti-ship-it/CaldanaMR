using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Client per la "Future Climate Sensor API" (dati CMIP6), porta 8004.
/// Legge Tmin/Tmax giornaliere (variabili tasmin/tasmax) per uno scenario climatico
/// e una data, e le interpola in una temperatura oraria dell'aria con profilo coseno
/// (minimo alle 6, massimo alle 15). Le coordinate sono lette dal SunPositioner,
/// cosi' coincidono con il sito del modello solare.
///
/// L'endpoint restituisce UN valore per chiamata: si interroga due volte
/// (tasmin e tasmax). La conversione da Kelvin a gradi Celsius la fa gia' l'API
/// (campo value_converted, unit_converted = "C").
///
/// Periodo disponibile: 01/01/2015 - 31/12/2099. Fuori range, o data non valida,
/// l'API risponde con codice 4xx e un messaggio in inglese nel campo "detail",
/// che qui viene estratto e restituito come errore.
/// </summary>
public class FutureClimateClient : MonoBehaviour
{
    [Header("Endpoint")]
    public string baseUrl = "http://80.211.133.205:8004";
    public int timeoutSeconds = 15;

    [Header("Coordinate")]
    [Tooltip("Se assegnato, latitudine/longitudine vengono lette da qui (stesso sito del Sole).")]
    public SunPositioner sun;
    [Tooltip("Usati solo se 'sun' non e' assegnato.")]
    public double fallbackLatitude = 43.30;
    public double fallbackLongitude = 11.75;

    // Profilo orario: minimo (alba) alle 6, massimo alle 15.
    private const float HourTmin = 6f;
    private const float HourTmax = 15f;

    /// <summary>Esito di una lettura climatica.</summary>
    public class ClimateResult
    {
        public bool ok;
        public float tminC;
        public float tmaxC;
        public string error;   // messaggio in inglese se ok == false
    }

    [Serializable]
    private class ClimatePayload
    {
        public string variable;
        public string scenario;
        public double lon;
        public double lat;
        public string date;    // dd/mm/yyyy
    }

    [Serializable]
    private class ClimateResponseDto
    {
        public float value_converted;
        public string unit_converted;
        public string date_dataset;
    }

    [Serializable]
    private class ErrorDto
    {
        public string detail;
    }

    private double Lat => sun != null ? sun.latitude : fallbackLatitude;
    private double Lon => sun != null ? sun.longitude : fallbackLongitude;

    /// <summary>
    /// Interpola la temperatura oraria dell'aria da Tmin/Tmax con profilo coseno.
    /// Minimo alle 6, massimo alle 15; raffreddamento continuo (periodico) altrove.
    /// </summary>
    public static float HourlyTemperature(float tmin, float tmax, float h)
    {
        float amp = tmax - tmin;
        float x;

        if (h >= HourTmin && h <= HourTmax)
        {
            // 6..15  ->  x 0..1 : salita da Tmin a Tmax
            x = (h - HourTmin) / (HourTmax - HourTmin);                 // /9
            return tmin + amp * 0.5f * (1f - Mathf.Cos(Mathf.PI * x));
        }

        if (h > HourTmax)
        {
            // 15..(6 del giorno dopo) -> discesa da Tmax verso Tmin
            x = (h - HourTmax) / (24f - HourTmax + HourTmin);           // /15
            return tmax - amp * 0.5f * (1f - Mathf.Cos(Mathf.PI * x));
        }

        // h < 6 : continuazione della discesa dopo la mezzanotte
        x = (h + (24f - HourTmax)) / (24f - HourTmax + HourTmin);       // (h+9)/15
        return tmax - amp * 0.5f * (1f - Mathf.Cos(Mathf.PI * x));
    }

    /// <summary>
    /// Scarica Tmin e Tmax (in C) per scenario+data. Al termine richiama onDone col risultato.
    /// scenarioApiId: "ssp126" | "ssp245" | "ssp585".
    /// </summary>
    public IEnumerator FetchDayMinMax(string scenarioApiId, int year, int month, int day,
                                      Action<ClimateResult> onDone)
    {
        var result = new ClimateResult();

        float tmin = 0f, tmax = 0f;
        bool ok = false;
        string err = null;

        yield return FetchOne("tasmin", scenarioApiId, year, month, day,
                              (v, e) => { ok = e == null; tmin = v; err = e; });
        if (!ok)
        {
            result.ok = false;
            result.error = err ?? "Unknown error while reading minimum temperature.";
            onDone?.Invoke(result);
            yield break;
        }

        yield return FetchOne("tasmax", scenarioApiId, year, month, day,
                              (v, e) => { ok = e == null; tmax = v; err = e; });
        if (!ok)
        {
            result.ok = false;
            result.error = err ?? "Unknown error while reading maximum temperature.";
            onDone?.Invoke(result);
            yield break;
        }

        result.ok = true;
        result.tminC = tmin;
        result.tmaxC = tmax;
        onDone?.Invoke(result);
    }

    // Una singola lettura. onDone(valore, null) se ok; onDone(0, messaggio) se errore.
    private IEnumerator FetchOne(string variable, string scenario, int year, int month, int day,
                                 Action<float, string> onDone)
    {
        var payload = new ClimatePayload
        {
            variable = variable,
            scenario = scenario,
            lon = Lon,
            lat = Lat,
            date = string.Format(CultureInfo.InvariantCulture, "{0:00}/{1:00}/{2:0000}", day, month, year),
        };
        string json = JsonUtility.ToJson(payload);

        using (var req = new UnityWebRequest($"{baseUrl}/climate", "POST"))
        {
            byte[] body = System.Text.Encoding.UTF8.GetBytes(json);
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = Mathf.Max(1, timeoutSeconds);

            yield return req.SendWebRequest();

            string text = req.downloadHandler != null ? req.downloadHandler.text : null;

#if UNITY_2020_1_OR_NEWER
            bool netError = req.result == UnityWebRequest.Result.ConnectionError
                            || req.result == UnityWebRequest.Result.DataProcessingError;
            bool httpError = req.result == UnityWebRequest.Result.ProtocolError;
#else
            bool netError = req.isNetworkError;
            bool httpError = req.isHttpError;
#endif
            if (netError)
            {
                onDone?.Invoke(0f, "Network error contacting the climate API: " + req.error);
                yield break;
            }

            if (httpError)
            {
                // L'API restituisce {"detail": "...messaggio in inglese..."} con codice 4xx.
                string detail = ExtractDetail(text);
                onDone?.Invoke(0f, detail ?? ("Climate API returned HTTP " + req.responseCode + "."));
                yield break;
            }

            ClimateResponseDto dto = null;
            try { dto = JsonUtility.FromJson<ClimateResponseDto>(text); }
            catch { dto = null; }

            if (dto == null || string.IsNullOrEmpty(dto.unit_converted))
            {
                onDone?.Invoke(0f, "Unexpected response from the climate API.");
                yield break;
            }

            onDone?.Invoke(dto.value_converted, null);
        }
    }

    private static string ExtractDetail(string body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            ErrorDto e = JsonUtility.FromJson<ErrorDto>(body);
            if (e != null && !string.IsNullOrEmpty(e.detail)) return e.detail;
        }
        catch { /* corpo non JSON: ignora */ }
        return null;
    }

    // ==================================================================
    //  Indici "window" heritage / tourism (per il 4o pannello)
    // ==================================================================

    /// <summary>Singolo indice: nome leggibile, valore numerico (se presente) e testo formattato.</summary>
    public class IndexEntry
    {
        public string name;
        public float value;
        public bool hasValue;
        public string valueText;
    }

    /// <summary>Esito di una richiesta di indici su finestra, gia' formattato per la UI (in inglese).</summary>
    public class WindowResult
    {
        public bool ok;
        public string error;        // messaggio in inglese se ok == false
        public string headerLine;   // titolo: "Heritage - ssp585 - urban_cultural"
        public string body;         // testo multilinea (fallback non colorato)
        public string periodText;   // righe "Window: ... / Reference date: ..."
        public List<IndexEntry> indices = new List<IndexEntry>();
        public List<string> notes = new List<string>();
    }

    /// <summary>Indici heritage su finestra centrata/indietro/avanti attorno alla data.</summary>
    public IEnumerator FetchHeritageWindow(string scenarioApiId, int year, int month, int day,
                                           int windowDays, string windowMode, Action<WindowResult> onDone)
    {
        string json = "{"
            + Q("scenario") + ":" + Q(scenarioApiId) + ","
            + Q("lon") + ":" + Num(Lon) + ","
            + Q("lat") + ":" + Num(Lat) + ","
            + Q("date") + ":" + Q(FormatDate(year, month, day)) + ","
            + Q("window_days") + ":" + windowDays + ","
            + Q("window_mode") + ":" + Q(windowMode) + ","
            + Q("include_daily_series") + ":false}";
        yield return FetchWindow("/heritage/window", json, "Heritage", onDone);
    }

    /// <summary>Indici tourism su finestra, con profilo turistico.</summary>
    public IEnumerator FetchTourismWindow(string scenarioApiId, int year, int month, int day,
                                          int windowDays, string windowMode, string tourismProfile,
                                          Action<WindowResult> onDone)
    {
        string json = "{"
            + Q("scenario") + ":" + Q(scenarioApiId) + ","
            + Q("lon") + ":" + Num(Lon) + ","
            + Q("lat") + ":" + Num(Lat) + ","
            + Q("date") + ":" + Q(FormatDate(year, month, day)) + ","
            + Q("window_days") + ":" + windowDays + ","
            + Q("window_mode") + ":" + Q(windowMode) + ","
            + Q("tourism_profile") + ":" + Q(tourismProfile) + ","
            + Q("include_daily_series") + ":false}";
        yield return FetchWindow("/tourism/window", json, "Tourism", onDone);
    }

    private IEnumerator FetchWindow(string endpoint, string json, string title, Action<WindowResult> onDone)
    {
        var result = new WindowResult();

        using (var req = new UnityWebRequest($"{baseUrl}{endpoint}", "POST"))
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = Mathf.Max(1, timeoutSeconds);

            yield return req.SendWebRequest();

            string text = req.downloadHandler != null ? req.downloadHandler.text : null;

#if UNITY_2020_1_OR_NEWER
            bool netError = req.result == UnityWebRequest.Result.ConnectionError
                            || req.result == UnityWebRequest.Result.DataProcessingError;
            bool httpError = req.result == UnityWebRequest.Result.ProtocolError;
#else
            bool netError = req.isNetworkError;
            bool httpError = req.isHttpError;
#endif
            if (netError)
            {
                result.ok = false;
                result.error = "Network error contacting the climate API: " + req.error;
                onDone?.Invoke(result);
                yield break;
            }
            if (httpError)
            {
                result.ok = false;
                result.error = ExtractDetail(text) ?? ("Climate API returned HTTP " + req.responseCode + ".");
                onDone?.Invoke(result);
                yield break;
            }

            BuildWindowResult(text, title, result);
            onDone?.Invoke(result);
        }
    }

    private static void BuildWindowResult(string text, string title, WindowResult result)
    {
        object root;
        try { root = Json.Parse(text); }
        catch { root = null; }

        var obj = root as Dictionary<string, object>;
        if (obj == null)
        {
            result.ok = false;
            result.error = "Unexpected response from the climate API.";
            return;
        }

        string scenario = GetStr(obj, "scenario");
        string profile = GetStr(obj, "tourism_profile");   // assente per heritage

        var header = new StringBuilder(title);
        if (!string.IsNullOrEmpty(scenario)) header.Append("  -  ").Append(scenario);
        if (!string.IsNullOrEmpty(profile)) header.Append("  -  ").Append(profile);
        result.headerLine = header.ToString();

        var sb = new StringBuilder();

        var period = obj.TryGetValue("period", out var pv) ? pv as Dictionary<string, object> : null;
        if (period != null)
        {
            string wdays = GetStr(period, "window_days");
            string wmode = GetStr(period, "window_mode");
            string start = GetStr(period, "start");
            string end = GetStr(period, "end");
            string refDate = GetStr(period, "reference_date");
            var sbP = new StringBuilder();
            sbP.Append("Window: ").Append(wdays).Append(" days, ").Append(wmode)
               .Append("  (").Append(start).Append(" \u2192 ").Append(end).Append(')');
            if (!string.IsNullOrEmpty(refDate)) sbP.Append("\nReference date: ").Append(refDate);
            result.periodText = sbP.ToString();
            sb.Append(result.periodText).Append("\n\n");
        }

        var indices = obj.TryGetValue("indices", out var iv) ? iv as Dictionary<string, object> : null;
        if (indices != null && indices.Count > 0)
        {
            sb.Append("Indices\n");
            foreach (var kv in indices)
            {
                var e = new IndexEntry { name = Prettify(kv.Key), valueText = FormatVal(kv.Value) };
                if (kv.Value is double dv) { e.value = (float)dv; e.hasValue = true; }
                result.indices.Add(e);
                sb.Append("- ").Append(e.name).Append(": ").Append(e.valueText).Append('\n');
            }
        }
        else
        {
            sb.Append("No indices returned for this window.\n");
        }

        var notes = obj.TryGetValue("notes", out var nv) ? nv as List<object> : null;
        if (notes != null && notes.Count > 0)
        {
            sb.Append("\nNotes\n");
            foreach (var n in notes)
                if (n is string ns) { result.notes.Add(ns); sb.Append("- ").Append(ns).Append('\n'); }
        }

        result.ok = true;
        result.body = sb.ToString();
    }

    // --- Helper di formattazione richiesta/risposta ---
    private static string Q(string s) => "\"" + s + "\"";
    private static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);
    private static string FormatDate(int y, int m, int d) =>
        string.Format(CultureInfo.InvariantCulture, "{0:00}/{1:00}/{2:0000}", d, m, y);

    private static string GetStr(Dictionary<string, object> d, string key) =>
        (d != null && d.TryGetValue(key, out var v) && v is string s) ? s : null;

    private static string FormatVal(object v)
    {
        if (v == null) return "n/a";
        if (v is double d) return d.ToString("0.###", CultureInfo.InvariantCulture);
        if (v is bool b) return b ? "yes" : "no";
        return v.ToString();
    }

    private static string Prettify(string key) =>
        string.IsNullOrEmpty(key) ? key : key.Replace('_', ' ');

    // ==================================================================
    //  Mini-parser JSON di sola lettura (per il dizionario dinamico "indices")
    //  Ritorna: Dictionary<string,object> | List<object> | string | double | bool | null
    // ==================================================================
    private static class Json
    {
        public static object Parse(string text)
        {
            int i = 0;
            return ParseValue(text, ref i);
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't' || c == 'f') return ParseBool(s, ref i);
            if (c == 'n' || c == 'N') { SkipLiteral(s, ref i); return null; }   // null / NaN
            return ParseNumber(s, ref i);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++; // '{'
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                object val = ParseValue(s, ref i);
                d[key] = val;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; break; }
                break;
            }
            return d;
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // '['
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (i < s.Length)
            {
                list.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; break; }
                break;
            }
            return list;
        }

        private static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            if (i < s.Length && s[i] == '"') i++; // apertura
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\' && i < s.Length)
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 <= s.Length &&
                                ushort.TryParse(s.Substring(i, 4), NumberStyles.HexNumber,
                                                CultureInfo.InvariantCulture, out ushort code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static object ParseBool(string s, ref int i)
        {
            if (s[i] == 't') { SkipLiteral(s, ref i); return true; }
            SkipLiteral(s, ref i);
            return false;
        }

        private static void SkipLiteral(string s, ref int i)
        {
            while (i < s.Length && char.IsLetter(s[i])) i++;
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            string num = s.Substring(start, i - start);
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return d;
            return null;
        }
    }
}

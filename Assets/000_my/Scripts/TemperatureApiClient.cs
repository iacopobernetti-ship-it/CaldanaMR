using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

public class TemperatureApiClient : MonoBehaviour
{
    [Header("Endpoints")]
    public string baseUrl = "http://80.211.133.205:8006";

    [Header("Model")]
    public string userId = "testuser";
    public string modelId = "testmodel";

    [Header("Simulation")]
    public float meteoStationTemperatureMax = 35f;
    public int year = 2025;
    public int dayOfYear = 227;
    public int[] timesOfDay = new int[] { 8, 14 };
    public string timezoneStr = "Europe/Rome";
    public bool enableShadowRaycast = false;
    public float shadowBias = 0.01f;

    [Header("Clear Sky Preset - Borgo Rinascimentale")]
    [Tooltip("Typical clear-sky Linke turbidity for a fairly clean historic settlement atmosphere.")]
    public float linkeTurbidity = 3.5f;

    [Tooltip("Typical urban-historic albedo: stone, plaster, roof tiles, pavements.")]
    [Range(0f, 1f)]
    public float albedo = 0.22f;

    [Tooltip("Include ground-reflected radiation contribution.")]
    public bool useGroundReflected = true;

    [Tooltip("Minimum surface temperature offset below Tmax for weakly irradiated faces.")]
    public float surfaceTempOffsetC = 8.0f;

    [Header("Downloaded Data")]
    public FaceDebugResponseData faceDebugData;
    public SimulationResponseData responseData;

    public string FaceDebugUrl => $"{baseUrl}/models/{userId}/{modelId}/face-debug";
    public string SimulateUrl => $"{baseUrl}/simulate";

    public IEnumerator DownloadFaceDebug()
    {
        using (UnityWebRequest req = UnityWebRequest.Get(FaceDebugUrl))
        {
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Errore chiamata face-debug: " + req.error);
                Debug.LogError(req.downloadHandler.text);
                yield break;
            }

            faceDebugData = JsonUtility.FromJson<FaceDebugResponseData>(req.downloadHandler.text);

            if (faceDebugData == null || faceDebugData.face_debug == null || faceDebugData.face_debug.Length == 0)
            {
                Debug.LogError("TemperatureApiClient: faceDebugData is null or empty.");
                yield break;
            }

            Debug.Log($"Face debug downloaded. face_count={faceDebugData.face_count}");
        }
    }

    public IEnumerator RunSimulation()
    {
        string payload = BuildJsonPayload();

        using (UnityWebRequest req = new UnityWebRequest(SimulateUrl, "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(payload);
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Errore chiamata simulate: " + req.error);
                Debug.LogError(req.downloadHandler.text);
                yield break;
            }

            responseData = JsonUtility.FromJson<SimulationResponseData>(req.downloadHandler.text);

            if (responseData == null || responseData.face_temperatures == null || responseData.face_temperatures.temperatures == null)
            {
                Debug.LogError("TemperatureApiClient: responseData is null or invalid.");
                yield break;
            }

            Debug.Log($"Simulation downloaded. temp_count={responseData.face_temperatures.face_count}");
        }
    }

    private string BuildJsonPayload()
    {
        string times = "";
        for (int i = 0; i < timesOfDay.Length; i++)
        {
            times += timesOfDay[i].ToString();
            if (i < timesOfDay.Length - 1) times += ",";
        }

        return "{" +
            "\"user_id\":\"" + userId + "\"," +
            "\"model_id\":\"" + modelId + "\"," +
            "\"meteo_station_temperature_max\":" + meteoStationTemperatureMax.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
            "\"year\":" + year + "," +
            "\"day_of_year\":" + dayOfYear + "," +
            "\"times_of_day\":[" + times + "]," +
            "\"timezone_str\":\"" + timezoneStr + "\"," +
            "\"enable_shadow_raycast\":" + enableShadowRaycast.ToString().ToLower() + "," +
            "\"shadow_bias\":" + shadowBias.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
            "\"linke_turbidity\":" + linkeTurbidity.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
            "\"albedo\":" + albedo.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
            "\"use_ground_reflected\":" + useGroundReflected.ToString().ToLower() + "," +
            "\"surface_temp_offset_c\":" + surfaceTempOffsetC.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "}";
    }
}
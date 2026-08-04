using System.Collections;
using UnityEngine;

public class TemperatureBootstrap : MonoBehaviour
{
    public TemperatureApiClient apiClient;
    public TriangleTemperatureColorizer colorizer;

    IEnumerator Start()
    {
        yield return StartCoroutine(apiClient.DownloadFaceDebug());

        if (apiClient == null ||
            apiClient.faceDebugData == null ||
            apiClient.faceDebugData.face_debug == null ||
            apiClient.faceDebugData.face_debug.Length == 0)
        {
            Debug.LogError("Face debug download failed. Colors not applied.");
            yield break;
        }

        yield return StartCoroutine(apiClient.RunSimulation());

        if (apiClient.responseData == null ||
            apiClient.responseData.face_temperatures == null ||
            apiClient.responseData.face_temperatures.temperatures == null)
        {
            Debug.LogError("Simulation failed or returned no temperature data. Colors not applied.");
            yield break;
        }

        colorizer.ApplyColors();
    }
}
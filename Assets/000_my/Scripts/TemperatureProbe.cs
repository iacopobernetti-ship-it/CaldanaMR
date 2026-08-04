using UnityEngine;
using UnityEngine.InputSystem;

public class TemperatureProbe : MonoBehaviour
{
    public Camera sourceCamera;
    public TriangleTemperatureColorizer colorizer;
    public bool drawDebugRay = true;
    public float debugRayDuration = 3f;

    private void Update()
    {
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            ProbeTriangle();
        }
    }

    private void ProbeTriangle()
    {
        if (sourceCamera == null)
        {
            Debug.LogError("TemperatureProbe: sourceCamera is null.");
            return;
        }

        if (colorizer == null)
        {
            Debug.LogError("TemperatureProbe: colorizer is null.");
            return;
        }

        Vector2 mousePosition = Mouse.current.position.ReadValue();
        Ray ray = sourceCamera.ScreenPointToRay(mousePosition);

        if (drawDebugRay)
            Debug.DrawRay(ray.origin, ray.direction * 1000f, Color.magenta, debugRayDuration);

        if (!Physics.Raycast(ray, out RaycastHit hit, 1000f))
        {
            Debug.Log("TemperatureProbe: no hit.");
            return;
        }

        int tri = hit.triangleIndex;

        if (!colorizer.TryGetTriangleDebugInfo(tri, out TriangleDebugInfo info))
        {
            Debug.LogError($"TemperatureProbe: unable to read debug info for triangle {tri}.");
            return;
        }

        string msg =
            "=== TRIANGLE DEBUG ===\n" +
            $"Triangle Index: {info.triangleIndex}\n" +
            $"Temperature: {info.temperature:F6} C\n" +
            $"Hit Point: {hit.point}\n" +
            $"Centroid: {info.centroid}\n" +
            $"Area: {info.area:F9}\n" +
            $"Face Normal: {info.faceNormal}\n" +
            $"Avg Vertex Normal: {info.averagedVertexNormal}\n" +
            $"Vertex 0: {info.vertex0}\n" +
            $"Vertex 1: {info.vertex1}\n" +
            $"Vertex 2: {info.vertex2}\n" +
            $"Hit Object: {hit.collider.name}";

        Debug.Log(msg);

        Debug.DrawLine(info.vertex0, info.vertex1, Color.yellow, debugRayDuration);
        Debug.DrawLine(info.vertex1, info.vertex2, Color.yellow, debugRayDuration);
        Debug.DrawLine(info.vertex2, info.vertex0, Color.yellow, debugRayDuration);

        Debug.DrawRay(info.centroid, info.faceNormal * 0.5f, Color.cyan, debugRayDuration);

        if (info.averagedVertexNormal.sqrMagnitude > 0f)
            Debug.DrawRay(info.centroid, info.averagedVertexNormal * 0.5f, Color.green, debugRayDuration);
    }
}
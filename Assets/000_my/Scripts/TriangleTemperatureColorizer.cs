using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class TriangleTemperatureColorizer : MonoBehaviour
{
    public TemperatureApiClient apiClient;
    public bool useDynamicRangeFromResponse = true;
    public float minTemp = 15f;
    public float maxTemp = 45f;

    [Header("Centroid Mapping")]
    [Tooltip("Numero di decimali usati per la chiave finale.")]
    public int centroidKeyDecimals = 4;

    [Tooltip("Numero di decimali usati durante la ricerca della migliore trasformazione.")]
    public int normalizedKeyDecimals = 3;

    [Header("Color Mapping")]
    public bool useCustomGradient = false;
    public TemperaturePalette palettePreset = TemperaturePalette.ClassicThermal;
    public Gradient temperatureGradient;

    [Header("Percentile Stretch")]
    [Tooltip("Se attivo, il range colore viene calcolato sui percentili delle temperature mappate.")]
    public bool usePercentileStretch = true;

    [Tooltip("Percentile minimo per lo stretch colori.")]
    [Range(0f, 100f)]
    public float lowerPercentile = 5f;

    [Tooltip("Percentile massimo per lo stretch colori.")]
    [Range(0f, 100f)]
    public float upperPercentile = 95f;

    private float[] mappedTemperatures;

    // --- Cache per evitare il ricalcolo costoso ad ogni colorazione ---
    private int[] _triToBackendIndex;   // triangolo -> indice faccia backend (calcolato una volta)
    private Mesh _coloredMesh;          // mesh splittata per-faccia, costruita una volta
    private Color[] _colorBuffer;       // buffer colori riusato ad ogni ricolorazione
    private bool _meshBuilt;            // mesh + collider gia' costruiti

    // --- Stato build asincrona + notifica per la UI (cerchio rosso/verde, Wait/Ready) ---
    public event Action<bool> OnBusyChanged;  // true = calcolo in corso (Wait), false = pronto (Ready)
    public bool IsBusy => _building;
    private bool _building;
    private bool _recolorPending;

    private void Awake()
    {
        EnsureGradientInitialized();
    }

    private void OnValidate()
    {
        EnsureGradientInitialized();

        lowerPercentile = Mathf.Clamp(lowerPercentile, 0f, 100f);
        upperPercentile = Mathf.Clamp(upperPercentile, 0f, 100f);

        if (upperPercentile < lowerPercentile)
            upperPercentile = lowerPercentile;
    }

    // Punto d'ingresso pubblico (chiamato dal controller). Non blocca mai il main thread.
    public void ApplyColors()
    {
        if (apiClient == null ||
            apiClient.faceDebugData == null ||
            apiClient.faceDebugData.face_debug == null ||
            apiClient.responseData == null ||
            apiClient.responseData.face_temperatures == null)
        {
            Debug.LogError("ApplyColors: missing API data.");
            return;
        }

        // Mesh + mapping gia' pronti: ricolorazione immediata (millisecondi).
        if (_meshBuilt && _triToBackendIndex != null)
        {
            RecolorNow();
            return;
        }

        // Build in corso: ricorda che serve una ricolorazione finale e non avviarne un'altra.
        if (_building)
        {
            _recolorPending = true;
            return;
        }

        // Prima volta: calcolo pesante in background, niente freeze.
        _ = BuildThenColorAsync();
    }

    // Ricolorazione leggera e sincrona: usa la cache degli indici, nessuna API, nessun re-bake.
    private void RecolorNow()
    {
        if (!_meshBuilt || _coloredMesh == null) return;

        if (useDynamicRangeFromResponse && apiClient.responseData != null && apiClient.responseData.metadata != null)
        {
            minTemp = apiClient.responseData.metadata.temperature_min_c;
            maxTemp = apiClient.responseData.metadata.temperature_max_c;
        }

        MeshFilter mf = GetComponent<MeshFilter>();
        int triangleCount = _coloredMesh.triangles.Length / 3;

        mappedTemperatures = BuildMappedTemperatures(_coloredMesh);
        if (mappedTemperatures == null || mappedTemperatures.Length != triangleCount)
        {
            Debug.LogError($"RecolorNow: mappedTemperatures invalid. mapped={mappedTemperatures?.Length ?? 0}, triangles={triangleCount}");
            return;
        }

        float colorMin = minTemp;
        float colorMax = maxTemp;
        if (usePercentileStretch)
        {
            if (!TryComputePercentileRange(mappedTemperatures, lowerPercentile, upperPercentile, out colorMin, out colorMax))
            {
                colorMin = minTemp;
                colorMax = maxTemp;
            }
        }
        if (Mathf.Approximately(colorMin, colorMax))
            colorMax = colorMin + 0.0001f;

        for (int tri = 0; tri < triangleCount; tri++)
        {
            float t = mappedTemperatures[tri];
            if (!float.IsFinite(t)) t = colorMin;

            Color col = TemperatureToColor(t, colorMin, colorMax);
            int baseIdx = tri * 3;
            _colorBuffer[baseIdx]     = col;
            _colorBuffer[baseIdx + 1] = col;
            _colorBuffer[baseIdx + 2] = col;
        }

        _coloredMesh.colors = _colorBuffer;

        // Riaggancia il renderer alla nostra mesh, nel caso qualcosa l'avesse clonata.
        if (mf != null && mf.sharedMesh != _coloredMesh)
            mf.sharedMesh = _coloredMesh;
    }

    // Calcolo iniziale: legge la mesh sul main thread, fa il mapping pesante in background,
    // poi torna sul main thread per costruire mesh/collider e applicare i colori.
    private async Awaitable BuildThenColorAsync()
    {
        _building = true;
        OnBusyChanged?.Invoke(true);   // -> Wait / rosso

        try
        {
            // --- MAIN THREAD: leggi i dati della mesh in array semplici ---
            MeshFilter mf = GetComponent<MeshFilter>();
            MeshCollider mc = GetComponent<MeshCollider>();
            Mesh sourceMesh = mf.sharedMesh;
            if (sourceMesh == null)
            {
                Debug.LogError("BuildThenColorAsync: sourceMesh is null.");
                return;
            }

            FaceDebugData[] debugFaces = apiClient.faceDebugData.face_debug;
            float[] rawTemperatures = apiClient.responseData.face_temperatures.temperatures;
            if (debugFaces == null || rawTemperatures == null || debugFaces.Length != rawTemperatures.Length)
            {
                Debug.LogError("BuildThenColorAsync: backend data mismatch.");
                return;
            }

            int[] tris = sourceMesh.triangles;
            Vector3[] verts = sourceMesh.vertices;
            Vector3[] norms = sourceMesh.normals;
            int triangleCount = tris.Length / 3;
            Vector3[] unityCentroids = GetUnityTriangleCentroids(sourceMesh);

            // --- BACKGROUND THREAD: il calcolo pesante O(N^2). Il visore resta fluido. ---
            int[] indexMap = null;
            await Awaitable.BackgroundThreadAsync();
            try
            {
                indexMap = ComputeIndexMap(unityCentroids, tris, verts, norms, debugFaces, triangleCount);
            }
            finally
            {
                await Awaitable.MainThreadAsync();
            }

            // Se l'oggetto e' stato distrutto durante l'attesa, esci.
            if (this == null) return;

            if (indexMap == null)
            {
                Debug.LogError("BuildThenColorAsync: centroid mapping failed.");
                return;
            }

            // --- MAIN THREAD: finalizza (mesh splittata + bake collider, una volta sola) ---
            _triToBackendIndex = indexMap;
            BuildColoredMeshStructure(sourceMesh, tris, triangleCount, mf, mc);
            _meshBuilt = true;

            RecolorNow();
        }
        catch (System.Exception e)
        {
            Debug.LogError("BuildThenColorAsync error: " + e);
        }
        finally
        {
            _building = false;
            if (this != null)
            {
                OnBusyChanged?.Invoke(false);   // -> Ready / verde

                if (_recolorPending)
                {
                    _recolorPending = false;
                    RecolorNow();
                }
            }
        }
    }

    // Mapping geometrico triangolo->indice backend. Opera SOLO su array: thread-safe (no oggetti Unity).
    private int[] ComputeIndexMap(Vector3[] unityCentroids, int[] tris, Vector3[] verts, Vector3[] norms, FaceDebugData[] debugFaces, int triangleCount)
    {
        int[] indexMap = new int[triangleCount];
        for (int i = 0; i < triangleCount; i++) indexMap[i] = -1;

        Vector3[] backendCentroids = GetBackendCentroids(debugFaces);
        TransformCandidate best = FindBestTransformCandidate(unityCentroids, backendCentroids);

        Vector3 unityCenter = ComputeCenter(unityCentroids);
        Vector3 unityScale = ComputeScale(unityCentroids);
        Vector3 backendCenter = ComputeCenter(backendCentroids);
        Vector3 backendScale = ComputeScale(backendCentroids);

        BackendFaceCandidate[] backendCandidates = new BackendFaceCandidate[debugFaces.Length];
        Dictionary<string, List<int>> keyToBackendIndices = new Dictionary<string, List<int>>(debugFaces.Length);

        for (int i = 0; i < debugFaces.Length; i++)
        {
            Vector3 bNorm = NormalizePoint(backendCentroids[i], backendCenter, backendScale);
            Vector3 bTransformed = ApplyTransformCandidate(bNorm, best);

            Vector3 normal = Vector3.zero;
            if (debugFaces[i].normal != null && debugFaces[i].normal.Length >= 3)
            {
                normal = new Vector3(debugFaces[i].normal[0], debugFaces[i].normal[1], debugFaces[i].normal[2]);
                if (normal.sqrMagnitude > 0f) normal.Normalize();
            }

            backendCandidates[i] = new BackendFaceCandidate
            {
                backendIndex = i,
                transformedCentroid = bTransformed,
                area = debugFaces[i].area,
                normal = normal,
                temperature = 0f,
                used = false
            };

            string key = MakeKey(bTransformed, centroidKeyDecimals);
            if (!keyToBackendIndices.TryGetValue(key, out List<int> list))
            {
                list = new List<int>();
                keyToBackendIndices[key] = list;
            }
            list.Add(i);
        }

        bool[] matchedMask = new bool[triangleCount];
        int matchedExact = 0, matchedFallback = 0, unmatched = 0;

        // Pass 1: match esatto per chiave centroide
        for (int tri = 0; tri < triangleCount; tri++)
        {
            Vector3 unityNorm = NormalizePoint(unityCentroids[tri], unityCenter, unityScale);
            string key = MakeKey(unityNorm, centroidKeyDecimals);

            if (!keyToBackendIndices.TryGetValue(key, out List<int> candidates))
                continue;

            int selected = -1;
            for (int cc = 0; cc < candidates.Count; cc++)
            {
                int idx = candidates[cc];
                if (!backendCandidates[idx].used) { selected = idx; break; }
            }

            if (selected >= 0)
            {
                indexMap[tri] = selected;
                backendCandidates[selected].used = true;
                matchedMask[tri] = true;
                matchedExact++;
            }
        }

        // Pass 2: fallback al candidato piu' vicino
        for (int tri = 0; tri < triangleCount; tri++)
        {
            if (matchedMask[tri]) continue;

            Vector3 v0, v1, v2, faceNormal; float area;
            GetTriangleGeometry(tri, tris, verts, out v0, out v1, out v2, out faceNormal, out area);

            Vector3 unityNorm = NormalizePoint((v0 + v1 + v2) / 3f, unityCenter, unityScale);

            Vector3 avgNormal;
            if (norms != null && norms.Length == verts.Length)
            {
                int i0 = tris[tri * 3 + 0], i1 = tris[tri * 3 + 1], i2 = tris[tri * 3 + 2];
                avgNormal = (norms[i0] + norms[i1] + norms[i2]) / 3f;
                if (avgNormal.sqrMagnitude > 0f) avgNormal.Normalize();
            }
            else avgNormal = faceNormal;

            int bestIdx = -1;
            float bestScore = float.MaxValue;

            for (int i = 0; i < backendCandidates.Length; i++)
            {
                if (backendCandidates[i].used) continue;

                float dCentroid = Vector3.Distance(unityNorm, backendCandidates[i].transformedCentroid);
                if (dCentroid > 0.03f) continue;

                float dArea = Mathf.Abs(area - backendCandidates[i].area) / Mathf.Max(area, 1e-6f);

                float normalPenalty = 0f;
                if (avgNormal.sqrMagnitude > 0f && backendCandidates[i].normal.sqrMagnitude > 0f)
                {
                    float dot = Mathf.Abs(Vector3.Dot(avgNormal.normalized, backendCandidates[i].normal.normalized));
                    normalPenalty = 1f - dot;
                }

                float score = dCentroid * 10f + dArea * 2f + normalPenalty;
                if (score < bestScore) { bestScore = score; bestIdx = i; }
            }

            if (bestIdx >= 0)
            {
                indexMap[tri] = bestIdx;
                backendCandidates[bestIdx].used = true;
                matchedMask[tri] = true;
                matchedFallback++;
            }
            else
            {
                unmatched++;
            }
        }

        if (matchedExact + matchedFallback != triangleCount)
            return null;

        return indexMap;
    }

    // Costruisce una sola volta la mesh splittata per-faccia e bakea il collider (MAIN THREAD).
    private void BuildColoredMeshStructure(Mesh sourceMesh, int[] sourceTriangles, int triangleCount, MeshFilter mf, MeshCollider mc)
    {
        Vector3[] sourceVertices = sourceMesh.vertices;
        Vector3[] sourceNormals  = sourceMesh.normals;
        bool hasNormals = sourceNormals != null && sourceNormals.Length == sourceVertices.Length;

        Vector3[] newVertices  = new Vector3[sourceTriangles.Length];
        Vector3[] newNormals   = new Vector3[sourceTriangles.Length];
        int[]     newTriangles = new int[sourceTriangles.Length];
        _colorBuffer           = new Color[sourceTriangles.Length];

        for (int tri = 0; tri < triangleCount; tri++)
        {
            for (int k = 0; k < 3; k++)
            {
                int srcIndex = sourceTriangles[tri * 3 + k];
                int dstIndex = tri * 3 + k;

                newVertices[dstIndex] = sourceVertices[srcIndex];
                if (hasNormals) newNormals[dstIndex] = sourceNormals[srcIndex];
                newTriangles[dstIndex] = dstIndex;
            }
        }

        _coloredMesh = new Mesh();
        _coloredMesh.name = sourceMesh.name + "_TriangleColored";
        if (newVertices.Length > 65535)
            _coloredMesh.indexFormat = IndexFormat.UInt32;

        _coloredMesh.vertices  = newVertices;
        _coloredMesh.triangles = newTriangles;
        if (hasNormals) _coloredMesh.normals = newNormals;
        else _coloredMesh.RecalculateNormals();
        _coloredMesh.colors = _colorBuffer;
        _coloredMesh.RecalculateBounds();

        mf.sharedMesh = _coloredMesh;

        if (mc != null)
        {
            mc.sharedMesh = null;
            mc.sharedMesh = _coloredMesh;
        }

        _meshBuilt = true;
    }

    // Azzera la cache geometrica: chiamalo solo se il modello sul server cambia in sessione.
    public void InvalidateGeometryCache()
    {
        _triToBackendIndex = null;
        _coloredMesh = null;
        _colorBuffer = null;
        _meshBuilt = false;
        _building = false;
        _recolorPending = false;
    }

    // Produce le temperature per triangolo usando la cache degli indici (O(N)).
    private float[] BuildMappedTemperatures(Mesh sourceMesh)
    {
        if (apiClient == null || apiClient.responseData == null ||
            apiClient.responseData.face_temperatures == null ||
            apiClient.responseData.face_temperatures.temperatures == null)
            return null;

        float[] rawTemperatures = apiClient.responseData.face_temperatures.temperatures;
        int triangleCount = sourceMesh.triangles.Length / 3;

        if (_triToBackendIndex == null || _triToBackendIndex.Length != triangleCount)
        {
            // La mappa geometrica non e' ancora pronta: la calcola BuildThenColorAsync().
            return null;
        }

        float[] mapped = new float[triangleCount];
        for (int tri = 0; tri < triangleCount; tri++)
        {
            int bi = _triToBackendIndex[tri];
            mapped[tri] = (bi >= 0 && bi < rawTemperatures.Length) ? rawTemperatures[bi] : float.NaN;
        }
        return mapped;
    }

    private bool TryComputePercentileRange(float[] values, float lowerPct, float upperPct, out float pMin, out float pMax)
    {
        pMin = 0f;
        pMax = 0f;

        if (values == null || values.Length == 0)
            return false;

        List<float> finiteValues = new List<float>(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            if (float.IsFinite(values[i]))
                finiteValues.Add(values[i]);
        }

        if (finiteValues.Count == 0)
            return false;

        finiteValues.Sort();

        pMin = ComputePercentileSorted(finiteValues, lowerPct);
        pMax = ComputePercentileSorted(finiteValues, upperPct);

        if (pMax < pMin)
        {
            float tmp = pMin;
            pMin = pMax;
            pMax = tmp;
        }

        return true;
    }

    private float ComputePercentileSorted(List<float> sortedValues, float percentile)
    {
        if (sortedValues == null || sortedValues.Count == 0)
            return float.NaN;

        if (sortedValues.Count == 1)
            return sortedValues[0];

        float p = Mathf.Clamp(percentile, 0f, 100f) / 100f;
        float index = p * (sortedValues.Count - 1);
        int lowerIndex = Mathf.FloorToInt(index);
        int upperIndex = Mathf.CeilToInt(index);

        if (lowerIndex == upperIndex)
            return sortedValues[lowerIndex];

        float t = index - lowerIndex;
        return Mathf.Lerp(sortedValues[lowerIndex], sortedValues[upperIndex], t);
    }

    private Vector3[] GetUnityTriangleCentroids(Mesh mesh)
    {
        int[] tris = mesh.triangles;
        Vector3[] verts = mesh.vertices;
        int triangleCount = tris.Length / 3;

        Vector3[] centroids = new Vector3[triangleCount];

        for (int tri = 0; tri < triangleCount; tri++)
        {
            int i0 = tris[tri * 3 + 0];
            int i1 = tris[tri * 3 + 1];
            int i2 = tris[tri * 3 + 2];
            centroids[tri] = (verts[i0] + verts[i1] + verts[i2]) / 3f;
        }

        return centroids;
    }

    private Vector3[] GetBackendCentroids(FaceDebugData[] debugFaces)
    {
        Vector3[] centroids = new Vector3[debugFaces.Length];

        for (int i = 0; i < debugFaces.Length; i++)
        {
            centroids[i] = new Vector3(
                debugFaces[i].centroid[0],
                debugFaces[i].centroid[1],
                debugFaces[i].centroid[2]
            );
        }

        return centroids;
    }

    private TransformCandidate FindBestTransformCandidate(Vector3[] unityCentroids, Vector3[] backendCentroids)
    {
        Vector3 unityCenter = ComputeCenter(unityCentroids);
        Vector3 unityScale = ComputeScale(unityCentroids);

        Vector3 backendCenter = ComputeCenter(backendCentroids);
        Vector3 backendScale = ComputeScale(backendCentroids);

        HashSet<string> unityKeys = new HashSet<string>();
        for (int i = 0; i < unityCentroids.Length; i++)
        {
            Vector3 n = NormalizePoint(unityCentroids[i], unityCenter, unityScale);
            unityKeys.Add(MakeKey(n, normalizedKeyDecimals));
        }

        int[][] perms = new int[][]
        {
            new int[]{0,1,2},
            new int[]{0,2,1},
            new int[]{1,0,2},
            new int[]{1,2,0},
            new int[]{2,0,1},
            new int[]{2,1,0},
        };

        TransformCandidate best = new TransformCandidate
        {
            matches = -1,
            permX = 0,
            permY = 1,
            permZ = 2,
            signX = 1,
            signY = 1,
            signZ = 1
        };

        foreach (var p in perms)
        {
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        TransformCandidate candidate = new TransformCandidate
                        {
                            permX = p[0],
                            permY = p[1],
                            permZ = p[2],
                            signX = sx,
                            signY = sy,
                            signZ = sz,
                            matches = 0
                        };

                        int matches = 0;

                        for (int i = 0; i < backendCentroids.Length; i++)
                        {
                            Vector3 n = NormalizePoint(backendCentroids[i], backendCenter, backendScale);
                            Vector3 t = ApplyTransformCandidate(n, candidate);
                            string key = MakeKey(t, normalizedKeyDecimals);

                            if (unityKeys.Contains(key))
                                matches++;
                        }

                        candidate.matches = matches;

                        if (candidate.matches > best.matches)
                            best = candidate;
                    }
                }
            }
        }

        return best;
    }

    private Vector3 ComputeCenter(Vector3[] points)
    {
        Vector3 min = points[0];
        Vector3 max = points[0];

        for (int i = 1; i < points.Length; i++)
        {
            min = Vector3.Min(min, points[i]);
            max = Vector3.Max(max, points[i]);
        }

        return (min + max) * 0.5f;
    }

    private Vector3 ComputeScale(Vector3[] points)
    {
        Vector3 min = points[0];
        Vector3 max = points[0];

        for (int i = 1; i < points.Length; i++)
        {
            min = Vector3.Min(min, points[i]);
            max = Vector3.Max(max, points[i]);
        }

        Vector3 s = max - min;

        if (Mathf.Abs(s.x) < 1e-6f) s.x = 1f;
        if (Mathf.Abs(s.y) < 1e-6f) s.y = 1f;
        if (Mathf.Abs(s.z) < 1e-6f) s.z = 1f;

        return s;
    }

    private Vector3 NormalizePoint(Vector3 p, Vector3 center, Vector3 scale)
    {
        return new Vector3(
            (p.x - center.x) / scale.x,
            (p.y - center.y) / scale.y,
            (p.z - center.z) / scale.z
        );
    }

    private Vector3 ApplyTransformCandidate(Vector3 p, TransformCandidate c)
    {
        float[] a = new float[] { p.x, p.y, p.z };

        return new Vector3(
            c.signX * a[c.permX],
            c.signY * a[c.permY],
            c.signZ * a[c.permZ]
        );
    }

    private string MakeKey(Vector3 c, int decimals)
    {
        return RoundForKey(c.x, decimals) + "|" + RoundForKey(c.y, decimals) + "|" + RoundForKey(c.z, decimals);
    }

    private string RoundForKey(float v, int decimals)
    {
        return Math.Round(v, decimals).ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture);
    }

    public float GetTemperatureFromTriangleIndex(int triangleIndex)
    {
        if (mappedTemperatures == null)
            return float.NaN;

        if (triangleIndex < 0 || triangleIndex >= mappedTemperatures.Length)
            return float.NaN;

        return mappedTemperatures[triangleIndex];
    }

    public bool TryGetTriangleDebugInfo(int triangleIndex, out TriangleDebugInfo info)
    {
        info = default;

        MeshFilter mf = GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null)
            return false;

        Mesh mesh = mf.sharedMesh;
        int[] tris = mesh.triangles;
        Vector3[] verts = mesh.vertices;
        Vector3[] norms = mesh.normals;

        int triangleCount = tris.Length / 3;
        if (triangleIndex < 0 || triangleIndex >= triangleCount)
            return false;

        int i0 = tris[triangleIndex * 3 + 0];
        int i1 = tris[triangleIndex * 3 + 1];
        int i2 = tris[triangleIndex * 3 + 2];

        Vector3 v0 = verts[i0];
        Vector3 v1 = verts[i1];
        Vector3 v2 = verts[i2];

        Vector3 e1 = v1 - v0;
        Vector3 e2 = v2 - v0;
        Vector3 faceNormal = Vector3.Cross(e1, e2);
        float area = 0.5f * faceNormal.magnitude;

        if (faceNormal.sqrMagnitude > 0f)
            faceNormal.Normalize();

        Vector3 averagedVertexNormal = Vector3.zero;
        bool hasVertexNormals = norms != null && norms.Length == verts.Length;
        if (hasVertexNormals)
        {
            averagedVertexNormal = (norms[i0] + norms[i1] + norms[i2]) / 3f;
            if (averagedVertexNormal.sqrMagnitude > 0f)
                averagedVertexNormal.Normalize();
        }

        info = new TriangleDebugInfo
        {
            triangleIndex = triangleIndex,
            temperature = GetTemperatureFromTriangleIndex(triangleIndex),
            vertex0 = v0,
            vertex1 = v1,
            vertex2 = v2,
            centroid = (v0 + v1 + v2) / 3f,
            faceNormal = faceNormal,
            averagedVertexNormal = averagedVertexNormal,
            area = area
        };

        return true;
    }

    private Color TemperatureToColor(float t, float minT, float maxT)
    {
        EnsureGradientInitialized();
        float x = Mathf.InverseLerp(minT, maxT, t);
        return temperatureGradient.Evaluate(x);
    }

    private void EnsureGradientInitialized()
    {
        if (temperatureGradient == null)
            temperatureGradient = new Gradient();

        if (!useCustomGradient || temperatureGradient.colorKeys == null || temperatureGradient.colorKeys.Length == 0)
        {
            ApplyPalettePreset(palettePreset);
        }
    }

    /// <summary>Colore della rampa corrente alla posizione normalizzata 0..1 (0 = min, 1 = max).
    /// Usato dalla legenda colori. Riflette sempre la palette selezionata.</summary>
    public Color EvaluateRamp(float t01)
    {
        EnsureGradientInitialized();
        return temperatureGradient.Evaluate(Mathf.Clamp01(t01));
    }

    private void ApplyPalettePreset(TemperaturePalette preset)
    {
        GradientColorKey[] colors;
        GradientAlphaKey[] alphas = new GradientAlphaKey[]
        {
            new GradientAlphaKey(1f, 0f),
            new GradientAlphaKey(1f, 1f)
        };

        switch (preset)
        {
            case TemperaturePalette.ClassicThermal:
                colors = new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0f, 0f, 1f), 0f),
                    new GradientColorKey(new Color(0f, 1f, 1f), 0.25f),
                    new GradientColorKey(new Color(0f, 1f, 0f), 0.50f),
                    new GradientColorKey(new Color(1f, 1f, 0f), 0.75f),
                    new GradientColorKey(new Color(1f, 0f, 0f), 1f),
                };
                break;

            case TemperaturePalette.HighContrastVR:
                colors = new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0.05f, 0.05f, 0.35f), 0f),
                    new GradientColorKey(new Color(0f, 0.8f, 1f), 0.30f),
                    new GradientColorKey(new Color(1f, 1f, 1f), 0.55f),
                    new GradientColorKey(new Color(1f, 0.45f, 0f), 0.78f),
                    new GradientColorKey(new Color(1f, 0f, 0f), 1f),
                };
                break;

            case TemperaturePalette.ArchitecturalWarm:
                colors = new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0.35f, 0.38f, 0.45f), 0f),
                    new GradientColorKey(new Color(0.62f, 0.68f, 0.75f), 0.25f),
                    new GradientColorKey(new Color(0.93f, 0.77f, 0.55f), 0.55f),
                    new GradientColorKey(new Color(0.89f, 0.46f, 0.22f), 0.80f),
                    new GradientColorKey(new Color(0.55f, 0.16f, 0.08f), 1f),
                };
                break;

            case TemperaturePalette.IronOxide:
                colors = new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0.12f, 0.12f, 0.18f), 0f),
                    new GradientColorKey(new Color(0.36f, 0.22f, 0.18f), 0.25f),
                    new GradientColorKey(new Color(0.63f, 0.30f, 0.18f), 0.55f),
                    new GradientColorKey(new Color(0.82f, 0.44f, 0.20f), 0.80f),
                    new GradientColorKey(new Color(0.95f, 0.72f, 0.30f), 1f),
                };
                break;

            case TemperaturePalette.Grayscale:
                colors = new GradientColorKey[]
                {
                    new GradientColorKey(Color.black, 0f),
                    new GradientColorKey(new Color(0.35f, 0.35f, 0.35f), 0.3f),
                    new GradientColorKey(new Color(0.65f, 0.65f, 0.65f), 0.65f),
                    new GradientColorKey(Color.white, 1f),
                };
                break;

            default:
                colors = new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0f, 0f, 1f), 0f),
                    new GradientColorKey(new Color(0f, 1f, 1f), 0.25f),
                    new GradientColorKey(new Color(0f, 1f, 0f), 0.50f),
                    new GradientColorKey(new Color(1f, 1f, 0f), 0.75f),
                    new GradientColorKey(new Color(1f, 0f, 0f), 1f),
                };
                break;
        }

        temperatureGradient.SetKeys(colors, alphas);
    }

    private struct BackendFaceCandidate
    {
        public int backendIndex;
        public Vector3 transformedCentroid;
        public float area;
        public Vector3 normal;
        public float temperature;
        public bool used;
    }

    private void GetTriangleGeometry(
        int triangleIndex,
        int[] tris,
        Vector3[] verts,
        out Vector3 v0,
        out Vector3 v1,
        out Vector3 v2,
        out Vector3 faceNormal,
        out float area)
    {
        int i0 = tris[triangleIndex * 3 + 0];
        int i1 = tris[triangleIndex * 3 + 1];
        int i2 = tris[triangleIndex * 3 + 2];

        v0 = verts[i0];
        v1 = verts[i1];
        v2 = verts[i2];

        faceNormal = Vector3.Cross(v1 - v0, v2 - v0);
        area = 0.5f * faceNormal.magnitude;

        if (faceNormal.sqrMagnitude > 0f)
            faceNormal.Normalize();
    }

    private struct TransformCandidate
    {
        public int permX;
        public int permY;
        public int permZ;
        public int signX;
        public int signY;
        public int signZ;
        public int matches;
    }
}

public enum TemperaturePalette
{
    ClassicThermal,
    HighContrastVR,
    ArchitecturalWarm,
    IronOxide,
    Grayscale
}

[System.Serializable]
public struct TriangleDebugInfo
{
    public int triangleIndex;
    public float temperature;
    public Vector3 vertex0;
    public Vector3 vertex1;
    public Vector3 vertex2;
    public Vector3 centroid;
    public Vector3 faceNormal;
    public Vector3 averagedVertexNormal;
    public float area;
}
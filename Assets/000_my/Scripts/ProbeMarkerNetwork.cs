using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Voce di un marker condiviso: chi l'ha piazzato (clientId), la posizione LOCALE nel frame
/// del modello (x,y,z) e la temperatura letta. Locale = ancorato al modello -> regge la colocation.
/// </summary>
public struct ProbeMarker : INetworkSerializable, IEquatable<ProbeMarker>
{
    public ulong clientId;
    public float x, y, z;      // posizione locale nel frame del modello
    public float nx, ny, nz;   // normale della superficie (locale al modello)
    public float tempC;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref clientId);
        s.SerializeValue(ref x);
        s.SerializeValue(ref y);
        s.SerializeValue(ref z);
        s.SerializeValue(ref nx);
        s.SerializeValue(ref ny);
        s.SerializeValue(ref nz);
        s.SerializeValue(ref tempC);
    }

    public bool Equals(ProbeMarker o) =>
        clientId == o.clientId && x == o.x && y == o.y && z == o.z &&
        nx == o.nx && ny == o.ny && nz == o.nz && tempC == o.tempC;
}

/// <summary>
/// Stato di rete dei marker della sonda: una lista sincronizzata con UN marker per utente.
/// Va su un NetworkObject (es. lo stesso di ThermalSimController). Ogni client, quando spara,
/// chiama RequestPlaceMarker; l'autorita' scrive la lista; tutti i client la ricevono
/// (anche chi entra dopo) e la disegnano con ProbeMarkerRenderer.
/// </summary>
public class ProbeMarkerNetwork : NetworkBehaviour
{
    private readonly NetworkList<ProbeMarker> _markers = new();

    /// <summary>Invocato quando la lista cambia (o allo spawn), per ridisegnare i marker.</summary>
    public event Action OnMarkersChanged;

    public int Count => _markers.Count;
    public ProbeMarker Get(int i) => _markers[i];

    public override void OnNetworkSpawn()
    {
        _markers.OnListChanged += HandleListChanged;
        OnMarkersChanged?.Invoke();   // stato iniziale (utile per il late joiner)

        if (IsOwner)   // l'autorita' dell'oggetto fa pulizia alla disconnessione
            NetworkManager.OnClientDisconnectCallback += HandleClientDisconnect;
    }

    public override void OnNetworkDespawn()
    {
        _markers.OnListChanged -= HandleListChanged;
        if (NetworkManager != null)
            NetworkManager.OnClientDisconnectCallback -= HandleClientDisconnect;
    }

    private void HandleListChanged(NetworkListEvent<ProbeMarker> _) => OnMarkersChanged?.Invoke();

    /// <summary>Da chiamare sul client che spara. localPos/localNormal nel frame del modello.</summary>
    public void RequestPlaceMarker(Vector3 localPos, Vector3 localNormal, float tempC)
    {
        ulong id = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0;
        PlaceMarkerRpc(id, localPos.x, localPos.y, localPos.z,
                       localNormal.x, localNormal.y, localNormal.z, tempC);
    }

    [Rpc(SendTo.Owner)]
    private void PlaceMarkerRpc(ulong clientId, float x, float y, float z,
                               float nx, float ny, float nz, float tempC)
    {
        var m = new ProbeMarker
        {
            clientId = clientId,
            x = x, y = y, z = z,
            nx = nx, ny = ny, nz = nz,
            tempC = tempC
        };

        // Aggiorna il marker esistente dell'utente, oppure aggiungine uno nuovo.
        for (int i = 0; i < _markers.Count; i++)
        {
            if (_markers[i].clientId == clientId)
            {
                _markers[i] = m;
                return;
            }
        }
        _markers.Add(m);
    }

    /// <summary>
    /// Toglie tutti i marker. Serve al cambio di borgo: le coordinate sono locali al
    /// frame del modello, quindi un marker piazzato su Caldana, riprodotto su Bibbona,
    /// indicherebbe un punto che nessuno ha mai misurato - e lo farebbe con l'aria di
    /// un dato, che e' il modo peggiore di sbagliare in uno strumento di analisi.
    /// </summary>
    public void RequestClearAll() => ClearAllRpc();

    [Rpc(SendTo.Owner)]
    private void ClearAllRpc()
    {
        _markers.Clear();
    }

    private void HandleClientDisconnect(ulong clientId)
    {
        for (int i = _markers.Count - 1; i >= 0; i--)
            if (_markers[i].clientId == clientId)
                _markers.RemoveAt(i);
    }
}

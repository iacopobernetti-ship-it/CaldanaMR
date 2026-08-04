using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Indicatore di stato: un cerchio (Image) rosso/verde e una scritta "Wait"/"Ready".
///
/// Diventa rosso/"Wait" quando c'e' lavoro in corso, verde/"Ready" quando e' pronto.
/// Combina DUE sorgenti (mostra Wait se almeno una e' occupata):
///   - controller.OnSimulationBusyChanged : la chiamata API della simulazione data/ora (~8-10s)
///   - colorizer.OnBusyChanged            : il calcolo iniziale della mappatura geometrica
/// Il Set Color non e' coperto perche' e' ormai istantaneo.
///
/// Lo stato e' LOCALE al client. Lo script va su un oggetto SEMPRE ATTIVO; se il pannello
/// che contiene cerchio e label viene acceso/spento, chiamare RefreshNow() al suo OnEnable.
/// </summary>
public class ThermalSimStatusUI : MonoBehaviour
{
    [Header("Riferimenti")]
    public ThermalSimNetworkController controller;
    public TriangleTemperatureColorizer colorizer;
    [Tooltip("Il cerchio (Image) che cambia colore.")]
    public Image statusCircle;
    [Tooltip("La scritta che mostra Wait / Ready.")]
    public TMP_Text statusLabel;

    [Header("Aspetto")]
    public Color waitColor = Color.red;
    public Color readyColor = Color.green;
    public string waitText = "Wait";
    public string readyText = "Ready";

    private bool _simBusy;
    private bool _buildBusy;

    private void OnEnable()
    {
        if (controller != null)
        {
            controller.OnSimulationBusyChanged += HandleSimBusy;
            _simBusy = controller.IsSimulationBusy;
        }
        if (colorizer != null)
        {
            colorizer.OnBusyChanged += HandleBuildBusy;
            _buildBusy = colorizer.IsBusy;
        }
        Refresh();
    }

    private void OnDisable()
    {
        if (controller != null)
            controller.OnSimulationBusyChanged -= HandleSimBusy;
        if (colorizer != null)
            colorizer.OnBusyChanged -= HandleBuildBusy;
    }

    private void HandleSimBusy(bool busy)
    {
        _simBusy = busy;
        Refresh();
    }

    private void HandleBuildBusy(bool busy)
    {
        _buildBusy = busy;
        Refresh();
    }

    /// <summary>Risincronizza l'indicatore con lo stato corrente.
    /// Chiamalo dall'OnEnable del pannello se questo viene acceso/spento.</summary>
    public void RefreshNow()
    {
        if (controller != null) _simBusy = controller.IsSimulationBusy;
        if (colorizer != null) _buildBusy = colorizer.IsBusy;
        Refresh();
    }

    private void Refresh()
    {
        bool busy = _simBusy || _buildBusy;

        if (statusCircle != null)
            statusCircle.color = busy ? waitColor : readyColor;

        if (statusLabel != null)
            statusLabel.text = busy ? waitText : readyText;
    }
}

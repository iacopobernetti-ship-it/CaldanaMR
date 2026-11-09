using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Selettore del borgo: una riga di pulsanti, uno per borgo configurato nel controller.
///
/// E' un componente a se' invece di una modifica a ThermalSimPanelUI perche' quel file
/// funziona e non vale la pena rimetterci le mani per aggiungere una riga: lo si appende
/// dove serve e si collega da Inspector, come gia' fatto per il pannello degli indici.
///
/// Gli onClick non vanno collegati a mano: li aggancia lo script in base alla posizione
/// del pulsante nell'array, cosi' l'ordine dei pulsanti segue l'ordine dei borghi.
/// </summary>
public class VillageSelectorUI : MonoBehaviour
{
    [Header("Riferimenti")]
    public ThermalSimNetworkController controller;

    [Header("Pulsanti")]
    [Tooltip("Un pulsante per borgo, nello STESSO ordine dell'array 'villages' del controller. " +
             "Metti Transition = None su ciascuno: l'evidenziazione la gestisce questo script.")]
    public Button[] villageButtons;

    [Tooltip("Se assegnate, queste etichette prendono il nome del borgo dal controller, " +
             "cosi' il testo dei pulsanti non va tenuto allineato a mano.")]
    public TextMeshProUGUI[] villageLabels;

    [Header("Evidenziazione")]
    public Color normalColor = new Color(0.22f, 0.22f, 0.24f, 1f);
    public Color selectedColor = new Color(0.16f, 0.45f, 0.78f, 1f);

    [Header("Etichetta di stato (opzionale)")]
    [Tooltip("Mostra il nome del borgo attivo, per esempio nel pannello informazioni.")]
    public TextMeshProUGUI activeVillageLabel;

    private void OnEnable()
    {
        WireButtons();

        if (controller != null) controller.OnStateChanged += Refresh;
        Refresh();
    }

    private void OnDisable()
    {
        if (controller != null) controller.OnStateChanged -= Refresh;
    }

    private void WireButtons()
    {
        if (villageButtons == null) return;

        for (int i = 0; i < villageButtons.Length; i++)
        {
            Button b = villageButtons[i];
            if (b == null) continue;

            // Si rimuovono prima i listener aggiunti da questo script in una precedente
            // attivazione: senza questo, un pannello riaperto piu' volte accumulerebbe
            // chiamate e un clic ne varrebbe tre.
            b.onClick.RemoveAllListeners();

            int captured = i;   // copia locale: senza, tutti i pulsanti userebbero l'ultimo indice
            b.onClick.AddListener(() =>
            {
                if (controller != null) controller.RequestSetVillage(captured);
            });
        }
    }

    /// <summary>Riallinea etichette ed evidenziazione allo stato sincronizzato.</summary>
    public void Refresh()
    {
        if (controller == null) return;

        int active = controller.VillageIndex;

        if (villageLabels != null)
        {
            for (int i = 0; i < villageLabels.Length; i++)
            {
                if (villageLabels[i] == null) continue;
                string n = controller.VillageNameAt(i);
                if (!string.IsNullOrEmpty(n)) villageLabels[i].text = n;
            }
        }

        if (villageButtons != null)
        {
            for (int i = 0; i < villageButtons.Length; i++)
            {
                Button b = villageButtons[i];
                if (b == null) continue;

                // Un pulsante oltre il numero di borghi configurati si nasconde invece di
                // restare cliccabile a vuoto: tre pulsanti e due borghi non devono dare
                // un terzo bersaglio che non fa niente.
                bool exists = i < controller.VillageCount;
                b.gameObject.SetActive(exists);
                if (!exists) continue;

                Image img = b.targetGraphic as Image;
                if (img == null) img = b.GetComponent<Image>();
                if (img != null) img.color = (i == active) ? selectedColor : normalColor;

                // Durante una simulazione i pulsanti restano premibili ma il controller
                // ignora la richiesta del borgo gia' attivo, quindi non serve bloccarli.
            }
        }

        if (activeVillageLabel != null)
            activeVillageLabel.text = controller.ActiveVillageName;
    }
}

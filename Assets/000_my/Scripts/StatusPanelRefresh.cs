using UnityEngine;

public class StatusPanelRefresh : MonoBehaviour
{
    public ThermalSimStatusUI statusUI;
    private void OnEnable()
    {
        if (statusUI != null) statusUI.RefreshNow();
    }
}
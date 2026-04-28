using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;

/// <summary>
/// Casts a physics ray from <see cref="rayOrigin"/> each frame and forwards hits/misses to
/// <see cref="CurvedCanvasInteractor"/> so its captured Canvas receives hover/click events.
/// </summary>
public class CurvedPanelInputForwarder : MonoBehaviour
{
    [Tooltip("Transform whose forward direction is used for the picking ray (e.g. controller tip).")]
    public Transform rayOrigin;

    [Tooltip("Trigger / select action. Read as float; click fires when value > 0.5.")]
    public InputActionReference triggerAction;

    [Tooltip("Curved canvas being driven by this forwarder.")]
    public CurvedCanvasInteractor curvedPanel;

    [Tooltip("Maximum ray distance (meters).")]
    public float maxDistance = 10f;

    [Tooltip("Optional layer mask to filter what the ray can hit.")]
    public LayerMask layerMask = ~0;

    bool _wasClicking;

    void OnEnable()
    {
        if (triggerAction != null && triggerAction.action != null && !triggerAction.action.enabled)
            triggerAction.action.Enable();
    }

    void Update()
    {
        if (rayOrigin == null || curvedPanel == null) return;

        float actionValue = triggerAction != null && triggerAction.action != null
                            ? triggerAction.action.ReadValue<float>()
                            : 0f;

        // Some controller profiles (notably VIVE Focus / Cosmos OpenXR on this project)
        // don't bind cleanly through the new Input System action, so the action value can
        // stay 0 even while the trigger is pressed. Fall back to the legacy XR InputDevices
        // API which the hardware does report through, so a click still fires either way.
        var rightHand = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        bool legacyTrigger = false;
        float legacyTriggerValue = 0f;
        if (rightHand.isValid)
        {
            rightHand.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out legacyTrigger);
            rightHand.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trigger, out legacyTriggerValue);
        }

        float effectiveTrigger = Mathf.Max(actionValue, legacyTriggerValue);
        bool clickingNow = effectiveTrigger > 0.5f || legacyTrigger;
        bool clickEdge = clickingNow && !_wasClicking;
        _wasClicking = clickingNow;

        bool hitOk = Physics.Raycast(rayOrigin.position, rayOrigin.forward,
                            out RaycastHit hit, maxDistance, layerMask, QueryTriggerInteraction.Collide);
        bool hitsThisPanel = hitOk && hit.collider.GetComponentInParent<CurvedCanvasInteractor>() == curvedPanel;

        if (hitsThisPanel)
            curvedPanel.OnRayHit(hit, clickEdge);
        else
            curvedPanel.OnRayMiss();
    }
}

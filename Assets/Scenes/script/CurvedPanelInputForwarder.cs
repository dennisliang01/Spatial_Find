using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;

/// <summary>
/// Casts a physics ray from <see cref="rayOrigin"/> each frame and forwards hits to
/// <see cref="CurvedCanvasInteractor"/>. The right-hand trigger fires a click while the ray is on the panel.
/// </summary>
public class CurvedPanelInputForwarder : MonoBehaviour
{
    [Tooltip("Right controller transform. Its forward direction is the picking ray.")]
    public Transform rayOrigin;

    [Tooltip("Right-hand trigger action. Read as float; click fires when value > 0.5.")]
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
        if (curvedPanel == null || rayOrigin == null) return;

        bool hits = TryRayHitsPanel(out RaycastHit hit);
        bool pressed = ReadRightTriggerPressed();
        bool clickEdge = hits && pressed && !_wasClicking;

        // Capture the held state regardless of panel hit so re-entering the panel with the
        // trigger already held doesn't fire a stale edge.
        _wasClicking = pressed;

        if (hits)
            curvedPanel.OnRayHit(hit, clickEdge);
        else
            curvedPanel.OnRayMiss();
    }

    bool TryRayHitsPanel(out RaycastHit bestHit)
    {
        bestHit = default;
        if (!Physics.Raycast(rayOrigin.position, rayOrigin.forward,
                out RaycastHit h, maxDistance, layerMask, QueryTriggerInteraction.Collide))
            return false;
        if (h.collider.GetComponentInParent<CurvedCanvasInteractor>() != curvedPanel)
            return false;
        bestHit = h;
        return true;
    }

    bool ReadRightTriggerPressed()
    {
        // Prefer the configured InputAction (XRI bindings); fall back to legacy XR feature polling
        // so headsets without the new input system still work.
        float actionVal = triggerAction != null && triggerAction.action != null
            ? triggerAction.action.ReadValue<float>()
            : 0f;

        UnityEngine.XR.InputDevice dev = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        bool legacyBtn = false;
        float legacyVal = 0f;
        if (dev.isValid)
        {
            dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out legacyBtn);
            dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trigger, out legacyVal);
        }

        return Mathf.Max(actionVal, legacyVal) > 0.5f || legacyBtn;
    }
}

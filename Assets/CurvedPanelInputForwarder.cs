using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;

/// <summary>
/// Casts a physics ray from <see cref="rayOrigin"/> (and optionally <see cref="leftRayOrigin"/>) each
/// frame and forwards hits/misses to <see cref="CurvedCanvasInteractor"/> so its captured Canvas
/// receives hover/click events. Uses the trigger that matches the controller whose ray wins (closest
/// hit when both aim at the panel).
/// </summary>
public class CurvedPanelInputForwarder : MonoBehaviour
{
    const string LeftHandTag = "LeftHand";

    [Tooltip("Transform whose forward direction is used for the picking ray (e.g. right controller tip).")]
    public Transform rayOrigin;

    [Tooltip("Optional left controller transform. If unset, a GameObject tagged \"" + LeftHandTag + "\" is used when found.")]
    public Transform leftRayOrigin;

    [Tooltip("Trigger / select action for the right hand. Read as float; click fires when value > 0.5.")]
    public InputActionReference triggerAction;

    [Tooltip("Optional. Left-hand trigger / activate value action (e.g. XRI LeftHand / Activate Value). If null, left clicks use only legacy XR trigger.")]
    public InputActionReference leftTriggerAction;

    [Tooltip("Curved canvas being driven by this forwarder.")]
    public CurvedCanvasInteractor curvedPanel;

    [Tooltip("Maximum ray distance (meters).")]
    public float maxDistance = 10f;

    [Tooltip("Optional layer mask to filter what the ray can hit.")]
    public LayerMask layerMask = ~0;

    bool _wasClicking;
    bool _lastHandWasLeft;

    void OnEnable()
    {
        ResolveLeftRayOriginIfNeeded();
        if (triggerAction != null && triggerAction.action != null && !triggerAction.action.enabled)
            triggerAction.action.Enable();
        if (leftTriggerAction != null && leftTriggerAction.action != null && !leftTriggerAction.action.enabled)
            leftTriggerAction.action.Enable();
    }

    void ResolveLeftRayOriginIfNeeded()
    {
        if (leftRayOrigin != null) return;
        try
        {
            var go = GameObject.FindGameObjectWithTag(LeftHandTag);
            if (go != null)
                leftRayOrigin = go.transform;
        }
        catch (UnityException)
        {
            // Tag not defined in Tag Manager; leave null.
        }
    }

    void Update()
    {
        ResolveLeftRayOriginIfNeeded();
        if (curvedPanel == null) return;
        if (rayOrigin == null && leftRayOrigin == null) return;

        bool useLeft = false;
        bool hitsThisPanel = false;
        RaycastHit hit = default;

        if (TryGetBestHit(out hit, out useLeft))
            hitsThisPanel = true;

        if (hitsThisPanel && useLeft != _lastHandWasLeft)
            _wasClicking = false;
        if (hitsThisPanel)
            _lastHandWasLeft = useLeft;

        float actionRight = triggerAction != null && triggerAction.action != null
            ? triggerAction.action.ReadValue<float>()
            : 0f;

        float actionLeft = leftTriggerAction != null && leftTriggerAction.action != null
            ? leftTriggerAction.action.ReadValue<float>()
            : 0f;

        var rightDev = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        bool legacyRightTriggerBtn = false;
        float legacyRightTriggerVal = 0f;
        if (rightDev.isValid)
        {
            rightDev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out legacyRightTriggerBtn);
            rightDev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trigger, out legacyRightTriggerVal);
        }

        var leftDev = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        bool legacyLeftTriggerBtn = false;
        float legacyLeftTriggerVal = 0f;
        if (leftDev.isValid)
        {
            leftDev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out legacyLeftTriggerBtn);
            leftDev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trigger, out legacyLeftTriggerVal);
        }

        float effectiveTrigger = useLeft
            ? Mathf.Max(actionLeft, legacyLeftTriggerVal)
            : Mathf.Max(actionRight, legacyRightTriggerVal);
        bool legacyClick = useLeft ? legacyLeftTriggerBtn : legacyRightTriggerBtn;

        bool clickingNow = effectiveTrigger > 0.5f || legacyClick;
        bool clickEdge = clickingNow && !_wasClicking;
        _wasClicking = clickingNow;

        if (hitsThisPanel)
            curvedPanel.OnRayHit(hit, clickEdge);
        else
        {
            curvedPanel.OnRayMiss();
            _wasClicking = false;
        }
    }

    bool TryGetBestHit(out RaycastHit bestHit, out bool fromLeft)
    {
        bestHit = default;
        fromLeft = false;
        bool any = false;
        float bestDist = float.MaxValue;

        if (rayOrigin != null &&
            Physics.Raycast(rayOrigin.position, rayOrigin.forward,
                out RaycastHit rhit, maxDistance, layerMask, QueryTriggerInteraction.Collide) &&
            rhit.collider.GetComponentInParent<CurvedCanvasInteractor>() == curvedPanel)
        {
            bestHit = rhit;
            bestDist = rhit.distance;
            fromLeft = false;
            any = true;
        }

        if (leftRayOrigin != null &&
            Physics.Raycast(leftRayOrigin.position, leftRayOrigin.forward,
                out RaycastHit lhit, maxDistance, layerMask, QueryTriggerInteraction.Collide) &&
            lhit.collider.GetComponentInParent<CurvedCanvasInteractor>() == curvedPanel)
        {
            if (!any || lhit.distance < bestDist)
            {
                bestHit = lhit;
                fromLeft = true;
            }
            any = true;
        }

        return any;
    }
}

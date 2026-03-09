using UnityEngine;

/// <summary>
/// Keeps the attached object in front of the XR camera (head-locked) so the panel stays in view.
/// Attach to the root of your panel/content. Adjust distance and height offset in the Inspector.
/// </summary>
public class HeadLockedPanel : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("Leave empty to use Main Camera (XR camera).")]
    public Transform followTarget;

    [Header("Placement")]
    [Tooltip("Distance in front of the user.")]
    public float distance = 2f;
    [Tooltip("Vertical offset from camera (positive = above eye level).")]
    public float heightOffset = 0f;

    [Header("Behavior")]
    [Tooltip("If true, panel rotation follows the camera so it always faces the user. If false, only position follows (panel stays upright).")]
    public bool matchCameraRotation = false;
    [Tooltip("Smooth follow (0 = instant). Higher = less jitter, more lag.")]
    [Range(0f, 20f)]
    public float smoothSpeed = 8f;

    private Transform _cam;

    void Start()
    {
        if (followTarget != null)
            _cam = followTarget;
        else
        {
            var origin = GameObject.Find("XR Origin");
            if (origin != null)
            {
                var cam = origin.GetComponentInChildren<Camera>();
                if (cam != null) _cam = cam.transform;
            }
            if (_cam == null && Camera.main != null)
                _cam = Camera.main.transform;
        }

        if (_cam == null)
        {
            Debug.LogError("[HeadLockedPanel] No camera found. Assign Follow Target or ensure XR Origin has a Camera.");
            enabled = false;
            return;
        }

        // Snap to target once at start so it's in view immediately
        ApplyPositionAndRotation(instant: true);
    }

    void LateUpdate()
    {
        if (_cam == null) return;
        ApplyPositionAndRotation(instant: smoothSpeed <= 0f);
    }

    void ApplyPositionAndRotation(bool instant)
    {
        Vector3 forward = _cam.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.01f) forward = _cam.forward;
        forward.Normalize();

        Vector3 targetPos = _cam.position + forward * distance + Vector3.up * heightOffset;
        Quaternion targetRot = matchCameraRotation ? _cam.rotation : Quaternion.LookRotation(-forward, Vector3.up);

        if (instant)
        {
            transform.position = targetPos;
            transform.rotation = targetRot;
        }
        else
        {
            float t = smoothSpeed * Time.deltaTime;
            transform.position = Vector3.Lerp(transform.position, targetPos, t);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, t);
        }
    }
}

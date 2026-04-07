using UnityEngine;
using VIVE.OpenXR.CompositionLayer;
using VIVE.OpenXR.Passthrough;

public class MRPassthrough : MonoBehaviour
{
    private static bool _appQuitting;

    private VIVE.OpenXR.Passthrough.XrPassthroughHTC _passthroughId;
    private bool _passthroughCreated;

    void OnApplicationQuit()
    {
        _appQuitting = true;
    }

    void Start()
    {
        _passthroughCreated = false;

        SetCameraTransparent();

        PassthroughAPI.CreatePlanarPassthrough(out _passthroughId, LayerType.Underlay);
        _passthroughCreated = _passthroughId != default;

        if (_passthroughCreated)
            Debug.Log("[MRPassthrough] Passthrough created successfully.");
        else
            Debug.LogWarning("[MRPassthrough] Passthrough creation failed.");
    }

    void SetCameraTransparent()
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            var xrOrigin = GameObject.Find("XR Origin");
            if (xrOrigin != null)
                cam = xrOrigin.GetComponentInChildren<Camera>();
        }

        if (cam != null)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        }
        else
        {
            Debug.LogWarning("[MRPassthrough] No camera found to set transparent background.");
        }
    }

    void OnDestroy()
    {
        if (!_passthroughCreated || _passthroughId == default || _appQuitting)
            return;
        try
        {
            PassthroughAPI.DestroyPassthrough(_passthroughId);
        }
        catch (System.Exception)
        {
            // Session may already be torn down; ignore.
        }
        _passthroughId = default;
        _passthroughCreated = false;
    }
}

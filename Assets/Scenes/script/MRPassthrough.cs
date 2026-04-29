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
        PassthroughAPI.CreatePlanarPassthrough(out _passthroughId, LayerType.Underlay);
        _passthroughCreated = _passthroughId != default;
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

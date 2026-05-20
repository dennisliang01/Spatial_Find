using UnityEngine;
using UnityEngine.InputSystem;
using Scenes.script;

/// <summary>
/// Attach to an <see cref="ImageGridPanel"/> root to render its runtime UI Canvas onto a
/// curved 3D mesh, mirroring the setup used for the PromptWindow panel.
///
/// Spawns a child GameObject "CurvedDisplay" with MeshFilter / MeshRenderer / MeshCollider +
/// <see cref="CurvedCanvasDisplay"/> + <see cref="CurvedCanvasInteractor"/> +
/// <see cref="CurvedPanelInputForwarder"/>. The display captures the runtime "ImageGrid_Canvas"
/// that <see cref="ImageGridPanel"/> creates in <c>Awake</c>, so this component must execute
/// BEFORE <see cref="ImageGridPanel"/> (enforced via <see cref="DefaultExecutionOrderAttribute"/>).
///
/// Disables <see cref="ImageGridPanel.anchorWorldSpaceCanvasWhenEnabled"/> on the same panel so
/// <c>WorldSpaceCanvasSpawnOnce</c> never runs and detaches the canvas from under us.
///
/// One-time scene setup: ensure at least one <see cref="CurvedPanelInputForwarder"/> in the
/// scene has its <c>rayOrigin</c> and <c>triggerAction</c> wired (e.g. on the existing prompt
/// CurvedImagePanel). Other instances will copy those bindings automatically.
/// </summary>
[DefaultExecutionOrder(-1000)]
[RequireComponent(typeof(ImageGridPanel))]
public class CurveImageGridPanel : MonoBehaviour
{
    [Header("Curved mesh shape")]
    [Tooltip("Width of the curved panel along the arc (meters).")]
    public float width = 1.5f;

    [Tooltip("Height of the curved panel (meters).")]
    public float height = 0.9f;

    [Range(5f, 90f)]
    [Tooltip("Total arc subtended by the panel, in degrees.")]
    public float curveAngleDegrees = 25f;

    [Range(4, 128)]
    public int columnSegments = 32;

    [Tooltip("True = wrap toward the viewer (concave). False = bulge away (convex).")]
    public bool concave = true;

    [Tooltip("Where local Y=0 sits on the mesh. Use Bottom on stage panels you want bottom-aligned across stages with different heights.")]
    public CurvedCanvasDisplay.VerticalPivot verticalPivot = CurvedCanvasDisplay.VerticalPivot.Bottom;

    [Tooltip("Local position of the curved mesh relative to the ImageGridPanel root.")]
    public Vector3 localPosition = Vector3.zero;

    [Tooltip("Local Euler rotation of the curved mesh relative to the ImageGridPanel root.")]
    public Vector3 localEulerAngles = Vector3.zero;

    [Header("Capture")]
    [Tooltip("Resolution of the auto-created render texture per panel.")]
    public Vector2Int renderTextureSize = new Vector2Int(2048, 1152);

    [Tooltip("Optional material used by the curved mesh. If null, CurvedCanvasDisplay creates an unlit/transparent instance.")]
    public Material curvedMaterial;

    [Header("Input")]
    [Tooltip("Controller transform whose forward direction casts the picking ray (right controller). If null, copied from any existing CurvedPanelInputForwarder in the scene.")]
    public Transform rayOrigin;

    [Tooltip("Trigger action for the right hand. If null, copied from any existing CurvedPanelInputForwarder in the scene.")]
    public InputActionReference triggerAction;

    [Tooltip("Optional layer mask for the picking ray.")]
    public LayerMask layerMask = ~0;

    [Tooltip("Maximum ray distance (meters).")]
    public float rayMaxDistance = 10f;

    GameObject _curvedDisplayGo;

    void Awake()
    {
        // Stop ImageGridPanel from world-anchoring its runtime canvas. Otherwise
        // WorldSpaceCanvasSpawnOnce.Awake() detaches the canvas (parent = null) before the
        // curved display has a chance to capture it via sourcePanel.GetComponentInChildren.
        var panel = GetComponent<ImageGridPanel>();
        if (panel != null)
            panel.anchorWorldSpaceCanvasWhenEnabled = false;

        BuildCurvedDisplayIfNeeded();
    }

    void BuildCurvedDisplayIfNeeded()
    {
        if (_curvedDisplayGo != null) return;

        _curvedDisplayGo = new GameObject(
            "CurvedDisplay",
            typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
        _curvedDisplayGo.transform.SetParent(transform, worldPositionStays: false);
        _curvedDisplayGo.transform.localPosition = localPosition;
        _curvedDisplayGo.transform.localRotation = Quaternion.Euler(localEulerAngles);
        _curvedDisplayGo.transform.localScale = Vector3.one;

        var display = _curvedDisplayGo.AddComponent<CurvedCanvasDisplay>();
        display.width = width;
        display.height = height;
        display.curveAngleDegrees = curveAngleDegrees;
        display.columnSegments = columnSegments;
        display.concave = concave;
        display.verticalPivot = verticalPivot;
        display.sourcePanel = transform;
        display.renderTextureSize = renderTextureSize;
        if (curvedMaterial != null)
            display.curvedMaterial = curvedMaterial;

        // CurvedCanvasDisplay.WireSiblingInteractor() will populate the interactor's
        // targetCanvas / uiCamera once the canvas is captured.
        _curvedDisplayGo.AddComponent<CurvedCanvasInteractor>();

        ResolveSharedInputBindings();

        var forwarder = _curvedDisplayGo.AddComponent<CurvedPanelInputForwarder>();
        forwarder.curvedPanel = _curvedDisplayGo.GetComponent<CurvedCanvasInteractor>();
        forwarder.rayOrigin = rayOrigin;
        forwarder.triggerAction = triggerAction;
        forwarder.maxDistance = rayMaxDistance;
        forwarder.layerMask = layerMask;
    }

    void ResolveSharedInputBindings()
    {
        if (rayOrigin != null && triggerAction != null) return;

        // Borrow controller bindings from any existing CurvedPanelInputForwarder in the scene
        // (e.g. the one already wired on the prompt CurvedImagePanel) so the user only has to
        // configure XR input once across all curved panels.
        var existing = FindObjectOfType<CurvedPanelInputForwarder>(includeInactive: true);
        if (existing == null) return;
        if (rayOrigin == null) rayOrigin = existing.rayOrigin;
        if (triggerAction == null) triggerAction = existing.triggerAction;
    }
}

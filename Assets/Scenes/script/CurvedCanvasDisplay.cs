using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Renders a UI <see cref="Canvas"/> into a <see cref="RenderTexture"/> via a dedicated
/// off-screen UI camera, then displays that RT on a curved (cylindrical-section) mesh.
///
/// Architecture:
///   1. The source Canvas is reparented under an off-screen "UI camera" and switched to
///      <see cref="RenderMode.ScreenSpaceCamera"/> so the camera draws it at a known
///      pixel size onto <see cref="renderTexture"/>.
///   2. The UI camera is parked far below the scene (<see cref="offscreenAnchorPosition"/>)
///      so the main / HMD camera never sees it directly.
///   3. The curved mesh sits at this GameObject's transform and samples the RT through
///      <c>SpatialFind/CurvedCanvasUnlit</c> (or any unlit/transparent material you supply).
///
/// IMPORTANT: do NOT assign <see cref="Canvas.worldCamera"/> to the rig / HMD camera
/// (e.g. by leaving <c>WorldSpaceCanvasSpawnOnce</c> active on the same object). Setting
/// the rig camera's <c>targetTexture</c> would redirect the player's view into the RT.
/// This component disables <c>WorldSpaceCanvasSpawnOnce</c> / <c>CanvasSpatialAnchor</c>
/// found on the source canvas so they cannot fight back.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
public class CurvedCanvasDisplay : MonoBehaviour
{
    [Header("Mesh shape")]
    [Tooltip("Width of the curved panel along the arc (meters).")]
    public float width = 1.5f;

    [Tooltip("Height of the curved panel (meters).")]
    public float height = 0.9f;

    [Range(5f, 90f)]
    [Tooltip("Total arc subtended by the panel, in degrees. Larger = more wrap.")]
    public float curveAngleDegrees = 25f;

    [Range(4, 128)]
    public int columnSegments = 32;

    [Tooltip("True = wrap toward the viewer (concave, like a curved monitor). False = bulge away (convex).")]
    public bool concave = true;

    public enum VerticalPivot { Center, Bottom, Top }

    [Tooltip("Where on the mesh local Y=0 lies. Center = mesh straddles 0. Bottom = mesh sits on transform y. Top = mesh hangs from transform y. Use Bottom on stage panels you want bottom-aligned.")]
    public VerticalPivot verticalPivot = VerticalPivot.Center;

    [Header("Capture targets")]
    [Tooltip("Source UI Canvas to capture. If left null, the first child Canvas of sourcePanel will be used at runtime.")]
    public Canvas targetCanvas;

    [Tooltip("Optional fallback root: at runtime, the first Canvas child found here will be captured.\nUseful when the Canvas is created at Awake (e.g. ImageGridPanel).")]
    public Transform sourcePanel;

    [Tooltip("Off-screen camera that renders the source canvas into renderTexture. If null, one is created automatically as a child of this object.")]
    public Camera uiCamera;

    [Tooltip("Render texture the UI camera draws into and that the curved mesh samples. If null, one is created at runtime.")]
    public RenderTexture renderTexture;

    [Tooltip("Material applied to the curved mesh. If null, an Unlit/Transparent material using SpatialFind/CurvedCanvasUnlit is created.")]
    public Material curvedMaterial;

    [Header("Capture options")]
    [Tooltip("Resolution of the auto-created render texture. Ignored if a renderTexture asset is assigned.")]
    public Vector2Int renderTextureSize = new Vector2Int(2048, 1152);

    [Tooltip("Distance from UI camera to the captured canvas plane (Screen Space - Camera).")]
    public float canvasPlaneDistance = 1f;

    [Tooltip("World position to park the off-screen UI camera so neither the main camera nor the user can see the flat canvas.")]
    public Vector3 offscreenAnchorPosition = new Vector3(0f, -1000f, 0f);

    [Tooltip("Tint applied to the curved mesh material (multiplied with the RT).")]
    public Color tint = Color.white;

    [Header("Debug")]
    [Tooltip("Logs each setup phase and any reasons setup is deferred.")]
    public bool verboseLogging = false;

    bool _setupDone;

    void OnEnable()
    {
        StartCoroutine(SetupWhenReady());
    }

    IEnumerator SetupWhenReady()
    {
        // Wait one frame so peer Awakes (e.g. ImageGridPanel.BuildShellIfNeeded) finish
        // creating the runtime Canvas before we capture it.
        yield return null;

        ResolveTargetCanvas();

        // If the canvas isn't there yet (e.g. panel disabled), poll a few frames before giving up.
        int safety = 60;
        while (targetCanvas == null && safety-- > 0)
        {
            yield return null;
            ResolveTargetCanvas();
        }

        if (targetCanvas == null)
        {
            Debug.LogError($"[CurvedCanvasDisplay] No targetCanvas found on '{name}'. " +
                           "Assign 'targetCanvas' or 'sourcePanel' (with a child Canvas).");
            yield break;
        }

        SuppressWorldSpaceCanvasHelpers(targetCanvas.gameObject);
        SetupRenderTexture();
        SetupUiCamera();
        ReparentAndConfigureCanvas();
        EnsureGraphicRaycasters();
        SetupMaterial();
        BuildMesh();
        WireSiblingInteractor();

        _setupDone = true;
        if (verboseLogging)
            Debug.Log($"[CurvedCanvasDisplay] Setup complete on '{name}'. RT={renderTexture.width}x{renderTexture.height}, canvas='{targetCanvas.name}'.");
    }

    /// <summary>
    /// Ensures the captured Canvas (and every nested sub-Canvas) has an enabled
    /// <see cref="GraphicRaycaster"/>. Without one, <see cref="CurvedCanvasInteractor"/>
    /// can't dispatch UI pointer events. Note: <c>TrackedDeviceGraphicRaycaster</c> from
    /// XR Interaction Toolkit derives from <c>BaseRaycaster</c> (not <c>GraphicRaycaster</c>)
    /// and does not satisfy this requirement, so we add the standard one when missing.
    /// </summary>
    void EnsureGraphicRaycasters()
    {
        var rootRaycaster = targetCanvas.GetComponent<GraphicRaycaster>();
        if (rootRaycaster == null)
            rootRaycaster = targetCanvas.gameObject.AddComponent<GraphicRaycaster>();
        rootRaycaster.enabled = true;

        var subCanvases = targetCanvas.GetComponentsInChildren<Canvas>(includeInactive: true);
        for (int i = 0; i < subCanvases.Length; i++)
        {
            var sub = subCanvases[i];
            if (sub == targetCanvas) continue;
            var gr = sub.GetComponent<GraphicRaycaster>();
            if (gr == null)
                gr = sub.gameObject.AddComponent<GraphicRaycaster>();
            gr.enabled = true;
        }
    }

    /// <summary>
    /// If a <see cref="CurvedCanvasInteractor"/> is on this GameObject, fill in its targetCanvas
    /// and uiCamera so the user doesn't have to wire them by hand.
    /// </summary>
    void WireSiblingInteractor()
    {
        var interactor = GetComponent<CurvedCanvasInteractor>();
        if (interactor == null) return;
        if (interactor.targetCanvas == null) interactor.targetCanvas = targetCanvas;
        if (interactor.uiCamera == null) interactor.uiCamera = uiCamera;
    }

    void ResolveTargetCanvas()
    {
        if (targetCanvas != null) return;
        if (sourcePanel == null) return;
        targetCanvas = sourcePanel.GetComponentInChildren<Canvas>(includeInactive: true);
    }

    /// <summary>
    /// Disables components that would re-anchor the captured canvas in world space and re-bind
    /// its <see cref="Canvas.worldCamera"/> to the rig camera (which would clobber the headset
    /// view if its targetTexture were also assigned).
    /// </summary>
    static void SuppressWorldSpaceCanvasHelpers(GameObject canvasGo)
    {
        foreach (MonoBehaviour mb in canvasGo.GetComponents<MonoBehaviour>())
        {
            if (mb == null) continue;
            string typeName = mb.GetType().Name;
            if (typeName == "WorldSpaceCanvasSpawnOnce" || typeName == "CanvasSpatialAnchor")
                mb.enabled = false;
        }
    }

    void SetupRenderTexture()
    {
        if (renderTexture != null)
        {
            if (!renderTexture.IsCreated()) renderTexture.Create();
            return;
        }

        renderTexture = new RenderTexture(
            Mathf.Max(64, renderTextureSize.x),
            Mathf.Max(64, renderTextureSize.y),
            24,
            RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.sRGB)
        {
            name = $"{name}_CanvasRT",
            useMipMap = false,
            autoGenerateMips = false,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            antiAliasing = 1,
        };
        renderTexture.Create();
    }

    void SetupUiCamera()
    {
        if (uiCamera == null)
        {
            var camGo = new GameObject($"{name}_UICamera");
            camGo.transform.SetParent(transform, worldPositionStays: false);
            uiCamera = camGo.AddComponent<Camera>();
        }

        uiCamera.transform.position = offscreenAnchorPosition;
        uiCamera.transform.rotation = Quaternion.identity;
        uiCamera.orthographic = false;
        uiCamera.fieldOfView = 60f;
        uiCamera.clearFlags = CameraClearFlags.SolidColor;
        uiCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
        uiCamera.cullingMask = ~0;
        uiCamera.nearClipPlane = 0.01f;
        uiCamera.farClipPlane = canvasPlaneDistance + 100f;
        uiCamera.depth = -100f;
        uiCamera.allowHDR = false;
        uiCamera.allowMSAA = false;
        uiCamera.useOcclusionCulling = false;
        uiCamera.targetTexture = renderTexture;
        uiCamera.stereoTargetEye = StereoTargetEyeMask.None;

        AudioListener listener = uiCamera.GetComponent<AudioListener>();
        if (listener != null) Destroy(listener);
    }

    void ReparentAndConfigureCanvas()
    {
        Transform canvasTr = targetCanvas.transform;

        // Park canvas under the UI camera so it travels with it and stays out of the main scene view.
        canvasTr.SetParent(uiCamera.transform, worldPositionStays: false);
        canvasTr.localPosition = Vector3.zero;
        canvasTr.localRotation = Quaternion.identity;
        // Reparenting with worldPositionStays=false keeps the previous localScale. Source
        // world-space canvases often use 0.01 so pixels map to meters; left that way, the
        // capture would be a tiny island on the render texture. Force unit scale for RT fill.
        canvasTr.localScale = Vector3.one;

        if (canvasTr is RectTransform rootRt && renderTexture != null)
        {
            rootRt.anchorMin = Vector2.zero;
            rootRt.anchorMax = Vector2.zero;
            rootRt.pivot = new Vector2(0.5f, 0.5f);
            rootRt.anchoredPosition = Vector2.zero;
            rootRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, renderTexture.width);
            rootRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, renderTexture.height);
        }

        targetCanvas.renderMode = RenderMode.ScreenSpaceCamera;
        targetCanvas.worldCamera = uiCamera;
        targetCanvas.planeDistance = canvasPlaneDistance;
        targetCanvas.sortingOrder = 0;
        targetCanvas.overrideSorting = false;
    }

    void SetupMaterial()
    {
        Material src = curvedMaterial;
        if (src == null)
        {
            Shader sh = Shader.Find("SpatialFind/CurvedCanvasUnlit");
            if (sh == null) sh = Shader.Find("Unlit/Transparent");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh == null)
            {
                Debug.LogError("[CurvedCanvasDisplay] No suitable unlit/transparent shader available.");
                return;
            }
            src = new Material(sh);
        }

        var instance = new Material(src) { name = $"{name}_CurvedMatInstance" };
        instance.mainTexture = renderTexture;
        if (instance.HasProperty("_MainTex")) instance.SetTexture("_MainTex", renderTexture);
        if (instance.HasProperty("_Color")) instance.SetColor("_Color", tint);
        GetComponent<MeshRenderer>().sharedMaterial = instance;
    }

    void BuildMesh()
    {
        int cols = Mathf.Max(2, columnSegments) + 1;
        var verts = new Vector3[cols * 2];
        var uvs = new Vector2[cols * 2];
        var tris = new int[(cols - 1) * 6];

        float angleRad = Mathf.Max(curveAngleDegrees, 0.1f) * Mathf.Deg2Rad;
        float radius = width / angleRad;
        float zSign = concave ? -1f : 1f;

        // yMin / yMax control where local Y=0 sits relative to the mesh, so panels with
        // different heights can share an aligned bottom (or top) at their transform.position.
        float yMin, yMax;
        switch (verticalPivot)
        {
            case VerticalPivot.Bottom: yMin = 0f;            yMax = height;      break;
            case VerticalPivot.Top:    yMin = -height;       yMax = 0f;          break;
            default:                   yMin = -height * 0.5f; yMax = height * 0.5f; break;
        }

        for (int c = 0; c < cols; c++)
        {
            float t = c / (float)(cols - 1);
            float angle = (t - 0.5f) * angleRad;
            float x = Mathf.Sin(angle) * radius;
            float z = zSign * (radius - Mathf.Cos(angle) * radius);

            verts[c]        = new Vector3(x, yMin, z);
            verts[c + cols] = new Vector3(x, yMax, z);

            uvs[c]          = new Vector2(t, 0f);
            uvs[c + cols]   = new Vector2(t, 1f);
        }

        // Front-face winding: pick the order that puts normals toward -Z when concave (toward
        // a viewer at -Z) and toward +Z when convex. Our shader is Cull Off so both faces
        // render anyway; this just keeps RecalculateNormals + lighting / picking sane.
        int tri = 0;
        for (int c = 0; c < cols - 1; c++)
        {
            int bl = c, br = c + 1, tl = c + cols, tr = c + cols + 1;
            if (concave)
            {
                tris[tri++] = bl; tris[tri++] = tl; tris[tri++] = tr;
                tris[tri++] = bl; tris[tri++] = tr; tris[tri++] = br;
            }
            else
            {
                tris[tri++] = bl; tris[tri++] = tr; tris[tri++] = tl;
                tris[tri++] = bl; tris[tri++] = br; tris[tri++] = tr;
            }
        }

        var mesh = new Mesh { name = $"{name}_CurvedMesh" };
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        GetComponent<MeshFilter>().sharedMesh = mesh;
        GetComponent<MeshCollider>().sharedMesh = mesh;
    }

    void OnDisable()
    {
        if (uiCamera != null) uiCamera.targetTexture = null;
    }

    void OnDestroy()
    {
        if (renderTexture != null && !AssetIsExternalRT())
        {
            renderTexture.Release();
        }
    }

    bool AssetIsExternalRT()
    {
#if UNITY_EDITOR
        return UnityEditor.AssetDatabase.Contains(renderTexture);
#else
        return true;
#endif
    }
}

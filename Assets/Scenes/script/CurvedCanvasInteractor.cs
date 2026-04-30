using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Translates a physics raycast hit on the curved mesh into UI events on the captured canvas.
/// The mesh's <see cref="MeshCollider"/> must use the same mesh that <see cref="CurvedCanvasDisplay"/>
/// generates so <see cref="RaycastHit.textureCoord"/> is valid.
///
/// Coordinate flow:
///   physics ray  ->  hit.textureCoord (UV in [0,1])
///                 ->  local point on captured Canvas RectTransform
///                 ->  world point on Canvas plane
///                 ->  UI-camera screen point (= RT pixel)
///                 ->  GraphicRaycaster on the canvas (and any sub-canvases)
/// </summary>
[RequireComponent(typeof(MeshCollider))]
public class CurvedCanvasInteractor : MonoBehaviour
{
    [Tooltip("Canvas captured by CurvedCanvasDisplay.")]
    public Canvas targetCanvas;

    [Tooltip("Optional: explicit UI camera used to render the canvas. If null, falls back to targetCanvas.worldCamera.")]
    public Camera uiCamera;

    readonly List<RaycastResult> _scratch = new List<RaycastResult>();
    GameObject _hovered;

    static readonly List<Graphic> s_graphicBuf = new List<Graphic>();
    MeshCollider _meshCollider;

    void Awake()
    {
        _meshCollider = GetComponent<MeshCollider>();
    }

    void Update()
    {
        // Disable our mesh collider while the captured canvas has no active raycast target
        // (e.g. the prompt panel after PromptWindow is hidden). Otherwise this curved mesh
        // would still be the closest hit for the controller ray, swallowing clicks meant for
        // visible curved panels behind it.
        if (targetCanvas == null || _meshCollider == null) return;
        s_graphicBuf.Clear();
        targetCanvas.GetComponentsInChildren<Graphic>(includeInactive: false, s_graphicBuf);
        bool anyActiveRaycastTarget = false;
        for (int i = 0; i < s_graphicBuf.Count; i++)
        {
            if (s_graphicBuf[i].raycastTarget)
            {
                anyActiveRaycastTarget = true;
                break;
            }
        }
        if (_meshCollider.enabled != anyActiveRaycastTarget)
            _meshCollider.enabled = anyActiveRaycastTarget;
    }

    Camera ResolveCamera()
    {
        if (uiCamera != null) return uiCamera;
        if (targetCanvas != null) return targetCanvas.worldCamera;
        return null;
    }

    /// <summary>Call from your XR forwarder each frame with the current hit and click state.</summary>
    public void OnRayHit(RaycastHit hit, bool clicked)
    {
        if (targetCanvas == null) return;
        SimulatePointer(hit.textureCoord, clicked);
    }

    /// <summary>Call when no ray currently hits the curved mesh so hover state clears cleanly.</summary>
    public void OnRayMiss()
    {
        ClearHover();
    }

    void SimulatePointer(Vector2 uv, bool clicked)
    {
        Camera cam = ResolveCamera();
        if (cam == null || EventSystem.current == null) return;

        var canvasRt = targetCanvas.GetComponent<RectTransform>();
        if (canvasRt == null) return;

        // UV (0..1) -> local space within the canvas RectTransform.
        Rect rect = canvasRt.rect;
        float localX = (uv.x - 0.5f) * rect.width;
        float localY = (uv.y - 0.5f) * rect.height;
        Vector3 worldPos = canvasRt.TransformPoint(localX, localY, 0f);
        Vector2 screenPos = RectTransformUtility.WorldToScreenPoint(cam, worldPos);

        var pointerData = new PointerEventData(EventSystem.current)
        {
            position = screenPos,
            button = PointerEventData.InputButton.Left,
        };

        // The canvas may have nested sub-Canvases (header / grid / footer / dim overlay), each
        // with its own GraphicRaycaster. Querying every raycaster ensures we catch Buttons on
        // any sub-Canvas; the root raycaster only sees graphics on its own Canvas.
        _scratch.Clear();
        var raycasters = targetCanvas.GetComponentsInChildren<GraphicRaycaster>();
        foreach (var raycaster in raycasters)
            raycaster.Raycast(pointerData, _scratch);

        GameObject newTarget = _scratch.Count > 0 ? _scratch[0].gameObject : null;

        if (newTarget != _hovered)
        {
            if (_hovered != null)
                ExecuteEvents.Execute(_hovered, pointerData, ExecuteEvents.pointerExitHandler);
            if (newTarget != null)
                ExecuteEvents.Execute(newTarget, pointerData, ExecuteEvents.pointerEnterHandler);
            _hovered = newTarget;
        }

        if (clicked && newTarget != null)
        {
            // The hit target is usually a child Graphic (e.g. a Button "Text" label or a
            // TMP_InputField placeholder). ExecuteHierarchy walks up parents to find the
            // actual handler (Button, TMP_InputField, etc.) - same approach as Unity's
            // StandaloneInputModule. ExecuteEvents.Execute alone would drop the click.
            var downHandler = ExecuteEvents.ExecuteHierarchy(newTarget, pointerData, ExecuteEvents.pointerDownHandler);
            pointerData.pointerPress = downHandler;
            pointerData.rawPointerPress = downHandler;
            ExecuteEvents.ExecuteHierarchy(newTarget, pointerData, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.ExecuteHierarchy(newTarget, pointerData, ExecuteEvents.pointerClickHandler);

            // Selectables (Button, TMP_InputField) need selection so they receive focus.
            var selectHandler = ExecuteEvents.GetEventHandler<ISelectHandler>(newTarget);
            if (selectHandler != null)
                EventSystem.current.SetSelectedGameObject(selectHandler, pointerData);
        }
    }

    void ClearHover()
    {
        if (_hovered == null) return;
        var pointerData = new PointerEventData(EventSystem.current);
        ExecuteEvents.Execute(_hovered, pointerData, ExecuteEvents.pointerExitHandler);
        _hovered = null;
    }

    void OnDisable()
    {
        ClearHover();
    }
}

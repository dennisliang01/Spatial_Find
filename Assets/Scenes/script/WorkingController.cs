using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;

namespace Scenes.script
{
    public class WorkingController : MonoBehaviour
    {
        public bool isLeftController = true;

        [SerializeField]
        [Tooltip("If unset, resolved once at runtime (same flow as PCInputController).")]
        private ClipSearchFlowController clipSearchFlowController;

        private LineRenderer laser;
        private bool triggerWasPressed = false;

        void Awake()
        {
            if (clipSearchFlowController == null)
                clipSearchFlowController = FindObjectOfType<ClipSearchFlowController>();
        }

        void Start()
        {
            try
            {
                // XRInteractorLineVisual owns the LineRenderer on this object; do not replace it.
                if (GetComponent<XRInteractorLineVisual>() != null)
                {
                    laser = null;
                    Debug.Log((isLeftController ? "Left" : "Right") +
                        " WorkingController: using XRInteractorLineVisual ray (custom laser skipped).");
                    return;
                }

                laser = GetComponent<LineRenderer>();
                if (laser == null)
                    laser = gameObject.AddComponent<LineRenderer>();

                if (laser != null)
                {
                    laser.positionCount = 2;
                    laser.startWidth = 0.01f;
                    laser.endWidth = 0.01f;

                    Shader shader = Shader.Find("Sprites/Default")
                        ?? Shader.Find("Standard")
                        ?? Shader.Find("Unlit/Color")
                        ?? Shader.Find("Universal Render Pipeline/Unlit");
                    if (shader != null)
                    {
                        laser.material = new Material(shader);
                    }
                    laser.startColor = Color.green;
                    laser.endColor = Color.red;

                    Debug.Log((isLeftController ? "Left" : "Right") + " Working Controller Ready with Laser");
                }
                else
                {
                    Debug.LogWarning("WorkingController: LineRenderer not available on this object; laser disabled.");
                }
            }
            catch (System.Exception e)
            {
                laser = null;
                Debug.LogWarning("WorkingController: Could not create laser: " + e.Message);
            }
        }

        void Update()
        {
            /* 
             * Let XR Interaction Toolkit handle controller tracking instead of doing it manually 
             * ActionBasedController already does this.
             */
            // UpdateControllerTracking();

            if (laser != null)
            {
                DrawLaser();
            }

            CheckForInput();
        }

        void UpdateControllerTracking()
        {
            InputDevice device = GetInputDevice();

            if (device.isValid)
            {
                if (device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 position))
                {
                    if (transform.parent != null)
                    {
                        transform.localPosition = position;
                    }
                    else
                    {
                        transform.position = position;
                    }
                }

                if (device.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rotation))
                {
                    if (transform.parent != null)
                        transform.localRotation = rotation;
                    else
                        transform.rotation = rotation;
                }
            }
            else
            {
                transform.localPosition =
                    isLeftController ? new Vector3(-0.2f, -0.1f, 0.5f) : new Vector3(0.2f, -0.1f, 0.5f);
            }
        }

        void DrawLaser()
        {
            if (laser == null) return;

            try
            {
                laser.SetPosition(0, transform.position);
                laser.SetPosition(1, transform.position + transform.forward * 5f);
            }
            catch (System.Exception e)
            {
                Debug.LogError("Error drawing laser: " + e.Message);
            }
        }

        void CheckForInput()
        {
            InputDevice device = GetInputDevice();
            if (!device.isValid)
            {
                Debug.LogWarning((isLeftController ? "Left" : "Right") + " controller not detected");
                return;
            }

            bool pressed = IsTriggerPressed(device);
            // Fire on release (falling edge), matching the XR Interaction Toolkit's pointer-click
            // convention. Firing on press would change the active panel mid-pull, causing XRI's
            // release event to land on a different target (e.g. the new panel's "back to prompt"
            // button) and fire a second, unwanted click.
            if (!pressed && triggerWasPressed)
            {
                Debug.Log((isLeftController ? "Left" : "Right") + " TRIGGER RELEASED!");
                ShootRaycast();
            }
            triggerWasPressed = pressed;
        }

        /// <summary>
        /// Robust trigger detection: combines the boolean <c>triggerButton</c> (which only latches
        /// near a full pull on many headsets) with the analog <c>trigger</c> float at a 0.5
        /// threshold so partial pulls register reliably.
        /// </summary>
        static bool IsTriggerPressed(InputDevice device)
        {
            bool btn = false;
            device.TryGetFeatureValue(CommonUsages.triggerButton, out btn);
            float val = 0f;
            device.TryGetFeatureValue(CommonUsages.trigger, out val);
            return btn || val > 0.5f;
        }

        void ShootRaycast()
        {
            GameObject hit = GetRaycastTarget();
            if (hit == null) return;
            HandleHit(hit);
        }

        GameObject GetRaycastTarget()
        {
            Vector3 origin = transform.position;
            Vector3 dir = transform.forward;
            float maxDist = 50f;

            Debug.DrawRay(origin, dir * maxDist, Color.red, 1f);
            Debug.Log($"[Ray] origin={origin}, forward={dir}, maxDist={maxDist}");

            RaycastHit[] hits = Physics.RaycastAll(origin, dir, maxDist, ~0, QueryTriggerInteraction.Collide);

            if (hits.Length == 0)
            {
                Debug.Log("RaycastAll: no hits");
                return null;
            }

            List<RaycastHit> validHits = new List<RaycastHit>();
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                var go = h.collider.gameObject;

                if (go.name.Contains("Controller") || go.name.Contains("Hand"))
                {
                    continue;
                }

                Debug.Log($"hit[{i}] name={go.name}, dist={h.distance}, hitPoint={h.point}, layer={LayerMask.LayerToName(go.layer)}, isTrigger={h.collider.isTrigger}");
                Debug.Log($"   transform.pos={go.transform.position}, transform.parent={(go.transform.parent ? go.transform.parent.name : "null")}");

                validHits.Add(h);

                GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                marker.transform.position = h.point;
                marker.transform.localScale = Vector3.one * 0.05f;
                Destroy(marker.GetComponent<Collider>());
                Destroy(marker, 2f);
            }

            if (validHits.Count == 0)
            {
                Debug.Log("No valid hits after filtering");
                return null;
            }

            validHits.Sort((a, b) => a.distance.CompareTo(b.distance));
            float minDistance = validHits[0].distance;
            // Stacked world-space CLIP panels each have header navigate BoxColliders; the closest hit can
            // be a *rear* panel's slab even when the user aims at the front panel's CloseToStart. Prefer any
            // CloseToStart that lies within a small window behind the closest hit along the ray.
            const float closeBehindSlabToleranceMeters = 0.35f;
            RaycastHit chosen = validHits[0];
            float bestCloseDist = float.MaxValue;
            foreach (RaycastHit h in validHits)
            {
                if (!string.Equals(h.collider.gameObject.name, "CloseToStart", StringComparison.Ordinal))
                    continue;
                if (h.distance <= minDistance + closeBehindSlabToleranceMeters && h.distance < bestCloseDist)
                {
                    bestCloseDist = h.distance;
                    chosen = h;
                }
            }

            return chosen.collider.gameObject;
        }

        void HandleHit(GameObject hit)
        {
            if (TryHandleInputFieldRedirect(hit))
                return;

            Debug.Log($"Processing interaction with: {hit.name}");

            if (TryHandleSubPanel(hit))
                return;

            if (TryHandleMeshController(hit))
                return;

            if (TryHandleImageTile(hit))
                return;

            if (TryHandleInputField(hit))
                return;

            if (TryHandleButton(hit))
                return;

            Debug.LogWarning($"No MeshController or ImageTile on {hit.name}");
        }

        bool TryHandleInputFieldRedirect(GameObject hit)
        {
            // A click on the prompt's TMP_InputField runs the configured submit redirect (Search
            // button) instead of focusing the field. Mirrors the curved-canvas path in
            // CurvedCanvasInteractor so both rendering modes behave the same.
            TMP_InputField hitField = hit.GetComponentInParent<TMP_InputField>();
            if (hitField != null)
            {
                TmpInputFieldXrPointerFocus focus = hitField.GetComponent<TmpInputFieldXrPointerFocus>();
                if (focus != null && focus.TryInvokeXrClickRedirect())
                    return true;
            }

            return false;
        }

        bool TryHandleSubPanel(GameObject hit)
        {
            // moved the subpanel handleing logic here 
            SubPanelController subPanel = hit.GetComponent<SubPanelController>();
            if (subPanel != null)
            {
                subPanel.SelectPanel();

                MeshController mainPanel = FindMainPanel(hit.transform);
                if (mainPanel != null)
                {
                    string subpanelPath = subPanel.GetFolderPath();
                    string subDisplayPath = subPanel.GetDisplayPath();
                    Debug.Log($"Subpanel path: '{subpanelPath}', Main panel current path: '{mainPanel.folderPath}'");

                    if (mainPanel.hasChild)
                        mainPanel.RemoveChildPlane();

                    mainPanel.folderPath = subpanelPath;
                    mainPanel.displayPath = subDisplayPath;
                    Debug.Log($"Setting main panel path to: {subpanelPath}");
                    Debug.Log($"Setting main display path to: {subDisplayPath}");
                    mainPanel.SpawnChildPlane();
                }
                else
                {
                    Debug.LogWarning("No main panel found for subpanel");
                }
                return true;
            }

            return false;
        }

        bool TryHandleMeshController(GameObject hit)
        {
            // Handle main panel
            MeshController meshController = hit.GetComponent<MeshController>();
            if (meshController != null)
            {
                if (!meshController.hasChild)
                {
                    Debug.Log("Select a subpanel to proceed");
                }
                else
                {
                    meshController.RemoveChildPlane();
                }
                return true;
            }

            return false;
        }

        bool TryHandleImageTile(GameObject hit)
        {
            ImageTile imageTile = hit.GetComponent<ImageTile>();
            if (imageTile == null)
                imageTile = hit.GetComponentInParent<ImageTile>();

            if (imageTile != null && !string.IsNullOrEmpty(imageTile.imageId))
            {
                if (clipSearchFlowController != null)
                {
                    ImageGridPanel grid = imageTile.GetComponentInParent<ImageGridPanel>();
                    clipSearchFlowController.OnUserPickedImageFromPanel(grid, imageTile.imageId);
                }
                else
                {
                    Debug.LogWarning("WorkingController: ClipSearchFlowController not found; cannot select image.");
                }

                return true;
            }

            return false;
        }

        bool TryHandleInputField(GameObject hit)
        {
            TMP_InputField tmpInput = hit.GetComponent<TMP_InputField>()
                ?? hit.GetComponentInParent<TMP_InputField>();
            if (tmpInput != null)
            {
                if (tmpInput.interactable)
                {
                    EventSystem es = EventSystem.current;
                    if (es != null)
                        es.SetSelectedGameObject(tmpInput.gameObject);
                    tmpInput.ActivateInputField();
                }
                return true;
            }

            return false;
        }

        bool TryHandleButton(GameObject hit)
        {
            Button uiButton = hit.GetComponent<Button>()
                ?? hit.GetComponentInParent<Button>();
            if (uiButton != null)
            {
                if (uiButton.interactable)
                    uiButton.onClick.Invoke();
                return true;
            }

            return false;
        }

        InputDevice GetInputDevice()
        {
            return InputDevices.GetDeviceAtXRNode(isLeftController ? XRNode.LeftHand : XRNode.RightHand);
        }

        MeshController FindMainPanel(Transform start)
        {
            Transform current = start;
            while (current != null)
            {
                MeshController mc = current.GetComponent<MeshController>();
                if (mc != null && mc.GetComponent<SubPanelController>() == null)
                {
                    return mc;
                }
                current = current.parent;
            }
            return null;
        }
    }
}

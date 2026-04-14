using UnityEngine;
using UnityEngine.XR;
using UnityEngine.UI;
using System.Collections.Generic;

namespace Scenes.script
{
    public class WorkingController : MonoBehaviour
    {
        public bool isLeftController = true;
        private LineRenderer laser;
        private bool triggerWasPressed = false;

        void Start()
        {
            try
            {
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

            /* Caused program to crash with VIVE Focus Vision */
            // if (Input.GetKeyDown(KeyCode.T))
            // {
            //     Vector3 camOrigin = Camera.main.transform.position;
            //     Vector3 camDir = (new Vector3(0, 4, 10) - camOrigin).normalized; 
            //     Debug.DrawRay(camOrigin, camDir * 30f, Color.blue, 2f);
            //     if (Physics.Raycast(camOrigin, camDir, out RaycastHit h, 30f))
            //     {
            //         Debug.Log("Camera ray hit: " + h.collider.name);
            //     }
            //     else
            //     {
            //         Debug.Log("Camera ray hit nothing");
            //     }
            // }

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
                // Ray from controller pose: origin + aim direction follow this transform (XR rig / attach point).
                Vector3 rayDirection = transform.forward;
                laser.SetPosition(0, transform.position);
                laser.SetPosition(1, transform.position + rayDirection * 25f);
            }
            catch (System.Exception e)
            {
                Debug.LogError("Error drawing laser: " + e.Message);
            }
        }

        void CheckForInput()
        {
            InputDevice device = GetInputDevice();
            if (device.isValid)
            {
                // Check for trigger press
                if (device.TryGetFeatureValue(CommonUsages.triggerButton, out bool triggerPressed))
                {
                    if (triggerPressed && !triggerWasPressed)
                    {
                        Debug.Log((isLeftController ? "Left" : "Right") + " TRIGGER PRESSED!");
                        ShootRaycast();
                    }
                    triggerWasPressed = triggerPressed;
                }

                /* Unneeded */
                // Also check trigger value (analog)
                // if (device.TryGetFeatureValue(CommonUsages.trigger, out float triggerValue))
                // {
                //     if (triggerValue > 0.1f) 
                //     {
                //         Debug.Log((isLeftController ? "Left" : "Right") + " TRIGGER VALUE: " + triggerValue);
                //     }
                // }

                if (device.TryGetFeatureValue(CommonUsages.primaryButton, out bool primary) && primary)
                {
                    Debug.Log((isLeftController ? "Left" : "Right") + " PRIMARY BUTTON!");
                }
            }
            else
            {
                Debug.LogWarning((isLeftController ? "Left" : "Right") + " controller not detected");
            }
        }

        void ShootRaycast()
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
                return;
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
                return;
            }

            validHits.Sort((a, b) => a.distance.CompareTo(b.distance));
            RaycastHit chosen = validHits[0];
            GameObject hitObject = chosen.collider.gameObject;

            Debug.Log($"Processing interaction with: {hitObject.name}");

            // Check for Button component (UI buttons in image grid)
            Button btn = hitObject.GetComponent<Button>();
            if (btn != null && btn.interactable)
            {
                Debug.Log($"Clicking button: {hitObject.name}");
                btn.onClick.Invoke();
                return;
            }

            // moved the subpanel handleing logic here
            SubPanelController subPanel = hitObject.GetComponent<SubPanelController>();
            if (subPanel != null)
            {
                subPanel.SelectPanel();

                MeshController mainPanel = FindMainPanel(hitObject.transform);
                if (mainPanel != null)
                {
                    string subpanelPath = subPanel.GetFolderPath();
                    string subDisplayPath = subPanel.GetDisplayPath();
                    Debug.Log($"Subpanel path: '{subpanelPath}', Main panel current path: '{mainPanel.folderPath}'");

                    if (!mainPanel.hasChild)
                    {
                        mainPanel.folderPath = subpanelPath;
                        mainPanel.displayPath = subDisplayPath;
                        Debug.Log($"Setting main panel path to: {subpanelPath}");
                        Debug.Log($"Setting main display path to: {subDisplayPath}");
                        mainPanel.SpawnChildPlane();
                    }
                    else
                    {
                        mainPanel.RemoveChildPlane();
                    }
                }
                else
                {
                    Debug.LogWarning("No main panel found for subpanel");
                }
                return;
            }

            // Handle main panel
            MeshController meshController = hitObject.GetComponent<MeshController>();
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
            }
            else
            {
                Debug.LogWarning($"No MeshController found on {hitObject.name}");
            }
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
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.XR.CoreUtils;
using UnityEngine.XR;

namespace Scenes.script
{
    /// <summary>
    /// Places a world-space canvas once in front of the HMD, then leaves it fixed in the scene.
    /// Attach to the same GameObject as <see cref="Canvas"/>. Do not parent the canvas under XR Origin or the camera.
    /// </summary>
    [RequireComponent(typeof(Canvas))]
    public class WorldSpaceCanvasSpawnOnce : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Horizontal distance in meters from the head to the canvas center (when using horizontal forward).")]
        float distanceMeters = 3.5f;

        [SerializeField]
        bool detachFromParentOnAwake = true;

        [SerializeField]
        bool faceUserOnSpawn = true;

        [SerializeField]
        bool yawOnly = true;

        [SerializeField]
        [Tooltip("If true, offset uses forward projected on the XZ plane so walking space matches perceived distance; avoids placing the panel much closer when the user looks down.")]
        bool placementAlongHorizontalPlane = true;

        [SerializeField]
        [Tooltip("Max frames to wait for XR display before placing anyway.")]
        int maxFramesWaitForXr = 120;

        bool _hasPlaced;

        /// <summary>True after the one-time world placement completed successfully (rig camera found and pose applied).</summary>
        public bool HasPlaced => _hasPlaced;

        /// <summary>Invoked once when <see cref="HasPlaced"/> becomes true.</summary>
        public event Action Placed;

        Canvas _canvas;

        void Awake()
        {
            _canvas = GetComponent<Canvas>();
            if (detachFromParentOnAwake)
                transform.SetParent(null, worldPositionStays: true);
        }

        void Start()
        {
            StartCoroutine(PlaceOnceWhenRigReady());
        }

        /// <summary>Call immediately after <see cref="GameObject.AddComponent{T}"/> if a builder (e.g. ImageGridPanel) should set distance before <see cref="Start"/> runs.</summary>
        public void OverrideSpawnDistance(float meters)
        {
            if (meters > 0.05f)
                distanceMeters = meters;
        }

        IEnumerator PlaceOnceWhenRigReady()
        {
            yield return null;
            yield return null;

            int waited = 0;
            while (!IsXrDisplayRunning() && waited < maxFramesWaitForXr)
            {
                waited++;
                yield return null;
            }

            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();

            XROrigin origin = FindObjectOfType<XROrigin>();
            Camera rigCam = origin != null ? origin.Camera : Camera.main;
            int safety = 60;
            while (rigCam == null && safety-- > 0)
                yield return null;

            if (rigCam == null)
            {
                Debug.LogError($"{nameof(WorldSpaceCanvasSpawnOnce)}: No rig camera found.");
                yield break;
            }

            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.worldCamera = rigCam;

            Transform head = rigCam.transform;
            Vector3 forward = GetPlacementForward(head);
            transform.position = head.position + forward * distanceMeters;

            if (faceUserOnSpawn)
            {
                // World-space Canvas draws its front on the local -Z / “into the screen” side of the plane.
                // LookRotation(toUser) aligned +Z toward the headset, so you saw the back of the panel — use -toUser.
                Vector3 toUser = head.position - transform.position;
                if (yawOnly)
                    toUser.y = 0f;

                if (toUser.sqrMagnitude > 1e-6f)
                    transform.rotation = Quaternion.LookRotation(-toUser.normalized, Vector3.up);
            }

            if (transform.parent != null)
            {
                Debug.LogWarning(
                    $"{nameof(WorldSpaceCanvasSpawnOnce)}: Canvas was still parented to '{transform.parent.name}'; moving to scene root so it stays world-locked.");
                transform.SetParent(null, worldPositionStays: true);
            }

            _hasPlaced = true;
            Placed?.Invoke();
        }

        Vector3 GetPlacementForward(Transform head)
        {
            if (!placementAlongHorizontalPlane)
                return head.forward.normalized;

            Vector3 flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-6f)
                return head.forward.normalized;
            return flat.normalized;
        }

        static bool IsXrDisplayRunning()
        {
            var list = new List<XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(list);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].running)
                    return true;
            }

            return false;
        }
    }
}

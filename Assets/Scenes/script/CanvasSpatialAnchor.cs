// Android AIO, Windows/Linux/macOS standalone (PC OpenXR streaming to VIVE), not Editor (XR Device Simulator / no anchor).
#if !UNITY_EDITOR && (UNITY_ANDROID || UNITY_STANDALONE)
#define CANVAS_USE_VIVE_ANCHOR
#endif

using UnityEngine;

#if CANVAS_USE_VIVE_ANCHOR
using System;
using VIVE.OpenXR.Toolkits.Anchor;
#endif

namespace Scenes.script
{
    /// <summary>
    /// After <see cref="WorldSpaceCanvasSpawnOnce"/> finishes placement, creates a VIVE spatial anchor at the canvas pose
    /// and drives the transform from the anchor each frame. Reduces apparent SLAM drift vs. raw Unity world space.
    /// </summary>
    /// <remarks>
    /// Enable XR_HTC_anchor: Project Settings → XR Plug-in Management → OpenXR → enable "VIVE XR Anchor (Beta)" on each
    /// target you ship (e.g. Android and Standalone/Windows for PC streaming to a VIVE headset).
    /// Implementation uses <see cref="AnchorManager"/> (wraps <c>ViveAnchor.CreateSpatialAnchor</c> and <c>xrLocateSpace</c> via <see cref="VIVE.OpenXR.Feature.Space.GetRelatedPose"/>).
    /// Editor / non-HTC OpenXR: if you see LogErrors from ViveAnchor (xrCreateSpatialAnchorHTC unsupported), disable the
    /// "VIVE XR Anchor (Beta)" OpenXR feature for Standalone/Android while using that runtime, or use Spatial Find → XR → Disable VIVE XR Anchor.
    /// </remarks>
    [DisallowMultipleComponent]
    public class CanvasSpatialAnchor : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Usually the same GameObject as this component. Leave empty to use GetComponent<WorldSpaceCanvasSpawnOnce>().")]
        WorldSpaceCanvasSpawnOnce spawner;

#if CANVAS_USE_VIVE_ANCHOR
        AnchorManager.Anchor _anchor;
        bool _anchorCreationAttempted;
        Vector3 _lastGoodPosition;
        Quaternion _lastGoodRotation;
        bool _hasLastGoodPose;
#endif

        void Awake()
        {
            if (spawner == null)
                spawner = GetComponent<WorldSpaceCanvasSpawnOnce>();
        }

        void OnEnable()
        {
            if (spawner == null)
                return;

            spawner.Placed += OnSpawnerPlaced;
            if (spawner.HasPlaced)
                TryCreateAnchorAfterPlacement();
        }

        void OnDisable()
        {
            if (spawner != null)
                spawner.Placed -= OnSpawnerPlaced;
        }

        void OnSpawnerPlaced()
        {
            TryCreateAnchorAfterPlacement();
        }

        void TryCreateAnchorAfterPlacement()
        {
#if CANVAS_USE_VIVE_ANCHOR
            if (_anchorCreationAttempted || _anchor != null)
                return;
            if (spawner == null || !spawner.HasPlaced)
                return;

            _anchorCreationAttempted = true;

            if (!AnchorManager.IsSupported())
            {
                Debug.LogWarning($"{nameof(CanvasSpatialAnchor)}: VIVE spatial anchor is not supported on this device.");
                return;
            }

            Pose pose = new Pose(transform.position, transform.rotation);
            string name = $"CanvasSpatialAnchor_{GetInstanceID()}_{DateTime.UtcNow.Ticks}";
            _anchor = AnchorManager.CreateAnchor(pose, name);
            if (_anchor == null)
            {
                Debug.LogError($"{nameof(CanvasSpatialAnchor)}: CreateAnchor failed.");
                return;
            }

            _lastGoodPosition = transform.position;
            _lastGoodRotation = transform.rotation;
            _hasLastGoodPose = true;
#endif
        }

        void LateUpdate()
        {
#if CANVAS_USE_VIVE_ANCHOR
            if (_anchor == null)
                return;

            try
            {
                if (AnchorManager.GetTrackingSpacePose(_anchor, out Pose pose))
                {
                    transform.SetPositionAndRotation(pose.position, pose.rotation);
                    _lastGoodPosition = pose.position;
                    _lastGoodRotation = pose.rotation;
                    _hasLastGoodPose = true;
                }
                else if (_hasLastGoodPose)
                {
                    transform.SetPositionAndRotation(_lastGoodPosition, _lastGoodRotation);
                }
            }
            catch
            {
                if (_hasLastGoodPose)
                    transform.SetPositionAndRotation(_lastGoodPosition, _lastGoodRotation);
            }
#endif
        }

        void OnDestroy()
        {
            DisposeAnchor();
        }

        void OnApplicationQuit()
        {
            DisposeAnchor();
        }

        void DisposeAnchor()
        {
#if CANVAS_USE_VIVE_ANCHOR
            if (_anchor != null)
            {
                _anchor.Dispose();
                _anchor = null;
            }
#endif
        }
    }
}

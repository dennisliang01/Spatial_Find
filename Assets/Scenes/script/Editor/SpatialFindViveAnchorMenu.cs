#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using VIVE.OpenXR.Feature;

namespace Scenes.script.Editor
{
    /// <summary>
    /// ViveAnchor runs at OpenXR init. In the Editor, the active OpenXR runtime often does not expose
    /// xrCreateSpatialAnchorHTC / xrGetSpatialAnchorNameHTC (e.g. Meta Link, generic OpenXR), which causes
    /// LogError spam even when <see cref="CanvasSpatialAnchor"/> is compiled out. Disable the OpenXR feature
    /// here when you are not using a HTC/VIVE runtime or VIVE Mock Runtime with anchor support.
    /// </summary>
    static class SpatialFindViveAnchorMenu
    {
        const string MenuDisable = "Spatial Find/XR/Disable VIVE XR Anchor (Beta) on Standalone+Android";
        const string MenuEnable = "Spatial Find/XR/Enable VIVE XR Anchor (Beta) on Standalone+Android";

        [MenuItem(MenuDisable, priority = 0)]
        static void DisableViveAnchorOpenXrFeatures()
        {
            SetViveAnchorEnabled(false);
        }

        [MenuItem(MenuEnable, priority = 1)]
        static void EnableViveAnchorOpenXrFeatures()
        {
            SetViveAnchorEnabled(true);
        }

        [MenuItem(MenuDisable, validate = true)]
        static bool ValidateDisable()
        {
            return AnyViveAnchorEnabled();
        }

        [MenuItem(MenuEnable, validate = true)]
        static bool ValidateEnable()
        {
            return AnyViveAnchorDisabled();
        }

        static bool AnyViveAnchorEnabled()
        {
            foreach (var group in new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android })
            {
                var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
                var f = settings != null ? settings.GetFeature<ViveAnchor>() : null;
                if (f != null && f.enabled)
                    return true;
            }

            return false;
        }

        static bool AnyViveAnchorDisabled()
        {
            foreach (var group in new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android })
            {
                var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
                var f = settings != null ? settings.GetFeature<ViveAnchor>() : null;
                if (f != null && !f.enabled)
                    return true;
            }

            return false;
        }

        static void SetViveAnchorEnabled(bool enabled)
        {
            foreach (var group in new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android })
            {
                var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
                if (settings == null)
                    continue;
                var f = settings.GetFeature<ViveAnchor>();
                if (f == null)
                    continue;
                f.enabled = enabled;
                EditorUtility.SetDirty(settings);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[SpatialFind] VIVE XR Anchor (Beta) OpenXR feature set to {enabled} for Standalone and Android. " +
                      "Re-enable before building for VIVE hardware if you need HTC spatial anchors.");
        }
    }
}
#endif

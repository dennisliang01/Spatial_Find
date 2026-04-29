#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Scenes.script
{
    /// <summary>
    /// Editor utility to set up the PCInputController and the new stage panels
    /// (ImageGridPanel_3, ImageGridPanel_1) in the SpatialFind1 scene.
    /// Run via the menu: Scenes/Setup PC Input Controller
    /// </summary>
    public static class SceneSetup_PCInputController
    {
        [MenuItem("Scenes/Setup PC Input Controller")]
        public static void SetupPCInputController()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.name != "SpatialFind1")
            {
                EditorUtility.DisplayDialog("Error", "Please open the SpatialFind1 scene first.", "OK");
                return;
            }

            EnsurePCInputController(scene);
            ImageGridPanel panel3 = EnsureStagePanel(scene, "ImageGridPanel_3", columns: 3, rows: 1);
            ImageGridPanel panel1 = EnsureStagePanel(scene, "ImageGridPanel_1", columns: 1, rows: 1);
            WireNewPanelsToFlowController(scene, panel3, panel1);

            EditorSceneManager.SaveScene(scene);
            EditorUtility.DisplayDialog(
                "Success",
                "Scene setup complete:\n" +
                "- PCInputController wired\n" +
                "- ImageGridPanel_3 (3x1) created/verified\n" +
                "- ImageGridPanel_1 (1x1) created/verified",
                "OK");
        }

        static void EnsurePCInputController(Scene scene)
        {
            GameObject pcInputGO = GameObject.Find("PCInputController");
            if (pcInputGO == null)
            {
                pcInputGO = new GameObject("PCInputController");
                EditorSceneManager.MarkSceneDirty(scene);
            }

            PCInputController controller = pcInputGO.GetComponent<PCInputController>();
            if (controller == null)
            {
                controller = pcInputGO.AddComponent<PCInputController>();
                EditorSceneManager.MarkSceneDirty(scene);
            }

            GameObject clipSearchGO = GameObject.Find("ClipSearch_Service");
            if (clipSearchGO == null)
                return;

            ClipSearchFlowController flowController = clipSearchGO.GetComponent<ClipSearchFlowController>();
            if (flowController == null)
                return;

            var field = typeof(PCInputController).GetField("clipSearchFlowController",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(controller, flowController);
                EditorSceneManager.MarkSceneDirty(scene);
            }
        }

        /// <summary>
        /// Creates or reconfigures an ImageGridPanel GameObject with the given grid shape.
        /// Mirrors the scene-authored position of ImageGridPanel_90 so ApplyStagePanelDepthOffsets can fan them out along Z at runtime.
        /// </summary>
        static ImageGridPanel EnsureStagePanel(Scene scene, string goName, int columns, int rows)
        {
            GameObject go = GameObject.Find(goName);
            if (go == null)
            {
                go = new GameObject(goName);
                EditorSceneManager.MarkSceneDirty(scene);
            }

            // Match position of ImageGridPanel_90 so runtime depth-step offsets produce a clean front-to-back stack.
            GameObject stage90GO = GameObject.Find("ImageGridPanel_90");
            if (stage90GO != null)
                go.transform.position = stage90GO.transform.position;

            ImageGridPanel panel = go.GetComponent<ImageGridPanel>();
            if (panel == null)
                panel = go.AddComponent<ImageGridPanel>();

            panel.columns = columns;
            panel.rows = rows;

            // Mirror the stage-90 panel's visual/data configuration where possible.
            if (stage90GO != null)
            {
                ImageGridPanel stage90 = stage90GO.GetComponent<ImageGridPanel>();
                if (stage90 != null)
                {
                    panel.cellSize = stage90.cellSize;
                    panel.spacing = stage90.spacing;
                    panel.padding = stage90.padding;
                    panel.canvasScale = stage90.canvasScale;
                    panel.imageFolderName = stage90.imageFolderName;
                    panel.absoluteDatasetRoot = stage90.absoluteDatasetRoot;
                    panel.panelBackground = stage90.panelBackground;
                    panel.cellBackground = stage90.cellBackground;
                    panel.gridBackground = stage90.gridBackground;
                    panel.headerBarColor = stage90.headerBarColor;
                    panel.loadingCellColor = stage90.loadingCellColor;
                    panel.layerLabelText = stage90.layerLabelText;
                }
            }

            // Stage panels other than stage 1 start inactive — the flow controller activates them.
            go.SetActive(false);

            EditorSceneManager.MarkSceneDirty(scene);
            return panel;
        }

        static void WireNewPanelsToFlowController(Scene scene, ImageGridPanel panel3, ImageGridPanel panel1)
        {
            GameObject clipSearchGO = GameObject.Find("ClipSearch_Service");
            if (clipSearchGO == null)
                return;

            ClipSearchFlowController flow = clipSearchGO.GetComponent<ClipSearchFlowController>();
            if (flow == null)
                return;

            SerializedObject so = new SerializedObject(flow);
            SerializedProperty p3 = so.FindProperty("panelStage3");
            SerializedProperty p1 = so.FindProperty("panelStage1");
            if (p3 != null)
                p3.objectReferenceValue = panel3;
            if (p1 != null)
                p1.objectReferenceValue = panel1;
            so.ApplyModifiedProperties();
            EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}
#endif

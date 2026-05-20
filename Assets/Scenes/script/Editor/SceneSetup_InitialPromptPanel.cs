#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

namespace Scenes.script
{
    /// <summary>
    /// Builds a screen-space <see cref="InitialPromptPanel"/> with TMP input + Search/mic buttons,
    /// wires <see cref="ClipSearchFlowController"/>, and saves the scene.
    /// Menu: <c>Scenes/Setup Initial Prompt Panel</c>
    /// </summary>
    public static class SceneSetup_InitialPromptPanel
    {
        const string ScenePath = "Assets/Scenes/SpatialFind1.unity";
        const string FontAssetPath = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";

        const string kStandardSpritePath = "UI/Skin/UISprite.psd";
        const string kBackgroundSpritePath = "UI/Skin/Background.psd";
        const string kInputFieldBackgroundPath = "UI/Skin/InputFieldBackground.psd";
        const string kKnobPath = "UI/Skin/Knob.psd";
        const string kCheckmarkPath = "UI/Skin/Checkmark.psd";
        const string kDropdownArrowPath = "UI/Skin/DropdownArrow.psd";
        const string kMaskPath = "UI/Skin/UIMask.psd";

        [MenuItem("Scenes/Setup Initial Prompt Panel")]
        public static void SetupFromMenu()
        {
            if (!EnsureSpatialFind1IsActive())
                return;

            RunSetup();
            EditorUtility.DisplayDialog(
                "Success",
                "Initial prompt UI created or updated, ClipSearchFlowController wired, submitDebugQueryOnStart disabled.\nSave is already applied.",
                "OK");
        }

        /// <summary>
        /// For batchmode: <c>-executeMethod Scenes.script.SceneSetup_InitialPromptPanel.BatchSetupInitialPromptPanel</c>
        /// </summary>
        public static void BatchSetupInitialPromptPanel()
        {
            try
            {
                EditorSceneManager.OpenScene(ScenePath);
                RunSetup();
                EditorApplication.Exit(0);
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[SceneSetup_InitialPromptPanel] Batch setup failed: " + ex);
                EditorApplication.Exit(1);
            }
        }

        static bool EnsureSpatialFind1IsActive()
        {
            Scene scene = SceneManager.GetActiveScene();
            string path = (scene.path ?? "").Replace('\\', '/');
            if (scene.name == "SpatialFind1" || path == ScenePath || path.EndsWith("Scenes/SpatialFind1.unity"))
                return true;

            EditorUtility.DisplayDialog("Error", "Open SpatialFind1.unity first, then run this menu again.", "OK");
            return false;
        }

        static TMP_DefaultControls.Resources GetTmpUiResources()
        {
            return new TMP_DefaultControls.Resources
            {
                standard = AssetDatabase.GetBuiltinExtraResource<Sprite>(kStandardSpritePath),
                background = AssetDatabase.GetBuiltinExtraResource<Sprite>(kBackgroundSpritePath),
                inputField = AssetDatabase.GetBuiltinExtraResource<Sprite>(kInputFieldBackgroundPath),
                knob = AssetDatabase.GetBuiltinExtraResource<Sprite>(kKnobPath),
                checkmark = AssetDatabase.GetBuiltinExtraResource<Sprite>(kCheckmarkPath),
                dropdown = AssetDatabase.GetBuiltinExtraResource<Sprite>(kDropdownArrowPath),
                mask = AssetDatabase.GetBuiltinExtraResource<Sprite>(kMaskPath),
            };
        }

        static void RunSetup()
        {
            Scene scene = SceneManager.GetActiveScene();
            TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);

            GameObject clipSearchGo = GameObject.Find("ClipSearch_Service");
            if (clipSearchGo == null)
            {
                Debug.LogError("[SceneSetup_InitialPromptPanel] ClipSearch_Service not found.");
                return;
            }

            ClipSearchFlowController flow = clipSearchGo.GetComponent<ClipSearchFlowController>();
            if (flow == null)
            {
                Debug.LogError("[SceneSetup_InitialPromptPanel] ClipSearchFlowController not found.");
                return;
            }

            GameObject root = flow.initialPromptPanel;
            if (root == null)
                root = GameObject.Find("PromptWindow");
            if (root == null)
                root = GameObject.Find("InitialPromptPanel");
            if (root == null)
            {
                root = new GameObject("InitialPromptPanel");
                Undo.RegisterCreatedObjectUndo(root, "Create InitialPromptPanel");
            }

            Canvas canvas = root.GetComponent<Canvas>();
            if (canvas == null)
                canvas = Undo.AddComponent<Canvas>(root);
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 50;

            if (root.GetComponent<GraphicRaycaster>() == null)
                Undo.AddComponent<GraphicRaycaster>(root);

            CanvasScaler scaler = root.GetComponent<CanvasScaler>();
            if (scaler == null)
                scaler = Undo.AddComponent<CanvasScaler>(root);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            RectTransform rootRt = root.GetComponent<RectTransform>();
            rootRt.anchorMin = Vector2.zero;
            rootRt.anchorMax = Vector2.one;
            rootRt.sizeDelta = Vector2.zero;
            rootRt.anchoredPosition = Vector2.zero;

            GameObject dim = FindOrCreateChild(root.transform, "Dim");
            RectTransform dimRt = dim.GetComponent<RectTransform>();
            dimRt.anchorMin = Vector2.zero;
            dimRt.anchorMax = Vector2.one;
            dimRt.sizeDelta = Vector2.zero;
            dimRt.anchoredPosition = Vector2.zero;
            Image dimImg = dim.GetComponent<Image>();
            if (dimImg == null)
                dimImg = Undo.AddComponent<Image>(dim);
            dimImg.color = new Color(0f, 0f, 0f, 0.55f);
            dimImg.raycastTarget = true;

            GameObject card = FindOrCreateChild(root.transform, "Card");
            RectTransform cardRt = card.GetComponent<RectTransform>();
            cardRt.anchorMin = new Vector2(0.5f, 0.5f);
            cardRt.anchorMax = new Vector2(0.5f, 0.5f);
            cardRt.pivot = new Vector2(0.5f, 0.5f);
            cardRt.sizeDelta = new Vector2(720f, 220f);
            cardRt.anchoredPosition = Vector2.zero;
            Image cardImg = card.GetComponent<Image>();
            if (cardImg == null)
                cardImg = Undo.AddComponent<Image>(card);
            cardImg.color = new Color(0.2f, 0.2f, 0.22f, 0.98f);
            if (cardImg.sprite == null)
                cardImg.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>(kStandardSpritePath);
            cardImg.type = Image.Type.Sliced;

            GameObject titleGo = FindOrCreateChild(card.transform, "Title");
            TextMeshProUGUI titleTmp = titleGo.GetComponent<TextMeshProUGUI>();
            if (titleTmp == null)
                titleTmp = Undo.AddComponent<TextMeshProUGUI>(titleGo);
            if (font != null)
                titleTmp.font = font;
            titleTmp.text = "Search the dataset";
            titleTmp.fontSize = 28;
            titleTmp.alignment = TextAlignmentOptions.Center;
            titleTmp.color = Color.white;
            RectTransform titleRt = titleGo.GetComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.sizeDelta = new Vector2(-48f, 48f);
            titleRt.anchoredPosition = new Vector2(0f, -16f);

            TMP_DefaultControls.Resources res = GetTmpUiResources();
            Transform inputTr = card.transform.Find("InitialPrompt_QueryInput");
            GameObject inputGo = inputTr != null ? inputTr.gameObject : null;
            TMP_InputField inputField;
            if (inputGo == null)
            {
                inputGo = TMP_DefaultControls.CreateInputField(res);
                inputGo.name = "InitialPrompt_QueryInput";
                Undo.RegisterCreatedObjectUndo(inputGo, "Create query input");
            }

            inputGo.transform.SetParent(card.transform, false);
            inputField = inputGo.GetComponent<TMP_InputField>();
            inputField.lineType = TMP_InputField.LineType.SingleLine;
            RectTransform inputRt = inputGo.GetComponent<RectTransform>();
            inputRt.anchorMin = new Vector2(0.08f, 0.38f);
            inputRt.anchorMax = new Vector2(0.92f, 0.72f);
            inputRt.offsetMin = Vector2.zero;
            inputRt.offsetMax = Vector2.zero;

            TextMeshProUGUI ph = inputField.placeholder as TextMeshProUGUI;
            if (ph != null)
                ph.text = "Type what you want to find, then press Enter or Search…";
            if (font != null)
            {
                inputField.fontAsset = font;
                if (inputField.textComponent != null)
                    ((TextMeshProUGUI)inputField.textComponent).font = font;
                if (ph != null)
                    ph.font = font;
            }

            Transform buttonTr = card.transform.Find("InitialPrompt_SearchButton");
            GameObject buttonGo = buttonTr != null ? buttonTr.gameObject : null;
            Button searchButton;
            bool createdSearchButton = false;
            if (buttonGo == null)
            {
                buttonGo = TMP_DefaultControls.CreateButton(res);
                buttonGo.name = "InitialPrompt_SearchButton";
                Undo.RegisterCreatedObjectUndo(buttonGo, "Create Search button");
                createdSearchButton = true;
                TextMeshProUGUI bt = buttonGo.GetComponentInChildren<TextMeshProUGUI>();
                if (bt != null)
                {
                    bt.text = "Search";
                    if (font != null)
                        bt.font = font;
                }
            }

            buttonGo.transform.SetParent(card.transform, false);
            searchButton = buttonGo.GetComponent<Button>();
            if (createdSearchButton)
                ConfigurePromptButtonRect(buttonGo.GetComponent<RectTransform>(), new Vector2(0.58f, 0.06f), new Vector2(0.88f, 0.22f));

            Transform micTr = card.transform.Find("InitialPrompt_MicButton");
            GameObject micGo = micTr != null ? micTr.gameObject : null;
            Button micButton;
            bool createdMicButton = false;
            if (micGo == null)
            {
                micGo = TMP_DefaultControls.CreateButton(res);
                micGo.name = "InitialPrompt_MicButton";
                Undo.RegisterCreatedObjectUndo(micGo, "Create Mic button");
                createdMicButton = true;
            }

            micGo.transform.SetParent(card.transform, false);
            micButton = micGo.GetComponent<Button>();
            if (createdMicButton)
                ConfigurePromptButtonRect(micGo.GetComponent<RectTransform>(), new Vector2(0.12f, 0.06f), new Vector2(0.42f, 0.22f));
            TextMeshProUGUI micText = micGo.GetComponentInChildren<TextMeshProUGUI>();
            if (micText != null)
            {
                micText.text = "Mic";
                if (font != null)
                    micText.font = font;
            }

            titleGo.transform.SetSiblingIndex(0);
            inputGo.transform.SetSiblingIndex(1);
            micGo.transform.SetSiblingIndex(2);
            buttonGo.transform.SetSiblingIndex(3);

            SerializedObject flowSo = new SerializedObject(flow);
            SerializedProperty pPanel = flowSo.FindProperty("initialPromptPanel");
            SerializedProperty pQuery = flowSo.FindProperty("queryInput");
            SerializedProperty pSubmit = flowSo.FindProperty("submitButton");
            SerializedProperty pSpeech = flowSo.FindProperty("speechInputButton");
            SerializedProperty pDebug = flowSo.FindProperty("submitDebugQueryOnStart");
            if (pPanel != null)
                pPanel.objectReferenceValue = root;
            if (pQuery != null)
                pQuery.objectReferenceValue = inputField;
            if (pSubmit != null)
                pSubmit.objectReferenceValue = searchButton;
            if (pSpeech != null)
                pSpeech.objectReferenceValue = micButton;
            if (pDebug != null)
                pDebug.boolValue = false;
            flowSo.ApplyModifiedProperties();

            PromptSpeechInputController speechController = clipSearchGo.GetComponent<PromptSpeechInputController>();
            if (speechController == null)
                speechController = Undo.AddComponent<PromptSpeechInputController>(clipSearchGo);
            SerializedObject speechSo = new SerializedObject(speechController);
            speechSo.FindProperty("queryInput").objectReferenceValue = inputField;
            speechSo.FindProperty("micButton").objectReferenceValue = micButton;
            speechSo.FindProperty("submitButton").objectReferenceValue = searchButton;
            speechSo.ApplyModifiedPropertiesWithoutUndo();

            GameObject stage90 = GameObject.Find("ImageGridPanel_90");
            if (stage90 != null)
            {
                ImageGridPanel ig = stage90.GetComponent<ImageGridPanel>();
                if (ig != null)
                {
                    SerializedObject igSo = new SerializedObject(ig);
                    igSo.FindProperty("populateRandomOnStart").boolValue = false;
                    igSo.ApplyModifiedProperties();
                }
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        static GameObject FindOrCreateChild(Transform parent, string name)
        {
            Transform existing = parent.Find(name);
            if (existing != null)
                return existing.gameObject;

            GameObject go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            return go;
        }

        static void ConfigurePromptButtonRect(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax)
        {
            if (rt == null)
                return;

            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
#endif

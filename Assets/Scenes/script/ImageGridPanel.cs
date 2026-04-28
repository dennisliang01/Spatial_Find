using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using TMPro;

namespace Scenes.script
{
    public class ImageGridPanel : MonoBehaviour
    {
        [Header("Grid Layout")]
        public int columns = 10;
        public int rows = 9;
        public Vector2 cellSize = new Vector2(90f, 70f);
        public Vector2 spacing = new Vector2(6f, 6f);
        public Vector2 padding = new Vector2(20f, 20f);

        [Header("Canvas")]
        [Tooltip("World-space scale of the canvas. 0.001 means 1 UI unit ≈ 1 mm.")]
        public float canvasScale = 0.001f;

        [Tooltip("Distance in front of the user when the world-space canvas is anchored (see WorldSpaceCanvasSpawnOnce).")]
        [SerializeField]
        float worldCanvasSpawnDistanceMeters = 3.5f;

        [Tooltip("When true, the grid canvas is world-anchored the first time this panel is enabled, not at shell build. Inactive CLIP stages stay parented until shown.")]
        // Public so curving helpers (e.g. CurveImageGridPanel) can disable world-space anchoring
        // before OnEnable runs and detaches the canvas via WorldSpaceCanvasSpawnOnce.
        public bool anchorWorldSpaceCanvasWhenEnabled = true;

        [Header("Aspect Ratio")]
        [Tooltip("When true, forces the canvas height to match targetAspectRatio (width/height) based on computed gridWidth.")]
        public bool useFixedAspectRatio = true;

        [Tooltip("Width divided by height. e.g. 16/9 = 1.777, 4/3 = 1.333")]
        public float targetAspectRatio = 1.777f;

        [Header("Data")]
        public string imageFolderName = "dogs_vs_cats";

        [Tooltip("Full path to the dogs_vs_cats folder. Leave empty to use StreamingAssets/imageFolderName. " +
                 "Point this at the same folder as CLIP_IMAGE_ROOT so Unity does not import 25k images under Assets (much faster Editor loads).")]
        public string absoluteDatasetRoot = "";

        [Header("API thumbnails")]
        [Tooltip("Used for HTTP fallback when the file is not under the local dataset root.")]
        public ClipSearchApiClient apiClient;

        /// <summary>
        /// CLIP flow stage for this panel (1 = 90-tile, 2 = 30…, 5 = final 1×1). 0 = not wired.
        /// Set by <see cref="ClipSearchFlowController"/> so clicks on this panel can jump back.
        /// </summary>
        public int clipSearchStageIndex { get; private set; }

        [Tooltip("Cell tint while downloading over HTTP.")]
        public Color loadingCellColor = new Color(0.82f, 0.82f, 0.84f, 1f);

        [Header("Visuals")]
        public Color panelBackground = new Color(0.7f, 0.7f, 0.7f, 1f);
        public Color cellBackground = Color.white;
        public Color gridBackground = new Color(0.55f, 0.55f, 0.55f, 1f);
        public Color headerBarColor = new Color(0.45f, 0.45f, 0.45f, 0.9f);

        [Tooltip("Short label in the top-left of the panel.")]
        public string layerLabelText = "Layer";

        [Header("Search chrome (CLIP flow)")]
        [Tooltip("Shown on a bar below the image grid.")]
        [SerializeField]
        string footerInstructionText = "Select the image closest to what you are looking for.";

        [SerializeField]
        Color promptPillColor = new Color(0.38f, 0.38f, 0.38f, 1f);

        [SerializeField]
        Color footerBarColor = new Color(0.48f, 0.48f, 0.48f, 0.95f);

        [Tooltip("Font size of the search query shown above the grid. Tune larger for stage 90, smaller for later stages.")]
        [SerializeField]
        float promptFontSize = 36f;

        [Tooltip("Font size of the footer instruction text below the grid. Tune larger for stage 90, smaller for later stages.")]
        [SerializeField]
        float instructionFontSize = 32f;

        [Header("Stack depth (CLIP)")]
        [Tooltip("When false, stack dimming is never shown for this panel.")]
        [SerializeField]
        bool enableStackDimOverlay = true;

        [Tooltip("Brightness multiplier (0–1) for stages behind the current one. 0.2 = 20% brightness (uniform black overlay).")]
        [SerializeField]
        [Range(0f, 1f)]
        float previousStagesBrightness = 0.2f;

        static Texture2D s_stackDimSolidTexture;
        RawImage _stackDimOverlay;

        static Texture2D GetOrCreateStackDimSolidTexture()
        {
            if (s_stackDimSolidTexture != null)
                return s_stackDimSolidTexture;

            const int n = 4;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            var white = Color.white;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                    tex.SetPixel(x, y, white);
            }

            tex.Apply(false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            s_stackDimSolidTexture = tex;
            return s_stackDimSolidTexture;
        }

        static readonly HashSet<string> ImageExtensions = new HashSet<string>
            { ".jpg", ".jpeg", ".png" };

        sealed class CellSlot
        {
            public RawImage Raw;
            public Button Btn;
            public string ImageId;
            public Texture2D OwnedTexture;
        }

        readonly List<CellSlot> _cells = new List<CellSlot>();
        TextMeshProUGUI _layerLabel;
        TextMeshProUGUI _queryPromptText;
        TextMeshProUGUI _footerInstructionText;
        RectTransform _gridLayoutRoot;
        RectTransform _headerBarRT;
        Button _headerNavigateButton;
        Button _closeToStartButton;
        RectTransform _closeButtonRT;
        int _navStageIndex;
        Action<int> _onHeaderNavigateClicked;
        Action _onCloseToStart;
        string _pendingQuery = "";
        [System.NonSerialized]
        bool _shellBuilt;
        Transform _canvasRoot;

        void Awake()
        {
            BuildShellIfNeeded();
        }

        void OnEnable()
        {
            BuildShellIfNeeded();
            if (anchorWorldSpaceCanvasWhenEnabled && _canvasRoot != null && _canvasRoot.parent == null)
                _canvasRoot.gameObject.SetActive(true);
            EnsureWorldCanvasAnchorIfConfigured();
        }

        void OnDisable()
        {
            if (_canvasRoot != null && _canvasRoot.parent == null)
                _canvasRoot.gameObject.SetActive(false);
        }

        void Start()
        {
        }

        public void SetLayerLabel(string text)
        {
            if (_layerLabel != null)
                _layerLabel.text = text ?? "";
        }

        /// <summary>Displays the user’s search prompt above the grid (CLIP flow).</summary>
        public void SetSearchQuery(string query)
        {
            string q = query ?? string.Empty;
            if (_queryPromptText == null)
            {
                _pendingQuery = q;
                return;
            }

            _queryPromptText.text = q;
        }

        /// <summary>Wires the top-right close control; pass null to disable.</summary>
        public void SetOnCloseToStart(Action callback)
        {
            _onCloseToStart = callback;
            BuildShellIfNeeded();
            RefreshCloseButtonWiring();
        }

        void RefreshCloseButtonWiring()
        {
            if (_closeToStartButton == null)
                return;

            _closeToStartButton.onClick.RemoveAllListeners();
            if (_onCloseToStart != null)
                _closeToStartButton.onClick.AddListener(() => _onCloseToStart());

            _closeToStartButton.interactable = _onCloseToStart != null;
        }

        /// <summary>Clears thumbnails and picks; does not destroy the shell.</summary>
        public void ClearGridAndResetPick()
        {
            if (!_shellBuilt)
                return;

            foreach (CellSlot slot in _cells)
            {
                ClearCell(slot);
                WirePick(slot, null, null);
            }

            SetSelectionEnabled(false);
        }

        /// <summary>
        /// Dims this panel when it represents an earlier CLIP stage than the foreground (uniform
        /// <see cref="previousStagesBrightness"/> via a full-panel black overlay).
        /// Pass <paramref name="active"/> false for the current / front stage.
        /// </summary>
        /// <param name="stepsBehind">Must be &gt;= 1 for dimming to apply; value does not change brightness (all back stages match).</param>
        public void SetBackgroundStackDimming(bool active, int stepsBehind)
        {
            if (_stackDimOverlay == null)
                return;

            if (!enableStackDimOverlay || !active || stepsBehind < 1)
            {
                _stackDimOverlay.gameObject.SetActive(false);
                return;
            }

            float b = Mathf.Clamp01(previousStagesBrightness);
            float overlayAlpha = 1f - b;
            _stackDimOverlay.color = new Color(0f, 0f, 0f, overlayAlpha);
            _stackDimOverlay.gameObject.SetActive(true);
        }

        /// <summary>Registers which CLIP stage this grid represents (1–5). Use 0 for local-only grids.</summary>
        public void SetClipSearchStage(int stage)
        {
            clipSearchStageIndex = Mathf.Clamp(stage, 0, 5);
        }

        /// <summary>
        /// Makes the header bar clickable to jump back to this CLIP stage (1–4) in <see cref="ClipSearchFlowController"/>.
        /// Pass stage 0 or null callback to remove.
        /// </summary>
        public void SetNavigateBackCallback(int clipSearchStageIndex, Action<int> onHeaderNavigateClicked)
        {
            _navStageIndex = clipSearchStageIndex;
            _onHeaderNavigateClicked = onHeaderNavigateClicked;
            BuildShellIfNeeded();
            TryFindHeaderBarTransform();
            EnsureHeaderNavigateButton();
            if (isActiveAndEnabled)
                EnsureWorldCanvasAnchorIfConfigured();
        }

        void TryFindHeaderBarTransform()
        {
            if (_headerBarRT != null)
                return;
            Transform t = _canvasRoot != null
                ? _canvasRoot.Find("PanelBG/HeaderBar")
                : transform.Find("ImageGrid_Canvas/PanelBG/HeaderBar");
            if (t != null)
                _headerBarRT = t.GetComponent<RectTransform>();
        }

        void EnsureHeaderNavigateButton()
        {
            if (_headerBarRT == null)
                return;

            if (_navStageIndex <= 0 || _onHeaderNavigateClicked == null)
            {
                if (_headerNavigateButton != null)
                {
                    Destroy(_headerNavigateButton.gameObject);
                    _headerNavigateButton = null;
                }

                return;
            }

            if (_headerNavigateButton != null)
            {
                _headerNavigateButton.onClick.RemoveAllListeners();
                _headerNavigateButton.onClick.AddListener(() => _onHeaderNavigateClicked(_navStageIndex));
                return;
            }

            GameObject hitGo = CreateUIElement("StageNavigateHit", _headerBarRT.transform);
            hitGo.transform.SetAsLastSibling();
            RectTransform hitRt = hitGo.GetComponent<RectTransform>();
            StretchFill(hitRt);
            Image hitImg = hitGo.AddComponent<Image>();
            hitImg.color = new Color(1f, 1f, 1f, 0.02f);
            hitImg.raycastTarget = true;
            Button btn = hitGo.AddComponent<Button>();
            btn.targetGraphic = hitImg;
            btn.transition = Selectable.Transition.None;
            _headerNavigateButton = btn;
            btn.onClick.AddListener(() => _onHeaderNavigateClicked(_navStageIndex));
            hitGo.AddComponent<BoxCollider>();
            StartCoroutine(ResyncHeaderNavigateColliderNextFrame(hitRt));
        }

        IEnumerator ResyncHeaderNavigateColliderNextFrame(RectTransform hitRt)
        {
            yield return null;
            if (hitRt == null)
                yield break;
            BoxCollider box = hitRt.GetComponent<BoxCollider>();
            if (box != null)
                SyncUiHeaderNavigateColliderThin(hitRt, box);
        }

        IEnumerator ResyncCloseButtonColliderNextFrame(RectTransform closeRt)
        {
            yield return null;
            if (closeRt == null)
                yield break;
            BoxCollider box = closeRt.GetComponent<BoxCollider>();
            if (box != null)
                SyncUiCloseColliderWithForwardBias(closeRt, box);
        }

        /// <summary>
        /// Keeps the header navigate slab thin in local Z so oblique controller rays are less likely to
        /// register it ahead of UI below (e.g. CloseToStart) on stacked world canvases.
        /// </summary>
        static void SyncUiHeaderNavigateColliderThin(RectTransform rt, BoxCollider box)
        {
            SyncUiCellCollider(rt, box);
            Vector3 s = box.size;
            s.z = Mathf.Min(s.z, 8f);
            box.size = s;
        }

        /// <summary>
        /// Nudges the close button collider slightly along local +Z so physics ray picks tend to favor it
        /// over deeper stacked panels when distances tie closely.
        /// </summary>
        static void SyncUiCloseColliderWithForwardBias(RectTransform rt, BoxCollider box)
        {
            SyncUiCellCollider(rt, box);
            box.center = new Vector3(box.center.x, box.center.y, box.center.z + 22f);
            Vector3 s = box.size;
            s.z = Mathf.Max(8f, s.z);
            box.size = s;
        }

        public void SetSelectionEnabled(bool enabled)
        {
            bool cellsPickable = enabled;
            foreach (CellSlot slot in _cells)
            {
                if (slot.Btn == null)
                    continue;
                bool on = cellsPickable && !string.IsNullOrEmpty(slot.ImageId);
                slot.Btn.interactable = on;
                if (slot.Raw != null)
                    slot.Raw.raycastTarget = on;
            }
        }

        /// <summary>
        /// Clears textures and click handlers; optionally loads thumbnails from disk or <see cref="apiClient"/>.
        /// </summary>
        /// <param name="reuseLoadedThumbnailsWithoutNetwork">
        /// When true (e.g. restoring after navigate-back), keeps cells that already show the same image id
        /// and skips HTTP; only disk fallback is used for missing textures.
        /// </param>
        public IEnumerator PopulateFromApiResults(ClipResultRecordDto[] results, Action<string> onCellPicked, bool reuseLoadedThumbnailsWithoutNetwork = false)
        {
            if (!_shellBuilt)
                BuildShellIfNeeded();

            ClipResultRecordDto[] safe = results ?? Array.Empty<ClipResultRecordDto>();

            for (int i = 0; i < _cells.Count; i++)
            {
                if (i >= safe.Length)
                {
                    ClearCell(_cells[i]);
                    WirePick(_cells[i], null, null);
                    continue;
                }

                ClipResultRecordDto rec = safe[i];

                if (reuseLoadedThumbnailsWithoutNetwork
                    && !string.IsNullOrEmpty(rec.id)
                    && string.Equals(_cells[i].ImageId, rec.id, StringComparison.Ordinal)
                    && _cells[i].Raw != null
                    && _cells[i].Raw.texture != null)
                {
                    if (_cells[i].Raw != null)
                    {
                        ImageTile imageTile = _cells[i].Raw.GetComponent<ImageTile>();
                        if (imageTile != null)
                            imageTile.imageId = rec.id;
                    }

                    WirePick(_cells[i], rec.id, onCellPicked);
                    continue;
                }

                ClearCell(_cells[i]);
                _cells[i].ImageId = rec.id;

                if (_cells[i].Raw != null)
                {
                    ImageTile imageTile = _cells[i].Raw.GetComponent<ImageTile>();
                    if (imageTile != null)
                        imageTile.imageId = rec.id;
                }

                string localPath = TryResolveLocalFile(rec.path);
                if (!string.IsNullOrEmpty(localPath))
                {
                    Texture2D tex = LoadTextureFromFile(localPath);
                    if (tex != null)
                    {
                        _cells[i].Raw.texture = tex;
                        _cells[i].OwnedTexture = tex;
                    }
                }
                else if (!reuseLoadedThumbnailsWithoutNetwork && apiClient != null && !string.IsNullOrEmpty(rec.path))
                {
                    _cells[i].Raw.color = loadingCellColor;
                    string url = apiClient.GetImageUrl(rec.path);
                    using (UnityWebRequest req = UnityWebRequestTexture.GetTexture(url))
                    {
                        yield return req.SendWebRequest();
                        _cells[i].Raw.color = cellBackground;
#if UNITY_2020_1_OR_NEWER
                        if (req.result != UnityWebRequest.Result.Success)
#else
                        if (req.isNetworkError || req.isHttpError)
#endif
                        {
                            Debug.LogWarning($"[ImageGridPanel] HTTP image failed: {url} ({req.error})");
                        }
                        else
                        {
                            Texture2D tex = DownloadHandlerTexture.GetContent(req);
                            _cells[i].Raw.texture = tex;
                            _cells[i].OwnedTexture = tex;
                        }
                    }
                }

                WirePick(_cells[i], rec.id, onCellPicked);
            }

            Debug.Log($"[ImageGridPanel] Populated API results: {safe.Length} results, {_cells.Count} slots.");
        }

        IEnumerator PopulateFromLocalPathsCoroutine(List<string> imagePaths)
        {
            if (!_shellBuilt)
                BuildShellIfNeeded();

            for (int i = 0; i < _cells.Count; i++)
            {
                ClearCell(_cells[i]);
                if (i < imagePaths.Count)
                {
                    Texture2D tex = LoadTextureFromFile(imagePaths[i]);
                    if (tex != null)
                    {
                        _cells[i].Raw.texture = tex;
                        _cells[i].OwnedTexture = tex;
                    }
                }

                WirePick(_cells[i], null, null);
            }

            Debug.Log($"[ImageGridPanel] Random local fill: {imagePaths.Count} images.");
            yield break;
        }

        void BuildShellIfNeeded()
        {
            if (_shellBuilt)
                return;

            int needed = columns * rows;
            float gridWidth = columns * cellSize.x + (columns - 1) * spacing.x;
            float gridHeight = rows * cellSize.y + (rows - 1) * spacing.y;

            float headerHeight = 44f;
            bool showFooterInstruction = clipSearchStageIndex != 5;
            // Prompt row: HorizontalLayoutGroup padding 6+6, QueryText RT offsets 4+4 — inner
            // height must fit TMP line height or large fonts clip to nothing.
            const float promptRowLayoutPadV = 12f;
            const float queryTextInnerPadV = 8f;
            const int promptMinWrappedLines = 2;
            float promptLineH = Mathf.Max(14f, promptFontSize * 1.25f);
            float promptBarHeight = Mathf.Max(
                52f,
                promptRowLayoutPadV + queryTextInnerPadV + promptLineH * promptMinWrappedLines);
            // Footer bar: StretchFill text uses offsetMin/Max vertical 4+4.
            const float footerTextInnerPadV = 8f;
            const int instructionMinWrappedLines = 2;
            float instructionLineH = Mathf.Max(14f, instructionFontSize * 1.25f);
            float footerBarHeight = showFooterInstruction
                ? Mathf.Max(52f, footerTextInnerPadV + instructionLineH * instructionMinWrappedLines)
                : 0f;
            float topSection = headerHeight + promptBarHeight;

            if (useFixedAspectRatio && targetAspectRatio > 0.001f)
            {
                float totalW = gridWidth + padding.x * 2f;
                float targetTotalH = totalW / targetAspectRatio;
                // totalHeight = topSection + (gridHeight + spacing.y) + footerBarHeight + padding.y * 2
                float verticalChrome = topSection + footerBarHeight + padding.y * 2f + spacing.y;
                float newGridHeight = targetTotalH - verticalChrome;
                float newCellHeight = (newGridHeight - spacing.y * (rows - 1)) / Mathf.Max(rows, 1);
                cellSize.y = Mathf.Max(newCellHeight, 1f);
                gridHeight = rows * cellSize.y + (rows - 1) * spacing.y;
            }

            float gridVisualHeight = gridHeight + spacing.y;
            float totalWidth = gridWidth + padding.x * 2;
            float totalHeight = topSection + gridVisualHeight + footerBarHeight + padding.y * 2f;

            GameObject canvasGO = new GameObject("ImageGrid_Canvas");
            canvasGO.transform.SetParent(transform, false);
            _canvasRoot = canvasGO.transform;

            Canvas canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 1;

            canvasGO.AddComponent<CanvasScaler>();
            canvasGO.AddComponent<TrackedDeviceGraphicRaycaster>();

            RectTransform canvasRT = canvasGO.GetComponent<RectTransform>();
            canvasRT.sizeDelta = new Vector2(totalWidth, totalHeight);
            canvasRT.localScale = Vector3.one * canvasScale;
            canvasRT.localPosition = Vector3.zero;
            canvasRT.localRotation = Quaternion.identity;

            GameObject panelGO = CreateUIElement("PanelBG", canvasGO.transform);
            RectTransform panelRT = panelGO.GetComponent<RectTransform>();
            StretchFill(panelRT);
            Image panelImg = panelGO.AddComponent<Image>();
            panelImg.color = panelBackground;
            panelImg.raycastTarget = false;

            GameObject headerGO = CreateUIElement("HeaderBar", panelGO.transform);
            RectTransform headerRT = headerGO.GetComponent<RectTransform>();
            headerRT.anchorMin = new Vector2(0, 1);
            headerRT.anchorMax = new Vector2(1, 1);
            headerRT.pivot = new Vector2(0.5f, 1);
            headerRT.sizeDelta = new Vector2(0, headerHeight);
            headerRT.anchoredPosition = Vector2.zero;
            // Sub-Canvas: isolates header rebuilds (close-button highlights, navigate-back hit slab)
            // from the rest of the panel.
            AddSubCanvas(headerGO);
            Image headerImg = headerGO.AddComponent<Image>();
            headerImg.color = headerBarColor;
            headerImg.raycastTarget = false;
            _headerBarRT = headerRT;

            GameObject promptRowGO = CreateUIElement("QueryPromptRow", panelGO.transform);
            RectTransform promptRowRT = promptRowGO.GetComponent<RectTransform>();
            promptRowRT.anchorMin = new Vector2(0, 1);
            promptRowRT.anchorMax = new Vector2(1, 1);
            promptRowRT.pivot = new Vector2(0.5f, 1);
            promptRowRT.sizeDelta = new Vector2(0, promptBarHeight);
            promptRowRT.anchoredPosition = new Vector2(0, -headerHeight);
            // Sub-Canvas: SetSearchQuery / SetLayerLabel text edits don't dirty the grid batch.
            AddSubCanvas(promptRowGO);
            HorizontalLayoutGroup promptRowLayout = promptRowGO.AddComponent<HorizontalLayoutGroup>();
            promptRowLayout.padding = new RectOffset(10, 10, 6, 6);
            promptRowLayout.spacing = 8;
            promptRowLayout.childAlignment = TextAnchor.MiddleCenter;
            promptRowLayout.childForceExpandWidth = false;
            promptRowLayout.childForceExpandHeight = true;
            promptRowLayout.childControlWidth = true;
            promptRowLayout.childControlHeight = true;

            GameObject labelGO = CreateUIElement("FirstLayerLabel", promptRowGO.transform);
            LayoutElement labelLe = labelGO.AddComponent<LayoutElement>();
            labelLe.minWidth = 90f;
            labelLe.preferredWidth = 110f;
            labelLe.flexibleWidth = 0f;
            _layerLabel = labelGO.AddComponent<TextMeshProUGUI>();
            _layerLabel.text = layerLabelText;
            _layerLabel.fontSize = 16;
            _layerLabel.color = new Color(0.75f, 0.75f, 0.75f, 1f);
            _layerLabel.alignment = TextAlignmentOptions.MidlineLeft;
            _layerLabel.fontStyle = FontStyles.Italic;
            _layerLabel.raycastTarget = false;

            GameObject pillGO = CreateUIElement("QueryPromptPill", promptRowGO.transform);
            LayoutElement pillLe = pillGO.AddComponent<LayoutElement>();
            pillLe.flexibleWidth = 1f;
            pillLe.minWidth = 40f;
            Image pillImg = pillGO.AddComponent<Image>();
            pillImg.color = promptPillColor;
            pillImg.raycastTarget = false;

            GameObject queryTextGO = CreateUIElement("QueryText", pillGO.transform);
            RectTransform queryTextRT = queryTextGO.GetComponent<RectTransform>();
            StretchFill(queryTextRT);
            queryTextRT.offsetMin = new Vector2(10f, 4f);
            queryTextRT.offsetMax = new Vector2(-10f, -4f);
            _queryPromptText = queryTextGO.AddComponent<TextMeshProUGUI>();
            _queryPromptText.text = string.Empty;
            _queryPromptText.fontSize = promptFontSize;
            _queryPromptText.color = Color.white;
            _queryPromptText.alignment = TextAlignmentOptions.MidlineLeft;
            _queryPromptText.enableWordWrapping = true;
            _queryPromptText.overflowMode = TextOverflowModes.Ellipsis;
            _queryPromptText.raycastTarget = false;

            GameObject closeGO = CreateUIElement("CloseToStart", promptRowGO.transform);
            _closeButtonRT = closeGO.GetComponent<RectTransform>();
            LayoutElement closeLe = closeGO.AddComponent<LayoutElement>();
            closeLe.minWidth = 44f;
            closeLe.preferredWidth = 44f;
            closeLe.minHeight = 44f;
            closeLe.preferredHeight = 44f;
            closeLe.flexibleWidth = 0f;
            Image closeImg = closeGO.AddComponent<Image>();
            closeImg.color = new Color(0.5f, 0.5f, 0.5f, 1f);
            closeImg.raycastTarget = true;
            _closeToStartButton = closeGO.AddComponent<Button>();
            _closeToStartButton.targetGraphic = closeImg;
            _closeToStartButton.transition = Selectable.Transition.None;

            GameObject closeLabelGO = CreateUIElement("Label", closeGO.transform);
            RectTransform closeLabelRT = closeLabelGO.GetComponent<RectTransform>();
            StretchFill(closeLabelRT);
            TextMeshProUGUI closeTmp = closeLabelGO.AddComponent<TextMeshProUGUI>();
            closeTmp.text = "\u00D7";
            closeTmp.fontSize = 28;
            closeTmp.color = Color.white;
            closeTmp.alignment = TextAlignmentOptions.Center;
            closeTmp.raycastTarget = false;

            closeGO.AddComponent<BoxCollider>();
            StartCoroutine(ResyncCloseButtonColliderNextFrame(_closeButtonRT));

            if (showFooterInstruction)
            {
                GameObject footerGO = CreateUIElement("FooterInstructionBar", panelGO.transform);
                RectTransform footerRT = footerGO.GetComponent<RectTransform>();
                footerRT.anchorMin = new Vector2(0, 0);
                footerRT.anchorMax = new Vector2(1, 0);
                footerRT.pivot = new Vector2(0.5f, 0);
                footerRT.sizeDelta = new Vector2(0, footerBarHeight);
                footerRT.anchoredPosition = new Vector2(0, padding.y);
                AddSubCanvas(footerGO);
                Image footerImg = footerGO.AddComponent<Image>();
                footerImg.color = footerBarColor;
                footerImg.raycastTarget = false;

                GameObject footerTextGO = CreateUIElement("InstructionText", footerGO.transform);
                RectTransform footerTextRT = footerTextGO.GetComponent<RectTransform>();
                StretchFill(footerTextRT);
                footerTextRT.offsetMin = new Vector2(12f, 4f);
                footerTextRT.offsetMax = new Vector2(-12f, -4f);
                _footerInstructionText = footerTextGO.AddComponent<TextMeshProUGUI>();
                _footerInstructionText.text = footerInstructionText;
                _footerInstructionText.fontSize = instructionFontSize;
                _footerInstructionText.color = Color.white;
                _footerInstructionText.alignment = TextAlignmentOptions.Center;
                _footerInstructionText.enableWordWrapping = true;
                _footerInstructionText.raycastTarget = false;
            }
            else
            {
                _footerInstructionText = null;
            }

            if (!string.IsNullOrEmpty(_pendingQuery))
            {
                _queryPromptText.text = _pendingQuery;
                _pendingQuery = string.Empty;
            }

            GameObject gridBgGO = CreateUIElement("GridBG", panelGO.transform);
            RectTransform gridBgRT = gridBgGO.GetComponent<RectTransform>();
            gridBgRT.anchorMin = new Vector2(0.5f, 1f);
            gridBgRT.anchorMax = new Vector2(0.5f, 1f);
            gridBgRT.pivot = new Vector2(0.5f, 1f);
            gridBgRT.sizeDelta = new Vector2(gridWidth + spacing.x, gridHeight + spacing.y);
            float gridTopOffset = topSection + padding.y - spacing.y * 0.5f;
            gridBgRT.anchoredPosition = new Vector2(0, -gridTopOffset);
            Image gridBgImg = gridBgGO.AddComponent<Image>();
            gridBgImg.color = gridBackground;
            gridBgImg.raycastTarget = false;

            GameObject gridGO = CreateUIElement("GridContainer", gridBgGO.transform);
            RectTransform gridRT = gridGO.GetComponent<RectTransform>();
            _gridLayoutRoot = gridRT;
            StretchFill(gridRT);
            gridRT.offsetMin = new Vector2(spacing.x * 0.5f, spacing.y * 0.5f);
            gridRT.offsetMax = new Vector2(-spacing.x * 0.5f, -spacing.y * 0.5f);
            // Sub-Canvas: per-cell texture swaps during PopulateFromApiResults only re-batch the
            // grid's own draw call, instead of dirtying header / prompt / footer / dim overlay.
            // This is the main perf reason for splitting into multiple Canvases.
            AddSubCanvas(gridGO);

            GridLayoutGroup grid = gridGO.AddComponent<GridLayoutGroup>();
            grid.cellSize = cellSize;
            grid.spacing = spacing;
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.childAlignment = TextAnchor.UpperLeft;
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = columns;

            _cells.Clear();
            for (int i = 0; i < needed; i++)
            {
                GameObject cellGO = CreateUIElement($"Cell_{i}", gridGO.transform);
                RawImage rawImg = cellGO.AddComponent<RawImage>();
                rawImg.color = cellBackground;
                rawImg.raycastTarget = true;

                Button btn = cellGO.AddComponent<Button>();
                btn.targetGraphic = rawImg;
                btn.transition = Selectable.Transition.None;

                // Physics raycasts (e.g. WorkingController) need a collider aligned to the RectTransform.
                // Unity does not size BoxCollider from UI layout; Vector3.one stays 1×1 in local units and misses the visible cell.
                var box = cellGO.AddComponent<BoxCollider>();
                box.isTrigger = false;

                // Add ImageTile component so PCInputController can retrieve the image ID on mouse click.
                cellGO.AddComponent<ImageTile>();

                _cells.Add(new CellSlot { Raw = rawImg, Btn = btn, ImageId = null, OwnedTexture = null });
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(gridRT);
            Canvas.ForceUpdateCanvases();
            ResyncCellPhysicsColliders();
            StartCoroutine(ResyncCellPhysicsCollidersNextFrame());

            GameObject dimGo = CreateUIElement("BackgroundStackDimOverlay", panelGO.transform);
            RectTransform dimRt = dimGo.GetComponent<RectTransform>();
            StretchFill(dimRt);
            // Sub-Canvas: SetActive toggles + alpha changes for the dim overlay don't invalidate
            // the grid / chrome batches.
            AddSubCanvas(dimGo);
            _stackDimOverlay = dimGo.AddComponent<RawImage>();
            _stackDimOverlay.texture = GetOrCreateStackDimSolidTexture();
            _stackDimOverlay.uvRect = new Rect(0f, 0f, 1f, 1f);
            _stackDimOverlay.raycastTarget = false;
            _stackDimOverlay.color = new Color(0f, 0f, 0f, 0f);
            _stackDimOverlay.gameObject.SetActive(false);
            dimGo.transform.SetAsLastSibling();

            _shellBuilt = true;
            Debug.Log($"[ImageGridPanel] Built shell {columns}x{rows} on {name}.");
            RefreshCloseButtonWiring();
            EnsureHeaderNavigateButton();
        }

        void EnsureWorldCanvasAnchorIfConfigured()
        {
            if (!anchorWorldSpaceCanvasWhenEnabled || !_shellBuilt || _canvasRoot == null)
                return;
            if (_canvasRoot.GetComponent<WorldSpaceCanvasSpawnOnce>() != null)
                return;

            var spawnOnce = _canvasRoot.gameObject.AddComponent<WorldSpaceCanvasSpawnOnce>();
            spawnOnce.OverrideSpawnDistance(worldCanvasSpawnDistanceMeters);

            if (_canvasRoot.GetComponent<CanvasSpatialAnchor>() == null)
                _canvasRoot.gameObject.AddComponent<CanvasSpatialAnchor>();
        }

        /// <summary>
        /// Recomputes each cell BoxCollider from its RectTransform after layout (safe to call if grid changes).
        /// </summary>
        public void ResyncCellPhysicsColliders()
        {
            foreach (CellSlot slot in _cells)
            {
                if (slot.Raw == null)
                    continue;
                var rt = slot.Raw.rectTransform;
                var box = slot.Raw.GetComponent<BoxCollider>();
                SyncUiCellCollider(rt, box);
            }
        }

        IEnumerator ResyncCellPhysicsCollidersNextFrame()
        {
            yield return null;
            if (_gridLayoutRoot != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(_gridLayoutRoot);
            Canvas.ForceUpdateCanvases();
            ResyncCellPhysicsColliders();
        }

        static void SyncUiCellCollider(RectTransform rt, BoxCollider box)
        {
            if (rt == null || box == null)
                return;

            Rect r = rt.rect;
            float depth = Mathf.Max(4f, Mathf.Min(Mathf.Max(r.width, 0.01f), Mathf.Max(r.height, 0.01f)) * 0.08f);
            box.center = new Vector3(r.center.x, r.center.y, 0f);
            box.size = new Vector3(Mathf.Max(r.width, 0.01f), Mathf.Max(r.height, 0.01f), depth);
        }

        void ClearCell(CellSlot slot)
        {
            if (slot.Btn != null)
                slot.Btn.onClick.RemoveAllListeners();

            if (slot.OwnedTexture != null)
            {
                Destroy(slot.OwnedTexture);
                slot.OwnedTexture = null;
            }

            if (slot.Raw != null)
            {
                slot.Raw.texture = null;
                slot.Raw.color = cellBackground;
                slot.Raw.raycastTarget = false;
            }

            slot.ImageId = null;
        }

        static void WirePick(CellSlot slot, string id, Action<string> onCellPicked)
        {
            if (slot.Btn == null)
                return;

            slot.Btn.onClick.RemoveAllListeners();
            if (string.IsNullOrEmpty(id) || onCellPicked == null)
            {
                slot.Btn.interactable = false;
                if (slot.Raw != null)
                    slot.Raw.raycastTarget = false;
                return;
            }

            slot.Btn.interactable = true;
            if (slot.Raw != null)
                slot.Raw.raycastTarget = true;
            string captured = id;
            slot.Btn.onClick.AddListener(() => onCellPicked(captured));
        }

        string TryResolveLocalFile(string relativePath)
        {
            return TryResolveDatasetFile(relativePath, out string full) ? full : null;
        }

        /// <summary>
        /// Resolves a server-relative path (e.g. cat/file.jpg) under the same dataset root used for local thumbnails.
        /// </summary>
        public bool TryResolveDatasetFile(string relativePath, out string absolutePath)
        {
            absolutePath = null;
            if (string.IsNullOrEmpty(relativePath))
                return false;

            string root = ResolveImageRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return false;

            string norm = relativePath.Replace('/', Path.DirectorySeparatorChar);
            string full = Path.Combine(root, norm);
            if (!File.Exists(full))
                return false;

            absolutePath = full;
            return true;
        }

        string ResolveImageRoot()
        {
            if (!string.IsNullOrWhiteSpace(absoluteDatasetRoot))
            {
                string trimmed = absoluteDatasetRoot.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (Directory.Exists(trimmed))
                    return trimmed;
                Debug.LogWarning($"[ImageGridPanel] absoluteDatasetRoot does not exist: {trimmed} — falling back to StreamingAssets.");
            }

            return Path.Combine(Application.streamingAssetsPath, imageFolderName);
        }

        List<string> CollectImagePaths(string root)
        {
            var paths = new List<string>();
            if (!Directory.Exists(root))
            {
                Debug.LogWarning($"[ImageGridPanel] Image root not found: {root}");
                return paths;
            }

            string manifestPath = Path.Combine(root, "image_manifest.txt");
            if (File.Exists(manifestPath))
            {
                try
                {
                    foreach (string line in File.ReadAllLines(manifestPath))
                    {
                        string rel = line.Trim();
                        if (rel.Length == 0)
                            continue;
                        rel = rel.Replace('/', Path.DirectorySeparatorChar);
                        string full = Path.Combine(root, rel);
                        if (File.Exists(full))
                        {
                            string ext = Path.GetExtension(full).ToLower();
                            if (ImageExtensions.Contains(ext))
                                paths.Add(full);
                        }
                    }

                    Debug.Log($"[ImageGridPanel] Loaded {paths.Count} paths from image_manifest.txt in {root}");
                    return paths;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ImageGridPanel] Failed to read image_manifest.txt: {ex.Message} — falling back to full scan.");
                }
            }
            else
            {
                Debug.LogWarning(
                    "[ImageGridPanel] image_manifest.txt not found — scanning all files under the dataset (slow for large folders). " +
                    "Start server.py once (same folder as CLIP_IMAGE_ROOT) to generate the manifest, or move the dataset outside Assets and set absoluteDatasetRoot.");
            }

            foreach (string file in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file).ToLower();
                if (ImageExtensions.Contains(ext))
                    paths.Add(file);
            }

            Debug.Log($"[ImageGridPanel] Found {paths.Count} images in {root} (full scan)");
            return paths;
        }

        static List<string> PickRandom(List<string> source, int count)
        {
            var shuffled = source.OrderBy(_ => UnityEngine.Random.value).ToList();
            return shuffled.Take(Mathf.Min(count, shuffled.Count)).ToList();
        }

        static Texture2D LoadTextureFromFile(string path)
        {
            try
            {
                byte[] data = File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (tex.LoadImage(data))
                    return tex;

                Debug.LogWarning($"[ImageGridPanel] Failed to decode: {path}");
                UnityEngine.Object.Destroy(tex);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ImageGridPanel] Error loading {path}: {ex.Message}");
            }

            return null;
        }

        static GameObject CreateUIElement(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        static void StretchFill(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// Adds a nested <see cref="Canvas"/> so this sub-tree batches its draw calls separately
        /// from the parent (left at <c>overrideSorting=false</c> so sibling/hierarchy ordering is
        /// preserved). Also ensures a <see cref="TrackedDeviceGraphicRaycaster"/> is present:
        /// graphics register with their nearest Canvas, so the root raycaster cannot hit Buttons
        /// living inside a sub-Canvas — each sub-Canvas needs its own raycaster for XR controller
        /// pointer events to reach <c>Button.onClick</c> on cells / close / navigate-back slab.
        /// No <see cref="CanvasScaler"/> is added; the root canvas owns scaling.
        /// </summary>
        static Canvas AddSubCanvas(GameObject go)
        {
            Canvas existing = go.GetComponent<Canvas>();
            Canvas canvas = existing != null ? existing : go.AddComponent<Canvas>();
            if (go.GetComponent<GraphicRaycaster>() == null)
                go.AddComponent<TrackedDeviceGraphicRaycaster>();
            return canvas;
        }
    }
}

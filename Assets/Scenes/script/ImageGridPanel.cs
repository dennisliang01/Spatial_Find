using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using TMPro;

namespace Scenes.script
{
    public class ImageGridPanel : MonoBehaviour
    {
        [Header("Mode")]
        [Tooltip("If true, fills the grid with random local images on Start. If false, shell only (for ClipSearchFlowController).")]
        public bool populateRandomOnStart = true;

        [Header("Grid Layout")]
        public int columns = 10;
        public int rows = 9;
        public Vector2 cellSize = new Vector2(90f, 70f);
        public Vector2 spacing = new Vector2(6f, 6f);
        public Vector2 padding = new Vector2(20f, 20f);

        [Header("Canvas")]
        [Tooltip("World-space scale of the canvas. 0.001 means 1 UI unit ≈ 1 mm.")]
        public float canvasScale = 0.001f;

        [Header("Data")]
        public string imageFolderName = "dogs_vs_cats";

        [Tooltip("Full path to the dogs_vs_cats folder. Leave empty to use StreamingAssets/imageFolderName. " +
                 "Point this at the same folder as CLIP_IMAGE_ROOT so Unity does not import 25k images under Assets (much faster Editor loads).")]
        public string absoluteDatasetRoot = "";

        [Header("API thumbnails")]
        [Tooltip("Used for HTTP fallback when the file is not under the local dataset root.")]
        public ClipSearchApiClient apiClient;

        [Tooltip("Cell tint while downloading over HTTP.")]
        public Color loadingCellColor = new Color(0.82f, 0.82f, 0.84f, 1f);

        [Header("Visuals")]
        public Color panelBackground = new Color(0.7f, 0.7f, 0.7f, 1f);
        public Color cellBackground = Color.white;
        public Color gridBackground = new Color(0.55f, 0.55f, 0.55f, 1f);
        public Color headerBarColor = new Color(0.45f, 0.45f, 0.45f, 0.9f);

        [Tooltip("Short label in the top-left of the panel.")]
        public string layerLabelText = "Layer";

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
        RectTransform _gridLayoutRoot;
        RectTransform _headerBarRT;
        Button _headerNavigateButton;
        int _navStageIndex;
        Action<int> _onHeaderNavigateClicked;
        bool _shellBuilt;

        void Awake()
        {
            BuildShellIfNeeded();
        }

        void Start()
        {
            if (!populateRandomOnStart)
                return;

            string imageRoot = ResolveImageRoot();
            int needed = columns * rows;
            List<string> imagePaths = CollectImagePaths(imageRoot);
            List<string> chosen = PickRandom(imagePaths, needed);
            StartCoroutine(PopulateFromLocalPathsCoroutine(chosen));
        }

        public void SetLayerLabel(string text)
        {
            if (_layerLabel != null)
                _layerLabel.text = text ?? "";
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
        }

        void TryFindHeaderBarTransform()
        {
            if (_headerBarRT != null)
                return;
            Transform t = transform.Find("ImageGrid_Canvas/PanelBG/HeaderBar");
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
                SyncUiCellCollider(hitRt, box);
        }

        public void SetSelectionEnabled(bool enabled)
        {
            foreach (CellSlot slot in _cells)
            {
                if (slot.Btn == null)
                    continue;
                slot.Btn.interactable = enabled && !string.IsNullOrEmpty(slot.ImageId);
            }
        }

        /// <summary>
        /// Clears textures and click handlers; optionally loads thumbnails from disk or <see cref="apiClient"/>.
        /// </summary>
        public IEnumerator PopulateFromApiResults(ClipResultRecordDto[] results, Action<string> onCellPicked)
        {
            if (!_shellBuilt)
                BuildShellIfNeeded();

            ClipResultRecordDto[] safe = results ?? Array.Empty<ClipResultRecordDto>();

            for (int i = 0; i < _cells.Count; i++)
            {
                ClearCell(_cells[i]);
                if (i >= safe.Length)
                {
                    WirePick(_cells[i], null, null);
                    continue;
                }

                ClipResultRecordDto rec = safe[i];
                _cells[i].ImageId = rec.id;

                // Set the ImageTile imageId so PCInputController can retrieve it on mouse click.
                if (_cells[i].Raw != null)
                {
                    ImageTile imageTile = _cells[i].Raw.GetComponent<ImageTile>();
                    if (imageTile != null)
                    {
                        imageTile.imageId = rec.id;
                    }
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
                else if (apiClient != null && !string.IsNullOrEmpty(rec.path))
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
            float headerHeight = 50f;
            float labelHeight = 30f;
            float totalWidth = gridWidth + padding.x * 2;
            float totalHeight = gridHeight + padding.y * 2 + headerHeight;

            GameObject canvasGO = new GameObject("ImageGrid_Canvas");
            canvasGO.transform.SetParent(transform, false);

            Canvas canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 1;

            canvasGO.AddComponent<CanvasScaler>();
            canvasGO.AddComponent<GraphicRaycaster>();

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

            GameObject headerGO = CreateUIElement("HeaderBar", panelGO.transform);
            RectTransform headerRT = headerGO.GetComponent<RectTransform>();
            headerRT.anchorMin = new Vector2(0, 1);
            headerRT.anchorMax = new Vector2(1, 1);
            headerRT.pivot = new Vector2(0.5f, 1);
            headerRT.sizeDelta = new Vector2(0, headerHeight);
            headerRT.anchoredPosition = Vector2.zero;
            Image headerImg = headerGO.AddComponent<Image>();
            headerImg.color = headerBarColor;
            _headerBarRT = headerRT;

            GameObject instrGO = CreateUIElement("InstructionText", headerGO.transform);
            RectTransform instrRT = instrGO.GetComponent<RectTransform>();
            StretchFill(instrRT);
            instrRT.offsetMin = new Vector2(120f, 0);
            instrRT.offsetMax = new Vector2(-20f, 0);

            // Remove label text for now to save space; the layer label already indicates what to pick, and instructions were too long to fit legibly.
            // TextMeshProUGUI instrTMP = instrGO.AddComponent<TextMeshProUGUI>();
            // instrTMP.text = "Select the Image closest to what you are looking for.";
            // instrTMP.fontSize = 20;
            // instrTMP.color = Color.white;
            // instrTMP.alignment = TextAlignmentOptions.Center;
            // instrTMP.enableWordWrapping = true;

            GameObject labelGO = CreateUIElement("FirstLayerLabel", panelGO.transform);
            RectTransform labelRT = labelGO.GetComponent<RectTransform>();
            labelRT.anchorMin = new Vector2(0, 1);
            labelRT.anchorMax = new Vector2(0, 1);
            labelRT.pivot = new Vector2(0, 1);
            labelRT.sizeDelta = new Vector2(120f, labelHeight);
            labelRT.anchoredPosition = new Vector2(5f, 0f);
            _layerLabel = labelGO.AddComponent<TextMeshProUGUI>();
            _layerLabel.text = layerLabelText;
            _layerLabel.fontSize = 16;
            _layerLabel.color = new Color(0.75f, 0.75f, 0.75f, 1f);
            _layerLabel.alignment = TextAlignmentOptions.Left;
            _layerLabel.fontStyle = FontStyles.Italic;
            _layerLabel.raycastTarget = false;

            GameObject gridBgGO = CreateUIElement("GridBG", panelGO.transform);
            RectTransform gridBgRT = gridBgGO.GetComponent<RectTransform>();
            gridBgRT.anchorMin = new Vector2(0.5f, 1f);
            gridBgRT.anchorMax = new Vector2(0.5f, 1f);
            gridBgRT.pivot = new Vector2(0.5f, 1f);
            gridBgRT.sizeDelta = new Vector2(gridWidth + spacing.x, gridHeight + spacing.y);
            gridBgRT.anchoredPosition = new Vector2(0, -(headerHeight + padding.y - spacing.y * 0.5f));
            Image gridBgImg = gridBgGO.AddComponent<Image>();
            gridBgImg.color = gridBackground;

            GameObject gridGO = CreateUIElement("GridContainer", gridBgGO.transform);
            RectTransform gridRT = gridGO.GetComponent<RectTransform>();
            _gridLayoutRoot = gridRT;
            StretchFill(gridRT);
            gridRT.offsetMin = new Vector2(spacing.x * 0.5f, spacing.y * 0.5f);
            gridRT.offsetMax = new Vector2(-spacing.x * 0.5f, -spacing.y * 0.5f);

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

            _shellBuilt = true;
            Debug.Log($"[ImageGridPanel] Built shell {columns}x{rows} on {name}.");
            EnsureHeaderNavigateButton();
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
                return;
            }

            slot.Btn.interactable = true;
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
    }
}

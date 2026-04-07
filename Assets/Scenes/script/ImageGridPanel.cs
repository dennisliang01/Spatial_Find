using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
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

        [Header("Data")]
        public string imageFolderName = "dogs_vs_cats";

        [Tooltip("Full path to the dogs_vs_cats folder. Leave empty to use StreamingAssets/imageFolderName. " +
                 "Point this at the same folder as CLIP_IMAGE_ROOT so Unity does not import 25k images under Assets (much faster Editor loads).")]
        public string absoluteDatasetRoot = "";

        [Header("Visuals")]
        public Color panelBackground = new Color(0.7f, 0.7f, 0.7f, 1f);
        public Color cellBackground = Color.white;
        public Color gridBackground = new Color(0.55f, 0.55f, 0.55f, 1f);
        public Color headerBarColor = new Color(0.45f, 0.45f, 0.45f, 0.9f);

        static readonly HashSet<string> ImageExtensions = new HashSet<string>
            { ".jpg", ".jpeg", ".png" };

        void Start()
        {
            string imageRoot = ResolveImageRoot();
            int needed = columns * rows;
            List<string> imagePaths = CollectImagePaths(imageRoot);
            List<string> chosen = PickRandom(imagePaths, needed);

            BuildPanel(chosen);
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

        /// <summary>
        /// Prefer image_manifest.txt (written by server.py on index build) so we do not scan tens of thousands of files.
        /// </summary>
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
                catch (System.Exception ex)
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

        List<string> PickRandom(List<string> source, int count)
        {
            var shuffled = source.OrderBy(_ => Random.value).ToList();
            return shuffled.Take(Mathf.Min(count, shuffled.Count)).ToList();
        }

        void BuildPanel(List<string> imagePaths)
        {
            int needed = columns * rows;
            float gridWidth = columns * cellSize.x + (columns - 1) * spacing.x;
            float gridHeight = rows * cellSize.y + (rows - 1) * spacing.y;
            float headerHeight = 50f;
            float labelHeight = 30f;
            float totalWidth = gridWidth + padding.x * 2;
            float totalHeight = gridHeight + padding.y * 2 + headerHeight;

            // --- Canvas ---
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

            // --- Panel background ---
            GameObject panelGO = CreateUIElement("PanelBG", canvasGO.transform);
            RectTransform panelRT = panelGO.GetComponent<RectTransform>();
            StretchFill(panelRT);
            Image panelImg = panelGO.AddComponent<Image>();
            panelImg.color = panelBackground;

            // --- Header bar ---
            GameObject headerGO = CreateUIElement("HeaderBar", panelGO.transform);
            RectTransform headerRT = headerGO.GetComponent<RectTransform>();
            headerRT.anchorMin = new Vector2(0, 1);
            headerRT.anchorMax = new Vector2(1, 1);
            headerRT.pivot = new Vector2(0.5f, 1);
            headerRT.sizeDelta = new Vector2(0, headerHeight);
            headerRT.anchoredPosition = Vector2.zero;
            Image headerImg = headerGO.AddComponent<Image>();
            headerImg.color = headerBarColor;

            // --- Instruction text ---
            GameObject instrGO = CreateUIElement("InstructionText", headerGO.transform);
            RectTransform instrRT = instrGO.GetComponent<RectTransform>();
            StretchFill(instrRT);
            instrRT.offsetMin = new Vector2(120f, 0);
            instrRT.offsetMax = new Vector2(-20f, 0);
            TextMeshProUGUI instrTMP = instrGO.AddComponent<TextMeshProUGUI>();
            instrTMP.text = "Select the Image closest to what you are looking for.";
            instrTMP.fontSize = 20;
            instrTMP.color = Color.white;
            instrTMP.alignment = TextAlignmentOptions.Center;
            instrTMP.enableWordWrapping = true;

            // --- "First Layer" label ---
            GameObject labelGO = CreateUIElement("FirstLayerLabel", panelGO.transform);
            RectTransform labelRT = labelGO.GetComponent<RectTransform>();
            labelRT.anchorMin = new Vector2(0, 1);
            labelRT.anchorMax = new Vector2(0, 1);
            labelRT.pivot = new Vector2(0, 1);
            labelRT.sizeDelta = new Vector2(120f, labelHeight);
            labelRT.anchoredPosition = new Vector2(5f, 0f);
            TextMeshProUGUI labelTMP = labelGO.AddComponent<TextMeshProUGUI>();
            labelTMP.text = "First Layer";
            labelTMP.fontSize = 16;
            labelTMP.color = new Color(0.75f, 0.75f, 0.75f, 1f);
            labelTMP.alignment = TextAlignmentOptions.Left;
            labelTMP.fontStyle = FontStyles.Italic;

            // --- Grid container (colored background behind cells to show as grid lines) ---
            GameObject gridBgGO = CreateUIElement("GridBG", panelGO.transform);
            RectTransform gridBgRT = gridBgGO.GetComponent<RectTransform>();
            gridBgRT.anchorMin = new Vector2(0.5f, 1f);
            gridBgRT.anchorMax = new Vector2(0.5f, 1f);
            gridBgRT.pivot = new Vector2(0.5f, 1f);
            gridBgRT.sizeDelta = new Vector2(gridWidth + spacing.x, gridHeight + spacing.y);
            gridBgRT.anchoredPosition = new Vector2(0, -(headerHeight + padding.y - spacing.y * 0.5f));
            Image gridBgImg = gridBgGO.AddComponent<Image>();
            gridBgImg.color = gridBackground;

            // --- Grid layout container ---
            GameObject gridGO = CreateUIElement("GridContainer", gridBgGO.transform);
            RectTransform gridRT = gridGO.GetComponent<RectTransform>();
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

            // --- Spawn cells ---
            for (int i = 0; i < needed; i++)
            {
                GameObject cellGO = CreateUIElement($"Cell_{i}", gridGO.transform);
                RawImage rawImg = cellGO.AddComponent<RawImage>();
                rawImg.color = cellBackground;

                if (i < imagePaths.Count)
                {
                    Texture2D tex = LoadTexture(imagePaths[i]);
                    if (tex != null)
                        rawImg.texture = tex;
                }
            }

            Debug.Log($"[ImageGridPanel] Built {columns}x{rows} grid with {imagePaths.Count} images loaded.");
        }

        Texture2D LoadTexture(string path)
        {
            try
            {
                byte[] data = File.ReadAllBytes(path);
                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (tex.LoadImage(data))
                    return tex;

                Debug.LogWarning($"[ImageGridPanel] Failed to decode: {path}");
                Object.Destroy(tex);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[ImageGridPanel] Error loading {path}: {ex.Message}");
            }
            return null;
        }

        GameObject CreateUIElement(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        void StretchFill(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}

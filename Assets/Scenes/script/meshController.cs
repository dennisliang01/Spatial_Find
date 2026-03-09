using UnityEngine;
using System.IO;

namespace Scenes.script
{
    public class MeshController : MonoBehaviour
    {
        [Header("Plane Settings")]
        public GameObject planePrefab;
        public float offsetDistance = 3f;
        public Vector3 scaleReduction = new Vector3(0.8f, 1f, 0.8f);

        [Header("Data Settings")]
        public string relativePath; //passed from selected subpanel
        public string relDisplayPath;
        private string myPath;
        private string myDisplayPath;
        public bool isLeafNode = false;
        public bool isBasePlane = false;
        public string folderPath;
        public string displayPath;

        [Header("Subpanel Settings")]
        public Vector3 spawnDirection = Vector3.down;
        public GameObject currentChildPlane;
        public bool hasChild = false;

        [Header("Label Settings")]
        public GameObject textLabelPrefab;
        public float labelHeight = -0.008f;
        public float labelz = 0.07f;

        void Start()
        {
            if (isBasePlane)
            {
                folderPath = Path.Combine(Application.streamingAssetsPath, relativePath);
                displayPath = Path.Combine(Application.streamingAssetsPath, relDisplayPath);
            }
            ;
            myDisplayPath = displayPath;
            myPath = folderPath;
            SetupPlaneVisuals();

        }



        //data loading setup 
        void SetupPlaneVisuals()
        {

            Debug.Log("=== PATH DEBUG INFO ===");
            Debug.Log($"Raw folderPath: '{folderPath}'");

            if (IsLeafNode())
            {
                //SetupAsImagePlane();

            }
            else
            {
                SetupAsFolderPlane();
            }
            SpawnFolderLabel();

        }

        bool IsEmptyFolder()
        {
            if (string.IsNullOrEmpty(folderPath))
            {
                Debug.LogWarning("Current folder path is null or empty");
                return false;
            }
            if (IsFilePath(folderPath))
            {
                return false;
            }
            bool hasNoSubfolders = Directory.GetDirectories(folderPath).Length == 0;
            bool hasImageFiles = Directory.GetFiles(folderPath, "*.jpg").Length > 0;
            return hasNoSubfolders && !hasImageFiles;
        }

        //deal with folders with no child 
        bool IsLeafNode()
        {
            if (string.IsNullOrEmpty(folderPath))
            {
                Debug.LogWarning("Folder path is null or empty");
                return false;
            }

            if (IsFilePath(folderPath))
            {
                Debug.Log($"Path is a file, treating as leaf node: {folderPath}");
                return true;
            }


            bool hasNoSubfolders = Directory.GetDirectories(folderPath).Length == 0;
            bool hasImageFiles = Directory.GetFiles(folderPath, "*.jpg").Length > 0;
            isLeafNode = hasNoSubfolders && hasImageFiles;
            return isLeafNode;

        }

        bool IsFilePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            string extension = Path.GetExtension(path).ToLower();
            return !string.IsNullOrEmpty(extension) &&
                (extension == ".jpg" || extension == ".jpeg" || extension == ".png");
        }


        void SetupAsFolderPlane()
        {
            //need to get the image from image collage 
            //no need to change file path
        }

        //this deal with the last level (image level)
        void SetupAsImagePlane()
        {
            if (!string.IsNullOrEmpty(folderPath))
            {
                string[] imageFiles = System.IO.Directory.GetFiles(folderPath, "*.jpg");
                if (imageFiles.Length > 0)
                {
                    //in this case the path will be image1.jpg 
                    //set this to the image_texture for the card script 
                }
            }
            SpawnFolderLabel();
        }



        public void SpawnChildPlane()
        {

            if (IsEmptyFolder())
            {
                Debug.Log("This is a empty folder - cannot spawn children");
                return;
            }

            if (planePrefab == null)
            {
                currentChildPlane = GameObject.CreatePrimitive(PrimitiveType.Plane);
            }
            else
            {
                currentChildPlane = Instantiate(planePrefab);
            }

            currentChildPlane.name = $"{gameObject.name}_Child";
            currentChildPlane.transform.SetParent(transform);

            Vector3 spawnPosition = CalculateChainPosition();
            currentChildPlane.transform.position = spawnPosition;
            currentChildPlane.transform.rotation = transform.rotation;

            Vector3 parentWorldScale = transform.lossyScale;
            Vector3 desiredWorldScale = new Vector3(
                parentWorldScale.x * scaleReduction.x,
                parentWorldScale.y * scaleReduction.y,
                parentWorldScale.z * scaleReduction.z
            );

            Vector3 requiredLocalScale = new Vector3(
                desiredWorldScale.x / parentWorldScale.x,
                desiredWorldScale.y / parentWorldScale.y,
                desiredWorldScale.z / parentWorldScale.z
            );


            currentChildPlane.transform.localScale = requiredLocalScale;

            //passing in the data path to spawned child
            Debug.Log($" Passing '{folderPath}' to the spawned child");
            MeshController childController = currentChildPlane.GetComponent<MeshController>();
            if (childController != null)
            {
                childController.folderPath = this.folderPath;
                childController.displayPath = this.displayPath;
                Debug.Log($"Passed folder path to child: {folderPath}");
                Debug.Log($"Passed display path to child: {displayPath}");
            }

            Renderer childRenderer = currentChildPlane.GetComponent<Renderer>();
            if (childRenderer != null)
            {
                //childRenderer.material.color = GetNextColor(GetComponent<Renderer>().material.color);
            }

            hasChild = true;
            Debug.Log("Spawned child plane with path: " + folderPath + " display: " + displayPath);
        }

        Vector3 CalculateChainPosition()
        {
            Vector3 direction = transform.TransformDirection(spawnDirection);
            int planeDepth = CountPlanesInChain();

            Debug.Log($"Calculating spawn position:");
            Debug.Log($"- Direction: {direction}");
            Debug.Log($"- Plane depth in chain: {planeDepth}");
            Debug.Log($"- Offset distance: {offsetDistance}");
            Debug.Log($"- Total offset: {offsetDistance * planeDepth}");
            Debug.Log($"- From position: {transform.position}");

            float heightAdjustment = CalculateHeightAdjustment();
            float actualOffset = offsetDistance * (1f / planeDepth);
            Vector3 spawnPos = transform.position + direction * actualOffset;
            spawnPos.y += heightAdjustment;



            return spawnPos;
        }

        float CalculateHeightAdjustment()
        {
            Renderer parentRenderer = GetComponent<Renderer>();
            if (currentChildPlane != null)
            {
                Renderer childRenderer = currentChildPlane.GetComponent<Renderer>();

                if (parentRenderer != null && childRenderer != null)
                {
                    float parentHeight = parentRenderer.bounds.size.y;
                    float childHeight = childRenderer.bounds.size.y;

                    return (childHeight - parentHeight) / 3f;
                }
            }

            return 0f;
        }

        int CountPlanesInChain()
        {
            int count = 0;
            Transform current = transform;

            while (current != null && current.GetComponent<MeshController>() != null)
            {
                count++;
                Debug.Log($"  Plane in chain: {current.name}");
                current = current.parent;
            }

            return count;
        }

        public void RemoveChildPlane()
        {
            if (currentChildPlane != null)
            {
                Destroy(currentChildPlane);
                hasChild = false;
                Debug.Log("Removed child plane");
                folderPath = myPath;
                displayPath = myDisplayPath;

            }
        }

        Color GetNextColor(Color currentColor)
        {
            float h, s, v;
            Color.RGBToHSV(currentColor, out h, out s, out v);
            h = (h + 0.3f) % 1f;
            s = Mathf.Clamp(s - 0.1f, 0.3f, 1f);
            v = Mathf.Clamp(v - 0.1f, 0.7f, 1f);
            return Color.HSVToRGB(h, s, v);
        }

        private void SpawnFolderLabel()
        {
            if (textLabelPrefab == null)
            {
                Debug.LogWarning("[MeshController] textLabelPrefab is not assigned. Cannot create folder label.");
                return;
            }

            string folderName = "Unknown";
            if (!string.IsNullOrEmpty(folderPath))
            {
                folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(folderName)) folderName = folderPath;
            }

            Transform existing = transform.Find($"{gameObject.name}_FolderLabel");
            if (existing != null) Destroy(existing.gameObject);

            GameObject spawnedLabel = Instantiate(textLabelPrefab);
            spawnedLabel.name = $"{gameObject.name}_FolderLabel";
            spawnedLabel.transform.SetParent(transform, false);



            spawnedLabel.transform.localPosition = new Vector3(-0.06f, labelHeight, labelz);

            spawnedLabel.transform.localRotation = Quaternion.Euler(-90f, 0f, 180f);


            Renderer planeR = GetComponent<Renderer>();
            Vector3 baseScale = Vector3.one;
            if (planeR != null)
            {
                Vector3 bounds = planeR.bounds.size;
                float uniform = Mathf.Max(bounds.x, bounds.y);
                baseScale = new Vector3(uniform, uniform, uniform);
            }
            float localLabelScale = 0.0003f;
            spawnedLabel.transform.localScale = baseScale * localLabelScale;

            // Find TMP inside the prefab
            var tmp = spawnedLabel.GetComponentInChildren<TMPro.TextMeshPro>();
            if (tmp == null)
            {
                Debug.LogError("[MeshController] TextMeshPro component not found in textLabelPrefab!");
                return;
            }

            tmp.text = folderName;
            tmp.color = Color.white;
            tmp.fontSize = 10;
            tmp.alignment = TMPro.TextAlignmentOptions.Center;

            tmp.ForceMeshUpdate();
            Bounds textBounds = tmp.textBounds;

            if (tmp.fontMaterial != null)
            {
                Material instMat = new Material(tmp.fontMaterial);

                // Do not write to depth buffer
                instMat.SetInt("_ZWrite", 0);

                // Render after everything else (Overlay queue)
                instMat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Overlay;

                tmp.fontMaterial = instMat; // assign instance back
            }

        }

    }
}
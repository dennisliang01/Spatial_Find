using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using Scenes.script;
using System.IO;
using UnityEngine.UI;

using TMPro;


public class CardQuadSpawner : MonoBehaviour
{
    public GameObject quadPrefab;
    public Texture imageTexture;
    public string textLabel;
    public float worldOffset = -0.167f;
    private Camera targetCamera;
    public GameObject textLabelPrefab;
    private string imageFileName;
    public Material glassMaterial;
    public float labelsize = 0.027f;

    //Hard coded the rotation and scaling 
    public float rotaionZ = -10.0f;
    public float scaleMultiplier = 4.5f;
    GameObject spawnedQuad;
    GameObject spawnedLabel;

    void Start()
    {
        if (targetCamera == null)
        {
            GameObject xrOriginGO = GameObject.Find("XR Origin");
            if (xrOriginGO != null)
            {
                targetCamera = xrOriginGO.GetComponentInChildren<Camera>();
            }

            if (targetCamera == null)
            {
                targetCamera = Camera.main;
            }
        }
        if (targetCamera == null)
        {
            Debug.LogError("[CardQuadSpawner] No camera found for spawning quad.");
            return;
        }

        LoadImageTexture();
        Debug.Log($"Image texture is null: {imageTexture == null}");

        if (imageTexture != null)
        {
            Debug.Log($"has image Texture, spawn quad");
            SpawnQuadInFrontOfCard(quadPrefab, imageTexture, 0.01f);
        }
    }

    void LoadImageTexture()
    {
        var subPanelController = GetComponent<SubPanelController>();
        if (subPanelController == null)
        {
            Debug.LogError("Parent does not have a subPanelController component.");
            return;
        }
        bool level2 = subPanelController.isLevel2;
        string path = null;
        if (level2)
        {
            // Debug.LogError("Setting path to displaypath in cardQuadSpawner");
            path = subPanelController.displayPath;
        }
        else
        {
            path = subPanelController.dataPath;
        }
        bool setImage = subPanelController.setImage;
        if (setImage)
        {
            if (!File.Exists(path))
            {
                Debug.LogError("Image file not found at path: " + path);
                return;
            }
            imageFileName = Path.GetFileNameWithoutExtension(path);
            byte[] imageData = File.ReadAllBytes(path);
            Texture2D tex = new Texture2D(2, 2);
            if (tex.LoadImage(imageData))
            {
                imageTexture = tex;
                Debug.Log($"setting image tex to {path}");

            }
            else
            {
                Debug.LogError("Failed to load image from path: " + path);
            }

        }
    }

    private string GetImageTitle(string path)
    {
        if (string.IsNullOrEmpty(path)) return "Untitled";

        string fileName = Path.GetFileNameWithoutExtension(path);
        fileName = System.Text.RegularExpressions.Regex.Replace(fileName, @"[\d_-]", " ").Trim();
        fileName = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(fileName.ToLower());

        return fileName;
    }

    public GameObject SpawnQuadInFrontOfCard(GameObject quadPrefab, Texture tex, float localZOffset = 0.01f)
    {
        if (quadPrefab == null) return null;

        var r = GetComponent<Renderer>();
        if (r == null) return null;

        Bounds b = r.bounds;
        Vector3 c = b.center;
        Vector3 e = b.extents;

        // Get the 8 corners of the bounding box
        var corners = new List<Vector3>(8);
        for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    corners.Add(c + new Vector3(sx * e.x, sy * e.y, sz * e.z));

        // plane's foward direction
        Vector3 outwardDirection = transform.forward;

        // Find the front face corners 
        var cornerScores = new List<(Vector3 pos, float score)>(8);
        for (int i = 0; i < corners.Count; i++)
        {
            // Score by how much they're in the outward direction from center
            // Score by how much they're in the outward direction from center
            Vector3 toCorner = (corners[i] - c).normalized;
            float score = Vector3.Dot(toCorner, outwardDirection);
            cornerScores.Add((corners[i], score));
        }

        // Sort by highest score (most outward)
        cornerScores.Sort((a, b) => b.score.CompareTo(a.score));

        // Take the front 4 corners
        var front4 = new List<Vector3>(4);
        for (int i = 0; i < 4; i++) front4.Add(cornerScores[i].pos);

        Vector3 frontCenterWorld = Vector3.zero;
        foreach (var v in front4) frontCenterWorld += v;
        frontCenterWorld /= front4.Count;

        Vector3 localRight = Vector3.right;
        Vector3 localUp = Vector3.up;

        Vector3 frontCenterLocal = transform.InverseTransformPoint(frontCenterWorld);

        float minR = float.PositiveInfinity, maxR = float.NegativeInfinity;
        float minU = float.PositiveInfinity, maxU = float.NegativeInfinity;

        foreach (var worldCorner in front4)
        {
            Vector3 localCorner = transform.InverseTransformPoint(worldCorner);
            Vector3 rel = localCorner - frontCenterLocal;

            float rProj = Vector3.Dot(rel, localRight);
            float uProj = Vector3.Dot(rel, localUp);

            if (rProj < minR) minR = rProj;
            if (rProj > maxR) maxR = rProj;
            if (uProj < minU) minU = uProj;
            if (uProj > maxU) maxU = uProj;
        }

        float localWidth = Mathf.Max(0.0001f, maxR - minR);
        float localHeight = Mathf.Max(0.0001f, maxU - minU);

        if (localWidth < 0.01f && localHeight > 0.1f)
        {
            Debug.LogWarning($"Degenerate width detected on '{name}' — falling back to local bounds");
            localWidth = transform.localScale.x * 0.1f;
        }

        Vector3 quadLocalPos = frontCenterLocal + new Vector3(0, 0, localZOffset);

        // spawning
        if (spawnedQuad != null) Destroy(spawnedQuad);

        spawnedQuad = Instantiate(quadPrefab);
        spawnedQuad.name = $"{name}_FrontQuad";
        spawnedQuad.transform.SetParent(transform, false);
        spawnedQuad.transform.localPosition = quadLocalPos;

        // Calculate scale 
        float uniformLocalScale = Mathf.Max(localWidth, localHeight) * scaleMultiplier;
        spawnedQuad.transform.localScale = new Vector3(uniformLocalScale, uniformLocalScale, uniformLocalScale);
        spawnedQuad.transform.localRotation = Quaternion.Euler(0f, 0f, rotaionZ);

        Transform child = spawnedQuad.transform.GetChild(0);
        if (child != null)
        {
            //offset between quad and card
            float childYOffset = -0.014f;
            child.localPosition = new Vector3(0f, childYOffset, worldOffset);
        }

        Debug.Log($"Quad local position: {quadLocalPos}, scale: {uniformLocalScale}");

        // Set material/texture
        Renderer quadR = spawnedQuad.GetComponentInChildren<Renderer>();
        if (quadR != null && tex != null)
        {
            Material mat = quadR.sharedMaterial != null ?
                new Material(quadR.sharedMaterial) :
                new Material(Shader.Find("Standard"));

            mat.mainTexture = tex;
            mat.mainTextureScale = new Vector2(1f, -1f);
            quadR.material = mat;
        }

        SpawnTextLabel(quadLocalPos, uniformLocalScale);
        return spawnedQuad;
    }

    private void SpawnTextLabel(Vector3 quadLocalPos, float quadScale)
    {
        string imageTitle;
        if (textLabel == "")
        {
            var subPanelController = GetComponent<SubPanelController>();
            string imgName = imageFileName;
            Debug.Log($"imageFileName {imageFileName}");
            imageTitle = imageFileName;
        }
        else
        {
            imageTitle = textLabel;
        }

        spawnedLabel = Instantiate(textLabelPrefab);
        spawnedLabel.name = $"{name}_ImageLabel";
        spawnedLabel.transform.SetParent(transform, false);


        float labelZOffset = -0.018f; // Slightly in front of the quad

        Vector3 labelLocalPos = quadLocalPos + new Vector3(0f, -0.005f, labelZOffset);
        spawnedLabel.transform.localPosition = labelLocalPos;
        spawnedLabel.transform.localRotation = Quaternion.Euler(-90f + rotaionZ, 0f, 180f);

        // Scale the label relative to the quad
        float labelScale = quadScale * labelsize; // Adjust this multiplier to get the right size
        spawnedLabel.transform.localScale = new Vector3(labelScale, labelScale, labelScale);


        // Set the text
        var tmp = spawnedLabel.GetComponentInChildren<TextMeshPro>();
        if (tmp == null)
        {
            Debug.LogError("TextMeshPro component not found!");
            return;
        }

        tmp.text = imageTitle;
        tmp.color = Color.white;
        tmp.fontSize = 8;
        tmp.alignment = TextAlignmentOptions.Center;

        tmp.ForceMeshUpdate();
        Bounds textBounds = tmp.textBounds;

        // --- Make a safe instance of the TMP material (do NOT modify shared material) ---
        if (tmp.fontMaterial != null)
        {
            Material instMat = new Material(tmp.fontMaterial);
            instMat.SetInt("_ZWrite", 1);                                  // write depth
            instMat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry + 1; // render after opaque geometry
            tmp.fontMaterial = instMat; // assign instance back to this TMP object
        }

        Transform oldBg = tmp.transform.Find("TMP_Background");
        if (oldBg != null) GameObject.Destroy(oldBg.gameObject);

        GameObject bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
        bg.name = "TMP_Background";
        bg.transform.SetParent(tmp.transform, false);


        float zOffset = 0.02f;
        bg.transform.localPosition = new Vector3(0f, 0f, zOffset);
        bg.transform.localRotation = Quaternion.identity;

        var col = bg.GetComponent<Collider>();
        if (col != null) GameObject.Destroy(col);

        float padX = 1.10f;
        float padY = 1.40f;
        Vector3 bgScale = new Vector3(Mathf.Max(0.001f, textBounds.size.x * padX),
                                    Mathf.Max(0.001f, textBounds.size.y * padY),
                                    1f);
        bg.transform.localScale = bgScale;

        var bgRenderer = bg.GetComponent<MeshRenderer>();
        bgRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        bgRenderer.receiveShadows = false;
        bgRenderer.material.SetInt("_ZWrite", 0);
        bgRenderer.material = glassMaterial;
        bgRenderer.material.SetColor("_BaseColor", new Color(0f, 0f, 0f, 1f));
        bgRenderer.material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        bgRenderer.material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        bgRenderer.material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

        // Text setup
        tmp.fontMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 1;

    }


}

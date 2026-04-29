using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;

namespace Scenes.script
{
    [Serializable]
    public class ClipSearchRequestBody
    {
        public string query;
        public int stage;
        public string[] candidates;
        public string[] selected;
    }

    [Serializable]
    public class ClipSearchResponseDto
    {
        public int stage;
        public int total;
        public ClipResultRecordDto[] results;
    }

    [Serializable]
    public class ClipResultRecordDto
    {
        public string id;
        public string path;
        public string category;
        public float probability;
    }

    [Serializable]
    public class ClipHealthResponseDto
    {
        public string status;
        public int image_count;
        public bool index_built;
        public bool cuda_available;
        public string model;
        public string image_root;
        public int[] stage_sizes;
        public int embedding_dim;
    }

    /// <summary>
    /// HTTP client for the CLIP search FastAPI server (server.py).
    /// Set <see cref="baseUrl"/> to your machine's LAN IP when running on a standalone headset (not localhost).
    /// </summary>
    public class ClipSearchApiClient : MonoBehaviour
    {
        [Tooltip("e.g. http://127.0.0.1:8000 — use PC LAN IP for device builds")]
        public string baseUrl = "http://127.0.0.1:8000";

        [Tooltip("Fired after a successful POST /search (read LastSearchResponse)")]
        public UnityEvent onSearchSuccess;

        [Tooltip("Fired on network or parse errors (read LastError)")]
        public UnityEvent onSearchError;

        public ClipSearchResponseDto LastSearchResponse { get; private set; }
        public string LastError { get; private set; }

        public ClipHealthResponseDto LastHealthResponse { get; private set; }

        public string GetImageUrl(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return baseUrl.TrimEnd('/');

            string root = baseUrl.TrimEnd('/');
            string norm = relativePath.Replace('\\', '/');
            string[] parts = norm.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            sb.Append(root).Append("/images/");
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append('/');
                sb.Append(Uri.EscapeDataString(parts[i]));
            }
            return sb.ToString();
        }

        public IEnumerator SearchCoroutine(
            string query,
            int stage,
            string[] candidates = null,
            string[] selected = null)
        {
            LastError = null;
            LastSearchResponse = null;

            var body = new ClipSearchRequestBody
            {
                query = query ?? "",
                stage = stage,
                candidates = candidates ?? Array.Empty<string>(),
                selected = selected ?? Array.Empty<string>(),
            };

            string json = JsonUtility.ToJson(body);
            string url = baseUrl.TrimEnd('/') + "/search";

            using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
                req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");

                yield return req.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                if (req.result != UnityWebRequest.Result.Success)
#else
                if (req.isNetworkError || req.isHttpError)
#endif
                {
                    LastError = req.error + (req.responseCode > 0 ? $" (HTTP {req.responseCode})" : "");
                    if (string.IsNullOrEmpty(LastError))
                        LastError = $"HTTP {req.responseCode}";
                    onSearchError?.Invoke();
                    yield break;
                }

                try
                {
                    LastSearchResponse = JsonUtility.FromJson<ClipSearchResponseDto>(req.downloadHandler.text);
                }
                catch (Exception ex)
                {
                    LastError = "JSON parse: " + ex.Message;
                    onSearchError?.Invoke();
                    yield break;
                }

                onSearchSuccess?.Invoke();
            }
        }

        public IEnumerator HealthCoroutine(UnityEvent onSuccess = null, UnityEvent onError = null)
        {
            LastHealthResponse = null;
            LastError = null;
            string url = baseUrl.TrimEnd('/') + "/health";

            using (var req = UnityWebRequest.Get(url))
            {
                yield return req.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                if (req.result != UnityWebRequest.Result.Success)
#else
                if (req.isNetworkError || req.isHttpError)
#endif
                {
                    LastError = req.error ?? $"HTTP {req.responseCode}";
                    onError?.Invoke();
                    yield break;
                }

                try
                {
                    LastHealthResponse = JsonUtility.FromJson<ClipHealthResponseDto>(req.downloadHandler.text);
                }
                catch (Exception ex)
                {
                    LastError = "JSON parse: " + ex.Message;
                    onError?.Invoke();
                    yield break;
                }

                onSuccess?.Invoke();
            }
        }
    }
}

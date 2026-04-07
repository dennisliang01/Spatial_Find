using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using TMPro;

namespace Scenes.script
{
    /// <summary>
    /// Drives the 4-stage CLIP search flow against <see cref="ClipSearchApiClient"/>, matching server.py / browser POC semantics.
    /// </summary>
    public class ClipSearchFlowController : MonoBehaviour
    {
        public ClipSearchApiClient apiClient;
        public ImageGridPanel panelStage90;
        public ImageGridPanel panelStage30;
        public ImageGridPanel panelStage10;

        [Tooltip("Optional. If set, stage-4 result is shown here (needs a Canvas parent in the scene).")]
        public RawImage finalResultRawImage;

        public TMP_InputField queryInput;
        public Button submitButton;

        [TextArea(1, 3)]
        public string debugQuery = "a cat";

        public bool submitDebugQueryOnStart;

        string _query;
        int _lastResponseStage;
        string[] _lastCandidateIds = Array.Empty<string>();
        readonly List<string> _selected = new List<string>();
        bool _busy;

        void Awake()
        {
            PropagateApiClient();
            if (submitButton != null)
                submitButton.onClick.AddListener(SubmitQueryFromUi);
        }

        void Start()
        {
            StartCoroutine(StartFlowRoutine());
        }

        IEnumerator StartFlowRoutine()
        {
            yield return null;
            if (submitDebugQueryOnStart && !string.IsNullOrWhiteSpace(debugQuery))
                BeginSearch(debugQuery.Trim());
        }

        void PropagateApiClient()
        {
            if (apiClient == null)
                return;
            if (panelStage90 != null)
                panelStage90.apiClient = apiClient;
            if (panelStage30 != null)
                panelStage30.apiClient = apiClient;
            if (panelStage10 != null)
                panelStage10.apiClient = apiClient;
        }

        public void SubmitQueryFromUi()
        {
            string q = queryInput != null ? queryInput.text.Trim() : "";
            if (string.IsNullOrEmpty(q))
            {
                Debug.LogWarning("[ClipSearchFlow] Empty query.");
                return;
            }

            BeginSearch(q);
        }

        public void BeginSearch(string query)
        {
            if (_busy)
                return;
            if (string.IsNullOrWhiteSpace(query))
            {
                Debug.LogWarning("[ClipSearchFlow] BeginSearch: empty query.");
                return;
            }

            if (apiClient == null)
            {
                Debug.LogError("[ClipSearchFlow] Assign ClipSearchApiClient.");
                return;
            }

            _query = query.Trim();
            _selected.Clear();
            _lastCandidateIds = Array.Empty<string>();
            _lastResponseStage = 0;

            ResetUiStateForNewRun();
            StartCoroutine(RunSearchStage(1, Array.Empty<string>(), Array.Empty<string>()));
        }

        void ResetUiStateForNewRun()
        {
            if (panelStage30 != null)
                panelStage30.gameObject.SetActive(false);
            if (panelStage10 != null)
                panelStage10.gameObject.SetActive(false);
            if (panelStage90 != null)
                panelStage90.gameObject.SetActive(true);

            SetAllPanelsNonInteractive();

            if (finalResultRawImage != null && finalResultRawImage.texture != null)
            {
                Destroy(finalResultRawImage.texture);
                finalResultRawImage.texture = null;
            }
        }

        void SetAllPanelsNonInteractive()
        {
            panelStage90?.SetSelectionEnabled(false);
            panelStage30?.SetSelectionEnabled(false);
            panelStage10?.SetSelectionEnabled(false);
        }

        IEnumerator RunSearchStage(int stage, string[] candidates, string[] selected)
        {
            _busy = true;
            SetAllPanelsNonInteractive();

            yield return StartCoroutine(apiClient.SearchCoroutine(_query, stage, candidates, selected));

            if (!string.IsNullOrEmpty(apiClient.LastError))
            {
                Debug.LogWarning("[ClipSearchFlow] Search error: " + apiClient.LastError);
                _busy = false;
                ReenablePanelForStage(stage);
                yield break;
            }

            ClipSearchResponseDto resp = apiClient.LastSearchResponse;
            if (resp == null || resp.results == null)
            {
                Debug.LogWarning("[ClipSearchFlow] Empty response.");
                _busy = false;
                yield break;
            }

            _lastResponseStage = resp.stage;
            _lastCandidateIds = IdsFromResults(resp.results);

            if (resp.stage >= 4)
            {
                yield return StartCoroutine(ShowFinalResult(resp.results));
                _busy = false;
                yield break;
            }

            ImageGridPanel target = PanelForStage(resp.stage);
            if (target == null)
            {
                Debug.LogError("[ClipSearchFlow] No panel for stage " + resp.stage);
                _busy = false;
                yield break;
            }

            ActivateThroughStage(resp.stage);
            yield return StartCoroutine(target.PopulateFromApiResults(resp.results, OnUserPickedImage));
            target.SetSelectionEnabled(true);
            _busy = false;
        }

        void ReenablePanelForStage(int stage)
        {
            if (stage <= 0 || stage >= 4)
                return;
            PanelForStage(stage)?.SetSelectionEnabled(true);
        }

        ImageGridPanel PanelForStage(int stage1to3)
        {
            if (stage1to3 == 1)
                return panelStage90;
            if (stage1to3 == 2)
                return panelStage30;
            if (stage1to3 == 3)
                return panelStage10;
            return null;
        }

        void ActivateThroughStage(int stage)
        {
            if (panelStage90 != null)
                panelStage90.gameObject.SetActive(stage >= 1);
            if (panelStage30 != null)
                panelStage30.gameObject.SetActive(stage >= 2);
            if (panelStage10 != null)
                panelStage10.gameObject.SetActive(stage >= 3);
        }

        static string[] IdsFromResults(ClipResultRecordDto[] results)
        {
            if (results == null || results.Length == 0)
                return Array.Empty<string>();

            var ids = new string[results.Length];
            for (int i = 0; i < results.Length; i++)
                ids[i] = results[i].id ?? "";
            return ids;
        }

        void OnUserPickedImage(string imageId)
        {
            if (_busy || string.IsNullOrEmpty(imageId))
                return;

            if (_lastCandidateIds == null || _lastCandidateIds.Length == 0)
            {
                Debug.LogWarning("[ClipSearchFlow] No candidate ids from last response; cannot refine.");
                return;
            }

            _selected.Add(imageId);
            int nextStage = _lastResponseStage + 1;
            if (nextStage > 4)
                return;

            _busy = true;
            StartCoroutine(RunSearchStage(nextStage, _lastCandidateIds, _selected.ToArray()));
        }

        IEnumerator ShowFinalResult(ClipResultRecordDto[] results)
        {
            SetAllPanelsNonInteractive();

            if (results == null || results.Length == 0)
            {
                Debug.LogWarning("[ClipSearchFlow] Final stage returned no results.");
                yield break;
            }

            ClipResultRecordDto rec = results[0];
            Debug.Log($"[ClipSearchFlow] Final pick: {rec.id} (p={rec.probability:F4})");

            if (finalResultRawImage != null)
            {
                if (panelStage90 != null)
                    panelStage90.gameObject.SetActive(false);
                if (panelStage30 != null)
                    panelStage30.gameObject.SetActive(false);
                if (panelStage10 != null)
                    panelStage10.gameObject.SetActive(false);

                if (finalResultRawImage.texture != null)
                {
                    Destroy(finalResultRawImage.texture);
                    finalResultRawImage.texture = null;
                }

                Texture2D tex = null;
                ImageGridPanel rootRef = panelStage90 != null ? panelStage90 : panelStage30 ?? panelStage10;
                if (rootRef != null && rootRef.TryResolveDatasetFile(rec.path, out string localPath))
                    tex = LoadTextureFromDisk(localPath);

                if (tex == null && apiClient != null && !string.IsNullOrEmpty(rec.path))
                {
                    string url = apiClient.GetImageUrl(rec.path);
                    using (UnityWebRequest req = UnityWebRequestTexture.GetTexture(url))
                    {
                        yield return req.SendWebRequest();
#if UNITY_2020_1_OR_NEWER
                        if (req.result == UnityWebRequest.Result.Success)
#else
                        if (!req.isNetworkError && !req.isHttpError)
#endif
                            tex = DownloadHandlerTexture.GetContent(req);
                    }
                }

                if (tex != null)
                    finalResultRawImage.texture = tex;
                yield break;
            }

            if (panelStage90 != null)
                panelStage90.gameObject.SetActive(false);
            if (panelStage30 != null)
                panelStage30.gameObject.SetActive(false);
            if (panelStage10 != null)
                panelStage10.gameObject.SetActive(true);

            if (panelStage10 != null)
                yield return StartCoroutine(panelStage10.PopulateFromApiResults(results, null));
        }

        static Texture2D LoadTextureFromDisk(string path)
        {
            try
            {
                byte[] data = File.ReadAllBytes(path);
                var t = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (t.LoadImage(data))
                    return t;
                Destroy(t);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ClipSearchFlow] LoadTextureFromDisk: " + ex.Message);
            }

            return null;
        }
    }
}

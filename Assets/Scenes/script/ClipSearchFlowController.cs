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
    [DefaultExecutionOrder(-100)]
    public class ClipSearchFlowController : MonoBehaviour
    {
        public ClipSearchApiClient apiClient;
        public ImageGridPanel panelStage90;
        public ImageGridPanel panelStage30;
        public ImageGridPanel panelStage10;
        public ImageGridPanel panelStage3;
        public ImageGridPanel panelStage1;

        const int FinalStage = 5;

        public TMP_InputField queryInput;
        public Button submitButton;

        [TextArea(1, 3)]
        public string debugQuery = "a cat";

        public bool submitDebugQueryOnStart;

        [Header("Initial prompt")]
        [Tooltip("When set, stage image panels start hidden and this root is shown until the user submits queryInput (Enter or submitButton).")]
        public GameObject initialPromptPanel;

        [Header("Panel Positioning")]
        [Tooltip("World-space Z offset applied between successive stage panels so each next stage appears closer to the camera.")]
        public float stageDepthStep = 1.0f;

        string _query;
        int _lastResponseStage;
        string[] _lastCandidateIds = Array.Empty<string>();
        readonly List<string> _selected = new List<string>();
        bool _busy;

        /// <summary>Cached API results per stage (1..5) for navigating back without re-querying.</summary>
        readonly ClipResultRecordDto[][] _cachedStageResults = new ClipResultRecordDto[6][];

        void Awake()
        {
            PropagateApiClient();
            ApplyStagePanelDepthOffsets();
            SetupPromptPhysicsAndFocus();
            if (submitButton != null)
                submitButton.onClick.AddListener(SubmitQueryFromUi);
            if (queryInput != null)
                queryInput.onSubmit.AddListener(OnQueryInputSubmit);
        }

        void OnDestroy()
        {
            if (submitButton != null)
                submitButton.onClick.RemoveListener(SubmitQueryFromUi);
            if (queryInput != null)
                queryInput.onSubmit.RemoveListener(OnQueryInputSubmit);
        }

        void SetupPromptPhysicsAndFocus()
        {
            if (queryInput != null && queryInput.GetComponent<TmpInputFieldXrPointerFocus>() == null)
                queryInput.gameObject.AddComponent<TmpInputFieldXrPointerFocus>();

            EnsureBoxColliderOnRect(queryInput != null ? queryInput.transform as RectTransform : null);
            EnsureBoxColliderOnRect(submitButton != null ? submitButton.transform as RectTransform : null);
        }

        static void EnsureBoxColliderOnRect(RectTransform rt)
        {
            if (rt == null)
                return;
            if (rt.GetComponent<BoxCollider>() != null)
                return;
            rt.gameObject.AddComponent<BoxCollider>();
        }

        void ResyncPromptPhysicsColliders()
        {
            SyncUiCollider(queryInput != null ? queryInput.transform as RectTransform : null,
                queryInput != null ? queryInput.GetComponent<BoxCollider>() : null);
            SyncUiCollider(submitButton != null ? submitButton.transform as RectTransform : null,
                submitButton != null ? submitButton.GetComponent<BoxCollider>() : null);
        }

        static void SyncUiCollider(RectTransform rt, BoxCollider box)
        {
            if (rt == null || box == null)
                return;

            Rect r = rt.rect;
            float depth = Mathf.Max(4f, Mathf.Min(
                Mathf.Max(r.width, 0.01f),
                Mathf.Max(r.height, 0.01f)) * 0.08f);
            box.center = new Vector3(r.center.x, r.center.y, 0f);
            box.size = new Vector3(
                Mathf.Max(r.width, 0.01f),
                Mathf.Max(r.height, 0.01f),
                depth);
        }

        IEnumerator ResyncPromptCollidersNextFrame()
        {
            yield return null;
            RectTransform layoutRoot = null;
            if (initialPromptPanel != null)
                layoutRoot = initialPromptPanel.transform as RectTransform;
            else if (queryInput != null)
                layoutRoot = queryInput.transform as RectTransform;
            if (layoutRoot != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(layoutRoot);
            Canvas.ForceUpdateCanvases();
            ResyncPromptPhysicsColliders();
        }

        void SetPromptInteractable(bool interactable)
        {
            if (queryInput != null)
                queryInput.interactable = interactable;
            if (submitButton != null)
                submitButton.interactable = interactable;
        }

        void ShowPromptUiAfterStage1Failure(string errorMessage)
        {
            SetPromptInteractable(true);
            ClearAllPanelStackDimming();
            if (initialPromptPanel != null)
            {
                if (panelStage90 != null)
                    panelStage90.gameObject.SetActive(false);
                initialPromptPanel.SetActive(true);
            }
        }

        void OnQueryInputSubmit(string text)
        {
            TryStartSearchFromUi();
        }

        /// <summary>
        /// Offsets stage panels along the world Z axis so each subsequent stage appears in front of the previous one.
        /// Stage 1 (90) stays at its scene-authored position; later stages are pulled toward the camera by <see cref="stageDepthStep"/>.
        /// </summary>
        void ApplyStagePanelDepthOffsets()
        {
            if (panelStage90 == null)
                return;

            Vector3 basePos = panelStage90.transform.position;

            if (panelStage30 != null)
                panelStage30.transform.position = new Vector3(basePos.x, basePos.y, basePos.z - stageDepthStep);

            if (panelStage10 != null)
                panelStage10.transform.position = new Vector3(basePos.x, basePos.y, basePos.z - stageDepthStep * 2f);

            if (panelStage3 != null)
                panelStage3.transform.position = new Vector3(basePos.x, basePos.y, basePos.z - stageDepthStep * 3f);

            if (panelStage1 != null)
                panelStage1.transform.position = new Vector3(basePos.x, basePos.y, basePos.z - stageDepthStep * 4f);
        }

        void Start()
        {
            ApplyInitialBootState();
            StartCoroutine(StartFlowRoutine());
            if (initialPromptPanel != null || queryInput != null)
            {
                ResyncPromptPhysicsColliders();
                StartCoroutine(ResyncPromptCollidersNextFrame());
            }
        }

        /// <summary>
        /// When <see cref="initialPromptPanel"/> is assigned, hides all stage grids and shows the prompt UI.
        /// Also avoids <see cref="ImageGridPanel"/> random-fill on first enable of stage 90 after submit.
        /// </summary>
        void ApplyInitialBootState()
        {
            if (initialPromptPanel == null)
                return;

            HideAllStageGridPanels();
            initialPromptPanel.SetActive(true);
        }

        void HideAllStageGridPanels()
        {
            if (panelStage90 != null)
                panelStage90.gameObject.SetActive(false);
            if (panelStage30 != null)
                panelStage30.gameObject.SetActive(false);
            if (panelStage10 != null)
                panelStage10.gameObject.SetActive(false);
            if (panelStage3 != null)
                panelStage3.gameObject.SetActive(false);
            if (panelStage1 != null)
                panelStage1.gameObject.SetActive(false);
        }

        /// <summary>
        /// Aborts in-flight search work, clears stage grids, and returns to the initial prompt when configured.
        /// </summary>
        public void ReturnToStart()
        {
            StopAllCoroutines();
            TmpInputFieldXrPointerFocus.EndPhysicsRetentionIfAny();

            _busy = false;
            _query = string.Empty;
            _selected.Clear();
            _lastCandidateIds = Array.Empty<string>();
            _lastResponseStage = 0;
            ClearStageResultCache();

            foreach (ImageGridPanel p in EnumerateStagePanels())
            {
                if (p == null)
                    continue;
                p.ClearGridAndResetPick();
                p.SetSearchQuery(string.Empty);
                p.SetOnCloseToStart(null);
            }

            HideAllStageGridPanels();
            ClearAllPanelStackDimming();

            if (initialPromptPanel != null)
                initialPromptPanel.SetActive(true);

            SetPromptInteractable(true);
            if (queryInput != null)
                queryInput.text = string.Empty;

            if (initialPromptPanel != null || queryInput != null)
            {
                ResyncPromptPhysicsColliders();
                StartCoroutine(ResyncPromptCollidersNextFrame());
            }

            StartCoroutine(RefocusPromptNextFrame());
        }

        IEnumerable<ImageGridPanel> EnumerateStagePanels()
        {
            yield return panelStage90;
            yield return panelStage30;
            yield return panelStage10;
            yield return panelStage3;
            yield return panelStage1;
        }

        IEnumerator RefocusPromptNextFrame()
        {
            yield return null;
            if (queryInput == null || !queryInput.gameObject.activeInHierarchy)
                yield break;
            if (initialPromptPanel != null && !initialPromptPanel.activeSelf)
                yield break;

            queryInput.Select();
            queryInput.ActivateInputField();
        }

        void ConfigurePanelSearchChrome(ImageGridPanel panel)
        {
            if (panel == null)
                return;

            if (!string.IsNullOrEmpty(_query))
                panel.SetSearchQuery(_query);
            panel.SetOnCloseToStart(ReturnToStart);
        }

        void WireSearchChromeThroughStage(int maxStage)
        {
            maxStage = Mathf.Clamp(maxStage, 1, FinalStage);
            for (int s = 1; s <= maxStage; s++)
                ConfigurePanelSearchChrome(PanelForStage(s));
        }

        IEnumerator StartFlowRoutine()
        {
            yield return null;
            if (initialPromptPanel != null && initialPromptPanel.activeSelf && queryInput != null)
            {
                queryInput.Select();
                queryInput.ActivateInputField();
            }

            if (submitDebugQueryOnStart && initialPromptPanel == null && !string.IsNullOrWhiteSpace(debugQuery))
                BeginSearch(debugQuery.Trim());
        }

        void PropagateApiClient()
        {
            if (apiClient != null)
            {
                if (panelStage90 != null)
                    panelStage90.apiClient = apiClient;
                if (panelStage30 != null)
                    panelStage30.apiClient = apiClient;
                if (panelStage10 != null)
                    panelStage10.apiClient = apiClient;
                if (panelStage3 != null)
                    panelStage3.apiClient = apiClient;
                if (panelStage1 != null)
                    panelStage1.apiClient = apiClient;
            }

            WireStageHeaderNavigation();
        }

        void WireStageHeaderNavigation()
        {
            if (panelStage90 != null)
            {
                panelStage90.SetClipSearchStage(1);
                panelStage90.SetNavigateBackCallback(1, OnStageHeaderNavigate);
            }

            if (panelStage30 != null)
            {
                panelStage30.SetClipSearchStage(2);
                panelStage30.SetNavigateBackCallback(2, OnStageHeaderNavigate);
            }

            if (panelStage10 != null)
            {
                panelStage10.SetClipSearchStage(3);
                panelStage10.SetNavigateBackCallback(3, OnStageHeaderNavigate);
            }

            if (panelStage3 != null)
            {
                panelStage3.SetClipSearchStage(4);
                panelStage3.SetNavigateBackCallback(4, OnStageHeaderNavigate);
            }

            // Final 1×1: no header "back" target, but still tagged as stage 5 for click-to-navigate-back.
            if (panelStage1 != null)
            {
                panelStage1.SetClipSearchStage(5);
                panelStage1.SetNavigateBackCallback(0, null);
            }
        }

        void OnStageHeaderNavigate(int stage)
        {
            RequestNavigateBackToStage(stage);
        }

        int PanelStageFromPanel(ImageGridPanel p)
        {
            if (p == null)
                return 0;
            if (p == panelStage90)
                return 1;
            if (p == panelStage30)
                return 2;
            if (p == panelStage10)
                return 3;
            if (p == panelStage3)
                return 4;
            if (p == panelStage1)
                return 5;
            return 0;
        }

        /// <summary>
        /// Cell pick entry point: if the click is on an earlier CLIP stage than the current one, navigate back
        /// (hide forward panels); otherwise treat as a refine pick on the current stage.
        /// </summary>
        public void OnUserPickedImageFromPanel(ImageGridPanel sourcePanel, string imageId)
        {
            if (_busy || string.IsNullOrEmpty(imageId))
                return;

            int panelStage = PanelStageFromPanel(sourcePanel);
            if (panelStage > 0
                && _lastResponseStage > panelStage
                && CanNavigateBackToStage(panelStage))
            {
                RequestNavigateBackToStage(panelStage);
                return;
            }

            OnUserPickedImage(imageId);
        }

        /// <summary>
        /// Jump back to a completed stage: panels 1..stage stay visible and active; later stages hide.
        /// Selection is truncated so the next pick continues the flow from that stage.
        /// </summary>
        public void RequestNavigateBackToStage(int stage)
        {
            if (_busy)
                return;
            if (!CanNavigateBackToStage(stage))
                return;

            int wantSelectedCount = Mathf.Max(0, stage - 1);
            while (_selected.Count > wantSelectedCount)
                _selected.RemoveAt(_selected.Count - 1);

            _lastResponseStage = stage;
            _lastCandidateIds = IdsFromResults(_cachedStageResults[stage]);

            SetAllPanelsNonInteractive();
            ActivateThroughStage(stage);

            for (int s = 1; s <= stage; s++)
            {
                ImageGridPanel p = PanelForStage(s);
                if (p == null)
                    continue;
                p.SetSelectionEnabled(s == stage);
            }

            RefreshStagePanelStackDimming();

            _busy = true;
            StartCoroutine(RefreshPanelsFromCacheThroughStageCoroutine(stage));
        }

        /// <summary>
        /// Re-applies cached API thumbnails for stages 1..stage and clears forward cache so the UI matches the snapshot.
        /// </summary>
        IEnumerator RefreshPanelsFromCacheThroughStageCoroutine(int stage)
        {
            try
            {
                for (int k = stage + 1; k < _cachedStageResults.Length; k++)
                    _cachedStageResults[k] = null;

                for (int s = 1; s <= stage; s++)
                {
                    ImageGridPanel p = PanelForStage(s);
                    ClipResultRecordDto[] cached = _cachedStageResults[s];
                    if (p == null)
                        continue;

                    ConfigurePanelSearchChrome(p);
                    if (cached == null || cached.Length == 0)
                        continue;

                    Action<string> onPick = s == stage ? (id => OnUserPickedImageFromPanel(p, id)) : null;
                    yield return p.PopulateFromApiResults(cached, onPick, reuseLoadedThumbnailsWithoutNetwork: true);
                }

                for (int s = 1; s <= stage; s++)
                    PanelForStage(s)?.SetSelectionEnabled(s == stage);
            }
            finally
            {
                _busy = false;
                RefreshStagePanelStackDimming();
            }
        }

        bool CanNavigateBackToStage(int stage)
        {
            if (string.IsNullOrEmpty(_query) || apiClient == null)
                return false;
            if (stage < 1 || stage >= FinalStage)
                return false;
            ClipResultRecordDto[] cached = _cachedStageResults[stage];
            if (cached == null || cached.Length == 0)
                return false;
            return _lastResponseStage > stage;
        }

        public void SubmitQueryFromUi()
        {
            TryStartSearchFromUi();
        }

        /// <summary>
        /// Validates <see cref="queryInput"/> and starts <see cref="BeginSearch"/> when non-empty.
        /// </summary>
        /// <returns>True if a search was started.</returns>
        public bool TryStartSearchFromUi()
        {
            string q = queryInput != null ? queryInput.text.Trim() : "";
            if (string.IsNullOrEmpty(q))
            {
                Debug.LogWarning("[ClipSearchFlow] Empty query.");
                return false;
            }

            BeginSearch(q);
            return true;
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

            if (initialPromptPanel != null)
                initialPromptPanel.SetActive(false);

            _query = query.Trim();
            _selected.Clear();
            _lastCandidateIds = Array.Empty<string>();
            _lastResponseStage = 0;
            ClearStageResultCache();

            ResetUiStateForNewRun();
            foreach (ImageGridPanel p in EnumerateStagePanels())
                ConfigurePanelSearchChrome(p);
            StartCoroutine(RunSearchStage(1, Array.Empty<string>(), Array.Empty<string>()));
        }

        void ResetUiStateForNewRun()
        {
            if (panelStage30 != null)
                panelStage30.gameObject.SetActive(false);
            if (panelStage10 != null)
                panelStage10.gameObject.SetActive(false);
            if (panelStage3 != null)
                panelStage3.gameObject.SetActive(false);
            if (panelStage1 != null)
                panelStage1.gameObject.SetActive(false);
            if (panelStage90 != null)
                panelStage90.gameObject.SetActive(true);

            SetAllPanelsNonInteractive();

            ClearAllPanelStackDimming();
        }

        void SetAllPanelsNonInteractive()
        {
            panelStage90?.SetSelectionEnabled(false);
            panelStage30?.SetSelectionEnabled(false);
            panelStage10?.SetSelectionEnabled(false);
            panelStage3?.SetSelectionEnabled(false);
            panelStage1?.SetSelectionEnabled(false);
        }

        IEnumerator RunSearchStage(int stage, string[] candidates, string[] selected)
        {
            _busy = true;
            SetAllPanelsNonInteractive();

            yield return StartCoroutine(apiClient.SearchCoroutine(_query, stage, candidates, selected));

            if (!string.IsNullOrEmpty(apiClient.LastError))
            {
                Debug.LogWarning("[ClipSearchFlow] Search error: " + apiClient.LastError);
                if (stage == 1)
                    ShowPromptUiAfterStage1Failure("Error: " + apiClient.LastError);
                _busy = false;
                ReenablePanelForStage(stage);
                yield break;
            }

            ClipSearchResponseDto resp = apiClient.LastSearchResponse;
            if (resp == null || resp.results == null)
            {
                Debug.LogWarning("[ClipSearchFlow] Empty response.");
                if (stage == 1)
                    ShowPromptUiAfterStage1Failure("Error: Empty response.");
                _busy = false;
                yield break;
            }

            _lastResponseStage = resp.stage;
            _lastCandidateIds = IdsFromResults(resp.results);

            if (resp.results != null && resp.stage >= 1 && resp.stage <= 5)
                _cachedStageResults[resp.stage] = CopyResults(resp.results);

            if (resp.stage >= FinalStage)
            {
                yield return StartCoroutine(ShowFinalResult(resp.results));
                RefreshStagePanelStackDimming();
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
            WireSearchChromeThroughStage(resp.stage);
            yield return StartCoroutine(target.PopulateFromApiResults(resp.results, id => OnUserPickedImageFromPanel(target, id)));
            target.SetSelectionEnabled(true);
            RefreshStagePanelStackDimming();
            _busy = false;
        }

        void ReenablePanelForStage(int stage)
        {
            if (stage <= 0 || stage >= FinalStage)
                return;
            PanelForStage(stage)?.SetSelectionEnabled(true);
        }

        ImageGridPanel PanelForStage(int stage)
        {
            if (stage == 1)
                return panelStage90;
            if (stage == 2)
                return panelStage30;
            if (stage == 3)
                return panelStage10;
            if (stage == 4)
                return panelStage3;
            if (stage == 5)
                return panelStage1;
            return null;
        }

        /// <summary>Highest CLIP stage index (1–5) whose panel GameObject is active.</summary>
        int HighestActivePanelStage()
        {
            for (int s = 5; s >= 1; s--)
            {
                ImageGridPanel p = PanelForStage(s);
                if (p != null && p.gameObject.activeSelf)
                    return s;
            }

            return 0;
        }

        void ClearAllPanelStackDimming()
        {
            foreach (ImageGridPanel p in EnumerateStagePanels())
                p?.SetBackgroundStackDimming(false, 0);
        }

        /// <summary>
        /// Applies a vertical dim gradient to every visible stage panel that is behind the current foreground stage.
        /// </summary>
        void RefreshStagePanelStackDimming()
        {
            int top = HighestActivePanelStage();
            if (top <= 0 || _lastResponseStage <= 0)
            {
                ClearAllPanelStackDimming();
                return;
            }

            int front = Mathf.Min(_lastResponseStage, top);
            for (int s = 1; s <= 5; s++)
            {
                ImageGridPanel p = PanelForStage(s);
                if (p == null)
                    continue;
                if (!p.gameObject.activeSelf)
                {
                    p.SetBackgroundStackDimming(false, 0);
                    continue;
                }

                int steps = front - s;
                if (steps <= 0)
                    p.SetBackgroundStackDimming(false, 0);
                else
                    p.SetBackgroundStackDimming(true, steps);
            }
        }

        void ActivateThroughStage(int stage)
        {
            if (panelStage90 != null)
                panelStage90.gameObject.SetActive(stage >= 1);
            if (panelStage30 != null)
                panelStage30.gameObject.SetActive(stage >= 2);
            if (panelStage10 != null)
                panelStage10.gameObject.SetActive(stage >= 3);
            if (panelStage3 != null)
                panelStage3.gameObject.SetActive(stage >= 4);
            if (panelStage1 != null)
                panelStage1.gameObject.SetActive(stage >= 5);
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

        void ClearStageResultCache()
        {
            for (int i = 0; i < _cachedStageResults.Length; i++)
                _cachedStageResults[i] = null;
        }

        static ClipResultRecordDto[] CopyResults(ClipResultRecordDto[] src)
        {
            if (src == null || src.Length == 0)
                return null;
            var dst = new ClipResultRecordDto[src.Length];
            Array.Copy(src, dst, src.Length);
            return dst;
        }

        public void OnUserPickedImage(string imageId)
        {
            if (_busy || string.IsNullOrEmpty(imageId))
                return;

            if (_lastCandidateIds == null || _lastCandidateIds.Length == 0)
            {
                Debug.LogWarning("[ClipSearchFlow] No candidate ids from last response; cannot refine.");
                return;
            }

            if (Array.IndexOf(_lastCandidateIds, imageId) < 0)
                return;

            _selected.Add(imageId);
            int nextStage = _lastResponseStage + 1;
            if (nextStage > FinalStage)
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
                ClearAllPanelStackDimming();
                yield break;
            }

            ClipResultRecordDto rec = results[0];
            Debug.Log($"[ClipSearchFlow] Final pick: {rec.id} (p={rec.probability:F4})");

            // Preferred path: show the final single image in panelStage1 (an ImageGridPanel configured as 1x1).
            if (panelStage1 != null)
            {
                ActivateThroughStage(FinalStage);
                WireSearchChromeThroughStage(FinalStage);
                yield return StartCoroutine(panelStage1.PopulateFromApiResults(results, null));
                RefreshStagePanelStackDimming();
                yield break;
            }


            // Last-ditch fallback: reuse panelStage3 to show the single result.
            if (panelStage90 != null)
                panelStage90.gameObject.SetActive(false);
            if (panelStage30 != null)
                panelStage30.gameObject.SetActive(false);
            if (panelStage10 != null)
                panelStage10.gameObject.SetActive(false);
            if (panelStage3 != null)
            {
                panelStage3.gameObject.SetActive(true);
                ConfigurePanelSearchChrome(panelStage3);
                yield return StartCoroutine(panelStage3.PopulateFromApiResults(results, null));
                RefreshStagePanelStackDimming();
            }
            else
            {
                ClearAllPanelStackDimming();
            }
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

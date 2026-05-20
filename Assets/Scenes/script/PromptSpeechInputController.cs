using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Scenes.script
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ClipSearchFlowController))]
    public class PromptSpeechInputController : MonoBehaviour
    {
        [Header("Prompt UI")]
        [SerializeField] private TMP_InputField queryInput;
        [SerializeField] private Button micButton;
        [SerializeField] private Button submitButton;

        [Header("Whisper")]
        [SerializeField] private string whisperManagerTypeName = "WhisperManager";
        [SerializeField] private string modelPath = "Whisper/ggml-tiny.bin";
        [SerializeField] private string language = "en";
        [SerializeField] private bool translateToEnglish = false;

        [Header("Recording")]
        [SerializeField] private string preferredMicrophoneName = "";
        [SerializeField] private int sampleRate = 16000;
        [SerializeField] private float maxRecordingSeconds = 10f;
        [SerializeField] private float minRecordingSeconds = 0.75f;

        ClipSearchFlowController _flowController;
        object _whisperManager;
        Type _whisperManagerType;
        MethodInfo _getTextAsyncMethod;
        TextMeshProUGUI _micButtonLabel;
        Coroutine _autoStopCoroutine;
        string _recordingDeviceName;
        AudioClip _recordingClip;
        bool _isRecording;
        bool _isTranscribing;
        bool _isMicrophoneReady;
        float _recordingStartTime = -1f;

        void Awake()
        {
            _flowController = GetComponent<ClipSearchFlowController>();

            if (queryInput == null)
                queryInput = _flowController.queryInput;
            if (submitButton == null)
                submitButton = _flowController.submitButton;
            if (micButton == null)
                micButton = _flowController.speechInputButton;

            ResolveMicButton();

            if (_flowController != null)
                _flowController.speechInputButton = micButton;

            if (micButton != null)
            {
                micButton.onClick.RemoveListener(OnMicButtonPressed);
                micButton.onClick.AddListener(OnMicButtonPressed);
                _micButtonLabel = micButton.GetComponentInChildren<TextMeshProUGUI>(true);
            }

            EnsureWhisperManager();
            UpdateMicButtonVisualState();
            RefreshPromptColliders();
        }

        void OnDestroy()
        {
            if (micButton != null)
                micButton.onClick.RemoveListener(OnMicButtonPressed);

            if (_isRecording)
                StopRecordingInternal();
        }

        void ResolveMicButton()
        {
            if (micButton != null)
                return;

            if (_flowController != null && _flowController.initialPromptPanel != null)
            {
                Transform namedButton = _flowController.initialPromptPanel.transform.Find("Card/InitialPrompt_MicButton");
                if (namedButton == null)
                    namedButton = _flowController.initialPromptPanel.transform.Find("InitialPrompt_MicButton");
                if (namedButton != null)
                    micButton = namedButton.GetComponent<Button>();
            }

            if (micButton == null && submitButton != null && submitButton.transform.parent != null)
            {
                Button[] buttons = submitButton.transform.parent.GetComponentsInChildren<Button>(true);
                foreach (Button button in buttons)
                {
                    if (button == null || button == submitButton)
                        continue;
                    if (string.Equals(button.gameObject.name, "InitialPrompt_MicButton", StringComparison.Ordinal))
                    {
                        micButton = button;
                        break;
                    }
                }
            }

            if (micButton == null)
                Debug.LogWarning("[PromptSpeechInput] No mic button assigned or found in the prompt window.");
        }

        void RefreshPromptColliders()
        {
            if (_flowController != null)
                _flowController.RefreshPromptUiColliders();
        }

        void OnMicButtonPressed()
        {
            if (_isTranscribing)
                return;

            if (_isRecording)
            {
                if (_isMicrophoneReady && Time.unscaledTime - _recordingStartTime >= minRecordingSeconds)
                    _ = StopRecordingAndTranscribeAsync();
                return;
            }

            StartRecording();
        }

        void StartRecording()
        {
            if (_isRecording || _isTranscribing)
                return;

            if (Microphone.devices == null || Microphone.devices.Length == 0)
            {
                Debug.LogWarning("[PromptSpeechInput] No microphone device available.");
                UpdateMicButtonVisualState();
                return;
            }

            if (!EnsureWhisperManager())
            {
                Debug.LogWarning("[PromptSpeechInput] WhisperManager is unavailable.");
                UpdateMicButtonVisualState();
                return;
            }

            string absoluteModelPath = Path.Combine(Application.streamingAssetsPath, modelPath);
            if (!File.Exists(absoluteModelPath))
            {
                Debug.LogWarning("[PromptSpeechInput] Whisper model not found at " + absoluteModelPath);
                UpdateMicButtonVisualState();
                return;
            }

            _recordingDeviceName = ResolveRecordingDeviceName();
            if (string.IsNullOrEmpty(_recordingDeviceName))
            {
                Debug.LogWarning("[PromptSpeechInput] No suitable microphone device found.");
                UpdateMicButtonVisualState();
                return;
            }

            int durationSeconds = Mathf.Max(1, Mathf.CeilToInt(maxRecordingSeconds));
            _recordingClip = Microphone.Start(_recordingDeviceName, false, durationSeconds, sampleRate);
            if (_recordingClip == null)
            {
                Debug.LogWarning("[PromptSpeechInput] Failed to start microphone recording.");
                UpdateMicButtonVisualState();
                return;
            }

            _isRecording = true;
            _isMicrophoneReady = false;
            _recordingStartTime = -1f;
            StartCoroutine(WaitForMicrophoneReady());
            _autoStopCoroutine = StartCoroutine(AutoStopAfterTimeout());
            UpdateMicButtonVisualState();
        }

        IEnumerator WaitForMicrophoneReady()
        {
            const float startupTimeoutSeconds = 2f;
            float deadline = Time.unscaledTime + startupTimeoutSeconds;

            while (_isRecording && Time.unscaledTime < deadline)
            {
                int position = Microphone.GetPosition(_recordingDeviceName);
                if (position > 0)
                {
                    _isMicrophoneReady = true;
                    _recordingStartTime = Time.unscaledTime;
                    UpdateMicButtonVisualState();
                    yield break;
                }

                yield return null;
            }

            if (_isRecording && !_isMicrophoneReady)
            {
                Debug.LogWarning("[PromptSpeechInput] Microphone did not start producing samples.");
                StopRecordingInternal();
                UpdateMicButtonVisualState();
            }
        }

        IEnumerator AutoStopAfterTimeout()
        {
            yield return new WaitForSecondsRealtime(Mathf.Max(0.1f, maxRecordingSeconds));
            if (_isRecording && _isMicrophoneReady)
                _ = StopRecordingAndTranscribeAsync();
        }

        async Task StopRecordingAndTranscribeAsync()
        {
            if (!_isRecording)
                return;

            if (!_isMicrophoneReady)
            {
                Debug.LogWarning("[PromptSpeechInput] Recording stopped before microphone was ready.");
                StopRecordingInternal();
                UpdateMicButtonVisualState();
                return;
            }

            if (Time.unscaledTime - _recordingStartTime < minRecordingSeconds)
            {
                Debug.LogWarning("[PromptSpeechInput] Recording too short for reliable transcription.");
                UpdateMicButtonVisualState();
                return;
            }

            AudioClip clip = FinalizeRecordingClip();
            if (clip == null)
            {
                UpdateMicButtonVisualState();
                return;
            }

            _isTranscribing = true;
            UpdateMicButtonVisualState();

            try
            {
                string transcript = await TranscribeClipAsync(clip);
                if (string.IsNullOrWhiteSpace(transcript))
                {
                    Debug.LogWarning("[PromptSpeechInput] Transcription returned empty text.");
                    return;
                }

                if (queryInput != null)
                    queryInput.text = transcript.Trim();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PromptSpeechInput] Transcription failed: " + ex.Message);
            }
            finally
            {
                _isTranscribing = false;
                if (clip != null)
                    Destroy(clip);
                UpdateMicButtonVisualState();
                RefreshPromptColliders();
            }
        }

        AudioClip FinalizeRecordingClip()
        {
            if (!_isRecording)
                return null;

            int samplePosition = Microphone.GetPosition(_recordingDeviceName);
            if (samplePosition <= 0)
            {
                StopRecordingInternal();
                Debug.LogWarning("[PromptSpeechInput] Recording captured no samples.");
                return null;
            }

            AudioClip sourceClip = _recordingClip;
            StopRecordingInternal();

            float[] samples = new float[samplePosition * sourceClip.channels];
            sourceClip.GetData(samples, 0);

            AudioClip trimmedClip = AudioClip.Create(
                "PromptSpeechRecording",
                samplePosition,
                sourceClip.channels,
                sourceClip.frequency,
                false);
            trimmedClip.SetData(samples, 0);

            return trimmedClip;
        }

        void StopRecordingInternal()
        {
            if (_autoStopCoroutine != null)
            {
                StopCoroutine(_autoStopCoroutine);
                _autoStopCoroutine = null;
            }

            if (!string.IsNullOrEmpty(_recordingDeviceName) && Microphone.IsRecording(_recordingDeviceName))
                Microphone.End(_recordingDeviceName);

            _isRecording = false;
            _isMicrophoneReady = false;
            _recordingStartTime = -1f;
            _recordingDeviceName = null;
            _recordingClip = null;
        }

        string ResolveRecordingDeviceName()
        {
            if (!string.IsNullOrWhiteSpace(preferredMicrophoneName))
            {
                foreach (string device in Microphone.devices)
                {
                    if (string.Equals(device, preferredMicrophoneName, StringComparison.OrdinalIgnoreCase))
                        return device;
                }

                Debug.LogWarning("[PromptSpeechInput] Preferred microphone not found: " + preferredMicrophoneName);
            }

            return Microphone.devices.Length > 0 ? Microphone.devices[0] : null;
        }

        async Task<string> TranscribeClipAsync(AudioClip clip)
        {
            if (!EnsureWhisperManager())
                return null;

            object taskObject = _getTextAsyncMethod.Invoke(_whisperManager, new object[] { clip });
            Task task = taskObject as Task;
            if (task == null)
                throw new InvalidOperationException("WhisperManager.GetTextAsync(AudioClip) did not return a Task.");

            await task;
            return ExtractTranscript(task);
        }

        bool EnsureWhisperManager()
        {
            if (_whisperManager != null && _getTextAsyncMethod != null)
            {
                ConfigureWhisperManager(_whisperManager);
                return true;
            }

            _whisperManagerType = FindTypeByName(whisperManagerTypeName);
            if (_whisperManagerType == null)
                return false;

            Component existing = GetComponent(_whisperManagerType);
            if (existing == null)
                existing = gameObject.AddComponent(_whisperManagerType);

            _whisperManager = existing;
            _getTextAsyncMethod = _whisperManagerType.GetMethod("GetTextAsync", new[] { typeof(AudioClip) });
            if (_getTextAsyncMethod == null)
                return false;

            ConfigureWhisperManager(_whisperManager);
            return true;
        }

        void ConfigureWhisperManager(object manager)
        {
            if (manager == null)
                return;

            ConfigureWhisperModelPath(manager);
            SetMemberIfPresent(manager, "language", language);
            SetMemberIfPresent(manager, "Language", language);
            SetMemberIfPresent(manager, "translateToEnglish", translateToEnglish);
            SetMemberIfPresent(manager, "TranslateToEnglish", translateToEnglish);
        }

        void ConfigureWhisperModelPath(object manager)
        {
            if (IsWhisperModelActive(manager))
                return;

            string currentModelPath = GetStringMember(manager, "ModelPath")
                ?? GetStringMember(manager, "modelPath");
            if (!string.IsNullOrEmpty(currentModelPath)
                && string.Equals(currentModelPath, modelPath, StringComparison.Ordinal))
                return;

            SetMemberIfPresent(manager, "modelPath", modelPath);
            SetMemberIfPresent(manager, "ModelPath", modelPath);
            SetMemberIfPresent(manager, "isModelPathInStreamingAssets", true);
            SetMemberIfPresent(manager, "IsModelPathInStreamingAssets", true);
        }

        static string ExtractTranscript(Task completedTask)
        {
            PropertyInfo resultProperty = completedTask.GetType().GetProperty("Result");
            if (resultProperty == null)
                return null;

            object result = resultProperty.GetValue(completedTask);
            if (result is string directText)
                return directText;

            if (result == null)
                return null;

            string text = GetStringMember(result, "Result");
            if (!string.IsNullOrWhiteSpace(text))
                return text;

            text = GetStringMember(result, "Text");
            if (!string.IsNullOrWhiteSpace(text))
                return text;

            text = GetStringMember(result, "AllText");
            if (!string.IsNullOrWhiteSpace(text))
                return text;

            foreach (PropertyInfo property in result.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.PropertyType != typeof(string))
                    continue;

                string candidate = property.GetValue(result) as string;
                if (!string.IsNullOrWhiteSpace(candidate))
                    return candidate;
            }

            return null;
        }

        static string GetStringMember(object target, string memberName)
        {
            PropertyInfo property = target.GetType().GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.PropertyType == typeof(string))
                return property.GetValue(target) as string;

            FieldInfo field = target.GetType().GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(string))
                return field.GetValue(target) as string;

            return null;
        }

        static bool IsWhisperModelActive(object target)
        {
            return GetBoolMember(target, "IsLoaded") || GetBoolMember(target, "IsLoading");
        }

        static bool GetBoolMember(object target, string memberName)
        {
            PropertyInfo property = target.GetType().GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.PropertyType == typeof(bool))
                return (bool)property.GetValue(target);

            FieldInfo field = target.GetType().GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(bool))
                return (bool)field.GetValue(target);

            return false;
        }

        static void SetMemberIfPresent(object target, string memberName, object value)
        {
            Type type = target.GetType();

            PropertyInfo property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite && value != null && property.PropertyType.IsAssignableFrom(value.GetType()))
            {
                try
                {
                    property.SetValue(target, value);
                }
                catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
                {
                    return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }
                return;
            }

            FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && value != null && field.FieldType.IsAssignableFrom(value.GetType()))
            {
                try
                {
                    field.SetValue(target, value);
                }
                catch (InvalidOperationException)
                {
                    return;
                }
            }
        }

        static Type FindTypeByName(string typeName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type found = assembly.GetType(typeName, false);
                if (found != null)
                    return found;

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }

                foreach (Type type in types)
                {
                    if (type == null)
                        continue;
                    if (type.Name == typeName)
                        return type;
                }
            }

            return null;
        }

        void UpdateMicButtonVisualState()
        {
            if (micButton == null)
                return;

            micButton.interactable = !_isTranscribing;
            if (_micButtonLabel == null)
                _micButtonLabel = micButton.GetComponentInChildren<TextMeshProUGUI>(true);
            if (_micButtonLabel == null)
                return;

            if (_isTranscribing)
                _micButtonLabel.text = "Transcribing...";
            else if (_isRecording && !_isMicrophoneReady)
                _micButtonLabel.text = "Starting...";
            else if (_isRecording)
                _micButtonLabel.text = "Stop";
            else
                _micButtonLabel.text = "Mic";
        }
    }
}

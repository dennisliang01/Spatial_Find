using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Scenes.script
{
    /// <summary>
    /// Ensures a <see cref="TMP_InputField"/> receives focus when clicked with the XR ray / mouse pointer.
    /// Add to the same GameObject as the TMP_InputField (or let <see cref="ClipSearchFlowController"/> add it at runtime when setup runs).
    /// </summary>
    [RequireComponent(typeof(TMP_InputField))]
    public sealed class TmpInputFieldXrPointerFocus : MonoBehaviour, IPointerDownHandler
    {
        TMP_InputField _field;

        /// <summary>
        /// Physics ray (<see cref="WorkingController"/>) activates the field, then XR/studio EventSystem often deselects
        /// on the same or next frame because the tracked UI ray is not the same as the physics ray.
        /// While true, <see cref="LateUpdate"/> re-applies selection so keyboard input can reach TMP.
        /// </summary>
        bool _retainPhysicsSelection;
        static TmpInputFieldXrPointerFocus _physicsRetentionInstance;

        void Awake()
        {
            _field = GetComponent<TMP_InputField>();
        }

        void OnDisable()
        {
            if (_physicsRetentionInstance == this)
            {
                _physicsRetentionInstance = null;
                _retainPhysicsSelection = false;
            }
        }

        /// <summary>Call from <c>WorkingController</c> after physics hit activates this field.</summary>
        public void BeginRetainPhysicsSelection()
        {
            if (_physicsRetentionInstance != null && _physicsRetentionInstance != this)
                _physicsRetentionInstance._retainPhysicsSelection = false;
            _physicsRetentionInstance = this;
            _retainPhysicsSelection = true;
        }

        /// <summary>Stop re-applying selection (e.g. user used another UI via physics ray).</summary>
        public static void EndPhysicsRetentionIfAny()
        {
            if (_physicsRetentionInstance != null)
                _physicsRetentionInstance._retainPhysicsSelection = false;
            _physicsRetentionInstance = null;
        }

        void LateUpdate()
        {
            if (!_retainPhysicsSelection || _field == null || !_field.interactable)
                return;
            EventSystem es = EventSystem.current;
            if (es == null)
                return;
            if (es.currentSelectedGameObject == _field.gameObject && _field.isFocused)
                return;
            es.SetSelectedGameObject(_field.gameObject);
            _field.ActivateInputField();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_field == null || !_field.interactable)
                return;

            _field.ActivateInputField();
            BeginRetainPhysicsSelection();
        }
    }
}

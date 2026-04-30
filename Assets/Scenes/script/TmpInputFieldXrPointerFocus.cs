using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Scenes.script
{
    /// <summary>
    /// Keeps a <see cref="TMP_InputField"/> focused while it is active in the hierarchy. The XR
    /// EventSystem regularly deselects fields because the tracked UI ray differs from the physics
    /// ray that opened them; <see cref="LateUpdate"/> reasserts selection so keyboard input keeps
    /// reaching TMP. Optionally redirects an XR pointer click on the field to a target Button, so
    /// the prompt window can route a controller click on the query field straight to Search.
    /// </summary>
    [RequireComponent(typeof(TMP_InputField))]
    public sealed class TmpInputFieldXrPointerFocus : MonoBehaviour
    {
        [Tooltip("Optional: when an XR pointer click lands on this field, invoke this button instead of activating the field for typing. " +
                 "Used by the prompt window so a controller click on the query field acts like clicking Search.")]
        public Button xrClickRedirectButton;

        TMP_InputField _field;

        void Awake()
        {
            _field = GetComponent<TMP_InputField>();
        }

        void LateUpdate()
        {
            if (_field == null || !_field.interactable)
                return;
            EventSystem es = EventSystem.current;
            if (es == null)
                return;
            if (es.currentSelectedGameObject == _field.gameObject && _field.isFocused)
                return;
            es.SetSelectedGameObject(_field.gameObject);
            _field.ActivateInputField();
        }

        /// <summary>
        /// Invokes <see cref="xrClickRedirectButton"/> if it is configured and active. XR click
        /// forwarders call this so a controller click on the field runs the associated submit
        /// action instead of activating the field.
        /// </summary>
        /// <returns>True if the redirect button was invoked.</returns>
        public bool TryInvokeXrClickRedirect()
        {
            Button btn = xrClickRedirectButton;
            if (btn == null || !btn.isActiveAndEnabled || !btn.interactable)
                return false;
            btn.onClick.Invoke();
            return true;
        }
    }
}

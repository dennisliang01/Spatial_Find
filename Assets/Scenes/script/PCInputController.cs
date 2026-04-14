namespace Scenes.script
{
    using UnityEngine;

    /// <summary>
    /// Handles PC mouse-click input for testing the image retrieval pipeline without a VR headset.
    /// Raycasts from the main camera on mouse click to detect clicks on image tiles,
    /// and invokes the existing ClipSearchFlowController.OnUserPickedImage() callback.
    ///
    /// This controller coexists with the VR interaction system and can be toggled on/off
    /// by enabling/disabling this GameObject.
    /// </summary>
    public class PCInputController : MonoBehaviour
    {
        [SerializeField]
        private ClipSearchFlowController clipSearchFlowController;

        private void Update()
        {
            if (!Input.GetMouseButtonDown(0))
            {
                return;
            }

            HandleMouseClick();
        }

        private void HandleMouseClick()
        {
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);

            RaycastHit[] hits = Physics.RaycastAll(ray, Mathf.Infinity, ~0, QueryTriggerInteraction.Collide);

            // Physics.RaycastAll returns hits in arbitrary order. With overlapping panels along Z
            // (stage 1→5 stacked in depth), we must sort by distance so the frontmost tile wins.
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            foreach (RaycastHit hit in hits)
            {
                // Skip controller and hand objects (same filter as WorkingController)
                if (hit.transform.name.Contains("Controller") || hit.transform.name.Contains("Hand"))
                {
                    continue;
                }

                // Look for an ImageTile component on the hit object or its parent
                ImageTile imageTile = hit.collider.GetComponent<ImageTile>();
                if (imageTile == null)
                {
                    imageTile = hit.collider.GetComponentInParent<ImageTile>();
                }

                if (imageTile != null && !string.IsNullOrEmpty(imageTile.imageId))
                {
                    clipSearchFlowController.OnUserPickedImage(imageTile.imageId);
                    return; // Only process the closest hit
                }
            }
        }
    }
}

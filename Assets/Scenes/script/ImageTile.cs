namespace Scenes.script
{
    using UnityEngine;

    /// <summary>
    /// Component that stores the image ID for a grid cell.
    /// Attached to each Cell_N RawImage in ImageGridPanel.
    /// Used by PCInputController to retrieve the clicked image's ID.
    /// </summary>
    public class ImageTile : MonoBehaviour
    {
        public string imageId;
    }
}

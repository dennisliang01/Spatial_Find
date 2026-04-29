# PC Mouse-Click Input Setup Guide

This guide explains how to set up and test the PC mouse-click input system for the SpatialFind1 scene.

**Standalone builds (no Editor):** use **`LaunchSpatialFind.cmd`** so the Python server and game start together; see [DISTRIBUTION_STANDALONE.md](DISTRIBUTION_STANDALONE.md).

## What Was Added

Three new C# scripts enable mouse-click testing without a VR headset:

### 1. **ImageTile.cs** (`Assets/Scenes/script/ImageTile.cs`)
- Simple component that stores the image ID for each grid cell
- Attached automatically to each Cell_N RawImage by ImageGridPanel
- Used by PCInputController to retrieve the clicked image's ID

### 2. **PCInputController.cs** (`Assets/Scenes/script/PCInputController.cs`)
- Listens for mouse clicks (Input.GetMouseButtonDown(0))
- Raycasts from Camera.main using Physics.RaycastAll
- Detects clicks on image tiles and invokes ClipSearchFlowController.OnUserPickedImage()
- Can be toggled on/off by enabling/disabling its GameObject

### 3. **SceneSetup_PC_InputController.cs** (Editor script)
- Automated setup utility - run via menu: `Scenes → Setup PC Input Controller`
- Creates the PCInputController GameObject
- Automatically wires the ClipSearchFlowController reference

### Modified: **ImageGridPanel.cs**
- Now attaches ImageTile component to each Cell_N
- Populates imageId on each ImageTile during PopulateFromApiResults()

## Setup Instructions

### Automatic Setup (Recommended)

1. Open **SpatialFind1** scene in Unity Editor
2. Go to menu: **Scenes → Setup PC Input Controller**
3. Click OK when the success dialog appears
4. The scene is now ready to test

### Manual Setup (Fallback)

1. Open **SpatialFind1** scene in Unity Editor
2. In the Hierarchy, create a new empty GameObject named `PCInputController`
3. Drag and drop `Assets/Scenes/script/PCInputController.cs` onto this GameObject
4. In the Inspector, locate the `Clip Search Flow Controller` field
5. Drag the `ClipSearch_Service` GameObject from the Hierarchy into that field
6. Make sure the `ClipSearch_Service` > `ClipSearchFlowController` component is assigned
7. Save the scene (Ctrl+S)

## How It Works

1. **Scene Start**: The scene auto-runs the debug query (submitDebugQueryOnStart=true)
   - ImageGridPanel_90 populates with 90 images
   - Each Cell_N gets an ImageTile component with imageId set

2. **Mouse Click**: When you click an image tile:
   - PCInputController raycasts from Camera.main
   - Finds the BoxCollider on the Cell_N
   - Retrieves the ImageTile component and its imageId
   - Calls `ClipSearchFlowController.OnUserPickedImage(imageId)`

3. **Backend Call**: ClipSearchFlowController:
   - Calls the backend `/search` endpoint with stage 2, candidates (90 images), and selected image
   - Hides ImageGridPanel_90
   - Shows ImageGridPanel_30 with 30 results
   - Repeats for stages 3 and 4

## Testing

### Golden Path Test

1. Play the scene in the Unity Editor (no VR headset required)
2. Wait for ImageGridPanel_90 to populate with 90 images (auto-runs on start)
3. **Click an image tile** → you should see:
   - No errors in the Console
   - The panel transitions (ImageGridPanel_90 hides, ImageGridPanel_30 shows)
   - The backend is called (check server.py output or network logs)
4. **Click an image in stage 2** → ImageGridPanel_30 hides, ImageGridPanel_10 shows
5. **Click an image in stage 3** → Final result display (stage 4)

### Edge Cases

- **Click outside tiles**: Nothing happens (no error)
- **Quick double-click**: System queues requests normally (may advance quickly if server responds fast)
- **Enable/disable PCInputController**: Can toggle via GameObject checkbox in Inspector
- **VR still works**: If you attach a VR headset, WorkingController's laser raycasts coexist with mouse input

## Troubleshooting

### "No hits" or clicks don't work
- Verify ImageGridPanel_90 is **active** in the Hierarchy (eye icon is on)
- Check that images have loaded (visual confirmation in the scene)
- Ensure Camera.main exists (XR Origin > Camera Offset > Main Camera)

### "Missing reference" error for clipSearchFlowController
- Open the PCInputController GameObject in the Inspector
- Manually drag ClipSearch_Service into the `Clip Search Flow Controller` field
- Save the scene

### Backend not called
- Verify the FastAPI server is running (`python server.py` in the project root)
- Check baseUrl in ClipSearchApiClient (should be `http://127.0.0.1:8000`)
- Check browser console / network tab in ClipSearchFlowController's HTTP logs

### Physics.Raycast misses tiles
- The BoxCollider on each Cell_N must be synced to its RectTransform
- ImageGridPanel calls `ResyncCellPhysicsColliders()` automatically after layout
- If colliders look misaligned, try: Delete PCInputController and re-run the setup script

## Coexistence with VR

Both mouse and VR inputs work simultaneously:
- **PCInputController** (mouse): Uses Physics.RaycastAll from Camera.main
- **WorkingController** (VR): Uses Physics.RaycastAll from controller
- Both invoke Button.onClick → ClipSearchFlowController.OnUserPickedImage()

The system cleanly handles both input methods without conflict.

## Performance Notes

- Raycasting every frame on mouse click is efficient (single Physics.RaycastAll)
- ImageTile component is lightweight (just one string field)
- No additional overhead when using VR (PCInputController is independent)
- Can disable PCInputController GameObject if mouse input interferes with testing

---

**Created**: 2026-04-14  
**For**: SpatialFind1 XR Image Retrieval Scene  
**Scope**: PC mouse-click testing support alongside VR interaction

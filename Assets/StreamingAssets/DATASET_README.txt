Large image datasets (e.g. dogs_vs_cats, ~25k files) under Assets/StreamingAssets slow down the Unity
Editor because every file gets tracked and receives a .meta file.

Recommended setup
-----------------
1. Create a folder OUTSIDE the Unity project, for example:
      <repo>/Data/dogs_vs_cats
   (The /Data/ folder is gitignored by default.)

2. Move (or download) your images there, same layout as before (e.g. cat/, dog/).

3. Python server — set before running server.py:
      Windows CMD:  set CLIP_IMAGE_ROOT=C:\path\to\Spatial_Find\Data\dogs_vs_cats
      PowerShell:   $env:CLIP_IMAGE_ROOT="C:\path\to\Spatial_Find\Data\dogs_vs_cats"

4. Start server.py once so it builds the index; it also writes image_manifest.txt in that folder.

5. Unity — on the GameObject with ImageGridPanel, set "Absolute Dataset Root" to the same path
   (full path to the dogs_vs_cats folder).

6. Remove or empty Assets/StreamingAssets/dogs_vs_cats if you no longer need it in the project.

Device builds: copy a subset of images into StreamingAssets, or ship data another way; StreamingAssets
must contain what you need at runtime on device.

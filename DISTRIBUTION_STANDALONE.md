# Standalone PC distribution (Unity + Python server)

Non-developers should start the app with **`LaunchSpatialFind.cmd`** in the folder you give them. That script starts the CLIP/FastAPI server, waits until `http://127.0.0.1:8000/health` succeeds, launches the Unity build, and stops the server when the game closes.

## What to ship

Everything below lives in **one application folder** (the same directory you double-click **`LaunchSpatialFind.cmd`** from). Example dev path: `C:\Users\denni\Spatial_Find`.

**Unity PC build** must be **complete** in **one folder**: **`VRproj.exe`** and the matching **`VRproj_Data`** folder as **siblings**. The launcher looks for that pair in (1) the folder that contains the launcher scripts and `server.py`, then (2) **each immediate subfolder** (alphabetical), so you can use e.g. `Spatial_Find\Build\VRproj.exe` + `Spatial_Find\Build\VRproj_Data\` even if the launchers stay in `Spatial_Find\`.

If your build lives elsewhere, either set **`SPATIAL_FIND_GAME_DIR`** before running (see `LaunchSpatialFind.cmd`), or edit **`$GameInstallDir`** at the top of `LaunchSpatialFind.ps1` (path relative to the script folder or absolute).

When you build in Unity, use **Add Open Scenes**, pick a folder, and copy the **entire** output (exe + `_Data` + `MonoBleedingEdge` etc. if present), or build directly into a subfolder of the repo.

Typical layout:

```text
Spatial_Find/                 # or Publish/
  LaunchSpatialFind.cmd
  LaunchSpatialFind.ps1
  VRproj.exe
  VRproj_Data/                # required next to VRproj.exe (Unity asset bundles, Managed/, etc.)
  server.py
  requirements.txt             # optional; for support / reinstall
  .venv/                       # copy from a machine where you already ran pip + CLIP install (large)
  logs/                        # created on run (server_stdout.log, server_stderr.log)
  Data/dogs_vs_cats/           # optional if you set CLIP_IMAGE_ROOT (see below)
```

- **Game executable name:** Default launcher expects **`VRproj.exe`** (see `ProjectSettings` → productName). If you rename the build, open `LaunchSpatialFind.ps1` and set `$GameExe` to match (the script expects **`<name>_Data`** next to it).
- **Python:** Prefer shipping a **`.venv`** folder created on your dev PC (`python -m venv .venv`, then install deps + CLIP per [server.py](server.py) header and [requirements.txt](requirements.txt)). End users then do not need a system Python install.
- **Dataset:** If images are not under `Assets/StreamingAssets/dogs_vs_cats` relative to `server.py`, set `CLIP_IMAGE_ROOT` to the folder that contains `cat`/`dog` (or your layout). In `LaunchSpatialFind.cmd`, uncomment the `set "CLIP_IMAGE_ROOT=..."` line and point it at `Data\dogs_vs_cats` (or your path). Match **ImageGridPanel → absolute dataset root** in Unity to the same folder.

## First run

The server may take **many minutes** the first time it embeds all images and builds the FAISS index. Later runs load from cache (see [server.py](server.py) docstring). Progress and errors appear under **`logs/`**.

## Firewall

The server listens on **`0.0.0.0:8000`**. Windows may show a firewall prompt the first time **Python** accepts inbound connections. Allow it on private networks, or the Unity client may not load images.

## Running without the launcher

Advanced users can run `python server.py` (or `.venv\Scripts\python.exe server.py`) from the same directory, then start **`VRproj.exe`** manually. The Unity client uses `http://127.0.0.1:8000` by default ([ClipSearchApiClient.cs](Assets/Scenes/script/ClipSearchApiClient.cs)).

## Optional: no Python on the PC

Shipping a working **`.venv`** is the straightforward approach but the folder is large (CLIP + PyTorch + FAISS). Packaging **`server.exe`** with PyInstaller/Nuitka is possible but is a separate build step; substitute that executable inside your own batch file or replace the `Start-Process` server step in `LaunchSpatialFind.ps1` if you go that route.

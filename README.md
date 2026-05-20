# Spatial Find

A Unity XR application for interactive image retrieval using CLIP embeddings. The user refines a text-based image search through a 4-stage funnel of progressively narrower grids, picking the closest match at each stage. Works on VR headsets and on PC for headless testing.

## Overview

- **Frontend:** Unity (C#), world-space CLIP grid panels, OpenXR + VIVE OpenXR plug-in.
- **Backend:** Python FastAPI server (`server.py`) running CLIP ViT-B/32 + FAISS over a local image dataset.
- **Pipeline:**
  1. User types or speaks a query.
  2. Stage 1 — 90 candidate images (10×9 grid).
  3. Stage 2 — 30 images (6×5).
  4. Stage 3 — 9 images (3×3).
  5. Stage 4 — final 1×1 result.

  Each stage searches the full FAISS index, fusing the original text query with the mean embedding of all images the user has selected so far.

## Repository layout

```
Spatial_Find/
├── Assets/
│   ├── Scenes/
│   │   ├── SpatialFind1.unity           Main scene
│   │   └── script/                      Runtime C# scripts
│   ├── StreamingAssets/                 (Optional) dataset location
│   └── …
├── server.py                            CLIP + FAISS retrieval backend
├── download_dogs_vs_cats.py             Dataset bootstrapper
├── requirements.txt
└── ProjectSettings/, Packages/          Unity project metadata
```

### Key scripts

| Script | Role |
|---|---|
| `ClipSearchFlowController.cs` | Orchestrates the 4-stage flow; calls the backend `/search` endpoint. |
| `ClipSearchApiClient.cs` | HTTP client (`UnityWebRequest`) for `/search`, `/images/{path}`, `/health`. |
| `ImageGridPanel.cs` | Procedurally builds world-space Canvas grids; loads textures from disk or HTTP. |
| `WorkingController.cs` | VR controller input — laser pointer + trigger raycasts. |
| `PCInputController.cs` | Mouse-click raycasts from `Camera.main` for headless testing. |
| `PromptSpeechInputController.cs` | Push-to-talk speech-to-text for the prompt field. |
| `CurveImageGridPanel.cs` / `CurvedCanvas*.cs` | Optional curved-canvas rendering. |
| `ImageTile.cs` | Lightweight component holding `imageId` on each grid cell. |

## Requirements

### Unity
- Unity 2022.3 LTS (or matching the version recorded in `ProjectSettings/ProjectVersion.txt`).
- Packages: XR Interaction Toolkit, OpenXR Plug-in, VIVE OpenXR, TextMeshPro, Newtonsoft.Json.

### Backend
- Python 3.10+.
- A CUDA-capable GPU is recommended (CLIP embedding the full dataset takes minutes on GPU vs. an hour-plus on CPU).
- Dependencies (see `requirements.txt`):
  ```
  pip install git+https://github.com/openai/CLIP.git faiss-gpu numpy fastapi uvicorn
  ```
  Use `faiss-cpu` instead of `faiss-gpu` on machines without a CUDA GPU. Install a CUDA-matched PyTorch wheel from <https://pytorch.org/get-started/locally/> if `torch.cuda.is_available()` is `False`.

## Setup

### 1. Dataset

Default dataset is Kaggle's Dogs vs. Cats (~25,000 images):

```
python download_dogs_vs_cats.py
```

Default path: `Assets/StreamingAssets/dogs_vs_cats/{cat,dog}/{file}.jpg`.

For faster Unity Editor startup, keep images **outside** `Assets/` (Unity imports every file under `Assets/`):

```powershell
$env:CLIP_IMAGE_ROOT = "C:\path\to\Data\dogs_vs_cats"
```

Then set the matching `Absolute Dataset Root` field on `ImageGridPanel` in the scene.

### 2. Start the backend

```
python server.py
```

- Listens on `0.0.0.0:8000`.
- First run embeds all images and caches to `clip_index.npy`, `clip_paths.json`, `clip_cache_meta.json` (5–10 min on GPU).
- Subsequent runs reload the cache in under 10 seconds when the dataset fingerprint matches.
- Force a rebuild: `set CLIP_FORCE_REBUILD=1`.

Browser POC UI: <http://127.0.0.1:8000/>.

### 3. Configure Unity client

In the scene, on the `ClipSearch_Service` object, set `ClipSearchApiClient.BaseURL`:
- PC editor / same machine: `http://127.0.0.1:8000`
- Standalone headset on the same LAN: `http://<host-LAN-IP>:8000`

### 4. Run

#### PC (no headset)
1. Open `Assets/Scenes/SpatialFind1.unity`.
2. Ensure a `PCInputController` GameObject exists (or run `Scenes → Setup PC Input Controller`).
3. Press Play and click image tiles to advance.

#### VR
1. Plug in / pair the headset and launch its OpenXR runtime (SteamVR, Oculus, VIVE Hub, etc.).
2. Press Play (Editor) or build for the target platform.
3. Use the controller laser; squeeze the trigger to select.

## Input methods

- **VR controller** — `WorkingController.cs` casts a ray from the controller and fires on trigger release.
- **PC mouse** — `PCInputController.cs` casts a ray from the main camera on click.
- **Speech-to-text** — `PromptSpeechInputController.cs` records from the system mic and transcribes the prompt. Set its `Preferred Microphone Name` field to target the headset mic.

All input paths converge on `ClipSearchFlowController.OnUserPickedImageFromPanel()`.

## Configuration reference

| Field | Default | Where |
|---|---|---|
| `BaseURL` | `http://127.0.0.1:8000` | `ClipSearchApiClient` |
| `Canvas scale` | `0.001` (1 UI unit ≈ 1 mm) | `ImageGridPanel.canvasScale` |
| Grid sizes | 10×9 / 6×5 / 3×3 / 1×1 | Stages 1–4 |
| Debug query | `"a brown labrador retriever"` | `ClipSearchFlowController.submitDebugQueryOnStart` |
| `CLIP_IMAGE_ROOT` | unset | Environment variable, server-side |
| `CLIP_DEVICE` | auto | `cuda:0` / `cpu`, environment variable |
| `CLIP_FORCE_REBUILD` | unset | Force re-embedding on next server start |

## API

- `POST /search` — body `{ query, stage, candidates[], selected[] }` → next-stage results.
- `GET  /images/{path}` — serves image files referenced in search results.
- `GET  /health` — liveness check.

## Troubleshooting

- **`ViveAnchor OnInstanceCreate() Use FakeData`** in the console — your OpenXR runtime doesn't expose the HTC anchor extension. Disable **VIVE XR Anchor** under *Edit → Project Settings → XR Plug-in Management → OpenXR* if you aren't using spatial anchors.
- **No microphone device available** — confirm the headset's mic is exposed to Windows (Settings → Sound → Input). Then set `PromptSpeechInputController.Preferred Microphone Name` to that exact device name.
- **`torch.cuda.is_available()` is False** — install a CUDA-matched PyTorch wheel; CLIP's default pip torch is often CPU-only.
- **Unity Editor startup is slow** — your dataset is probably under `Assets/`. Move it out and use `CLIP_IMAGE_ROOT` + `ImageGridPanel.absoluteDatasetRoot`.
- **Headset can't reach the server** — use the host LAN IP in `BaseURL`, not `127.0.0.1`, and open port 8000 in the host firewall.

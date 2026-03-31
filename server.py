"""
CLIP + FAISS image retrieval server — FastAPI backend for Unity VR.

Setup
-----
1. Python 3.10+ recommended.

2. Install dependencies:
       pip install git+https://github.com/openai/CLIP.git faiss-gpu numpy fastapi uvicorn
   (torch and torchvision are pulled in automatically by CLIP.)
   If you don't have a CUDA GPU, replace faiss-gpu with faiss-cpu.

3. Prepare the dataset (50 curated ImageNet-1K classes, ~50 000 images):
       python download_imagenet50.py
   Images go into  assets/StreamingAssets/collague_images/{classname}/{file}.JPEG

4. Run the server (listens on 0.0.0.0:8000):
       python server.py

   First startup embeds all images with CLIP ViT-B/32 and caches the result to
   clip_index.npy + clip_paths.json (5-10 min on GPU).
   Subsequent startups load from cache in under 10 seconds.

5. Open http://127.0.0.1:8000/ for a browser-based POC UI.
   API: POST /search, GET /images/…, GET /health.
"""

from __future__ import annotations

import json
import logging
import time
from pathlib import Path
from typing import Any

import clip
import faiss
import numpy as np
import torch
from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, HTMLResponse
from PIL import Image
from pydantic import BaseModel, Field

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

_SERVER_DIR = Path(__file__).resolve().parent
IMAGE_ROOT = _SERVER_DIR / "assets" / "StreamingAssets" / "collague_images"
IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png"}

CACHE_EMBEDDINGS = _SERVER_DIR / "clip_index.npy"
CACHE_PATHS = _SERVER_DIR / "clip_paths.json"

STAGE_SIZES = [90, 30, 10, 1]
CLIP_MODEL_NAME = "ViT-B/32"
EMBED_BATCH_SIZE = 256

# ---------------------------------------------------------------------------
# Module-level state (populated at startup)
# ---------------------------------------------------------------------------
DEVICE: str = "cuda" if torch.cuda.is_available() else "cpu"
CLIP_MODEL: Any = None
CLIP_PREPROCESS: Any = None
EMBEDDINGS: np.ndarray = np.empty((0, 512), dtype=np.float32)
IMAGE_RECORDS: list[dict[str, str]] = []
RECORD_BY_ID: dict[str, int] = {}  # id -> index into IMAGE_RECORDS / EMBEDDINGS
FAISS_INDEX: faiss.IndexFlatIP | None = None
INDEX_BUILT: bool = False


# ---------------------------------------------------------------------------
# Softmax helper
# ---------------------------------------------------------------------------
def rank_by_softmax(
    scores: np.ndarray, temperature: float = 0.07
) -> tuple[np.ndarray, np.ndarray]:
    """Return (sorted_indices, probabilities) in descending probability order.

    Probabilities sum to 1.0.
    """
    logits = scores / temperature
    logits -= logits.max()  # numerical stability
    exp = np.exp(logits)
    probs = exp / exp.sum()
    order = np.argsort(-probs)
    return order, probs[order]


# ---------------------------------------------------------------------------
# Startup: build or load CLIP index
# ---------------------------------------------------------------------------
def _walk_images() -> list[dict[str, str]]:
    records: list[dict[str, str]] = []
    if not IMAGE_ROOT.is_dir():
        logger.warning("IMAGE_ROOT missing: %s", IMAGE_ROOT.resolve())
        return records
    root = IMAGE_ROOT.resolve()
    for p in sorted(root.rglob("*")):
        if p.is_file() and p.suffix.lower() in IMAGE_EXTENSIONS:
            rel = p.relative_to(root)
            category = rel.parent.name if rel.parent != Path(".") else ""
            stem = rel.stem
            rec_id = f"{category}/{stem}" if category else stem
            records.append(
                {"id": rec_id, "path": rel.as_posix(), "category": category}
            )
    return records


def _embed_images(records: list[dict[str, str]]) -> np.ndarray:
    """Embed all images with CLIP in batches.  Returns L2-normalised (N, 512)."""
    root = IMAGE_ROOT.resolve()
    all_embeddings: list[np.ndarray] = []
    n = len(records)

    for start in range(0, n, EMBED_BATCH_SIZE):
        batch_records = records[start : start + EMBED_BATCH_SIZE]
        images = []
        for rec in batch_records:
            img = Image.open(root / rec["path"]).convert("RGB")
            images.append(CLIP_PREPROCESS(img))
        image_input = torch.stack(images).to(DEVICE)
        with torch.no_grad():
            feats = CLIP_MODEL.encode_image(image_input)
        feats = feats.cpu().numpy().astype(np.float32)
        all_embeddings.append(feats)
        if (start // EMBED_BATCH_SIZE) % 20 == 0:
            logger.info("Embedded %d / %d images", min(start + EMBED_BATCH_SIZE, n), n)

    emb = np.vstack(all_embeddings)
    norms = np.linalg.norm(emb, axis=1, keepdims=True)
    norms[norms == 0] = 1.0
    emb /= norms
    return emb


def build_index() -> None:
    global EMBEDDINGS, IMAGE_RECORDS, RECORD_BY_ID, FAISS_INDEX, INDEX_BUILT
    global CLIP_MODEL, CLIP_PREPROCESS

    t0 = time.perf_counter()
    logger.info("Loading CLIP model %s on %s ...", CLIP_MODEL_NAME, DEVICE)
    CLIP_MODEL, CLIP_PREPROCESS = clip.load(CLIP_MODEL_NAME, device=DEVICE)

    if CACHE_EMBEDDINGS.exists() and CACHE_PATHS.exists():
        logger.info("Loading cached index from %s", CACHE_EMBEDDINGS)
        EMBEDDINGS = np.load(str(CACHE_EMBEDDINGS)).astype(np.float32)
        with open(CACHE_PATHS, "r", encoding="utf-8") as f:
            IMAGE_RECORDS = json.load(f)
    else:
        logger.info("Walking %s for images...", IMAGE_ROOT.resolve())
        IMAGE_RECORDS = _walk_images()
        if not IMAGE_RECORDS:
            logger.warning("No images found — index will be empty")
            EMBEDDINGS = np.empty((0, 512), dtype=np.float32)
        else:
            logger.info("Embedding %d images (batch_size=%d)...", len(IMAGE_RECORDS), EMBED_BATCH_SIZE)
            EMBEDDINGS = _embed_images(IMAGE_RECORDS)
            np.save(str(CACHE_EMBEDDINGS), EMBEDDINGS)
            with open(CACHE_PATHS, "w", encoding="utf-8") as f:
                json.dump(IMAGE_RECORDS, f)
            logger.info("Saved cache to %s and %s", CACHE_EMBEDDINGS, CACHE_PATHS)

    RECORD_BY_ID = {rec["id"]: idx for idx, rec in enumerate(IMAGE_RECORDS)}

    FAISS_INDEX = faiss.IndexFlatIP(512)
    if len(EMBEDDINGS) > 0:
        FAISS_INDEX.add(EMBEDDINGS)

    INDEX_BUILT = True
    elapsed = time.perf_counter() - t0
    logger.info(
        "Index ready: %d images, %.1fs elapsed, CUDA=%s",
        len(IMAGE_RECORDS), elapsed, torch.cuda.is_available(),
    )


# ---------------------------------------------------------------------------
# Pydantic models
# ---------------------------------------------------------------------------
class SearchRequest(BaseModel):
    query: str
    stage: int = Field(ge=1, le=4)
    candidates: list[str] = Field(default_factory=list)
    selected: list[str] = Field(default_factory=list)


class ResultRecord(BaseModel):
    id: str
    path: str
    category: str
    probability: float


class SearchResponse(BaseModel):
    stage: int
    total: int
    results: list[ResultRecord]


# ---------------------------------------------------------------------------
# POC HTML UI
# ---------------------------------------------------------------------------
_POC_UI_HTML = """<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>CLIP Image Search</title>
  <style>
    :root { font-family: system-ui, sans-serif; background: #1a1a1e; color: #e8e8ec; }
    body { max-width: 1100px; margin: 1rem auto; padding: 0 1rem; }
    h1 { font-size: 1.25rem; font-weight: 600; }
    .row { display: flex; flex-wrap: wrap; gap: 0.5rem; align-items: center; margin: 1rem 0; }
    input[type="text"] { flex: 1; min-width: 12rem; padding: 0.5rem 0.6rem; border-radius: 6px;
      border: 1px solid #444; background: #2a2a30; color: inherit; }
    button { padding: 0.5rem 1rem; border-radius: 6px; border: none; background: #3d6df2;
      color: #fff; cursor: pointer; font-weight: 500; }
    button.secondary { background: #444; }
    button:disabled { opacity: 0.5; cursor: not-allowed; }
    .meta { font-size: 0.85rem; color: #9898a6; margin-bottom: 1rem; }
    .err { color: #f66; margin: 0.5rem 0; white-space: pre-wrap; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: 0.75rem; }
    .card { background: #25252c; border-radius: 8px; overflow: hidden; border: 1px solid #333;
      position: relative; transition: transform 0.1s, outline-color 0.15s; }
    .card.pickable { outline: 2px solid transparent; outline-offset: 2px; cursor: pointer; }
    .card.pickable:hover { transform: scale(1.03); outline-color: #555; }
    .card img { width: 100%; height: 140px; object-fit: cover; display: block; background: #111; }
    .card .body { padding: 0.4rem 0.55rem; font-size: 0.7rem; line-height: 1.3; }
    .card .id { font-weight: 600; word-break: break-all; color: #c8d4ff; }
    .card .prob { color: #7fdf7f; }
    .hero { max-width: 480px; margin: 1rem auto; text-align: center; }
    .hero img { width: 100%; max-height: 70vh; object-fit: contain; border-radius: 8px; }
    .stage-bar { display: flex; gap: 0.35rem; margin: 0.75rem 0; }
    .stage-bar .dot { width: 2rem; height: 0.35rem; border-radius: 3px; background: #333; }
    .stage-bar .dot.done { background: #3d6df2; }
    .stage-bar .dot.active { background: #6b8ff8; }
    .picks { display: flex; gap: 0.5rem; align-items: center; margin: 0.5rem 0; flex-wrap: wrap; }
    .picks .pick-thumb { width: 56px; height: 56px; object-fit: cover; border-radius: 6px;
      border: 2px solid #3d6df2; }
    .picks .pick-label { font-size: 0.7rem; color: #9898a6; }
  </style>
</head>
<body>
  <h1>CLIP image retrieval</h1>
  <p class="meta" id="health">Loading…</p>

  <div class="row" id="queryRow">
    <input type="text" id="q" placeholder="Describe what you're looking for…" autocomplete="off" />
    <button type="button" id="go">Search</button>
  </div>

  <div class="stage-bar" id="stageBar">
    <div class="dot" id="dot1"></div>
    <div class="dot" id="dot2"></div>
    <div class="dot" id="dot3"></div>
    <div class="dot" id="dot4"></div>
  </div>
  <p class="meta" id="stageInfo"></p>

  <div id="picksRow" class="picks" style="display:none;">
    <span class="pick-label">Selected:</span>
  </div>

  <div id="controls" style="display:none;">
    <div class="row">
      <button type="button" class="secondary" id="resetBtn">Start over</button>
    </div>
  </div>

  <p class="err" id="err" hidden></p>
  <div class="grid" id="out"></div>
  <div id="heroDone" style="display:none;" class="hero">
    <h2 class="meta">Final pick</h2>
    <div id="heroContent"></div>
    <button type="button" class="secondary" id="heroReset" style="margin-top:1rem;">Start over</button>
  </div>

  <script>
    const STAGE_SIZES = [90, 30, 10, 1];
    let state = { query: "", stage: 0, candidates: [], results: [], selected: [] };
    let advancing = false;

    function imgUrl(path) {
      return "/images/" + path.split("/").map(encodeURIComponent).join("/");
    }

    async function refreshHealth() {
      const el = document.getElementById("health");
      try {
        const r = await fetch("/health");
        const j = await r.json();
        el.textContent = "Status: " + j.status + " · images: " + j.image_count +
          " · index: " + (j.index_built ? "ready" : "building…") +
          " · CUDA: " + j.cuda_available;
      } catch (e) {
        el.textContent = "Could not reach /health";
      }
    }

    function updateStageBar() {
      for (let i = 1; i <= 4; i++) {
        const dot = document.getElementById("dot" + i);
        dot.className = "dot";
        if (i < state.stage) dot.classList.add("done");
        if (i === state.stage) dot.classList.add("active");
      }
      const info = document.getElementById("stageInfo");
      if (state.stage === 0) {
        info.textContent = "Enter a query to start (4 stages: 90 \\u2192 30 \\u2192 10 \\u2192 1).";
      } else if (state.stage <= 3) {
        info.textContent = "Stage " + state.stage + " of 4 \\u2014 showing " +
          state.results.length + " results. Click an image to refine (" +
          STAGE_SIZES[state.stage] + " next).";
      } else {
        info.textContent = "Stage 4 \\u2014 final result.";
      }
    }

    function renderPicks() {
      const row = document.getElementById("picksRow");
      row.innerHTML = '<span class="pick-label">Selected:</span>';
      if (state.selected.length === 0) { row.style.display = "none"; return; }
      row.style.display = "flex";
      for (const s of state.selected) {
        const found = state.results.find(r => r.id === s) ||
          { path: s.replace(/\\/[^/]+$/, "") + "/" + s.split("/").pop() + ".JPEG" };
        const img = document.createElement("img");
        img.className = "pick-thumb";
        img.src = imgUrl(found.path || "");
        img.title = s;
        row.appendChild(img);
      }
    }

    function resetAll() {
      state = { query: "", stage: 0, candidates: [], results: [], selected: [] };
      advancing = false;
      document.getElementById("q").value = "";
      document.getElementById("q").disabled = false;
      document.getElementById("go").style.display = "";
      document.getElementById("controls").style.display = "none";
      document.getElementById("out").innerHTML = "";
      document.getElementById("err").hidden = true;
      document.getElementById("heroDone").style.display = "none";
      document.getElementById("picksRow").style.display = "none";
      updateStageBar();
    }

    function renderCards(results, pickable) {
      const out = document.getElementById("out");
      out.innerHTML = "";
      for (const it of results) {
        const card = document.createElement("div");
        card.className = "card" + (pickable ? " pickable" : "");
        card.dataset.id = it.id;
        card.dataset.path = it.path;
        card.innerHTML =
          '<img src="' + imgUrl(it.path) + '" alt="" loading="lazy" />' +
          '<div class="body">' +
            '<div class="id"></div>' +
            '<div>category: <span class="cat"></span></div>' +
            '<div class="prob"></div>' +
          '</div>';
        card.querySelector(".id").textContent = it.id;
        card.querySelector(".cat").textContent = it.category || "\\u2014";
        card.querySelector(".prob").textContent = "p = " + it.probability.toFixed(4);
        if (pickable) {
          card.addEventListener("click", () => pickImage(it.id));
        }
        out.appendChild(card);
      }
    }

    async function doSearch(query, stage, candidates, selected) {
      const err = document.getElementById("err");
      err.hidden = true;
      const r = await fetch("/search", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ query: query, stage: stage, candidates: candidates, selected: selected }),
      });
      if (!r.ok) throw new Error("HTTP " + r.status + ": " + await r.text());
      return r.json();
    }

    async function pickImage(id) {
      if (advancing || state.stage < 1 || state.stage >= 4) return;
      advancing = true;

      state.selected.push(id);
      renderPicks();

      const nextStage = state.stage + 1;
      document.getElementById("out").innerHTML = '<p class="meta">Loading stage ' + nextStage + '…</p>';
      try {
        const data = await doSearch(state.query, nextStage, state.candidates, state.selected);
        state.stage = nextStage;
        state.results = data.results;
        state.candidates = data.results.map(r => r.id);
        updateStageBar();
        if (nextStage >= 4) {
          document.getElementById("controls").style.display = "none";
          document.getElementById("out").innerHTML = "";
          document.getElementById("heroDone").style.display = "";
          const it = data.results[0];
          const h = document.getElementById("heroContent");
          h.innerHTML = it
            ? '<img src="' + imgUrl(it.path) + '" alt="" />' +
              '<p class="meta">' + it.id + ' (p=' + it.probability.toFixed(4) + ')</p>'
            : '<p class="meta">No result.</p>';
        } else {
          renderCards(data.results, true);
        }
      } catch (e) {
        state.selected.pop();
        renderPicks();
        document.getElementById("err").textContent = String(e.message || e);
        document.getElementById("err").hidden = false;
      } finally {
        advancing = false;
      }
    }

    document.getElementById("go").addEventListener("click", async () => {
      const q = document.getElementById("q").value.trim();
      const err = document.getElementById("err");
      if (!q) { err.textContent = "Enter a query."; err.hidden = false; return; }
      const btn = document.getElementById("go");
      btn.disabled = true;
      document.getElementById("out").innerHTML = '<p class="meta">Searching…</p>';
      try {
        const data = await doSearch(q, 1, [], []);
        state.query = q;
        state.stage = 1;
        state.results = data.results;
        state.candidates = data.results.map(r => r.id);
        state.selected = [];
        document.getElementById("q").disabled = true;
        document.getElementById("go").style.display = "none";
        document.getElementById("controls").style.display = "";
        updateStageBar();
        renderPicks();
        renderCards(data.results, true);
      } catch (e) {
        document.getElementById("out").innerHTML = "";
        err.textContent = String(e.message || e);
        err.hidden = false;
      } finally {
        btn.disabled = false;
      }
    });
    document.getElementById("q").addEventListener("keydown", (e) => {
      if (e.key === "Enter") document.getElementById("go").click();
    });

    document.getElementById("resetBtn").addEventListener("click", resetAll);
    document.getElementById("heroReset").addEventListener("click", resetAll);

    refreshHealth();
    updateStageBar();
  </script>
</body>
</html>"""


# ---------------------------------------------------------------------------
# FastAPI app
# ---------------------------------------------------------------------------
app = FastAPI(title="CLIP image retrieval")
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.on_event("startup")
def startup_event() -> None:
    build_index()


# ---------------------------------------------------------------------------
# Endpoints
# ---------------------------------------------------------------------------
@app.get("/", response_class=HTMLResponse)
def poc_ui() -> str:
    return _POC_UI_HTML


@app.get("/health")
def health() -> dict[str, Any]:
    return {
        "status": "ok",
        "image_count": len(IMAGE_RECORDS),
        "index_built": INDEX_BUILT,
        "cuda_available": torch.cuda.is_available(),
    }


@app.post("/search", response_model=SearchResponse)
def search(body: SearchRequest) -> SearchResponse:
    if not INDEX_BUILT:
        raise HTTPException(status_code=503, detail="index not ready")

    stage = body.stage
    k = STAGE_SIZES[stage - 1]

    try:
        if stage == 1:
            return _search_stage1(body.query, k)
        else:
            return _search_later_stage(stage, k, body.candidates, body.selected)
    except HTTPException:
        raise
    except Exception as exc:
        logger.exception("Search failed at stage %d", stage)
        raise HTTPException(status_code=500, detail=str(exc)) from exc


def _search_stage1(query: str, k: int) -> SearchResponse:
    text_tokens = clip.tokenize([query]).to(DEVICE)
    with torch.no_grad():
        text_feat = CLIP_MODEL.encode_text(text_tokens)
    text_feat = text_feat.cpu().numpy().astype(np.float32)
    text_feat /= np.linalg.norm(text_feat, axis=1, keepdims=True)

    n_search = min(k, FAISS_INDEX.ntotal)
    if n_search == 0:
        return SearchResponse(stage=1, total=0, results=[])

    scores, indices = FAISS_INDEX.search(text_feat, n_search)
    scores = scores[0]
    indices = indices[0]

    order, probs = rank_by_softmax(scores)

    results: list[ResultRecord] = []
    for rank_pos in range(len(order)):
        idx = int(indices[order[rank_pos]])
        rec = IMAGE_RECORDS[idx]
        results.append(
            ResultRecord(
                id=rec["id"],
                path=rec["path"],
                category=rec["category"],
                probability=round(float(probs[rank_pos]), 4),
            )
        )
    return SearchResponse(stage=1, total=len(results), results=results)


def _search_later_stage(
    stage: int, k: int, candidate_ids: list[str], selected_ids: list[str]
) -> SearchResponse:
    if not candidate_ids:
        logger.warning("Stage %d: empty candidates, falling back to zero-vector search", stage)
        n_search = min(k, FAISS_INDEX.ntotal)
        if n_search == 0:
            return SearchResponse(stage=stage, total=0, results=[])
        qvec = np.zeros((1, 512), dtype=np.float32)
        scores, indices = FAISS_INDEX.search(qvec, n_search)
        uniform_p = round(1.0 / max(n_search, 1), 4)
        results = []
        for i in range(n_search):
            idx = int(indices[0][i])
            rec = IMAGE_RECORDS[idx]
            results.append(
                ResultRecord(
                    id=rec["id"], path=rec["path"],
                    category=rec["category"], probability=uniform_p,
                )
            )
        return SearchResponse(stage=stage, total=len(results), results=results)

    valid_indices: list[int] = []
    for cid in candidate_ids:
        idx = RECORD_BY_ID.get(cid)
        if idx is None:
            logger.warning("Stage %d: unknown candidate id %r, skipping", stage, cid)
            continue
        valid_indices.append(idx)

    if not valid_indices:
        return SearchResponse(stage=stage, total=0, results=[])

    cand_embeddings = EMBEDDINGS[valid_indices]  # (C, 512)

    # Build anchor from user-selected images across prior stages
    sel_emb_indices: list[int] = []
    for sid in selected_ids:
        idx = RECORD_BY_ID.get(sid)
        if idx is not None:
            sel_emb_indices.append(idx)
        else:
            logger.warning("Stage %d: unknown selected id %r, skipping", stage, sid)

    if sel_emb_indices:
        anchor = EMBEDDINGS[sel_emb_indices].mean(axis=0, keepdims=True)
    else:
        anchor = cand_embeddings[:min(5, len(valid_indices))].mean(axis=0, keepdims=True)

    norm = np.linalg.norm(anchor, axis=1, keepdims=True)
    norm[norm == 0] = 1.0
    anchor /= norm

    cosine_scores = (cand_embeddings @ anchor.T).squeeze()  # (C,)
    if cosine_scores.ndim == 0:
        cosine_scores = cosine_scores.reshape(1)

    order, probs = rank_by_softmax(cosine_scores)

    results: list[ResultRecord] = []
    for rank_pos in range(min(k, len(order))):
        orig_idx = valid_indices[order[rank_pos]]
        rec = IMAGE_RECORDS[orig_idx]
        results.append(
            ResultRecord(
                id=rec["id"],
                path=rec["path"],
                category=rec["category"],
                probability=round(float(probs[rank_pos]), 4),
            )
        )
    return SearchResponse(stage=stage, total=len(results), results=results)


@app.get("/images/{file_path:path}")
def serve_image(file_path: str) -> FileResponse:
    if ".." in file_path or file_path.startswith(("/", "\\")):
        raise HTTPException(status_code=404, detail="Not found")

    root = IMAGE_ROOT.resolve()
    try:
        target = (root / file_path).resolve()
    except OSError:
        raise HTTPException(status_code=404, detail="Not found") from None

    try:
        target.relative_to(root)
    except ValueError:
        raise HTTPException(status_code=404, detail="Not found") from None

    if not target.is_file():
        raise HTTPException(status_code=404, detail="Not found")

    return FileResponse(target)


# ---------------------------------------------------------------------------
# CLI entry point
# ---------------------------------------------------------------------------
if __name__ == "__main__":
    import uvicorn

    uvicorn.run("server:app", host="0.0.0.0", port=8000, reload=False)

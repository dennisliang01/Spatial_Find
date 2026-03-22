"""
AI-powered image retrieval POC — FastAPI backend for Unity.

Setup
-----
1. Python 3.11+ recommended.

2. Install dependencies:
       pip install fastapi uvicorn ollama

3. Install Ollama from https://ollama.com and start it locally (default http://localhost:11434).

4. Pull the chat model:
       ollama pull mistral

5. Place image files under:
       <repo>/assets/StreamingAssets/collage_images
   (Resolved relative to server.py, not the shell cwd.)
   Folder name = category; filename stem split on underscores = tags.

6. Run the server (listens on 0.0.0.0:8000) from any directory:
       python /path/to/server.py
       python server.py
   Open http://127.0.0.1:8000/ for a barebones UI (quick search + 4-level refine).
   API: POST /search, POST /search/refine (stateless refinement), GET /images/…, GET /health.

7. Export processed metadata (same records as in-memory index) to JSON:
       python server.py export-dataset
       python server.py export-dataset -o path/to/out.json
"""

from __future__ import annotations

import concurrent.futures
import json
import logging
import re
import time
from pathlib import Path
from typing import Any

import ollama
from ollama import ResponseError
from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, HTMLResponse
from pydantic import BaseModel, Field, model_validator

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

# Resolve dataset relative to this file so indexing works even if cwd is not the repo root.
_SERVER_DIR = Path(__file__).resolve().parent
IMAGE_ROOT = _SERVER_DIR / "assets" / "StreamingAssets" / "collage_images"
IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".webp", ".gif"}
OLLAMA_MODEL = "mistral"
OLLAMA_TIMEOUT_SEC = 30.0

# Default result counts per refinement level (1..4). Client may override via top_n.
LEVEL_TOP_N: tuple[int, int, int, int] = (90, 30, 10, 1)

_SYSTEM_PROMPT = """You are a ranking assistant for image retrieval.
You MUST respond with ONLY a valid JSON array of strings — image IDs in order from most relevant to least relevant for the user's query.
Every ID in your array MUST appear exactly as given in the candidate list. Do not invent IDs, do not rename them, do not add commentary.
If multiple candidates tie, preserve a stable order. Output nothing before or after the JSON array."""


class SearchRequest(BaseModel):
    query: str
    top_n: int = Field(ge=1)


class ImageRecord(BaseModel):
    id: str
    path: str
    category: str
    tags: list[str]


class RefineTurn(BaseModel):
    selected_ids: list[str] = Field(default_factory=list)
    user_note: str = ""


class RefineRequest(BaseModel):
    """Stateless multi-level refine: client sends full history each call (Option A)."""

    level: int = Field(ge=1, le=4)
    base_query: str
    turns: list[RefineTurn] = Field(default_factory=list)
    candidate_ids: list[str] = Field(default_factory=list)
    top_n: int | None = None

    @model_validator(mode="after")
    def turns_match_level(self) -> RefineRequest:
        expected = self.level - 1
        if len(self.turns) != expected:
            raise ValueError(
                f"For level {self.level}, expected exactly {expected} prior turn(s) in `turns`, "
                f"got {len(self.turns)}"
            )
        return self


_POC_UI_HTML = """<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Image search POC</title>
  <style>
    :root { font-family: system-ui, sans-serif; background: #1a1a1e; color: #e8e8ec; }
    body { max-width: 1100px; margin: 1rem auto; padding: 0 1rem; }
    h1 { font-size: 1.25rem; font-weight: 600; }
    .tabs { display: flex; gap: 0.5rem; margin: 1rem 0; }
    .tabs button { background: #333; color: #ccc; }
    .tabs button.active { background: #3d6df2; color: #fff; }
    .panel { display: none; }
    .panel.visible { display: block; }
    .row { display: flex; flex-wrap: wrap; gap: 0.5rem; align-items: center; margin: 1rem 0; }
    input[type="text"] { flex: 1; min-width: 12rem; padding: 0.5rem 0.6rem; border-radius: 6px;
      border: 1px solid #444; background: #2a2a30; color: inherit; }
    input[type="number"] { width: 4rem; padding: 0.5rem; border-radius: 6px;
      border: 1px solid #444; background: #2a2a30; color: inherit; }
    textarea { width: 100%; min-height: 4rem; padding: 0.5rem; border-radius: 6px;
      border: 1px solid #444; background: #2a2a30; color: inherit; box-sizing: border-box; }
    button { padding: 0.5rem 1rem; border-radius: 6px; border: none; background: #3d6df2;
      color: #fff; cursor: pointer; font-weight: 500; }
    button.secondary { background: #444; }
    button:disabled { opacity: 0.5; cursor: not-allowed; }
    .meta { font-size: 0.85rem; color: #9898a6; margin-bottom: 1rem; }
    .err { color: #f66; margin: 0.5rem 0; white-space: pre-wrap; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(200px, 1fr)); gap: 1rem; }
    .card { background: #25252c; border-radius: 8px; overflow: hidden; border: 1px solid #333; position: relative; }
    .card.pickable { outline-offset: 2px; }
    .card.pickable:has(input:checked) { outline: 2px solid #3d6df2; }
    .card img { width: 100%; height: 160px; object-fit: cover; display: block; background: #111; }
    .card .chk { position: absolute; top: 6px; left: 6px; width: 1.1rem; height: 1.1rem; z-index: 1; }
    .card .body { padding: 0.5rem 0.65rem; font-size: 0.75rem; line-height: 1.35; }
    .card .id { font-weight: 600; word-break: break-all; color: #c8d4ff; }
    .card .tags { color: #9898a6; margin-top: 0.25rem; }
    .hero { max-width: 480px; margin: 1rem auto; text-align: center; }
    .hero img { width: 100%; max-height: 70vh; object-fit: contain; border-radius: 8px; }
  </style>
</head>
<body>
  <h1>Image retrieval POC</h1>
  <p class="meta" id="health">Loading…</p>
  <div class="tabs">
    <button type="button" id="tabQuick" class="active">Quick search</button>
    <button type="button" id="tabRefine">4-level refine</button>
  </div>

  <div id="panelQuick" class="panel visible">
    <div class="row">
      <input type="text" id="q" placeholder="Query e.g. snow algoma" autocomplete="off" />
      <label>top_n <input type="number" id="n" value="8" min="1" max="200" /></label>
      <button type="button" id="go">Search</button>
    </div>
    <p class="err" id="err" hidden></p>
    <div class="grid" id="out"></div>
  </div>

  <div id="panelRefine" class="panel">
    <p class="meta" id="refineStep">Level 1 of 4 — targets ~90 / ~30 / ~10 / 1 images.</p>
    <div class="row" id="refineStartRow">
      <input type="text" id="rq" placeholder="e.g. I want a brown dog in snow" autocomplete="off" />
      <button type="button" id="refineStart">Start level 1</button>
    </div>
    <p class="err" id="refineErr" hidden></p>
    <div id="refineNoteWrap" style="display:none;">
      <label class="meta">Optional note for next step</label>
      <textarea id="refineNote" placeholder="e.g. more like the second one, outdoor light"></textarea>
      <div class="row" style="margin-top:0.5rem;">
        <button type="button" id="refineNext">Continue to next level</button>
        <button type="button" class="secondary" id="refineReset">Start over</button>
      </div>
    </div>
    <div class="grid" id="refineOut"></div>
    <div id="refineDone" style="display:none;" class="hero">
      <h2 class="meta">Final pick</h2>
      <div id="refineHero"></div>
      <button type="button" class="secondary" id="refineDoneReset" style="margin-top:1rem;">Start over</button>
    </div>
  </div>

  <script>
    const LEVEL_TOP = [90, 30, 10, 1];
    function imgUrl(path) {
      return "/images/" + path.split("/").map(encodeURIComponent).join("/");
    }
    async function refreshHealth() {
      const el = document.getElementById("health");
      try {
        const r = await fetch("/health");
        const j = await r.json();
        el.textContent = "Status: " + j.status + " · indexed images: " + j.image_count;
      } catch (e) {
        el.textContent = "Could not reach /health — is the server running?";
      }
    }
    function setTab(quick) {
      document.getElementById("tabQuick").classList.toggle("active", quick);
      document.getElementById("tabRefine").classList.toggle("active", !quick);
      document.getElementById("panelQuick").classList.toggle("visible", quick);
      document.getElementById("panelRefine").classList.toggle("visible", !quick);
    }
    document.getElementById("tabQuick").addEventListener("click", () => setTab(true));
    document.getElementById("tabRefine").addEventListener("click", () => setTab(false));

    async function search() {
      const q = document.getElementById("q").value.trim();
      const n = Math.max(1, parseInt(document.getElementById("n").value, 10) || 8);
      const out = document.getElementById("out");
      const err = document.getElementById("err");
      const go = document.getElementById("go");
      err.hidden = true;
      out.innerHTML = "";
      if (!q) { err.textContent = "Enter a query."; err.hidden = false; return; }
      go.disabled = true;
      out.innerHTML = "<p class=\\"meta\\">Searching…</p>";
      try {
        const r = await fetch("/search", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ query: q, top_n: n }),
        });
        if (!r.ok) throw new Error("HTTP " + r.status + ": " + await r.text());
        const items = await r.json();
        out.innerHTML = "";
        if (!items.length) { out.innerHTML = "<p class=\\"meta\\">No results.</p>"; return; }
        for (const it of items) {
          const card = document.createElement("div");
          card.className = "card";
          card.innerHTML =
            '<img src="' + imgUrl(it.path) + '" alt="" loading="lazy" />' +
            '<div class="body"><div class="id"></div><div>category: <span class="cat"></span></div><div class="tags"></div></div>';
          card.querySelector(".id").textContent = it.id;
          card.querySelector(".cat").textContent = it.category || "—";
          card.querySelector(".tags").textContent = "tags: " + (it.tags || []).join(", ");
          out.appendChild(card);
        }
      } catch (e) {
        out.innerHTML = "";
        err.textContent = String(e.message || e);
        err.hidden = false;
      } finally {
        go.disabled = false;
      }
    }
    document.getElementById("go").addEventListener("click", search);
    document.getElementById("q").addEventListener("keydown", (e) => { if (e.key === "Enter") search(); });

    let refine = { baseQuery: "", turns: [], lastIds: [], displayLevel: 0 };

    function refineSetStepText() {
      const el = document.getElementById("refineStep");
      if (refine.displayLevel === 0) {
        el.textContent = "Level 1 of 4 — up to ~" + LEVEL_TOP[0] + " images. Enter your goal, then Start.";
        return;
      }
      if (refine.displayLevel >= 4) {
        el.textContent = "Level 4 of 4 — final result.";
        return;
      }
      el.textContent = "Level " + refine.displayLevel + " of 4 — pick image(s), add an optional note, then continue (~" +
        LEVEL_TOP[refine.displayLevel] + " results next).";
    }

    function resetRefine() {
      refine = { baseQuery: "", turns: [], lastIds: [], displayLevel: 0 };
      document.getElementById("rq").value = "";
      document.getElementById("refineNote").value = "";
      document.getElementById("refineStartRow").style.display = "";
      document.getElementById("refineNoteWrap").style.display = "none";
      document.getElementById("refineOut").innerHTML = "";
      document.getElementById("refineErr").hidden = true;
      document.getElementById("refineDone").style.display = "none";
      document.getElementById("refineHero").innerHTML = "";
      refineSetStepText();
    }

    function renderRefineCards(items, pickable) {
      const out = document.getElementById("refineOut");
      out.innerHTML = "";
      for (const it of items) {
        const card = document.createElement("div");
        card.className = "card" + (pickable ? " pickable" : "");
        const chk = pickable
          ? '<input type="checkbox" class="chk" data-id="' + it.id.replace(/"/g, "&quot;") + '" />'
          : "";
        card.innerHTML = chk +
          '<img src="' + imgUrl(it.path) + '" alt="" loading="lazy" />' +
          '<div class="body"><div class="id"></div><div>category: <span class="cat"></span></div><div class="tags"></div></div>';
        card.querySelector(".id").textContent = it.id;
        card.querySelector(".cat").textContent = it.category || "—";
        card.querySelector(".tags").textContent = "tags: " + (it.tags || []).join(", ");
        out.appendChild(card);
      }
    }

    async function refineRequest(payload) {
      const err = document.getElementById("refineErr");
      err.hidden = true;
      const r = await fetch("/search/refine", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      });
      if (!r.ok) throw new Error("HTTP " + r.status + ": " + await r.text());
      return r.json();
    }

    document.getElementById("refineStart").addEventListener("click", async () => {
      const q = document.getElementById("rq").value.trim();
      const err = document.getElementById("refineErr");
      if (!q) { err.textContent = "Enter a base query."; err.hidden = false; return; }
      refine.baseQuery = q;
      refine.turns = [];
      refine.lastIds = [];
      refine.displayLevel = 0;
      const btn = document.getElementById("refineStart");
      btn.disabled = true;
      document.getElementById("refineOut").innerHTML = "<p class=\\"meta\\">Loading level 1…</p>";
      try {
        const items = await refineRequest({
          level: 1,
          base_query: refine.baseQuery,
          turns: [],
          candidate_ids: [],
          top_n: LEVEL_TOP[0],
        });
        refine.lastIds = items.map((x) => x.id);
        refine.displayLevel = 1;
        document.getElementById("refineStartRow").style.display = "none";
        document.getElementById("refineNoteWrap").style.display = "";
        refineSetStepText();
        renderRefineCards(items, true);
      } catch (e) {
        err.textContent = String(e.message || e);
        err.hidden = false;
        document.getElementById("refineOut").innerHTML = "";
      } finally {
        btn.disabled = false;
      }
    });

    document.getElementById("refineNext").addEventListener("click", async () => {
      if (refine.displayLevel < 1 || refine.displayLevel > 3) return;
      const nextLevel = refine.displayLevel + 1;
      const boxes = document.querySelectorAll("#refineOut input.chk:checked");
      const selected = Array.from(boxes).map((b) => b.getAttribute("data-id"));
      const note = document.getElementById("refineNote").value.trim();
      refine.turns = refine.turns.concat([{ selected_ids: selected, user_note: note }]);
      const btn = document.getElementById("refineNext");
      btn.disabled = true;
      document.getElementById("refineOut").innerHTML = "<p class=\\"meta\\">Loading level " + nextLevel + "…</p>";
      document.getElementById("refineErr").hidden = true;
      try {
        const items = await refineRequest({
          level: nextLevel,
          base_query: refine.baseQuery,
          turns: refine.turns,
          candidate_ids: refine.lastIds,
          top_n: LEVEL_TOP[nextLevel - 1],
        });
        refine.lastIds = items.map((x) => x.id);
        refine.displayLevel = nextLevel;
        document.getElementById("refineNote").value = "";
        refineSetStepText();
        if (nextLevel >= 4) {
          document.getElementById("refineNoteWrap").style.display = "none";
          document.getElementById("refineOut").innerHTML = "";
          document.getElementById("refineDone").style.display = "";
          const it = items[0];
          const h = document.getElementById("refineHero");
          h.innerHTML = it
            ? '<img src="' + imgUrl(it.path) + '" alt="" /><p class="meta">' + it.id + "</p>"
            : "<p class=\\"meta\\">No result.</p>";
        } else {
          renderRefineCards(items, true);
        }
      } catch (e) {
        document.getElementById("refineErr").textContent = String(e.message || e);
        document.getElementById("refineErr").hidden = false;
        refine.turns.pop();
      } finally {
        btn.disabled = false;
      }
    });

    document.getElementById("refineReset").addEventListener("click", resetRefine);
    document.getElementById("refineDoneReset").addEventListener("click", resetRefine);

    refreshHealth();
    refineSetStepText();
  </script>
</body>
</html>"""


app = FastAPI(title="Image retrieval POC")
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

IMAGE_RECORDS: list[dict[str, Any]] = []


def _load_image_records() -> list[dict[str, Any]]:
    records: list[dict[str, Any]] = []
    if not IMAGE_ROOT.is_dir():
        logger.warning("IMAGE_ROOT does not exist or is not a directory: %s", IMAGE_ROOT.resolve())
        return records

    root = IMAGE_ROOT.resolve()
    paths: list[Path] = []
    for p in sorted(root.rglob("*")):
        if p.is_file() and p.suffix.lower() in IMAGE_EXTENSIONS:
            paths.append(p)

    for file_path in paths:
        rel = file_path.relative_to(root)
        stem = rel.stem
        category = rel.parent.name if rel.parent != Path(".") else ""
        tags = stem.split("_") if stem else []
        rel_posix = rel.as_posix()
        rec_id = f"{category}/{stem}" if category else stem
        records.append(
            {
                "id": rec_id,
                "path": rel_posix,
                "category": category,
                "tags": tags,
            }
        )
    return records


IMAGE_RECORDS = _load_image_records()
RECORD_BY_ID: dict[str, dict[str, Any]] = {r["id"]: r for r in IMAGE_RECORDS}
logger.info(
    "Loaded %d image record(s) from %s",
    len(IMAGE_RECORDS),
    IMAGE_ROOT.resolve(),
)


def _query_tokens(query: str) -> list[str]:
    return [t.lower() for t in query.split() if t.strip()]


def _whole_word_match(token: str, text: str) -> bool:
    """True if token appears as its own word in text (not as a substring inside a longer token)."""
    if not token or not text:
        return False
    try:
        return (
            re.search(rf"(?<!\w){re.escape(token)}(?!\w)", text, flags=re.IGNORECASE) is not None
        )
    except re.error:
        return False


def _prefilter_candidates(query: str, all_records: list[dict[str, Any]]) -> list[dict[str, Any]]:
    tokens = _query_tokens(query)
    if not tokens:
        return list(all_records)

    out: list[dict[str, Any]] = []
    for rec in all_records:
        cat_l = (rec["category"] or "").lower()
        if any(_whole_word_match(tok, cat_l) for tok in tokens):
            out.append(rec)
            continue
        tags = rec["tags"]
        if any(any(_whole_word_match(tok, tag.lower()) for tag in tags) for tok in tokens):
            out.append(rec)
    return out


def _format_candidates_for_prompt(candidates: list[dict[str, Any]]) -> str:
    lines: list[str] = []
    for i, rec in enumerate(candidates, start=1):
        tags_str = ", ".join(rec["tags"])
        lines.append(f"{i}. {rec['id']} | category={rec['category']} | tags=[{tags_str}]")
    return "\n".join(lines)


def _extract_json_array(text: str) -> str | None:
    s = text.strip()
    fence = re.match(r"^```(?:json)?\s*([\s\S]*?)\s*```$", s, re.IGNORECASE)
    if fence:
        s = fence.group(1).strip()
    start = s.find("[")
    end = s.rfind("]")
    if start == -1 or end == -1 or end <= start:
        return None
    return s[start : end + 1]


def _parse_ranked_ids(content: str) -> list[str] | None:
    chunk = _extract_json_array(content)
    if not chunk:
        return None
    try:
        data = json.loads(chunk)
    except json.JSONDecodeError:
        return None
    if not isinstance(data, list):
        return None
    out: list[str] = []
    for item in data:
        if isinstance(item, str):
            out.append(item)
    return out


def _ollama_chat(messages: list[dict[str, str]]) -> Any:
    return ollama.chat(model=OLLAMA_MODEL, messages=messages)


def _rank_with_ollama_from_parts(
    intro: str, candidates: list[dict[str, Any]]
) -> tuple[list[str] | None, float, bool, bool]:
    """
    Returns (ranked_ids_or_none, elapsed_seconds, timed_out, ollama_request_failed).
    ollama_request_failed is True when the Ollama call raised (already logged); do not log "bad JSON".
    """
    user_body = (
        f"{intro.strip()}\n\n"
        f"Candidates (use ONLY these IDs, verbatim):\n{_format_candidates_for_prompt(candidates)}\n\n"
        "Return a JSON array of candidate IDs, most relevant first."
    )
    messages = [
        {"role": "system", "content": _SYSTEM_PROMPT},
        {"role": "user", "content": user_body},
    ]

    executor = concurrent.futures.ThreadPoolExecutor(max_workers=1)
    future = executor.submit(_ollama_chat, messages)
    t0 = time.perf_counter()
    timed_out = False
    ollama_request_failed = False
    response: Any = None
    try:
        response = future.result(timeout=OLLAMA_TIMEOUT_SEC)
    except concurrent.futures.TimeoutError:
        timed_out = True
        response = None
        logger.warning(
            "Ollama chat timed out after %.1fs (candidate_count=%d)",
            OLLAMA_TIMEOUT_SEC,
            len(candidates),
        )
    except ConnectionError as e:
        ollama_request_failed = True
        response = None
        logger.warning(
            "Ollama unreachable (%s); candidate_count=%d — using fallback order. "
            "Start Ollama and ensure the mistral model is pulled.",
            e,
            len(candidates),
        )
    except ResponseError as e:
        ollama_request_failed = True
        response = None
        logger.warning(
            "Ollama returned an error (%s); candidate_count=%d — using fallback order. "
            "If you see 'model not found', run: ollama pull %s",
            e,
            len(candidates),
            OLLAMA_MODEL,
        )
    except Exception:
        ollama_request_failed = True
        logger.exception(
            "Ollama chat failed (candidate_count=%d); using pre-filter order fallback",
            len(candidates),
        )
        response = None
    finally:
        executor.shutdown(wait=False, cancel_futures=False)

    elapsed = time.perf_counter() - t0

    if timed_out:
        return None, elapsed, True, False
    if response is None:
        return None, elapsed, False, ollama_request_failed

    content = ""
    try:
        msg = response.get("message") or {}
        content = msg.get("content") or ""
    except (AttributeError, TypeError):
        content = str(response)

    ids = _parse_ranked_ids(content)
    return ids, elapsed, False, False


def _rank_with_ollama(
    user_query: str, candidates: list[dict[str, Any]]
) -> tuple[list[str] | None, float, bool, bool]:
    intro = f"User query: {user_query}"
    return _rank_with_ollama_from_parts(intro, candidates)


def _records_in_id_order(ids: list[str], record_by_id: dict[str, dict[str, Any]]) -> list[dict[str, Any]]:
    out: list[dict[str, Any]] = []
    for i in ids:
        rec = record_by_id.get(i)
        if rec is not None:
            out.append(rec)
    return out


def _build_refine_intro(base_query: str, turns: list[RefineTurn]) -> str:
    parts: list[str] = [f"Original request: {base_query.strip()}"]
    for step_i, t in enumerate(turns, start=1):
        lines: list[str] = [f"Step {step_i} feedback:"]
        note = (t.user_note or "").strip()
        if note:
            lines.append(f'  User note: "{note}"')
        if t.selected_ids:
            lines.append("  User highlighted these images:")
            for sid in t.selected_ids:
                rec = RECORD_BY_ID.get(sid)
                if rec:
                    tags_str = ", ".join(rec["tags"])
                    lines.append(
                        f"    - id={rec['id']} | category={rec['category']} | tags=[{tags_str}]"
                    )
                else:
                    lines.append(f"    - id={sid} (unknown id)")
        parts.append("\n".join(lines))
    parts.append(
        "Rank the following candidate image IDs for the combined intent (most relevant first). "
        "Consider the original request and every refinement step."
    )
    return "\n\n".join(parts)


def _records_from_ranked_ids(
    ranked_ids: list[str],
    candidates: list[dict[str, Any]],
    top_n: int,
) -> list[dict[str, Any]]:
    cand_by_id = {rec["id"]: rec for rec in candidates}
    cand_id_set = set(cand_by_id.keys())
    seen: set[str] = set()
    ordered: list[dict[str, Any]] = []
    for rid in ranked_ids:
        if rid in cand_id_set and rid not in seen:
            ordered.append(cand_by_id[rid])
            seen.add(rid)
        if len(ordered) >= top_n:
            break
    return ordered[:top_n]


def _fallback_slice(candidates: list[dict[str, Any]], top_n: int) -> list[dict[str, Any]]:
    return candidates[:top_n]


@app.get("/", response_class=HTMLResponse)
def poc_ui() -> str:
    return _POC_UI_HTML


@app.get("/health")
def health() -> dict[str, Any]:
    return {"status": "ok", "image_count": len(IMAGE_RECORDS)}


@app.post("/search")
def search(body: SearchRequest) -> list[ImageRecord]:
    all_recs = IMAGE_RECORDS
    if not all_recs:
        return []

    candidates = _prefilter_candidates(body.query, all_recs)
    if not candidates:
        candidates = list(all_recs)

    n_cand = len(candidates)
    logger.info("Search: passing %d candidate(s) to Ollama (top_n=%d)", n_cand, body.top_n)

    ranked_ids, ollama_elapsed, timed_out, ollama_failed = _rank_with_ollama(body.query, candidates)
    logger.info("Ollama call finished in %.3fs (timed_out=%s)", ollama_elapsed, timed_out)

    if timed_out or ranked_ids is None:
        if not timed_out and not ollama_failed:
            logger.warning("Malformed or unparseable Ollama JSON; using pre-filter order fallback")
        sliced = _fallback_slice(candidates, body.top_n)
        return [ImageRecord(**r) for r in sliced]

    ordered = _records_from_ranked_ids(ranked_ids, candidates, body.top_n)
    return [ImageRecord(**r) for r in ordered]


@app.post("/search/refine")
def search_refine(body: RefineRequest) -> list[ImageRecord]:
    """Multi-level refinement; client resends full `turns` and `candidate_ids` each request (stateless)."""
    all_recs = IMAGE_RECORDS
    if not all_recs:
        return []

    for ti, t in enumerate(body.turns):
        for sid in t.selected_ids:
            if sid not in RECORD_BY_ID:
                raise HTTPException(
                    status_code=400,
                    detail=f"Unknown selected_id in turns[{ti}]: {sid!r}",
                )

    tn = body.top_n if body.top_n is not None else LEVEL_TOP_N[body.level - 1]
    tn = min(max(tn, 1), 500)

    if body.level == 1:
        candidates = _prefilter_candidates(body.base_query, all_recs)
        if not candidates:
            candidates = list(all_recs)
        intro = f"User query: {body.base_query.strip()}"
    else:
        if not body.candidate_ids:
            logger.warning(
                "Refine level %d: empty candidate_ids; falling back to full index",
                body.level,
            )
            pool = list(all_recs)
        else:
            unknown = [i for i in body.candidate_ids if i not in RECORD_BY_ID]
            if unknown:
                raise HTTPException(
                    status_code=400,
                    detail=f"Unknown candidate_ids (showing first 8): {unknown[:8]}",
                )
            pool = _records_in_id_order(body.candidate_ids, RECORD_BY_ID)
            if not pool:
                logger.warning(
                    "Refine level %d: candidate_ids resolved to no records; falling back to full index",
                    body.level,
                )
                pool = list(all_recs)
        candidates = pool
        intro = _build_refine_intro(body.base_query, body.turns)

    n_cand = len(candidates)
    logger.info(
        "Refine L%d: passing %d candidate(s) to Ollama (top_n=%d)",
        body.level,
        n_cand,
        tn,
    )

    ranked_ids, ollama_elapsed, timed_out, ollama_failed = _rank_with_ollama_from_parts(intro, candidates)
    logger.info("Ollama call finished in %.3fs (timed_out=%s)", ollama_elapsed, timed_out)

    if timed_out or ranked_ids is None:
        if not timed_out and not ollama_failed:
            logger.warning("Malformed or unparseable Ollama JSON; using pre-filter order fallback")
        sliced = _fallback_slice(candidates, tn)
        return [ImageRecord(**r) for r in sliced]

    ordered = _records_from_ranked_ids(ranked_ids, candidates, tn)
    return [ImageRecord(**r) for r in ordered]


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


if __name__ == "__main__":
    import argparse
    import sys

    import uvicorn

    parser = argparse.ArgumentParser(description="Image retrieval POC backend.")
    sub = parser.add_subparsers(dest="cmd", required=False)

    sub.add_parser("serve", help="Run HTTP server (default if no subcommand is given).")
    p_exp = sub.add_parser(
        "export-dataset",
        help="Write processed dataset metadata (id, path, category, tags) to a JSON file.",
    )
    p_exp.add_argument(
        "-o",
        "--output",
        type=Path,
        default=Path("collage_images_dataset.json"),
        help="Output file path (default: collage_images_dataset.json).",
    )

    args = parser.parse_args()
    if args.cmd == "export-dataset":
        out: Path = args.output
        out.write_text(json.dumps(IMAGE_RECORDS, indent=2), encoding="utf-8")
        print(f"Wrote {len(IMAGE_RECORDS)} record(s) to {out.resolve()}", file=sys.stderr)
        sys.exit(0)

    uvicorn.run("server:app", host="0.0.0.0", port=8000, reload=False)

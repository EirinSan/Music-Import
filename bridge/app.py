"""
Bridge HTTP optionnel pour Jellyfin.Plugin.DoujinStyle.

Utile uniquement si l'adresse IP datacenter/seedbox (ex: Ultra.cc) reçoit une page
anti-bot sur doujinstyle.com alors que votre connexion résidentielle y accède normalement.
Ce service effectue uniquement des requêtes HTTP standards (aucun contournement de CAPTCHA).
"""

from __future__ import annotations

import os
import re
from html import unescape
from typing import Optional
from urllib.parse import urljoin

import httpx
from bs4 import BeautifulSoup
from fastapi import FastAPI, Header, HTTPException, Query, Response

DOUJINSTYLE_BASE = os.environ.get("DOUJINSTYLE_BASE_URL", "https://doujinstyle.com").rstrip("/")
BRIDGE_API_KEY = os.environ.get("BRIDGE_API_KEY", "").strip()

DEFAULT_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 "
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
    ),
    "Accept-Language": "en-US,en;q=0.9,fr;q=0.8,ja;q=0.7",
}

ALBUM_ID_RE = re.compile(r"id=(\d+)", re.IGNORECASE)
CSS_URL_RE = re.compile(r"url\(['\"]?([^)'\"]+)['\"]?\)", re.IGNORECASE)

app = FastAPI(title="DoujinStyle Jellyfin Bridge", version="1.0.0")


def verify_key(x_bridge_key: Optional[str]) -> None:
    if BRIDGE_API_KEY and x_bridge_key != BRIDGE_API_KEY:
        raise HTTPException(status_code=401, detail="Invalid X-Bridge-Key")


def is_challenge_html(html: str) -> bool:
    lowered = html.lower()
    return (
        "<title>one moment, please...</title>" in lowered
        or "<title>just a moment...</title>" in lowered
        or "/cdn-cgi/challenge-platform/" in lowered
        or "cf-turnstile" in lowered
    )


@app.get("/health")
async def health() -> dict:
    return {"status": "ok", "upstream": DOUJINSTYLE_BASE}


@app.get("/api/search")
async def search(
    q: str = Query(..., min_length=2),
    only_flac: bool = Query(False),
    x_bridge_key: Optional[str] = Header(default=None),
) -> list[dict]:
    verify_key(x_bridge_key)
    params = {"p": "search", "source": "1", "type": "blanket", "result": q, "page": "0"}
    if only_flac:
        params["format0"] = "on"

    async with httpx.AsyncClient(headers=DEFAULT_HEADERS, follow_redirects=True, timeout=20.0) as client:
        resp = await client.get(f"{DOUJINSTYLE_BASE}/", params=params)
        html = resp.text

    if is_challenge_html(html):
        raise HTTPException(
            status_code=503,
            detail="DoujinStyle returned an anti-bot challenge page to this bridge IP.",
        )

    soup = BeautifulSoup(html, "html.parser")
    results: list[dict] = []
    seen_ids: set[str] = set()

    for box in soup.select(".gridBox"):
        details = box.select_one(".gridDetails") or box
        links = details.select("a[href]")
        if not links:
            continue

        album_link = None
        album_id = None
        for a in links:
            href = a.get("href", "")
            m = ALBUM_ID_RE.search(href)
            if m:
                album_link = a
                album_id = m.group(1)
                break

        if not album_link or not album_id or album_id in seen_ids:
            continue
        seen_ids.add(album_id)

        title = (album_link.get("title") or album_link.get_text(" ", strip=True)).strip()
        if not title:
            continue

        artist_link = details.select_one("a[href*='type=artist']") or (links[1] if len(links) > 1 else None)
        artist = artist_link.get_text(" ", strip=True) if artist_link else "Doujin Artist"

        cover_url = None
        for styled in box.select("[style]"):
            m_cov = CSS_URL_RE.search(styled.get("style", ""))
            if m_cov and "default_cover" not in m_cov.group(1):
                raw = m_cov.group(1).strip()
                if raw.startswith("./"):
                    cover_url = f"{DOUJINSTYLE_BASE}/{raw[2:]}"
                elif raw.startswith("/thumbs/"):
                    cover_url = f"{DOUJINSTYLE_BASE}{raw}"
                elif raw.startswith("/"):
                    cover_url = f"{DOUJINSTYLE_BASE}/thumbs{raw}"
                else:
                    cover_url = urljoin(f"{DOUJINSTYLE_BASE}/", raw)
                break

        tags = []
        for t in details.select(".gridTinyTag a"):
            val = unescape(t.get_text(" ", strip=True)).replace("&#45;", "-")
            if val and val not in tags:
                tags.append(val)

        results.append(
            {
                "Id": album_id,
                "Title": unescape(title),
                "Artist": unescape(artist) or "Doujin Artist",
                "CoverUrl": cover_url,
                "PageUrl": f"{DOUJINSTYLE_BASE}/?p=page&type=1&id={album_id}",
                "Tags": tags,
                "Tracks": [],
                "DetailsLoaded": False,
            }
        )

    return results


@app.get("/api/album/{album_id}")
async def album_detail(
    album_id: str,
    x_bridge_key: Optional[str] = Header(default=None),
) -> dict:
    verify_key(x_bridge_key)
    page_url = f"{DOUJINSTYLE_BASE}/?p=page&type=1&id={album_id}"
    async with httpx.AsyncClient(headers=DEFAULT_HEADERS, follow_redirects=True, timeout=20.0) as client:
        resp = await client.get(page_url)
        html = resp.text

    if is_challenge_html(html):
        raise HTTPException(status_code=503, detail="DoujinStyle challenge detected.")

    soup = BeautifulSoup(html, "html.parser")
    h2 = soup.select_one("h2")
    if not h2:
        raise HTTPException(status_code=404, detail="Album not found")

    title = h2.get_text(" ", strip=True)
    artist = "Doujin Artist"
    fmt = None
    date_added = None
    tags: list[str] = []

    cover_a = soup.select_one("#imgClick a[href]")
    cover_img = soup.select_one(".coverWrap[src]")
    cover_url = None
    if cover_a:
        cover_url = urljoin(f"{DOUJINSTYLE_BASE}/", cover_a["href"])
    elif cover_img:
        cover_url = urljoin(f"{DOUJINSTYLE_BASE}/", cover_img["src"])

    for span in soup.select(".pageSpan1"):
        key = span.get_text(" ", strip=True).rstrip(":").lower()
        val_node = span.find_next_sibling()
        if not val_node:
            continue
        if key in ("artist", "artists"):
            a_nodes = val_node.select("a")
            artist = ", ".join(a.get_text(" ", strip=True) for a in a_nodes) if a_nodes else val_node.get_text(" ", strip=True)
        elif key == "format":
            fmt = val_node.get_text(" ", strip=True)
        elif key == "date added":
            date_added = val_node.get_text(" ", strip=True)
        elif key == "tags":
            tags = [a.get_text(" ", strip=True) for a in val_node.select("a") if a.get_text(strip=True)]

    return {
        "Id": album_id,
        "Title": title,
        "Artist": artist,
        "CoverUrl": cover_url or f"{DOUJINSTYLE_BASE}/thumbs/{album_id}.jpg",
        "PageUrl": page_url,
        "Format": fmt,
        "DateAdded": date_added,
        "Tags": tags,
        "Tracks": [],
        "DetailsLoaded": True,
    }


@app.get("/api/resolve/{album_id}")
async def resolve_download(
    album_id: str,
    x_bridge_key: Optional[str] = Header(default=None),
) -> dict:
    verify_key(x_bridge_key)
    page_url = f"{DOUJINSTYLE_BASE}/?p=page&type=1&id={album_id}"
    form_data = {"type": "1", "id": album_id, "source": "0", "download_link": ""}

    async with httpx.AsyncClient(headers={**DEFAULT_HEADERS, "Referer": page_url}, follow_redirects=False, timeout=20.0) as client:
        resp = await client.post(f"{DOUJINSTYLE_BASE}/", data=form_data)

    location = resp.headers.get("location")
    if not location:
        if is_challenge_html(resp.text):
            raise HTTPException(status_code=503, detail="DoujinStyle challenge detected.")
        raise HTTPException(status_code=502, detail="No redirect Location returned by DoujinStyle.")

    return {"DownloadUrl": urljoin(f"{DOUJINSTYLE_BASE}/", location)}


@app.get("/api/image")
async def proxy_image(
    url: str = Query(...),
    x_bridge_key: Optional[str] = Header(default=None),
) -> Response:
    verify_key(x_bridge_key)
    async with httpx.AsyncClient(headers={**DEFAULT_HEADERS, "Referer": f"{DOUJINSTYLE_BASE}/"}, follow_redirects=True, timeout=15.0) as client:
        resp = await client.get(url)
        if resp.status_code != 200 or "html" in resp.headers.get("content-type", "").lower():
            raise HTTPException(status_code=404, detail="Image unavailable")
        return Response(content=resp.content, media_type=resp.headers.get("content-type", "image/jpeg"))

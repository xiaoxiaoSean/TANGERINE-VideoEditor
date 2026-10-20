"""Collect original CC0 instrumental music and retain the author's license evidence.

This is an offline resource-authoring tool; the editor never invokes it.
Only album pages explicitly linking to CC0 are eligible. Original MPEG audio
frames remain unchanged; embedded album artwork is removed losslessly so that
third-party cover images are not redistributed with the editor.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import datetime
import hashlib
import html
import io
import json
import re
import shutil
import sys
import time
import urllib.parse
import urllib.request
import zipfile
from pathlib import Path

SITE = "https://loyaltyfreakmusic.com"
CC0 = "https://creativecommons.org/publicdomain/zero/1.0/"
ROOT = Path(__file__).resolve().parents[1]
DESTINATION = ROOT / "TANGERINE-VideoEditor/Resources/BuiltinMusic"
CACHE = ROOT / "tools/.builtin-music-collection"

# Each album has an explicit CC0 link on its composer's original album page.
# Background-music albums contain instrumental arrangements. Avoid remixes,
# sampled historical recordings, French songs, and live/vocal releases.
ALBUM_SELECTION = [
    ("bleeps-and-bloops", "电子节拍", "轻快、科技"),
    ("instrumental-rb-beats-to-sing-or-rap-on", "器乐节奏蓝调", "律动、都市"),
    ("robot-dance", "电子舞曲", "动感、活力"),
    ("roller-disco-dance-dance", "合成器波", "复古、欢快"),
    ("relaxing-ukulele", "尤克里里", "明亮、轻松"),
    ("the-adventure-goes-on-vol-1", "原声民谣", "温暖、旅行"),
    ("hyper-metal", "器乐摇滚", "热血、力量"),
    ("positive-attitude", "轻松治愈", "温柔、积极"),
    ("ambient-work-for-movies-and-games-vol-2", "悬疑氛围", "神秘、悬疑"),
    ("to-chill-and-stay-awake", "氛围电子", "平静、专注"),
    ("melodies-with-a-beat", "低保真节拍", "悠闲、放松"),
    ("incredible-kart-game", "游戏配乐", "冒险、童趣"),
    ("a-tale-is-never-forgotten", "原声吉他", "叙事、温暖"),
    ("lofi-guitar-ambiences", "吉他氛围", "舒缓、沉静"),
]


def fetch(url: str) -> bytes:
    for attempt in range(4):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "TangerineMusicResourceCollector/1.0"})
            with urllib.request.urlopen(request, timeout=90) as response:
                return response.read()
        except Exception:
            if attempt == 3:
                raise
            time.sleep(attempt + 1)
    raise RuntimeError("Download did not complete")


def text(value: str) -> str:
    return html.unescape(re.sub(r"<[^>]*>", " ", value)).strip()


def album_metadata(url: str) -> dict:
    slug = url.rstrip("/").rsplit("/", 1)[1]
    local = CACHE / f"{slug}.html"
    if not local.exists():
        local.write_bytes(fetch(url))
    source = local.read_text(encoding="utf-8")
    title = re.search(r"<h1>(.*?)</h1>", source, re.S)
    author = re.search(r"<p>By\s*:(.*?)</p>", source, re.S)
    genre = re.search(r"<p>Genre\s*:(.*?)</p>", source, re.S)
    description = re.search(r'<div class="post_description">(.*?)</div>', source, re.S)
    downloads = re.findall(r'href="([^"]+\.zip)"', source)
    return {
        "slug": slug,
        "title": text(title[1]) if title else slug,
        "artist": text(author[1]) if author else "",
        "genres": text(genre[1]) if genre else "",
        "description": text(description[1]) if description else "",
        "sourceUrl": url,
        "licenseId": "CC0-1.0" if CC0 in source else "OTHER",
        "downloadUrl": downloads[0] if downloads else "",
    }


def discover() -> None:
    CACHE.mkdir(parents=True, exist_ok=True)
    def index(page: int) -> list[str]:
        path = CACHE / f"index-{page:02d}.html"
        if not path.exists():
            path.write_bytes(fetch(f"{SITE}/music/" + (f"page/{page}/" if page > 1 else "")))
        return re.findall(r'href="(https://loyaltyfreakmusic.com/music/[^\"]+)" class="tease_thumb"', path.read_text(encoding="utf-8"))
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as executor:
        urls = list(dict.fromkeys(url for group in executor.map(index, range(1, 19)) for url in group))
        albums = list(executor.map(album_metadata, urls))
    (CACHE / "albums.json").write_text(json.dumps(albums, ensure_ascii=False, indent=2), encoding="utf-8")
    for album in albums:
        if album["licenseId"] == "CC0-1.0" and album["artist"] in ("Komiku", "Loyalty Freak Music", "Monplaisir"):
            print(json.dumps(album, ensure_ascii=False), flush=True)


def download_albums() -> None:
    CACHE.mkdir(parents=True, exist_ok=True)
    catalog = CACHE / "albums.json"
    if catalog.exists():
        albums = {album["slug"]: album for album in json.loads(catalog.read_text(encoding="utf-8"))}
    else:
        # A fresh checkout needs only the curated original album pages; the
        # optional discovery command is not required to reproduce the bundle.
        with concurrent.futures.ThreadPoolExecutor(max_workers=5) as executor:
            selected = list(executor.map(album_metadata, (f"{SITE}/music/{slug}/" for slug, _, _ in ALBUM_SELECTION)))
        albums = {album["slug"]: album for album in selected}
    def download(selection: tuple[str, str, str]) -> None:
        slug, genre, mood = selection
        album = albums[slug]
        if album["licenseId"] != "CC0-1.0" or not album["downloadUrl"]:
            raise ValueError(f"Album lacks an original CC0 grant or download: {slug}")
        destination = CACHE / f"{slug}.zip"
        if not destination.exists():
            content = fetch(album["downloadUrl"])
            # Parse the complete archive before publishing its local cache.
            with zipfile.ZipFile(io.BytesIO(content)) as archive:
                if not any(name.lower().endswith(".mp3") for name in archive.namelist()):
                    raise ValueError(f"No MP3 music in {slug}")
            destination.write_bytes(content)
        with zipfile.ZipFile(destination) as archive:
            from mutagen.mp3 import MP3
            songs = []
            for entry in archive.infolist():
                if not entry.filename.lower().endswith(".mp3") or entry.filename.startswith("__MACOSX/"):
                    continue
                music = MP3(io.BytesIO(archive.read(entry)))
                tags = music.tags
                title = str(tags.get("TIT2", Path(entry.filename).stem)) if tags else Path(entry.filename).stem
                songs.append({"entry": entry.filename, "title": title, "durationSeconds": music.info.length,
                              "size": entry.file_size})
        album["genre"] = genre
        album["mood"] = mood
        album["songs"] = songs
        (CACHE / f"{slug}.json").write_text(json.dumps(album, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"{slug}: {len(songs)} original MP3 tracks, {destination.stat().st_size // 1024 // 1024} MiB", flush=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=5) as executor:
        list(executor.map(download, ALBUM_SELECTION))


def audio_payload(content: bytes) -> bytes:
    """Exclude metadata to establish that packaging never changes MPEG frames."""
    start = 0
    if content.startswith(b"ID3"):
        size = sum((content[6 + index] & 0x7f) << (7 * (3 - index)) for index in range(4))
        start = 10 + size
        if content[5] & 0x10:
            start += 10
    end = len(content) - 128 if len(content) >= 128 and content[-128:-125] == b"TAG" else len(content)
    return content[start:end]


def slugify(value: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", value.lower()).strip("-")


def collect() -> None:
    from mutagen.mp3 import MP3
    albums = [json.loads((CACHE / f"{slug}.json").read_text(encoding="utf-8")) for slug, _, _ in ALBUM_SELECTION
              if (CACHE / f"{slug}.json").exists()]
    if len(albums) < 8:
        raise ValueError("At least eight independent genre groups must have completed downloading")
    existing_manifest = DESTINATION / "manifest.json"
    frozen = json.loads(existing_manifest.read_text(encoding="utf-8"))["tracks"] if existing_manifest.exists() else None
    pools = []
    for album in albums:
        # Reject explicit vocal cues and short jingles. Selection always keeps
        # complete songs; no slicing or duplicate excerpts can fill the count.
        songs = [song for song in album["songs"] if 45 <= song["durationSeconds"] <= 600 and
                 not re.search(r"\b(vocals?|spoken|narrat(?:ion|or)|interview|a cappella)\b", song["title"], re.I)]
        songs.sort(key=lambda song: song["durationSeconds"] < 60)
        pools.append([(album, song) for song in songs])
    if frozen:
        eligible = {(album["sourceUrl"], song["entry"]): (album, song) for pool in pools for album, song in pool}
        # An existing shipped manifest freezes its selection and order. Later
        # optional album downloads must not silently replace bundled tracks.
        pools = [[eligible[(track["sourceUrl"], track["sourceArchiveEntry"])]] for track in frozen]
    chosen = []
    seen_payloads = set()
    while any(pools) and len(chosen) < 100:
        for pool in pools:
            if not pool or len(chosen) >= 100:
                continue
            album, song = pool.pop(0)
            with zipfile.ZipFile(CACHE / f"{album['slug']}.zip") as archive:
                original = archive.read(song["entry"])
            payload_hash = hashlib.sha256(audio_payload(original)).hexdigest()
            if payload_hash in seen_payloads:
                continue
            seen_payloads.add(payload_hash)
            chosen.append((album, song, original, payload_hash))
    if len(chosen) != 100:
        raise ValueError(f"Need 100 distinct complete instrumental tracks; only {len(chosen)} eligible")
    audio_directory = DESTINATION / "audio"
    license_directory = DESTINATION / "LICENSES"
    album_evidence = license_directory / "albums"
    audio_directory.mkdir(parents=True, exist_ok=True)
    album_evidence.mkdir(parents=True, exist_ok=True)
    fetched = datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds")
    tracks = []
    for album, song, original, payload_hash in chosen:
        stable_suffix = hashlib.sha256((album["sourceUrl"] + "\n" + song["entry"]).encode()).hexdigest()[:8]
        track_id = "music." + album["slug"][:42] + "-" + slugify(song["title"])[:48] + "-" + stable_suffix
        destination = audio_directory / f"{track_id}.mp3"
        destination.write_bytes(original)
        music = MP3(destination)
        if music.tags:
            music.tags.delall("APIC")
            music.save()
        packaged = destination.read_bytes()
        if hashlib.sha256(audio_payload(packaged)).hexdigest() != payload_hash:
            raise ValueError(f"Packaging altered the original MPEG audio: {track_id}")
        evidence_path = f"LICENSES/albums/{album['slug']}.html"
        shutil.copyfile(CACHE / f"{album['slug']}.html", DESTINATION / evidence_path)
        tracks.append({
            "id": track_id, "title": song["title"], "sourceTitle": song["title"],
            "artist": album["artist"], "genre": album["genre"], "mood": album["mood"],
            "durationSeconds": round(music.info.length, 6),
            "audioFile": f"audio/{track_id}.mp3", "thumbnailFile": f"thumbnails/{track_id}.png",
            "sourceUrl": album["sourceUrl"], "downloadUrl": album["downloadUrl"],
            "sourceArchiveEntry": song["entry"], "sourceAlbum": album["title"], "sourceCategory": album["genres"],
            "licenseId": "CC0-1.0", "licenseUrl": CC0, "licenseEvidenceFile": evidence_path,
            "sha256": hashlib.sha256(packaged).hexdigest(), "sourceSha256": hashlib.sha256(original).hexdigest(),
            "audioPayloadSha256": payload_hash, "instrumental": True,
            "attribution": f"{song['title']} — {album['artist']} ({album['title']}), CC0 1.0; {album['sourceUrl']}",
            "description": f"{album['genre']}纯音乐；来自作者发布的 CC0 专辑《{album['title']}》。保留完整音乐，未剪辑、未转码。",
            "packagingNote": "仅去除 ID3 内嵌专辑封面；原始 MPEG 音频帧保持逐字节一致。", "collectedAtUtc": fetched,
        })
    manifest = {"schemaVersion": 1, "licenseId": "CC0-1.0", "licenseUrl": CC0, "tracks": tracks}
    temporary_manifest = DESTINATION / "manifest.json.part"
    temporary_manifest.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    temporary_manifest.replace(DESTINATION / "manifest.json")
    (license_directory / "Author-FAQ.html").write_bytes(fetch(f"{SITE}/faq/"))
    try:
        (license_directory / "CC0-1.0.txt").write_bytes(fetch(CC0 + "legalcode.txt"))
    except Exception:
        (license_directory / "CC0-1.0.html").write_bytes(fetch(CC0 + "legalcode.en"))
    album_names = "\n".join(f"- {album['title']} — {album['artist']}：{album['sourceUrl']}" for album in albums)
    (license_directory / "README.md").write_text(
        "# 内置音乐来源与许可\n\n"
        "100 首完整器乐来自作曲者 Rrrrrose 的官方音乐站 Loyalty Freak Music，包含其 Komiku、Loyalty Freak Music、Monplaisir 艺名。每首选中作品所在的官方专辑页均明确链接 CC0 1.0；逐专辑原页存放于 albums/。\n\n"
        "许可：CC0 1.0 Universal，允许复制、修改、商业使用与再分发，无需取得额外许可。许可全文见 CC0-1.0.txt（如官网返回 HTML，则为 CC0-1.0.html）；作者 FAQ 原页见 Author-FAQ.html。并未依赖“免费下载”推定再分发权。\n\n"
        "仅提取原始 ZIP 中的完整 MP3；不截取、不拼接、不改变速度、不重编码。统一移除 ID3 APIC 内嵌专辑图片，避免将第三方专辑图一起打包。sourceSha256 为原 MP3，sha256 为去图后的打包文件，audioPayloadSha256 为不含头尾 ID3 的音频数据，采集器会确认前后相同。软件缩略封面另行生成。\n\n"
        "风格按原专辑器乐编配分类；排除明确歌曲/人声专辑、翻唱混音和来源说明含不确定采样的专辑。保留原作品名称与作者姓名，并提供自愿署名文本。作品不得被登记为自身独占的 Content ID 内容。\n\n"
        "manifest.json 包含逐曲原始专辑页、下载 ZIP 地址、原 ZIP 文件名、作者、原始分类、时长、许可和校验值。evidence-index.json 记录采集时许可证据文件的 SHA-256。\n\n"
        "## 选用专辑\n\n" + album_names + "\n", encoding="utf-8")
    provenance = [{"file": str(path.relative_to(DESTINATION)).replace("\\", "/"),
                   "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
                  for path in sorted(license_directory.rglob("*")) if path.is_file() and path.name != "evidence-index.json"]
    (license_directory / "evidence-index.json").write_text(json.dumps({"collectedAtUtc": fetched, "files": provenance}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"tracks": len(tracks), "genres": len({track['genre'] for track in tracks}),
                      "bytes": sum((DESTINATION / track['audioFile']).stat().st_size for track in tracks),
                      "manifest": str(DESTINATION / "manifest.json")}, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--discover", action="store_true")
    parser.add_argument("--download", action="store_true")
    parser.add_argument("--collect", action="store_true")
    options = parser.parse_args()
    if options.discover:
        discover()
    if options.download:
        download_albums()
    if options.collect:
        collect()

# Silent end-to-end download test for gated direct-MP4 streams:
# Mints a fresh stream URL from a site's media JSON API (mimicking companion
# extension DOM sniff), POSTs an extension-shaped payload to the app bridge
# (no dialog), polls startup.log to a terminal state (END OK), and verifies
# the output with ffprobe.
#
# Generic by design: every site-specific value arrives via CLI flags. Never
# hardcode site hosts, page paths, or API routes in this file.
#
# Usage:
#   python silent_gated_mp4_test.py --page-url <watch page>
#       --stream-url <stream JSON template, {id} and {media_id} placeholders>
#       [--meta-url <metadata JSON template, {id} placeholder>]
#       [--meta-media-key <dotted path, list indices allowed; default medias.0.media_id>]
#       [--id <content id>] [--quality 1080p] [--title <fallback title>]
#
# The stream endpoint must return JSON with a `qualities` array whose items
# carry the file in `src` (or `url`/`file`) and the label in `quality`
# (or `size`/`label`).

import argparse
import json
import os
import re
import ssl
import subprocess
import sys
import time
import urllib.request

BRIDGE = 'http://127.0.0.1:45732/api/download'
HEALTH = 'http://127.0.0.1:45732/api/health'
LOG = os.path.expandvars(r'%LOCALAPPDATA%\WrenchDownloader\startup.log')
HISTORY = os.path.expandvars(r'%LOCALAPPDATA%\WrenchDownloader\history.json')
POLL_INTERVAL = 2
TIMEOUT_SEC = 180
UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36'


def fail(msg, code=1):
    print(f'GATEDMP4-TEST FAIL: {msg}')
    sys.exit(code)


def bridge_up():
    try:
        with urllib.request.urlopen(HEALTH, timeout=5) as r:
            return r.status == 200
    except Exception:
        return False


def fetch_json(url, referer):
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    headers = {
        'User-Agent': UA,
        'Accept': 'application/json, text/plain, */*',
        'Referer': referer,
    }
    req = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(req, timeout=10, context=ctx) as resp:
        return json.loads(resp.read().decode('utf-8', errors='ignore'))


def dig(obj, path):
    cur = obj
    for part in path.split('.'):
        if isinstance(cur, list):
            try:
                cur = cur[int(part)]
            except (ValueError, IndexError):
                return None
        elif isinstance(cur, dict):
            cur = cur.get(part)
        else:
            return None
        if cur is None:
            return None
    return cur


def mint_stream(args):
    media_id = ''
    title = args.title
    if args.meta_url:
        try:
            meta = fetch_json(args.meta_url.format(id=args.id), args.page_url)
        except Exception as e:
            fail(f'Failed to fetch content metadata: {e}')
        media_id = str(dig(meta, args.meta_media_key) or '')
        if not media_id:
            fail(f'Metadata key {args.meta_media_key!r} yielded no media id')
        if not title:
            title = str(meta.get('title') or meta.get('name') or f'Gated_Video_{args.id}')
    if not title:
        title = f'Gated_Video_{args.id}'

    stream_url = args.stream_url.format(id=args.id, media_id=media_id)
    try:
        stream_data = fetch_json(stream_url, args.page_url)
    except Exception as e:
        fail(f'Failed to fetch stream data: {e}')

    qualities = stream_data.get('qualities', [])
    if not qualities:
        fail('No stream qualities returned by stream endpoint')

    q_entry = next((q for q in qualities
                    if args.quality.lower() in str(q.get('quality', '')).lower()
                    or args.quality.lower() in str(q.get('size', '')).lower()), None)
    if not q_entry:
        q_entry = qualities[-1]

    direct_url = q_entry.get('src') or q_entry.get('url') or q_entry.get('file')
    if not direct_url:
        fail('Chosen quality entry carries no stream URL')
    actual_quality = str(q_entry.get('size') or q_entry.get('quality') or q_entry.get('label') or args.quality)
    if actual_quality.isdigit():
        actual_quality = f'{actual_quality}p'

    print(f'[+] Minted stream: quality={actual_quality}')
    print(f'[+] Direct URL: {direct_url[:80]}...')
    return direct_url, actual_quality, title


def post_download(video_url, quality, title, page_url):
    payload = {
        'url': video_url,
        'title': f'{title} - {quality}',
        'quality': quality,
        'format': 'mp4',
        'pageUrl': page_url,
        'referrer': page_url,
        'userAgent': UA,
        'audioUrl': '',
        'audioReferrer': '',
        'prompt': False,
    }
    req = urllib.request.Request(
        BRIDGE,
        data=json.dumps(payload).encode('utf-8'),
        headers={'Content-Type': 'application/json'}
    )
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            res = json.loads(r.read().decode('utf-8'))
            return res.get('id')
    except Exception as e:
        fail(f'Bridge POST failed: {e}')


def wait_terminal(track_id, start_pos=0):
    deadline = time.time() + TIMEOUT_SEC
    pos = start_pos
    started = False
    start_at = time.time()

    print(f'[+] Polling {LOG} for track {track_id[:8]}...')
    while time.time() < deadline:
        if os.path.exists(LOG):
            try:
                with open(LOG, encoding='utf-8', errors='replace') as f:
                    f.seek(pos)
                    chunk = f.read()
                    pos = f.tell()
                    for line in chunk.splitlines():
                        if track_id[:8] in line:
                            short = line[:200]
                            if 'START' in line or 'BRIDGE-RECV' in line:
                                started = True
                                print(f'  [LOG START] {short}')
                            if 'END OK' in line or 'completed' in line:
                                print(f'  [LOG END OK] {short}')
                                return True
                            if 'END FAILED' in line or 'terminal failure' in line:
                                print(f'  [LOG END FAILED] {short}')
                                fail(f'Download failed in app: {short}')
            except Exception:
                pass

        if not started and (time.time() - start_at > 30):
            fail('No START line within 30s. A modal dialog might be blocking (ensure Settings -> Show download dialog is OFF).')

        time.sleep(POLL_INTERVAL)

    fail(f'Timed out waiting for download to complete after {TIMEOUT_SEC}s')


def verify_output(track_id, expected_quality):
    entry = None
    for _ in range(15):
        try:
            if os.path.exists(HISTORY):
                with open(HISTORY, encoding='utf-8') as f:
                    history = json.load(f)
                entry = next((h for h in history if h.get('Id') == track_id and h.get('SavePath') and os.path.exists(h.get('SavePath', ''))), None)
                if entry:
                    break
        except Exception:
            pass
        time.sleep(1)

    if not entry or not entry.get('SavePath') or not os.path.exists(entry['SavePath']):
        fail(f'No valid history entry or file on disk found for track_id={track_id}')

    saved_path = entry['SavePath']
    file_size = os.path.getsize(saved_path)
    file_size_mb = file_size / (1024 * 1024)
    print(f'[+] Saved file: {saved_path} ({file_size_mb:.2f} MB)')

    if file_size < 1024 * 1024:
        fail(f'File size is suspiciously small: {file_size} bytes')

    try:
        cmd = [
            'ffprobe', '-v', 'error',
            '-show_entries', 'stream=index,codec_type,codec_name,width,height',
            '-of', 'default=noprint_wrappers=1',
            saved_path
        ]
        out = subprocess.run(cmd, capture_output=True, text=True, timeout=30)
        ff_out = out.stdout.strip()
        print('[+] ffprobe output:')
        for line in ff_out.splitlines():
            print(f'    {line}')
    except Exception as e:
        fail(f'ffprobe execution failed: {e}')

    mh = re.search(r'height=(\d+)', ff_out)
    mw = re.search(r'width=(\d+)', ff_out)
    if not mh or not mw:
        fail('ffprobe did not detect video stream dimensions')

    width = int(mw.group(1))
    height = int(mh.group(1))
    print(f'[+] Video dimensions: {width}x{height}')

    want_h = int(re.search(r'\d+', expected_quality).group(0)) if re.search(r'\d+', expected_quality) else 1080
    if height != want_h:
        fail(f'Resolution mismatch: expected {want_h}p, got {height}p')

    has_audio = 'codec_type=audio' in ff_out
    print(f'[+] Audio stream present: {has_audio}')
    if not has_audio:
        print('[!] Warning: no audio stream detected in media')

    print(f'=== SUCCESS: GATED MP4 {expected_quality} SILENT DOWNLOAD VERIFIED ===')
    return 0


def main():
    ap = argparse.ArgumentParser(description='Silent gated direct-MP4 download test (all site values via flags).')
    ap.add_argument('--page-url', required=True)
    ap.add_argument('--stream-url', required=True)
    ap.add_argument('--meta-url', default='')
    ap.add_argument('--meta-media-key', default='medias.0.media_id')
    ap.add_argument('--id', default='')
    ap.add_argument('--quality', default='1080p')
    ap.add_argument('--title', default='')
    args = ap.parse_args()

    if not bridge_up():
        fail('WrenchDownloader bridge is offline on port 45732')

    video_url, actual_q, title = mint_stream(args)
    start_pos = os.path.getsize(LOG) if os.path.exists(LOG) else 0
    track_id = post_download(video_url, actual_q, title, args.page_url)
    print(f'[+] POSTed download to bridge: track_id={track_id}')

    wait_terminal(track_id, start_pos)
    ret = verify_output(track_id, actual_q)
    sys.exit(ret)


if __name__ == '__main__':
    main()

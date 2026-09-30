# How the IDM App Receives Data from the Extension

IDM ships no app-side source code. Everything below was established by
decoding the extension source (`IDM extension/IDMGCExt`), reading the live
Windows registry, binary-string analysis of the installed app binaries, and
independent public reports (nilaoda Blog discussion #59, Chrome native-
messaging docs, IDM FAQ). Statements marked **[observed]** were verified on
this machine; **[reported]** comes from public sources.

## TL;DR

Two channels, in priority order:

1. **Primary: localhost WebSocket server** — `ws://127.0.0.1:1001/`, hosted by
   `IDMNetMon.dll`/`IDMNetMon64.dll`, subprotocol
   `plugin.v3.internetdownloadmanager.com`, custom framed messages.
2. **Fallback: Chrome Native Messaging** — host name `com.tonec.idm`,
   registered in the Windows registry, served by the `IDMMsgHost.exe` shim.

Plus a third trick: **download interception via `http://127.0.0.1:1001/status/…`
redirects** ([reported], superuser.com).

## 1. Channel 1 — Localhost WebSocket (primary)

### Connection (extension `background.js`, `n.Ba`, line 37) [observed]

```text
ws://127.0.0.1:1001/?cid=<client-id>&rnd=<9 random digits>
ws://0.1.0.1:1001/?cid=<client-id>&rnd=<9 random digits>   (second attempt)
subprotocol: plugin.v3.internetdownloadmanager.com
per-attempt timeout: 1500 ms → rotate endpoint, retry with backoff (5s × round, ≤120 rounds)
```

### Hello (extension `n.Na`, line 38) [observed]

On open, the extension sends message `[113,93,…]` carrying: extension build
flags, numeric build id (`16845059`), the client id (`ca`, persisted in
`storage.local` under `client`), UI locale, and `manifest_version`.
Field envelope: `{[112]:…, [113]:…, [125]: <json>, [116]: <locale>}`.

### Framing (function `S`, line 41 + `qb`/`wb`) [observed]

- Messages are arrays of **numeric-tagged fields** joined by `;`.
- Over WebSocket they are sent as a **`Blob`** (`ws.send(new Blob(segments))`).
- Incoming `onmessage` data goes through the `wb` parser; native-messaging
  input is wrapped in a `Blob` first, so both channels share one parser.
- Heartbeat: message `[0,1,1]` every 15 s (`n.Ma`, line 40).

### Download-candidate message (function `X`, ~lines 99–100) [observed]

Tag → meaning (decoded from code + community field table):

| Tag | Meaning |
|---|---|
| `4` | file type string (`M3U8`, `TS`, …) |
| `6` | target stream URL |
| `7` | source page URL |
| `8` | action: `1` = download now, `2` = add to queue, `4` = show dialog |
| `11` | request headers (verbatim) |
| `13` | response headers |
| `50` | referer / page context |
| `51` | cookies |
| `54` | User-Agent |
| `100` | custom file name |
| `110` | description |

A community-reproduced example ([reported], nilaoda Blog):

```js
ws = new WebSocket("ws://127.0.0.1:1001/?cid=" + Math.random().toString().substr(2, 9),
                   "plugin.v3.internetdownloadmanager.com");
task = { 6: url, 7: pageUrl, 8: 4, 50: referer, 51: cookie, 54: ua, 100: name, 110: desc };
```

### Server side: `IDMNetMon.dll` / `IDMNetMon64.dll` [observed]

Binary-string hits (ASCII) in both DLLs:

```text
127.0.0.1:1001   0.1.0.1:1001
Origin   Sec-WebSocket-Protocol   Sec-WebSocket-Key   X-IDM-ProcessId
/client/   /status/
Access-Control-Allow-Origin: *
HTTP/1.1 101 Switching Protocols / Upgrade: websocket / Connection: Upgrade
Sec-WebSocket-Accept: <GUID 258EAFA5-E914-47DA-95CA-C5AB0DC85B11 computation>
Sec-WebSocket-Protocol: plugin.v3.internetdownloadmanager.com
HTTP/1.0 %u IDM / X-IDM-Status: %llu
```

So the DLL implements a real HTTP+WebSocket server: standard RFC 6455
handshake, **mandatory `Origin` check** (community-tested: only
`Origin: chrome-extension://ngpampappnmepgilojfohadhhmbhlaek` is accepted —
other pages cannot connect), `/client/` = socket endpoint, `/status/` =
plain-HTTP endpoint.

> Note: nothing was listening on port 1001 at probe time while `IDMan.exe`
> was running — the endpoint appears to start on demand (capture/panel
> session). The extension's aggressive retry loop + native-messaging fallback
> bridge exactly this gap.

## 2. Channel 2 — Native Messaging (fallback / bootstrap)

Used when both WebSocket endpoints fail (`a == La.length` branch, line 37)
[observed]:

```js
browser.runtime.connectNative("com.tonec.idm")
```

### Registration (Windows registry) [observed on this machine]

```text
HKLM\SOFTWARE\Google\Chrome\NativeMessagingHosts\com.tonec.idm
  (default) = C:\Program Files (x86)\Internet Download Manager\IDMMsgHost.json
HKCU\...\com.tonec.idm  → same file
```

### Host manifest `IDMMsgHost.json` [observed]

```json
{
    "name": "com.tonec.idm",
    "description": "Internet Download Manager extension helper",
    "type": "stdio",
    "allowed_origins": [
        "chrome-extension://ngpampappnmepgilojfohadhhmbhlaek/",
        "chrome-extension://llbjbkhnmlidjebalopleeepgdfgcpec/"
    ],
    "path": "IDMMsgHost.exe"
}
```

Standard Chrome native-messaging `stdio` protocol applies (Chrome docs):
Chrome spawns `IDMMsgHost.exe`, passes the caller origin as argv[1], and
exchanges **JSON, UTF-8, each message prefixed by a 32-bit length** over
stdin/stdout (≤64 MiB per message into the host, ≤1 MiB back).

### `IDMMsgHost.exe` (39 KB, 2021) [observed]

Contains no WebSocket/HTTP strings — it is a thin shim. Its job per the
protocol: keep a persistent `connectNative` port open (messages framed by the
same `qb` builder, sent as joined strings instead of `Blob`), wake/forward
into the running `IDMan.exe`, and thereby bootstrap the WebSocket data plane.
`IDMan.exe` itself contains the strings `NativeMessagingHosts`,
`com.tonec.idm`, `IDMMsgHost` — i.e. the app (installer) owns writing the
registration — plus `Uninstall.exe` (cleanup on uninstall).

Community equivalence ([reported]): the same field-tagged task object sent
through `connectNative("com.tonec.idm")` produces the same result as the
WebSocket path.

## 3. Channel 3 — `/status/` interception redirects [reported]

Public report (superuser.com): clicking a file can land the browser on
`http://127.0.0.1:1001/status/204+IDM`. I.e. IDM's integration (extension +
`IDMNetMon` traffic monitor) redirects intercepted browser downloads to the
local `/status/` endpoint, which answers locally and hands the real transfer
to the app — the classic IDM "take over this download" path for plain files,
distinct from the video-sniffing path.

## 4. Security properties (why only IDM can use it)

- `Origin` header pinned to IDM's own extension ids (server rejects others).
- Native-messaging `allowed_origins` pinned to the same ids (enforced by Chrome).
- Loopback-only server (`127.0.0.1`), custom subprotocol handshake.
- Consequence for us: we cannot (and should not) piggyback IDM's channel;
  our `ExtensionBridgeServer` (HTTP `127.0.0.1:45732`) is the correct
  equivalent — but note IDM's session-forwarding discipline (verbatim request
  headers + guaranteed cookies) as the bar to match.

## 5. Evidence index

| Claim | Source |
|---|---|
| WS URL, `cid`/`rnd`, subprotocol, 1.5 s rotation, backoff | `background.js:37,39` |
| Hello payload, client id in storage, locale | `background.js:38`, `:114` |
| `;`-framing, Blob-over-WS, 15 s heartbeat | `background.js:40–41` |
| Field tags 4/6/7/8/50/51/54/100/110 | `background.js:99–100` + nilaoda Blog #59 |
| Server in IDMNetMon*.dll, Origin check, /client/, /status/ | binary strings, this machine |
| Registry + `IDMMsgHost.json` + shim role | registry + files, this machine |
| Registration owned by app installer | `IDMan.exe`/`Uninstall.exe` strings |
| `Origin: chrome-extension://ngpamp…` mandatory | nilaoda Blog (tested) |
| `/status/204+IDM` redirect interception | superuser.com report |
| stdio JSON+u32-length native protocol | Chrome developer docs |

## 6. Relevance to the gated-YouTube problem

IDM's HLS-master success on age-restricted videos decomposes into: (a) body
sniffing finds `hls_playlist` without the browser requesting it, (b) the full
live session (headers + cookies) travels with the URL over this channel,
(c) the app downloads segments + muxes with zero page re-resolution. Our
pipeline mirrors all three; the remaining work is purely capture-side
(fresh HLS master in-tab), never transport-side.

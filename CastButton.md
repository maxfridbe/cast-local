# CastButton.md — Integrating Seekable Chromecast Casting from `cast-local`

This document explains how casting **with working seek** is implemented in this project
(`Program.cs`), so an LLM (or human) can lift the code into another application as a
"Cast" button. The target feature set:

1. **Identify cast targets** (discover Chromecast/Google TV devices on the LAN)
2. **Cast to** a chosen target (serve any local video file, seekable)
3. **Control** the playback from the app that hosts the button (play/pause/seek/stop/volume/status)

Everything referenced below lives in a single file: `Program.cs`. The only external
dependency is the [`GoogleCast` NuGet package v1.7.0](https://www.nuget.org/packages/GoogleCast)
(CASTV2 protocol client) plus `ffmpeg`/`ffprobe` binaries on PATH.

---

## The core insight: why seeking works (and what does NOT work)

A Chromecast is just an HTTP client. To cast a local file you run a small HTTP server on
the sender machine and hand the TV a URL. The hard part is **seeking**, because most
local files (MKV, AC3 audio) aren't directly playable and must be transcoded on the fly —
and a half-transcoded file can't satisfy byte-range seeks.

**Approaches that were tried in this repo and failed — do not reinvent them:**

| Approach | Why it fails |
|---|---|
| Serve the growing transcode output as one MP4 with HTTP 206 Range requests | The TV binary-searches byte offsets near the end of the file to parse headers/index; those bytes don't exist yet, requests hang, playback times out. Also time↔byte mapping is unknowable before the transcode finishes. |
| `StreamType.Live` + intercept seeks by polling `currentTime` and reloading with a `?seek=` URL | The TV **resets a live stream to position 0 on any seek attempt** and never reports where the user wanted to go. Forward seek intent is unrecoverable. Backward-only, glitchy. |

**The approach that works: HLS event playlist.**

`ffmpeg` transcodes the source into a *growing* HLS playlist: an `index.m3u8` plus
4-second `segNNNNN.ts` segments. `#EXT-X-PLAYLIST-TYPE:EVENT` means "segments are only
ever appended". The Chromecast **default media receiver plays HLS natively**: it re-polls
the playlist, sees the timeline grow, and handles seeking itself by simply requesting the
segment that contains the target time. No interception, no byte-range math, no hacks.

- Seekable window = whatever has been transcoded so far. With `-c:v copy` (H.264 sources)
  transcoding runs 15–30× realtime, so a 2-hour movie is fully seekable within ~5–8 minutes.
- When ffmpeg finishes it appends `#EXT-X-ENDLIST`, the playlist becomes VOD, and the TV
  shows the true final duration.
- HLS is naturally paced (the TV prefetches only a few segments ahead), which also solves
  Wi-Fi saturation without any bandwidth throttling.

```
Video file ──ffmpeg──▶ /tmp/cast_hls_<guid>/index.m3u8 + seg00000.ts, seg00001.ts, ...
                            │
                   embedded HTTP server (HttpListener)
                            │  GET /media.m3u8, GET /segNNNNN.ts   (CORS: *)
                            ▼
                   Chromecast default receiver  ◀── CASTV2 control channel (port 8009)
                                                     LOAD / PLAY / PAUSE / SEEK / STATUS
```

There are **two independent channels**; keep them separate in your integration:
- **Media plane**: your HTTP server → TV pulls playlist/segments.
- **Control plane**: a persistent TLS socket to the TV on port 8009 (the `GoogleCast`
  library's `Sender`), used to load the URL and to send transport commands.

---

## Part 1 — Identify cast targets

Reference: `Program.cs` lines ~265–340 (discovery block in `Main`), plus helpers
`CheckPortAsync` (~line 660) and `GetEurekaDeviceNameAsync` (~line 680).

mDNS discovery alone is unreliable (AP isolation, VPNs, containers), so this project runs
**two strategies in parallel** and merges the results:

1. **Multi-interface mDNS scan** — enumerate all up, multicast-capable, non-loopback
   NICs and run `new DeviceLocator().FindReceiversAsync(networkInterface)` on each in
   parallel. Each returns `IReceiver` objects (name + `IPEndPoint`).
2. **Direct TCP probe** of known/likely TV IPs — try a TCP connect to port `8009` with a
   ~1.5 s timeout (`CheckPortAsync`). If open, it's a cast device; fetch its friendly
   name from the undocumented-but-stable REST endpoint
   `http://<ip>:8008/setup/eureka_info` (JSON field `"name"`, see
   `GetEurekaDeviceNameAsync`), then synthesize a `Receiver { IPEndPoint = <ip>:8009 }`.
   This works even when mDNS is completely blocked.

Finally **de-duplicate by IP address** (the same TV often shows up from both paths).

For a Cast button UI: run both strategies with a ~2–3 s budget, show the merged list in a
picker. A returned target is fully described by `(friendlyName, ip)` — that's all you
need to persist for a "recent devices" list.

---

## Part 2 — Cast to a target

### 2a. Figure out which local IP the TV can reach (`GetLocalIpAddress`, ~line 700)

Don't guess from the NIC list — VPN/virtual adapters make that wrong. Instead bind a UDP
socket and `Connect(targetIp, 8009)` (no packets are sent); the OS routing table picks
the correct local address, read it from `socket.LocalEndPoint`. Fall back to the same
trick against `8.8.8.8`, then to a DNS/hostname scan. This local IP is what you embed in
the media URL you give the TV.

### 2b. Start the media server (`StartHttpServer` ~line 741, `ServeHlsAsync` ~line 1114)

`HttpListener` on the first free port starting at 8080 (prefix `http://*:{port}/`, with a
fallback to binding the specific local IP if the wildcard needs admin rights).

Serving rules (all implemented in `ServeHlsAsync`):
- **Every response must carry `Access-Control-Allow-Origin: *`** — the receiver fetches
  HLS via XHR and silently dies without CORS. Also answer `OPTIONS` preflights (204 with
  `Allow-Methods: GET, HEAD, OPTIONS`, `Allow-Headers: Content-Type, Range`).
- Any request path ending in `.m3u8` → serve `<hlsDir>/index.m3u8` with content type
  `application/vnd.apple.mpegurl` and `Cache-Control: no-cache, no-store` (the playlist
  grows; the TV must always re-fetch it).
- `GET /segNNNNN.ts` → serve the segment file verbatim, content type `video/mp2t`, with
  `Content-Length`. Segments referenced by the playlist are always complete files
  (ffmpeg updates the playlist only after finishing a segment), so plain full-file
  serving is sufficient — no range logic needed.
- Sanitize the path with `Path.GetFileName(...)` before touching the filesystem.

### 2c. Start the transcode (`Main`, ~lines 108–160)

```bash
ffmpeg -i "<source>" -map 0:v:0 -map 0:a:0 -sn \
       -c:v copy -c:a aac -ac 2 \
       -f hls -hls_time 4 -hls_playlist_type event -hls_flags independent_segments \
       -hls_segment_filename "<hlsDir>/seg%05d.ts" -y "<hlsDir>/index.m3u8"
```

Why each flag matters (learned the hard way):
- `-map 0:v:0 -map 0:a:0 -sn` — pin exactly one video + one audio stream and **exclude
  subtitles**; otherwise a subrip track can break or pollute the mux.
- `-c:v copy` — H.264 sources (the common case) are stream-copied: near-zero CPU, 15–30×
  realtime. If the source video isn't Chromecast-compatible (e.g. HEVC on older devices,
  check with `ffprobe` first), substitute `-c:v libx264 -preset veryfast` and expect
  ~1–4× realtime instead.
- `-c:a aac -ac 2` — Chromecast's default receiver cannot decode AC3/EAC3 5.1 from
  arbitrary containers; downmix to stereo AAC or you get frozen video/no audio.
- `-hls_time 4` — 4 s segments: good seek granularity vs. request overhead.
- `-hls_playlist_type event` — append-only playlist ⇒ growing seekable timeline.

**Pre-buffer gate:** wait until `index.m3u8` exists and contains ≥ 3 `#EXTINF` entries
(or ffmpeg exited) before sending the LOAD — the receiver errors out on an empty
playlist. This takes ~1–2 s for stream-copy.

Also grab the true duration up front with `ffprobe` (`GetVideoDurationAsync`, ~line 1201)
— useful for your UI scrubber before the playlist reaches full length:
`ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 "<file>"`.

### 2d. Connect and load (`Main`, ~lines 440–495)

```csharp
var sender = new GoogleCast.Sender();
await sender.ConnectAsync(new Receiver {
    Id = Guid.NewGuid().ToString(),
    IPEndPoint = new IPEndPoint(IPAddress.Parse(targetIp), 8009)
});
var mediaChannel = sender.GetChannel<IMediaChannel>();
await sender.LaunchAsync(mediaChannel);          // launches the default media receiver app

string url = $"http://{localIp}:{port}/media.m3u8?t={DateTime.UtcNow.Ticks}";
await mediaChannel.LoadAsync(new MediaInformation {
    ContentId = url,
    ContentType = "application/x-mpegURL",       // tells the receiver it's HLS
    StreamType = StreamType.Buffered             // Buffered, NOT Live — Live kills seeking
});
```

Details that matter:
- **Cache-bust the URL** (`?t=<ticks>`): the receiver aggressively caches by ContentId;
  recasting the same URL can silently replay the old session.
- `LoadAsync` can throw `TimeoutException` while the TV is *still successfully loading* —
  catch it and continue; verify via status polling instead of failing the cast.
- Keep the `sender`, HTTP server, and ffmpeg process alive for the whole session — the
  sender process IS the media server.

---

## Part 3 — Control from the Cast button UI

All transport control goes through the already-connected `IMediaChannel`. Verified API
surface of GoogleCast 1.7.0:

```csharp
await mediaChannel.PlayAsync();          // resume
await mediaChannel.PauseAsync();         // pause
await mediaChannel.SeekAsync(seconds);   // absolute position, in seconds — the TV
                                         // fetches the right HLS segment natively
await mediaChannel.StopAsync();          // stop playback / end session
var status = await mediaChannel.GetStatusAsync();   // poll ~1/sec

var receiverChannel = sender.GetChannel<IReceiverChannel>();
await receiverChannel.SetVolumeAsync(0.5f);     // 0.0 – 1.0
await receiverChannel.SetIsMutedAsync(true);
```

`MediaStatus` fields to drive the UI (see the poller loop in `Main`, ~lines 513–580):
- `status.PlayerState` — `"PLAYING"`, `"PAUSED"`, `"BUFFERING"`, `"IDLE"`.
- `status.CurrentTime` — seconds; feed your scrubber.
- `status.IdleReason` — when `IDLE`: `"FINISHED"` (ended normally — auto-close the
  session) or `"ERROR"`.
- Scrubber max = duration from `ffprobe` (Part 2c). Note: until transcoding finishes, a
  seek beyond the transcoded edge clamps to the live edge — either disable that region
  in the UI (compare against playlist length) or accept the clamp.

Polling notes:
- Poll `GetStatusAsync()` about once per second. Wrap in try/catch and **keep the HTTP
  server running on transient control-channel errors** — the TV drops/re-opens the
  control socket occasionally while media playback continues fine.
- Because seeking is native in HLS mode, `CurrentTime` jumps are *normal* (they're the
  user seeking); do not treat them as errors or try to "correct" them. (The legacy
  `--live` code path in `Program.cs` does reload-on-jump — that logic is intentionally
  disabled when HLS is active; do not port it.)

**Teardown** (Cast button "disconnect", see `finally` block in `Main`, ~lines 630–660):
kill the ffmpeg process, delete the HLS temp directory recursively, `listener.Stop()`,
`sender.Disconnect()`. Also auto-tear-down when the poller sees `IDLE/FINISHED`.

---

## Suggested integration shape

Wrap the above into a session object; the button's states map directly onto it:

```csharp
// Button click → discovery picker
IReadOnlyList<CastTarget> targets = await CastDiscovery.FindAsync(timeout, knownIps);

// Target picked → start session
var session = await CastSession.StartAsync(target, videoFilePath);
//   internally: local-IP resolution → HttpListener → ffmpeg HLS → prebuffer gate
//   → ConnectAsync → LaunchAsync → LoadAsync

// While connected → controller UI
session.Play(); session.Pause(); session.Seek(seconds); session.SetVolume(v);
session.StatusChanged += (s) => UpdateScrubber(s.CurrentTime, s.PlayerState);

// Disconnect / finished
await session.DisposeAsync();   // kill ffmpeg, delete temp dir, stop server, disconnect
```

## Checklist of gotchas (all discovered by breaking things)

- [ ] CORS `*` on **every** media-plane response, including OPTIONS — HLS dies silently without it.
- [ ] `application/x-mpegURL` + `StreamType.Buffered` on LOAD; playlist served as `application/vnd.apple.mpegurl`.
- [ ] AC3/EAC3 → stereo AAC always; never pass through multichannel audio.
- [ ] `-sn` — never let subtitle tracks into the mux.
- [ ] Cache-bust every ContentId URL.
- [ ] Wait for ≥ 3 HLS segments before LOAD.
- [ ] Treat `LoadAsync` timeout as non-fatal; verify via status poll.
- [ ] `Cache-Control: no-cache` on the playlist only, never on segments.
- [ ] Survive control-channel drops without killing the media server.
- [ ] Firewall: the sender machine must accept inbound TCP on the media port (8080+) and reach the TV on 8009/8008.
- [ ] Clean up `/tmp/cast_hls_*` and the ffmpeg child on every exit path.

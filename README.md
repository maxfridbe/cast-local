# 📺 Smart TV Cast Utility

A high-performance C# (.NET 10) console utility designed to cast static images and progressive videos (like MP4/WebM) to Google Cast-enabled devices (Chromecast, Google TV, Android TV) on your local wireless network. It can run interactively, take direct IP targets, or accept media piped from standard input (stdin) in a pipeline.

---

## 💡 Why This Tool Was Built

Standard Google Cast applications depend heavily on **multicast DNS (mDNS)** discovery. However, mDNS traffic is frequently dropped by wireless routers (due to AP isolation or IGMP snooping) or blocked when running applications inside containers, VMs, or under VPNs like Tailscale. 

To solve this, this utility combines multiple strategies:
1. **Parallel Multi-Interface mDNS Scan**: Searches all active multicast-capable network adapters.
2. **Direct TCP/REST Probing**: Probes targeted TV IPs on Google Cast ports (`8009` and `8008`) to resolve devices directly, bypassing mDNS entirely.
3. **Smart Local Route Resolution**: Uses UDP socket binding towards the TV IP to query the OS routing table and determine which local interface is actually routable to the TV, avoiding routing failures caused by VPNs/virtual adapters.
4. **HLS Event-Playlist Transcoding**: For local video files, it transcodes in the background into a growing HLS playlist (video stream-copied when possible, audio converted to stereo AAC). The Chromecast default receiver plays HLS natively, so timeline scrubbing/seeking works natively within the transcoded window, which expands to the full movie in minutes.
5. **Caching Read-Ahead Proxy Stream**: For piped streams, it replays pre-buffered bytes from memory to satisfy the Chromecast's multi-request connection sequence.
6. **Cache Busting**: Generates unique timestamped URLs for every cast request to bypass Chromecast's aggressive receiver cache.

---

## 🛠️ System Architecture

```mermaid
graph TD
    Input[MKV/MP4 Video File] -->|1. Start Transcode| Ffmpeg[FFmpeg Worker Process]
    Ffmpeg -->|2. Write Segments| HlsDir[HLS dir /tmp/cast_hls_*/index.m3u8 + seg*.ts]
    HlsDir -->|3. Read Playlist/Segments| Server[Embedded HTTP Server]
    Server -->|4. Serve HLS over HTTP| TV[Smart TV / Chromecast]
    TV -->|5. Native HLS Seek| Server
    Server -->|6. Serve Requested Segment| HlsDir
```

---

## 🚀 How to Run

### Prerequisite
* .NET 10 SDK (pre-configured on this system).
* `ffmpeg` and `ffprobe` (for on-the-fly transcoding and metadata detection).

### 1. Dynamic Seekable Local File Mode (HLS, default)
Pass a local video file (MKV, MP4, AVI, WebM) directly to the command. The tool spawns `ffmpeg` in the background to transcode the video into a growing **HLS event playlist**. The Chromecast default receiver plays HLS natively, giving **real native seeking** with your TV remote, phone, or Google Home app — no seek interception needed. Right after startup you can seek within whatever has been transcoded so far; the window grows to the full movie within minutes (stream-copy typically runs 15-30x realtime):

```bash
# HLS Mode (default): native timeline + native seeking
./bin/Release/net10.0/linux-x64/publish/cast-local "/var/home/maxfridbe/Videos/MaxFlix/Tri.kota.Zimnie.kanikuly.1080p-EniaHD.mkv"

# Legacy Live Transcoding Mode (VLC-style infinite live source with seek interception)
./bin/Release/net10.0/linux-x64/publish/cast-local --live "/var/home/maxfridbe/Videos/MaxFlix/Tri.kota.Zimnie.kanikuly.1080p-EniaHD.mkv"
```

Options for local file casting:
* `--live`: Legacy fallback. Forces the TV to treat the video as an infinite progressive live stream served from a single growing fMP4 file, with remote seeks intercepted by the status poller (which restarts the backend transcoder from the seek point). Note: forward seeks are unreliable in this mode because the TV resets a live stream to zero on seek — prefer the default HLS mode.
* `--size <bytes>`: Specify the estimated total size of the video stream in bytes (e.g., `--size 282000000` for 282MB) so that the TV can make standard range requests without chunked-encoding limitations.

### 2. Piped Progressive Live Mode
Pipe any PNG, JPG/JPEG, GIF, MP4, or WebM media file directly into the command. The tool will auto-detect the file type based on its magic bytes, start the HTTP server, and automatically cast to the TV (defaulting to `--cc 1`):

```bash
# Pipe a static image
cat nature_wallpaper.jpg | ./bin/Release/net10.0/linux-x64/publish/cast-local

# Transcode a movie on-the-fly and stream it progressively
ffmpeg -i "Tri.kota.Zimnie.kanikuly.1080p-EniaHD.mkv" -c:v copy -c:a aac -movflags frag_keyframe+empty_moov -f mp4 pipe:1 | ./bin/Release/net10.0/linux-x64/publish/cast-local --live --size 282000000
```

Options for live video streaming:
* `--live`: Force the utility to treat the input as a progressive live stream.
* `--size <bytes>`: Specify the estimated total size of the video stream in bytes (e.g., `--size 282000000` for 282MB) so that the TV can make standard range requests without chunked-encoding limitations.

### 3. Interactive Mode (Auto-Scan)
Scans network adapters and probes the Living Room TV (`192.168.50.109`) in parallel. If multiple displays are resolved, it displays a numbered menu:
```bash
./bin/Release/net10.0/linux-x64/publish/cast-local
```

### 4. Selector Mode (`--cc`)
Select a specific discovered device by its index when multiple options are present:
```bash
./bin/Release/net10.0/linux-x64/publish/cast-local --cc 1
```

### 5. Direct IP Mode
Manually specify the TV's IP address to bypass discovery completely:
```bash
./bin/Release/net10.0/linux-x64/publish/cast-local 192.168.50.109
```

---

## 📦 Compiling to Standalone Single Binary (No dotnet deps)

The project is pre-configured to build a **self-contained, single-file native executable** for Linux x64 that has zero external `.NET` dependencies. Trimming is disabled (`PublishTrimmed=false`) to prevent reflection errors inside the `GoogleCast` SDK.

To compile:
```bash
dotnet publish -c Release
```

The output binary will be generated at:
[cast-local](file:///var/home/maxfridbe/Dev/vibecoding/cast-local/bin/Release/net10.0/linux-x64/publish/cast-local) (approx. 75 MB).

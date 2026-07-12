# 📺 Smart TV Cast Utility

A high-performance C# (.NET 10) console utility designed to cast static images, progressive videos (like MP4/WebM), and audio files (like MP3/AAC/FLAC/WAV) to Google Cast-enabled devices (Chromecast, Google TV, Android TV) on your local wireless network. It can run interactively, take direct IP targets, or accept a local file path.

---

## 💡 Why This Tool Was Built

Standard Google Cast applications depend heavily on **multicast DNS (mDNS)** discovery. However, mDNS traffic is frequently dropped by wireless routers (due to AP isolation or IGMP snooping) or blocked when running applications inside containers, VMs, or under VPNs like Tailscale. 

To solve this, this utility combines multiple strategies:
1. **Parallel Multi-Interface mDNS Scan**: Searches all active multicast-capable network adapters.
2. **Direct TCP/REST Probing**: Probes targeted TV IPs on Google Cast ports (`8009` and `8008`) to resolve devices directly, bypassing mDNS entirely.
3. **Smart Local Route Resolution**: Uses UDP socket binding towards the TV IP to query the OS routing table and determine which local interface is actually routable to the TV, avoiding routing failures caused by VPNs/virtual adapters.
4. **HLS Event-Playlist Transcoding**: For local video files, it transcodes in the background into a growing HLS playlist (video stream-copied when possible, audio converted to stereo AAC). The Chromecast default receiver plays HLS natively, so timeline scrubbing/seeking works natively within the transcoded window, which expands to the full movie in minutes.
5. **Direct Native Audio Streaming**: For native audio files (`.mp3`, `.aac`, `.wav`, `.flac`, `.ogg`, `.m4a`), it bypasses transcoding entirely and streams them raw over HTTP, enabling native seek/scrubbing via HTTP range requests. Other audio formats are transcoded on the fly to high-quality MP3.
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

### 2. Audio Casting Mode (Native / MP3 fallback)
Pass a local audio file (MP3, AAC, FLAC, WAV, M4A, etc.) directly. Native audio formats are streamed directly with zero transcoding overhead. Other formats are transcoded to high-quality MP3 on the fly:

```bash
# Play an MP3 directly
./bin/Release/net10.0/linux-x64/publish/cast-local "/var/home/maxfridbe/Music/favorite_song.mp3" --cc 1
```

### 3. Network Scan Mode (`--scan`)
Perform a discovery scan on network interfaces and probe the Living Room TV directly, printing out all discovered Cast-enabled devices and exiting:
```bash
./bin/Release/net10.0/linux-x64/publish/cast-local --scan
```

### 4. Interactive Mode (Auto-Scan)
Scans network adapters and probes the Living Room TV (`192.168.50.109`) in parallel. If multiple displays are resolved, it displays a numbered menu:
```bash
./bin/Release/net10.0/linux-x64/publish/cast-local
```

### 5. Selector Mode (`--cc`)
Select a specific discovered device by its index when multiple options are present:
```bash
./bin/Release/net10.0/linux-x64/publish/cast-local --cc 1
```

### 6. Direct IP Mode
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

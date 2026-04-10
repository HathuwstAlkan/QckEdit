# QckEdit

A fast right-click Windows context menu tool to change video speed and compress using HEVC/H.265 or FFV1.

## Download

Get the latest executable from the [Releases](https://github.com/HathuwstAlkan/QckEdit/releases) section.

## Features
- **Context Menu Integration**: Process videos directly from Windows Explorer via simple right-click operations.
- **Speed Adjustment**: Quickly alter video and audio playback speed (0.25x up to 8.0x). Automatically corrects audio pitch to match playback.
- **Video Compression**: Re-encode natively using HEVC/H.265 (CRF 18 through 28) or FFV1 (lossless).
- **Combined Presets**: Change speed and compress the file in one click. 
- **Extensive Format Support**: Works with exactly these formats: `.mp4`, `.mov`, `.mkv`, `.avi`, `.wmv`, `.m4v`, `.webm`, `.flv`, `.ts`, `.mts`, `.m2ts`, `.3gp`, `.obs`, `.rec`, `.hevc`, `.h265`, `.h264`, `.f4v`, `.ogv`, `.vob`, `.asf`, `.divx`, `.rmvb`, and `.capture`.

## Installation
Run `QckEdit.exe` as Administrator. The installer will:
1. Automatically download `ffmpeg` and `ffprobe` (if missing).
2. Register the necessary Windows shell context menu extensions.

*(To uninstall, use `QckEdit.exe --uninstall`)*

## Build from Source

Run `build.bat` inside the root folder to produce a single-file executable.
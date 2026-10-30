# Screen Guide

**Ask where a feature is, and see it highlighted on the real interface.**

Screen Guide reads the current interface and points to a feature. You click; the assistant guides the next step.

## Features

- **Real-interface guidance:** Uses controls and screenshots, including step-by-step menu guidance.
- **Click-through highlights:** Yellow marks verified controls; white marks image estimates.
- **Compact dock:** Click to expand, click again for settings, and move away to dock.
- **Text and voice:** Supports local English transcription and Codex / Claude Code integration.

## Get started

Download the Windows x64 demo from the repository's Releases page and follow the included instructions. The demo requires the .NET 10 Windows Desktop Runtime and a configured model connection or compatible agent.

To run from source, install the .NET 10 SDK and run:

```powershell
.\start.ps1
```

Open **Connection settings** to configure your model endpoint and key. Screenshot modes require an image-capable model. When used through Codex or Claude Code, the current agent conversation chooses the next entry; the local app reads the interface and displays the highlight. See the [plugin guide](codex-plugin/README.md).

## Notes

- The assistant does not click, type, or run commands for you. Press **Esc** to clear the highlight.
- Screenshot modes send the visible target window to the model; choose **Controls only** to avoid sending screenshots.
- This is a prototype. Complex interfaces and image-based locations can be unreliable. Windows is the primary platform; Linux support is experimental.
- The project is licensed under [MIT](LICENSE). Dependencies and optional speech models have their own licenses.

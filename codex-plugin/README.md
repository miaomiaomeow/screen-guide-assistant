# Screen Guide plugin — English edition 0.3.1

This package connects Codex or Claude Code to the local Windows Screen Guide. The current conversation model chooses each entry; the desktop app reads the live interface, verifies bounds and displays click-through highlights. The plugin never selects a fixed model or calls the model configured in the desktop app.

Start the English desktop app using `../start.ps1` before using the plugin. All desktop editions share one local connection; exit other editions first. The package's `mcp.json` starts `server/publish/ScreenGuide.Plugin.dll` using the installed .NET 10 runtime. Plugin name and descriptions are English. No credentials are required by the plugin itself.

When using a source checkout, first install the .NET 10 SDK and run `./codex-plugin/build.ps1 -SelfTest` from the repository root. The downloadable demo already includes the compiled plugin.

Eight tools provide status, target listing, binding, guidance sessions, current-view reads, validated pointing, bounded waits for the user's actions, and clearing. Hybrid mode provides real controls plus a native MCP image block. The user clicks or hovers; no tool clicks, types or activates another app. Agent voice input is handled by the agent client.

For Claude Code, `../claude-code/start.ps1` generates a session-only MCP configuration targeting the compiled server here. No account or model settings are changed.

Validation: 98 protocol checks. This does not verify a real desktop guidance session. Building this package does not update an already-installed Codex plugin; the installed package must be refreshed separately. Existing 0.3.0 remains protocol-compatible with the English desktop app.

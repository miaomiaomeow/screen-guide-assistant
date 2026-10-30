"""Build the Windows demo from an explicit allowlist; never bundle local configuration."""
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED
import hashlib

ROOT = Path(__file__).resolve().parent
APP = ROOT / 'app-english'
PLUGIN = ROOT / 'codex-plugin'
OUT = ROOT / 'release' / 'ScreenGuide-Windows-x64.zip'
RUNTIME_FILES = [
    'ScreenGuide.exe', 'ScreenGuide.dll', 'ScreenGuide.deps.json', 'ScreenGuide.runtimeconfig.json',
    'NAudio.Core.dll', 'NAudio.WinMM.dll', 'onnxruntime.dll', 'sherpa-onnx-c-api.dll', 'sherpa-onnx.dll',
]
PLUGIN_FILES = ['ScreenGuide.Plugin.exe', 'ScreenGuide.Plugin.dll', 'ScreenGuide.Plugin.deps.json', 'ScreenGuide.Plugin.runtimeconfig.json']
README = '''Screen Guide — Windows x64 demo

1. Install .NET 10 Windows Desktop Runtime (x64): https://dotnet.microsoft.com/download/dotnet/10.0
2. Extract the whole archive and run app/ScreenGuide.exe.
3. Open the control panel, choose Connection settings, select New, and enter your own API endpoint, model ID and API key. Screenshot modes require an image-capable model.
4. Ask where a feature is. The assistant highlights; you click.

English speech is optional. In PowerShell from this extracted directory, run:
    .\\prepare-speech.ps1 -Engine sensevoice
This explicitly downloads model files into app/models/sensevoice. Model weights are not included. Check the downloaded license and upstream terms.

To use the optional Codex / Claude Code integration, see codex-plugin/README.md. Build-independent plugin binaries are included. Agent sessions use the current conversation model; the standalone app uses its configured model.

Only one Screen Guide edition can use the local connection at a time. Screenshots may contain visible document content and are sent to your selected model in image modes. Choose Controls only if you do not want to send screenshots.

This is a prototype. Some custom interfaces or obscured windows may not be reliably located. Use Esc to clear highlights. See LICENSE and third-party-licenses/ for notices.
'''

def main():
    files = [(APP / name, 'app/' + name) for name in RUNTIME_FILES]
    files += [(PLUGIN / 'server' / 'publish' / name, 'codex-plugin/server/publish/' + name) for name in PLUGIN_FILES]
    files += [(PLUGIN / name, 'codex-plugin/' + name) for name in ('plugin.json', 'mcp.json', 'tools.json', 'README.md')]
    files += [(ROOT / 'claude-code' / name, 'claude-code/' + name) for name in ('start.ps1', 'reserved-options.json', 'GUIDE_PROMPT.md', 'README.md')]
    files += [(ROOT / name, name) for name in ('LICENSE', 'prepare-speech.ps1')]
    files += [(p, 'third-party-licenses/' + p.name) for p in sorted((ROOT / 'third-party-licenses').glob('*.txt'))]
    if len(files) != len({name for _, name in files}): raise ValueError('Duplicate archive paths')
    for source, name in files:
        if not source.is_file() or source.is_symlink(): raise FileNotFoundError(source)
        if '.local.' in name or 'models/' in name or 'logs/' in name: raise ValueError('Private or large file in allowlist: ' + name)
    OUT.parent.mkdir(exist_ok=True)
    with ZipFile(OUT, 'w', ZIP_DEFLATED, compresslevel=9) as archive:
        archive.writestr('README.txt', README)
        archive.writestr('start.ps1', "$exe = Join-Path $PSScriptRoot 'app\\ScreenGuide.exe'\nStart-Process -FilePath $exe -WorkingDirectory $PSScriptRoot\n")
        for source, name in files: archive.write(source, name)
    with ZipFile(OUT) as archive:
        if archive.testzip() is not None or len(archive.namelist()) != len(files) + 2: raise ValueError('Archive verification failed')
    digest = hashlib.sha256(OUT.read_bytes()).hexdigest()
    OUT.with_suffix('.zip.sha256').write_text(digest + '  ' + OUT.name + '\n', encoding='ascii')
    print(f'{OUT} ({OUT.stat().st_size} bytes)\nSHA256 {digest}')

if __name__ == '__main__': main()

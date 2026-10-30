param(
    [ValidateSet('sensevoice', 'paraformer', 'zipformer', 'all')]
    [string]$Engine = 'all',
    [switch]$IncludeTestAudio
)

$ErrorActionPreference = 'Stop'
$demoRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\', '/')
$evidenceDirectory = Join-Path $demoRoot 'evidence'

function Assert-DemoPath([string]$Path) {
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($demoRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside this demo: $resolvedPath"
    }
    $checkedPath = $resolvedPath
    while ($checkedPath.Length -ge $demoRoot.Length) {
        if (Test-Path -LiteralPath $checkedPath) {
            $checkedItem = Get-Item -LiteralPath $checkedPath -Force
            if (($checkedItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing a linked destination: $checkedPath"
            }
        }
        if ($checkedPath -eq $demoRoot) { break }
        $checkedPath = [IO.Path]::GetDirectoryName($checkedPath)
    }
    return $resolvedPath
}

function Test-VerifiedFile([string]$Path, [long]$Length, [string]$Sha256) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    if ((Get-Item -LiteralPath $Path).Length -ne $Length) { return $false }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Sha256
}

function Get-VerifiedModelFile([string]$BaseUrl, [string]$RelativeUrl, [string]$Destination, [long]$Length, [string]$Sha256) {
    $destinationPath = Assert-DemoPath $Destination
    if (Test-VerifiedFile $destinationPath $Length $Sha256) {
        Write-Host "Verified existing $([IO.Path]::GetFileName($destinationPath))"
        return
    }
    $destinationParent = Assert-DemoPath ([IO.Path]::GetDirectoryName($destinationPath))
    [IO.Directory]::CreateDirectory($destinationParent) | Out-Null
    $temporaryPath = Assert-DemoPath ($destinationPath + '.download')
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
    # The revision and expected content hashes stay fixed. A unique query avoids
    # proxies reusing an expired Hugging Face signed redirect.
    $requestNonce = [DateTime]::UtcNow.Ticks
    $downloadUrl = $BaseUrl + $RelativeUrl + '?download=true&screen_guide_request=' + $requestNonce
    Write-Host "Downloading $RelativeUrl ($Length bytes)"
    $partPaths = @()
    $keepCompletedParts = $true
    try {
        if ($Length -gt 50000000) {
            # Four bounded HTTP ranges speed up the large model on slow links.
            # Each piece is size-checked; the merged file is SHA256-checked.
            $partSize = [long][Math]::Ceiling($Length / 4.0)
            $curlArguments = [Collections.Generic.List[string]]::new()
            $curlArguments.AddRange([string[]]@('--parallel', '--parallel-immediate', '--parallel-max', '4'))
            $partLengths = @()
            for ($partIndex = 0; $partIndex -lt 4; $partIndex++) {
                $partPath = Assert-DemoPath ($temporaryPath + '.part' + $partIndex)
                $partPaths += $partPath
                $partStart = $partIndex * $partSize
                $partEnd = [long][Math]::Min($Length - 1, $partStart + $partSize - 1)
                $partLengths += $partEnd - $partStart + 1
                if ((Test-Path -LiteralPath $partPath -PathType Leaf) -and (Get-Item -LiteralPath $partPath).Length -eq $partLengths[$partIndex]) {
                    Write-Host "Reusing completed range $partIndex (full file hash will be verified)"
                    continue
                }
                if (Test-Path -LiteralPath $partPath) { Remove-Item -LiteralPath $partPath -Force }
                if ($curlArguments.Count -gt 4) { $curlArguments.Add('--next') }
                $curlArguments.AddRange([string[]]@('--fail', '--location', '--retry', '3', '--retry-all-errors', '--connect-timeout', '20', '--speed-limit', '1024', '--speed-time', '60', '--max-time', '300', '--silent', '--show-error', '--range', "$partStart-$partEnd", '--output', $partPath, ($downloadUrl + '&part=' + $partIndex)))
            }
            if ($curlArguments.Count -gt 4) {
                & curl.exe @curlArguments
                if ($LASTEXITCODE -ne 0) { throw "Parallel download failed (curl exit $LASTEXITCODE)." }
            }
            $outputFile = [IO.File]::Create($temporaryPath)
            try {
                for ($partIndex = 0; $partIndex -lt $partPaths.Count; $partIndex++) {
                    if ((Get-Item -LiteralPath $partPaths[$partIndex]).Length -ne $partLengths[$partIndex]) {
                        throw "Range size verification failed for part $partIndex."
                    }
                    $partFile = [IO.File]::OpenRead($partPaths[$partIndex])
                    try { $partFile.CopyTo($outputFile) } finally { $partFile.Dispose() }
                }
            } finally { $outputFile.Dispose() }
        } else {
            & curl.exe --fail --location --retry 3 --retry-all-errors --connect-timeout 20 --max-time 180 --silent --show-error --output $temporaryPath $downloadUrl
            if ($LASTEXITCODE -ne 0) { throw "Download failed for $RelativeUrl (curl exit $LASTEXITCODE)." }
        }
        $keepCompletedParts = $false
        if (-not (Test-VerifiedFile $temporaryPath $Length $Sha256)) {
            throw "Size or SHA256 verification failed for $RelativeUrl."
        }
        Move-Item -LiteralPath $temporaryPath -Destination $destinationPath -Force
        Write-Host "Verified $RelativeUrl"
    } finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
        for ($partIndex = 0; $partIndex -lt $partPaths.Count; $partIndex++) {
            $partPath = $partPaths[$partIndex]
            if ((Test-Path -LiteralPath $partPath) -and (-not $keepCompletedParts -or (Get-Item -LiteralPath $partPath).Length -ne $partLengths[$partIndex])) {
                Remove-Item -LiteralPath $partPath -Force
            }
        }
    }
}

# Only this explicit setup script downloads model files. The application does
# not download a model, record audio, or contact a transcription service here.
# No argument prepares all three choices; -Engine sensevoice retains the old
# single-engine setup. Verified existing files are always reused.
$engineSpecs = @{
    sensevoice = @{
        Model = 'SenseVoiceSmall, sherpa-onnx int8 export, 2024-07-17'
        Repository = 'csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17'
        Revision = '2365baeacb507f821a0c8120fcee3d484dba7a07'
        Documentation = 'https://k2-fsa.github.io/sherpa/onnx/sense-voice/pretrained.html'
        Upstream = 'https://github.com/FunAudioLLM/SenseVoice'
        LicenseNotice = 'See the downloaded LICENSE and upstream model license; the runtime package license is separate.'
        Files = @(
            @{ Remote = 'model.int8.onnx'; Local = 'model.int8.onnx'; Length = 239233841; Sha256 = 'c71f0ce00bec95b07744e116345e33d8cbbe08cef896382cf907bf4b51a2cd51' },
            @{ Remote = 'tokens.txt'; Local = 'tokens.txt'; Length = 315894; Sha256 = 'f449eb28dc567533d7fa59be34e2abca8784f771850c78a47fb731a31429a1dc' },
            @{ Remote = 'LICENSE'; Local = 'LICENSE'; Length = 71; Sha256 = '221c6df10b0931a5629adad671ea48fb7747e034c414b6d2bfa275bc3dd4ea17' },
            @{ Remote = 'README.md'; Local = 'README.md'; Length = 104; Sha256 = '763991a00edaea534ab36bf1b7cf89e61e911666dcfabbba71f91f9f7c593a63' }
        )
    }
    paraformer = @{
        Model = 'Paraformer large Chinese + English, sherpa-onnx int8 export, 2023-09-14'
        Repository = 'csukuangfj/sherpa-onnx-paraformer-zh-2023-09-14'
        Revision = 'def027084691107096b5ebba69785756d63de6c5'
        Documentation = 'https://k2-fsa.github.io/sherpa/onnx/pretrained_models/offline-paraformer/paraformer-models.html'
        Upstream = 'https://www.modelscope.cn/models/iic/speech_paraformer-large-vad-punc_asr_nat-zh-cn-16k-common-vocab8404-onnx/summary'
        LicenseNotice = 'The downloaded upstream README declares Apache-2.0; retain upstream model notices separately from runtime licensing.'
        Files = @(
            @{ Remote = 'model.int8.onnx'; Local = 'model.int8.onnx'; Length = 243371218; Sha256 = 'f36a0433bcf096bd6d6f11b80a3ac8bed110bdca632fe0d731df8d1a84475945' },
            @{ Remote = 'tokens.txt'; Local = 'tokens.txt'; Length = 75756; Sha256 = '59aba8873a2ed1e122c25fee421e25f283b63290efbde85c1f01a853d83cb6e6' },
            @{ Remote = 'README.md'; Local = 'README.md'; Length = 191; Sha256 = '7fa50f584c7f944ce9bf97d53e69050d92dbae745091603fa212f0edd94c7acf' }
        )
    }
    zipformer = @{
        Model = 'Zipformer Chinese + English transducer, epoch 34 avg 19, 2023-11-22; int8 encoder/joiner and fp32 decoder'
        Repository = 'zrjin/icefall-asr-zipformer-multi-zh-en-2023-11-22'
        Revision = 'f8603c37a3f7601b1563e911be81c507f88b702c'
        Documentation = 'https://k2-fsa.github.io/sherpa/onnx/pretrained_models/offline-transducer/zipformer-transducer-models.html'
        Upstream = 'https://github.com/k2-fsa/icefall/pull/1265'
        LicenseNotice = 'Retain the downloaded model README and review upstream model terms separately from the sherpa-onnx runtime license.'
        Files = @(
            @{ Remote = 'exp/encoder-epoch-34-avg-19.int8.onnx'; Local = 'encoder.int8.onnx'; Length = 69037476; Sha256 = '28780a73b0fc5a05ec579ad4dbbc7d8fcd09fed5e6c431f84dcaab8f8170d9bf' },
            @{ Remote = 'exp/decoder-epoch-34-avg-19.onnx'; Local = 'decoder.onnx'; Length = 5165101; Sha256 = '110dc19143f0731ce0f64513e524d7d8f729e37107e1392da790f66d6c936a0b' },
            @{ Remote = 'exp/joiner-epoch-34-avg-19.int8.onnx'; Local = 'joiner.int8.onnx'; Length = 1033479; Sha256 = '498861ff0963ecbbff101a940ed533a715e5d68ee96431a3b092d0ca00c3ba38' },
            @{ Remote = 'data/lang_bbpe_2000/tokens.txt'; Local = 'tokens.txt'; Length = 25645; Sha256 = '2d5d6245197582cea396b23fa29d592705be31bac9a97bb74a8d1bca3ca6a0e0' },
            @{ Remote = 'data/lang_bbpe_2000/bbpe.model'; Local = 'bbpe.model'; Length = 270440; Sha256 = '82852098f3b36b61a34856a26ae5750f16d596feb682f24d0df40a958d36a05f' },
            @{ Remote = 'README.md'; Local = 'README.md'; Length = 48; Sha256 = 'a9dfd730a571c894cb84f9a570c47078fb3dba44584205fab34f5182e558421a' }
        )
    }
}

$selectedEngines = if ($Engine -eq 'all') { @('sensevoice', 'paraformer', 'zipformer') } else { @($Engine) }
foreach ($engineId in $selectedEngines) {
    $modelSpec = $engineSpecs[$engineId]
    $modelDirectory = Assert-DemoPath (Join-Path $demoRoot ('app\models\' + $engineId))
    $modelBaseUrl = 'https://huggingface.co/' + $modelSpec.Repository + '/resolve/' + $modelSpec.Revision + '/'
    foreach ($modelFile in $modelSpec.Files) {
        Get-VerifiedModelFile $modelBaseUrl $modelFile.Remote (Join-Path $modelDirectory $modelFile.Local) $modelFile.Length $modelFile.Sha256
    }
    $sourceRecord = [ordered]@{
        engine = $engineId
        model = $modelSpec.Model
        repository = 'https://huggingface.co/' + $modelSpec.Repository
        revision = $modelSpec.Revision
        documentation = $modelSpec.Documentation
        upstream = $modelSpec.Upstream
        licenseNotice = $modelSpec.LicenseNotice
        files = @($modelSpec.Files | ForEach-Object {
            [ordered]@{ file = $_.Local; source = $modelBaseUrl + $_.Remote; bytes = $_.Length; sha256 = $_.Sha256 }
        })
    }
    $sourcePath = Assert-DemoPath (Join-Path $modelDirectory 'SOURCE.json')
    $sourceRecord | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $sourcePath -Encoding UTF8
    Write-Host "$engineId model is ready: $modelDirectory"
}

if ($IncludeTestAudio) {
    $fixtureSpec = $engineSpecs.sensevoice
    $fixtureBaseUrl = 'https://huggingface.co/' + $fixtureSpec.Repository + '/resolve/' + $fixtureSpec.Revision + '/'
    Get-VerifiedModelFile $fixtureBaseUrl 'test_wavs/zh.wav' (Join-Path $evidenceDirectory 'sensevoice-zh.wav') 178988 'b77f1794fe374a0ba1ee1dc458bfaf9349496cbbfc32780c50ba3c5a7ad8e373'
}

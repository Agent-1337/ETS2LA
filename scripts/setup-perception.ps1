$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$assetRoot = Join-Path $projectRoot '.tools\perception'
New-Item -ItemType Directory -Path $assetRoot, (Join-Path $assetRoot 'tessdata') -Force | Out-Null

function Get-Sha256([string] $path) {
    $stream = [IO.File]::OpenRead($path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

function Get-Asset([string] $url, [string] $target, [string] $sha256 = '') {
    if (Test-Path -LiteralPath $target) {
        if (!$sha256 -or (Get-Sha256 $target) -eq $sha256) { return }
        throw "Existing perception asset failed checksum: $target"
    }
    $partial = $target + '.download'
    Write-Host "Downloading local perception asset: $([IO.Path]::GetFileName($target))"
    $savedProgress = $ProgressPreference
    try {
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $url -OutFile $partial -UseBasicParsing
        if ($sha256 -and (Get-Sha256 $partial) -ne $sha256) {
            throw "Downloaded perception asset failed checksum: $target"
        }
        Move-Item -LiteralPath $partial -Destination $target
    } finally { $ProgressPreference = $savedProgress }
}

$modelRevision = 'ff690b0a8050566c290287545bd059350f3e9096'
$modelBase = "https://huggingface.co/onnx-community/grounding-dino-tiny-ONNX/resolve/$modelRevision"
Get-Asset "$modelBase/onnx/model.onnx" (Join-Path $assetRoot 'grounding-dino.onnx') '267c8b00d269dc2dda0d8592812d3027abd5fb59f1cabf8e3d56f8fdf07f68ec'
Get-Asset "$modelBase/vocab.txt" (Join-Path $assetRoot 'vocab.txt') '07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3'
# Official Tesseract fast language data, versioned release; inference remains local.
Get-Asset 'https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/eng.traineddata' (Join-Path $assetRoot 'tessdata\eng.traineddata') '7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2'
Get-Asset 'https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/fra.traineddata' (Join-Path $assetRoot 'tessdata\fra.traineddata') 'ced037562e8c80c13122dece28dd477d399af80911a28791a66a63ac1e3445ca'
Write-Host 'Local perception models ready.'

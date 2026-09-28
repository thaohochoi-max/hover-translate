# Dựng lại thư mục python-embed\ (bundle Python cho installer) từ đầu.
# Kết quả: ..\python-embed\python.exe + toàn bộ site-packages cần thiết
# (~1GB, KHÔNG commit vào git - xem .gitignore).
#
# Chạy: powershell -File build-python-embed.ps1
#
# Lưu ý: pip cài stanza bản mới nhất theo mặc định, nhưng argostranslate 1.11.0
# cần đúng stanza==1.10.1 - script này cài lại đúng bản đó ở bước cuối để
# tránh xung đột dependency (đã gặp thực tế lúc build lần đầu).

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$embedDir = Join-Path $root "python-embed"
$pythonVersion = "3.12.7"

if (Test-Path $embedDir) {
    Write-Host "Xoá $embedDir cũ..."
    Remove-Item $embedDir -Recurse -Force
}
New-Item -ItemType Directory -Path $embedDir | Out-Null

$zipPath = Join-Path $env:TEMP "python-embed-$pythonVersion.zip"
Write-Host "Tải Python $pythonVersion embeddable..."
Invoke-WebRequest -Uri "https://www.python.org/ftp/python/$pythonVersion/python-$pythonVersion-embed-amd64.zip" -OutFile $zipPath
Expand-Archive -Path $zipPath -DestinationPath $embedDir -Force

$pthFile = Join-Path $embedDir "python312._pth"
Set-Content -Path $pthFile -Encoding ASCII -Value @"
python312.zip
.
Lib\site-packages

import site
"@

$getPipPath = Join-Path $env:TEMP "get-pip.py"
Write-Host "Cài pip..."
Invoke-WebRequest -Uri "https://bootstrap.pypa.io/get-pip.py" -OutFile $getPipPath
& "$embedDir\python.exe" $getPipPath --no-warn-script-location

Write-Host "Cài faster-whisper + argostranslate (kéo theo torch/stanza/spacy - ~1GB)..."
& "$embedDir\python.exe" -m pip install --no-warn-script-location faster-whisper argostranslate

Write-Host "Chốt lại đúng bản stanza==1.10.1 (argostranslate 1.11.0 cần bản này)..."
& "$embedDir\python.exe" -m pip install --no-warn-script-location "stanza==1.10.1"

Write-Host "Kiểm tra import..."
& "$embedDir\python.exe" -c "import argostranslate.translate; from faster_whisper import WhisperModel; print('OK')"

Write-Host "Xong. Thư mục: $embedDir"

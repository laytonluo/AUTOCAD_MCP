# ============================================================
# fix_autocad_mcp_big5.ps1
# 修正 autocad-mcp 繁體中文 Big5 編碼亂碼問題
#
# 使用方式：
#   .\fix_autocad_mcp_big5.ps1 -RepoPath "E:\02CODE\autocad-mcp"
#
# 修正內容：
#   1. file_ipc.py - 加入 _redecode_cjk() 函式，解析 Big5 字元
#   2. client.py   - json.dumps 加入 ensure_ascii=False，保留中文字元
# ============================================================

param(
    [string]$RepoPath = "E:\02CODE\autocad-mcp"
)

Write-Host ""
Write-Host "======================================================"
Write-Host " AutoCAD MCP 繁體中文 Big5 編碼修正腳本"
Write-Host "======================================================"
Write-Host " Repo 路徑: $RepoPath"
Write-Host ""

# --- 確認路徑存在 ---
if (-not (Test-Path $RepoPath)) {
    Write-Host "❌ 找不到路徑: $RepoPath" -ForegroundColor Red
    Write-Host "   請確認 autocad-mcp 已 clone，並指定正確路徑。" -ForegroundColor Yellow
    exit 1
}

$fileIpc = "$RepoPath\src\autocad_mcp\backends\file_ipc.py"
$clientPy = "$RepoPath\src\autocad_mcp\client.py"

if (-not (Test-Path $fileIpc)) {
    Write-Host "❌ 找不到 file_ipc.py: $fileIpc" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $clientPy)) {
    Write-Host "❌ 找不到 client.py: $clientPy" -ForegroundColor Red
    exit 1
}

# ============================================================
# 修正 1：file_ipc.py — 加入 _redecode_cjk 函式與呼叫
# ============================================================

$ipcContent = Get-Content $fileIpc -Raw -Encoding UTF8

if ($ipcContent -match "_redecode_cjk") {
    Write-Host "⏭️  file_ipc.py 已包含 _redecode_cjk，略過" -ForegroundColor Cyan
} else {
    $redecodeFunc = @'


def _redecode_cjk(obj, encoding: str = "big5"):
    """Recursively fix strings mis-encoded as Latin-1 code-points.

    AutoCAD LISP serialises Chinese characters (Big5 bytes) as individual
    \u00xx JSON escapes. After json.loads() each byte becomes a U+00xx
    code-point. We detect such strings (any char in U+0080-U+00FF) and
    re-encode to raw bytes via latin-1, then decode with the target encoding.
    Pure ASCII strings pass through unchanged.
    """
    if isinstance(obj, str):
        if any(0x80 <= ord(c) <= 0xFF for c in obj):
            try:
                return obj.encode("latin-1").decode(encoding)
            except (UnicodeEncodeError, UnicodeDecodeError):
                return obj
        return obj
    if isinstance(obj, dict):
        return {k: _redecode_cjk(v, encoding) for k, v in obj.items()}
    if isinstance(obj, list):
        return [_redecode_cjk(i, encoding) for i in obj]
    return obj

'@

    $ipcContent = $ipcContent -replace "(log = structlog\.get_logger\(\))", ('$1' + $redecodeFunc)
    $ipcContent = $ipcContent -replace "(data = json\.loads\(text\))", ('$1' + "`r`n                        data = _redecode_cjk(data)")
    [System.IO.File]::WriteAllText($fileIpc, $ipcContent, [System.Text.Encoding]::UTF8)
    Write-Host "✅ file_ipc.py — _redecode_cjk 函式已加入" -ForegroundColor Green
}

# ============================================================
# 修正 2：client.py — ensure_ascii=False
# ============================================================

$clientContent = Get-Content $clientPy -Raw -Encoding UTF8

if ($clientContent -match "ensure_ascii=False") {
    Write-Host "⏭️  client.py 已包含 ensure_ascii=False，略過" -ForegroundColor Cyan
} else {
    $clientContent = $clientContent -replace `
        'json\.dumps\(data, default=str, separators=\(",", ":"\)\)', `
        'json.dumps(data, default=str, separators=(",", ":"), ensure_ascii=False)'
    [System.IO.File]::WriteAllText($clientPy, $clientContent, [System.Text.Encoding]::UTF8)
    Write-Host "✅ client.py — ensure_ascii=False 已加入" -ForegroundColor Green
}

# ============================================================
# 清除 __pycache__
# ============================================================

$caches = @(
    "$RepoPath\src\autocad_mcp\__pycache__",
    "$RepoPath\src\autocad_mcp\backends\__pycache__"
)
foreach ($cache in $caches) {
    if (Test-Path $cache) {
        Remove-Item $cache -Recurse -Force
        Write-Host "🗑️  已清除: $cache" -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "======================================================"
Write-Host " ✅ 所有修正完成！"
Write-Host "======================================================"
Write-Host ""
Write-Host " 下一步："
Write-Host "  1. 重啟 Claude Desktop"
Write-Host "  2. 在 AutoCAD 命令列執行："
Write-Host '     (load "你的路徑/autocad-mcp/lisp-code/mcp_dispatch.lsp")'
Write-Host ""

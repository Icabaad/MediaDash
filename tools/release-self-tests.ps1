$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'release.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Release script has parse errors' }
$guard = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-ReleaseSource' }, $true)
if (-not $guard) { throw 'Release source guard missing' }
Invoke-Expression $guard.Extent.Text

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('mediadash-release-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
Push-Location $testRoot
try {
    & git init --quiet
    & git config user.name 'Release guard test'
    & git config user.email 'release-test@example.invalid'
    Set-Content source.txt 'original'
    & git add source.txt
    & git commit --quiet -m 'Owned test source'
    if ($LASTEXITCODE -ne 0) { throw 'Test repository setup failed' }
    $sha = Assert-ReleaseSource
    if (-not $sha) { throw 'Clean source was rejected' }
    foreach ($change in @('modified', 'untracked', 'staged', 'new-head')) {
        if ($change -eq 'untracked') { Set-Content extra.txt 'owned fixture' }
        else { Set-Content source.txt $change }
        if ($change -eq 'staged' -or $change -eq 'new-head') { & git add source.txt }
        if ($change -eq 'new-head') { & git commit --quiet -m 'Changed HEAD' }
        $refused = $false
        try { $null = Assert-ReleaseSource -ExpectedSha $sha } catch { $refused = $true }
        if (-not $refused) { throw "$change was accepted" }
        if ($change -eq 'untracked') { Remove-Item -LiteralPath extra.txt }
        elseif ($change -ne 'new-head') { & git restore --staged --worktree source.txt }
        Write-Host "PASS release refuses $change"
    }
    Write-Host 'PASS release accepts clean source'
}
finally {
    Pop-Location
    if ([IO.Path]::GetDirectoryName($testRoot) -ne [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)) { throw 'Test cleanup path is outside the temporary directory' }
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}

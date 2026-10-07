param(
    [string]$BaseUrl = 'http://localhost:5105',
    [string]$ImagePath,
    # API token from the account page of the web app (writes require a signed-in user).
    [string]$Token = $env:RECIPE_API_TOKEN
)

$ErrorActionPreference = 'Stop'
$null = Get-Command curl.exe -ErrorAction Stop
$BaseUrl = $BaseUrl.TrimEnd('/')
if (-not $Token) {
    throw 'Sign in to Recipe Manager, open the account page, create an API token and pass it with -Token or RECIPE_API_TOKEN.'
}
$resolvedImage = if ($ImagePath) { (Resolve-Path -LiteralPath $ImagePath).Path } else { $null }
$sessionDirectory = Join-Path ([IO.Path]::GetTempPath()) ('recipe-manager-api-demo-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($sessionDirectory)
$cookieFile = Join-Path $sessionDirectory 'cookies.txt'
$headerFile = Join-Path $sessionDirectory 'headers.cfg'
$authFile = Join-Path $sessionDirectory 'auth.cfg'
$requestFile = Join-Path $sessionDirectory 'request.json'
$responseFile = Join-Path $sessionDirectory 'response.json'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$createdRoutes = New-Object 'System.Collections.Generic.List[string]'

function Invoke-ApiRequest {
    param(
        [string]$Method,
        [string]$Path,
        [int[]]$ExpectedStatus,
        [object]$Body = $null,
        [string]$Image = '',
        [switch]$WithoutToken
    )
    # Files avoid PowerShell's native-command JSON quoting and keep tokens out of console output.
    $curlArguments = @('--silent', '--show-error', '--connect-timeout', '5', '--max-time', '30',
        '--request', $Method, '--cookie', $cookieFile, '--cookie-jar', $cookieFile,
        '--output', $responseFile, '--write-out', '%{http_code}', '--config', $authFile)
    if ($Method -ne 'GET' -and -not $WithoutToken) {
        $curlArguments += @('--config', $headerFile)
    }
    if ($null -ne $Body) {
        [IO.File]::WriteAllText($requestFile, ($Body | ConvertTo-Json -Depth 6), $utf8)
        $curlArguments += @('--header', 'Content-Type: application/json', '--data-binary', "@$requestFile")
    }
    if ($Image) {
        $curlArguments += @('--form', ('imageFile=@"' + $Image.Replace('\', '/') + '"'))
    }
    $curlArguments += ($BaseUrl + $Path)
    $statusText = & curl.exe @curlArguments
    if ($LASTEXITCODE -ne 0) { throw "curl failed for $Method $Path." }
    $status = [int]$statusText
    Write-Host "$Method $Path -> HTTP $status"
    if ($status -notin $ExpectedStatus) {
        throw "Expected HTTP $($ExpectedStatus -join ' or '), received $status for $Method $Path."
    }
    $text = [IO.File]::ReadAllText($responseFile)
    $value = if ($text.Trim()) { $text | ConvertFrom-Json } else { $null }
    return [PSCustomObject]@{ Status = $status; Body = $value }
}

try {
    [IO.File]::WriteAllText($authFile, ('header = "Authorization: Bearer ' + $Token.Trim() + '"'), $utf8)
    $session = (Invoke-ApiRequest GET '/api/antiforgery' 200).Body
    [IO.File]::WriteAllText($headerFile, ('header = "' + $session.headerName + ': ' + $session.requestToken + '"'), $utf8)
    $suffix = [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $lookupIds = @{}
    foreach ($resource in @('categories', 'ingredients')) {
        $collection = Invoke-ApiRequest GET "/api/$resource" 200
        Write-Host "JSON collection contains $(@($collection.Body).Count) records."
        $created = (Invoke-ApiRequest POST "/api/$resource" 201 @{ name = "C5 demo $resource $suffix" }).Body
        $path = "/api/$resource/$($created.id)"
        $createdRoutes.Add($path)
        $lookupIds[$resource] = $created.id
        $null = Invoke-ApiRequest GET $path 200
        $null = Invoke-ApiRequest PUT $path 200 @{ name = "C5 updated $resource $suffix" }
        $null = Invoke-ApiRequest POST $path 200 @{ name = "C5 POST update $resource $suffix" }
    }

    $recipeFields = @{ title = "C5 demo recipe $suffix"; description = 'Created through curl.';
        instructions = "Prepare the ingredients.`nCook and serve.";
        cookingTimeMinutes = 20; servings = 2; categoryId = $lookupIds['categories'] }
    $recipe = (Invoke-ApiRequest POST '/api/recipes' 201 $recipeFields).Body
    $recipePath = "/api/recipes/$($recipe.id)"
    $createdRoutes.Add($recipePath)
    $collection = Invoke-ApiRequest GET '/api/recipes' 200
    Write-Host "JSON collection contains $(@($collection.Body).Count) recipes."
    $read = (Invoke-ApiRequest GET $recipePath 200).Body
    Write-Host ($read | ConvertTo-Json -Depth 6)
    $recipeFields.title = "C5 updated recipe $suffix"
    $null = Invoke-ApiRequest PUT $recipePath 200 $recipeFields
    $recipeFields.title = "C5 POST update recipe $suffix"
    $null = Invoke-ApiRequest POST $recipePath 200 $recipeFields
    if ($resolvedImage) {
        $uploaded = (Invoke-ApiRequest POST "$recipePath/image" 200 -Image $resolvedImage).Body
        if (-not $uploaded.imagePath) { throw 'The upload did not return an image path.' }
        Write-Host "Saved image: $($uploaded.imagePath)"
    }

    $null = Invoke-ApiRequest DELETE "/api/categories/$($lookupIds['categories'])" 409
    $null = Invoke-ApiRequest POST '/api/recipes' 400 @{}
    $null = Invoke-ApiRequest POST '/api/categories' 400 @{ name = 'Blocked without antiforgery' } -WithoutToken
    $null = Invoke-ApiRequest DELETE $recipePath 204
    $null = $createdRoutes.Remove($recipePath)
    $null = Invoke-ApiRequest GET $recipePath 404
    foreach ($resource in @('ingredients', 'categories')) {
        $path = "/api/$resource/$($lookupIds[$resource])"
        $null = Invoke-ApiRequest DELETE $path 204
        $null = $createdRoutes.Remove($path)
        $null = Invoke-ApiRequest GET $path 404
    }
    Write-Host 'C5 curl demonstration passed; all demo records removed.'
}
finally {
    # Remove only IDs created by this run, in reverse dependency order.
    for ($index = $createdRoutes.Count - 1; $index -ge 0; $index--) {
        try { $null = Invoke-ApiRequest DELETE $createdRoutes[$index] @(204, 404) }
        catch { Write-Warning "Could not remove demo record $($createdRoutes[$index]); check the application." }
    }
    foreach ($file in @($cookieFile, $headerFile, $authFile, $requestFile, $responseFile)) {
        [IO.File]::Delete($file)
    }
    [IO.Directory]::Delete($sessionDirectory)
}

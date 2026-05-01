# Documentation Generation Script

# Check if DocFX is installed
$docfx = Get-Command docfx -ErrorAction SilentlyContinue

if (-not $docfx) {
    Write-Host "DocFX not found. Installing..." -ForegroundColor Cyan
    dotnet tool install -g docfx
}

# Ensure we are in the project root
$root = Get-Item "."
Write-Host "Generating documentation for project at $($root.FullName)..." -ForegroundColor Green

# Create docfx config if it doesn't exist
if (-not (Test-Path "docfx.json")) {
    Write-Host "Initializing DocFX configuration..." -ForegroundColor Cyan
    # Simple default docfx.json
    $config = @'
{
  "metadata": [
    {
      "src": [
        {
          "files": ["**/*.csproj"],
          "exclude": ["**/obj/**", "**/bin/**"]
        }
      ],
      "dest": "api",
      "disableDefaultFilter": false
    }
  ],
  "build": {
    "content": [
      {
        "files": ["api/**.yml", "api/index.md"]
      },
      {
        "files": ["*.md", "docs/**.md", "docs/**/toc.yml", "toc.yml", "index.md"]
      }
    ],
    "resource": [
      {
        "files": ["images/**"]
      }
    ],
    "output": "_site",
    "template": ["default", "modern"],
    "postProcessors": [],
    "markdownEngineName": "markdig",
    "noLangKeyword": false
  }
}
'@
    $config | Out-File -FilePath "docfx.json" -Encoding utf8
}

# Run DocFX
Write-Host "Running DocFX Build..." -ForegroundColor Cyan
docfx build --serve

# Script build et deploy CoucheMur
param([switch]$Deploy, [switch]$BuildOnly)

Write-Host "=== BUILD COUCHEMUR ===" -ForegroundColor Cyan
if (!$BuildOnly) {
    Write-Host "Mode: Build + Deploy + Test (Revit sera fermé puis relancé)" -ForegroundColor Cyan
} else {
    Write-Host "Mode: Build seulement (Revit non touché)" -ForegroundColor Cyan
}

# Toujours fermer Revit au début (sauf si BuildOnly)
if (!$BuildOnly) {
    Write-Host "Vérification et fermeture de Revit..." -ForegroundColor Yellow
    $revitProcesses = Get-Process -Name "Revit" -ErrorAction SilentlyContinue
    if ($revitProcesses) {
        Write-Host "Fermeture de Revit en cours..." -ForegroundColor Yellow
        $revitProcesses | Stop-Process -Force
        Start-Sleep 3
        Write-Host "✓ Revit fermé" -ForegroundColor Green
    } else {
        Write-Host "✓ Revit n'était pas ouvert" -ForegroundColor Gray
    }
}

# Build
Write-Host "Build..." -ForegroundColor Yellow
dotnet build CoucheMur.csproj -c Debug --nologo -v minimal
if ($LASTEXITCODE -ne 0) { exit 1 }

# Deploy (par défaut sauf si BuildOnly)
if (!$BuildOnly) {
    Write-Host "Deploy..." -ForegroundColor Yellow
    
    # Lire le chemin Assembly depuis le fichier .addin
    $addinContent = Get-Content "CoucheMur.addin" -Raw
    if ($addinContent -match '<Assembly>(.*?)</Assembly>') {
        $assemblyPath = $matches[1]
        $targetDir = Split-Path $assemblyPath -Parent
        Write-Host "Chemin Assembly depuis .addin: $assemblyPath" -ForegroundColor Cyan
        Write-Host "Répertoire de déploiement: $targetDir" -ForegroundColor Cyan
    } else {
        Write-Host "ERREUR: Impossible de lire le chemin Assembly depuis le fichier .addin" -ForegroundColor Red
        exit 1
    }
    
    if (!(Test-Path $targetDir)) { 
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
        Write-Host "✓ Répertoire créé: $targetDir" -ForegroundColor Green
    }
    
    # Vérifier la DLL source
    $sourceDll = "bin\Debug\net48\CoucheMur.dll"
    if (!(Test-Path $sourceDll)) {
        Write-Host "ERREUR: DLL source non trouvée: $sourceDll" -ForegroundColor Red
        exit 1
    }
    
    $sourceInfo = Get-Item $sourceDll
    Write-Host "DLL source: $($sourceInfo.Length) bytes, $($sourceInfo.LastWriteTime)" -ForegroundColor Cyan
    
    # Supprimer et copier avec vérification selon le chemin .addin
    $targetDll = $assemblyPath
    Remove-Item $targetDll -Force -ErrorAction SilentlyContinue
    Copy-Item $sourceDll -Destination $targetDll -Force
    
    # Vérifier la copie
    if (Test-Path $targetDll) {
        $targetInfo = Get-Item $targetDll
        Write-Host "DLL cible: $($targetInfo.Length) bytes, $($targetInfo.LastWriteTime)" -ForegroundColor Cyan
        
        if ($sourceInfo.Length -eq $targetInfo.Length -and $sourceInfo.LastWriteTime -eq $targetInfo.LastWriteTime) {
            Write-Host "✓ DLL copiée avec succès" -ForegroundColor Green
        } else {
            Write-Host "ATTENTION: Problème lors de la copie!" -ForegroundColor Red
        }
    } else {
        Write-Host "ERREUR: DLL non copiée" -ForegroundColor Red
        exit 1
    }
    
    # Copier .addin dans le répertoire Addins de Revit 2024 (ProgramData)
    $addinTargetPath = "C:\ProgramData\Autodesk\Revit\Addins\2024\CoucheMur.addin"
    $addinTargetDir = Split-Path $addinTargetPath -Parent
    if (!(Test-Path $addinTargetDir)) { 
        New-Item -ItemType Directory -Path $addinTargetDir -Force | Out-Null
    }
    Copy-Item "CoucheMur.addin" -Destination $addinTargetPath -Force
    Write-Host "✓ Fichier .addin copié vers: $addinTargetPath" -ForegroundColor Green
    
    Write-Host "Lancement de Revit pour test..." -ForegroundColor Yellow
    Start-Process "${env:ProgramFiles}\Autodesk\Revit 2024\Revit.exe"
    Start-Sleep 2
    Write-Host "✓ DEPLOY TERMINÉ - Revit lancé pour test" -ForegroundColor Green
}

Write-Host "=== TERMINE ===" -ForegroundColor Green

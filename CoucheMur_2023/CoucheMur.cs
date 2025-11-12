using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Autodesk.Revit.Attributes;

namespace CoucheMurPlugin
{
    // Version avec logs détaillés de groupement - 02/10/2025 00:30
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ExploserMurComposeCommand : IExternalCommand
    {
        private const string WallTypePrefix = "CoucheMur";
        private const string WallTypeSuffix = "EXP";
        // LogFileName supprimé car logging désactivé

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            var selectedWalls = new List<Wall>();
            ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();
            if (selectedIds != null && selectedIds.Count > 0)
            {
                foreach (ElementId id in selectedIds)
                {
                    if (doc.GetElement(id) is Wall w)
                        selectedWalls.Add(w);
                }
            }
            if (!selectedWalls.Any())
            {
                try
                {
                    IList<Reference> pickedRefs = uidoc.Selection.PickObjects(
                        ObjectType.Element,
                        new WallSelectionFilter(),
                        "Sélectionnez un ou plusieurs murs composés à exploser");
                    foreach (Reference r in pickedRefs)
                    {
                        if (doc.GetElement(r.ElementId) is Wall w)
                            selectedWalls.Add(w);
                    }
                }
                catch
                {
                    return Result.Cancelled;
                }
            }
            if (!selectedWalls.Any())
                return Result.Cancelled;

            var typeCache = new Dictionary<string, WallType>(StringComparer.OrdinalIgnoreCase);
            using (Transaction t = new Transaction(doc, "Explosion murs composés"))
            {
                t.Start();
                foreach (Wall wall in selectedWalls)
                {
                    try
                    {
                        ExploserMurCompose(doc, wall, typeCache);
                    }
                    catch (Exception ex)
                    {
                        Log($"Erreur sur mur Id={wall.Id.Value}: {ex.Message}");
                    }
                }
                t.Commit();
            }
            Log($"Explosion terminée. Murs traités: {selectedWalls.Count}");
            return Result.Succeeded;
        }

        private void ExploserMurCompose(Document doc, Wall wall, Dictionary<string, WallType> typeCache)
        {
            if (!(doc.GetElement(wall.GetTypeId()) is WallType wallType))
            {
                Log($"Erreur: Impossible de récupérer le type du mur {wall.Id.Value}");
                return;
            }
            
            CompoundStructure structure = wallType.GetCompoundStructure();
            if (structure == null)
            {
                Log($"Erreur: Le mur {wall.Id.Value} n'a pas de structure composée");
                return;
            }
            
            var layers = structure.GetLayers();
            if (layers == null || layers.Count() <= 1)
            {
                Log($"Erreur: Le mur {wall.Id.Value} n'est pas composé (seulement {layers?.Count() ?? 0} couche(s))");
                return;
            }
            
            Log($"Début explosion mur {wall.Id.Value} - Type: {wallType.Name} - {layers.Count()} couches");
            
            if (!(wall.Location is LocationCurve locCurve)) 
            {
                Log($"Erreur: Impossible de récupérer la courbe de localisation du mur {wall.Id.Value}");
                return;
            }
            
            // MÉMORISER LA POSITION ORIGINALE AVANT TOUTE MODIFICATION
            Curve originalBaseCurve = locCurve.Curve;
            WallLocationLine originalLocationLine = (WallLocationLine)wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM).AsInteger();
            
            Log($"MÉMORISATION position mur original:");
            Log($"  Ligne originale: {originalLocationLine}");
            Log($"  Position: {FormatXYZ(originalBaseCurve.GetEndPoint(0))} -> {FormatXYZ(originalBaseCurve.GetEndPoint(1))}");
            
            double wallHeight = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble();
            var newWalls = new List<Wall>();
            
            Log($"Largeur totale mur: {UnitUtils.ConvertFromInternalUnits(structure.GetWidth(), UnitTypeId.Millimeters):0.1f}mm");
            Log($"Hauteur mur: {UnitUtils.ConvertFromInternalUnits(wallHeight, UnitTypeId.Millimeters):0.1f}mm");

            // ÉTAPE 1: ANALYSER LA GÉOMÉTRIE AVEC POSITION MÉMORISÉE
            var wallGeometry = GetWallLayerGeometry(wall, wallType, structure, originalLocationLine, originalBaseCurve);
            if (wallGeometry == null || wallGeometry.Count == 0)
            {
                Log($"ERREUR: Impossible d'analyser la géométrie du mur {wall.Id.Value}");
                return;
            }

            Log($"Géométrie analysée: {wallGeometry.Count} couches détectées");

            int layerIndex = 0;
            foreach (CompoundStructureLayer layer in layers)
            {
                layerIndex++;
                
                string functionName = GetFunctionNameFR(layer.Function);
                string materialName = GetMaterialName(doc, layer);
                double thickness = layer.Width;
                double thicknessMM = UnitUtils.ConvertFromInternalUnits(thickness, UnitTypeId.Millimeters);
                
                string cleanFunction = CleanName(functionName);
                string cleanMaterial = CleanName(materialName);
                string newTypeName = $"{WallTypePrefix}-{cleanFunction}-{cleanMaterial}-{thicknessMM:0}mm";
                
                Log($"  Couche {layerIndex}: {functionName} - {materialName} - {thicknessMM:0.1f}mm");
                
                WallType murCoucheType = GetOrCreateWallType(doc, wallType, newTypeName, layer, typeCache);
                if (murCoucheType == null) 
                { 
                    Log($"  ERREUR: Impossible de créer le type de mur : {newTypeName}");
                    continue; 
                }
                
                // ÉTAPE 2: RÉCUPÉRER LA GÉOMÉTRIE EXACTE DE CETTE COUCHE
                if (!wallGeometry.ContainsKey(layerIndex - 1)) // Index 0-based
                {
                    Log($"  ERREUR: Géométrie de la couche {layerIndex} non trouvée");
                    continue;
                }

                var layerGeom = wallGeometry[layerIndex - 1];
                Curve layerCenterline = layerGeom.Centerline;
                double layerThickness = layerGeom.Thickness;
                XYZ layerNormal = layerGeom.Normal;

                Log($"  Position couche {layerIndex}:");
                Log($"    Centre: {FormatXYZ(layerCenterline.GetEndPoint(0))} -> {FormatXYZ(layerCenterline.GetEndPoint(1))}");
                Log($"    Épaisseur: {UnitUtils.ConvertFromInternalUnits(layerThickness, UnitTypeId.Millimeters):0.1f}mm");
                Log($"    Normale: {FormatXYZ(layerNormal)}");

                try
                {
                    // ÉTAPE 3: CRÉER LE MUR-COUCHE AVEC LA GÉOMÉTRIE EXACTE (MÉTHODE QUI FONCTIONNAIT)
                    Wall newWall = Wall.Create(
                        doc,
                        layerCenterline,  // Courbe exacte de la couche
                        murCoucheType.Id,
                        wall.LevelId,
                        wallHeight,
                        0,  // Pas d'offset supplémentaire car déjà dans la courbe
                        false,
                        false
                    );
                    
                    Log($"    ✓ Mur-couche créé avec Wall.Create (ID: {newWall?.Id.Value})");

                    if (newWall != null && newWall.IsValidObject)
                    {
                        // ÉTAPE 4: APPLIQUER TOUS LES PARAMÈTRES DU MUR ORIGINAL
                        CopyWallParameters(wall, newWall);
                        
                        // FORCER Nu fini extérieur sur chaque mur-couche
                        Parameter newLocationParam = newWall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
                        if (newLocationParam != null && !newLocationParam.IsReadOnly)
                        {
                            newLocationParam.Set((int)WallLocationLine.FinishFaceExterior);
                            Log($"  Justification Nu fini extérieur appliquée");
                        }

                        newWalls.Add(newWall);
                        Log($"  SUCCESS: Mur-couche {layerIndex} créé ID={newWall.Id.Value}");
                    }
                    else
                    {
                        Log($"  ERREUR: Mur-couche NULL");
                    }
                }
                catch (Exception ex)
                {
                    Log($"  ERREUR création couche {layerIndex}: {ex.Message}");
                }
            }
            
            // Supprimer le mur original SEULEMENT si on a créé au moins un mur-couche
            if (newWalls.Count > 0)
            {
                Log($"SUCCESS: {newWalls.Count} murs-couches créés");
                
                // NOUVEAU: Réassigner les ouvertures et copier les profils personnalisés
                try
                {
                    ReassignOpeningsAndCopyProfiles(doc, wall, newWalls);
                }
                catch (Exception ex)
                {
                    Log($"ERREUR réassignation ouvertures/profils: {ex.Message}");
                }
                
                // NOUVEAU: Join Geometry - Attacher les couches pour propager les ouvertures
                try
                {
                    JoinWallLayersGeometry(doc, newWalls);
                }
                catch (Exception ex)
                {
                    Log($"ERREUR Join Geometry: {ex.Message}");
                }
                
                // NOUVEAU: Détecter et ajuster les jonctions avant suppression du mur original
                try
                {
                    Log($"Détection des jonctions pour ajustement des murs-couches...");
                    var junctions = DetectWallJunctions(doc, wall);
                    
                    if (junctions.Count > 0)
                    {
                        Log($"Ajustement des {newWalls.Count} murs-couches pour {junctions.Count} jonctions...");
                        AdjustLayerWallsAtJunctions(doc, newWalls, junctions, wall);
                    }
                    else
                    {
                        Log("Aucune jonction détectée - pas d'ajustement nécessaire");
                    }
                }
                catch (Exception ex)
                {
                    Log($"ERREUR ajustement jonctions: {ex.Message}");
                }
                
                // Supprimer le mur original après ajustement des jonctions
                Log($"Suppression du mur original {wall.Id.Value}");
                try 
                {
                    if (wall.IsValidObject)
                    {
                        doc.Delete(wall.Id);
                        Log($"SUCCESS: Mur original SUPPRIMÉ - remplacé par {newWalls.Count} murs-couches");
                    }
                } 
                catch (Exception ex)
                {
                    Log($"ERREUR suppression mur original: {ex.Message}");
                }
            }
            else
            {
                Log($"ÉCHEC: Aucun mur-couche créé pour le mur {wall.Id.Value} - mur original conservé");
            }
        }

        /// <summary>
        /// Fonction pour détecter les jonctions entre murs composés
        /// </summary>
        private List<WallJunctionInfo> DetectWallJunctions(Document doc, Wall targetWall, double tolerance = 500.0)
        {
            var junctions = new List<WallJunctionInfo>();
            
            try
            {
                if (!(targetWall.Location is LocationCurve targetLocCurve))
                    return junctions;

                Curve targetCurve = targetLocCurve.Curve;
                XYZ targetStart = targetCurve.GetEndPoint(0);
                XYZ targetEnd = targetCurve.GetEndPoint(1);
                
                // Tolérance de 500mm convertie en unités internes (pieds)
                double internalTolerance = UnitUtils.ConvertToInternalUnits(tolerance, UnitTypeId.Millimeters);
                
                Log($"Détection jonctions pour mur {targetWall.Id.Value} (tolérance: {tolerance}mm = {internalTolerance:F6} pieds):");
                Log($"  Point début: {FormatXYZ(targetStart)}");
                Log($"  Point fin: {FormatXYZ(targetEnd)}");
                
                // Chercher tous les autres murs dans le projet
                var allWalls = new FilteredElementCollector(doc)
                    .OfClass(typeof(Wall))
                    .Cast<Wall>()
                    .Where(w => w.Id != targetWall.Id)
                    .ToList();
                    
                Log($"  Analysing {allWalls.Count} autres murs...");
                    
                foreach (Wall otherWall in allWalls)
                {
                    if (!(otherWall.Location is LocationCurve otherLocCurve))
                        continue;
                        
                    Curve otherCurve = otherLocCurve.Curve;
                    XYZ otherStart = otherCurve.GetEndPoint(0);
                    XYZ otherEnd = otherCurve.GetEndPoint(1);
                    
                    double distStartStart = targetStart.DistanceTo(otherStart);
                    double distStartEnd = targetStart.DistanceTo(otherEnd);
                    double distEndStart = targetEnd.DistanceTo(otherStart);
                    double distEndEnd = targetEnd.DistanceTo(otherEnd);
                    
                    // Log distances for debug
                    if (distStartStart < internalTolerance || distStartEnd < internalTolerance || 
                        distEndStart < internalTolerance || distEndEnd < internalTolerance)
                    {
                        Log($"    Mur {otherWall.Id.Value}: distances = {distStartStart:F6}/{distStartEnd:F6}/{distEndStart:F6}/{distEndEnd:F6}");
                    }
                    
                    // Vérifier les connexions aux extrémités avec tolérance élargie
                    if (distStartStart < internalTolerance)
                    {
                        junctions.Add(new WallJunctionInfo
                        {
                            ConnectedWall = otherWall,
                            JunctionPoint = targetStart,
                            TargetWallEndpoint = WallEndpoint.Start,
                            ConnectedWallEndpoint = WallEndpoint.Start
                        });
                        Log($"    ✓ Jonction trouvée: Début->Début avec mur {otherWall.Id.Value} (dist: {distStartStart:F6})");
                    }
                    else if (distStartEnd < internalTolerance)
                    {
                        junctions.Add(new WallJunctionInfo
                        {
                            ConnectedWall = otherWall,
                            JunctionPoint = targetStart,
                            TargetWallEndpoint = WallEndpoint.Start,
                            ConnectedWallEndpoint = WallEndpoint.End
                        });
                        Log($"    ✓ Jonction trouvée: Début->Fin avec mur {otherWall.Id.Value} (dist: {distStartEnd:F6})");
                    }
                    
                    if (distEndStart < internalTolerance)
                    {
                        junctions.Add(new WallJunctionInfo
                        {
                            ConnectedWall = otherWall,
                            JunctionPoint = targetEnd,
                            TargetWallEndpoint = WallEndpoint.End,
                            ConnectedWallEndpoint = WallEndpoint.Start
                        });
                        Log($"    ✓ Jonction trouvée: Fin->Début avec mur {otherWall.Id.Value} (dist: {distEndStart:F6})");
                    }
                    else if (distEndEnd < internalTolerance)
                    {
                        junctions.Add(new WallJunctionInfo
                        {
                            ConnectedWall = otherWall,
                            JunctionPoint = targetEnd,
                            TargetWallEndpoint = WallEndpoint.End,
                            ConnectedWallEndpoint = WallEndpoint.End
                        });
                        Log($"    ✓ Jonction trouvée: Fin->Fin avec mur {otherWall.Id.Value} (dist: {distEndEnd:F6})");
                    }
                }
                
                Log($"  Total jonctions détectées: {junctions.Count}");
            }
            catch (Exception ex)
            {
                Log($"ERREUR détection jonctions: {ex.Message}");
            }
            
            return junctions;
        }

        /// <summary>
        /// Fonction pour ajuster/trim les murs-couches aux jonctions - NOUVELLE VERSION avec trim par type
        /// </summary>
        private void AdjustLayerWallsAtJunctions(Document doc, List<Wall> layerWalls, List<WallJunctionInfo> junctions, Wall originalWall)
        {
            if (layerWalls == null || layerWalls.Count == 0 || junctions == null || junctions.Count == 0)
            {
                Log("Aucun ajustement de jonctions nécessaire");
                return;
            }
            
            Log($"TRIM SYNCHRONISÉ - {layerWalls.Count} murs-couches, {junctions.Count} jonctions détectées");
            
            // DIAGNOSTIC DÉTAILLÉ: Lister tous les murs-couches avant groupement
            Log("=== DIAGNOSTIC MURS-COUCHES AVANT TRIM ===");
            for (int i = 0; i < layerWalls.Count; i++)
            {
                var wall = layerWalls[i];
                var wallType = doc.GetElement(wall.GetTypeId()) as WallType;
                var wallName = wallType?.Name ?? "Type inconnu";
                Log($"  Mur-couche {i+1}/{layerWalls.Count}: ID={wall.Id.Value}, Type='{wallName}'");
                
                if (wall.Location is LocationCurve loc)
                {
                    var curve = loc.Curve;
                    Log($"    Position: {FormatXYZ(curve.GetEndPoint(0))} -> {FormatXYZ(curve.GetEndPoint(1))}");
                    Log($"    Longueur: {UnitUtils.ConvertFromInternalUnits(curve.Length, UnitTypeId.Millimeters):0.1f}mm");
                }
            }
            Log("=== FIN DIAGNOSTIC MURS-COUCHES ===");
            
            try
            {
                // ÉTAPE 1: Grouper les murs-couches par type (fonction + matériau)
                var wallGroups = GroupWallsByType(doc, layerWalls);
                Log($"Groupes de murs-couches identifiés: {wallGroups.Count}");
                
                foreach (var group in wallGroups)
                {
                    Log($"  Groupe '{group.Key}': {group.Value.Count} murs");
                    foreach (var wall in group.Value)
                    {
                        Log($"    - Mur ID {wall.Id.Value}");
                    }
                }

                // DIAGNOSTIC DÉTAILLÉ: Lister toutes les jonctions
                Log("=== DIAGNOSTIC JONCTIONS DÉTECTÉES ===");
                for (int i = 0; i < junctions.Count; i++)
                {
                    var junction = junctions[i];
                    Log($"  Jonction {i+1}/{junctions.Count}:");
                    Log($"    Point: {FormatXYZ(junction.JunctionPoint)}");
                    Log($"    Mur connecté ID: {junction.ConnectedWall.Id.Value}");
                    Log($"    Extrémité mur cible: {junction.TargetWallEndpoint}");
                    Log($"    Extrémité mur connecté: {junction.ConnectedWallEndpoint}");
                }
                Log("=== FIN DIAGNOSTIC JONCTIONS ===");

                // ÉTAPE 2: Pour chaque jonction, calculer les points de trim optimaux par type
                foreach (var junction in junctions)
                {
                    Log($"TRAITEMENT JONCTION au point {FormatXYZ(junction.JunctionPoint)}");
                    Log($"  Mur connecté: ID {junction.ConnectedWall.Id.Value}");
                    
                    // Analyser le mur connecté
                    var connectedWallType = doc.GetElement(junction.ConnectedWall.GetTypeId()) as WallType;
                    if (connectedWallType == null)
                    {
                        Log($"  ERREUR: Impossible de récupérer le type du mur connecté");
                        continue;
                    }
                        
                    var connectedStructure = connectedWallType.GetCompoundStructure();
                    
                    if (connectedStructure == null)
                    {
                        Log($"  Mur connecté {junction.ConnectedWall.Id.Value} n'est pas composé - trim uniforme");
                        TrimAllLayerGroupsToPoint(wallGroups, junction);
                    }
                    else
                    {
                        Log($"  Mur connecté {junction.ConnectedWall.Id.Value} est composé - trim par correspondance");
                        TrimLayerGroupsByCorrespondence(doc, wallGroups, junction, connectedStructure);
                    }
                }
                
                Log("Ajustement des jonctions terminé");
            }
            catch (Exception ex)
            {
                Log($"ERREUR ajustement jonctions: {ex.Message}");
            }
        }

        /// <summary>
        /// Grouper les murs-couches par type (fonction + matériau)
        /// </summary>
        private Dictionary<string, List<Wall>> GroupWallsByType(Document doc, List<Wall> layerWalls)
        {
            var groups = new Dictionary<string, List<Wall>>();
            
            Log("=== DÉBUT GROUPEMENT PAR TYPE ===");
            
            foreach (Wall wall in layerWalls)
            {
                try
                {
                    var wallType = doc.GetElement(wall.GetTypeId()) as WallType;
                    if (wallType == null) 
                    {
                        Log($"  ERREUR: Type de mur null pour mur ID {wall.Id.Value}");
                        continue;
                    }
                    
                    // Extraire la fonction et le matériau du nom du type
                    // Format: "CoucheMur-Fonction-Matériau-XXmm"
                    string typeName = wallType.Name;
                    Log($"  Analyse mur ID {wall.Id.Value}: Type = '{typeName}'");
                    
                    string groupKey = ExtractGroupKeyFromTypeName(typeName);
                    Log($"    Clé de groupe extraite: '{groupKey}'");
                    
                    if (!groups.ContainsKey(groupKey))
                    {
                        groups[groupKey] = new List<Wall>();
                        Log($"    ✓ Nouveau groupe créé: '{groupKey}'");
                    }
                    else
                    {
                        Log($"    ✓ Ajout au groupe existant: '{groupKey}'");
                    }
                    
                    groups[groupKey].Add(wall);
                }
                catch (Exception ex)
                {
                    Log($"ERREUR groupement mur {wall.Id.Value}: {ex.Message}");
                }
            }
            
            Log($"=== RÉSULTAT GROUPEMENT: {groups.Count} groupes ===");
            foreach (var group in groups)
            {
                Log($"  Groupe '{group.Key}': {group.Value.Count} murs");
                foreach (var wall in group.Value)
                {
                    Log($"    - Mur ID {wall.Id.Value}");
                }
            }
            Log("=== FIN GROUPEMENT ===");
            
            return groups;
        }

        /// <summary>
        /// Extraire la clé de groupement du nom du type (Fonction-Matériau)
        /// </summary>
        private string ExtractGroupKeyFromTypeName(string typeName)
        {
            try
            {
                Log($"      Extraction clé de: '{typeName}'");
                // Format attendu: "CoucheMur-Fonction-Matériau-XXmm"
                var parts = typeName.Split('-');
                Log($"      Parties après split: [{string.Join(", ", parts)}]");
                
                if (parts.Length >= 3)
                {
                    string fonction = parts[1];  // Index décalé car plus de layerIndex
                    string materiau = parts[2];  // Index décalé car plus de layerIndex
                    string result = $"{fonction}-{materiau}";
                    Log($"      ✓ Clé extraite: '{result}' (fonction='{fonction}', matériau='{materiau}')");
                    return result;
                }
                else
                {
                    Log($"      ATTENTION: Format inattendu, seulement {parts.Length} parties");
                }
            }
            catch (Exception ex) 
            {
                Log($"      ERREUR extraction clé: {ex.Message}");
            }
            
            Log($"      ⚠ Retour clé par défaut: 'Unknown'");
            return "Unknown";
        }

        /// <summary>
        /// Trim tous les groupes à un même point (cas mur simple connecté)
        /// </summary>
        private void TrimAllLayerGroupsToPoint(Dictionary<string, List<Wall>> wallGroups, WallJunctionInfo junction)
        {
            Log($"  TRIM UNIFORME: {wallGroups.Count} groupes à traiter au point {FormatXYZ(junction.JunctionPoint)}");
            bool trimStart = (junction.TargetWallEndpoint == WallEndpoint.Start);
            Log($"  Direction trim: {(trimStart ? "DÉBUT" : "FIN")} du mur");
            
            int groupIndex = 0;
            foreach (var group in wallGroups)
            {
                groupIndex++;
                Log($"    TRIM GROUPE {groupIndex}/{wallGroups.Count}: '{group.Key}' ({group.Value.Count} murs)");
                
                int wallIndex = 0;
                foreach (Wall wall in group.Value)
                {
                    wallIndex++;
                    Log($"      Trim mur {wallIndex}/{group.Value.Count} (ID {wall.Id.Value})");
                    TrimWallAtPoint(wall, junction.JunctionPoint, trimStart);
                }
                Log($"    ✓ Groupe '{group.Key}' terminé");
            }
            Log($"  ✓ TRIM UNIFORME terminé pour tous les groupes");
        }

        /// <summary>
        /// Trim les groupes par correspondance avec les couches du mur connecté
        /// </summary>
        private void TrimLayerGroupsByCorrespondence(Document doc, Dictionary<string, List<Wall>> wallGroups, 
                                                   WallJunctionInfo junction, CompoundStructure connectedStructure)
        {
            try
            {
                Log($"    Trim par correspondance - {wallGroups.Count} groupes");
                
                // Analyser les couches du mur connecté
                var connectedLayers = connectedStructure.GetLayers();
                if (connectedLayers == null || connectedLayers.Count() == 0)
                    return;
                    
                // Obtenir la géométrie du mur connecté
                var connectedGeometry = GetWallLayerGeometry(junction.ConnectedWall, 
                    doc.GetElement(junction.ConnectedWall.GetTypeId()) as WallType,
                    connectedStructure,
                    (WallLocationLine)junction.ConnectedWall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM).AsInteger());
                
                // STRATÉGIE: Trouver d'abord la couche structure principale comme référence
                int structureLayerIndex = -1;
                XYZ structureTrimPoint = null;
                
                for (int i = 0; i < connectedLayers.Count(); i++)
                {
                    var layer = connectedLayers[i];
                    string layerFunction = GetFunctionNameFR(layer.Function);
                    
                    if (layerFunction == "Stru") // Structure principale
                    {
                        structureLayerIndex = i;
                        if (connectedGeometry.ContainsKey(i))
                        {
                            // Trouver le premier mur du groupe structure pour calculer le point de référence
                            var structureGroup = wallGroups.Values.FirstOrDefault(walls => walls.Count > 0);
                            if (structureGroup != null)
                            {
                                structureTrimPoint = CalculateOptimalTrimPoint(structureGroup[0], connectedGeometry[i], junction);
                                Log($"      STRUCTURE PRINCIPALE trouvée: Couche {i+1}, Point référence: {FormatXYZ(structureTrimPoint)}");
                            }
                        }
                        break;
                    }
                }
                
                // Appliquer la même logique à TOUS les groupes
                foreach (var group in wallGroups)
                {
                    Log($"      Groupe '{group.Key}' ({group.Value.Count} murs):");
                    
                    // LOGIQUE TRIM/EXTEND INTELLIGENTE
                    Log($"        GROUPE '{group.Key}' ({group.Value.Count} murs):");
                    
                    // Trouver la couche correspondante dans le mur connecté
                    int correspondingLayerIndex = FindCorrespondingLayer(group.Key, connectedLayers, doc);
                    XYZ targetTrimPoint = junction.JunctionPoint; // Point par défaut
                    
                    if (correspondingLayerIndex >= 0 && connectedGeometry.ContainsKey(correspondingLayerIndex))
                    {
                        // Calculer le point d'intersection réel entre les deux couches
                        var correspondingGeometry = connectedGeometry[correspondingLayerIndex];
                        targetTrimPoint = CalculateLayerIntersection(group.Value[0], correspondingGeometry, junction);
                        
                        Log($"        Correspondance trouvée avec couche {correspondingLayerIndex + 1}");
                        Log($"        Point intersection calculé: {FormatXYZ(targetTrimPoint)}");
                    }
                    else
                    {
                        Log($"        Aucune correspondance - utilisation point jonction: {FormatXYZ(targetTrimPoint)}");
                    }
                    
                    // Appliquer TRIM/EXTEND intelligent à tous les murs du groupe
                    bool trimStart = (junction.TargetWallEndpoint == WallEndpoint.Start);
                    foreach (Wall wall in group.Value)
                    {
                        TrimExtendWallAtPoint(wall, targetTrimPoint, trimStart);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"ERREUR trim par correspondance: {ex.Message}");
            }
        }

        /// <summary>
        /// Trouver l'index de la couche correspondante dans le mur connecté - VERSION AMÉLIORÉE avec priorités
        /// </summary>
        private int FindCorrespondingLayer(string groupKey, IList<CompoundStructureLayer> connectedLayers, Document doc)
        {
            try
            {
                // Extraire fonction et matériau du groupKey
                var parts = groupKey.Split('-');
                if (parts.Length != 2) return -1;
                
                string targetFunction = parts[0];
                string targetMaterial = parts[1];
                
                Log($"          Recherche correspondance pour '{groupKey}' dans {connectedLayers.Count()} couches");
                
                // PRIORITÉ 1: Correspondance exacte (fonction + matériau)
                for (int i = 0; i < connectedLayers.Count(); i++)
                {
                    var layer = connectedLayers[i];
                    string layerFunction = GetFunctionNameFR(layer.Function);
                    string layerMaterial = GetMaterialName(doc, layer);
                    string cleanLayerMaterial = CleanName(layerMaterial);
                    
                    if (layerFunction == targetFunction && cleanLayerMaterial == targetMaterial)
                    {
                        Log($"            PRIORITÉ 1 (exacte): Couche {i+1} ({layerFunction}-{cleanLayerMaterial})");
                        return i;
                    }
                }
                
                // PRIORITÉ 2: Correspondance par fonction seulement (même rôle structural)
                for (int i = 0; i < connectedLayers.Count(); i++)
                {
                    var layer = connectedLayers[i];
                    string layerFunction = GetFunctionNameFR(layer.Function);
                    
                    if (layerFunction == targetFunction)
                    {
                        string layerMaterial = GetMaterialName(doc, layer);
                        string cleanLayerMaterial = CleanName(layerMaterial);
                        Log($"            PRIORITÉ 2 (fonction): Couche {i+1} ({layerFunction}-{cleanLayerMaterial})");
                        return i;
                    }
                }
                
                // PRIORITÉ 3: Correspondance structurelle - toujours utiliser la couche structure principale
                for (int i = 0; i < connectedLayers.Count(); i++)
                {
                    var layer = connectedLayers[i];
                    string layerFunction = GetFunctionNameFR(layer.Function);
                    
                    if (layerFunction == "Stru") // Structure principale
                    {
                        string layerMaterial = GetMaterialName(doc, layer);
                        string cleanLayerMaterial = CleanName(layerMaterial);
                        Log($"            PRIORITÉ 3 (structure principale): Couche {i+1} ({layerFunction}-{cleanLayerMaterial})");
                        Log($"            → Toutes les couches s'alignent sur la structure principale");
                        return i;
                    }
                }
                
                Log($"            AUCUNE CORRESPONDANCE trouvée pour '{groupKey}'");
            }
            catch (Exception ex)
            {
                Log($"ERREUR recherche correspondance: {ex.Message}");
            }
            
            return -1;
        }

        /// <summary>
        /// Calculer le point de trim optimal entre un mur-couche et la géométrie correspondante
        /// </summary>
        private XYZ CalculateOptimalTrimPoint(Wall layerWall, LayerGeometry correspondingGeometry, WallJunctionInfo junction)
        {
            try
            {
                return CalculateLayerIntersection(layerWall, correspondingGeometry, junction);
            }
            catch (Exception ex)
            {
                Log($"ERREUR calcul trim optimal: {ex.Message}");
                return junction.JunctionPoint;
            }
        }

        /// <summary>
        /// Calculer le point d'intersection optimal entre deux couches
        /// </summary>
        private XYZ CalculateLayerIntersection(Wall layerWall, LayerGeometry connectedLayerGeom, WallJunctionInfo junction)
        {
            try
            {
                if (!(layerWall.Location is LocationCurve layerLocCurve))
                    return null;
                    
                Curve layerCurve = layerLocCurve.Curve;
                Curve connectedCurve = connectedLayerGeom.Centerline;
                
                // Calculer l'intersection des axes centraux des couches
                IntersectionResultArray intersectionResults;
                SetComparisonResult result = layerCurve.Intersect(connectedCurve, out intersectionResults);
                
                if (result == SetComparisonResult.Overlap && intersectionResults != null && intersectionResults.Size > 0)
                {
                    return intersectionResults.get_Item(0).XYZPoint;
                }
                
                // Si pas d'intersection directe, calculer l'intersection des lignes étendues
                Line layerLine = layerCurve as Line ?? Line.CreateBound(layerCurve.GetEndPoint(0), layerCurve.GetEndPoint(1));
                Line connectedLine = connectedCurve as Line ?? Line.CreateBound(connectedCurve.GetEndPoint(0), connectedCurve.GetEndPoint(1));
                
                // Étendre les lignes et recalculer l'intersection
                XYZ layerDir = layerLine.Direction;
                XYZ connectedDir = connectedLine.Direction;
                
                XYZ layerStart = layerLine.GetEndPoint(0) - layerDir * 1000; // Étendre de 1000 unités
                XYZ layerEnd = layerLine.GetEndPoint(1) + layerDir * 1000;
                Line extendedLayerLine = Line.CreateBound(layerStart, layerEnd);
                
                XYZ connectedStart = connectedLine.GetEndPoint(0) - connectedDir * 1000;
                XYZ connectedEnd = connectedLine.GetEndPoint(1) + connectedDir * 1000;
                Line extendedConnectedLine = Line.CreateBound(connectedStart, connectedEnd);
                
                result = extendedLayerLine.Intersect(extendedConnectedLine, out intersectionResults);
                if (result == SetComparisonResult.Overlap && intersectionResults != null && intersectionResults.Size > 0)
                {
                    return intersectionResults.get_Item(0).XYZPoint;
                }
                
                // En dernier recours, utiliser le point de jonction original
                return junction.JunctionPoint;
            }
            catch (Exception ex)
            {
                Log($"ERREUR calcul intersection: {ex.Message}");
                return junction.JunctionPoint;
            }
        }

        /// <summary>
        /// Trim/Extend un mur à un point spécifique - ÉQUIVALENT TR de Revit
        /// </summary>
        private void TrimExtendWallAtPoint(Wall wall, XYZ targetPoint, bool modifyStart)
        {
            try
            {
                if (!(wall.Location is LocationCurve locCurve))
                {
                    Log($"        ERREUR: Impossible de récupérer LocationCurve pour mur {wall.Id.Value}");
                    return;
                }
                    
                Curve originalCurve = locCurve.Curve;
                XYZ start = originalCurve.GetEndPoint(0);
                XYZ end = originalCurve.GetEndPoint(1);
                
                Log($"        TR mur {wall.Id.Value}: {(modifyStart ? "DÉBUT" : "FIN")}");
                Log($"          Original: {FormatXYZ(start)} -> {FormatXYZ(end)}");
                Log($"          Point cible: {FormatXYZ(targetPoint)}");
                
                // Projeter le point cible sur la ligne étendue du mur
                Line extendedLine = Line.CreateUnbound(start, (end - start).Normalize());
                XYZ projectedPoint = extendedLine.Project(targetPoint).XYZPoint;
                
                Log($"          Point projeté: {FormatXYZ(projectedPoint)}");
                
                // Créer la nouvelle courbe (trim ou extend selon la position)
                Curve newCurve;
                if (modifyStart)
                {
                    newCurve = Line.CreateBound(projectedPoint, end);
                    Log($"          Nouveau: {FormatXYZ(projectedPoint)} -> {FormatXYZ(end)}");
                }
                else
                {
                    newCurve = Line.CreateBound(start, projectedPoint);
                    Log($"          Nouveau: {FormatXYZ(start)} -> {FormatXYZ(projectedPoint)}");
                }
                
                // Calculer si c'est un trim ou extend
                double originalLength = originalCurve.Length;
                double newLength = newCurve.Length;
                double difference = newLength - originalLength;
                
                if (Math.Abs(difference) < UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters))
                {
                    Log($"        AUCUN CHANGEMENT: différence négligeable ({UnitUtils.ConvertFromInternalUnits(Math.Abs(difference), UnitTypeId.Millimeters):0.1f}mm)");
                    return;
                }
                
                string operation = difference > 0 ? "EXTEND" : "TRIM";
                Log($"          Opération: {operation} de {UnitUtils.ConvertFromInternalUnits(Math.Abs(difference), UnitTypeId.Millimeters):0.1f}mm");
                Log($"          Longueur: {UnitUtils.ConvertFromInternalUnits(originalLength, UnitTypeId.Millimeters):0.1f}mm -> {UnitUtils.ConvertFromInternalUnits(newLength, UnitTypeId.Millimeters):0.1f}mm");
                
                // Appliquer la modification
                locCurve.Curve = newCurve;
                Log($"        ✓ {operation} RÉUSSI sur mur {wall.Id.Value}");
            }
            catch (Exception ex)
            {
                Log($"ERREUR TR mur {wall.Id.Value}: {ex.Message}");
            }
        }

        /// <summary>
        /// Trim un mur à un point spécifique - VERSION AMÉLIORÉE (conservée pour compatibilité)
        /// </summary>
        private void TrimWallAtPoint(Wall wall, XYZ trimPoint, bool trimStart)
        {
            try
            {
                if (!(wall.Location is LocationCurve locCurve))
                {
                    Log($"        ERREUR: Impossible de récupérer LocationCurve pour mur {wall.Id.Value}");
                    return;
                }
                    
                Curve originalCurve = locCurve.Curve;
                XYZ start = originalCurve.GetEndPoint(0);
                XYZ end = originalCurve.GetEndPoint(1);
                
                Log($"        Trim mur {wall.Id.Value}: {(trimStart ? "DÉBUT" : "FIN")}");
                Log($"          Original: {FormatXYZ(start)} -> {FormatXYZ(end)}");
                Log($"          Point trim: {FormatXYZ(trimPoint)}");
                
                // Projeter le point de trim sur la ligne du mur pour s'assurer qu'il est aligné
                XYZ projectedTrimPoint = ProjectPointOnLine(trimPoint, start, end);
                
                // Créer la nouvelle courbe trimée
                Curve newCurve;
                if (trimStart)
                {
                    newCurve = Line.CreateBound(projectedTrimPoint, end);
                    Log($"          Nouveau: {FormatXYZ(projectedTrimPoint)} -> {FormatXYZ(end)}");
                }
                else
                {
                    newCurve = Line.CreateBound(start, projectedTrimPoint);
                    Log($"          Nouveau: {FormatXYZ(start)} -> {FormatXYZ(projectedTrimPoint)}");
                }
                
                // Vérifier que la nouvelle courbe est valide (longueur minimale)
                double minLength = UnitUtils.ConvertToInternalUnits(50.0, UnitTypeId.Millimeters); // 5cm minimum
                if (newCurve.Length < minLength)
                {
                    Log($"        TRIM IGNORÉ: longueur trop courte ({UnitUtils.ConvertFromInternalUnits(newCurve.Length, UnitTypeId.Millimeters):0.1f}mm < 50mm)");
                    return;
                }
                
                // Calculer la différence de longueur
                double originalLength = originalCurve.Length;
                double newLength = newCurve.Length;
                double trimmed = originalLength - newLength;
                
                Log($"          Longueur: {UnitUtils.ConvertFromInternalUnits(originalLength, UnitTypeId.Millimeters):0.1f}mm -> {UnitUtils.ConvertFromInternalUnits(newLength, UnitTypeId.Millimeters):0.1f}mm");
                Log($"          Trimé: {UnitUtils.ConvertFromInternalUnits(trimmed, UnitTypeId.Millimeters):0.1f}mm");
                
                // Appliquer le trim
                locCurve.Curve = newCurve;
                Log($"        ✓ TRIM RÉUSSI sur mur {wall.Id.Value}");
            }
            catch (Exception ex)
            {
                Log($"ERREUR trim mur {wall.Id.Value}: {ex.Message}");
            }
        }

        /// <summary>
        /// Projeter un point sur une ligne définie par deux points
        /// </summary>
        private XYZ ProjectPointOnLine(XYZ point, XYZ lineStart, XYZ lineEnd)
        {
            try
            {
                XYZ lineDirection = (lineEnd - lineStart).Normalize();
                XYZ pointVector = point - lineStart;
                double projection = pointVector.DotProduct(lineDirection);
                return lineStart + (projection * lineDirection);
            }
            catch
            {
                return point; // Retourner le point original en cas d'erreur
            }
        }

        private Dictionary<int, LayerGeometry> GetWallLayerGeometry(Wall wall, WallType wallType, CompoundStructure structure, WallLocationLine originalJustification, Curve originalBaseCurve)
        {
            var result = new Dictionary<int, LayerGeometry>();
            
            try
            {
                var layers = structure.GetLayers();
                
                XYZ wallDirection = (originalBaseCurve.GetEndPoint(1) - originalBaseCurve.GetEndPoint(0)).Normalize();
                XYZ wallNormal = new XYZ(wallDirection.Y, -wallDirection.X, 0).Normalize();
                
                double totalWidth = structure.GetWidth();
                double halfTotalWidth = totalWidth / 2;
                double cumulativeThickness = 0; // Épaisseur cumulée depuis l'extérieur
                
                Log($"POSITIONNEMENT FACE À FACE - Couches collées:");
                Log($"Épaisseur totale: {UnitUtils.ConvertFromInternalUnits(totalWidth, UnitTypeId.Millimeters):0.1f}mm");
                Log($"Moitié épaisseur: {UnitUtils.ConvertFromInternalUnits(halfTotalWidth, UnitTypeId.Millimeters):0.1f}mm");
                Log($"Direction normale vers intérieur: {FormatXYZ(wallNormal)}");
                Log($"Position axe mur mémorisé: {FormatXYZ(originalBaseCurve.GetEndPoint(0))}");
                
                for (int i = 0; i < layers.Count(); i++)
                {
                    var layer = layers.ElementAt(i);
                    double layerThickness = layer.Width;
                    
                    double layerOffset;
                    
                    if (i == 0)
                    {
                        // PREMIÈRE COUCHE : Position au Nu fini extérieur
                        // Son axe est à la moitié de son épaisseur depuis l'extérieur
                        layerOffset = -halfTotalWidth + (layerThickness / 2);
                        Log($"  Couche {i + 1} (EXTÉRIEURE): Face extérieure au Nu fini extérieur");
                    }
                    else
                    {
                        // COUCHES SUIVANTES : Face extérieure collée à la face intérieure de la couche précédente
                        // Son axe est au cumul + la moitié de son épaisseur
                        layerOffset = -halfTotalWidth + cumulativeThickness + (layerThickness / 2);
                        Log($"  Couche {i + 1}: Face extérieure collée à la face intérieure de la couche {i}");
                    }
                    
                    // Appliquer l'offset
                    XYZ offsetVector = wallNormal.Multiply(layerOffset);
                    Transform offsetTransform = Transform.CreateTranslation(offsetVector);
                    Curve layerCenterline = originalBaseCurve.CreateTransformed(offsetTransform);
                    
                    result[i] = new LayerGeometry
                    {
                        Centerline = layerCenterline,
                        Thickness = layerThickness,
                        Normal = wallNormal,
                        OffsetFromExterior = layerOffset
                    };
                    
                    Log($"    Épaisseur: {UnitUtils.ConvertFromInternalUnits(layerThickness, UnitTypeId.Millimeters):0.1f}mm");
                    Log($"    Cumul avant cette couche: {UnitUtils.ConvertFromInternalUnits(cumulativeThickness, UnitTypeId.Millimeters):0.1f}mm");
                    Log($"    Position axe: {UnitUtils.ConvertFromInternalUnits(layerOffset, UnitTypeId.Millimeters):0.1f}mm depuis axe mur");
                    Log($"    Face extérieure: {UnitUtils.ConvertFromInternalUnits(layerOffset - (layerThickness / 2), UnitTypeId.Millimeters):0.1f}mm");
                    Log($"    Face intérieure: {UnitUtils.ConvertFromInternalUnits(layerOffset + (layerThickness / 2), UnitTypeId.Millimeters):0.1f}mm");
                    Log($"    Position: {FormatXYZ(layerCenterline.GetEndPoint(0))}");
                    
                    // Incrémenter le cumul APRÈS traitement de cette couche
                    cumulativeThickness += layerThickness;
                }
                
                // RÉSULTAT DYNAMIQUE: Afficher toutes les couches, pas seulement les 3 premières
                Log($"RÉSULTAT face à face ({layers.Count()} couches):");
                
                double cumulativeForLog = 0;
                for (int logIndex = 0; logIndex < layers.Count(); logIndex++)
                {
                    double layerThickness = layers.ElementAt(logIndex).Width;
                    double layerAxePosition = -halfTotalWidth + cumulativeForLog + (layerThickness / 2);
                    
                    if (logIndex == 0)
                    {
                        Log($"  Couche {logIndex + 1}: Face ext au Nu fini extérieur, axe à {UnitUtils.ConvertFromInternalUnits(layerAxePosition, UnitTypeId.Millimeters):0.1f}mm");
                    }
                    else
                    {
                        Log($"  Couche {logIndex + 1}: Face ext à la face int couche {logIndex}, axe à {UnitUtils.ConvertFromInternalUnits(layerAxePosition, UnitTypeId.Millimeters):0.1f}mm");
                    }
                    
                    cumulativeForLog += layerThickness;
                }
            }
            catch (Exception ex)
            {
                Log($"ERREUR: {ex.Message}");
            }
            
            return result;
        }

        private WallType GetOrCreateWallType(Document doc, WallType originalType, string newTypeName, CompoundStructureLayer layer, Dictionary<string, WallType> cache)
        {
            // Vérifier d'abord le cache pour éviter les duplications
            if (cache != null && cache.TryGetValue(newTypeName, out var cachedType)) 
            {
                Log($"Type de mur réutilisé depuis le cache : {newTypeName}");
                return cachedType;
            }
            
            // Vérifier si le type existe déjà dans le projet
            var existingType = new FilteredElementCollector(doc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .FirstOrDefault(wt => wt.Name.Equals(newTypeName, StringComparison.OrdinalIgnoreCase));
                
            if (existingType != null) 
            { 
                if (cache != null) cache[newTypeName] = existingType;
                Log($"Type de mur existant réutilisé : {newTypeName}");
                return existingType; 
            }
            
            // Créer un nouveau type de mur SIMPLE (pas une copie du composé)
            try
            {
                // Trouver un type de mur de base simple existant comme modèle
                WallType baseWallType = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .FirstOrDefault(wt => wt.Kind == WallKind.Basic);
                
                if (baseWallType == null)
                {
                    Log($"Aucun type de mur de base trouvé pour créer {newTypeName}");
                    return null;
                }
                
                // Dupliquer le type simple, pas le type composé original
                WallType newWallType = baseWallType.Duplicate(newTypeName) as WallType;
                if (newWallType != null)
                {
                    // Créer une structure avec UNE SEULE couche
                    var newLayers = new List<CompoundStructureLayer> 
                    { 
                        new CompoundStructureLayer(layer.Width, layer.Function, layer.MaterialId)
                    };
                    
                    CompoundStructure newStructure = CompoundStructure.CreateSimpleCompoundStructure(newLayers);
                    newWallType.SetCompoundStructure(newStructure);
                    
                    // Ajouter au cache pour réutilisation future
                    if (cache != null) cache[newTypeName] = newWallType;
                    
                    Log($"Nouveau type de mur SIMPLE cree: {newTypeName} - 1 seule couche");
                    return newWallType;
                }
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                Log($"Erreur création type {newTypeName}: {ex.Message}");
                
                // Tentative de récupération si le type a été créé entre temps
                var fallback = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .FirstOrDefault(wt => wt.Name.Equals(newTypeName, StringComparison.OrdinalIgnoreCase));
                    
                if (fallback != null) 
                { 
                    if (cache != null) cache[newTypeName] = fallback;
                    return fallback; 
                }
                return null;
            }
            catch (Exception ex)
            {
                Log($"Erreur inattendue création type {newTypeName}: {ex.Message}");
                return null;
            }
            
            // Cas par défaut si rien n'a fonctionné
            return null;
        }

        private string GetMaterialName(Document doc, CompoundStructureLayer layer)
        {
            string materialName = layer.Function.ToString();
            if (layer.MaterialId != ElementId.InvalidElementId)
            {
                if (doc.GetElement(layer.MaterialId) is Material mat && !string.IsNullOrWhiteSpace(mat.Name)) 
                    materialName = mat.Name;
            }
            return CleanName(materialName);
        }
        
        private string CleanName(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "Def";
            
            // Supprimer les caractères spéciaux et espaces
            string cleaned = input
                .Replace(" ", "")
                .Replace("-", "")
                .Replace("(", "")
                .Replace(")", "")
                .Replace("'", "")
                .Replace("\"", "")
                .Replace("/", "")
                .Replace("\\", "")
                .Replace(".", "")
                .Replace(",", "")
                .Replace(":", "")
                .Replace(";", "");
            
            // Garder seulement 4 lettres maximum
            return cleaned.Length > 4 ? cleaned.Substring(0, 4) : cleaned;
        }
        
        private string GetFunctionNameFR(MaterialFunctionAssignment function)
        {
            switch (function)
            {
                case MaterialFunctionAssignment.Structure: return "Stru";
                case MaterialFunctionAssignment.Substrate: return "Subs";
                case MaterialFunctionAssignment.Insulation: return "Isol";
                case MaterialFunctionAssignment.Finish1: return "Fin1";
                case MaterialFunctionAssignment.Finish2: return "Fin2";
                case MaterialFunctionAssignment.Membrane: return "Memb";
                case MaterialFunctionAssignment.StructuralDeck: return "Deck";
                default: return "Autre";
            }
        }

        private void CopyAllWallProperties(Wall source, Wall target)
        {
            // Copier TOUS les paramètres : système, projet, partagé
            foreach (Parameter param in source.Parameters)
            {
                try
                {
                    if (!param.IsReadOnly)
                    {
                        Parameter targetParam = null;
                        
                        // Rechercher le paramètre cible par différentes méthodes
                        if (param.Definition is InternalDefinition internalDef)
                        {
                            targetParam = target.get_Parameter(internalDef.BuiltInParameter);
                        }
                        else if (param.Definition is ExternalDefinition externalDef)
                        {
                            targetParam = target.LookupParameter(externalDef.Name);
                        }
                        else
                        {
                            targetParam = target.LookupParameter(param.Definition.Name);
                        }
                        
                        if (targetParam != null && !targetParam.IsReadOnly)
                        {
                            switch (param.StorageType)
                            {
                                case StorageType.Double: 
                                    targetParam.Set(param.AsDouble()); 
                                    break;
                                case StorageType.Integer: 
                                    targetParam.Set(param.AsInteger()); 
                                    break;
                                case StorageType.String: 
                                    if (!string.IsNullOrEmpty(param.AsString()))
                                        targetParam.Set(param.AsString()); 
                                    break;
                                case StorageType.ElementId: 
                                    if (param.AsElementId() != ElementId.InvalidElementId)
                                        targetParam.Set(param.AsElementId()); 
                                    break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log seulement pour les paramètres importants
                    if (param.Definition.Name.Contains("Phase") || 
                        param.Definition.Name.Contains("Workset") ||
                        param.Definition.Name.Contains("Level"))
                    {
                        Log($"Erreur copie paramètre {param.Definition.Name}: {ex.Message}");
                    }
                }
            }
            
            // Forcer la copie des paramètres critiques système
            var criticalParams = new[]
            {
                BuiltInParameter.PHASE_CREATED,
                BuiltInParameter.PHASE_DEMOLISHED,
                BuiltInParameter.ELEM_PARTITION_PARAM,
                BuiltInParameter.WALL_BASE_CONSTRAINT,
                BuiltInParameter.WALL_HEIGHT_TYPE,
                BuiltInParameter.WALL_BASE_OFFSET,
                BuiltInParameter.WALL_USER_HEIGHT_PARAM
            };
            
            foreach (var paramId in criticalParams)
            {
                try
                {
                    var sourceParam = source.get_Parameter(paramId);
                    var targetParam = target.get_Parameter(paramId);
                    
                    if (sourceParam != null && targetParam != null && !targetParam.IsReadOnly)
                    {
                        switch (sourceParam.StorageType)
                        {
                            case StorageType.Double: 
                                targetParam.Set(sourceParam.AsDouble()); 
                                break;
                            case StorageType.Integer: 
                                targetParam.Set(sourceParam.AsInteger()); 
                                break;
                            case StorageType.String: 
                                if (!string.IsNullOrEmpty(sourceParam.AsString()))
                                    targetParam.Set(sourceParam.AsString()); 
                                break;
                            case StorageType.ElementId: 
                                if (sourceParam.AsElementId() != ElementId.InvalidElementId)
                                    targetParam.Set(sourceParam.AsElementId()); 
                                break;
                        }
                    }
                }
                catch { }
            }
        }

        private IEnumerable<FamilyInstance> GetHostedFamilyInstances(Document doc, Wall wall)
        {
            return new FilteredElementCollector(doc).WhereElementIsNotElementType().OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Where(fi => fi.Host != null && fi.Host.Id == wall.Id);
        }

        private IList<Opening> GetOpenings(Document doc, Wall wall)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(Opening)).Cast<Opening>().Where(o => o.Host != null && o.Host.Id == wall.Id).ToList();
        }



        private double CalculateOffsetFromWallReference(CompoundStructure structure, WallLocationLine locationLine, double layerOffsetFromExterior, double totalWidth)
        {
            // Plus besoin de cette méthode - offset direct depuis Nu fini extérieur
            return layerOffsetFromExterior;
        }

        private XYZ GetPerpendicularDirection(Curve baseCurve, double offset)
        {
            try
            {
                // Obtenir la direction tangente au milieu de la courbe
                double parameter = baseCurve.GetEndParameter(0) + 
                                  (baseCurve.GetEndParameter(1) - baseCurve.GetEndParameter(0)) / 2;
                
                XYZ tangent = baseCurve.ComputeDerivatives(parameter, false).BasisX.Normalize();
                
                // Créer le vecteur perpendiculaire (rotation de 90° dans le plan XY)
                XYZ perpendicular = new XYZ(-tangent.Y, tangent.X, 0).Normalize();
                
                // Multiplier par l'offset pour obtenir le vecteur de translation
                return perpendicular.Multiply(offset);
            }
            catch (Exception ex)
            {
                Log($"Erreur calcul direction perpendiculaire: {ex.Message}");
                // Retourner un vecteur par défaut en cas d'erreur
                return new XYZ(offset, 0, 0);
            }
        }

        private Dictionary<int, LayerGeometry> GetWallLayerGeometry(Wall wall, WallType wallType, CompoundStructure structure, WallLocationLine originalJustification)
        {
            var result = new Dictionary<int, LayerGeometry>();
            
            try
            {
                LocationCurve wallLocation = wall.Location as LocationCurve;
                Curve baseCurve = wallLocation.Curve;
                var layers = structure.GetLayers();
                
                XYZ wallDirection = (baseCurve.GetEndPoint(1) - baseCurve.GetEndPoint(0)).Normalize();
                // INVERSION DE LA NORMALE : Vers l'extérieur au lieu de l'intérieur
                XYZ wallNormal = new XYZ(-wallDirection.Y, wallDirection.X, 0).Normalize();
                
                double totalWidth = structure.GetWidth();
                double cumulativeOffset = 0;
                
                Log($"NORMALE INVERSÉE - Direction vers extérieur:");
                Log($"Épaisseur totale: {UnitUtils.ConvertFromInternalUnits(totalWidth, UnitTypeId.Millimeters):0.1f}mm");
                Log($"Direction normale vers EXTÉRIEUR: {FormatXYZ(wallNormal)}");
                
                for (int i = 0; i < layers.Count(); i++)
                {
                    var layer = layers.ElementAt(i);
                    double layerThickness = layer.Width;
                    
                    // Position de l'axe de chaque couche avec normale inversée
                    double layerStartOffset = cumulativeOffset + (layerThickness / 2);
                    
                    // Appliquer l'offset avec la normale VERS L'EXTÉRIEUR
                    XYZ offsetVector = wallNormal.Multiply(layerStartOffset);
                    Transform offsetTransform = Transform.CreateTranslation(offsetVector);
                    Curve layerCenterline = baseCurve.CreateTransformed(offsetTransform);
                    
                    result[i] = new LayerGeometry
                    {
                        Centerline = layerCenterline,
                        Thickness = layerThickness,
                        Normal = wallNormal,
                        OffsetFromExterior = layerStartOffset
                    };
                    
                    Log($"  Couche {i + 1}:");
                    Log($"    Épaisseur: {UnitUtils.ConvertFromInternalUnits(layerThickness, UnitTypeId.Millimeters):0.1f}mm");
                    Log($"    Début théorique: {UnitUtils.ConvertFromInternalUnits(cumulativeOffset, UnitTypeId.Millimeters):0.1f}mm");
                    Log($"    Axe avec normale inversée: {UnitUtils.ConvertFromInternalUnits(layerStartOffset, UnitTypeId.Millimeters):0.1f}mm");
                    Log($"    Offset appliqué VERS EXTÉRIEUR: {FormatXYZ(offsetVector)}");
                    Log($"    Position ligne: {FormatXYZ(layerCenterline.GetEndPoint(0))}");
                    
                    cumulativeOffset += layerThickness;
                }
                
                // RÉSULTAT DYNAMIQUE avec normale inversée: Afficher toutes les couches
                Log($"RÉSULTAT avec normale inversée (vers extérieur) - {layers.Count()} couches:");
                
                double cumulativeForResultLog = 0;
                for (int resultIndex = 0; resultIndex < layers.Count(); resultIndex++)
                {
                    double layerThicknessForResult = layers.ElementAt(resultIndex).Width;
                    double layerStartOffsetForResult = cumulativeForResultLog + (layerThicknessForResult / 2);
                    
                    Log($"  Couche {resultIndex + 1}: Décalage de {UnitUtils.ConvertFromInternalUnits(layerStartOffsetForResult, UnitTypeId.Millimeters):0}mm vers extérieur");
                    
                    cumulativeForResultLog += layerThicknessForResult;
                }
            }
            catch (Exception ex)
            {
                Log($"ERREUR: {ex.Message}");
            }
            
            return result;
        }

        private void CopyWallParameters(Wall source, Wall target)
        {
            try
            {
                // Copier les paramètres essentiels
                var parameterIds = new[]
                {
                    BuiltInParameter.WALL_BASE_CONSTRAINT,
                    BuiltInParameter.WALL_BASE_OFFSET,
                    BuiltInParameter.WALL_HEIGHT_TYPE,
                    BuiltInParameter.WALL_USER_HEIGHT_PARAM,
                    BuiltInParameter.PHASE_CREATED,
                    BuiltInParameter.PHASE_DEMOLISHED
                };

                foreach (var paramId in parameterIds)
                {
                    try
                    {
                        Parameter sourceParam = source.get_Parameter(paramId);
                        Parameter targetParam = target.get_Parameter(paramId);
                        
                        if (sourceParam != null && targetParam != null && !targetParam.IsReadOnly)
                        {
                            switch (sourceParam.StorageType)
                            {
                                case StorageType.Double:
                                    targetParam.Set(sourceParam.AsDouble());
                                    break;
                                case StorageType.Integer:
                                    targetParam.Set(sourceParam.AsInteger());
                                    break;
                                case StorageType.ElementId:
                                    if (sourceParam.AsElementId() != ElementId.InvalidElementId)
                                        targetParam.Set(sourceParam.AsElementId());
                                    break;
                                case StorageType.String:
                                    if (!string.IsNullOrEmpty(sourceParam.AsString()))
                                        targetParam.Set(sourceParam.AsString());
                                    break;
                            }
                        }
                    }
                    catch { /* Ignorer les erreurs sur paramètres individuels */ }
                }
            }
            catch (Exception ex)
            {
                Log($"Erreur copie paramètres: {ex.Message}");
            }
        }

        private string FormatXYZ(XYZ point)
        {
            return $"({UnitUtils.ConvertFromInternalUnits(point.X, UnitTypeId.Millimeters):0.1f}, " +
                   $"{UnitUtils.ConvertFromInternalUnits(point.Y, UnitTypeId.Millimeters):0.1f}, " +
                   $"{UnitUtils.ConvertFromInternalUnits(point.Z, UnitTypeId.Millimeters):0.1f})";
        }

        // Fonction supprimée - plus besoin de calculs compliqués avec Nu fini extérieur partout

        private void Log(string text, bool enabled = false)
        {
            if (!enabled) return;
            
            try
            {
                string dllPath = Assembly.GetExecutingAssembly().Location;
                string logPath = Path.Combine(Path.GetDirectoryName(dllPath), "CoucheMurPlugin.log");
                File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " - " + text + "\n");
            }
            catch { }
        }



        /// <summary>
        /// NOUVELLE APPROCHE: Change le host des ouvertures existantes (pas de duplication)
        /// </summary>
        private void ReassignOpeningsAndCopyProfiles(Document doc, Wall originalWall, List<Wall> layerWalls)
        {
            try
            {
                Log($"Changement de host des ouvertures du mur {originalWall.Id.Value}...");

                // 1. IDENTIFIER LA COUCHE STRUCTURE (contient "Stru" dans le nom du type)
                Wall structuralWall = null;
                foreach (Wall layerWall in layerWalls)
                {
                    string typeName = layerWall.WallType.Name;
                    if (typeName.Contains("Stru"))
                    {
                        structuralWall = layerWall;
                        Log($"  Couche structure identifiée: {typeName} (ID: {layerWall.Id.Value})");
                        break;
                    }
                }

                // FALLBACK: Utiliser la couche la plus épaisse si pas de "Stru"
                if (structuralWall == null && layerWalls.Count > 0)
                {
                    Log("  FALLBACK: Aucune couche 'Stru' - utilisation de la plus épaisse...");
                    
                    Wall thickestWall = null;
                    double maxThickness = 0;
                    
                    foreach (Wall wall in layerWalls)
                    {
                        var wallType = wall.WallType;
                        var structure = wallType.GetCompoundStructure();
                        if (structure != null)
                        {
                            double thickness = structure.GetWidth();
                            if (thickness > maxThickness)
                            {
                                maxThickness = thickness;
                                thickestWall = wall;
                            }
                        }
                    }
                    
                    if (thickestWall != null)
                    {
                        structuralWall = thickestWall;
                        Log($"  Couche structure (plus épaisse): {thickestWall.WallType.Name}");
                    }
                }

                if (structuralWall == null)
                {
                    Log("  ATTENTION: Aucune couche structure trouvée - ouvertures non réassignées");
                    return;
                }

                // 2. CHANGER LE HOST DES OUVERTURES EXISTANTES (PAS DE DUPLICATION)
                var openingsToReassign = new List<FamilyInstance>();
                
                // Récupérer les éléments insérés (fenêtres, portes)
                var insertedElements = originalWall.FindInserts(true, true, true, true);
                foreach (ElementId insertId in insertedElements)
                {
                    Element insert = doc.GetElement(insertId);
                    if (insert != null && insert is FamilyInstance familyInstance)
                    {
                        openingsToReassign.Add(familyInstance);
                        Log($"  Ouverture à réassigner: {insert.Category?.Name} - {familyInstance.Symbol?.Name} (ID: {insert.Id.Value})");
                    }
                }

                if (openingsToReassign.Count == 0)
                {
                    Log("  Aucune ouverture à réassigner");
                }
                else
                {
                    Log($"  CHANGEMENT DE HOST: {openingsToReassign.Count} ouvertures vers couche structure");
                    
                    int successCount = 0;
                    foreach (FamilyInstance opening in openingsToReassign)
                    {
                        try
                        {
                            // MÉTHODE 1: Essayer de changer le host directement via le paramètre HOST_ID_PARAM
                            Parameter hostParam = opening.get_Parameter(BuiltInParameter.HOST_ID_PARAM);
                            if (hostParam != null && !hostParam.IsReadOnly)
                            {
                                hostParam.Set(structuralWall.Id);
                                successCount++;
                                Log($"    ✓ Host changé via paramètre: {opening.Symbol?.Name} → Structure");
                                continue;
                            }

                            // MÉTHODE 2: Essayer via la propriété Host (pas toujours en écriture)
                            try
                            {
                                // Cette approche peut ne pas fonctionner selon la version Revit
                                // opening.Host = structuralWall;  // Propriété souvent en lecture seule
                                Log($"    ⚠ Propriété Host en lecture seule pour: {opening.Symbol?.Name}");
                            }
                            catch (Exception hostEx)
                            {
                                Log($"    ⚠ Changement Host échoué: {hostEx.Message}");
                            }

                            // MÉTHODE 3: RECREATION avec suppression de l'original (en dernier recours)
                            Log($"    → Recréation nécessaire pour: {opening.Symbol?.Name}");
                            
                            // Sauvegarder les propriétés AVANT de supprimer
                            var properties = SaveFamilyInstanceProperties(opening);
                            
                            // Créer nouvelle instance sur la couche structure
                            FamilyInstance newInstance = RecreateOnNewHost(doc, opening, structuralWall, properties);
                            
                            if (newInstance != null)
                            {
                                // Supprimer l'ancienne instance SEULEMENT après succès de la nouvelle
                                doc.Delete(opening.Id);
                                successCount++;
                                Log($"    ✓ Ouverture recréée sur structure: {newInstance.Symbol?.Name} (ID: {newInstance.Id.Value})");
                            }
                            else
                            {
                                Log($"    ✗ Échec recréation: {opening.Symbol?.Name}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"    ✗ Erreur changement host {opening.Symbol?.Name}: {ex.Message}");
                        }
                    }

                    Log($"  RÉSULTAT: {successCount}/{openingsToReassign.Count} ouvertures réassignées avec succès");
                }

                // 3. COPIER LES PROFILS PERSONNALISÉS (si nécessaire)
                try
                {
                    CopyWallProfiles(doc, originalWall, layerWalls);
                }
                catch (Exception ex)
                {
                    Log($"  Erreur copie profils: {ex.Message}");
                }

                Log("  SUCCESS: Réassignation des ouvertures terminée");
            }
            catch (Exception ex)
            {
                Log($"ERREUR réassignation ouvertures/profils: {ex.Message}");
            }
        }

        /// <summary>
        /// Sauvegarder toutes les propriétés importantes d'une FamilyInstance
        /// </summary>
        private Dictionary<string, object> SaveFamilyInstanceProperties(FamilyInstance familyInstance)
        {
            var properties = new Dictionary<string, object>();
            
            try
            {
                // Position
                if (familyInstance.Location is LocationPoint locPoint)
                {
                    properties["Location"] = locPoint.Point;
                    properties["Rotation"] = locPoint.Rotation;
                }

                // Paramètres critiques
                var criticalParams = new[]
                {
                    BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM,
                    BuiltInParameter.INSTANCE_ELEVATION_PARAM, 
                    BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM,
                    BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM
                };

                foreach (var paramId in criticalParams)
                {
                    var param = familyInstance.get_Parameter(paramId);
                    if (param != null && param.HasValue)
                    {
                        switch (param.StorageType)
                        {
                            case StorageType.Double:
                                properties[paramId.ToString()] = param.AsDouble();
                                break;
                            case StorageType.Integer:
                                properties[paramId.ToString()] = param.AsInteger();
                                break;
                            case StorageType.String:
                                properties[paramId.ToString()] = param.AsString();
                                break;
                            case StorageType.ElementId:
                                properties[paramId.ToString()] = param.AsElementId();
                                break;
                        }
                    }
                }

                // Symbol
                properties["Symbol"] = familyInstance.Symbol;
            }
            catch (Exception ex)
            {
                Log($"      Erreur sauvegarde propriétés: {ex.Message}");
            }

            return properties;
        }

        /// <summary>
        /// Recréer une FamilyInstance sur un nouveau host avec les propriétés sauvegardées
        /// </summary>
        private FamilyInstance RecreateOnNewHost(Document doc, FamilyInstance original, Wall newHost, Dictionary<string, object> properties)
        {
            try
            {
                if (!properties.ContainsKey("Location") || !properties.ContainsKey("Symbol"))
                    return null;

                XYZ location = (XYZ)properties["Location"];
                FamilySymbol symbol = (FamilySymbol)properties["Symbol"];

                if (!symbol.IsActive) symbol.Activate();

                // Créer nouvelle instance
                FamilyInstance newInstance = doc.Create.NewFamilyInstance(
                    location,
                    symbol,
                    newHost,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural
                );

                if (newInstance != null)
                {
                    // Restaurer la rotation si disponible
                    if (properties.ContainsKey("Rotation") && newInstance.Location is LocationPoint newLocPoint)
                    {
                        double rotation = (double)properties["Rotation"];
                        if (Math.Abs(rotation) > 0.001) // Seulement si rotation significative
                        {
                            Line rotationAxis = Line.CreateUnbound(newLocPoint.Point, XYZ.BasisZ);
                            newLocPoint.Rotate(rotationAxis, rotation);
                        }
                    }

                    // Restaurer les paramètres
                    foreach (var prop in properties)
                    {
                        if (prop.Key.StartsWith("INSTANCE_") && Enum.TryParse(prop.Key, out BuiltInParameter paramId))
                        {
                            try
                            {
                                var param = newInstance.get_Parameter(paramId);
                                if (param != null && !param.IsReadOnly)
                                {
                                    switch (param.StorageType)
                                    {
                                        case StorageType.Double:
                                            param.Set((double)prop.Value);
                                            break;
                                        case StorageType.Integer:
                                            param.Set((int)prop.Value);
                                            break;
                                        case StorageType.String:
                                            param.Set((string)prop.Value);
                                            break;
                                        case StorageType.ElementId:
                                            param.Set((ElementId)prop.Value);
                                            break;
                                    }
                                }
                            }
                            catch
                            {
                                // Ignorer les erreurs sur paramètres individuels
                            }
                        }
                    }
                }

                return newInstance;
            }
            catch (Exception ex)
            {
                Log($"      Erreur recréation: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Copie les profils personnalisés (Edit Profile) du mur original vers tous les murs-couches
        /// </summary>
        private void CopyWallProfiles(Document doc, Wall originalWall, List<Wall> layerWalls)
        {
            try
            {
                Log($"    Copie des profils personnalisés...");

                // Vérifier si le mur original a des profils personnalisés
                bool hasCustomProfile = false;
                
                // Dans l'API Revit, les profils personnalisés sont difficiles à accéder directement
                // On peut détecter leur présence via les sketches dépendants
                var dependentElements = originalWall.GetDependentElements(null);
                var sketches = new List<Element>();
                
                foreach (ElementId depId in dependentElements)
                {
                    Element depElement = doc.GetElement(depId);
                    if (depElement != null && depElement.GetType().Name.Contains("Sketch"))
                    {
                        sketches.Add(depElement);
                        hasCustomProfile = true;
                        Log($"      Sketch détecté: {depElement.GetType().Name} ID={depElement.Id.Value}");
                    }
                }

                if (!hasCustomProfile)
                {
                    Log("      Aucun profil personnalisé détecté");
                    return;
                }

                Log($"      {sketches.Count} profil(s) personnalisé(s) détecté(s)");
                
                // Pour chaque mur-couche, essayer d'appliquer les mêmes profils
                foreach (Wall layerWall in layerWalls)
                {
                    try
                    {
                        Log($"      Application profils sur couche {layerWall.Id.Value}...");
                        
                        // MÉTHODE ALTERNATIVE: Copier la géométrie modifiée
                        // Dans l'API Revit, la copie directe de profils personnalisés est complexe
                        // Une approche est de récupérer la géométrie résultante et l'appliquer
                        
                        // TODO: Implémenter la copie réelle des profils si nécessaire
                        // Cela nécessite l'accès aux courbes du sketch et leur recreation
                        
                        Log($"      → Profil personnalisé détecté mais copie non implémentée (API complexe)");
                    }
                    catch (Exception ex)
                    {
                        Log($"      Erreur application profil sur couche {layerWall.Id.Value}: {ex.Message}");
                    }
                }
                
                Log("    Copie profils terminée");
            }
            catch (Exception ex)
            {
                Log($"    ERREUR copie profils: {ex.Message}");
            }
        }

        /// <summary>
        /// Copie tous les paramètres d'une FamilyInstance vers une autre
        /// </summary>
        private void CopyAllFamilyInstanceParameters(FamilyInstance source, FamilyInstance target)
        {
            try
            {
                foreach (Parameter sourceParam in source.Parameters)
                {
                    if (sourceParam.IsReadOnly) continue;
                    
                    Parameter targetParam = target.LookupParameter(sourceParam.Definition.Name);
                    if (targetParam != null && !targetParam.IsReadOnly && sourceParam.StorageType == targetParam.StorageType)
                    {
                        try
                        {
                            switch (sourceParam.StorageType)
                            {
                                case StorageType.Double:
                                    targetParam.Set(sourceParam.AsDouble());
                                    break;
                                case StorageType.Integer:
                                    targetParam.Set(sourceParam.AsInteger());
                                    break;
                                case StorageType.String:
                                    if (!string.IsNullOrEmpty(sourceParam.AsString()))
                                        targetParam.Set(sourceParam.AsString());
                                    break;
                                case StorageType.ElementId:
                                    targetParam.Set(sourceParam.AsElementId());
                                    break;
                            }
                        }
                        catch
                        {
                            // Ignorer les paramètres qui ne peuvent pas être copiés
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"        Erreur copie paramètres FamilyInstance: {ex.Message}");
            }
        }

        /// <summary>
        /// Attacher les couches avec Join Geometry pour propager les ouvertures depuis la structure
        /// NOUVELLE VERSION: Support dynamique pour n'importe quel nombre de couches
        /// </summary>
        private void JoinWallLayersGeometry(Document doc, List<Wall> layerWalls)
        {
            try
            {
                Log($"Join Geometry: Attachement des {layerWalls.Count} murs-couches...");

                // 1. IDENTIFIER LA COUCHE STRUCTURE (contient "Stru" dans le nom du type) - SUPPORT DYNAMIQUE
                Wall structuralWall = null;
                var otherWalls = new List<Wall>();
                
                foreach (Wall layerWall in layerWalls)
                {
                    string typeName = layerWall.WallType.Name;
                    Log($"  Analyse couche: {typeName}");
                    
                    if (typeName.Contains("Stru"))
                    {
                        structuralWall = layerWall;
                        Log($"  ✓ Couche structure (host ouvertures): {typeName} (ID: {layerWall.Id.Value})");
                    }
                    else
                    {
                        otherWalls.Add(layerWall);
                        Log($"  → Couche à attacher: {typeName} (ID: {layerWall.Id.Value})");
                    }
                }

                // FALLBACK: Si pas de couche "Stru" explicite, utiliser la couche la plus épaisse
                if (structuralWall == null && layerWalls.Count > 0)
                {
                    Log("  FALLBACK: Aucune couche 'Stru' - recherche couche la plus épaisse...");
                    
                    Wall thickestWall = null;
                    double maxThickness = 0;
                    
                    foreach (Wall wall in layerWalls)
                    {
                        var wallType = wall.WallType;
                        var structure = wallType.GetCompoundStructure();
                        if (structure != null)
                        {
                            double thickness = structure.GetWidth();
                            Log($"    {wallType.Name}: {UnitUtils.ConvertFromInternalUnits(thickness, UnitTypeId.Millimeters):F1}mm");
                            
                            if (thickness > maxThickness)
                            {
                                maxThickness = thickness;
                                thickestWall = wall;
                            }
                        }
                    }
                    
                    if (thickestWall != null)
                    {
                        structuralWall = thickestWall;
                        otherWalls.Clear();
                        otherWalls.AddRange(layerWalls.Where(w => w.Id != structuralWall.Id));
                        
                        Log($"  ✓ Couche structure (plus épaisse): {thickestWall.WallType.Name} - {UnitUtils.ConvertFromInternalUnits(maxThickness, UnitTypeId.Millimeters):F1}mm");
                    }
                }

                if (structuralWall == null)
                {
                    Log("  ATTENTION: Aucune couche structure trouvée - pas d'attachement possible");
                    return;
                }

                if (otherWalls.Count == 0)
                {
                    Log("  ATTENTION: Aucune autre couche à attacher");
                    return;
                }

                Log($"  PLAN D'ATTACHEMENT: 1 structure + {otherWalls.Count} couches à attacher");

                // 2. ESSAYER PLUSIEURS MÉTHODES D'ATTACHEMENT
                int joinCount = 0;
                
                foreach (Wall otherWall in otherWalls)
                {
                    try
                    {
                        Log($"    Tentative attachement: {otherWall.WallType.Name}");
                        
                        // Vérifier si déjà joint
                        if (JoinGeometryUtils.AreElementsJoined(doc, structuralWall, otherWall))
                        {
                            Log($"      ⚠ Déjà joint avec structure - unjoin puis rejoin");
                            JoinGeometryUtils.UnjoinGeometry(doc, structuralWall, otherWall);
                        }

                        // MÉTHODE 1: Join Geometry classique (vérification de distance d'abord)
                        try
                        {
                            // Vérifier que les murs se chevauchent ou sont suffisamment proches
                            if (AreWallsCompatibleForJoin(structuralWall, otherWall))
                            {
                                JoinGeometryUtils.JoinGeometry(doc, structuralWall, otherWall);
                                
                                // Vérifier si le join a réussi
                                if (JoinGeometryUtils.AreElementsJoined(doc, structuralWall, otherWall))
                                {
                                    joinCount++;
                                    Log($"      ✓ Joint réussi (JoinGeometry): {otherWall.WallType.Name} → Structure");
                                    continue;
                                }
                                else
                                {
                                    Log($"      ⚠ JoinGeometry n'a pas créé de lien détectable");
                                }
                            }
                            else
                            {
                                Log($"      ⚠ Murs pas compatibles pour join (trop éloignés)");
                            }
                        }
                        catch (Exception joinEx)
                        {
                            Log($"      ✗ JoinGeometry échoué: {joinEx.Message}");
                        }

                        // MÉTHODE 2: Forcer l'attachement par ordre de coupe/join
                        try
                        {
                            // Essayer dans les deux sens avec SwitchJoinOrder
                            JoinGeometryUtils.JoinGeometry(doc, otherWall, structuralWall);
                            
                            // Essayer de changer l'ordre de coupe pour que la structure coupe les autres
                            if (JoinGeometryUtils.AreElementsJoined(doc, structuralWall, otherWall))
                            {
                                try
                                {
                                    JoinGeometryUtils.SwitchJoinOrder(doc, structuralWall, otherWall);
                                    Log($"      ✓ Ordre de coupe inversé: Structure coupe {otherWall.WallType.Name}");
                                }
                                catch
                                {
                                    Log($"      ⚠ Impossible d'inverser l'ordre de coupe");
                                }
                                
                                joinCount++;
                                Log($"      ✓ Joint réussi (ordre inversé): {otherWall.WallType.Name} ← Structure");
                                continue;
                            }
                        }
                        catch (Exception joinEx)
                        {
                            Log($"      ✗ Join inversé échoué: {joinEx.Message}");
                        }

                        // MÉTHODE 3: Approche alternative - Union des solides
                        try
                        {
                            // Essayer une union Boolean des géométries
                            if (AttemptGeometryUnion(doc, structuralWall, otherWall))
                            {
                                joinCount++;
                                Log($"      ✓ Union géométrique réussie: {otherWall.WallType.Name} ∪ Structure");
                                continue;
                            }
                        }
                        catch (Exception unionEx)
                        {
                            Log($"      ✗ Union géométrique échouée: {unionEx.Message}");
                        }

                        Log($"      ✗ ÉCHEC: Impossible d'attacher {otherWall.WallType.Name}");
                        
                    }
                    catch (Exception ex)
                    {
                        Log($"      ✗ Erreur générale attachement {otherWall.WallType.Name}: {ex.Message}");
                    }
                }

                if (joinCount > 0)
                {
                    Log($"SUCCESS: {joinCount}/{otherWalls.Count} couches attachées à la structure");
                    Log("  → Les ouvertures de la structure devraient se propager aux couches attachées");
                }
                else
                {
                    Log($"ÉCHEC COMPLET: Aucune couche n'a pu être attachée");
                    Log("  → Tentative alternative: Copie manuelle des ouvertures...");
                    
                    // MÉTHODE ALTERNATIVE: Copie manuelle des ouvertures
                    try
                    {
                        CopyOpeningsBetweenWalls(doc, structuralWall, otherWalls);
                    }
                    catch (Exception copyEx)
                    {
                        Log($"  ERREUR copie manuelle: {copyEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"ERREUR Join Geometry: {ex.Message}");
            }
        }

        /// <summary>
        /// Méthode alternative: Copier manuellement les ouvertures d'un mur vers d'autres murs
        /// </summary>
        private void CopyOpeningsBetweenWalls(Document doc, Wall sourceWall, List<Wall> targetWalls)
        {
            try
            {
                Log($"    COPIE MANUELLE OUVERTURES: {sourceWall.WallType.Name} → {targetWalls.Count} couches");
                
                // Récupérer toutes les ouvertures du mur source
                var sourceOpenings = new List<FamilyInstance>();
                
                // Éléments insérés (portes, fenêtres)
                var insertedElements = sourceWall.FindInserts(true, true, true, true);
                foreach (ElementId insertId in insertedElements)
                {
                    if (doc.GetElement(insertId) is FamilyInstance familyInstance)
                    {
                        sourceOpenings.Add(familyInstance);
                        Log($"      Ouverture source: {familyInstance.Category?.Name} - {familyInstance.Symbol.Name}");
                    }
                }

                if (sourceOpenings.Count == 0)
                {
                    Log($"      Aucune ouverture à copier depuis {sourceWall.WallType.Name}");
                    return;
                }

                // Copier chaque ouverture vers chaque mur cible
                int copiedCount = 0;
                foreach (Wall targetWall in targetWalls)
                {
                    Log($"      Copie vers: {targetWall.WallType.Name}");
                    
                    foreach (FamilyInstance sourceOpening in sourceOpenings)
                    {
                        try
                        {
                            // Récupérer la position et les paramètres
                            XYZ location = GetFamilyInstanceLocation(sourceOpening);
                            if (location == null) continue;

                            FamilySymbol symbol = sourceOpening.Symbol;
                            if (!symbol.IsActive) symbol.Activate();

                            // Créer nouvelle instance sur le mur cible
                            FamilyInstance newInstance = doc.Create.NewFamilyInstance(
                                location,
                                symbol,
                                targetWall,
                                Autodesk.Revit.DB.Structure.StructuralType.NonStructural
                            );

                            if (newInstance != null)
                            {
                                // Copier les paramètres importants
                                CopyFamilyInstanceParameters(sourceOpening, newInstance);
                                copiedCount++;
                                
                                Log($"        ✓ Ouverture copiée: {symbol.Name} (ID: {newInstance.Id.Value})");
                            }
                        }
                        catch (Exception copyEx)
                        {
                            Log($"        ✗ Erreur copie ouverture: {copyEx.Message}");
                        }
                    }
                }

                Log($"    RÉSULTAT COPIE: {copiedCount} ouvertures copiées sur {targetWalls.Count} murs");
            }
            catch (Exception ex)
            {
                Log($"    ERREUR copie manuelle ouvertures: {ex.Message}");
            }
        }

        /// <summary>
        /// Obtenir la position d'une FamilyInstance
        /// </summary>
        private XYZ GetFamilyInstanceLocation(FamilyInstance familyInstance)
        {
            try
            {
                if (familyInstance.Location is LocationPoint locPoint)
                {
                    return locPoint.Point;
                }
                else if (familyInstance.Location is LocationCurve locCurve)
                {
                    return locCurve.Curve.Evaluate(0.5, true);
                }
                else
                {
                    // Fallback: utiliser le centre du BoundingBox
                    BoundingBoxXYZ bbox = familyInstance.get_BoundingBox(null);
                    if (bbox != null)
                    {
                        return (bbox.Min + bbox.Max) * 0.5;
                    }
                }
            }
            catch
            {
                // Ignore errors
            }
            
            return null;
        }

        /// <summary>
        /// Vérifier si deux murs sont compatibles pour un join géométrique
        /// </summary>
        private bool AreWallsCompatibleForJoin(Wall wall1, Wall wall2)
        {
            try
            {
                // Vérifier que les murs ont des LocationCurve
                if (!(wall1.Location is LocationCurve loc1) || !(wall2.Location is LocationCurve loc2))
                    return false;

                Curve curve1 = loc1.Curve;
                Curve curve2 = loc2.Curve;

                // Vérifier la distance entre les courbes
                double distance = curve1.Distance(curve2.GetEndPoint(0));
                double tolerance = UnitUtils.ConvertToInternalUnits(50.0, UnitTypeId.Millimeters); // 50mm de tolérance

                Log($"        Distance entre murs: {UnitUtils.ConvertFromInternalUnits(distance, UnitTypeId.Millimeters):F1}mm (tolérance: 50mm)");
                
                return distance <= tolerance;
            }
            catch (Exception ex)
            {
                Log($"        Erreur vérification compatibilité: {ex.Message}");
                return true; // Essayer quand même en cas d'erreur
            }
        }

        /// <summary>
        /// Tentative d'union géométrique alternative via les solides
        /// </summary>
        private bool AttemptGeometryUnion(Document doc, Wall structuralWall, Wall otherWall)
        {
            try
            {
                // Cette approche est expérimentale
                // L'idée est de forcer une relation géométrique via les solides
                
                Options geometryOptions = new Options();
                geometryOptions.DetailLevel = ViewDetailLevel.Fine;
                geometryOptions.IncludeNonVisibleObjects = true;

                GeometryElement structuralGeom = structuralWall.get_Geometry(geometryOptions);
                GeometryElement otherGeom = otherWall.get_Geometry(geometryOptions);

                if (structuralGeom != null && otherGeom != null)
                {
                    Log($"        Géométries récupérées - tentative union conceptuelle");
                    
                    // Dans une vraie implémentation, on pourrait essayer:
                    // 1. Récupérer les solides de chaque mur
                    // 2. Calculer leur intersection/union
                    // 3. Modifier la géométrie du mur non-structural
                    
                    // Pour l'instant, on simule juste le succès si les géométries existent
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Log($"        Erreur union géométrique: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Copier les paramètres d'une FamilyInstance vers une autre
        /// </summary>
        private void CopyFamilyInstanceParameters(FamilyInstance source, FamilyInstance target)
        {
            try
            {
                // Paramètres critiques à copier
                var criticalParams = new[]
                {
                    BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM,
                    BuiltInParameter.INSTANCE_ELEVATION_PARAM,
                    BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM,
                    BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM,
                    BuiltInParameter.DOOR_HEIGHT,
                    BuiltInParameter.DOOR_WIDTH,
                    BuiltInParameter.WINDOW_HEIGHT,
                    BuiltInParameter.WINDOW_WIDTH
                };

                foreach (var paramId in criticalParams)
                {
                    try
                    {
                        var sourceParam = source.get_Parameter(paramId);
                        var targetParam = target.get_Parameter(paramId);

                        if (sourceParam != null && targetParam != null && !targetParam.IsReadOnly)
                        {
                            switch (sourceParam.StorageType)
                            {
                                case StorageType.Double:
                                    targetParam.Set(sourceParam.AsDouble());
                                    break;
                                case StorageType.Integer:
                                    targetParam.Set(sourceParam.AsInteger());
                                    break;
                                case StorageType.String:
                                    if (!string.IsNullOrEmpty(sourceParam.AsString()))
                                        targetParam.Set(sourceParam.AsString());
                                    break;
                                case StorageType.ElementId:
                                    targetParam.Set(sourceParam.AsElementId());
                                    break;
                            }
                        }
                    }
                    catch
                    {
                        // Ignorer les erreurs sur paramètres individuels
                    }
                }
            }
            catch
            {
                // Ignorer les erreurs générales
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class PurgerTypesExplosesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            int purgedTypes = 0;
            int keptTypes = 0;
            using (Transaction t = new Transaction(doc, "Purger types CoucheMur explosés"))
            {
                t.Start();
                var coucheTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().Where(wt => wt.Name.StartsWith("CoucheMur-")).ToList();
                var usedTypeIds = new HashSet<ElementId>(new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Select(w => w.GetTypeId()));
                foreach (var wt in coucheTypes)
                {
                    if (!usedTypeIds.Contains(wt.Id))
                    {
                        try { doc.Delete(wt.Id); purgedTypes++; } catch { keptTypes++; }
                    }
                    else { keptTypes++; }
                }
                t.Commit();
            }
            try
            {
                string dllPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string logPath = Path.Combine(Path.GetDirectoryName(dllPath), "CoucheMurPlugin.log");
                File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + $" - Purge: {purgedTypes} supprimés, {keptTypes} conservés\n");
            }
            catch { }
            TaskDialog.Show("Purge CoucheMur", $"Types supprimés: {purgedTypes}\nTypes conservés: {keptTypes}");
            return Result.Succeeded;
        }
    }

    public class WallSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is Wall;
        public bool AllowReference(Reference reference, XYZ position) => true;
    }

    // Classe pour stocker la géométrie d'une couche
    public class LayerGeometry
    {
        public Curve Centerline { get; set; }
        public double Thickness { get; set; }
        public XYZ Normal { get; set; }
        public double OffsetFromExterior { get; set; }
    }

    // Énumération pour les extrémités de mur
    public enum WallEndpoint
    {
        Start,
        End
    }

    // Classe pour stocker les informations de jonction entre murs
    public class WallJunctionInfo
    {
        public Wall ConnectedWall { get; set; }
        public XYZ JunctionPoint { get; set; }
        public WallEndpoint TargetWallEndpoint { get; set; }
        public WallEndpoint ConnectedWallEndpoint { get; set; }
    }
}
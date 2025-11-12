# CoucheMur Plugin pour Autodesk Revit

Bienvenue dans le plugin **CoucheMur** pour Autodesk Revit ! Cet outil puissant est conçu pour transformer la manière dont vous manipulez les murs composés dans vos projets Revit. Fini les manipulations complexes de géométrie pour chaque couche : "CoucheMur" vous permet de décomposer un mur composé en ses couches individuelles, chacune devenant un mur Revit distinct et entièrement modifiable.

Imaginez la flexibilité : chaque couche de votre mur (structure, isolation, finitions) devient une entité indépendante, tout en conservant les liens avec les ouvertures (portes, fenêtres) et en gérant intelligemment les jonctions avec d'autres murs. Que ce soit pour des analyses détaillées, des exports spécifiques ou une documentation précise, CoucheMur simplifie votre workflow et ouvre de nouvelles possibilités de modélisation.

## Fonctionnalités Clés

*   **Décomposition Intelligente** : Transforme un mur composé en une série de murs simples, un pour chaque couche.
*   **Gestion des Ouvertures** : Réaffecte automatiquement les portes et fenêtres du mur original aux nouvelles couches, en priorisant la couche structurelle.
*   **Ajustement des Jonctions** : Adapte la géométrie des murs de couche pour assurer des raccords parfaits avec les murs adjacents.
*   **Nettoyage Facilité** : Une commande dédiée pour purger les types de murs de couche inutilisés de votre projet.

## Téléchargement et Installation

Le plugin CoucheMur est disponible pour plusieurs versions de Revit. Veuillez télécharger les fichiers correspondant à votre version de Revit et suivre les instructions d'installation ci-dessous.

### Versions Disponibles

*   **Revit 2026**
    *   [**Télécharger CoucheMur_2026.zip**](https://github.com/SDViz/Revit/raw/main/CoucheMur_2026.zip) (Contient `CoucheMur_2026.addin` et `CoucheMur.dll`)
*   **Revit 2025**
    *   [**Télécharger CoucheMur_2025.zip**](https://github.com/SDViz/Revit/raw/main/CoucheMur_2025.zip) (Contient `CoucheMur_2025.addin` et `CoucheMur.dll`)
*   **Revit 2024**
    *   [**Télécharger CoucheMur_2024.zip**](https://github.com/SDViz/Revit/raw/main/CoucheMur_2024.zip) (Contient `CoucheMur_2024.addin` et `CoucheMur.dll`)
*   **Revit 2023**
    *   [**Télécharger CoucheMur_2023.zip**](https://github.com/SDViz/Revit/raw/main/CoucheMur_2023.zip) (Contient `CoucheMur_2023.addin` et `CoucheMur.dll`)

## Comment Installer

Suivez ces étapes simples pour installer le plugin CoucheMur dans Autodesk Revit :

1.  **Téléchargez l'archive ZIP** :
    *   Téléchargez le fichier `.zip` correspondant à votre version de Revit (par exemple, `CoucheMur_2026.zip`).

2.  **Extrayez les fichiers** :
    *   Décompressez l'archive ZIP. Vous y trouverez `CoucheMur_YYYY.addin` (où YYYY est l'année de Revit) et `CoucheMur.dll`.
    *   Assurez-vous que ces deux fichiers sont dans le même dossier après l'extraction.

2.  **Localisez le dossier AddIns de Revit** :
    Le dossier AddIns de Revit est généralement situé à l'un des emplacements suivants (remplacez `[VersionRevit]` par votre version de Revit, par exemple `2026`) :
    *   `C:\ProgramData\Autodesk\Revit\Addins\[VersionRevit]\`
    *   `C:\Users\[VotreNomUtilisateur]\AppData\Roaming\Autodesk\Revit\Addins\[VersionRevit]\`

3.  **Copiez les fichiers** :
    *   Copiez le fichier `.addin` téléchargé (par exemple, `CoucheMur_2026.addin`) et le fichier `CoucheMur.dll` dans le dossier AddIns de Revit que vous avez localisé à l'étape précédente.

4.  **Démarrez Revit** :
    *   Lancez Autodesk Revit. Le plugin "CoucheMur" devrait maintenant apparaître sous l'onglet "Compléments" (ou "Add-Ins") dans le ruban Revit.

## Utilisation

1.  **Sélectionnez un mur composé** : Dans Revit, sélectionnez un ou plusieurs murs composés que vous souhaitez décomposer.
2.  **Exécutez la commande** : Allez dans l'onglet "Compléments" et cliquez sur la commande "Exploser Mur Composé".
3.  **Vérifiez les résultats** : Le mur original sera remplacé par des murs individuels pour chaque couche, avec les ouvertures et jonctions ajustées.
4.  **Purger les types inutilisés** : Si vous avez créé de nombreux types de murs de couche et que vous souhaitez nettoyer votre projet, utilisez la commande "Purger Types Explosés" sous l'onglet "Compléments".

Profitez de la flexibilité accrue dans vos projets Revit !

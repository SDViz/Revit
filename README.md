# CoucheMur Plugin pour Autodesk Revit

Bienvenue dans le plugin **CoucheMur** pour Autodesk Revit ! Cet outil puissant est conçu pour transformer la manière dont vous manipulez les murs composés dans vos projets Revit. Fini les manipulations complexes de géométrie pour chaque couche : "CoucheMur" vous permet de décomposer un mur composé en ses couches individuelles, chacune devenant un mur Revit distinct et entièrement modifiable.

Imaginez la flexibilité : chaque couche de votre mur (structure, isolation, finitions) devient une entité indépendante, tout en conservant les liens avec les ouvertures (portes, fenêtres) et en gérant intelligemment les jonctions avec d'autres murs. Que ce soit pour des analyses détaillées, des exports spécifiques ou une documentation précise, CoucheMur simplifie votre workflow et ouvre de nouvelles possibilités de modélisation.

## Fonctionnalités Clés

*   **Décomposition Intelligente** : Transforme un mur composé en une série de murs simples, un pour chaque couche.
*   **Gestion des Ouvertures** : Réaffecte automatiquement les portes et fenêtres du mur original aux nouvelles couches, en priorisant la couche structurelle.
*   **Ajustement des Jonctions** : Adapte la géométrie des murs de couche pour assurer des raccords parfaits avec les murs adjacents.
*   **Nettoyage Facilité** : Une commande dédiée pour purger les types de murs de couche inutilisés de votre projet.

## Téléchargement

Vous pouvez télécharger la dernière version compilée du plugin directement depuis le dépôt GitHub :

*   [**Télécharger CoucheMur.addin**](https://github.com/SDViz/Revit/raw/main/CoucheMur.addin)
*   [**Télécharger CoucheMur.dll**](https://github.com/SDViz/Revit/raw/main/bin/Release/net48/CoucheMur.dll)

## Comment Installer

Suivez ces étapes simples pour installer le plugin CoucheMur dans Autodesk Revit :

1.  **Téléchargez les fichiers** :
    *   Téléchargez `CoucheMur.addin`
    *   Téléchargez `CoucheMur.dll`
    *   Assurez-vous de télécharger les deux fichiers et de les placer dans le même dossier sur votre machine.

2.  **Localisez le dossier AddIns de Revit** :
    Le dossier AddIns de Revit est généralement situé à l'un des emplacements suivants (remplacez `[VersionRevit]` par votre version de Revit, par exemple `Revit 2023`) :
    *   `C:\ProgramData\Autodesk\Revit\Addins\[VersionRevit]\`
    *   `C:\Users\[VotreNomUtilisateur]\AppData\Roaming\Autodesk\Revit\Addins\[VersionRevit]\`

3.  **Copiez les fichiers** :
    *   Copiez les deux fichiers téléchargés (`CoucheMur.addin` et `CoucheMur.dll`) dans le dossier AddIns de Revit que vous avez localisé à l'étape précédente.

4.  **Démarrez Revit** :
    *   Lancez Autodesk Revit. Le plugin "CoucheMur" devrait maintenant apparaître sous l'onglet "Compléments" (ou "Add-Ins") dans le ruban Revit.

## Utilisation

1.  **Sélectionnez un mur composé** : Dans Revit, sélectionnez un ou plusieurs murs composés que vous souhaitez décomposer.
2.  **Exécutez la commande** : Allez dans l'onglet "Compléments" et cliquez sur la commande "Exploser Mur Composé".
3.  **Vérifiez les résultats** : Le mur original sera remplacé par des murs individuels pour chaque couche, avec les ouvertures et jonctions ajustées.
4.  **Purger les types inutilisés** : Si vous avez créé de nombreux types de murs de couche et que vous souhaitez nettoyer votre projet, utilisez la commande "Purger Types Explosés" sous l'onglet "Compléments".

Profitez de la flexibilité accrue dans vos projets Revit !

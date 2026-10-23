# QbtAutoUpdate

[![Build](https://github.com/MrGautier256/QbtAutoUpdate/actions/workflows/build.yml/badge.svg)](https://github.com/MrGautier256/QbtAutoUpdate/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/MrGautier256/QbtAutoUpdate)](https://github.com/MrGautier256/QbtAutoUpdate/releases/latest)
[![Licence : MIT](https://img.shields.io/badge/licence-MIT-blue.svg)](LICENSE)

Garde à jour la version officielle de [qBittorrent](https://www.qbittorrent.org/) pour Windows. Quand une
nouvelle version sort, QbtAutoUpdate :

1. ferme qBittorrent proprement, pour qu'il enregistre d'abord ses données de reprise ;
2. télécharge l'installateur officiel et vérifie sa signature OpenPGP ;
3. l'installe en silence en conservant votre version de libtorrent (1.2 ou 2.x) ;
4. relance qBittorrent.

Il passe par le Planificateur de tâches (aucun processus en arrière-plan) et, après une seule invite
administrateur à l'installation, ne demande plus rien.

*[English version](README.md)*

## Installation

**Prérequis**
- Windows 10 (version 1809 ou plus récente) ou Windows 11, en x64 ou ARM64.
- qBittorrent installé avec l'installateur officiel de qbittorrent.org. La version portable, les builds
  32 bits et les dérivés comme qBittorrent Enhanced Edition ne sont pas pris en charge.

**Étapes**
1. Téléchargez `QbtAutoUpdate-win-x64.exe` depuis la [dernière version](https://github.com/MrGautier256/QbtAutoUpdate/releases/latest).
   Sur un PC Windows ARM, prenez `QbtAutoUpdate-win-arm64.exe`.
2. Double-cliquez dessus. L'exécutable n'est pas encore signé (voir [la FAQ](#pourquoi-windows-smartscreen-affiche-t-il-un-avertissement-)) :
   SmartScreen peut afficher « Windows a protégé votre ordinateur ». Cliquez sur **Informations
   complémentaires**, puis **Exécuter quand même**.
3. Choisissez un mode :
   - **Mettre à jour automatiquement** : fermer qBittorrent proprement, installer, le relancer.
   - **Me demander d'abord par une notification** : une notification Windows avec « Mettre à jour
     maintenant », « Plus tard » et « Ignorer cette version ».
4. Cliquez sur **Installer** et acceptez l'invite administrateur. C'est la seule.

QbtAutoUpdate se copie dans `C:\Program Files\QbtAutoUpdate` et s'ajoute au menu Démarrer et aux
*Applications installées*. Vous pouvez ensuite supprimer le fichier téléchargé.

Pour mettre à jour QbtAutoUpdate lui-même, lancez la nouvelle version de la même façon. La fenêtre propose
la mise à niveau sur place et garde vos réglages.

## Utilisation

Ouvrez **QbtAutoUpdate** depuis le menu Démarrer pour voir ce qui est installé et l'heure de la dernière
vérification. De là, vous pouvez :
- rechercher une mise à jour maintenant ;
- désactiver ou réactiver les mises à jour automatiques ;
- passer d'un mode à l'autre ;
- réinstaller la version précédente ;
- ouvrir les journaux ;
- désinstaller.

**Quand il vérifie.** Il cherche une nouvelle version 3 minutes après l'ouverture de session, puis toutes
les 6 heures. Il saute la vérification sur une connexion limitée, sauf si vous la lancez vous-même.

**Ce qu'il installe.** Il installe la même variante que celle que vous utilisez :
- qBittorrent 5.3.0 a fait passer sa version par défaut à libtorrent 2.1 ;
- si vous utilisez la version libtorrent 1.2, QbtAutoUpdate installe le build `lt12` ;
- il ne change jamais de série sans que vous le demandiez (`config --variant`).

**Quand qBittorrent ne peut pas être fermé.** Si qBittorrent ne peut pas être fermé (une boîte de dialogue
est ouverte, ou il est occupé), la mise à jour attend la vérification suivante, et une notification propose
de réessayer.

**Revenir en arrière.** Si une version pose problème, **Réinstaller la version précédente** remet celle
d'avant, dans la même série libtorrent. Les mises à jour automatiques ignorent ensuite la version retirée
jusqu'à la sortie d'une plus récente. Une recherche manuelle peut quand même l'installer.

### Ligne de commande

Le même exe s'utilise en ligne de commande pour les scripts. Lancez `QbtAutoUpdate.exe help` dans un
terminal pour le détail (l'aide et les diagnostics sont en anglais).

| Commande | Rôle |
|----------|------|
| `status` | Version et variante installées, dernière version, réglages, dernier résultat, emplacement des journaux |
| `check` | Cherche une mise à jour ; l'installe (mode auto) ou l'annonce (mode notification) |
| `update` | Télécharge, vérifie et installe la dernière version tout de suite |
| `setup --quiet [--mode auto\|notify] [--interval <heures>] [--variant auto\|lt2\|lt12]` | Installation sans fenêtre (administrateur) |
| `config [--enabled on\|off] [--mode ...] [--interval ...] [--variant ...] [--skipped-version <v>\|none]` | Modifie les réglages (administrateur) ; sans option, les affiche |
| `rollback` | Réinstalle la version précédente (administrateur) |
| `restart` | Ferme qBittorrent comme le fait une mise à jour, puis le relance |
| `webui --url http://localhost:8080 --api-key <clé>` | Facultatif : fermer qBittorrent par son interface Web plutôt que par le Restart Manager |
| `uninstall [--keep-logs] [--quiet]` | Désinstalle QbtAutoUpdate (administrateur) |

Toutes les commandes acceptent `--dry-run`, qui fait tout sauf fermer qBittorrent et installer.

### Fichier de réglages

Les réglages de la machine sont dans `C:\ProgramData\QbtAutoUpdate\config.json`, modifiable par les seuls
administrateurs. La commande `config` couvre les plus courants. Les autres clés se modifient à la main et
s'appliquent à l'exécution suivante.

| Clé | Défaut | Rôle |
|-----|--------|------|
| `enabled` | `true` | Vérifications automatiques activées ou non |
| `mode` | `notify` | `auto` ou `notify` (la fenêtre d'installation pose la question) |
| `checkIntervalHours` | `6` | Heures entre deux vérifications (1 à 168) |
| `variant` | `auto` | `auto` garde la série libtorrent installée ; `lt2` ou `lt12` en impose une |
| `minReleaseAgeHours` | `0` | Âge minimal d'une version avant son installation automatique |
| `skipOnMeteredConnection` | `true` | Pas de téléchargement automatique sur une connexion limitée |
| `closeTimeoutSeconds` | `120` | Temps laissé à qBittorrent pour se fermer |
| `restoreDesktopShortcut` | `true` | Recrée le raccourci du Bureau public que l'installateur supprime |
| `backupConfig` | `true` | Sauvegarde les réglages de qBittorrent avant chaque mise à jour (les 3 dernières sont gardées) |
| `backupTorrents` | `false` | Sauvegarde aussi `BT_backup` (données de reprise), qui peut être volumineux |
| `notifications` | `all` | `all`, `errors` ou `none` |
| `allowForceClose` | `false` | Tue qBittorrent s'il ne se ferme pas à temps. **Déconseillé** : voir plus bas |
| `skippedVersion` | aucune | Posée par le rollback ; les mises à jour automatiques ignorent cette version |

À propos de `allowForceClose` : tuer qBittorrent fait perdre les données de reprise écrites depuis sa
dernière sauvegarde. Les torrents peuvent alors repartir d'une progression plus ancienne ou être revérifiés.
L'option est désactivée par défaut et doit le rester, sauf si qBittorrent bloque à la fermeture.

## Fonctionnement

- **Tâche Check.** Elle s'exécute pour chaque utilisateur connecté, sans droits administrateur, à
  l'ouverture de session puis toutes les quelques heures. Elle lit la dernière version sur qbittorrent.org,
  puis trouve l'installateur et sa signature sur SourceForge.
  - En mode auto, elle les télécharge et les vérifie, ferme qBittorrent, sauvegarde ses réglages, puis
    passe les fichiers à la tâche Apply.
  - En mode notification, elle affiche d'abord une notification.
- **Tâche Apply.** Elle s'exécute en SYSTEM, à la demande, depuis `C:\Program Files\QbtAutoUpdate` (un
  dossier que seuls les administrateurs peuvent modifier), et ne prend aucun argument.
  - Elle ne reçoit les fichiers que par un dossier de dépôt où les utilisateurs standard peuvent ajouter des
    fichiers, sans pouvoir lister ni lire ceux des autres.
  - Elle les copie dans un dossier réservé à SYSTEM et aux administrateurs, et y revérifie tout.
  - Elle lance l'installateur avec `/S /D=<dossier actuel>` et contrôle la version et la variante installées.
- **Relance.** La tâche Check relance ensuite qBittorrent dans votre session, avec les mêmes options
  d'instance qu'avant.

Rien ne reste en mémoire entre deux vérifications.

## Sécurité

- **Signature.** Chaque installateur est vérifié avec la signature OpenPGP publiée à côté de lui et la clé
  de publication de qBittorrent. Cette clé est intégrée à l'exe et son empreinte est figée :
  `D8F3 DA77 AAC6 7410 5359  9C13 6E4A 2D02 5B7C C9A2`. Une clé inconnue, expirée ou révoquée, une
  signature absente ou un fichier modifié arrêtent la mise à jour ; rien n'est lancé.
- **Autres contrôles de l'installateur.** Son nom, sa ressource de version, son architecture et sa série
  libtorrent doivent tous correspondre à ce qui est attendu. Les versions plus anciennes sont refusées,
  sauf par `rollback`, réservé aux administrateurs.
- **Téléchargements.** HTTPS uniquement, depuis une liste fermée d'hôtes : www.qbittorrent.org,
  qbittorrent.github.io, sourceforge.net, downloads.sourceforge.net et ses miroirs. L'hôte final est
  revérifié après chaque redirection.
- **Boutons des notifications.** Ils portent un jeton secret propre à chaque utilisateur. Un lien
  `qbtautoupdate:` venu d'une page web sans ce jeton est ignoré.
- **qBittorrent n'est jamais tué**, sauf si vous activez `allowForceClose`.
- **Les journaux** ne contiennent ni mot de passe ni clé d'API. Les secrets facultatifs de l'interface Web
  sont chiffrés avec DPAPI pour votre compte Windows.

Détails : [docs/DECISIONS.md](docs/DECISIONS.md) et [docs/FINDINGS.md](docs/FINDINGS.md) (en anglais).

## Effets de bord connus de l'installateur officiel

QbtAutoUpdate lance l'installateur de qBittorrent en silence, avec ses choix par défaut. À chaque mise à
jour, il :

- met `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled` à 1 (prise en charge des
  chemins longs pour tout le PC) ;
- recrée la règle du Pare-feu Windows de qBittorrent, si vous l'aviez supprimée ou modifiée ;
- supprime le raccourci du Bureau public. QbtAutoUpdate le remet s'il existait (`restoreDesktopShortcut`).

Vos réglages, vos torrents et le démarrage avec Windows ne sont pas touchés.

## Conseil : désactivez la vérification intégrée de qBittorrent

qBittorrent a sa propre vérification des mises à jour, qui vous demande de télécharger la nouvelle version
à la main. Avec QbtAutoUpdate, elle fait doublon. Dans qBittorrent, ouvrez **Outils › Options ›
Comportement** et décochez **Vérifier la disponibilité de mises à jour du logiciel**.

## Désinstallation

Utilisez **Désinstaller** dans la fenêtre de QbtAutoUpdate, les *Applications installées* des Paramètres
Windows, ou `QbtAutoUpdate.exe uninstall` en administrateur. Cela supprime :
- les tâches planifiées ;
- `C:\Program Files\QbtAutoUpdate` et `C:\ProgramData\QbtAutoUpdate` ;
- votre propre dossier `%LOCALAPPDATA%\QbtAutoUpdate` (ajoutez `--keep-logs` pour garder les journaux) ;
- le raccourci du menu Démarrer et l'inscription des notifications.

qBittorrent lui-même n'est pas touché. Les autres comptes Windows gardent leur petit dossier
`%LOCALAPPDATA%\QbtAutoUpdate`, que l'on peut supprimer à la main.

## Dépannage

- Lancez `QbtAutoUpdate.exe status` dans un terminal : il affiche l'installation détectée, la dernière
  version et le résultat de la dernière tentative.
- Journaux :
  - `%LOCALAPPDATA%\QbtAutoUpdate\logs` : vérifications, téléchargements, fermeture et relance, pour votre
    compte ;
  - `C:\ProgramData\QbtAutoUpdate\logs` : installation, changements de réglages et installation en SYSTEM.
- `C:\ProgramData\QbtAutoUpdate\state.json` garde la dernière installation et la dernière tentative.
- Les sauvegardes de réglages sont dans `%LOCALAPPDATA%\QbtAutoUpdate\backups`.

## FAQ

### Pourquoi pas winget, Winget-AutoUpdate ou UniGetUI ?

Ce sont de bons outils de mise à jour généralistes. QbtAutoUpdate ne fait qu'une chose : qBittorrent, avec
ses particularités. Constaté en octobre 2026 :

- **winget** :
  - Le paquet `qBittorrent.qBittorrent` installe la version par défaut. Depuis la 5.3.0, c'est libtorrent
    2.1 : un PC en version 5.2.x par défaut (libtorrent 1.2) change donc de série à la mise à jour.
  - Le paquet séparé `qBittorrent.qBittorrent.lt2` s'arrête à la 5.2.4, et il n'existe pas de paquet
    libtorrent 1.2 pour la 5.3.0.
  - L'intégrité repose sur le SHA-256 du manifeste communautaire, pas sur la signature de qBittorrent.
  - winget ne ferme pas qBittorrent avant de lancer l'installateur.
- **Winget-AutoUpdate (WAU)** :
  - Il lance winget à intervalles réguliers et installe donc les mêmes paquets.
  - La fermeture d'une application est laissée à vos propres scripts « mods », et la fonction fournie
    utilise `Stop-Process -Force`, qui tue le processus.
- **UniGetUI** :
  - Il passe lui aussi par winget pour qBittorrent.
  - On peut lui indiquer des processus à fermer avant une mise à jour. Il envoie une demande de fermeture à
    la fenêtre principale, attend une seconde, puis tue le processus si l'option « tuer les processus qui
    refusent de se fermer » est active.
  - Avec « réduire dans la zone de notification à la fermeture », une demande de fermeture de fenêtre ne
    fait que masquer qBittorrent : il finit tué, ou la mise à jour échoue.

QbtAutoUpdate, lui :
- ferme qBittorrent comme le fait Windows à l'arrêt du PC, et attend qu'il ait enregistré ses données de
  reprise ;
- conserve votre série libtorrent ;
- vérifie la signature OpenPGP de qBittorrent lui-même ;
- ne dépend pas de winget.

### Pourquoi Windows SmartScreen affiche-t-il un avertissement ?

L'exécutable n'est pas encore signé avec un certificat Authenticode : SmartScreen ne le connaît donc pas.
Chaque version publie le SHA-256 des deux fichiers dans `SHA256SUMS.txt`, et ils sont compilés par GitHub
Actions à partir de ce dépôt. Une signature via la [SignPath Foundation](https://signpath.org/) (gratuite
pour les projets open source) et un manifeste winget sont prévus.

### Se met-il à jour lui-même ?

Pas encore. Téléchargez la nouvelle version et double-cliquez dessus : elle met à niveau la copie installée
et garde vos réglages.

### Fonctionne-t-il avec des comptes standard (non administrateurs) ?

Oui. Un administrateur fait l'installation une fois. Ensuite, les mises à jour s'installent pour tous les
utilisateurs sans invite, et les notifications s'affichent pour la personne connectée.

### Peut-il fermer qBittorrent par son interface Web ?

Oui, c'est facultatif. Si l'interface Web est activée,
`QbtAutoUpdate.exe webui --url http://localhost:8080 --api-key <clé>` lui fait demander à qBittorrent de
quitter par l'API. Donnez `-` au lieu de la clé pour la transmettre par un pipe et la garder hors de
l'historique du terminal ; `--username` et `--password` fonctionnent aussi. Sinon, le Restart Manager est
utilisé. Seules les adresses de ce PC sont acceptées.

## Compiler depuis les sources

Il faut le SDK .NET 10.

```powershell
dotnet build
dotnet test                                       # tests unitaires
dotnet publish src/QbtAutoUpdate -c Release -r win-x64 -o publish   # un seul exe, allégé
```

- [docs/TESTING.md](docs/TESTING.md) : ce qui a été testé et comment.
- [sandbox/](sandbox/README.md) : le scénario de bout en bout dans Windows Sandbox.
- Publications : pousser un tag `v1.2.3`, ou lancer le workflow *Release*, compile les deux exécutables et
  publie la version GitHub avec la section correspondante du [CHANGELOG](CHANGELOG.md).

## Licence

[MIT](LICENSE). QbtAutoUpdate n'est pas affilié au projet qBittorrent.

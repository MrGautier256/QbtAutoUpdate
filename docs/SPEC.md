# Original specification (French)

> This is the specification the project was started from, kept verbatim in its original language.
> Where reality turned out differently, [FINDINGS.md](FINDINGS.md) records what was observed and
> [DECISIONS.md](DECISIONS.md) records what the code does instead. Those two documents win over this one.

---

# Cahier des charges : QbtAutoUpdate

> Nom provisoire : `QbtAutoUpdate` (à renommer partout si je choisis un autre nom).
> Ce document est ton cahier des charges. Lis-le en entier avant d'écrire la moindre ligne de code.

## 1. Objectif

Créer, dans ce dépôt vide, un utilitaire Windows open source (licence MIT) qui garde qBittorrent à jour sans intervention.

- Mode `auto` : la nouvelle version est détectée, téléchargée, vérifiée, qBittorrent est fermé proprement, la mise à jour est installée en silence, puis qBittorrent est relancé s'il tournait avant. L'utilisateur n'a rien à faire.
- Mode `notify` : une notification Windows propose « Mettre à jour maintenant », « Plus tard », « Ignorer cette version ». Un clic suffit.

L'outil sera publié. Il doit fonctionner sur n'importe quel PC sous Windows 10 (1809 ou plus) ou Windows 11, x64 ou ARM64, compte administrateur ou standard, sans rien de propre à ma machine : aucun chemin, nom d'utilisateur, langue ou réglage écrit en dur.

Contraintes de conception :

- Aucun processus résident. Tout passe par le Planificateur de tâches : zéro mémoire consommée entre deux vérifications.
- Une seule invite UAC, au moment d'installer l'outil. Plus aucune ensuite.
- Ne jamais tuer qBittorrent par défaut (risque de perdre les données de reprise des torrents) ou proposer de le faire via popup pour une mise a jour.
- Ne jamais exécuter un installateur dont la signature PGP officielle n'a pas été vérifiée.
- Ne jamais changer de variante de build (libtorrent 2.x ou 1.2) sans demande explicite de l'utilisateur.

## 2. Faits vérifiés sur qBittorrent (octobre 2026)

Ces points viennent de la lecture du code source et des pages officielles. Avant de coder, relis toi-même les sources citées (fichiers bruts via `raw.githubusercontent.com/qbittorrent/qBittorrent/master/...`). Si quelque chose a changé, adapte-toi et consigne l'écart dans `docs/FINDINGS.md`.

### 2.1 Connaître la dernière version

- `https://www.qbittorrent.org/versions.json` renvoie `{"win": {"version": "5.3.0"}, "macos": {"version": "5.2.3"}}`.
- Copie de secours : `https://qbittorrent.github.io/qBittorrent-website/versions.json`.
- Ce sont exactement les deux URL du vérificateur intégré de qBittorrent (`src/gui/programupdater.cpp`) : il interroge les deux, garde la version la plus haute, et sous Windows se contente d'ouvrir la page de téléchargement. Fais pareil : interroger les deux, prendre le maximum, tolérer l'échec de l'une.
- L'ancien flux FossHub est mort (retiré dans la 5.3.0, PR #24611). Ne pas l'utiliser.
- Les GitHub Releases du dépôt officiel ne servent pas à distribuer les binaires Windows. Ne pas en dépendre.

### 2.2 Télécharger les installateurs

- Hébergés sur SourceForge : `https://sourceforge.net/projects/qbittorrent/files/qbittorrent-win32/qbittorrent-{VERSION}/`
- Contenu du dossier 5.3.0 (publié le 2026-10-20) :
  - `qbittorrent_5.3.0_x64_setup.exe` (build par défaut : Qt6 et libtorrent 2.1.x)
  - `qbittorrent_5.3.0_lt12_x64_setup.exe` (Qt6 et libtorrent 1.2.x)
  - un fichier `.asc` (signature PGP détachée) pour chacun
  - `README` (changelog de la version)
- Téléchargement direct : `https://downloads.sourceforge.net/project/qbittorrent/qbittorrent-win32/qbittorrent-{VERSION}/{FICHIER}` (redirige vers un miroir `*.dl.sourceforge.net`).
- Flux RSS du dossier, pratique pour lister les fichiers et leur date : `https://sourceforge.net/projects/qbittorrent/rss?path=/qbittorrent-win32/qbittorrent-{VERSION}`
- Le nommage des variantes a changé : avant la 5.2.0, la build par défaut utilisait libtorrent 1.2 et la variante libtorrent 2.0 ; depuis la 5.2.0 c'est l'inverse (défaut 2.1, variante `lt12`). Conséquence : la variante se définit par la série libtorrent (1.x ou 2.x), pas par le suffixe du nom de fichier, et le nom ne doit jamais être construit à partir d'un motif figé (voir 4.4).
- Le script de l'installateur prévoit une architecture `arm64` (`QBT_CPU_ARCH`), mais aucun installateur arm64 n'est publié pour la 5.3.0. Sur une machine ARM64 : prendre un fichier arm64 s'il existe, sinon l'installateur x64 (exécuté en émulation). L'installateur refuse un binaire arm64 sur une machine x64.

### 2.3 Signature

- Depuis la v3.3.4, tous les binaires sont signés en PGP.
- Clé actuelle : 4096R/5B7CC9A2, empreinte `D8F3DA77AAC6741053599C136E4A2D025B7CC9A2`.
- Clé publique : `https://github.com/qbittorrent/qBittorrent/raw/master/5B7CC9A2.asc`. Embarque-la comme ressource dans l'exe et épingle l'empreinte complète dans le code.
- La page de téléchargement du site affiche aussi des SHA-256, mais dans un tableau HTML. Ne pas en dépendre (au mieux un contrôle secondaire, désactivable).

### 2.4 Comportement de l'installateur NSIS

Fichiers à relire : `dist/windows/qbittorrent.nsi`, `config.nsh`, `installer.nsh`, `uninstaller.nsh`.

- Silencieux avec `/S`. Dossier cible avec `/D=C:\chemin` (toujours en dernier, sans guillemets, même s'il contient des espaces).
- `RequestExecutionLevel user` plus le plugin UAC (`UAC_RunElevated`) : lancé sans élévation, il affiche une invite UAC même en `/S`. Lancé depuis un processus déjà élevé (admin ou SYSTEM), aucune invite.
- `SetShellVarContext all` : raccourcis dans le menu Démarrer commun et sur le Bureau public.
- Dossier par défaut `$PROGRAMFILES64\qBittorrent`, relu depuis `HKLM\Software\qBittorrent`, valeur `InstallLocation`.
- Clé de désinstallation : `HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\qBittorrent` (`DisplayVersion`, `UninstallString`, `DisplayIcon`).
- Aucun `SetRegView 64` dans les scripts et l'installateur est un exécutable 32 bits : sur Windows 64 bits, ces clés atterrissent très probablement dans la vue 32 bits (`HKLM\SOFTWARE\WOW6432Node\...`). Lis les deux vues (`RegistryView.Registry32` puis `Registry64`) et confirme sur une vraie installation.
- Si une installation existe déjà, l'installateur lance d'abord l'ancien `uninst.exe /S`, puis installe.
- Le désinstallateur supprime le dossier du menu Démarrer et `$DESKTOP\qBittorrent.lnk`, donc le raccourci du Bureau public. Il ne touche ni à `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\qBittorrent`, ni à `%APPDATA%\qBittorrent`, ni à `%LOCALAPPDATA%\qBittorrent` (ces sections sont décochées par défaut).
- Sections cochées par défaut, donc installées en `/S` : fichiers, raccourci du menu Démarrer, règle de pare-feu, suppression de la limite de 260 caractères (`HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled = 1`). Sections décochées : raccourci Bureau, démarrage avec Windows.
  - Conséquence 1 : après une mise à jour silencieuse, un raccourci Bureau qui existait a disparu. L'outil doit noter sa présence avant et le recréer après (même cible, même icône, même dossier de travail).
  - Conséquence 2 : `LongPathsEnabled` passera à 1. À documenter dans le README.
- La vérification « qBittorrent est ouvert » (`check_instance`) est branchée en fonction de sortie de page (`MUI_PAGE_CUSTOMFUNCTION_LEAVE`). Or NSIS n'appelle aucune fonction liée aux pages en mode silencieux. En `/S`, l'installateur ne protège donc pas contre un qbittorrent.exe ouvert. C'est à l'outil de garantir qu'aucun qbittorrent.exe ne tourne avant de lancer l'installateur, puis de vérifier le résultat.
- Codes de sortie connus : 0 succès ; 1654 refus (Windows antérieur à 10 1809, système non 64 bits, arm64 sur x64) ; 1223 UAC annulé ; 1314 privilège manquant ; 1603 échec d'élévation ; 1062 service d'ouverture de session arrêté ; 15618 instance ouverte (chemin non silencieux).
- L'installateur porte sa version dans ses ressources (`FileVersion` égal à la version, `VIProductVersion` sur 4 composants) : utilisable pour vérifier qu'un fichier correspond bien à la version annoncée.

### 2.5 Fermer qBittorrent proprement

- qBittorrent écrit ses données de reprise (fastresume) en se fermant. Un kill peut faire perdre la progression récente et forcer des revérifications.
- qBittorrent connecte `QGuiApplication::commitDataRequest` à `Application::shutdownCleanup` (`src/app/application.cpp`) : il gère la fin de session Windows.
- Le Restart Manager de Windows (`RmStartSession`, `RmRegisterResources`, `RmShutdown` sans forçage, `RmEndSession`) envoie `WM_QUERYENDSESSION` avec `ENDSESSION_CLOSEAPP`, puis `WM_ENDSESSION`, puis `WM_CLOSE` si l'application est toujours là. C'est la méthode prévue par Windows pour les installateurs : méthode principale. Enregistre le processus précis (`RM_UNIQUE_PROCESS`, PID plus heure de démarrage).
- Un simple `WM_CLOSE` est une mauvaise idée : avec « réduire dans la zone de notification à la fermeture », la fenêtre est juste cachée ; avec « confirmer la fermeture si des torrents sont actifs », une boîte de dialogue bloque.
- Alternative si l'utilisateur a activé l'interface Web : `POST /api/v2/auth/login` (cookie `SID`), puis `POST /api/v2/app/shutdown`, avec un en-tête `Referer` ou `Origin` identique à l'hôte. Optionnel, configuré par utilisateur.
- La fermeture peut être longue (annonces d'arrêt aux trackers, beaucoup de torrents) : attendre la fin réelle du processus avec un délai configurable (120 s par défaut).
- Si qBittorrent tourne élevé (certains le lancent en admin), un processus non élevé ne peut pas le fermer : détecter ce cas et le signaler.
- Tout ceci doit être validé empiriquement (section 8) avec « réduire à la fermeture » et « confirmer à la fermeture » activés.

## 3. Choix techniques

- C# et .NET 10, un seul exécutable `QbtAutoUpdate.exe`, publié en self-contained single-file pour `win-x64` et `win-arm64` (trimming si compatible). Aucune dépendance à un runtime .NET installé.
- `OutputType` = `WinExe` pour qu'aucune console ne clignote quand une tâche planifiée lance l'exe. Lancé depuis un terminal, il appelle `AttachConsole(ATTACH_PARENT_PROCESS)` pour afficher sa sortie.
- Manifeste `asInvoker`, `supportedOS` Windows 10. Les verbes qui exigent les droits admin se relancent eux-mêmes avec le verbe `runas`.
- Dépendances minimales, chacune justifiée dans `docs/DECISIONS.md` :
  - `BouncyCastle.Cryptography` pour la vérification PGP ;
  - `TaskScheduler` (Microsoft.Win32.TaskScheduler) ou l'API COM du Planificateur directement ;
  - notifications : `Microsoft.Toolkit.Uwp.Notifications` (`ToastNotificationManagerCompat`, prévu pour les apps non empaquetées). Vérifie qu'il fonctionne avec .NET 10 et un TFM du type `net10.0-windows10.0.19041.0` ; sinon, Windows App SDK `AppNotificationManager` en mode non empaqueté ;
  - Restart Manager, ACL, registre, raccourcis `.lnk`, informations de processus : P/Invoke ou API .NET, pas de bibliothèque lourde.
- Pas de PowerShell dans le produit (stratégies d'exécution, heuristiques antivirus sur les scripts qui téléchargent et lancent des exe, pas de vérification PGP propre sans gpg, notifications à boutons pénibles).
- Code, identifiants, commentaires, messages de commit et `README.md` en anglais (projet public), plus un `README.fr.md`. Textes visibles (notifications, messages) en anglais et en français via `.resx`, choisis selon la langue d'affichage de Windows.

## 4. Architecture

### 4.1 Emplacements

- Binaire : `%ProgramFiles%\QbtAutoUpdate\QbtAutoUpdate.exe`. Obligatoire : la tâche SYSTEM exécute ce fichier, il ne doit être modifiable que par les administrateurs.
- Données machine : `%ProgramData%\QbtAutoUpdate\`
  - `config.json` : réglages machine (écriture Administrators et SYSTEM, lecture Users) ;
  - `state.json` : dernière installation faite par l'outil (version, variante, date, résultat, état « busy ») ; écrit par SYSTEM, lisible par tous ;
  - `inbox\` : dépôt où un utilisateur pose installateur et signature (Users peuvent créer des fichiers) ;
  - `work\` : SYSTEM et Administrators uniquement ;
  - `logs\` : journaux de la partie SYSTEM.
- Données utilisateur : `%LOCALAPPDATA%\QbtAutoUpdate\` (`user.json`, `staging\`, `backups\`, `logs\`). Mot de passe éventuel de l'interface Web chiffré avec DPAPI (CurrentUser).
- Journaux tournants, 10 fichiers maximum, sans secret.

### 4.2 Verbes de l'exécutable

- `setup` (admin) : copie l'exe dans Program Files, crée les dossiers ProgramData avec leurs ACL, écrit `config.json`, enregistre les deux tâches, enregistre l'AUMID des notifications, lance une première vérification. Interactif par défaut (mode, intervalle, variante), entièrement scriptable par options (`--mode auto --interval 6 --variant auto --quiet`). Un double-clic sur l'exe non installé propose ce setup.
- `uninstall` (admin) : supprime tâches, binaire, AUMID et données machine (option `--keep-logs`).
- `check` (utilisateur, non élevé) : compare la version installée à la dernière, puis selon le mode lance `update` ou affiche la notification.
- `update` (utilisateur, non élevé) : orchestration complète (4.3).
- `apply` (SYSTEM, uniquement via la tâche) : vérifie et installe ce qui est dans `inbox\` (4.3).
- `status` : version installée, variante, dernière version disponible, dernier résultat, chemins des journaux.
- `rollback` (admin, l'invite UAC est acceptable ici) : réinstalle la version précédente, signée, depuis SourceForge.
- Activation des notifications (clics sur les boutons) gérée par le même exe.
- Options globales : `--dry-run` (tout faire sauf fermer qBittorrent et installer) et `--verbose`.

### 4.3 Déroulé d'une mise à jour

Côté utilisateur (`update`, non élevé) :

1. Détecter l'installation (4.5). Absente, portable ou non officielle : journaliser et sortir.
2. Récupérer la dernière version (2.1). Sortir si elle n'est pas plus récente, si l'utilisateur l'a ignorée, si un report est en cours, ou si elle est trop récente au regard de `minReleaseAgeHours` (date du fichier dans le RSS SourceForge).
3. Résoudre le fichier de la bonne variante et de la bonne architecture (4.4). Fichier introuvable (miroirs pas encore synchronisés) : réessayer au prochain passage. Variante absente de cette version : notifier, ne rien installer.
4. Télécharger installateur et `.asc` dans `staging\` (HTTPS, hôtes autorisés, contrôle que le fichier commence par `MZ` et pas par du HTML, 3 tentatives en changeant de miroir), puis vérifier la signature tout de suite pour échouer avant de toucher à qBittorrent.
5. Si qBittorrent tourne dans la session : mémoriser sa ligne de commande (pour relancer avec les mêmes arguments, par exemple `--profile`), le fermer proprement (2.5), puis sauvegarder sa configuration (4.7). En cas d'échec de fermeture : abandonner sans rien casser et notifier « Fermez qBittorrent pour terminer la mise à jour » avec un bouton « Réessayer ».
6. Copier installateur et `.asc` dans `inbox\`, démarrer la tâche `Apply`, attendre sa fin (état de la tâche et `state.json`), avec un délai maximal.
7. Relancer qBittorrent, non élevé, avec les mêmes arguments, seulement s'il tournait avant.
8. Notification de résultat (selon le réglage), nettoyage de `staging\`.

Côté SYSTEM (`apply`, n'accepte aucun argument de l'appelant) :

1. Refuser de tourner si le processus n'est ni SYSTEM ni élevé.
2. Prendre dans `inbox\` la paire installateur plus `.asc` la plus récente dont le nom respecte les motifs connus. Tout le reste est ignoré puis supprimé.
3. La copier dans `work\`. Toutes les vérifications portent sur cette copie, jamais sur le fichier de l'inbox.
4. Vérifier : signature PGP par la clé épinglée ; `FileVersion` de l'installateur cohérent avec le nom de fichier ; version strictement supérieure à la version installée (version égale acceptée seulement si l'installation détectée est incohérente, par exemple exe manquant ou version du fichier différente de `DisplayVersion`) ; variante conforme à `config.json` (ou, en mode `auto`, à celle enregistrée dans `state.json` si elle existe) ; architecture compatible.
5. Vérifier qu'aucun qbittorrent.exe ne tourne, toutes sessions confondues. Sinon : écrire « busy » dans `state.json` et sortir.
6. Noter la présence du raccourci du Bureau public, lancer `installateur /S /D=<InstallLocation actuel>`, attendre, interpréter le code de sortie.
7. Contrôler que `DisplayVersion` et la version du fichier `qbittorrent.exe` correspondent à la cible. Recréer le raccourci du Bureau public s'il existait (si `restoreDesktopShortcut` est actif).
8. Écrire `state.json` et le journal, vider `work\` et `inbox\`.

Pourquoi ce découpage : le téléchargement profite du réseau et du proxy de l'utilisateur, la coupure de qBittorrent ne dure que le temps de l'installation, et la partie SYSTEM n'a besoin ni du réseau ni de faire confiance à quoi que ce soit venant d'un utilisateur : elle vérifie, puis exécute un installateur officiel signé et plus récent.

### 4.4 Choix du fichier à installer

- Lister les fichiers du dossier de la version (RSS SourceForge du dossier, page du dossier en secours).
- Règles dans une table testée, pas dispersées dans la logique :
  - variante libtorrent 2.x : `qbittorrent_{V}_x64_setup.exe` depuis la 5.2.0 (ou l'équivalent `arm64` s'il existe sur une machine ARM64) ;
  - variante libtorrent 1.2 : `qbittorrent_{V}_lt12_x64_setup.exe` depuis la 5.2.0 ;
  - pour les versions antérieures, établis les règles à partir des vraies listes de fichiers ;
  - tout fichier inconnu est ignoré et journalisé.
- Tests avec les listes de fichiers réelles de plusieurs versions (au minimum une 5.0.x, une 5.1.x, une 5.2.x et la 5.3.0, récupérées sur SourceForge et stockées en fixtures).

### 4.5 Détection de l'installation

- Lire `InstallLocation` et la clé Uninstall dans les deux vues du registre (2.4).
- Lire la version de `qbittorrent.exe` (`FileVersionInfo`). En cas de désaccord avec `DisplayVersion`, le fichier fait foi.
- Vérifier `CompanyName` et `ProductName` du binaire : si ce n'est pas le projet qBittorrent officiel (par exemple le fork « Enhanced Edition »), ne rien faire.
- Cas non gérés en v1, détectés et journalisés : installation portable (pas de clé de registre, dossier `profile` à côté de l'exe), ancienne installation 32 bits 4.x dans `Program Files (x86)`, version installée plus récente que celle publiée (bêta, build de développement).
- Variante installée (série libtorrent 1.x ou 2.x), dans cet ordre :
  1. `state.json` si l'outil a déjà fait une installation ;
  2. API Web `GET /api/v2/app/buildInfo` (champ libtorrent) si l'interface Web est configurée ;
  3. une heuristique hors ligne fiable, à trouver (par exemple une chaîne de version libtorrent dans `qbittorrent.exe`), validée par un test sur les deux installateurs réels de la 5.3.0 ;
  4. sinon, question pendant `setup`, libtorrent 2.x par défaut.

### 4.6 Tâches planifiées (dossier `\QbtAutoUpdate\`)

- `Check` : principal = groupe `BUILTIN\Users` (s'exécute pour chaque utilisateur connecté), jeton interactif, privilèges minimaux. Déclencheurs : ouverture de session (délai 3 min) et toutes les N heures (6 par défaut). Réglages : rattrapage si un passage a été manqué, seulement si le réseau est disponible, instances multiples ignorées, durée maximale 1 h, autorisé sur batterie.
- `Apply` : principal = SYSTEM, niveau le plus élevé, aucun déclencheur (à la demande uniquement), commande fixe `"%ProgramFiles%\QbtAutoUpdate\QbtAutoUpdate.exe" apply`. Descripteur de sécurité de la tâche : lecture et exécution pour `BUILTIN\Users` (ou Authenticated Users), afin qu'un processus non élevé puisse la démarrer via `IRegisteredTask.Run`. C'est ce qui donne zéro invite UAC, y compris sur un compte standard.
- Option `skipOnMeteredConnection` (API `ConnectionCost`), activée par défaut : pas de mise à jour automatique sur connexion limitée.

### 4.7 Sauvegarde de la configuration

Avant chaque mise à jour, une fois qBittorrent fermé : copier `%APPDATA%\qBittorrent\*.ini` dans `%LOCALAPPDATA%\QbtAutoUpdate\backups\<ancienne-version>_<date>\`, en gardant les 3 dernières. Option désactivée par défaut (volume possiblement important) : archiver aussi `%LOCALAPPDATA%\qBittorrent\BT_backup`. Gérer un profil personnalisé (`--profile` dans la ligne de commande mémorisée).

### 4.8 Configuration

- `config.json` (machine) : `mode` (`auto` ou `notify`), `checkIntervalHours`, `variant` (`auto`, `lt2` ou `lt12`), `minReleaseAgeHours` (0 par défaut), `skipOnMeteredConnection`, `closeTimeoutSeconds`, `restoreDesktopShortcut`, `backupConfig`, `backupTorrents`, `notifications` (`all`, `errors` ou `none`), `allowForceClose` (désactivé, voir section 5).
- `user.json` (utilisateur) : `skippedVersion`, `snoozeUntil`, interface Web optionnelle (`url`, `username`, mot de passe chiffré DPAPI).
- Validation du schéma ; valeurs par défaut sûres si le fichier est absent ou corrompu.

### 4.9 Notifications

- Disponible : « qBittorrent X.Y.Z est disponible », boutons « Mettre à jour maintenant », « Plus tard » (report de 24 h), « Ignorer cette version », et un lien vers le changelog (fichier README du dossier SourceForge ou page news du site).
- Succès : « qBittorrent mis à jour vers X.Y.Z ».
- Échec : message court et chemin du journal.
- Un clic sur un bouton relance l'exe via l'activation de la notification, même si le processus `check` est terminé.

## 5. Sécurité (non négociable)

- La tâche SYSTEM n'exécute qu'un binaire situé dans un dossier modifiable par les seuls administrateurs, et n'accepte aucun argument venant de l'appelant.
- Tout ce qui vient d'un utilisateur (inbox) est traité comme hostile : copie dans un dossier protégé, vérification, puis exécution depuis ce dossier.
- PGP : clé publique embarquée, empreinte complète épinglée, signature détachée vérifiée sur le fichier exact, clé ni révoquée ni expirée. Signature par une clé inconnue (rotation de clé côté qBittorrent) : échec fermé et notification claire invitant à mettre à jour QbtAutoUpdate.
- HTTPS uniquement. Hôtes autorisés : `www.qbittorrent.org`, `qbittorrent.github.io`, `sourceforge.net`, `downloads.sourceforge.net`, `*.dl.sourceforge.net`. Contrôle de l'hôte final après redirections.
- Jamais de retour à une version antérieure par le chemin automatique (seul `rollback`, en admin, le permet).
- Aucun secret dans les journaux.
- Ne jamais forcer la fermeture de qBittorrent. `allowForceClose` peut exister, désactivé par défaut, avec un avertissement dans le README.

## 6. Cas limites à gérer et à tester

- qBittorrent absent, portable, fork, ancienne installation 32 bits, version plus récente que la version publiée.
- `versions.json` injoignable sur une URL, sur les deux, ou au format modifié.
- Version annoncée mais fichiers pas encore sur SourceForge, ou miroir qui renvoie une page HTML.
- Variante demandée absente de la nouvelle version.
- qBittorrent qui refuse de se fermer, qui met longtemps, qui tourne élevé, ou qui tourne dans une autre session Windows.
- Antivirus qui verrouille l'installateur un instant (réessais).
- Coupure ou redémarrage pendant l'installation : au passage suivant, l'état incohérent est détecté et l'installation refaite.
- Espace disque insuffisant.
- Connexion limitée, PC sur batterie, session verrouillée.
- Compte standard (setup fait par un administrateur), plusieurs comptes sur le même PC.
- Versions à 3 ou 4 composants (`5.3.0`, `5.1.2.10`), normalisées sur 4 composants pour la comparaison.

## 7. Structure du dépôt

```
src/QbtAutoUpdate/            exe (WinExe)
  Program.cs                  routage des verbes
  Core/                       VersionSource, ReleaseResolver, Downloader, PgpVerifier,
                              InstallDetector, VariantDetector, Config, State
  Platform/                   RestartManager, ScheduledTasks, Toasts, Acl, Shortcuts,
                              ProcessTools, RegistryReader
  Flows/                      Setup, Uninstall, Check, Update, Apply, Rollback, Status
  Resources/                  qbittorrent-5B7CC9A2.asc, Strings.resx, Strings.fr.resx
  app.manifest
tests/QbtAutoUpdate.Tests/    xUnit
docs/                         ARCHITECTURE.md, DECISIONS.md, FINDINGS.md, TESTING.md
sandbox/                      configuration Windows Sandbox (.wsb) et script du scénario
.github/workflows/            build.yml, release.yml
README.md, README.fr.md, LICENSE (MIT), CHANGELOG.md
```

## 8. Tests

- Unitaires : comparaison de versions, lecture de `versions.json`, choix du fichier (listes réelles de plusieurs versions), règles de l'inbox, validation de la configuration, interprétation des codes de sortie.
- PGP : fichier signé par une clé générée pour les tests (cas valide, fichier altéré, mauvaise clé), plus un test d'intégration (catégorie `Integration`, hors CI par défaut) qui télécharge le vrai installateur 5.3.0 et son `.asc` et les vérifie avec la vraie clé.
- Scénario de bout en bout dans Windows Sandbox (fichier `.wsb` qui monte le dossier de build) :
  1. installer qBittorrent 5.2.3 (build par défaut), puis refaire le scénario avec la variante lt12 ;
  2. ajouter un torrent légal (une ISO Linux par exemple) et le laisser télécharger ;
  3. activer « réduire dans la zone de notification à la fermeture », « confirmer à la fermeture », le démarrage avec Windows et le raccourci Bureau ;
  4. `setup --mode auto`, puis lancer la tâche `Check` à la main ;
  5. attendus : qBittorrent en 5.3.0, même variante, fermé sans kill, relancé, torrent repris sans revérification complète, raccourci Bureau présent, clé Run intacte, aucune invite UAC après le setup, aucune fenêtre qui clignote ;
  6. refaire avec un compte standard, puis en mode `notify` ;
  7. déposer un installateur altéré dans l'inbox : refusé, journalisé, notifié.
- Consigner les résultats réels, y compris ce qui ne marche pas, dans `docs/TESTING.md`.

## 9. CI et publication

- GitHub Actions sur `windows-latest` : build, tests unitaires, publication `win-x64` et `win-arm64` en single-file self-contained.
- Sur tag `v*` : release GitHub avec les exe, un `SHA256SUMS.txt` et le changelog.
- README : ce que fait l'outil ; installation (télécharger, double-cliquer, accepter une invite UAC) ; désinstallation ; réglages ; effets de bord connus (`LongPathsEnabled`, règle de pare-feu recréée) ; sécurité (comment la signature est vérifiée) ; dépannage (où sont les journaux) ; conseil de désactiver la vérification de mises à jour intégrée de qBittorrent ; FAQ « pourquoi pas winget, Winget-AutoUpdate ou UniGetUI ? ». Pour cette FAQ, vérifie leur comportement actuel avant d'écrire quoi que ce soit à leur sujet ; l'angle de l'outil est d'être spécialisé : fermeture propre et relance de qBittorrent, conservation de la variante, vérification PGP, aucune dépendance à winget.
- Mentionner que l'exe n'est pas signé Authenticode au départ (SmartScreen avertira) ; pistes pour plus tard : SignPath Foundation (signature gratuite pour projets open source) et un manifeste winget.

## 10. Méthode de travail

- Avant tout code : relis les fichiers sources qBittorrent cités et les URL de la section 2, puis écris `docs/FINDINGS.md` (ce qui est confirmé, ce qui a changé).
- Propose ensuite un plan court et attends ma validation.
- Travaille par phases. À la fin de chacune : build OK, tests verts, résumé de ce qui marche et de ce qui reste, puis pause.
  1. Socle : solution, détection d'installation, dernière version, résolution du fichier, téléchargement, vérification PGP, `status`, `check --dry-run`.
  2. Fermeture propre (Restart Manager et interface Web), relance, sauvegarde de la configuration ; `update` testable en lançant l'exe élevé à la main (mode développement).
  3. Tâches planifiées, `apply` en SYSTEM, inbox, ACL, raccourci Bureau, `setup`, `uninstall`.
  4. Notifications, modes, report et version ignorée, localisation, `rollback`.
  5. CI, README, documentation, scénario Windows Sandbox.
- Ne me pose une question que si une décision bloque vraiment ; sinon choisis, et note le choix et sa raison dans `docs/DECISIONS.md`.
- Ne lance jamais de commande qui installe ou désinstalle qBittorrent, ou qui crée des tâches planifiées, sur ma machine sans me demander : les essais réels se font dans Windows Sandbox ou une VM.

## 11. Critères de fin (v1)

- [ ] Sur un Windows propre avec qBittorrent 5.2.3, après `setup` en mode auto, la mise à jour vers la dernière version se fait seule au passage suivant de la tâche, sans invite UAC ni fenêtre visible, avec fermeture propre et relance.
- [ ] La variante libtorrent 1.2 reste en 1.2, la 2.x reste en 2.x.
- [ ] Le raccourci Bureau et le démarrage avec Windows sont conservés.
- [ ] Mode notify : un clic sur la notification suffit.
- [ ] Un installateur non signé, altéré ou plus ancien est refusé.
- [ ] Fonctionne sur un compte standard après un setup fait par un administrateur.
- [ ] `uninstall` ne laisse ni tâche ni fichier (hors journaux si demandé).
- [ ] Rien de propre à une machine dans le code ni dans la configuration par défaut.

---

## Addendum (2026-10-22, from the maintainer, after the findings)

Ideally QbtAutoUpdate is either an installer you run once and forget, or an exe where you simply switch
automatic updates on or off. See DECISIONS.md D-14.

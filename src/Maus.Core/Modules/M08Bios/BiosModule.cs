using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;
using Maus.Core.Hardware;
using Maus.Core.Platform;
using Microsoft.Win32;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M08Bios;

/// <summary>
/// Module 8 — BIOS. Identifie la carte mère et l'âge du BIOS, vérifie le microcode Intel 0x12F et le déploiement
/// des certificats Secure Boot 2023, puis indique la page officielle du fabricant. Ne télécharge ni ne flashe rien.
/// </summary>
public sealed class BiosModule : IAuditModule
{
    internal const string BiosQuery = "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS";
    internal const string BoardQuery = "SELECT Manufacturer, Product, Version FROM Win32_BaseBoard";
    internal const string BitLockerQuery = "SELECT DriveLetter, ProtectionStatus, VolumeType FROM Win32_EncryptableVolume";
    internal const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    internal const string SecureBootStateKey = @"SYSTEM\CurrentControlSet\Control\SecureBoot\State";
    internal const string ServicingKey = @"SYSTEM\CurrentControlSet\Control\SecureBoot\Servicing";
    internal const string TpmWmiProvider = "Microsoft-Windows-TPM-WMI";
    internal const uint RequiredMicrocode = 0x12F;

    private const string IntelAdvisoryUrl = "https://www.intel.com/content/www/us/en/support/articles/000102331/processors.html";
    private const string RecoveryKeyUrl = "https://aka.ms/myrecoverykey";
    private static string UpdateCategory => T("Mise à jour du BIOS");
    private const string CertificatesCategory = "Certificats Secure Boot 2023";
    private static string PreparationCategory => T("Avant la mise à jour");
    private const int EventWindowDays = 180;

    /// <summary>Message affiché avant toute mise à jour du BIOS.</summary>
    internal static string UpdateWarning => T("La mise à jour du BIOS est une opération manuelle et sensible. Téléchargez-le uniquement sur le site du fabricant " +
        "et suivez ses vidéos officielles. Branchez un portable sur secteur et ne coupez jamais l'alimentation : la carte mère " +
        "pourrait devenir inutilisable. Notez d'abord votre clé BitLocker. Vos réglages BIOS, dont XMP/EXPO, peuvent revenir " +
        "aux valeurs par défaut. Bon à savoir : MAUS n'installe aucun BIOS, c'est vous qui lancez la mise à jour avec l'outil " +
        "du fabricant. Une erreur pendant l'opération peut provoquer une panne ou une perte de données, et certains fabricants " +
        "limitent leur garantie dans ce cas.");

    private readonly IFirmwareVariableReader _firmware;

    public BiosModule()
        : this(new WindowsFirmwareVariableReader())
    {
    }

    internal BiosModule(IFirmwareVariableReader firmware)
    {
        _firmware = firmware;
    }

    public string Id => "M08";

    public string Title => "BIOS";

    public int Order => 80;

    public Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        var bios = QueryAll(context.Cim, BiosQuery, CimScopes.Default);
        var board = QueryAll(context.Cim, BoardQuery, CimScopes.Default);
        var boardRow = board.Rows.Count > 0 ? board.Rows[0] : null;
        var target = BiosVendorDirectory.Resolve(
            context.Hardware.Manufacturer,
            context.Hardware.Model,
            context.Hardware.IsLaptop,
            boardRow?.GetString("Manufacturer")?.Trim() ?? context.Hardware.BoardManufacturer,
            boardRow?.GetString("Product")?.Trim() ?? context.Hardware.BoardProduct);

        var findings = new List<Finding> { DetectBiosAge(bios, target, context) };
        if (DetectMicrocode(context, target) is { } microcode)
        {
            findings.Add(microcode);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var (secureBootFinding, secureBootEnabled) = DetectSecureBoot(context.Registry);
        var servicing = ReadServicing(context.Registry);
        findings.Add(DetectCertificateStatus(servicing, secureBootEnabled, target));
        findings.Add(DetectFirmwareError(servicing, secureBootEnabled, target));
        findings.Add(DetectServicingEvents(context, secureBootEnabled, target));
        if (DetectDefaultKeys(context, servicing, target) is { } defaultKeys)
        {
            findings.Add(defaultKeys);
        }

        findings.Add(DetectBootManager(servicing));
        findings.Add(secureBootFinding);

        cancellationToken.ThrowIfCancellationRequested();
        findings.Add(DetectBitLocker(context.Cim));
        findings.Add(DetectBoard(board, boardRow, target));
        findings.Add(DescribeUpdateMethod(context, target));
        return Task.FromResult<IReadOnlyList<Finding>>(findings);
    }

    private static Finding DetectBiosAge(CimQuery bios, BiosTarget target, AuditContext context)
    {
        const string id = "M08.bios-age";
        var title = T("BIOS de moins de 12 mois");
        if (bios.Rows.Count == 0)
        {
            return bios.Denied
                ? Finding.AdminRequired(id, title, UpdateCategory)
                : Finding.Unknown(id, title, "Informations du BIOS illisibles (Win32_BIOS).", UpdateCategory);
        }

        var row = bios.Rows[0];
        var version = row.GetString("SMBIOSBIOSVersion")?.Trim();
        var versionText = string.IsNullOrEmpty(version) ? T("version inconnue") : $"version {version}";
        var date = row.GetDateTime("ReleaseDate");
        if (date is null)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = UpdateCategory,
                Status = FindingStatus.Unknown,
                Current = T("{0}, date inconnue", versionText),
                Expected = T("moins de 12 mois"),
                Explanation = T("Le BIOS ne publie pas sa date de sortie : son âge ne peut pas être calculé."),
                Advice = PageAdvice(target),
            };
        }

        var ageDays = (context.Now.LocalDateTime.Date - date.Value.Date).TotalDays;
        var months = Math.Max(0, (int)(ageDays / 30.44));
        var old = ageDays > 365;
        return new Finding
        {
            Id = id,
            Title = title,
            Category = UpdateCategory,
            Status = old ? FindingStatusExtensions.ForDeviation(Severity.Low) : FindingStatus.Ok,
            Severity = Severity.Low,
            Current = $"{versionText}, du {date.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} ({FormatAge(months)})",
            Expected = T("moins de 12 mois"),
            Explanation = T("Le BIOS est le micrologiciel de la carte mère. Ses mises à jour corrigent surtout la stabilité et la sécurité " +
                "(microcode du processeur, certificats Secure Boot) ; le gain de performances est rarement mesurable."),
            Advice = old ? T("Vérifiez s'il existe une version plus récente. {0} {1}", PageAdvice(target), UpdateWarning) : null,
        };
    }

    private static Finding? DetectMicrocode(AuditContext context, BiosTarget target)
    {
        var concerned = context.Hardware.Cpu.IsIntelRaptorLakeDesktop;
        var title = T("Microcode Intel 0x12F ou plus récent");
        uint? revision;
        uint? previous;
        try
        {
            revision = MicrocodeRevision.Parse(context.Registry.GetBinary(RegistryHive.LocalMachine, CpuKey, "Update Revision"));
            previous = MicrocodeRevision.Parse(context.Registry.GetBinary(RegistryHive.LocalMachine, CpuKey, "Previous Update Revision"));
        }
        catch (MausAccessDeniedException)
        {
            return concerned ? Finding.AdminRequired("M08.intel-microcode", title, UpdateCategory) : null;
        }

        if (!concerned)
        {
            return revision is null ? null : new Finding
            {
                Id = "M08.microcode",
                Title = T("Révision du microcode du processeur"),
                Category = UpdateCategory,
                Status = FindingStatus.Info,
                Current = MicrocodeRevision.Format(revision.Value),
                Explanation = T("Le microcode corrige le fonctionnement interne du processeur ; il est fourni par le BIOS et parfois par Windows. " +
                    "Votre processeur n'est pas concerné par l'alerte Intel 0x12F (Core i5, i7 et i9 de bureau de 13e et 14e génération)."),
            };
        }

        const string id = "M08.intel-microcode";
        var explanation = T("Les Core i5, i7 et i9 de bureau de 13e et 14e génération peuvent se dégrader avec le temps (instabilité croissante). " +
            "Intel demande un BIOS avec le microcode 0x12F ou ultérieur, recommande les réglages « Intel Default Settings » " +
            "et prolonge la garantie de ces processeurs de deux ans.");
        if (revision is null)
        {
            return Finding.Unknown(id, title, T("Révision du microcode illisible dans le registre (Update Revision)."), UpdateCategory);
        }

        if (revision < RequiredMicrocode)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = UpdateCategory,
                Status = FindingStatusExtensions.ForDeviation(Severity.High),
                Severity = Severity.High,
                Current = MicrocodeRevision.Format(revision.Value),
                Expected = T("0x12F ou plus"),
                Explanation = explanation,
                Advice = T("Mettez à jour le BIOS dès que possible. {0} Recommandations d'Intel : {1}. " +
                    "Gardez ensuite les « Intel Default Settings » dans le BIOS. {2}", PageAdvice(target), IntelAdvisoryUrl, UpdateWarning),
            };
        }

        if (previous is not null && previous < RequiredMicrocode)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = UpdateCategory,
                Status = FindingStatusExtensions.ForDeviation(Severity.Low),
                Severity = Severity.Low,
                Current = T("{0} chargé par Windows, {1} fourni par le BIOS", MicrocodeRevision.Format(revision.Value), MicrocodeRevision.Format(previous.Value)),
                Expected = T("0x12F ou plus, fourni par le BIOS"),
                Explanation = explanation + T(" Windows applique un microcode récent au démarrage, mais celui du BIOS semble plus ancien (lecture à confirmer)."),
                Advice = T("Intel conseille d'installer le microcode par le BIOS. {0} {1}", PageAdvice(target), UpdateWarning),
            };
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = UpdateCategory,
            Status = FindingStatus.Ok,
            Severity = Severity.High,
            Current = MicrocodeRevision.Format(revision.Value),
            Expected = T("0x12F ou plus"),
            Explanation = explanation,
        };
    }

    private static (Finding Finding, bool? Enabled) DetectSecureBoot(IRegistryReader registry)
    {
        const string id = "M08.secure-boot";
        const string title = "Secure Boot actif";
        var explanation = T("Secure Boot vérifie la signature de tout ce qui démarre avant Windows. Les certificats 2023 ne servent que s'il est actif.");
        int? value;
        try
        {
            value = registry.GetDword(RegistryHive.LocalMachine, SecureBootStateKey, "UEFISecureBootEnabled");
        }
        catch (MausAccessDeniedException)
        {
            return (Finding.AdminRequired(id, title, CertificatesCategory), null);
        }

        var finding = value switch
        {
            1 => new Finding
            {
                Id = id,
                Title = title,
                Category = CertificatesCategory,
                Status = FindingStatus.Ok,
                Current = T("actif"),
                Expected = T("actif"),
                Explanation = explanation,
            },
            0 => new Finding
            {
                Id = id,
                Title = title,
                Category = CertificatesCategory,
                Status = FindingStatusExtensions.ForDeviation(Severity.Medium),
                Severity = Severity.Medium,
                Current = T("désactivé"),
                Expected = T("actif"),
                Explanation = explanation + T(" Certains jeux en ligne (anti-triche) l'exigent aussi."),
                Advice = T("Secure Boot se réactive dans le BIOS (menu Boot ou Security ; le mode UEFI doit être actif et le CSM désactivé). " +
                    "Si un autre système (Linux par exemple) démarre sur ce PC, vérifiez d'abord qu'il est compatible avec Secure Boot."),
            },
            _ => new Finding
            {
                Id = id,
                Title = title,
                Category = CertificatesCategory,
                Status = FindingStatus.Unknown,
                Current = T("non renseigné"),
                Expected = T("actif"),
                Explanation = T("Windows ne publie pas l'état de Secure Boot (valeur UEFISecureBootEnabled absente) : " +
                    "PC démarré en mode BIOS hérité (CSM) ou firmware sans Secure Boot."),
            },
        };
        return (finding, value switch { 1 => true, 0 => false, _ => null });
    }

    private static Finding DetectCertificateStatus(ServicingState servicing, bool? secureBootEnabled, BiosTarget target)
    {
        const string id = "M08.ca2023-status";
        var title = T("Certificats Secure Boot 2023 déployés");
        var explanation = T("Les certificats Microsoft de 2011 qui valident le démarrage ont expiré en juin 2026 (KEK CA et UEFI CA) ; " +
            "le Windows Production PCA 2011 expire le 19 octobre 2026. Windows installe les certificats 2023 dans le firmware ; " +
            "certains PC exigent d'abord un BIOS récent du fabricant.");
        if (servicing.Denied)
        {
            return Finding.AdminRequired(id, title, CertificatesCategory);
        }

        var status = servicing.Status;
        if (status is null)
        {
            return secureBootEnabled == true
                ? Finding.Unknown(id, title, T("Windows n'a pas encore renseigné l'état du déploiement (valeur UEFICA2023Status absente)."), CertificatesCategory)
                : NotApplicable(id, title, T("non renseigné"), explanation);
        }

        if (status.Equals("Updated", StringComparison.OrdinalIgnoreCase))
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = CertificatesCategory,
                Status = FindingStatus.Ok,
                Severity = Severity.Medium,
                Current = T("à jour (Updated)"),
                Expected = T("à jour (Updated)"),
                Explanation = explanation,
            };
        }

        var current = status switch
        {
            _ when status.Equals("NotStarted", StringComparison.OrdinalIgnoreCase) => T("non commencé (NotStarted)"),
            _ when status.Equals("InProgress", StringComparison.OrdinalIgnoreCase) => T("en cours (InProgress)"),
            _ => status,
        };
        if (secureBootEnabled != true)
        {
            return NotApplicable(id, title, current, explanation);
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = CertificatesCategory,
            Status = FindingStatusExtensions.ForDeviation(Severity.Medium),
            Severity = Severity.Medium,
            Current = current,
            Expected = T("à jour (Updated)"),
            Explanation = explanation,
            Advice = T("Installez toutes les mises à jour de Windows Update, puis redémarrez deux fois. Si l'état ne progresse pas, " +
                "installez le dernier BIOS du fabricant. {0} L'état est aussi visible dans Sécurité Windows > Sécurité de l'appareil.", PageAdvice(target)),
        };
    }

    private static Finding DetectFirmwareError(ServicingState servicing, bool? secureBootEnabled, BiosTarget target)
    {
        const string id = "M08.ca2023-error";
        var title = T("Aucune erreur du firmware pendant la mise à jour des certificats");
        var explanation = T("Windows note ici le code d'erreur renvoyé par le firmware quand il refuse d'enregistrer les certificats 2023.");
        if (servicing.Denied)
        {
            return Finding.AdminRequired(id, title, CertificatesCategory);
        }

        if (servicing.Error is null or 0)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = CertificatesCategory,
                Status = FindingStatus.Ok,
                Severity = Severity.Medium,
                Current = T("aucune"),
                Expected = T("aucune"),
                Explanation = explanation,
            };
        }

        var severity = secureBootEnabled == true ? Severity.Medium : Severity.Info;
        return new Finding
        {
            Id = id,
            Title = title,
            Category = CertificatesCategory,
            Status = FindingStatusExtensions.ForDeviation(severity),
            Severity = severity,
            Current = $"code 0x{servicing.Error.Value:X8}",
            Expected = T("aucune"),
            Explanation = explanation,
            Advice = T("Consultez le journal Système (source TPM-WMI, événements 1795 et 1801), puis installez le dernier BIOS du fabricant. ") + PageAdvice(target),
        };
    }

    private static Finding DetectServicingEvents(AuditContext context, bool? secureBootEnabled, BiosTarget target)
    {
        const string id = "M08.secure-boot-events";
        var title = T("Journal Système : mise à jour Secure Boot appliquée");
        var explanation = T("Windows journalise l'installation des certificats dans le firmware : 1808 quand elle réussit, 1801 quand les certificats " +
            "ne sont pas encore appliqués, 1795 quand le firmware renvoie une erreur.");
        IReadOnlyList<EventRecordInfo> events;
        try
        {
            events = context.EventLogs.Query("System", TpmWmiProvider, [1801, 1795, 1808], context.Now.AddDays(-EventWindowDays).LocalDateTime);
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, CertificatesCategory);
        }
        catch (Exception ex) when (ex is DataSourceUnavailableException or EventLogException)
        {
            return Finding.Unknown(id, title, T("Journal Système illisible."), CertificatesCategory);
        }

        var lastSuccess = events.Where(e => e.Id == 1808).OrderByDescending(e => e.TimeCreated).FirstOrDefault();
        var lastFailure = events.Where(e => e.Id is 1801 or 1795).OrderByDescending(e => e.TimeCreated).FirstOrDefault();
        if (lastFailure is not null && (lastSuccess is null || lastFailure.TimeCreated > lastSuccess.TimeCreated))
        {
            var severity = secureBootEnabled == false ? Severity.Info : Severity.Medium;
            var what = lastFailure.Id == 1795 ? T("erreur renvoyée par le firmware") : T("certificats non appliqués au firmware");
            return new Finding
            {
                Id = id,
                Title = title,
                Category = CertificatesCategory,
                Status = FindingStatusExtensions.ForDeviation(severity),
                Severity = severity,
                Current = T("événement {0} ({1}) le {2}", lastFailure.Id, what, FormatDate(lastFailure.TimeCreated)),
                Expected = T("événement 1808, sans 1801 ni 1795 plus récent"),
                Explanation = explanation,
                Advice = T("Installez le dernier BIOS du fabricant, puis laissez Windows Update terminer le déploiement. ") + PageAdvice(target),
            };
        }

        if (lastSuccess is not null)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = CertificatesCategory,
                Status = FindingStatus.Ok,
                Severity = Severity.Medium,
                Current = T("événement 1808 (mise à jour appliquée) le {0}", FormatDate(lastSuccess.TimeCreated)),
                Expected = T("événement 1808, sans 1801 ni 1795 plus récent"),
                Explanation = explanation,
            };
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = CertificatesCategory,
            Status = FindingStatus.Info,
            Current = T("aucun événement 1801, 1795 ou 1808 depuis {0} jours", EventWindowDays),
            Expected = T("événement 1808"),
            Explanation = explanation,
        };
    }

    /// <summary>
    /// Si Windows démarre déjà avec le gestionnaire signé 2023, un retour aux clés Secure Boot par défaut du firmware
    /// sans « Windows UEFI CA 2023 » empêcherait le démarrage (constat publié par ASUS).
    /// </summary>
    private Finding? DetectDefaultKeys(AuditContext context, ServicingState servicing, BiosTarget target)
    {
        if (servicing.Capable != 2)
        {
            return null;
        }

        const string id = "M08.dbdefault";
        var title = T("Clés Secure Boot par défaut du firmware à jour");
        var explanation = T("Windows démarre avec le gestionnaire de démarrage signé 2023. Si les clés Secure Boot par défaut du firmware ignorent " +
            "« Windows UEFI CA 2023 », les restaurer dans le BIOS (ou laisser une mise à jour du BIOS le faire) empêcherait le PC de démarrer.");
        if (!context.IsElevated)
        {
            return Finding.AdminRequired(id, title, CertificatesCategory);
        }

        byte[]? dbDefault;
        try
        {
            dbDefault = _firmware.Read(FirmwareVariables.DbDefault, FirmwareVariables.GlobalVariableGuid);
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, CertificatesCategory);
        }
        catch (DataSourceUnavailableException)
        {
            return Finding.Unknown(id, title, T("Variables du firmware illisibles."), CertificatesCategory);
        }

        if (dbDefault is null)
        {
            return Finding.Unknown(id, title, T("Le firmware ne publie pas ses clés par défaut (variable dbDefault absente)."), CertificatesCategory);
        }

        var contains2023 = Encoding.ASCII.GetString(dbDefault).Contains("Windows UEFI CA 2023", StringComparison.Ordinal);
        return new Finding
        {
            Id = id,
            Title = title,
            Category = CertificatesCategory,
            Status = contains2023 ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(Severity.Medium),
            Severity = Severity.Medium,
            Current = contains2023 ? T("contiennent « Windows UEFI CA 2023 »") : T("sans « Windows UEFI CA 2023 »"),
            Expected = T("contiennent « Windows UEFI CA 2023 »"),
            Explanation = explanation,
            Advice = contains2023
                ? null
                : T("Ne restaurez pas les clés Secure Boot par défaut (« Restore Factory Keys », « Reset to default ») tant que le fabricant " +
                  "n'a pas publié de BIOS intégrant le certificat 2023. {0}", PageAdvice(target)),
        };
    }

    private static Finding DetectBootManager(ServicingState servicing)
    {
        const string id = "M08.boot-manager-2023";
        var title = T("Gestionnaire de démarrage signé 2023");
        if (servicing.Denied)
        {
            return Finding.AdminRequired(id, title, CertificatesCategory);
        }

        var current = servicing.Capable switch
        {
            2 => T("certificat 2023 en base DB, démarrage par le gestionnaire signé 2023"),
            1 => T("certificat 2023 en base DB, gestionnaire signé 2011 encore utilisé"),
            0 => T("certificat 2023 absent de la base DB"),
            null => T("non renseigné"),
            var other => $"valeur {other}",
        };
        return new Finding
        {
            Id = id,
            Title = title,
            Category = CertificatesCategory,
            Status = servicing.Capable == 2 ? FindingStatus.Ok : FindingStatus.Info,
            Current = current,
            Expected = T("certificat 2023 en base DB, démarrage par le gestionnaire signé 2023"),
            Explanation = T("Indication donnée par Windows « à titre indicatif » (WindowsUEFICA2023Capable) : " +
                "elle montre si le PC démarre déjà avec le gestionnaire de démarrage signé par le certificat Windows UEFI CA 2023."),
        };
    }

    private static Finding DetectBitLocker(ICimReader cim)
    {
        const string id = "M08.bitlocker";
        var title = T("BitLocker sur le disque système");
        var explanation = T("Une mise à jour du BIOS change les mesures de démarrage : si BitLocker est actif et non suspendu, " +
            "Windows demandera la clé de récupération au démarrage suivant.");
        var query = QueryAll(cim, BitLockerQuery, CimScopes.BitLocker);
        if (query.Denied)
        {
            return Finding.AdminRequired(id, title, PreparationCategory);
        }

        var system = query.Rows.FirstOrDefault(r => r.GetInt64("VolumeType") == 0)
            ?? query.Rows.FirstOrDefault(r => string.Equals(r.GetString("DriveLetter"), "C:", StringComparison.OrdinalIgnoreCase));
        if (system is null)
        {
            return new Finding
            {
                Id = id,
                Title = title,
                Category = PreparationCategory,
                Status = FindingStatus.Info,
                Current = query.Unavailable ? T("non disponible sur ce PC") : T("aucun volume chiffrable trouvé"),
                Explanation = explanation,
            };
        }

        var protection = system.GetInt64("ProtectionStatus");
        return new Finding
        {
            Id = id,
            Title = title,
            Category = PreparationCategory,
            Status = FindingStatus.Info,
            Current = protection switch
            {
                1 => T("protection active"),
                0 => T("protection désactivée"),
                _ => T("état inconnu"),
            },
            Explanation = explanation,
            Advice = protection == 1
                ? T("Avant de mettre à jour le BIOS, notez votre clé de récupération ({0}), puis suspendez la protection " +
                  "pour deux redémarrages : elle reprend d'elle-même ensuite.", RecoveryKeyUrl)
                : null,
            Fixable = protection == 1,
        };
    }

    private static Finding DetectBoard(CimQuery board, CimRow? boardRow, BiosTarget target)
    {
        const string id = "M08.board";
        var title = target.IsBrandedPc ? T("PC et page officielle du fabricant") : T("Carte mère et page officielle du fabricant");
        if (boardRow is null && target.Model is null)
        {
            return board.Denied
                ? Finding.AdminRequired(id, title, UpdateCategory)
                : Finding.Unknown(id, title, T("Modèle de la carte mère illisible (Win32_BaseBoard)."), UpdateCategory);
        }

        var revision = boardRow?.GetString("Version")?.Trim();
        var current = target.DisplayName;
        if (target.IsBrandedPc && boardRow?.GetString("Product")?.Trim() is { Length: > 0 } product && !BiosVendorDirectory.IsPlaceholder(product))
        {
            current += T(" (carte mère {0})", product);
        }

        if (!BiosVendorDirectory.IsPlaceholder(revision))
        {
            current += T(", révision {0}", revision);
        }

        return new Finding
        {
            Id = id,
            Title = title,
            Category = UpdateCategory,
            Status = FindingStatus.Info,
            Current = current,
            Explanation = T("MAUS identifie le modèle pour indiquer la bonne page du fabricant, sans jamais transmettre le numéro de série."),
            Advice = PageAdvice(target),
        };
    }

    private static Finding DescribeUpdateMethod(AuditContext context, BiosTarget target)
    {
        var checks = T("Après la mise à jour, revérifiez XMP/EXPO (Module 10), Resizable BAR (Module 9), la virtualisation et VBS (Module 13) " +
            "et Secure Boot (Module 1).");
        if (context.Hardware.Cpu.Vendor == HardwareVendor.Amd)
        {
            checks += T(" Processeur AMD : au redémarrage, le PC peut afficher « fTPM NV corrupted ». Répondre Y réinitialise le fTPM : " +
                "la clé BitLocker sera demandée et le code PIN de connexion Windows devra être recréé.");
        }

        return new Finding
        {
            Id = "M08.update-method",
            Title = T("Mettre à jour le BIOS : méthode conseillée"),
            Category = PreparationCategory,
            Status = FindingStatus.Info,
            Current = target.Tool,
            Explanation = target.IsBrandedPc
                ? T("Sur un PC de marque, l'application officielle du fabricant propose le BIOS adapté au modèle. MAUS ne télécharge ni n'installe aucun BIOS.")
                : T("Sur un PC monté, le BIOS se met à jour depuis son propre outil, avec le fichier téléchargé sur la page de la carte mère. " +
                  "MAUS ne télécharge ni n'installe aucun BIOS."),
            Advice = $"{UpdateWarning} {checks}",
        };
    }

    private static Finding NotApplicable(string id, string title, string current, string explanation) => new()
    {
        Id = id,
        Title = title,
        Category = CertificatesCategory,
        Status = FindingStatus.Info,
        Current = current,
        Expected = T("à jour (Updated)"),
        Explanation = explanation + T(" Secure Boot étant désactivé ou non pris en charge, ce point reste informatif."),
    };

    private static string PageAdvice(BiosTarget target) => target.Url is { } url
        ? $"Page officielle : {url}."
        : T("Cherchez « {0} BIOS » sur le site officiel du fabricant.", target.DisplayName);

    private static string FormatDate(DateTime date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    internal static string FormatAge(int months)
    {
        if (months < 1)
        {
            return T("moins d'un mois");
        }

        if (months < 12)
        {
            return $"{months} mois";
        }

        var years = months / 12;
        var rest = months % 12;
        var yearsText = years == 1 ? "1 an" : $"{years} ans";
        return rest == 0 ? yearsText : T("{0} et {1} mois", yearsText, rest);
    }

    private static ServicingState ReadServicing(IRegistryReader registry)
    {
        try
        {
            return new ServicingState(
                registry.GetString(RegistryHive.LocalMachine, ServicingKey, "UEFICA2023Status")?.Trim(),
                registry.GetDword(RegistryHive.LocalMachine, ServicingKey, "WindowsUEFICA2023Capable"),
                registry.GetDword(RegistryHive.LocalMachine, ServicingKey, "UEFICA2023Error"),
                Denied: false);
        }
        catch (MausAccessDeniedException)
        {
            return new ServicingState(null, null, null, Denied: true);
        }
    }

    private static CimQuery QueryAll(ICimReader cim, string wql, string scope)
    {
        try
        {
            return new CimQuery(cim.Query(wql, scope), Denied: false, Unavailable: false);
        }
        catch (MausAccessDeniedException)
        {
            return new CimQuery([], Denied: true, Unavailable: false);
        }
        catch (DataSourceUnavailableException)
        {
            return new CimQuery([], Denied: false, Unavailable: true);
        }
    }

    private sealed record CimQuery(IReadOnlyList<CimRow> Rows, bool Denied, bool Unavailable);

    private sealed record ServicingState(string? Status, int? Capable, int? Error, bool Denied);
}

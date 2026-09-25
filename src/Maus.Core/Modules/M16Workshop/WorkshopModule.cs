using System.Globalization;
using Maus.Core.Platform;
using Maus.Core.Workshop;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M16Workshop;

/// <summary>
/// Module 16 — Atelier matériel : identité du processeur, tensions de la RAM, seuils de la carte graphique,
/// usure de la batterie et virtualisation, comparés aux seuils de sécurité. Lecture seule, sans pilote.
/// </summary>
public sealed class WorkshopModule : IAuditModule
{
    internal const string VirtualizationQuery = "SELECT VirtualizationFirmwareEnabled FROM Win32_Processor";
    internal const string HypervisorQuery = "SELECT HypervisorPresent FROM Win32_ComputerSystem";

    private static readonly Lazy<SafetyLimits> Limits = new(SafetyLimits.Load);

    private readonly ICpuIdSource _cpuId;
    private readonly INvmlSource _nvml;

    public WorkshopModule()
        : this(new X86CpuIdSource(), new WindowsNvmlSource())
    {
    }

    internal WorkshopModule(ICpuIdSource cpuId, INvmlSource nvml)
    {
        _cpuId = cpuId;
        _nvml = nvml;
    }

    public string Id => "M16";

    public string Title => T("Atelier matériel");

    public int Order => 160;

    public Task<IReadOnlyList<Finding>> DetectAsync(AuditContext context, CancellationToken cancellationToken)
    {
        var inventory = HardwareInventoryReader.Read(context, _cpuId, _nvml);
        var limits = Limits.Value;
        var findings = new List<Finding> { DescribeCpu(inventory.Cpu, limits) };
        findings.Add(DetectVirtualization(context.Cim, inventory.Cpu));
        findings.AddRange(DetectMemoryVoltage(inventory.Memory, limits));
        cancellationToken.ThrowIfCancellationRequested();

        var index = 0;
        foreach (var gpu in inventory.Gpus.Where(g => g.Nvidia is not null))
        {
            findings.Add(DetectGpuTemperature(gpu, index, limits));
            findings.Add(DetectGpuPowerLimit(gpu, index));
            index++;
        }

        findings.AddRange(inventory.Batteries.Select((battery, i) => DetectBattery(battery, i, limits)));
        return Task.FromResult<IReadOnlyList<Finding>>(findings);
    }

    private static Finding DescribeCpu(CpuIdentity cpu, SafetyLimits limits)
    {
        var limit = limits.ForCpu(cpu.Name);
        var details = new List<string> { T("{0} cœurs, {1} threads", cpu.Cores, cpu.Threads) };
        if (cpu.BaseClockMhz > 0)
        {
            details.Add(T("{0} MHz de base", cpu.BaseClockMhz));
        }

        if (cpu.Hybrid)
        {
            details.Add(T("architecture hybride (cœurs performance et efficacité)"));
        }

        if (cpu.InstructionSets.Count > 0)
        {
            details.Add(string.Join(", ", cpu.InstructionSets.Where(s => s is "AVX2" or "AVX-512" or "SHA" or "AES")));
        }

        return new Finding
        {
            Id = "M16.cpu",
            Title = cpu.Name,
            Category = T("Processeur"),
            Status = FindingStatus.Info,
            Current = string.Join(" · ", details.Where(d => d.Length > 0)),
            Expected = limit is null ? null : T("température maximale prévue par le fabricant : {0} °C", limit.MaxC),
            Explanation = T("Identité lue directement dans le processeur (instruction CPUID) : elle ne dépend ni de Windows ni du BIOS.") +
                          (cpu.RunningInVirtualMachine ? " " + T("MAUS tourne dans une machine virtuelle : le matériel affiché est celui qu'elle simule.") : string.Empty) +
                          (limit is null ? string.Empty : " " + T("Source du seuil de température : {0}.", limit.Source)),
            Advice = T("La tension et la température réelles du processeur ne se lisent qu'avec un pilote : MAUS ne les invente pas."),
        };
    }

    private static Finding DetectVirtualization(ICimReader cim, CpuIdentity cpu)
    {
        const string id = "M16.virtualization";
        var title = T("Virtualisation du processeur (VT-x / AMD-V)");
        if (!cpu.VirtualizationCapable)
        {
            return Finding.Unknown(id, title, T("Le processeur n'annonce pas la virtualisation, ou elle n'a pas pu être lue."), T("Processeur"));
        }

        bool? enabled;
        try
        {
            var hypervisor = cim.Query(HypervisorQuery) is { Count: > 0 } system ? system[0].GetBool("HypervisorPresent") : null;
            var firmware = cim.Query(VirtualizationQuery) is { Count: > 0 } cpus ? cpus[0].GetBool("VirtualizationFirmwareEnabled") : null;
            enabled = hypervisor == true ? true : firmware;
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException)
        {
            enabled = null;
        }

        return enabled is null
            ? Finding.Unknown(id, title, T("État de la virtualisation illisible."), T("Processeur"))
            : new Finding
            {
                Id = id,
                Title = title,
                Category = T("Processeur"),
                Status = enabled.Value ? FindingStatus.Ok : FindingStatus.Info,
                Current = enabled.Value ? T("activée") : T("désactivée dans le BIOS"),
                Expected = T("au choix"),
                Explanation = T("La virtualisation sert au Bac à sable Windows, à WSL2, aux machines virtuelles et à certaines protections de Windows (intégrité de la mémoire, voir Module 13)."),
                Advice = enabled.Value ? null : T("Pour l'activer : BIOS, option « Intel Virtualization Technology » ou « SVM Mode » (guidé par le Module 8, jamais modifié par MAUS)."),
            };
    }

    private static IEnumerable<Finding> DetectMemoryVoltage(IReadOnlyList<MemoryModuleInfo> modules, SafetyLimits limits)
    {
        var measured = modules.Where(m => m.ConfiguredMillivolts is not null).ToList();
        if (modules.Count == 0)
        {
            yield break;
        }

        const string id = "M16.memory-voltage";
        var title = T("Tension de la mémoire vive");
        if (measured.Count == 0)
        {
            yield return Finding.Unknown(id, title, T("Le BIOS n'indique pas la tension des barrettes (SMBIOS)."), T("Mémoire vive"));
            yield break;
        }

        var worst = measured.MaxBy(m => m.ConfiguredMillivolts)!;
        var limit = limits.ForMemory(worst.Generation);
        var volts = Volts(worst.ConfiguredMillivolts!.Value);
        if (limit is null)
        {
            yield return new Finding
            {
                Id = id,
                Title = title,
                Category = T("Mémoire vive"),
                Status = FindingStatus.Info,
                Current = volts,
                Explanation = T("Type de mémoire sans seuil connu dans le catalogue de MAUS."),
            };
            yield break;
        }

        var level = SafetyRules.MemoryVoltage(worst.ConfiguredMillivolts.Value, limit);
        var severity = level switch
        {
            SafetyLevel.Dangerous => Severity.High,
            SafetyLevel.Elevated => Severity.Medium,
            _ => Severity.Info,
        };
        yield return new Finding
        {
            Id = id,
            Title = title,
            Category = T("Mémoire vive"),
            Status = level == SafetyLevel.Normal ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(severity),
            Severity = severity,
            Current = T("{0} (DDR{1}, {2})", volts, limit.Generation, worst.Slot),
            Expected = T("{0} nominal ; repère : {1} au plus", Volts(limit.NominalMv), Volts(limit.ElevatedAboveMv)),
            Explanation = level switch
            {
                SafetyLevel.Dangerous => T("Cette tension dépasse le maximum absolu de la norme : risque d'usure prématurée des barrettes et d'instabilité."),
                SafetyLevel.Elevated => T("Cette tension dépasse le repère publié ci-dessous : c'est souvent le signe d'un surcadençage manuel, ou d'un kit très rapide. Surveillez la stabilité et la température."),
                _ => T("La tension des barrettes est dans la plage normale."),
            } + " " + T("Source : {0}", limit.Source),
            Advice = level == SafetyLevel.Normal
                ? null
                : T("Revenez au profil XMP/EXPO d'origine dans le BIOS (voir Module 10), puis lancez le test de la RAM de l'atelier pour vérifier la stabilité."),
        };
    }

    private static Finding DetectGpuTemperature(GpuIdentity gpu, int index, SafetyLimits limits)
    {
        var state = gpu.Nvidia!;
        var id = string.Create(CultureInfo.InvariantCulture, $"M16.gpu-temperature.{index}");
        var title = T("Température de {0}", gpu.Info.Name);
        if (state.TemperatureC is not { } temperature)
        {
            return Finding.Unknown(id, title, T("Température non fournie par la carte."), T("Carte graphique"));
        }

        var thresholds = new List<string>();
        if (state.SlowdownTemperatureC is { } slowdown)
        {
            thresholds.Add(T("ralentit à {0} °C", slowdown));
        }

        if (state.ShutdownTemperatureC is { } shutdown)
        {
            thresholds.Add(T("s'éteint à {0} °C", shutdown));
        }

        var hot = state.SlowdownTemperatureC is { } limit && temperature >= limit - limits.GpuIdleHotMarginC && (state.UtilizationPercent ?? 0) < 20;
        return new Finding
        {
            Id = id,
            Title = title,
            Category = T("Carte graphique"),
            Status = hot ? FindingStatusExtensions.ForDeviation(Severity.Medium) : FindingStatus.Ok,
            Severity = hot ? Severity.Medium : Severity.Info,
            Current = T("{0} °C (utilisation {1} %)", temperature, state.UtilizationPercent ?? 0),
            Expected = thresholds.Count == 0 ? null : T("seuils donnés par la carte : {0}", string.Join(", ", thresholds)),
            Explanation = T("Les seuils viennent de la carte elle-même (NVML, pilote NVIDIA) : au-delà, elle baisse ses fréquences, puis s'éteint pour se protéger."),
            Advice = hot
                ? T("Au repos, la carte est déjà proche de son seuil de ralentissement : dépoussiérez-la, vérifiez que ses ventilateurs tournent et que le boîtier est bien aéré.")
                : null,
        };
    }

    private static Finding DetectGpuPowerLimit(GpuIdentity gpu, int index)
    {
        var state = gpu.Nvidia!;
        var id = string.Create(CultureInfo.InvariantCulture, $"M16.gpu-power.{index}");
        var title = T("Limite de puissance de {0}", gpu.Info.Name);
        if (state.PowerLimitWatts is not { } applied || state.DefaultPowerLimitWatts is not { } standard || standard <= 0)
        {
            return Finding.Unknown(id, title, T("Limite de puissance non fournie par la carte."), T("Carte graphique"));
        }

        var ratio = applied / standard;
        var current = T("{0:0} W appliqués, {1:0} W par défaut", applied, standard);
        var (status, explanation) = ratio switch
        {
            > 1.02 => (FindingStatus.Info, T("La limite de puissance a été relevée au-dessus de la valeur d'origine ({0:0} % de plus) : la carte est surcadencée. Elle chauffe et consomme davantage.", (ratio - 1) * 100)),
            < 0.9 => (FindingStatus.Info, T("La carte est limitée à {0:0} % de sa puissance d'origine (réglage d'économie, portable, ou sous-cadençage volontaire).", ratio * 100)),
            _ => (FindingStatus.Ok, T("La carte utilise sa limite de puissance d'origine.")),
        };
        return new Finding
        {
            Id = id,
            Title = title,
            Category = T("Carte graphique"),
            Status = status,
            Current = current + (state.PowerWatts is { } now ? " · " + T("{0:0} W en ce moment", now) : string.Empty),
            Expected = state.MaxPowerLimitWatts is { } max ? T("maximum autorisé par la carte : {0:0} W", max) : null,
            Explanation = explanation,
        };
    }

    private static Finding DetectBattery(BatteryIdentity battery, int index, SafetyLimits limits)
    {
        var id = string.Create(CultureInfo.InvariantCulture, $"M16.battery.{index}");
        var title = T("Usure de la batterie");
        if (battery.HealthPercent is not { } health)
        {
            return Finding.Unknown(id, title, T("Capacités de la batterie illisibles."), T("Batterie"));
        }

        var worn = health < limits.BatteryWornBelowPercent;
        return new Finding
        {
            Id = id,
            Title = title,
            Category = T("Batterie"),
            Status = worn ? FindingStatusExtensions.ForDeviation(Severity.Medium) : FindingStatus.Ok,
            Severity = worn ? Severity.Medium : Severity.Info,
            Current = T("{0} % de la capacité d'origine ({1} / {2} mWh)", health, battery.FullChargeCapacityMwh, battery.DesignCapacityMwh),
            Expected = T("au moins {0} %", limits.BatteryWornBelowPercent),
            Explanation = T("Une batterie perd de la capacité avec les cycles de charge et la chaleur : l'autonomie baisse d'autant."),
            Advice = worn ? T("Autonomie réduite : pensez à la faire remplacer (pièce d'origine ou compatible de qualité), surtout si le PC s'éteint sans prévenir.") : null,
        };
    }

    private static string Volts(int millivolts) => (millivolts / 1000.0).ToString("0.00 V", Culture);
}

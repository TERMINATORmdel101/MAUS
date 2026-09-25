using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Fixes;

/// <summary>Lit et écrit un réglage quelle que soit sa nature (registre, paramètre système).</summary>
public interface ISettingsAccessor
{
    /// <summary>Valeur actuelle, ou <c>null</c> si elle est absente.</summary>
    /// <exception cref="MausAccessDeniedException">Lecture refusée.</exception>
    /// <exception cref="InvalidOperationException">Paramètre système illisible.</exception>
    SettingValue? Read(SettingKey key);

    /// <summary>Vrai si la clé de registre qui porte la valeur existe (pour la supprimer au retour arrière si MAUS l'a créée).</summary>
    bool ContainerExists(SettingKey key);

    /// <summary>Écrit la valeur, ou la supprime si <paramref name="value"/> est <c>null</c>.</summary>
    /// <exception cref="MausAccessDeniedException">Écriture refusée.</exception>
    /// <exception cref="InvalidOperationException">Windows a refusé le paramètre système.</exception>
    void Write(SettingKey key, SettingValue? value);

    /// <summary>Supprime la clé de registre si elle est vide.</summary>
    void RemoveContainerIfEmpty(SettingKey key);
}

public sealed class SettingsAccessor(
    IRegistryReader registryReader,
    IRegistryWriter registryWriter,
    ISystemParametersReader parametersReader,
    ISystemParametersWriter parametersWriter,
    IPowerSchemeAccessor? power = null) : ISettingsAccessor
{
    private readonly IPowerSchemeAccessor _power = power ?? new Win32PowerSchemeAccessor();

    public SettingValue? Read(SettingKey key)
    {
        switch (key.Kind)
        {
            case SettingKind.Registry:
                var raw = registryReader.GetValue(key.RegistryHive, key.Path!, key.Name!);
                return raw is null ? null : SettingValue.FromRegistry(raw, registryReader.GetValueKind(key.RegistryHive, key.Path!, key.Name!));
            case SettingKind.SystemParameter:
                return SettingValue.Bool(parametersReader.GetBool(key.SpiGet)
                    ?? throw new InvalidOperationException(T("Paramètre système illisible : {0}", key)));
            case SettingKind.MinimizeAnimation:
                return SettingValue.Bool(parametersReader.GetMinimizeAnimation()
                    ?? throw new InvalidOperationException(T("Paramètre système illisible : {0}", key)));
            case SettingKind.ActivePowerScheme:
                return SettingValue.Text((_power.GetActiveScheme() ?? throw new InvalidOperationException(T("Mode de gestion actif illisible."))).ToString("D"));
            default:
                throw new NotSupportedException(key.Kind.ToString());
        }
    }

    public bool ContainerExists(SettingKey key) =>
        key.Kind != SettingKind.Registry || registryReader.KeyExists(key.RegistryHive, key.Path!);

    public void Write(SettingKey key, SettingValue? value)
    {
        switch (key.Kind)
        {
            case SettingKind.Registry when value is null:
                registryWriter.DeleteValue(key.RegistryHive, key.Path!, key.Name!);
                break;
            case SettingKind.Registry:
                registryWriter.SetValue(key.RegistryHive, key.Path!, key.Name!, value.ToRegistryObject(), value.Kind);
                break;
            case SettingKind.SystemParameter:
                if (!parametersWriter.SetBool(key.SpiSet, RequireValue(key, value).AsBool(), key.SpiUseUiParam))
                {
                    throw new InvalidOperationException(T("Windows a refusé le réglage {0}.", key));
                }

                break;
            case SettingKind.MinimizeAnimation:
                if (!parametersWriter.SetMinimizeAnimation(RequireValue(key, value).AsBool()))
                {
                    throw new InvalidOperationException(T("Windows a refusé le réglage {0}.", key));
                }

                break;
            case SettingKind.ActivePowerScheme:
                if (!Guid.TryParse(RequireValue(key, value).Data, out var scheme) || !_power.SetActiveScheme(scheme))
                {
                    throw new InvalidOperationException(T("Windows a refusé le mode de gestion {0} (absent de ce PC ?).", value?.Data));
                }

                break;
            default:
                throw new NotSupportedException(key.Kind.ToString());
        }
    }

    public void RemoveContainerIfEmpty(SettingKey key)
    {
        if (key.Kind == SettingKind.Registry)
        {
            registryWriter.DeleteKeyIfEmpty(key.RegistryHive, key.Path!);
        }
    }

    private static SettingValue RequireValue(SettingKey key, SettingValue? value) =>
        value ?? throw new InvalidOperationException(T("Un paramètre système ne peut pas être supprimé : {0}", key));
}

using System.Reflection;

namespace App.Services;

/// <summary>Reads release metadata without substituting a fixed development version.</summary>
public static class ApplicationVersionReader
{
    public static string Read(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return Select(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.GetName().Version);
    }

    public static string Select(string? informationalVersion, Version? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var value = informationalVersion.Split('+', 2)[0];
            if (value.Length <= 128
                && value.Length != 0
                && value.All(character => char.IsAsciiLetterOrDigit(character)
                    || character is '.' or '-'))
            {
                return value;
            }
        }

        return assemblyVersion?.ToString() ?? "unknown";
    }
}

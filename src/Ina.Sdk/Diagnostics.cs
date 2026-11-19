namespace Ina.Sdk;

/// <summary>
/// Codes of the errors that the tasks report. INA1xxx are configuration problems, which the targets of the SDK
/// (Ina.Sdk.targets) report as well; INA2xxx are failures of the link itself.
/// </summary>
internal static class Diagnostics
{
    /// <summary>The runtime identifier is not one Ina can produce executables for.</summary>
    public const string UnsupportedRuntimeIdentifier = "INA1002";

    /// <summary>The linker rejected the inputs, e.g. because of an undefined symbol.</summary>
    public const string LinkFailed = "INA2001";

    /// <summary>An object file, a static library or the exports file cannot be read.</summary>
    public const string UnreadableInput = "INA2002";
}

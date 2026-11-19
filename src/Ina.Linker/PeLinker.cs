using Ina.Linker.Pe;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ina.Linker;

/// <summary>
/// A static linker for Windows executables (PE32+) written in managed code. It links COFF objects and static
/// libraries without the Microsoft linker, the Windows SDK or the C runtime libraries of Visual C++: imports are
/// bound by name through an import table, and the pieces of the C runtime that are normally linked statically
/// are generated.
/// </summary>
public static class PeLinker
{
    public static LinkResult Link(PeLinkOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new PeLinkSession(options, logger ?? NullLogger.Instance).Run();
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ina.Linker;

/// <summary>
/// A static linker for ELF executables written in managed code. It links relocatable objects and static libraries
/// into a dynamically linked executable without requiring a native toolchain or a sysroot of the target system.
/// </summary>
public static class ElfLinker
{
    public static LinkResult Link(LinkOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new LinkSession(options, logger ?? NullLogger.Instance).Run();
    }
}

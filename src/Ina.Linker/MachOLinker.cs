using Ina.Linker.MachO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ina.Linker;

/// <summary>
/// A static linker for Mach-O executables (macOS) written in managed code. It links relocatable objects and static
/// libraries into a dynamically linked, ad-hoc signed executable without requiring Apple's toolchain or SDK, so it
/// runs on any host.
/// </summary>
/// <remarks>
/// The options are the same as for <see cref="ElfLinker"/>, with these differences: symbol names are given the way
/// C code spells them (<c>main</c>), and the linker adds the leading underscore of Mach-O; the output is always
/// position independent, because macOS does not run anything else on Apple silicon.
/// </remarks>
public static class MachOLinker
{
    public static LinkResult Link(LinkOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.PositionIndependent)
        {
            throw new LinkerException("macOS executables are always position independent; fixed-address executables are not supported");
        }

        return new MachLinkSession(options, logger ?? NullLogger.Instance).Run();
    }
}

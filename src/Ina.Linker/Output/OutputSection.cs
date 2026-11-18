using Ina.Linker.Elf;
using Ina.Linker.Input;

namespace Ina.Linker.Output;

/// <summary>An output section that concatenates input sections of the same name.</summary>
internal sealed class OutputSection(string name, uint type, ulong flags) : OutputChunk(name, type, flags, 1)
{
    public List<InputSection> Members { get; } = [];

    public void Add(InputSection section)
    {
        Members.Add(section);
        section.Parent = this;

        // PROGBITS wins over NOBITS: zero-initialized members are then simply written out as zeros.
        if (section.Type != SectionType.NoBits && Type == SectionType.NoBits)
        {
            Type = section.Type;
        }

        Flags |= section.Flags & (SectionFlags.Write | SectionFlags.Alloc | SectionFlags.ExecInstr | SectionFlags.Tls);
    }

    /// <summary>Assigns the offsets of the members and computes the size and alignment of the section.</summary>
    public void Finish()
    {
        ulong offset = 0;
        ulong alignment = 1;
        foreach (var member in Members)
        {
            offset = AlignUp(offset, member.Alignment);
            member.Offset = offset;
            offset += member.Size;
            alignment = Math.Max(alignment, member.Alignment);
        }

        Size = offset;
        Alignment = alignment;
        if (Type is SectionType.InitArray or SectionType.FiniArray or SectionType.PreInitArray)
        {
            EntrySize = 8;
        }
    }

    public override void Write(Span<byte> destination, LinkContext context)
    {
        foreach (var member in Members)
        {
            if (member.IsNoBits)
            {
                continue;
            }

            var target = destination.Slice((int)member.Offset, (int)member.Size);
            member.Content.CopyTo(target);
            context.ApplyRelocations(member, target);
        }
    }

    private static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) & ~(alignment - 1);
}

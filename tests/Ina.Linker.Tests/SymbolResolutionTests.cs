using Ina.Linker.Elf;
using Ina.Linker.Tests.Support;

namespace Ina.Linker.Tests;

public class SymbolResolutionTests : ElfLinkerTestBase
{
    [Fact]
    public void PullsArchiveMembersThatDefineUndefinedSymbols()
    {
        // The second member refers to a symbol that exists nowhere; it must not be loaded because nothing needs it.
        var archive = ArchiveBuilder.Build(
            withSymbolTable: true,
            ("helper.o", Code.Function("helper", Code.Return(5))),
            ("a_member_with_a_rather_long_name.o", Code.Function("unused", [0xe8, 0x00, 0x00, 0x00, 0x00, 0xc3])));

        Assert.Equal(5, Run(Link(Code.MainCalling("helper"), archive)).ExitCode);
    }

    [Fact]
    public void ArchiveMembersCanPullInFurtherMembers()
    {
        var archive = ArchiveBuilder.Build(
            withSymbolTable: true,
            ("leaf_function_in_a_long_named_member.o", Code.Function("leaf", Code.Return(6))),
            ("middle.o", CallerOf("middle", "leaf")));

        Assert.Equal(6, Run(Link(Code.MainCalling("middle"), archive)).ExitCode);
    }

    [Fact]
    public void ArchiveWithoutSymbolTableIsIndexedFromItsMembers()
    {
        var archive = ArchiveBuilder.Build(withSymbolTable: false, ("helper.o", Code.Function("helper", Code.Return(8))));

        Assert.Equal(8, Run(Link(Code.MainCalling("helper"), archive)).ExitCode);
    }

    [Fact]
    public void StrongDefinitionReplacesWeakOne()
    {
        var weak = new ElfObjectBuilder();
        weak.Weak("value", weak.Text(".text", Code.Return(1)));

        var output = Link(Code.MainCalling("value"), weak.Build(), Code.Function("value", Code.Return(2)));

        Assert.Equal(2, Run(output).ExitCode);
    }

    [Fact]
    public void WeakDefinitionIsUsedWhenNothingElseDefinesTheSymbol()
    {
        var weak = new ElfObjectBuilder();
        weak.Weak("value", weak.Text(".text", Code.Return(1)));

        Assert.Equal(1, Run(Link(Code.MainCalling("value"), weak.Build())).ExitCode);
    }

    [Fact]
    public void DuplicateStrongDefinitionsAreReported()
    {
        var error = Assert.Throws<LinkerException>(() => Link(
            Code.Function("main", Code.Return(0)), Code.Function("twice", Code.Return(1)), Code.Function("twice", Code.Return(2))));

        Assert.Contains("duplicate symbol: twice", error.Message);
        Assert.Contains("input1", error.Message);
        Assert.Contains("input2", error.Message);
    }

    [Fact]
    public void UndefinedSymbolIsReportedWithTheReferringSection()
    {
        var error = Assert.Throws<LinkerException>(() => Link(Code.MainCalling("does_not_exist")));

        Assert.Contains("undefined symbol: does_not_exist", error.Message);
        Assert.Contains("input0:(.text)", error.Message);
    }

    [Fact]
    public void UnresolvedWeakReferenceEvaluatesToZero()
    {
        var builder = new ElfObjectBuilder();
        var text = builder.Text(".text",
            0x48, 0x8b, 0x05, 0x00, 0x00, 0x00, 0x00, // mov  rax, [rip + optional@GOT]
            0x31, 0xc9,                               // xor  ecx, ecx
            0x48, 0x85, 0xc0,                         // test rax, rax
            0x0f, 0x94, 0xc1,                         // sete cl
            0x89, 0xc8,                               // mov  eax, ecx
            0xc3);                                    // ret
        builder.Global("main", text);
        builder.WeakUndefined("optional");
        builder.Relocate(text, 3, RelocX64.RexGotPcRelX, "optional", -4);

        Assert.Equal(1, Run(Link(builder.Build())).ExitCode);
    }

    [Fact]
    public void CommonSymbolsOfSeveralObjectsShareOneAllocation()
    {
        // input0 stores into the common variable, input1 reads it back.
        var writer = new ElfObjectBuilder();
        var writerText = writer.Text(".text",
            0x48, 0x83, 0xec, 0x08,                                     // sub  rsp, 8
            0xc7, 0x05, 0x00, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, // mov  dword [rip + shared], 9
            0xe8, 0x00, 0x00, 0x00, 0x00,                               // call reader
            0x48, 0x83, 0xc4, 0x08,                                     // add  rsp, 8
            0xc3);                                                      // ret
        writer.Global("main", writerText);
        writer.Common("shared", size: 4, alignment: 4);
        writer.Relocate(writerText, 6, RelocX64.Pc32, "shared", -8);
        writer.Relocate(writerText, 15, RelocX64.Plt32, "reader", -4);

        var reader = new ElfObjectBuilder();
        var readerText = reader.Text(".text",
            0x8b, 0x05, 0x00, 0x00, 0x00, 0x00, // mov eax, [rip + shared]
            0xc3);                              // ret
        reader.Global("reader", readerText);
        reader.Common("shared", size: 4, alignment: 4);
        reader.Relocate(readerText, 2, RelocX64.Pc32, "shared", -4);

        Assert.Equal(9, Run(Link(writer.Build(), reader.Build())).ExitCode);
    }

    [Fact]
    public void OnlyTheFirstCopyOfAComdatGroupIsKept()
    {
        // Both objects carry their own copy of an inline function, as C++ translation units do.
        static byte[] WithInlineCopy(string caller, int value)
        {
            var builder = new ElfObjectBuilder();
            var inline = builder.Text(".text.inline_function", Code.Return(value));
            builder.Global("inline_function", inline);
            builder.Comdat("inline_function", inline);

            var text = builder.Text(".text", Code.CallAndReturn);
            builder.Global(caller, text);
            builder.Relocate(text, Code.CallOperand, RelocX64.Plt32, "inline_function", -4);
            return builder.Build();
        }

        var output = Link(WithInlineCopy("main", 3), WithInlineCopy("other", 4));

        Assert.Equal(3, Run(output).ExitCode);
    }

    [Fact]
    public void StartAndStopSymbolsDelimitSectionsNamedLikeIdentifiers()
    {
        var builder = new ElfObjectBuilder();
        builder.Data("registry", new byte[8]);
        var text = builder.Text(".text",
            0x48, 0x8d, 0x05, 0x00, 0x00, 0x00, 0x00, // lea rax, [rip + __stop_registry]
            0x48, 0x8d, 0x15, 0x00, 0x00, 0x00, 0x00, // lea rdx, [rip + __start_registry]
            0x48, 0x29, 0xd0,                         // sub rax, rdx
            0xc3);                                    // ret
        builder.Global("main", text);
        builder.Relocate(text, 3, RelocX64.Pc32, "__stop_registry", -4);
        builder.Relocate(text, 10, RelocX64.Pc32, "__start_registry", -4);

        var other = new ElfObjectBuilder();
        other.Data("registry", new byte[8]);

        // Both contributions are kept alive by the reference to the delimiting symbols.
        Assert.Equal(16, Run(Link(builder.Build(), other.Build())).ExitCode);
    }

    [Fact]
    public void ObjectsForAnotherArchitectureAreRejected()
    {
        var builder = new ElfObjectBuilder(ElfMachine.AArch64);
        builder.Global("main", builder.Text(".text", 0xc0, 0x03, 0x5f, 0xd6));

        var error = Assert.Throws<LinkerException>(() => Link(builder.Build()));

        Assert.Contains("different architecture", error.Message);
    }

    [Fact]
    public void UnknownInputFormatIsRejected()
    {
        var error = Assert.Throws<LinkerException>(() => Link("this is not an object file"u8.ToArray()));

        Assert.Contains("unknown file format", error.Message);
    }

    private static byte[] CallerOf(string name, string callee)
    {
        var builder = new ElfObjectBuilder();
        var text = builder.Text(".text", Code.CallAndReturn);
        builder.Global(name, text);
        builder.Relocate(text, Code.CallOperand, RelocX64.Plt32, callee, -4);
        return builder.Build();
    }
}

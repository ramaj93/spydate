using System.Globalization;
using ICSharpCode.Decompiler.TypeSystem;
using Spydate.App.Services;
using Spydate.Core.Elf;
using Spydate.Core.PE;
using Spydate.Core.Readings;
using Spydate.Core.Symbols;
using Spydate.Decompiler.Managed;
using Spydate.Disassembly;
using Wpf.Ui.Controls;

namespace Spydate.App.ViewModels;

/// <summary>Builds the explorer tree for an <see cref="OpenedBinary"/>: each format shows its own structures.</summary>
public static class ExplorerTreeBuilder
{
    public static ExplorerNodeViewModel Build(OpenedBinary binary) => binary.Image switch
    {
        ElfImage elf => BuildElf(binary, elf),
        PeImage pe => BuildPe(binary, pe),
        var other => throw new NotSupportedException($"No explorer for {other.Format}."),
    };

    /// <summary>
    /// An ELF's tree: its own structures (segments, the dynamic section, symbol tables) where a PE has directories
    /// and resources, then the same analysis nodes every native binary gets.
    /// </summary>
    private static ExplorerNodeViewModel BuildElf(OpenedBinary binary, ElfImage elf)
    {
        string subtitle = $"{elf.Header.ClassName} · {elf.Header.MachineName} · {elf.Kind}";
        var root = new ExplorerNodeViewModel(elf.FileName, elf.IsLibrary ? SymbolRegular.Library24 : SymbolRegular.Document24, new OverviewTarget(), subtitle)
        {
            IsExpanded = true,
        };

        root.Add(new ExplorerNodeViewModel("Overview", SymbolRegular.Info24, new OverviewTarget()));
        root.Add(new ExplorerNodeViewModel("Headers", SymbolRegular.DocumentHeader24, new HeadersTarget()));
        root.Add(new ExplorerNodeViewModel("Segments", SymbolRegular.Storage24, new SegmentsTarget(), elf.Segments.Count.ToString(CultureInfo.InvariantCulture)));

        var sections = root.Add(new ExplorerNodeViewModel("Sections", SymbolRegular.Layer24, new SectionsTarget(), elf.SectionHeaders.Count.ToString(CultureInfo.InvariantCulture)));
        sections.ChildrenFactory = () => elf.SectionHeaders.Where(s => s.Index > 0).Select(s => new ExplorerNodeViewModel(
            s.Name.Length == 0 ? $"<section {s.Index}>" : s.Name,
            s.IsExecutable ? SymbolRegular.Code24 : SymbolRegular.Storage24,
            new HexTarget((long)s.Offset),
            s.IsAllocated ? $"{s.TypeName} · 0x{elf.AddressOf(s):X}" : s.TypeName));

        if (elf.Dynamic.Count > 0)
        {
            root.Add(new ExplorerNodeViewModel("Dynamic", SymbolRegular.PlugConnected24, new DynamicTarget(), elf.Needed.Count == 1 ? "1 library" : $"{elf.Needed.Count} libraries"));
        }

        var imports = root.Add(new ExplorerNodeViewModel("Imports", SymbolRegular.ArrowImport24, new ImportsTarget(), elf.Imports.Count.ToString(CultureInfo.InvariantCulture)));
        imports.ChildrenFactory = () => elf.Imports
            .GroupBy(i => i.Module.Length == 0 ? "(any library)" : i.Module)
            .Select(g =>
            {
                var module = new ExplorerNodeViewModel(g.Key, SymbolRegular.Box24, new ImportsTarget(), g.Count().ToString(CultureInfo.InvariantCulture));
                module.ChildrenFactory = () => g.Select(i => new ExplorerNodeViewModel(i.DisplayName, SymbolRegular.ArrowRight24, new ImportsTarget(), $"GOT 0x{elf.RvaToVa(i.SlotRva):X}"));
                return module;
            });

        var exports = root.Add(new ExplorerNodeViewModel("Exports", SymbolRegular.ArrowExport24, new ExportsTarget(), elf.Exports.Count == 0 ? "none" : elf.Exports.Count.ToString(CultureInfo.InvariantCulture)));
        exports.ChildrenFactory = () => elf.Exports.Select(e => new ExplorerNodeViewModel(
            e.DisplayName,
            SymbolRegular.Flash24,
            binary.Analysis is not null && elf.SectionFromRva(e.Rva) is { IsExecutable: true } ? new DisassemblyTarget(elf.RvaToVa(e.Rva), e.DisplayName) : new ExportsTarget(),
            $"0x{elf.RvaToVa(e.Rva):X}"));

        root.Add(new ExplorerNodeViewModel(
            "Symbols",
            SymbolRegular.Tag24,
            new SymbolsTarget(),
            elf.StaticSymbols.Count > 0 ? $"{elf.DynamicSymbols.Count + elf.StaticSymbols.Count:N0}" : $"{elf.DynamicSymbols.Count:N0} · stripped"));

        AddAnalysisNodes(binary, root);
        root.Add(new ExplorerNodeViewModel("Strings", SymbolRegular.TextT24, new StringsTarget(), "ascii + utf-16"));
        root.Add(new ExplorerNodeViewModel("Hex dump", SymbolRegular.Grid24, new HexTarget(0), $"{elf.Length:N0} bytes"));
        return root;
    }

    /// <summary>Functions, and the entry point under Headers, for any binary the native analysis can read.</summary>
    private static void AddAnalysisNodes(OpenedBinary binary, ExplorerNodeViewModel root)
    {
        if (binary.Analysis is not { } analysis)
        {
            return;
        }

        var image = binary.Image;
        var functions = root.Add(new ExplorerNodeViewModel("Functions", SymbolRegular.BranchFork24, new FunctionsTarget(), "analyzing…"));
        functions.ChildrenFactory = () => FunctionNodes(analysis);
        var entryNode = new ExplorerNodeViewModel("Entry point", SymbolRegular.Play24,
            image.EntryPointRva != 0 ? new DisassemblyTarget(image.EntryPointVa, SymbolTable.EntryPointName(image)) : new OverviewTarget(),
            image.EntryPointRva != 0 ? $"0x{image.EntryPointVa:X}" : "none");
        root.Children.Insert(2, entryNode);
    }

    private static ExplorerNodeViewModel BuildPe(OpenedBinary binary, PeImage pe)
    {
        string subtitle = $"{(pe.Is64Bit ? "PE32+" : "PE32")} · {pe.Machine}{(pe.IsManaged ? " · .NET" : string.Empty)}";
        var root = new ExplorerNodeViewModel(pe.FileName, pe.IsManaged ? SymbolRegular.Library24 : SymbolRegular.Document24, new OverviewTarget(), subtitle)
        {
            IsExpanded = true,
        };

        root.Add(new ExplorerNodeViewModel("Overview", SymbolRegular.Info24, new OverviewTarget()));
        root.Add(new ExplorerNodeViewModel("Headers", SymbolRegular.DocumentHeader24, new HeadersTarget()));

        var sections = root.Add(new ExplorerNodeViewModel("Sections", SymbolRegular.Layer24, new SectionsTarget(), pe.Sections.Count.ToString()));
        foreach (var s in pe.Sections)
        {
            sections.Add(new ExplorerNodeViewModel(
                s.Name.Length == 0 ? $"<section {s.Index}>" : s.Name,
                s.IsExecutable ? SymbolRegular.Code24 : SymbolRegular.Storage24,
                new HexTarget(s.PointerToRawData),
                $"{s.Permissions} · 0x{s.VirtualAddress:X}"));
        }

        var imports = root.Add(new ExplorerNodeViewModel("Imports", SymbolRegular.ArrowImport24, new ImportsTarget(), $"{pe.Imports.Count + pe.DelayImports.Count} modules"));
        foreach (var module in pe.Imports.Concat(pe.DelayImports))
        {
            var m = module;
            var moduleNode = imports.Add(new ExplorerNodeViewModel(m.Name, SymbolRegular.Box24, new ImportsTarget(), $"{m.Functions.Count}{(m.IsDelayLoad ? " · delay" : string.Empty)}"));
            moduleNode.ChildrenFactory = () => m.Functions.Select(f => new ExplorerNodeViewModel(f.DisplayName, SymbolRegular.ArrowRight24, new ImportsTarget(), $"IAT 0x{pe.RvaToVa(f.IatRva):X}"));
        }

        if (pe.Exports is { } exports)
        {
            var exportsNode = root.Add(new ExplorerNodeViewModel("Exports", SymbolRegular.ArrowExport24, new ExportsTarget(), exports.Entries.Count.ToString()));
            exportsNode.ChildrenFactory = () => exports.Entries.Select(e => new ExplorerNodeViewModel(
                e.DisplayName,
                e.IsForwarder ? SymbolRegular.Link24 : SymbolRegular.Flash24,
                e.IsForwarder || binary.Analysis is null ? new ExportsTarget() : new DisassemblyTarget(pe.RvaToVa(e.Rva), e.Name ?? $"Ordinal{e.Ordinal}"),
                e.IsForwarder ? e.ForwarderName : $"#{e.Ordinal}"));
        }
        else
        {
            root.Add(new ExplorerNodeViewModel("Exports", SymbolRegular.ArrowExport24, new ExportsTarget(), "none"));
        }

        AddAnalysisNodes(binary, root);

        if (binary.Managed is { } managed)
        {
            var asmNode = root.Add(new ExplorerNodeViewModel("Assembly", SymbolRegular.Library24, new ManagedAssemblyTarget(), managed.TargetFramework));
            asmNode.ChildrenFactory = () => managed.References.Select(reference =>
            {
                var r = reference;
                var refNode = new ExplorerNodeViewModel(r.Display, SymbolRegular.Link24, null);
                refNode.ChildrenFactory = () => ReferenceChildren(managed, r);
                return refNode;
            });

            // The native functions the managed code calls — the P/Invokes the PE import table cannot
            // show, since a managed image imports only the runtime stub. Each opens the method that
            // makes the call. Where an anti-debug check lives, most of the time.
            if (managed.PInvokes.Count > 0)
            {
                var native = root.Add(new ExplorerNodeViewModel("Native calls", SymbolRegular.PlugConnected24, null, managed.PInvokes.Count.ToString(CultureInfo.InvariantCulture)));
                native.ChildrenFactory = () => managed.PInvokes.Select(p =>
                {
                    NodeTarget? target = managed.Locate(p.Token) is { Member: { } member } located
                        ? new ManagedMemberTarget(located.Type, member)
                        : null;
                    return new ExplorerNodeViewModel(p.Native, SymbolRegular.ArrowExport24, target, p.Managed);
                });
            }

        }

        // The bytecode reading's namespaces, whatever the reading is: the tree is built through the seam, and only
        // what a node opens depends on which reading it is.
        if (binary.Bytecode is { } reading)
        {
            var namespaces = root.Add(new ExplorerNodeViewModel("Namespaces", SymbolRegular.Braces24, reading is DotNetReading ? new ManagedAssemblyTarget() : null, reading.Namespaces.Count.ToString()));
            namespaces.IsExpanded = true;
            var targets = TargetsFor(reading);
            foreach (var ns in reading.Namespaces)
            {
                var n = ns;
                var nsNode = namespaces.Add(new ExplorerNodeViewModel(n.DisplayName, SymbolRegular.Braces24, null, n.Types.Count.ToString()));
                nsNode.ChildrenFactory = () => n.Types.Select(t => TypeNode(t, targets));
            }
        }

        if (pe.Resources is { Children.Count: > 0 } resources)
        {
            var node = root.Add(new ExplorerNodeViewModel("Resources", SymbolRegular.Image24, new ResourcesTarget(), $"{resources.Children.Count} types"));
            node.ChildrenFactory = () => resources.Children.Select(t => ResourceNodeVm(pe, t, t.Id, t.DisplayName));
        }

        root.Add(new ExplorerNodeViewModel("Strings", SymbolRegular.TextT24, new StringsTarget(), "ascii + utf-16"));
        root.Add(new ExplorerNodeViewModel("Hex dump", SymbolRegular.Grid24, new HexTarget(0), $"{pe.Length:N0} bytes"));
        return root;
    }

    /// <summary>Resource subtree; leaves jump to their bytes in the hex view.</summary>
    private static ExplorerNodeViewModel ResourceNodeVm(PeImage pe, ResourceNode node, uint typeId = 0, string typeName = "Resource")
    {
        if (!node.IsDirectory)
        {
            var target = new ResourcePreviewTarget(typeId, node.Id, node.DataRva, node.DataSize, $"{typeName}: {node.DisplayName}");
            return new ExplorerNodeViewModel(node.DisplayName, SymbolRegular.Document24, target, $"{node.DataSize:N0} bytes");
        }

        // The type is fixed at the first level and inherited by everything below it.
        uint childType = node.Level == 1 ? node.Id : typeId;
        string childTypeName = node.Level == 1 ? node.DisplayName : typeName;

        var vm = new ExplorerNodeViewModel(node.DisplayName, SymbolRegular.Folder24, new ResourcesTarget(), node.Children!.Count.ToString(CultureInfo.InvariantCulture));
        vm.ChildrenFactory = () => node.Children!.Select(c => ResourceNodeVm(pe, c, childType, childTypeName));
        return vm;
    }

    public static IEnumerable<ExplorerNodeViewModel> FunctionNodes(BinaryAnalysis analysis)
        => analysis.Functions.Select(f => new ExplorerNodeViewModel(f.Name, SymbolRegular.Flash24, new DisassemblyTarget(f.EntryVa, f.Name), $"0x{f.EntryVa:X} · {f.InstructionCount} insns"));

    /// <summary>
    /// A reference's own types and members, loaded on expansion — the resolved assembly grouped into
    /// namespaces, the same shape the opened assembly has. Opening one decompiles through that
    /// assembly rather than this one, so a type carries it in its target. When the assembly is not on
    /// this machine, one node says so.
    /// </summary>
    private static IEnumerable<ExplorerNodeViewModel> ReferenceChildren(ManagedAssembly parent, ManagedReference reference)
    {
        var resolved = parent.Resolve(reference);
        if (resolved is null)
        {
            return [new ExplorerNodeViewModel("could not be found", SymbolRegular.Warning24, null, "not on this machine")];
        }

        return resolved.Namespaces.Select(ns =>
        {
            var n = ns;
            var nsNode = new ExplorerNodeViewModel(n.DisplayName, SymbolRegular.Braces24, null, n.Types.Count.ToString(CultureInfo.InvariantCulture));
            nsNode.ChildrenFactory = () => n.Types.Select(t => TypeNode(t, TargetsFor(new DotNetReading(resolved), resolved)));
            return nsNode;
        });
    }

    /// <summary>
    /// A type node and, lazily, its nested types and members. <paramref name="external"/> is the
    /// assembly to open the type through when it is not the one opened — a resolved reference — and
    /// null for the opened assembly's own types, which the factory decompiles through the binary and
    /// draws the debugger's addresses against.
    /// </summary>
    /// <summary>
    /// What opening a type or member of a reading means. A .NET one opens the C# and IL documents, through the
    /// assembly it belongs to (<paramref name="external"/> for a resolved reference, null for the opened one);
    /// any other reading opens its own rendering.
    /// </summary>
    private static Func<IBytecodeType, IBytecodeMember?, NodeTarget?> TargetsFor(IBytecodeReading reading, ManagedAssembly? external = null)
        => reading is DotNetReading
            ? (type, member) => member is ManagedMember m
                ? new ManagedMemberTarget((ManagedType)type, m, external)
                : new ManagedTypeTarget((ManagedType)type, external)
            : (type, member) => new ReadingTarget(type, member);

    private static ExplorerNodeViewModel TypeNode(IBytecodeType type, Func<IBytecodeType, IBytecodeMember?, NodeTarget?> targets)
    {
        var node = new ExplorerNodeViewModel(type.Name, IconFor(type.Kind), targets(type, null), type.KindName);
        node.ChildrenFactory = () =>
            type.NestedTypes.Select(n => TypeNode(n, targets))
                .Concat(type.Members.Select(m => new ExplorerNodeViewModel(m.Signature, IconFor(m.Kind), targets(type, m))));
        return node;
    }

    private static SymbolRegular IconFor(BytecodeTypeKind kind) => kind switch
    {
        BytecodeTypeKind.Interface => SymbolRegular.ShapeIntersect24,
        BytecodeTypeKind.Enum => SymbolRegular.TextBulletListSquare24,
        BytecodeTypeKind.Struct => SymbolRegular.Cube24,
        BytecodeTypeKind.Delegate => SymbolRegular.Flash24,
        _ => SymbolRegular.Class24,
    };

    private static SymbolRegular IconFor(BytecodeMemberKind kind) => kind switch
    {
        BytecodeMemberKind.Method => SymbolRegular.Code24,
        BytecodeMemberKind.Constructor => SymbolRegular.Wrench24,
        BytecodeMemberKind.Field => SymbolRegular.Tag24,
        BytecodeMemberKind.Property => SymbolRegular.Settings24,
        BytecodeMemberKind.Event => SymbolRegular.Flash24,
        _ => SymbolRegular.Document24,
    };
}

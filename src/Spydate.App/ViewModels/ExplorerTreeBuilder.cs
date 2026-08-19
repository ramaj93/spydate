using ICSharpCode.Decompiler.TypeSystem;
using Spydate.App.Services;
using Spydate.Core.PE;
using Spydate.Decompiler.Managed;
using Spydate.Disassembly;
using Wpf.Ui.Controls;

namespace Spydate.App.ViewModels;

/// <summary>Builds the explorer tree for an <see cref="OpenedBinary"/>.</summary>
public static class ExplorerTreeBuilder
{
    public static ExplorerNodeViewModel Build(OpenedBinary binary)
    {
        var pe = binary.Image;
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

        if (binary.Analysis is { } analysis)
        {
            var functions = root.Add(new ExplorerNodeViewModel("Functions", SymbolRegular.BranchFork24, new FunctionsTarget(), "analyzing…"));
            functions.ChildrenFactory = () => FunctionNodes(analysis);
            var entryNode = new ExplorerNodeViewModel("Entry point", SymbolRegular.Play24,
                pe.EntryPointRva != 0 ? new DisassemblyTarget(pe.EntryPointVa, pe.IsDll ? "DllEntryPoint" : "EntryPoint") : new OverviewTarget(),
                pe.EntryPointRva != 0 ? $"0x{pe.EntryPointVa:X}" : "none");
            root.Children.Insert(2, entryNode);
        }

        if (binary.Managed is { } managed)
        {
            var asmNode = root.Add(new ExplorerNodeViewModel("Assembly", SymbolRegular.Library24, new ManagedAssemblyTarget(), managed.TargetFramework));
            asmNode.ChildrenFactory = () => managed.AssemblyReferences.Select(r => new ExplorerNodeViewModel(r, SymbolRegular.Link24, new ManagedAssemblyTarget()));

            var namespaces = root.Add(new ExplorerNodeViewModel("Namespaces", SymbolRegular.Braces24, new ManagedAssemblyTarget(), managed.Namespaces.Count.ToString()));
            namespaces.IsExpanded = true;
            foreach (var ns in managed.Namespaces)
            {
                var n = ns;
                var nsNode = namespaces.Add(new ExplorerNodeViewModel(n.DisplayName, SymbolRegular.Braces24, null, n.Types.Count.ToString()));
                nsNode.ChildrenFactory = () => n.Types.Select(t => TypeNode(managed, t));
            }
        }

        root.Add(new ExplorerNodeViewModel("Hex dump", SymbolRegular.Grid24, new HexTarget(0), $"{pe.Length:N0} bytes"));
        return root;
    }

    public static IEnumerable<ExplorerNodeViewModel> FunctionNodes(BinaryAnalysis analysis)
        => analysis.Functions.Select(f => new ExplorerNodeViewModel(f.Name, SymbolRegular.Flash24, new DisassemblyTarget(f.EntryVa, f.Name), $"0x{f.EntryVa:X} · {f.InstructionCount} insns"));

    private static ExplorerNodeViewModel TypeNode(ManagedAssembly managed, ManagedType type)
    {
        var node = new ExplorerNodeViewModel(type.Name, IconFor(type.Kind), new ManagedTypeTarget(type), type.Kind.ToString().ToLowerInvariant());
        node.ChildrenFactory = () =>
            type.NestedTypes.Select(n => TypeNode(managed, n))
                .Concat(type.Members.Select(m => new ExplorerNodeViewModel(m.Signature, IconFor(m.Kind), new ManagedMemberTarget(type, m))));
        return node;
    }

    private static SymbolRegular IconFor(TypeKind kind) => kind switch
    {
        TypeKind.Interface => SymbolRegular.ShapeIntersect24,
        TypeKind.Enum => SymbolRegular.TextBulletListSquare24,
        TypeKind.Struct => SymbolRegular.Cube24,
        TypeKind.Delegate => SymbolRegular.Flash24,
        _ => SymbolRegular.Class24,
    };

    private static SymbolRegular IconFor(ManagedMemberKind kind) => kind switch
    {
        ManagedMemberKind.Method => SymbolRegular.Code24,
        ManagedMemberKind.Constructor => SymbolRegular.Wrench24,
        ManagedMemberKind.Field => SymbolRegular.Tag24,
        ManagedMemberKind.Property => SymbolRegular.Settings24,
        ManagedMemberKind.Event => SymbolRegular.Flash24,
        _ => SymbolRegular.Document24,
    };
}

using System.Globalization;
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
            asmNode.ChildrenFactory = () => managed.References.Select(reference =>
            {
                var r = reference;
                var refNode = new ExplorerNodeViewModel(r.Display, SymbolRegular.Link24, null);
                refNode.ChildrenFactory = () => ReferenceChildren(managed, r);
                return refNode;
            });

            var namespaces = root.Add(new ExplorerNodeViewModel("Namespaces", SymbolRegular.Braces24, new ManagedAssemblyTarget(), managed.Namespaces.Count.ToString()));
            namespaces.IsExpanded = true;
            foreach (var ns in managed.Namespaces)
            {
                var n = ns;
                var nsNode = namespaces.Add(new ExplorerNodeViewModel(n.DisplayName, SymbolRegular.Braces24, null, n.Types.Count.ToString()));
                nsNode.ChildrenFactory = () => n.Types.Select(t => TypeNode(t));
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
            nsNode.ChildrenFactory = () => n.Types.Select(t => TypeNode(t, resolved));
            return nsNode;
        });
    }

    /// <summary>
    /// A type node and, lazily, its nested types and members. <paramref name="external"/> is the
    /// assembly to open the type through when it is not the one opened — a resolved reference — and
    /// null for the opened assembly's own types, which the factory decompiles through the binary and
    /// draws the debugger's addresses against.
    /// </summary>
    private static ExplorerNodeViewModel TypeNode(ManagedType type, ManagedAssembly? external = null)
    {
        var node = new ExplorerNodeViewModel(type.Name, IconFor(type.Kind), new ManagedTypeTarget(type, external), type.Kind.ToString().ToLowerInvariant());
        node.ChildrenFactory = () =>
            type.NestedTypes.Select(n => TypeNode(n, external))
                .Concat(type.Members.Select(m => new ExplorerNodeViewModel(m.Signature, IconFor(m.Kind), new ManagedMemberTarget(type, m, external))));
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

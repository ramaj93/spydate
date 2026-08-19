using System.Collections;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spydate.Core.PE;
using Wpf.Ui.Controls;

namespace Spydate.App.ViewModels.Documents;

/// <summary>One 16-byte line of the hex dump.</summary>
public sealed record HexRow(long Offset, string OffsetText, string Bytes, string Ascii);

/// <summary>
/// Read-only virtual list: rows are formatted on demand from the underlying buffer, so a 100 MB file costs
/// nothing beyond the file itself. Implements the non-generic <see cref="IList"/> because that's what
/// WPF's virtualizing panels use for index access.
/// </summary>
public sealed class HexRowList : IList<HexRow>, IList, IReadOnlyList<HexRow>
{
    public const int BytesPerRow = 16;
    private readonly ReadOnlyMemory<byte> _data;
    private readonly long _baseOffset;

    public HexRowList(ReadOnlyMemory<byte> data, long baseOffset = 0)
    {
        _data = data;
        _baseOffset = baseOffset;
        Count = (_data.Length + BytesPerRow - 1) / BytesPerRow;
    }

    public int Count { get; }

    public HexRow this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            int start = index * BytesPerRow;
            int len = Math.Min(BytesPerRow, _data.Length - start);
            var span = _data.Span.Slice(start, len);
            var hex = new StringBuilder(BytesPerRow * 3);
            var ascii = new StringBuilder(BytesPerRow);
            for (int i = 0; i < BytesPerRow; i++)
            {
                if (i < len)
                {
                    byte b = span[i];
                    hex.Append(HexDigits[b >> 4]).Append(HexDigits[b & 0xF]);
                    ascii.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
                }
                else
                {
                    hex.Append("  ");
                }

                if (i == 7)
                {
                    hex.Append("  ");
                }
                else if (i < BytesPerRow - 1)
                {
                    hex.Append(' ');
                }
            }

            long offset = _baseOffset + start;
            return new HexRow(offset, offset.ToString("X8", CultureInfo.InvariantCulture), hex.ToString(), ascii.ToString());
        }
        set => throw new NotSupportedException();
    }

    private const string HexDigits = "0123456789ABCDEF";

    public int IndexOfOffset(long offset) => (int)Math.Clamp((offset - _baseOffset) / BytesPerRow, 0, Math.Max(0, Count - 1));

    // --- IList<HexRow> / IList plumbing (read-only) ---
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
    public IEnumerator<HexRow> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public int IndexOf(HexRow item) => item is null ? -1 : IndexOfOffset(item.Offset);
    public bool Contains(HexRow item) => IndexOf(item) >= 0;
    public void CopyTo(HexRow[] array, int arrayIndex)
    {
        for (int i = 0; i < Count; i++)
        {
            array[arrayIndex + i] = this[i];
        }
    }

    public void CopyTo(Array array, int index)
    {
        for (int i = 0; i < Count; i++)
        {
            array.SetValue(this[i], index + i);
        }
    }

    public int Add(object? value) => throw new NotSupportedException();
    public void Add(HexRow item) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(object? value) => value is HexRow r && Contains(r);
    public int IndexOf(object? value) => value is HexRow r ? IndexOf(r) : -1;
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Insert(int index, HexRow item) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public bool Remove(HexRow item) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}

/// <summary>Hex dump of the whole file with go-to-offset and section jump support.</summary>
public sealed partial class HexDocumentViewModel : DocumentViewModel
{
    private readonly PeImage _pe;

    public HexDocumentViewModel(PeImage pe) : base("hex", "Hex", SymbolRegular.Grid24)
    {
        _pe = pe;
        Rows = new HexRowList(pe.Data);
        Sections = pe.Sections.Select(s => new SectionJump(s.Name, s.PointerToRawData)).ToList();
        Sections.Insert(0, new SectionJump("Headers", 0));
        if (pe.Overlay.Length > 0)
        {
            Sections.Add(new SectionJump("Overlay", pe.Overlay.Offset));
        }
    }

    public HexRowList Rows { get; }

    public List<SectionJump> Sections { get; }

    [ObservableProperty]
    private string _gotoText = "0";

    [ObservableProperty]
    private HexRow? _selectedRow;

    [ObservableProperty]
    private string _positionInfo = string.Empty;

    [ObservableProperty]
    private SectionJump? _selectedSection;

    partial void OnSelectedSectionChanged(SectionJump? value)
    {
        if (value is not null)
        {
            GoToOffset(value.Offset);
        }
    }

    partial void OnSelectedRowChanged(HexRow? value)
    {
        if (value is null)
        {
            PositionInfo = string.Empty;
            return;
        }

        uint offset = (uint)value.Offset;
        var section = _pe.Sections.FirstOrDefault(s => offset >= s.PointerToRawData && offset < s.PointerToRawData + s.SizeOfRawData);
        uint? rva = _pe.OffsetToRva(offset);
        PositionInfo = rva is { } r
            ? $"offset 0x{offset:X}  •  RVA 0x{r:X}  •  VA 0x{_pe.RvaToVa(r):X}  •  {section?.Name ?? "headers"}"
            : $"offset 0x{offset:X}  •  not mapped";
    }

    /// <summary>Raised when the view should scroll to a row.</summary>
    public event EventHandler<HexRow>? ScrollRequested;

    [RelayCommand]
    private void GoTo()
    {
        string text = GotoText.Trim();
        var style = NumberStyles.HexNumber;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        if (!long.TryParse(text, style, CultureInfo.InvariantCulture, out long value))
        {
            StatusMessage = "Enter a hexadecimal offset, RVA or VA.";
            return;
        }

        // Accept VA or RVA too: try file offset first, then VA, then RVA.
        long offset = value;
        if (value >= (long)_pe.ImageBase && _pe.VaToOffset((ulong)value) is { } fromVa)
        {
            offset = fromVa;
        }
        else if (value >= _pe.Length && _pe.RvaToOffset((uint)Math.Min(value, uint.MaxValue)) is { } fromRva)
        {
            offset = fromRva;
        }

        GoToOffset(offset);
    }

    public void GoToOffset(long offset)
    {
        if (Rows.Count == 0)
        {
            return;
        }

        var row = Rows[Rows.IndexOfOffset(offset)];
        SelectedRow = row;
        ScrollRequested?.Invoke(this, row);
        StatusMessage = null;
    }
}

public sealed record SectionJump(string Name, long Offset)
{
    public override string ToString() => $"{Name} (0x{Offset:X})";
}

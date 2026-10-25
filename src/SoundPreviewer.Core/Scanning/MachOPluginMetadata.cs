using System.Buffers.Binary;
using System.Text;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Scanning;

internal static class MachOPluginMetadata
{
    private const long MaximumBinaryBytes = 512L * 1024 * 1024;
    private const int MaximumTableBytes = 32 * 1024 * 1024;

    public static PluginArchitecture Read(string path, PluginBinaryFormat format, CancellationToken token)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length is < 32 or > MaximumBinaryBytes) throw new InvalidDataException("Mach-O 文件大小无效或超过 512 MB 限制。");
        byte[] header = ReadAt(file, 0, 8);
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (magic is not (0xcafebabe or 0xbebafeca or 0xcafebabf or 0xbfbafeca))
            return ReadSlice(file, 0, file.Length, format, token).Architecture;
        bool little = magic is 0xbebafeca or 0xbfbafeca, wide = magic is 0xcafebabf or 0xbfbafeca;
        uint count = UInt32(header, 4, little);
        int entrySize = wide ? 32 : 20;
        if (count is 0 or > 32 || 8L + count * entrySize > file.Length)
            throw new InvalidDataException("Mach-O Universal 架构表无效。");
        var slices = new List<(long Start, long End)>();
        var architectures = new HashSet<PluginArchitecture>();
        for (uint i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            byte[] entry = ReadAt(file, 8 + i * entrySize, entrySize);
            ulong offset = wide ? UInt64(entry, 8, little) : UInt32(entry, 8, little);
            ulong size = wide ? UInt64(entry, 16, little) : UInt32(entry, 12, little);
            if (offset < 8UL + count * (uint)entrySize || offset > (ulong)file.Length || size < 32 || size > (ulong)file.Length - offset)
                throw new InvalidDataException("Mach-O Universal 架构范围越界。");
            long start = (long)offset, end = checked((long)(offset + size));
            if (slices.Any(slice => start < slice.End && end > slice.Start)) throw new InvalidDataException("Mach-O Universal 架构范围重叠。");
            slices.Add((start, end));
            var slice = ReadSlice(file, start, (long)size, format, token);
            if (slice.Cpu != UInt32(entry, 0, little)) throw new InvalidDataException("Mach-O Universal 架构与实际文件头不一致。");
            architectures.Add(slice.Architecture);
        }
        if (architectures.Contains(PluginArchitecture.X64) && architectures.Contains(PluginArchitecture.Arm64)) return PluginArchitecture.Universal;
        if (architectures.Contains(PluginArchitecture.Arm64)) return PluginArchitecture.Arm64;
        if (architectures.Contains(PluginArchitecture.X64)) return PluginArchitecture.X64;
        return PluginArchitecture.X86;
    }

    private static (PluginArchitecture Architecture, uint Cpu) ReadSlice(FileStream file, long start, long length,
        PluginBinaryFormat format, CancellationToken token)
    {
        byte[] header = ReadAt(file, start, 32);
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        bool little = magic is 0xcefaedfe or 0xcffaedfe, wide = magic is 0xfeedfacf or 0xcffaedfe;
        if (magic is not (0xfeedface or 0xcefaedfe or 0xfeedfacf or 0xcffaedfe))
            throw new InvalidDataException("不是受支持的 macOS Mach-O 文件。");
        uint cpu = UInt32(header, 4, little);
        PluginArchitecture architecture = cpu switch
        {
            7 when !wide => PluginArchitecture.X86,
            0x01000007 when wide => PluginArchitecture.X64,
            0x0100000c when wide => PluginArchitecture.Arm64,
            _ => throw new InvalidDataException("Mach-O 架构或位数不一致 / 不受支持。")
        };
        uint fileType = UInt32(header, 12, little), count = UInt32(header, 16, little), size = UInt32(header, 20, little);
        int headerSize = wide ? 32 : 28;
        if (format == PluginBinaryFormat.Standalone ? fileType != 2 : fileType is not (6 or 8))
            throw new InvalidDataException("Mach-O 文件类型与应用 / 插件包不一致。");
        if (count > 65536 || size > MaximumTableBytes || headerSize + (long)size > length || count > size / 8)
            throw new InvalidDataException("Mach-O 加载命令表无效。");
        byte[] commands = ReadAt(file, start + headerSize, (int)size);
        // AU factory names are declared by each bundle and are not VST entry points.
        // Discovery checks the binary structure only; it does not prove AU registration.
        bool entryFound = format is PluginBinaryFormat.Standalone or PluginBinaryFormat.AudioUnit;
        int position = 0;
        for (uint i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (position > commands.Length - 8) throw new InvalidDataException("Mach-O 加载命令截断。");
            uint command = UInt32(commands, position, little), commandSize = UInt32(commands, position + 4, little);
            if (commandSize < 8 || commandSize > commands.Length - position || commandSize % (wide ? 8 : 4) != 0)
                throw new InvalidDataException("Mach-O 加载命令长度无效。");
            if (!entryFound && command == 2 && commandSize >= 24)
                entryFound = HasSymbol(file, start, length, commands, position, wide, little, format, token);
            if (!entryFound && command == 0x80000033 && commandSize >= 16)
                entryFound = HasTrie(file, start, length, UInt32(commands, position + 8, little), UInt32(commands, position + 12, little), format);
            if (!entryFound && command is 0x22 or 0x80000022 && commandSize >= 48)
                entryFound = HasTrie(file, start, length, UInt32(commands, position + 40, little), UInt32(commands, position + 44, little), format);
            position = checked(position + (int)commandSize);
        }
        if (position != commands.Length) throw new InvalidDataException("Mach-O 加载命令总长度不一致。");
        if (!entryFound) throw new InvalidDataException(format == PluginBinaryFormat.Vst3
            ? "Mach-O 插件未导出 GetPluginFactory 入口。" : "Mach-O 插件未导出 VSTPluginMain / main_macho 入口。");
        return (architecture, cpu);
    }

    private static bool HasSymbol(FileStream file, long start, long length, byte[] commands, int position,
        bool wide, bool little, PluginBinaryFormat format, CancellationToken token)
    {
        uint symbolOffset = UInt32(commands, position + 8, little), count = UInt32(commands, position + 12, little);
        uint stringOffset = UInt32(commands, position + 16, little), stringSize = UInt32(commands, position + 20, little);
        int entrySize = wide ? 16 : 12;
        long symbolSize = (long)count * entrySize;
        if (symbolSize > MaximumTableBytes || stringSize > MaximumTableBytes || symbolOffset + symbolSize > length ||
            (long)stringOffset + stringSize > length) throw new InvalidDataException("Mach-O 符号表越界或过大。");
        byte[] symbols = ReadAt(file, start + symbolOffset, (int)symbolSize);
        byte[] strings = ReadAt(file, start + stringOffset, (int)stringSize);
        for (int i = 0; i < symbols.Length; i += entrySize)
        {
            token.ThrowIfCancellationRequested();
            byte type = symbols[i + 4];
            // Exclude debug, undefined, private and non-external symbols.
            if ((type & 0xe1) != 1 || (type & 0x0e) is not (0x0e or 2) || (type & 0x10) != 0) continue;
            uint offset = UInt32(symbols, i, little);
            if (offset >= strings.Length) throw new InvalidDataException("Mach-O 符号名称地址越界。");
            int end = Array.IndexOf(strings, (byte)0, (int)offset, Math.Min(256, strings.Length - (int)offset));
            if (end < 0) continue;
            string name = Encoding.ASCII.GetString(strings, (int)offset, end - (int)offset);
            if (ExpectedNames(format).Contains(name, StringComparer.Ordinal) &&
                (wide ? UInt64(symbols, i + 8, little) : UInt32(symbols, i + 8, little)) != 0) return true;
        }
        return false;
    }

    private static bool HasTrie(FileStream file, long start, long length, uint offset, uint size, PluginBinaryFormat format)
    {
        if (size == 0) return false;
        if (size > MaximumTableBytes || (long)offset + size > length) throw new InvalidDataException("Mach-O 导出树越界或过大。");
        byte[] data = ReadAt(file, start + offset, (int)size);
        foreach (string expected in ExpectedNames(format))
        {
            int node = 0, matched = 0;
            var visited = new HashSet<int>();
            while (visited.Add(node))
            {
                int cursor = node;
                ulong terminalSize = Uleb(data, ref cursor);
                if (terminalSize > (ulong)(data.Length - cursor)) throw new InvalidDataException("Mach-O 导出树节点截断。");
                if (matched == expected.Length)
                {
                    if (terminalSize == 0) break;
                    int terminalEnd = cursor + (int)terminalSize;
                    ulong flags = Uleb(data, ref cursor), address = Uleb(data, ref cursor);
                    if (cursor > terminalEnd) throw new InvalidDataException("Mach-O 导出树终端截断。");
                    if ((flags & 8) == 0 && address != 0) return true;
                    break;
                }
                cursor += (int)terminalSize;
                if (cursor >= data.Length) throw new InvalidDataException("Mach-O 导出树子节点截断。");
                int children = data[cursor++], next = -1, nextMatched = matched;
                for (int child = 0; child < children; child++)
                {
                    int end = Array.IndexOf(data, (byte)0, cursor, Math.Min(256, data.Length - cursor));
                    if (end < 0) throw new InvalidDataException("Mach-O 导出树名称截断。");
                    string edge = Encoding.ASCII.GetString(data, cursor, end - cursor);
                    cursor = end + 1;
                    ulong childOffset = Uleb(data, ref cursor);
                    if (childOffset >= (ulong)data.Length) throw new InvalidDataException("Mach-O 导出树地址越界。");
                    if (edge.Length > 0 && expected.AsSpan(matched).StartsWith(edge, StringComparison.Ordinal))
                    { next = (int)childOffset; nextMatched = matched + edge.Length; }
                }
                if (next < 0) break;
                node = next;
                matched = nextMatched;
            }
        }
        return false;
    }

    private static string[] ExpectedNames(PluginBinaryFormat format) => format == PluginBinaryFormat.Vst3
        ? ["_GetPluginFactory"] : ["_VSTPluginMain", "_main_macho"];

    private static ulong Uleb(byte[] data, ref int position)
    {
        ulong value = 0;
        for (int shift = 0; shift <= 63; shift += 7)
        {
            if (position >= data.Length) throw new InvalidDataException("Mach-O ULEB128 截断。");
            byte next = data[position++];
            if (shift == 63 && (next & 0xfe) != 0) throw new InvalidDataException("Mach-O ULEB128 溢出。");
            value |= (ulong)(next & 127) << shift;
            if ((next & 128) == 0) return value;
        }
        throw new InvalidDataException("Mach-O ULEB128 过长。");
    }

    private static byte[] ReadAt(FileStream file, long position, int count)
    {
        if (position < 0 || count < 0 || position > file.Length - count) throw new InvalidDataException("Mach-O 读取地址越界。");
        byte[] data = new byte[count];
        file.Position = position;
        file.ReadExactly(data);
        return data;
    }

    private static uint UInt32(byte[] data, int offset, bool little) => little
        ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    private static ulong UInt64(byte[] data, int offset, bool little) => little
        ? BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8)) : BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8));
}

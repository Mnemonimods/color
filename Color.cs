using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using MISAML.Core;
using MISAML.Godot;

public static class Color
{
    // program address register
    private const string ProgramAddressRegister = "PA";

    // resolved on first use after a program starts, cleared when it starts or stops
    private static object? _vm;
    private static byte[]? _memory;

    private static readonly Hooks HookApi = new();
    private static readonly Ids IdApi = new();
    private static readonly GodotBridge Bridge = new();

    private static readonly int[] Palette = new int[256];
    private static readonly byte[] PaletteBytes = new byte[768];
    private static readonly bool[] Claimed = new bool[256];

    // embedded palette tables found in the running program, by id
    private static readonly Dictionary<int, (int First, int[] Colors)> Banks = [];

    private static readonly byte[] PaletteMagic = "MISAML_PAL\0"u8.ToArray();

    // the GDScript node in main.gd
    private static object? _paletteNode;

    // RegCode type + register name => idx, looked up once then cache
    private static Type? _regCode;
    private static readonly Dictionary<string, int> RegIndexes = [];

    // same two colors as _fill_default() in main.gd
    static Color()
    {
        for (int i = 0; i < 256; i++)
            Palette[i] = LinearInterpolation(0x0F1517, 0xB9C2A5, i);
        Claimed[0] = true;
    }

    public static void Init()
    {
        HookApi.Register("Asm.Running.VirtualMachineeee", "Initialize", OnVmStart);

        // loader hands out the numbers once every mod has registered
        IdApi.RegisterSyscall("SYS_SET_PALETTE_COLOR", SetPaletteColor);
        IdApi.RegisterSyscall("SYS_COLOR_TO_INDEX", ColorToIndexSyscall);
        IdApi.RegisterSyscall("SYS_RESET_PALETTE", _ => ResetPalette());
        IdApi.RegisterSyscall("SYS_GET_PALETTE_COLOR", GetPaletteColor);
        IdApi.RegisterSyscall("SYS_SET_PALETTE_RANGE", SetRangeSyscall);
        IdApi.RegisterSyscall("SYS_GET_PALETTE_RANGE", GetRangeSyscall);
        IdApi.RegisterSyscall("SYS_CLEAR_PALETTE", _ => ClearPalette());
        IdApi.RegisterSyscall("SYS_LOAD_PALETTE", vm => ApplyBank(ReadRegister(vm, "A0")));
    }

    // the fucking syscall handlers

    private static void SetPaletteColor(object vm)
    {
        int index = ReadRegister(vm, "A0");
        int rgb = ReadRegister(vm, "A1") & 0xFFFFFF;
        if (index is >= 0 and <= 255)
            SetEntry(index, rgb);
    }

    private static void ColorToIndexSyscall(object vm)
    {
        int rgb = ReadRegister(vm, "A0") & 0xFFFFFF;
        WriteRegister(vm, "A0", ColorToIndex(rgb));
    }

    private static void GetPaletteColor(object vm)
    {
        int index = ReadRegister(vm, "A0");
        WriteRegister(vm, "A0", index is >= 0 and <= 255 ? Palette[index] : 0);
    }

    private static void SetRangeSyscall(object vm)
    {
        int first = ReadRegister(vm, "A0");
        int count = ReadRegister(vm, "A1");
        int address = ReadRegister(vm, "A2");
        SetRange(vm, first, count, address);
    }

    private static void GetRangeSyscall(object vm)
    {
        int first = ReadRegister(vm, "A0");
        int count = ReadRegister(vm, "A1");
        int address = ReadRegister(vm, "A2");
        GetRange(vm, first, count, address);
    }

    // default palette to start, reset it so no leftover fuckery
    private static HookResult OnVmStart(object? vm, object?[] args)
    {
        try
        {
            _vm = null; _memory = null;
            ResetPalette();
            LoadEmbeddedTables(args.Length > 4 ? args[4] as byte[] : null);
            ApplyBank(0);
        }
        catch (Exception) { }

        return HookResult.Continue(); // let game's own Initialize run
    }

    private static void ResetPalette()
    {
        for (int i = 0; i < 256; i++)
        {
            Palette[i] = LinearInterpolation(0x0F1517, 0xB9C2A5, i);
            Claimed[i] = false;
        }
        Claimed[0] = true;

        if (_paletteNode != null)
            Bridge.Call(_paletteNode, "reset_palette");
    }

    // entry 0 stays the background
    private static void ClearPalette()
    {
        for (int i = 1; i < 256; i++)
            Store(i, 0);
        PushAll();
    }

    // layout: marker, u8 id, u8 first, u8 count, count * u32 (0x00RRGGBB BE)
    private static void LoadEmbeddedTables(byte[]? program)
    {
        Banks.Clear();
        if (program == null) return;

        int pos = 0;
        while (pos < program.Length)
        {
            int at = program.AsSpan(pos).IndexOf(PaletteMagic);
            if (at < 0) return;

            int p = pos + at + PaletteMagic.Length;
            pos = p;
            if (p + 3 > program.Length) return;

            int id = program[p];
            int first = program[p + 1];
            int count = program[p + 2];
            p += 3;
            if (count == 0 || first + count > 256 || p + count * 4 > program.Length) continue;

            var colors = new int[count];
            for (int i = 0; i < count; i++)
                colors[i] = BinaryPrimitives.ReadInt32BigEndian(program.AsSpan(p + i * 4)) & 0xFFFFFF;

            Banks[id] = (first, colors);
        }
    }

    private static void ApplyBank(int id)
    {
        if (!Banks.TryGetValue(id, out var bank)) return;

        for (int i = 0; i < bank.Colors.Length; i++)
            Store(bank.First + i, bank.Colors[i]);
        PushAll();
    }

    private static void Store(int index, int rgb)
    {
        Palette[index] = rgb;
        Claimed[index] = true;
    }

    private static void SetEntry(int index, int rgb)
    {
        Store(index, rgb);
        PushToScreen(index, rgb);
    }

    // never creates entry, finds closest (for nowwwwwwwww) //TODO
    private static int ColorToIndex(int rgb)
    {
        int best = 1;
        int bestDist = int.MaxValue;
        for (int i = 1; i < 256; i++)
        {
            int d = Distance(Palette[i], rgb);
            if (d == 0) return i; /* exact match */
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    private static void SetRange(object vm, int first, int count, int address)
    {
        if (first < 0 || count <= 0 || first + count > 256) return;

        var memory = Memory(vm);
        int start = ReadRegister(vm, ProgramAddressRegister) + address;
        if (address < 0 || start + count * 4 > memory.Length) return;

        for (int i = 0; i < count; i++)
            Store(first + i, BinaryPrimitives.ReadInt32BigEndian(memory.AsSpan(start + i * 4)) & 0xFFFFFF);
        PushAll();
    }

    private static void GetRange(object vm, int first, int count, int address)
    {
        if (first < 0 || count <= 0 || first + count > 256) return;

        var memory = Memory(vm);
        int start = ReadRegister(vm, ProgramAddressRegister) + address;
        if (address < 0 || start + count * 4 > memory.Length) return;

        for (int i = 0; i < count; i++)
            BinaryPrimitives.WriteInt32BigEndian(memory.AsSpan(start + i * 4), Palette[first + i]);
    }

    private static void PushToScreen(int index, int rgb)
    {
        // first time main.gd is reached, send everything so far
        if (_paletteNode == null) { PushAll(); return; }
        Send(index, rgb);
    }

    // one gd call for the whole palette
    private static void PushAll()
    {
        _paletteNode ??= FindPaletteNode();
        if (_paletteNode == null) return;

        for (int i = 0; i < 256; i++)
        {
            PaletteBytes[i * 3] = (byte)(Palette[i] >> 16);
            PaletteBytes[i * 3 + 1] = (byte)(Palette[i] >> 8);
            PaletteBytes[i * 3 + 2] = (byte)Palette[i];
        }
        Bridge.Call(_paletteNode, "set_palette_bytes", PaletteBytes);
    }

    private static void Send(int index, int rgb)
        => Bridge.Call(_paletteNode!, "set_color", index, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    private static object? FindPaletteNode()
    {
        var tree = Bridge.CallStatic("Godot.Engine", "GetMainLoop")!;
        return Bridge.Call(tree, "get_first_node_in_group", "misaml_palette");
    }

    private static int LinearInterpolation(int a, int b, int i)
    {
        int Ch(int shift) => (int)Math.Round((((a >> shift) & 0xFF) * (255 - i) + ((b >> shift) & 0xFF) * i) / 255.0);
        return (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }

    private static int Distance(int a, int b)
    {
        int dr = ((a >> 16) & 0xFF) - ((b >> 16) & 0xFF);
        int dg = ((a >> 8) & 0xFF) - ((b >> 8) & 0xFF);
        int db = (a & 0xFF) - (b & 0xFF);
        return dr * dr + dg * dg + db * db;
    }

    private static void Sync(object vm)
    {
        if (ReferenceEquals(vm, _vm)) return;
        _vm = vm;
        _memory = null;
    }

    private static int[] Registers(object vm)
    {
        Sync(vm);
        return (int[])Bridge.GetMember(vm, "_registers")!;
    }

    private static byte[] Memory(object vm)
    {
        Sync(vm);
        return _memory ??= (byte[])Bridge.GetMember(vm, "_memory")!;
    }

    private static int ReadRegister(object vm, string name)
        => Registers(vm)[RegIndex(vm, name)];

    private static void WriteRegister(object vm, string name, int value)
        => Registers(vm)[RegIndex(vm, name)] = value;

    private static int RegIndex(object vm, string name)
    {
        if (RegIndexes.TryGetValue(name, out var index)) return index;

        _regCode ??= vm.GetType().Assembly.GetType("Asm.General.RegCode")!;
        index = Convert.ToInt32(Enum.Parse(_regCode, name));
        RegIndexes[name] = index;
        return index;
    }
}

// alpha blending would be sick later, and palette rotation //TODO
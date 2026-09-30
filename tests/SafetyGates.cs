// Exercises the compiled safety gates of I6Pull without touching any hardware.
// Only the pure static methods TxAllowed() and RequireReadRequest() are invoked by reflection;
// nothing here loads CMNSI632.dll or opens an adapter.
//
//   csc /target:library /platform:anycpu /out:I6Pull.lib.dll ..\src\I6Pull.cs
//   csc /out:SafetyGates.exe SafetyGates.cs
//   SafetyGates.exe I6Pull.lib.dll
using System;
using System.Reflection;

static class SafetyGates
{
    static Type program;
    static int failures, checks;

    static byte[] Frame(byte b0) { var f = new byte[8]; f[0] = b0; return f; }

    static byte[] Read(int cmd, int itn, int offset, int length)
    {
        return new byte[] {
            (byte)cmd, (byte)(itn >> 8), (byte)itn,
            (byte)(offset >> 24), (byte)(offset >> 16), (byte)(offset >> 8), (byte)offset,
            (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length };
    }

    static bool Tx(int id, bool ext, byte[] p)
    {
        var m = program.GetMethod("TxAllowed", BindingFlags.NonPublic | BindingFlags.Static);
        return (bool)m.Invoke(null, new object[] { id, ext, p });
    }

    static bool ReadAllowed(byte[] p)
    {
        var m = program.GetMethod("RequireReadRequest", BindingFlags.NonPublic | BindingFlags.Static);
        try { m.Invoke(null, new object[] { p }); return true; }
        catch (TargetInvocationException e)
        {
            if (e.InnerException is InvalidOperationException) return false;
            throw;
        }
    }

    static void Expect(string name, bool actual, bool expected)
    {
        checks++;
        if (actual != expected) { failures++; Console.WriteLine("FAIL " + name + ": got " + actual + ", want " + expected); }
        else Console.WriteLine("ok   " + name);
    }

    static int Main(string[] args)
    {
        program = Assembly.LoadFrom(args[0]).GetType("Program", true);

        // --- transmit allowlist: J1939 transport from tool address 0xF9 only
        Expect("TP.CM RTS 0x10 on 18EC00F9", Tx(0x18EC00F9, true, Frame(0x10)), true);
        Expect("TP.CM CTS 0x11 on 18EC00F9", Tx(0x18EC00F9, true, Frame(0x11)), true);
        Expect("TP.CM EOM-ACK 0x13 on 18EC00F9", Tx(0x18EC00F9, true, Frame(0x13)), true);
        Expect("TP.DT on 18EB00F9", Tx(0x18EB00F9, true, Frame(0x01)), true);
        Expect("TP.CM BAM 0x20 refused", Tx(0x18EC00F9, true, Frame(0x20)), false);
        Expect("TP.CM abort 0xFF refused", Tx(0x18EC00F9, true, Frame(0xFF)), false);
        Expect("7-byte TP.DT refused", Tx(0x18EB00F9, true, new byte[7]), false);
        Expect("request PGN EF00 direct refused", Tx(0x18EF00F9, true, Frame(0x48)), false);
        Expect("ECM-address frame refused", Tx(0x18ECF900, true, Frame(0x10)), false);
        foreach (int id in new[] { 0x112, 0x512, 0x001, 0x500, 0x7DF, 0x18EC })
            Expect("11-bit id 0x" + id.ToString("X") + " refused", Tx(id, false, Frame(0x10)), false);
        Expect("null payload refused", Tx(0x18EC00F9, true, null), false);

        // --- request choke point: ReadByNTN only, not blocked, bounded
        Expect("ReadByNTN 104F len 588", ReadAllowed(Read(0x48, 0x104F, 0, 588)), true);
        Expect("ReadByNTN len 1024", ReadAllowed(Read(0x48, 0x0100, 0, 1024)), true);
        Expect("write opcode 0x43 refused", ReadAllowed(Read(0x43, 0x104F, 0, 16)), false);
        Expect("write opcode 0x46 refused", ReadAllowed(Read(0x46, 0x104F, 0, 16)), false);
        foreach (int itn in new[] { 0x0005, 0x0016, 0x001E, 0x001F, 0x0020, 0x0021, 0x0022, 0x1000, 0x1083, 0x11AF, 0x1267 })
            Expect("blocked ITN " + itn.ToString("X4") + " refused", ReadAllowed(Read(0x48, itn, 0, 16)), false);
        Expect("length 0 refused", ReadAllowed(Read(0x48, 0x104F, 0, 0)), false);
        Expect("length 1025 refused", ReadAllowed(Read(0x48, 0x104F, 0, 1025)), false);
        Expect("negative length refused", ReadAllowed(Read(0x48, 0x104F, 0, -1)), false);
        Expect("short payload refused", ReadAllowed(new byte[] { 0x48, 0x10, 0x4F }), false);
        Expect("null request refused", ReadAllowed(null), false);

        Console.WriteLine(checks + " checks, " + failures + " failed");
        return failures == 0 ? 0 : 1;
    }
}

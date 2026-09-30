// I6Pull — read-only Cummins INLINE 6 / RP1210 puller for Dodge CM551 (ISB VP44).
// Reads KennPar ITNs over 29-bit CAN (PGN EF00, command 0x48). Never programs,
// erases, jumps to bootloader, or transmits VP44 11-bit fueling frames.
//
// Safety model (enforced in code, not just documented):
//   * The only transmit path is SendCan(), which accepts an explicit allowlist of frames:
//     J1939 transport control (RTS/CTS/EOM-ACK) and data on 0x18EC00F9 / 0x18EB00F9.
//     No 11-bit frame is ever sent.
//   * Every request to the ECM passes RequireReadRequest(): ReadByNTN (0x48) only, for an
//     ITN that is not blocked, with a bounded length.
//   * ITNs are taken only from the catalog CSV. --itn selects from it; it cannot add to it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class Native
{
    public const string Dll = "CMNSI632.dll";

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool SetDllDirectory(string path);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern short RP1210_ClientConnect(
        IntPtr hwnd, short deviceId, string protocol, int txBuf, int rxBuf, short packetize);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern short RP1210_ClientDisconnect(short clientId);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern short RP1210_SendMessage(short clientId, byte[] buf, short size, short notify, short block);

    // RP1210B (CMNSI632.ini RP1210=B): buffer, then size, then blocking.
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern short RP1210_ReadMessage(short clientId, byte[] buf, short bufSize, short blockMs);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern short RP1210_SendCommand(short cmd, short clientId, byte[] data, short len);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern short RP1210_GetErrorMsg(short err, StringBuilder buf);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern short RP1210_GetHardwareStatus(short clientId, byte[] buf, short bufSize, short block);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern short RP1210_ReadDetailedVersion(short clientId, StringBuilder api, StringBuilder dll, StringBuilder fw);

    [DllImport("user32.dll")]
    public static extern bool PeekMessage(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref Msg msg);
}

[StructLayout(LayoutKind.Sequential)]
internal struct Msg
{
    public IntPtr hwnd;
    public uint message;
    public IntPtr wParam;
    public IntPtr lParam;
    public uint time;
    public int ptX;
    public int ptY;
}

internal static class Rp
{
    public const short AllFiltersPass = 3;
    public const short EchoTx = 16;
    public const short SetMessageReceive = 18;
    public const short DeviceUsb = 254;
    public const byte ToolAddr = 0xF9;

    public static string Err(short code)
    {
        var sb = new StringBuilder(256);
        try { Native.RP1210_GetErrorMsg(code, sb); } catch { }
        return sb.ToString();
    }

    public static string Hex(byte[] b, int n)
    {
        if (n <= 0) return "";
        var sb = new StringBuilder(n * 3);
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(b[i].ToString("X2"));
        }
        return sb.ToString();
    }
}

internal static class Program
{
    // Never request these. They are passwords, calibration-download keys or boot-copy addresses.
    // Keep in sync with BLOCKED_ITNS in vp44tune/i6host.py (a unit test compares them).
    static readonly HashSet<int> BlockedItn = new HashSet<int>
    {
        0x0005, 0x0016, 0x001E, 0x001F, 0x0020, 0x0021, 0x0022,
        0x1000, 0x1083, 0x11AF, 0x1267
    };

    // Largest single ReadByNTN request; longer reads are split into chunks of this size.
    const int MaxReadChunk = 1024;

    // Transmit allowlist. Tool address 0xF9 to the ECM, J1939 transport protocol only:
    // TP.CM (PGN EC00) with control byte RTS 0x10, CTS 0x11 or EOM-ACK 0x13, and TP.DT (PGN EB00).
    // Everything else, including every 11-bit frame (the ECM<->VP44 fueling bus), is refused.
    static bool TxAllowed(int canId, bool ext29, byte[] payload)
    {
        if (!ext29 || payload == null) return false;
        if (canId == 0x18EB00F9) return payload.Length == 8;
        if (canId == 0x18EC00F9)
            return payload.Length == 8 && (payload[0] == 0x10 || payload[0] == 0x11 || payload[0] == 0x13);
        return false;
    }

    // Choke point for requests to the ECM: ReadByNTN only, for a non-blocked ITN, bounded length.
    static void RequireReadRequest(byte[] payload)
    {
        if (payload == null || payload.Length != 11 || payload[0] != 0x48)
            throw new InvalidOperationException("refusing to transmit: not a ReadByNTN request");
        int ntn = (payload[1] << 8) | payload[2];
        int length = (payload[7] << 24) | (payload[8] << 16) | (payload[9] << 8) | payload[10];
        if (IsBlocked(ntn))
            throw new InvalidOperationException("refusing to transmit: ITN " + ntn.ToString("X4") + " is blocked");
        if (length <= 0 || length > MaxReadChunk)
            throw new InvalidOperationException("refusing to transmit: bad read length " + length);
    }

    static bool Quiet = true;
    static string ReadsPath = "";
    static int Limit;
    static readonly List<int> OnlyItns = new List<int>();
    static int LenOverride;
    static bool ProbeOnly;

    static int Main(string[] args)
    {
        string outPath = "dump.jsonl";
        string proto = "CAN:Baud=250,Channel=1";
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        string defaultReads = Path.Combine(exeDir, "catalog", "chr0000_reads.csv");
        if (File.Exists(defaultReads)) ReadsPath = defaultReads;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h") { Help(); return 0; }
            else if (a == "--out" && i + 1 < args.Length) outPath = args[++i];
            else if (a == "--proto" && i + 1 < args.Length) proto = args[++i];
            else if (a == "--reads" && i + 1 < args.Length) ReadsPath = args[++i];
            else if (a == "--limit" && i + 1 < args.Length) Limit = int.Parse(args[++i]);
            else if (a == "--itn" && i + 1 < args.Length)
            {
                int v;
                if (!int.TryParse(args[++i].Replace("0x", "").Replace("0X", ""),
                        System.Globalization.NumberStyles.HexNumber, null, out v))
                { Console.WriteLine("bad --itn value " + args[i]); return 2; }
                OnlyItns.Add(v);
            }
            else if (a == "--len" && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], out LenOverride) || LenOverride <= 0)
                { Console.WriteLine("bad --len value " + args[i]); return 2; }
            }
            else if (a == "--probe") ProbeOnly = true;
            else if (a == "--verbose") Quiet = false;
            else
            {
                Console.WriteLine("unknown arg " + a);
                Help();
                return 2;
            }
        }

        if (string.IsNullOrEmpty(ReadsPath) || !File.Exists(ReadsPath))
        {
            Console.WriteLine("No catalog CSV. Pass --reads catalog/chr0000_reads.csv");
            return 2;
        }

        Native.SetDllDirectory(@"C:\Windows\SysWOW64");
        Console.WriteLine("I6Pull READ-ONLY  proto=" + proto);
        Console.WriteLine("Will not send erase / program / bootloader / write commands.");
        Console.WriteLine("Will not TX 11-bit IDs 0x112/0x512/0x001/0x500 (VP44).");

        using (var w = new StreamWriter(outPath, false, new UTF8Encoding(false)))
        {
            Log(w, "meta", "read_only=true blocked_writes=true reads=" + ReadsPath);
            if (!Session(w, proto))
                return 1;
        }
        Console.WriteLine("wrote " + Path.GetFullPath(outPath));
        return 0;
    }

    static void Help()
    {
        Console.WriteLine("I6Pull — read-only INLINE 6 dump of a Dodge CM551");
        Console.WriteLine("  I6Pull.exe [--out dump.jsonl] [--reads catalog/chr0000_reads.csv]");
        Console.WriteLine("            [--proto CAN:Baud=250,Channel=1] [--limit N] [--verbose]");
        Console.WriteLine("            [--probe] [--itn HEX] [--len N]");
        Console.WriteLine();
        Console.WriteLine("Close INSITE first (it holds the adapter). Key-on, engine stopped is fine.");
        Console.WriteLine("Requires Cummins INLINE 6 USB drivers (CMNSI632.dll, 32-bit).");
        Console.WriteLine("--probe connects, prints hardware, disconnects. No catalog pull.");
        Console.WriteLine("--itn HEX may be repeated. --len N overrides byte length for those ITNs.");
        Console.WriteLine("AFFLLMZA (104F) is always requested at 588 bytes (21x14 cells).");
        Console.WriteLine("Read-only. No 0x43/0x46. No 11-bit VP44 TX.");
    }

    static bool Session(StreamWriter w, string protocol)
    {
        short id = Native.RP1210_ClientConnect(IntPtr.Zero, Rp.DeviceUsb, protocol, 8192, 8192, 0);
        Log(w, "connect", "proto=" + protocol + " rc=" + id + " " + Rp.Err(id));
        if (id < 0 || id > 127)
        {
            Console.WriteLine("CONNECT FAIL " + protocol + " " + id + " " + Rp.Err(id));
            Console.WriteLine("Close INSITE / other INLINE tools and retry.");
            return false;
        }
        try
        {
            HwInfo(w, id);
            Cmd(w, id, Rp.SetMessageReceive, new byte[] { 1 }, "SET_MESSAGE_RECEIVE on");
            Cmd(w, id, Rp.EchoTx, new byte[] { 1 }, "ECHO_TX on");
            Cmd(w, id, Rp.AllFiltersPass, null, "ALL_FILTERS_PASS");
            Drain(w, id, 80, protocol + "-idle");
            if (ProbeOnly)
            {
                Log(w, "probe", "ok");
                Console.WriteLine("PROBE OK");
                return true;
            }
            CatalogPull(w, id, ReadsPath);
            return true;
        }
        finally
        {
            Native.RP1210_ClientDisconnect(id);
            Log(w, "disconnect", protocol);
        }
    }

    static void HwInfo(StreamWriter w, short id)
    {
        var api = new StringBuilder(80);
        var dll = new StringBuilder(80);
        var fw = new StringBuilder(80);
        short rc = Native.RP1210_ReadDetailedVersion(id, api, dll, fw);
        Log(w, "ver", "rc=" + rc + " api=" + api + " dll=" + dll + " fw=" + fw);
        var buf = new byte[64];
        rc = Native.RP1210_GetHardwareStatus(id, buf, (short)buf.Length, 0);
        Log(w, "hw", "rc=" + rc + " " + Rp.Err(rc) + " " + Rp.Hex(buf, Math.Max((int)rc, 16)));
    }

    static void Cmd(StreamWriter w, short id, short cmd, byte[] data, string tag)
    {
        short rc = Native.RP1210_SendCommand(cmd, id, data, (short)(data == null ? 0 : data.Length));
        Log(w, "cmd", tag + " cmd=" + cmd + " rc=" + rc + " " + Rp.Err(rc));
    }

    static void CatalogPull(StreamWriter w, short id, string path)
    {
        var rows = new List<int[]>();
        var names = new List<string>();
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line.StartsWith("itn", StringComparison.OrdinalIgnoreCase)) continue;
            string[] p = line.Split(',');
            if (p.Length < 2) continue;
            int itn = Convert.ToInt32(p[0].Trim(), 16);
            int len = int.Parse(p[1].Trim());
            if (IsBlocked(itn) || len <= 0) continue;
            if (OnlyItns.Count > 0 && !OnlyItns.Contains(itn)) continue;
            // --len can only shorten a catalog read, never extend it.
            if (LenOverride > 0 && OnlyItns.Contains(itn) && LenOverride < len) len = LenOverride;
            if (itn == 0x104F && len < 588) len = 588; // AFFLLMZA 21x14 x u16
            rows.Add(new int[] { itn, len });
            names.Add(p.Length > 2 ? p[2].Trim() : "");
        }
        // --itn selects from the catalog; an ITN the catalog does not list is refused, never invented.
        for (int k = 0; k < OnlyItns.Count; k++)
        {
            int want = OnlyItns[k];
            bool have = false;
            for (int r = 0; r < rows.Count; r++) if (rows[r][0] == want) { have = true; break; }
            if (!have)
                Console.WriteLine("ITN " + want.ToString("X4") + " is blocked or not in the catalog; not requested");
        }
        int n = rows.Count;
        if (Limit > 0 && Limit < n) n = Limit;
        Log(w, "catalog", "file=" + path + " n=" + n + " of " + rows.Count);
        int ok = 0, miss = 0, streak = 0;
        var swAll = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < n; i++)
        {
            int itn = rows[i][0];
            int len = rows[i][1];
            byte[] payload = CanReadNtn(w, id, itn, len);
            if (payload != null && payload.Length > 0)
            {
                ok++;
                streak = 0;
                byte[] data = StripEnvelope(payload);
                Log(w, "ntn", "ITN " + itn.ToString("X4") + " name=" + names[i] +
                    " req=" + len + " raw=" + payload.Length + " data=" + data.Length + " " + Rp.Hex(data, data.Length));
            }
            else
            {
                miss++;
                streak++;
                Log(w, "ntn", "ITN " + itn.ToString("X4") + " name=" + names[i] + " no payload");
            }
            if ((i + 1) % 10 == 0 || i + 1 == n)
                Console.WriteLine("pull " + (i + 1) + "/" + n + " ok=" + ok + " miss=" + miss + " " + (swAll.ElapsedMilliseconds / 1000) + "s");
            if (ok == 0 && miss >= 8)
            {
                Log(w, "abort", "no ECM replies — is the key on? close INSITE?");
                break;
            }
            if (streak >= 80)
            {
                Log(w, "abort", "80 consecutive misses — stop");
                break;
            }
        }
        Log(w, "catalog_done", "ok=" + ok + " miss=" + miss + " ms=" + swAll.ElapsedMilliseconds);
    }

    static byte[] StripEnvelope(byte[] all)
    {
        if (all != null && all.Length > 11 && all[0] == 0x49)
        {
            var d = new byte[all.Length - 11];
            Buffer.BlockCopy(all, 11, d, 0, d.Length);
            return d;
        }
        return all ?? new byte[0];
    }

    static byte[] CanReadNtn(StreamWriter w, short client, int ntn, int length)
    {
        const int chunk = MaxReadChunk;
        if (length <= chunk)
            return CanReadNtnOnce(w, client, ntn, 0, length);
        var parts = new List<byte[]>();
        int off = 0;
        while (off < length)
        {
            int n = Math.Min(chunk, length - off);
            byte[] raw = CanReadNtnOnce(w, client, ntn, off, n);
            if (raw == null) break;
            byte[] data = StripEnvelope(raw);
            if (data.Length == 0) break;
            parts.Add(data);
            if (data.Length < n) break;
            off += data.Length;
        }
        if (parts.Count == 0) return null;
        int total = 0;
        for (int i = 0; i < parts.Count; i++) total += parts[i].Length;
        var body = new byte[total];
        int o = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            Buffer.BlockCopy(parts[i], 0, body, o, parts[i].Length);
            o += parts[i].Length;
        }
        var wrapped = new byte[11 + body.Length];
        wrapped[0] = 0x49;
        wrapped[1] = (byte)(ntn >> 8);
        wrapped[2] = (byte)ntn;
        wrapped[7] = (byte)(body.Length >> 24);
        wrapped[8] = (byte)(body.Length >> 16);
        wrapped[9] = (byte)(body.Length >> 8);
        wrapped[10] = (byte)body.Length;
        Buffer.BlockCopy(body, 0, wrapped, 11, body.Length);
        return wrapped;
    }

    static byte[] CanReadNtnOnce(StreamWriter w, short client, int ntn, int offset, int length)
    {
        byte[] req = new byte[] {
            0x48, (byte)(ntn >> 8), (byte)ntn,
            (byte)(offset >> 24), (byte)(offset >> 16), (byte)(offset >> 8), (byte)offset,
            (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length
        };
        if (!CanSendTpEf00(w, client, req))
            return null;
        byte[] rts = WaitCan29(client, 0x18ECF900, 0x10, 250);
        if (rts == null)
            return null;
        int total = rts[1] | (rts[2] << 8);
        int pkts = rts[3] & 0xFF;
        if (pkts <= 0) pkts = (total + 6) / 7;
        var parts = new byte[Math.Max(pkts, 1)][];
        int got = 0;
        int next = 1;
        while (got < pkts)
        {
            int win = Math.Min(32, pkts - got);
            byte[] cts = new byte[] { 0x11, (byte)win, (byte)next, 0xFF, 0xFF, 0x00, 0xEF, 0x00 };
            SendCan(w, client, 0x18EC00F9, true, cts, "CTS");
            int before = got;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var buf = new byte[256];
            int waitMs = 400 + win * 20;
            while (sw.ElapsedMilliseconds < waitMs && got < before + win)
            {
                short n = ReadMaybe(client, buf);
                if (n < 12 || IsRpError(n)) continue;
                int echo, ext, canId;
                byte[] data;
                if (!ParseCan(buf, n, out echo, out ext, out canId, out data)) continue;
                if (echo != 0 || ext == 0 || canId != 0x18EBF900 || data.Length < 2) continue;
                int seq = data[0];
                if (seq < 1 || seq > pkts) continue;
                if (parts[seq - 1] != null) continue;
                var chunk = new byte[data.Length - 1];
                Buffer.BlockCopy(data, 1, chunk, 0, chunk.Length);
                parts[seq - 1] = chunk;
                got++;
            }
            if (got == before) break;
            next = got + 1;
        }
        SendCan(w, client, 0x18EC00F9, true,
            new byte[] { 0x13, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0xEF, 0x00 }, "EOMACK");
        if (got == 0) return null;
        int filled = 0;
        for (int i = 0; i < pkts; i++)
        {
            if (parts[i] == null) break;
            filled += parts[i].Length;
        }
        if (filled == 0) return null;
        var all = new byte[filled];
        int o = 0;
        for (int i = 0; i < pkts; i++)
        {
            if (parts[i] == null) break;
            Buffer.BlockCopy(parts[i], 0, all, o, parts[i].Length);
            o += parts[i].Length;
        }
        if (total > 0 && o > total)
        {
            var trim = new byte[total];
            Buffer.BlockCopy(all, 0, trim, 0, total);
            return trim;
        }
        if (o < all.Length)
        {
            var trim = new byte[o];
            Buffer.BlockCopy(all, 0, trim, 0, o);
            return trim;
        }
        return all;
    }

    static bool CanSendTpEf00(StreamWriter w, short client, byte[] payload)
    {
        RequireReadRequest(payload);
        int total = payload.Length;
        int pkts = (total + 6) / 7;
        byte[] rts = new byte[] {
            0x10, (byte)total, (byte)(total >> 8), (byte)pkts, 0xFF, 0x00, 0xEF, 0x00
        };
        SendCan(w, client, 0x18EC00F9, true, rts, "tool RTS");
        byte[] cts = WaitCan29(client, 0x18ECF900, 0x11, 200);
        if (cts == null) return false;
        int seq = 1, off = 0;
        while (off < total)
        {
            int n = Math.Min(7, total - off);
            var dt = new byte[8];
            dt[0] = (byte)seq;
            Buffer.BlockCopy(payload, off, dt, 1, n);
            for (int i = 1 + n; i < 8; i++) dt[i] = 0xFF;
            SendCan(w, client, 0x18EB00F9, true, dt, "tool DT");
            off += n;
            seq++;
        }
        return true;
    }

    static byte[] WaitCan29(short client, int wantId, byte firstData, int ms)
    {
        var buf = new byte[256];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            short n = ReadMaybe(client, buf);
            if (n < 12 || IsRpError(n)) continue;
            int echo, ext, canId;
            byte[] data;
            if (!ParseCan(buf, n, out echo, out ext, out canId, out data)) continue;
            if (echo == 0 && ext != 0 && canId == wantId && data.Length > 0 && data[0] == firstData)
                return data;
        }
        return null;
    }

    static bool ParseCan(byte[] buf, int n, out int echo, out int ext, out int canId, out byte[] data)
    {
        echo = 0; ext = 0; canId = 0; data = new byte[0];
        if (n < 10) return false;
        echo = buf[4];
        if (n >= 12 && buf[5] == 1)
        {
            ext = 1;
            canId = (buf[6] << 24) | (buf[7] << 16) | (buf[8] << 8) | buf[9];
            int dlen = n - 10;
            data = new byte[dlen];
            Buffer.BlockCopy(buf, 10, data, 0, dlen);
            return true;
        }
        ext = 0;
        canId = (buf[6] << 8) | buf[7];
        int d2 = n - 8;
        if (d2 < 0) return false;
        data = new byte[d2];
        Buffer.BlockCopy(buf, 8, data, 0, d2);
        return true;
    }

    static void SendCan(StreamWriter w, short id, int canId, bool ext29, byte[] payload, string tag)
    {
        if (!TxAllowed(canId, ext29, payload))
            throw new InvalidOperationException("refusing to transmit frame 0x" + canId.ToString("X") + (ext29 ? " (29-bit)" : " (11-bit)"));
        var a = new byte[5 + payload.Length];
        a[0] = (byte)(ext29 ? 1 : 0);
        a[1] = (byte)((canId >> 24) & 0xFF);
        a[2] = (byte)((canId >> 16) & 0xFF);
        a[3] = (byte)((canId >> 8) & 0xFF);
        a[4] = (byte)(canId & 0xFF);
        Buffer.BlockCopy(payload, 0, a, 5, payload.Length);
        short rc = Native.RP1210_SendMessage(id, a, (short)a.Length, 0, 0);
        if (!Quiet)
            Log(w, "tx", tag + " rc=" + rc + " " + Rp.Hex(a, a.Length));
        if (rc != 0)
            Console.WriteLine("TX fail " + tag + " rc=" + rc + " " + Rp.Err(rc));
    }

    static bool IsBlocked(int itn)
    {
        return BlockedItn.Contains(itn);
    }

    static void Drain(StreamWriter w, short id, int ms, string proto)
    {
        var buf = new byte[2048];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int nmsg = 0, nerr = 0, nempty = 0;
        while (sw.ElapsedMilliseconds < ms)
        {
            Pump();
            short n = ReadMaybe(id, buf);
            if (n > 0 && !IsRpError(n)) nmsg++;
            else if (IsRpError(n)) { nerr++; System.Threading.Thread.Sleep(20); }
            else { nempty++; System.Threading.Thread.Sleep(10); }
        }
        Log(w, "listen_done", proto + " messages=" + nmsg + " empty=" + nempty + " err=" + nerr + " ms=" + ms);
    }

    static bool IsRpError(short n)
    {
        if (n < 0) return true;
        if (n < 128) return false;
        if (n <= 162) return true;
        if (n == 202 || n == 213 || n == 220 || n == 222) return true;
        return false;
    }

    static short ReadMaybe(short id, byte[] buf)
    {
        Pump();
        return Native.RP1210_ReadMessage(id, buf, (short)buf.Length, 0);
    }

    static void Pump()
    {
        Msg m;
        while (Native.PeekMessage(out m, IntPtr.Zero, 0, 0, 1))
        {
            Native.TranslateMessage(ref m);
            Native.DispatchMessage(ref m);
        }
    }

    static void Log(StreamWriter w, string kind, string msg)
    {
        string line = "{\"t\":\"" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff") +
                      "Z\",\"k\":\"" + kind + "\",\"m\":\"" +
                      msg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}";
        w.WriteLine(line);
        w.Flush();
        if (kind == "connect" || kind == "cmd" || kind == "listen_done" || kind == "meta" ||
            kind == "ver" || kind == "hw" || kind == "catalog" || kind == "catalog_done" || kind == "abort")
            Console.WriteLine(kind + " " + msg);
    }
}

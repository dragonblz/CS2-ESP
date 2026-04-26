using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FoxSense.Core;

/// <summary>
/// Direct-syscall memory engine. Reads ntdll from disk to bypass hooks.
/// </summary>
public sealed class Memory : IDisposable
{
    // ── kernel32 imports (never ntdll) ──
    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualAlloc(IntPtr addr, uint size, uint type, uint protect);

    [DllImport("kernel32.dll")]
    private static extern bool VirtualFree(IntPtr addr, uint size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    // ── Syscall delegate ──
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NtReadDelegate(
        IntPtr ProcessHandle, IntPtr BaseAddress,
        byte[] Buffer, int Size, out int BytesRead);

    private NtReadDelegate? _sysRead;
    private IntPtr _stubMem = IntPtr.Zero;

    // Minimum rights — read only
    private const int PROCESS_VM_READ = 0x0010;
    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint MEM_RELEASE = 0x8000;

    public IntPtr Handle { get; private set; }
    public IntPtr ClientBase { get; private set; }
    public int Pid { get; private set; }
    public bool IsAttached { get; private set; }

    // XOR-obfuscated strings
    private static string Decode(byte[] data)
    {
        var sb = new StringBuilder(data.Length);
        foreach (byte b in data) sb.Append((char)(b ^ 0x5A));
        return sb.ToString();
    }

    private static readonly byte[] TargetProc = { 0x39, 0x29, 0x68 };                                     // "cs2"
    private static readonly byte[] TargetModule = { 0x39, 0x36, 0x33, 0x3F, 0x34, 0x2E, 0x74, 0x3E, 0x36, 0x36 }; // "client.dll"

    // ═══════════════════════════════════════════════════
    //  SYSCALL INITIALIZATION
    // ═══════════════════════════════════════════════════

    private bool InitSyscall()
    {
        try
        {
            string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            byte[] cleanNtdll = File.ReadAllBytes(Path.Combine(sysDir, "ntdll.dll"));

            int sysNum = FindSyscallNumber(cleanNtdll, "NtReadVirtualMemory");
            if (sysNum < 0) return false;

            byte[] stub =
            {
                0x4C, 0x8B, 0xD1,                                                // mov r10, rcx
                0xB8, (byte)(sysNum & 0xFF), (byte)((sysNum >> 8) & 0xFF), 0x00, 0x00, // mov eax, sysNum
                0x0F, 0x05,                                                        // syscall
                0xC3                                                               // ret
            };

            _stubMem = VirtualAlloc(IntPtr.Zero, (uint)stub.Length,
                MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (_stubMem == IntPtr.Zero) return false;

            Marshal.Copy(stub, 0, _stubMem, stub.Length);
            _sysRead = Marshal.GetDelegateForFunctionPointer<NtReadDelegate>(_stubMem);
            return true;
        }
        catch { return false; }
    }

    // ═══════════════════════════════════════════════════
    //  PE EXPORT PARSER
    // ═══════════════════════════════════════════════════

    private static int FindSyscallNumber(byte[] pe, string funcName)
    {
        if (pe.Length < 0x40 || pe[0] != 0x4D || pe[1] != 0x5A) return -1;

        int peOff = BitConverter.ToInt32(pe, 0x3C);
        if (peOff <= 0 || peOff + 4 >= pe.Length) return -1;
        if (pe[peOff] != 0x50 || pe[peOff + 1] != 0x45) return -1;

        int numSections = BitConverter.ToInt16(pe, peOff + 6);
        int optHeaderSize = BitConverter.ToInt16(pe, peOff + 20);
        int ohOff = peOff + 24;
        if (ohOff + 2 >= pe.Length || BitConverter.ToUInt16(pe, ohOff) != 0x20B) return -1;

        int exportRva = BitConverter.ToInt32(pe, ohOff + 112);
        if (exportRva == 0) return -1;

        int secStart = ohOff + optHeaderSize;
        int exportOff = RvaToFile(pe, exportRva, secStart, numSections);
        if (exportOff < 0 || exportOff + 40 >= pe.Length) return -1;

        int numNames = BitConverter.ToInt32(pe, exportOff + 24);
        int namesOff = RvaToFile(pe, BitConverter.ToInt32(pe, exportOff + 32), secStart, numSections);
        int ordinalsOff = RvaToFile(pe, BitConverter.ToInt32(pe, exportOff + 36), secStart, numSections);
        int funcsOff = RvaToFile(pe, BitConverter.ToInt32(pe, exportOff + 28), secStart, numSections);
        if (namesOff < 0 || ordinalsOff < 0 || funcsOff < 0) return -1;

        for (int i = 0; i < numNames; i++)
        {
            int nameOff = RvaToFile(pe, BitConverter.ToInt32(pe, namesOff + i * 4), secStart, numSections);
            if (nameOff < 0 || !MatchAscii(pe, nameOff, funcName)) continue;

            int ordinal = BitConverter.ToUInt16(pe, ordinalsOff + i * 2);
            int funcOff = RvaToFile(pe, BitConverter.ToInt32(pe, funcsOff + ordinal * 4), secStart, numSections);
            if (funcOff < 0 || funcOff + 8 >= pe.Length) return -1;

            if (pe[funcOff] == 0x4C && pe[funcOff + 1] == 0x8B &&
                pe[funcOff + 2] == 0xD1 && pe[funcOff + 3] == 0xB8)
                return BitConverter.ToInt32(pe, funcOff + 4);

            return -1;
        }
        return -1;
    }

    private static int RvaToFile(byte[] pe, int rva, int secStart, int numSec)
    {
        for (int i = 0; i < numSec; i++)
        {
            int s = secStart + i * 40;
            if (s + 40 > pe.Length) return -1;
            int vAddr = BitConverter.ToInt32(pe, s + 12);
            int vSize = BitConverter.ToInt32(pe, s + 8);
            int raw = BitConverter.ToInt32(pe, s + 20);
            if (rva >= vAddr && rva < vAddr + vSize) return rva - vAddr + raw;
        }
        return -1;
    }

    private static bool MatchAscii(byte[] data, int offset, string target)
    {
        for (int i = 0; i < target.Length; i++)
        {
            if (offset + i >= data.Length || data[offset + i] != (byte)target[i]) return false;
        }
        return offset + target.Length < data.Length && data[offset + target.Length] == 0;
    }

    // ═══════════════════════════════════════════════════
    //  ATTACH & READ
    // ═══════════════════════════════════════════════════

    public bool Attach()
    {
        if (IsAttached) return true;
        if (_sysRead == null && !InitSyscall()) return false;

        var procs = Process.GetProcessesByName(Decode(TargetProc));
        if (procs.Length == 0) return false;

        Pid = procs[0].Id;
        Handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION, false, Pid);
        if (Handle == IntPtr.Zero) return false;

        string modName = Decode(TargetModule);
        try
        {
            foreach (ProcessModule mod in procs[0].Modules)
            {
                if (mod.ModuleName != null &&
                    mod.ModuleName.Equals(modName, StringComparison.OrdinalIgnoreCase))
                {
                    ClientBase = mod.BaseAddress;
                    break;
                }
            }
        }
        catch { /* Access denied on some modules — that's fine */ }

        if (ClientBase != IntPtr.Zero)
        {
            IsAttached = true;
            return true;
        }

        CloseHandle(Handle);
        Handle = IntPtr.Zero;
        return false;
    }

    public T Read<T>(long address) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        byte[] buf = new byte[size];
        _sysRead!(Handle, (IntPtr)address, buf, size, out _);
        GCHandle pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try { return Marshal.PtrToStructure<T>(pin.AddrOfPinnedObject()); }
        finally { pin.Free(); }
    }

    public byte ReadByte(long address)
    {
        byte[] buf = new byte[1];
        _sysRead!(Handle, (IntPtr)address, buf, 1, out _);
        return buf[0];
    }

    public string ReadString(long address, int maxLen = 64)
    {
        byte[] buf = new byte[maxLen];
        _sysRead!(Handle, (IntPtr)address, buf, maxLen, out _);
        int end = Array.IndexOf<byte>(buf, 0);
        if (end < 0) end = maxLen;
        return Encoding.UTF8.GetString(buf, 0, end);
    }

    public long ClientAddr(int offset) => ClientBase.ToInt64() + offset;

    public void Dispose()
    {
        if (_stubMem != IntPtr.Zero)
        {
            VirtualFree(_stubMem, 0, MEM_RELEASE);
            _stubMem = IntPtr.Zero;
        }
        if (Handle != IntPtr.Zero)
        {
            CloseHandle(Handle);
            Handle = IntPtr.Zero;
        }
        IsAttached = false;
    }
}

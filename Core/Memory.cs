using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FoxSense.Core;

/// <summary>
/// Direct-syscall memory engine. Reads AND writes via ntdll syscalls
/// parsed from the clean on-disk copy — bypasses all userland hooks.
/// </summary>
public sealed class Memory : IDisposable
{
    // ── kernel32 imports ──
    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualAlloc(IntPtr addr, uint size, uint type, uint protect);

    [DllImport("kernel32.dll")]
    private static extern bool VirtualFree(IntPtr addr, uint size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, int nSize, out int lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, int nSize, out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(
        IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(
        IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(
        IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize,
        IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    // ── Syscall delegates ──
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NtReadDelegate(
        IntPtr ProcessHandle, IntPtr BaseAddress,
        byte[] Buffer, int Size, out int BytesRead);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NtWriteDelegate(
        IntPtr ProcessHandle, IntPtr BaseAddress,
        byte[] Buffer, int Size, out int BytesWritten);

    private NtReadDelegate? _sysRead;
    private NtWriteDelegate? _sysWrite;
    private IntPtr _stubMem = IntPtr.Zero;

    // Process access rights
    private const int PROCESS_ALL_ACCESS = 0x1FFFFF;
    private const int PROCESS_VM_READ = 0x0010;
    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;

    // ── Stub layout: read stub (11 bytes) + write stub (11 bytes) ──
    private const int STUB_SIZE = 11;

    public IntPtr Handle { get; private set; }
    public IntPtr ClientBase { get; private set; }
    public IntPtr Engine2Base { get; private set; }
    public int Pid { get; private set; }
    public bool IsAttached { get; private set; }
    public bool HasWriteAccess { get; private set; }

    // Write jitter for stealth
    private readonly Random _jitter = new();
    private void SleepJitter() => Thread.Sleep(_jitter.Next(1, 4));

    // XOR-obfuscated strings
    private static string Decode(byte[] data)
    {
        var sb = new StringBuilder(data.Length);
        foreach (byte b in data) sb.Append((char)(b ^ 0x5A));
        return sb.ToString();
    }

    private static readonly byte[] TargetProc = { 0x39, 0x29, 0x68 };                                     // "cs2"
    private static readonly byte[] TargetModule = { 0x39, 0x36, 0x33, 0x3F, 0x34, 0x2E, 0x74, 0x3E, 0x36, 0x36 }; // "client.dll"
    private static readonly byte[] EngineModule = { 0x3F, 0x34, 0x3D, 0x33, 0x34, 0x3F, 0x68, 0x74, 0x3E, 0x36, 0x36 }; // "engine2.dll"

    // ═══════════════════════════════════════════════════
    //  SYSCALL INITIALIZATION
    // ═══════════════════════════════════════════════════

    private bool InitSyscall()
    {
        try
        {
            string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            byte[] cleanNtdll = File.ReadAllBytes(Path.Combine(sysDir, "ntdll.dll"));

            int readNum = FindSyscallNumber(cleanNtdll, "NtReadVirtualMemory");
            int writeNum = FindSyscallNumber(cleanNtdll, "NtWriteVirtualMemory");
            if (readNum < 0) return false;

            // Allocate space for both stubs
            int totalSize = STUB_SIZE * 2;
            _stubMem = VirtualAlloc(IntPtr.Zero, (uint)totalSize,
                MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (_stubMem == IntPtr.Zero) return false;

            // Build read stub
            byte[] readStub = BuildStub(readNum);
            Marshal.Copy(readStub, 0, _stubMem, readStub.Length);
            _sysRead = Marshal.GetDelegateForFunctionPointer<NtReadDelegate>(_stubMem);

            // Build write stub (if syscall number found)
            if (writeNum >= 0)
            {
                byte[] writeStub = BuildStub(writeNum);
                IntPtr writePtr = _stubMem + STUB_SIZE;
                Marshal.Copy(writeStub, 0, writePtr, writeStub.Length);
                _sysWrite = Marshal.GetDelegateForFunctionPointer<NtWriteDelegate>(writePtr);
            }

            return true;
        }
        catch { return false; }
    }

    private static byte[] BuildStub(int sysNum)
    {
        return new byte[]
        {
            0x4C, 0x8B, 0xD1,                                                       // mov r10, rcx
            0xB8, (byte)(sysNum & 0xFF), (byte)((sysNum >> 8) & 0xFF), 0x00, 0x00,  // mov eax, sysNum
            0x0F, 0x05,                                                               // syscall
            0xC3                                                                      // ret
        };
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
    //  ATTACH & CONNECTION
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Checks if the attached process is still alive.
    /// Call this periodically to detect CS2 restarts/updates.
    /// </summary>
    public bool ValidateConnection()
    {
        if (!IsAttached) return false;
        try
        {
            var proc = Process.GetProcessById(Pid);
            if (proc.HasExited) { Detach(); return false; }
            return true;
        }
        catch { Detach(); return false; }
    }

    /// <summary>
    /// Resets connection state so we can re-attach after CS2 restarts.
    /// </summary>
    public void Detach()
    {
        if (Handle != IntPtr.Zero)
        {
            CloseHandle(Handle);
            Handle = IntPtr.Zero;
        }
        ClientBase = IntPtr.Zero;
        Engine2Base = IntPtr.Zero;
        Pid = 0;
        IsAttached = false;
        HasWriteAccess = false;
    }

    public bool Attach()
    {
        // If we think we're attached, verify the process is still alive
        if (IsAttached)
        {
            if (ValidateConnection()) return true;
            // Process died — fall through to re-attach
        }

        if (_sysRead == null && !InitSyscall()) return false;

        var procs = Process.GetProcessesByName(Decode(TargetProc));
        if (procs.Length == 0) return false;

        Pid = procs[0].Id;

        // Open with PROCESS_ALL_ACCESS (matching reference project)
        Handle = OpenProcess(PROCESS_ALL_ACCESS, false, Pid);
        if (Handle == IntPtr.Zero)
        {
            // Fallback: read-only if write access denied
            Handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION, false, Pid);
            if (Handle == IntPtr.Zero) return false;
            HasWriteAccess = false;
        }
        else
        {
            // Write access available via syscall or kernel32 fallback
            HasWriteAccess = true;
        }

        string clientName = Decode(TargetModule);
        string engineName = Decode(EngineModule);
        ClientBase = IntPtr.Zero;
        Engine2Base = IntPtr.Zero;

        try
        {
            foreach (ProcessModule mod in procs[0].Modules)
            {
                if (mod.ModuleName == null) continue;
                if (mod.ModuleName.Equals(clientName, StringComparison.OrdinalIgnoreCase))
                    ClientBase = mod.BaseAddress;
                else if (mod.ModuleName.Equals(engineName, StringComparison.OrdinalIgnoreCase))
                    Engine2Base = mod.BaseAddress;
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

    // ═══════════════════════════════════════════════════
    //  READ (via NtReadVirtualMemory syscall)
    // ═══════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════
    //  WRITE (via NtWriteVirtualMemory syscall)
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Write a struct to game memory. Tries direct syscall first,
    /// falls back to kernel32 WriteProcessMemory if syscall fails.
    /// </summary>
    public bool Write<T>(long address, T value) where T : struct
    {
        if (!HasWriteAccess) return false;
        int size = Marshal.SizeOf<T>();
        byte[] buf = new byte[size];
        GCHandle pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(value, pin.AddrOfPinnedObject(), false);
        }
        finally { pin.Free(); }

        // Try syscall first
        if (_sysWrite != null)
        {
            int status = _sysWrite(Handle, (IntPtr)address, buf, size, out _);
            if (status == 0) return true;
        }

        // Fallback to kernel32 WriteProcessMemory
        return WriteProcessMemory(Handle, (IntPtr)address, buf, size, out _);
    }

    /// <summary>
    /// Write raw bytes to game memory.
    /// </summary>
    public bool WriteBytes(long address, byte[] data)
    {
        if (!HasWriteAccess) return false;
        if (_sysWrite != null)
        {
            int status = _sysWrite(Handle, (IntPtr)address, data, data.Length, out _);
            if (status == 0) return true;
        }
        return WriteProcessMemory(Handle, (IntPtr)address, data, data.Length, out _);
    }

    /// <summary>
    /// Write with randomized jitter delay for stealth.
    /// Use this for skin changer writes to defeat behavioral analysis.
    /// </summary>
    public bool WriteJittered<T>(long address, T value) where T : struct
    {
        SleepJitter();
        return Write(address, value);
    }

    // ═══════════════════════════════════════════════════
    //  FORCE FULL UPDATE (instant skin refresh)
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Forces CS2 to request a full entity snapshot from the server
    /// by setting deltaTick = -1 on the NetworkGameClient.
    /// This refreshes all weapon entities, applying skin changes instantly.
    /// Much safer than CreateRemoteThread — just a single 4-byte write.
    /// </summary>
    public bool ForceFullUpdate()
    {
        if (Engine2Base == IntPtr.Zero || !HasWriteAccess) return false;
        long netClient = Read<long>(Engine2Base.ToInt64() + SkinOffsets.dwNetworkGameClient);
        if (netClient == 0 || netClient < 0x10000) return false;
        return Write(netClient + SkinOffsets.dwNetworkGameClient_deltaTick, -1);
    }

    // ═══════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════

    public long ClientAddr(int offset) => ClientBase.ToInt64() + offset;
    public long EngineAddr(int offset) => Engine2Base.ToInt64() + offset;

    // ═══════════════════════════════════════════════════
    //  REMOTE MEMORY ALLOCATION (in game process)
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Allocate memory in the target process (matching reference's mem.Allocate).
    /// </summary>
    public long AllocateRemote(uint size = 0x1000)
    {
        IntPtr addr = VirtualAllocEx(Handle, IntPtr.Zero, size,
            MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        return addr.ToInt64();
    }

    /// <summary>
    /// Free remote memory (matching reference's mem.Free).
    /// </summary>
    public bool FreeRemote(long address)
    {
        return VirtualFreeEx(Handle, (IntPtr)address, 0, MEM_RELEASE);
    }

    // ═══════════════════════════════════════════════════
    //  REMOTE THREAD EXECUTION
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Call a function in the target process via CreateRemoteThread.
    /// Matching reference's mem.CallThread(funcAddress).
    /// </summary>
    public bool CallThread(long funcAddress)
    {
        if (funcAddress == 0) return false;
        IntPtr hThread = CreateRemoteThread(Handle, IntPtr.Zero, 0,
            (IntPtr)funcAddress, IntPtr.Zero, 0, out _);
        if (hThread == IntPtr.Zero) return false;
        WaitForSingleObject(hThread, 5000);
        CloseHandle(hThread);
        return true;
    }

    // ═══════════════════════════════════════════════════
    //  SIGNATURE SCANNING
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Read raw bytes from game memory.
    /// </summary>
    public byte[] ReadBytes(long address, int size)
    {
        byte[] buf = new byte[size];
        _sysRead?.Invoke(Handle, (IntPtr)address, buf, size, out _);
        return buf;
    }

    /// <summary>
    /// Scan a module for a byte pattern (matching reference's mem.SigScan).
    /// Pattern format: "48 83 EC ?? E8 ?? ?? ?? ??" where ?? is wildcard.
    /// </summary>
    public long SigScan(IntPtr moduleBase, int moduleSize, string pattern)
    {
        if (moduleBase == IntPtr.Zero || moduleSize <= 0) return 0;

        // Parse pattern
        var parts = pattern.Split(' ');
        var bytes = new (byte val, bool wild)[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "?" || parts[i] == "??")
                bytes[i] = (0, true);
            else
                bytes[i] = (Convert.ToByte(parts[i], 16), false);
        }

        // Read module in chunks to avoid huge allocations
        int chunkSize = 0x100000; // 1MB chunks
        long baseAddr = moduleBase.ToInt64();

        for (int offset = 0; offset < moduleSize - bytes.Length; offset += chunkSize - bytes.Length)
        {
            int readSize = Math.Min(chunkSize, moduleSize - offset);
            byte[] buffer = ReadBytes(baseAddr + offset, readSize);

            for (int i = 0; i < readSize - bytes.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < bytes.Length; j++)
                {
                    if (!bytes[j].wild && buffer[i + j] != bytes[j].val)
                    {
                        found = false;
                        break;
                    }
                }
                if (found) return baseAddr + offset + i;
            }
        }
        return 0;
    }

    /// <summary>
    /// Get module size for sig scanning.
    /// </summary>
    public int GetModuleSize(string moduleName)
    {
        try
        {
            var procs = Process.GetProcessById(Pid);
            foreach (ProcessModule mod in procs.Modules)
            {
                if (mod.ModuleName != null &&
                    mod.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                    return mod.ModuleMemorySize;
            }
        }
        catch { }
        return 0;
    }

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
        HasWriteAccess = false;
    }

}

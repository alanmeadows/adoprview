using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AdoPrView;

/// <summary>A detached, exact-run process owner. Only this process holds the kill-on-close job handle.</summary>
internal static class NativeFixSupervisor
{
    internal sealed record Specification(string RunId, string Token, string SessionId, string RunDirectory,
        string SourceDirectory, string CommonDirectory, string Worktree, string Branch, string Commit,
        string Git, string Cli, string Remote, string FetchUrl, string[] PushUrls, bool PushToSource);
    internal sealed record Receipt(string RunId, string Token, string SessionId, int SupervisorPid,
        long SupervisorStartedTicks, int? ExitCode, string? Error, bool DescendantsStopped);

    internal static string JobName(Specification s) => @"Local\AdoPrView-Fix-" + s.RunId + "-" + s.Token;
    internal static string FilePath(Specification s, string name) => Path.Combine(s.RunDirectory, name);

    internal static void SafePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("Native run paths must be absolute.");
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Native run ownership cannot be verified through a reparse point.");
    }

    internal static void Verify(Specification s)
    {
        if (!Guid.TryParseExact(s.RunId, "N", out _) || !Guid.TryParseExact(s.SessionId, "D", out _) ||
            s.Token.Length != 64 || s.Token.Any(c => !Uri.IsHexDigit(c)) ||
            Path.GetFileName(s.RunDirectory) != s.RunId ||
            Path.GetFileName(Path.GetDirectoryName(s.RunDirectory)) != "native" ||
            !Path.GetFullPath(s.Worktree).Equals(Path.Combine(s.RunDirectory, "worktree"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The native run ownership record is invalid; work is preserved.");
        SafePath(s.RunDirectory);
        var recorded = Read<Specification>(FilePath(s, "ownership.json"));
        if (JsonSerializer.Serialize(recorded) != JsonSerializer.Serialize(s))
            throw new InvalidOperationException("The native run ownership receipt changed; work is preserved.");
    }

    internal static T Read<T>(string path)
    {
        SafePath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > 1024 * 1024) throw new IOException("Native receipt exceeds its size limit.");
        return JsonSerializer.Deserialize<T>(file) ?? throw new IOException("Native receipt is empty.");
    }

    internal static void Write<T>(string path, T value)
    {
        SafePath(path);
        var pending = path + ".pending";
        SafePath(pending);
        using (var file = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(file, value);
            file.Flush(true);
        }
        File.Move(pending, path, true);
    }

    internal static void Launch(Specification s, string? supervisorExe = null, bool nestedSyntheticFixture = false)
    {
        Verify(s);
        var executable = supervisorExe ?? Path.Combine(AppContext.BaseDirectory, "AdoPrView.exe");
        if (!File.Exists(executable)) throw new InvalidOperationException("The native Fix supervisor executable is missing. Reinstall the complete application.");
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var command = new StringBuilder(Quote(executable) + " --native-fix-supervisor " + Quote(FilePath(s, "ownership.json")));
        // Never fall back to inheriting a host's lifetime job: that would falsely promise detached execution.
        if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false,
            0x00000008 | 0x00000200 | (nestedSyntheticFixture ? 0u : 0x01000000u), IntPtr.Zero, s.RunDirectory, ref startup, out var process))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Native supervisor launch failed (the host may prohibit job breakaway).");
        CloseHandle(process.Thread);
        CloseHandle(process.Process);
    }

    internal static Receipt? Status(Specification s)
    {
        Verify(s);
        var path = FilePath(s, "receipt.json");
        if (!File.Exists(path)) return null;
        var result = Read<Receipt>(path);
        if (result.RunId != s.RunId || result.Token != s.Token || result.SessionId != s.SessionId)
            throw new InvalidOperationException("The native supervisor receipt belongs to a different run.");
        if (result.SupervisorPid <= 0 || result.SupervisorStartedTicks <= 0)
            throw new InvalidOperationException("The native supervisor identity receipt is invalid.");
        return result;
    }

    internal static bool SupervisorAlive(Receipt r)
    {
        try
        {
            using var process = Process.GetProcessById(r.SupervisorPid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == r.SupervisorStartedTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    internal static uint? ActiveProcesses(Specification s)
    {
        Verify(s);
        using var job = OpenJobObject(0x0004, false, JobName(s));
        if (job.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 2) return null;
            throw new Win32Exception(error, "The exact owned native job could not be inspected.");
        }
        return ActiveProcesses(job);
    }

    static uint ActiveProcesses(SafeFileHandle job)
    {
        var accounting = new BasicAccounting();
        if (!QueryInformationJobObject(job, 1, ref accounting, Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return accounting.ActiveProcesses;
    }

    internal static void RequestCancel(Specification s)
    {
        Verify(s);
        Write(FilePath(s, "cancel.json"), new[] { s.RunId, s.Token, s.SessionId });
    }

    static bool CancelRequested(Specification s)
    {
        var durableCancel = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(s.RunDirectory))!, s.RunId + ".cancelled");
        SafePath(durableCancel);
        if (File.Exists(durableCancel)) return true;
        if (!File.Exists(FilePath(s, "cancel.json"))) return false;
        var request = Read<string[]>(FilePath(s, "cancel.json"));
        if (!request.SequenceEqual(new[] { s.RunId, s.Token, s.SessionId }))
            throw new InvalidOperationException("Cancellation does not match this owned native run.");
        return true;
    }

    internal static string[] Arguments(Specification s) =>
        ["--no-auto-update", "--no-color", "--no-ask-user", "--yolo", "--stream", "on", "--session-id", s.SessionId,
         "--deny-tool", "shell(gh pr merge)", "--deny-tool", "shell(az repos pr update)",
         .. s.PushToSource ? Array.Empty<string>() : new[] { "--deny-tool", "shell(git push)" }];

    internal static int Run(string specificationPath)
    {
        Specification? s = null;
        Receipt? receipt = null;
        SafeFileHandle? job = null;
        ProcessInfo child = default;
        try
        {
            s = Read<Specification>(specificationPath);
            Verify(s);
            SafePath(FilePath(s, "supervisor.lock"));
            using var lease = new FileStream(FilePath(s, "supervisor.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            // An existing receipt is never replayed into a new prompt/session.
            if (File.Exists(FilePath(s, "receipt.json"))) return 78;
            using var self = Process.GetCurrentProcess();
            receipt = new(s.RunId, s.Token, s.SessionId, self.Id, self.StartTime.ToUniversalTime().Ticks, null, null, false);
            Write(FilePath(s, "receipt.json"), receipt);
            SafePath(s.Worktree);
            job = CreateJobObject(IntPtr.Zero, JobName(s));
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (Marshal.GetLastWin32Error() == 183) throw new InvalidOperationException("An exact-run native job already exists; no duplicate was launched.");
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x00002000 } };
            if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (CancelRequested(s))
            {
                Write(FilePath(s, "receipt.json"), receipt with { ExitCode = 143, DescendantsStopped = true });
                return 143;
            }
            using var input = InheritableFile(FilePath(s, "prompt"), false);
            using var output = InheritableFile(FilePath(s, "stdout.log"), true);
            using var error = InheritableFile(FilePath(s, "stderr.log"), true);
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 0x00000100,
                StdInput = input.DangerousGetHandle(), StdOutput = output.DangerousGetHandle(), StdError = error.DangerousGetHandle() };
            var command = new StringBuilder(Quote(s.Cli) + " " + string.Join(" ", Arguments(s).Select(Quote)));
            var environment = ExplainModel.ChildEnvironment();
            environment["COPILOT_ASSISTED_APPROVAL"] = "false";
            environment["NO_COLOR"] = "1";
            var block = string.Join('\0', environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value)) + "\0\0";
            var pointer = Marshal.StringToHGlobalUni(block);
            try
            {
                if (!CreateProcess(s.Cli, command, IntPtr.Zero, IntPtr.Zero, true,
                    0x00000004 | 0x08000000 | 0x00000400, pointer, s.Worktree, ref startup, out child))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(pointer); }
            // The CLI cannot spawn even one descendant before assignment to the owned kill-on-close job.
            if (!AssignProcessToJobObject(job, child.Process))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (ResumeThread(child.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(child.Thread); child.Thread = IntPtr.Zero;
            var cancelled = false;
            while (WaitForSingleObject(child.Process, 100) == 258)
            {
                if (CancelRequested(s))
                {
                    cancelled = true;
                    if (!TerminateJobObject(job, 143)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    break;
                }
            }
            if (WaitForSingleObject(child.Process, 10_000) != 0) throw new IOException("The native CLI stop could not be confirmed.");
            if (!GetExitCodeProcess(child.Process, out var code)) throw new Win32Exception(Marshal.GetLastWin32Error());
            // Completion also retires background children; they may not outlive the exact Fix run.
            if (ActiveProcesses(job) != 0 && !TerminateJobObject(job, cancelled ? 143u : code))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var timeout = Stopwatch.StartNew();
            while (ActiveProcesses(job) != 0 && timeout.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(50);
            if (ActiveProcesses(job) != 0) throw new IOException("Native descendants are still active; stop is unconfirmed.");
            Write(FilePath(s, "receipt.json"), receipt with { ExitCode = cancelled ? 143 : unchecked((int)code), DescendantsStopped = true });
            return cancelled ? 143 : unchecked((int)code);
        }
        catch (Exception ex)
        {
            // A suspended child that could not be assigned must not be leaked.
            if (child.Process != IntPtr.Zero) TerminateProcess(child.Process, 1);
            if (job is not null && !job.IsInvalid) TerminateJobObject(job, 1);
            if (s is not null && receipt is not null)
            {
                try
                {
                    var stopped = job is null || job.IsInvalid || ActiveProcesses(job) == 0;
                    Write(FilePath(s, "receipt.json"), receipt with { ExitCode = 1, DescendantsStopped = stopped,
                        Error = ex is Win32Exception w ? $"Native process supervision failed (Windows error {w.NativeErrorCode})." :
                            "Native process supervision failed; inspect the retained run and its logs." });
                }
                catch { }
            }
            return 1;
        }
        finally
        {
            if (child.Thread != IntPtr.Zero) CloseHandle(child.Thread);
            if (child.Process != IntPtr.Zero) CloseHandle(child.Process);
            job?.Dispose();
            if (s is not null && receipt is not null)
                try { SafePath(FilePath(s, "prompt")); File.Delete(FilePath(s, "prompt")); } catch { }
        }
    }

    static SafeFileHandle InheritableFile(string path, bool output)
    {
        SafePath(path);
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
        var file = CreateFile(path, output ? 0x40000000u : 0x80000000u, 1 | 2 | 4, ref security,
            output ? 2u : 3u, 0x80, IntPtr.Zero);
        if (file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return file;
    }

    internal static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public short Show, ReservedSize; public IntPtr ReservedPointer, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimits
    {
        public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct BasicAccounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, ref SecurityAttributes security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", EntryPoint = "OpenJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle OpenJobObject(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, ref BasicAccounting accounting, int size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll")]
    static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);
}

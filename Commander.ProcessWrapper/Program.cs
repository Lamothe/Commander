// Commander.ProcessWrapper
//
// Supervises a command on behalf of Commander:
//  - puts itself, and everything it starts, into a fresh process group
//  - forwards termination signals (SIGTERM/SIGINT/SIGHUP) to that whole group
//  - waits for the command and exits with its exit code
//
// Commander signals the process it started, so forwarding to the group is what
// lets commands with nested processes (dotnet watch -> build -> app) stop
// cleanly instead of leaving orphaned children behind.
//
// On Windows there is no fork/exec or POSIX signals; the same contract is
// implemented with a Job Object (see RunWindows below).

using System.Diagnostics;
using System.Runtime.InteropServices;

const int JobObjectExtendedLimitInformation = 9;
const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

if (args.Length == 0) return 1;

if (OperatingSystem.IsWindows())
{
    RunWindows(args);
    return;
}

// Linux signal numbers. The PosixSignal enum is a cross-platform abstraction
// whose values are not system signal numbers, so the forwards below use these.
const int SigHup = 1;
const int SigInt = 2;
const int SigTerm = 15;

// Give the command its own process group so the wrapper (the group leader)
// can forward signals to every process in the tree with kill(-pgid, sig).
_ = setpgid(0, 0);

// 1 = PR_SET_PDEATHSIG, 15 = SIGTERM: receive SIGTERM when Commander exits.
_ = prctl(1, 15, 0, 0, 0);

var child = new Process
{
    StartInfo = new ProcessStartInfo
    {
        FileName = args[0]
    }
};

for (var i = 1; i < args.Length; i++)
{
    child.StartInfo.ArgumentList.Add(args[i]);
}

// Forward termination signals to the whole group. The handler stays active
// while the command shuts down so the child's exit is still reported.
var forwarding = 0;
var registrations = new List<PosixSignalRegistration>();

foreach (var (signal, number) in new[] { (PosixSignal.SIGTERM, SigTerm), (PosixSignal.SIGINT, SigInt), (PosixSignal.SIGHUP, SigHup) })
{
    registrations.Add(PosixSignalRegistration.Create(signal, context =>
    {
        context.Cancel = true;

        // The forwarded copy of the signal also reaches this process; ignore
        // it so the wrapper can wait for a clean child shutdown.
        if (Interlocked.Exchange(ref forwarding, 1) != 0)
        {
            return;
        }

        _ = kill(-getpgrp(), number);
    }));
}

try
{
    child.Start();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to start {args[0]}: {ex.Message}");
    return 127;
}

child.WaitForExit();

// The registrations must stay reachable for the lifetime of the wrapper.
GC.KeepAlive(registrations);
return child.ExitCode;

[DllImport("libc.so.6", SetLastError = true)]
static extern int setpgid(int pid, int pgid);

static void RunWindows(string[] args)
{
    // Windows has no fork/exec or POSIX signals. Replicate the wrapper's
    // contract using a Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE:
    //   - The real executable is launched inside the job.
    //   - When this wrapper exits for ANY reason (normal, killed by the app,
    //     crashed), the job handle closes and the entire child tree dies.
    //     This is the Windows equivalent of prctl(PR_SET_PDEATHSIG).
    //   - The wrapper stays alive and forwards the child's exit code so the
    //     app sees the same exit-code semantics as the Linux execvp path.
    var job = CreateJobObjectW(IntPtr.Zero, null);
    if (job == IntPtr.Zero)
    {
        Console.Error.WriteLine($"CreateJobObjectW failed. Error code: {Marshal.GetLastWin32Error()}");
        Environment.Exit(1);
    }

    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        }
    };

    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info,
            (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
    {
        Console.Error.WriteLine($"SetInformationJobObject failed. Error code: {Marshal.GetLastWin32Error()}");
        CloseHandle(job);
        Environment.Exit(1);
    }

    var psi = new ProcessStartInfo
    {
        FileName = args[0],
        UseShellExecute = false
    };
    for (var i = 1; i < args.Length; i++)
    {
        psi.ArgumentList.Add(args[i]);
    }

    // The child inherits this process's standard handles, so its stdout/stderr
    // flow into the redirected pipes the app reads.
    using var process = StartChild(psi);
    if (process == null)
    {
        Console.Error.WriteLine("Failed to start process.");
        CloseHandle(job);
        Environment.Exit(1);
    }

    if (!AssignProcessToJobObject(job, process.Handle))
    {
        Console.Error.WriteLine($"AssignProcessToJobObject failed. Error code: {Marshal.GetLastWin32Error()}");
    }

    process.WaitForExit();
    CloseHandle(job);
    Environment.Exit(process.ExitCode);
}

static Process? StartChild(ProcessStartInfo psi)
{
    try
    {
        return Process.Start(psi);
    }
    catch (Exception ex)
    {
        // execvp never returns on failure either — it reports the error and the
        // process ends. Mirror that: report to stderr and exit non-zero.
        Console.Error.WriteLine($"Failed to start process: {ex.Message}");
        return null;
    }
}

[DllImport("libc.so.6", SetLastError = true)]
static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

[DllImport("libc.so.6", SetLastError = true)]
static extern int getpgrp();

[DllImport("libc.so.6", SetLastError = true)]
static extern int kill(int pid, int sig);

[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass,
    ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool CloseHandle(IntPtr hObject);

[StructLayout(LayoutKind.Sequential)]
struct JOBOBJECT_BASIC_LIMIT_INFORMATION
{
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public UIntPtr MinimumWorkingSetSize;
    public UIntPtr MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public UIntPtr Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
}

[StructLayout(LayoutKind.Sequential)]
struct IO_COUNTERS
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
{
    public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
    public IO_COUNTERS IoInfo;
    public UIntPtr ProcessMemoryLimit;
    public UIntPtr JobMemoryLimit;
    public UIntPtr PeakProcessMemoryUsed;
    public UIntPtr PeakJobMemoryUsed;
}

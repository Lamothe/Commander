using System.Diagnostics;
using System.Runtime.InteropServices;

const int JobObjectExtendedLimitInformation = 9;
const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

if (args.Length == 0) return;

if (OperatingSystem.IsWindows())
{
    RunWindows(args);
    return;
}

// 1 = PR_SET_PDEATHSIG, 9 = SIGKILL
_ = prctl(1, 15, 0, 0, 0);

// C expects a NULL-terminated array. C# arrays don't have this.
var execvpArgs = new string[args.Length + 1];
Array.Copy(args, execvpArgs, args.Length);
execvpArgs[args.Length] = null!;

// Replace this .NET process with the target application
_ = execvp(args[0], execvpArgs);

var errorCode = Marshal.GetLastPInvokeError();

// IMPORTANT: execvp only returns if it FAILS. 
// If it succeeds, the process is replaced and this line never runs.
Console.Error.WriteLine($"execvp failed. Error code: {errorCode}");

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
static extern int execvp(string file, string[] args);

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

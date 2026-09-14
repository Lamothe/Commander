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

using System.Diagnostics;
using System.Runtime.InteropServices;

if (args.Length == 0) return 1;

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

[DllImport("libc.so.6", SetLastError = true)]
static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

[DllImport("libc.so.6", SetLastError = true)]
static extern int getpgrp();

[DllImport("libc.so.6", SetLastError = true)]
static extern int kill(int pid, int sig);

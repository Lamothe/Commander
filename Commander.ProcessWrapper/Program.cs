using System.Runtime.InteropServices;

if (args.Length == 0) return;

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

[DllImport("libc.so.6", SetLastError = true)]
static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

[DllImport("libc.so.6", SetLastError = true)]
static extern int execvp(string file, string[] args);
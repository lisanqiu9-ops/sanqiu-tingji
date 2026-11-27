using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SanqiuTingji;

/// Launches local CLI tools on a hidden Win32 desktop (`CreateDesktopW`) with redirected UTF-8
/// pipes and a kill-on-close Job Object, so neither the CLI nor any console grandchild it spawns
/// (such as background updaters, node/cmd wrappers, or conhost) can ever flash a black window on
/// the user's active desktop.
internal static class SilentProcess
{
    private const string HiddenDesktopName = "SanqiuNotesSilentCli";
    private const uint DesktopAccess = 0x000F01FF; // STANDARD_RIGHTS_REQUIRED | READ/WRITE/CREATEWINDOW/CREATEPROCESS
    private const uint HandleFlagInherit = 0x00000001;
    private const uint StartfUseShowWindow = 0x00000001;
    private const uint StartfUseStdHandles = 0x00000100;
    private const short SwHide = 0;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private static readonly object DesktopGate = new();
    private static IntPtr hiddenDesktop;
    private static bool desktopAttempted;

    internal sealed record RunResult(int ExitCode, string Stdout, string Stderr);

    private static string? EnsureHiddenDesktop()
    {
        lock (DesktopGate)
        {
            if (hiddenDesktop != IntPtr.Zero) return HiddenDesktopName;
            if (desktopAttempted) return null;
            desktopAttempted = true;
            hiddenDesktop = CreateDesktopW(HiddenDesktopName, null, IntPtr.Zero, 0, DesktopAccess, IntPtr.Zero);
            if (hiddenDesktop == IntPtr.Zero)
                Log.Debug($"silent-proc: CreateDesktopW failed ({Marshal.GetLastWin32Error()}), falling back");
            return hiddenDesktop != IntPtr.Zero ? HiddenDesktopName : null;
        }
    }

    internal static async Task<RunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? stdinText,
        IReadOnlyDictionary<string, string>? extraEnv,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var desktop = EnsureHiddenDesktop();
        if (desktop == null)
            return await RunFallbackAsync(fileName, arguments, stdinText, extraEnv, timeout, ct);

        SafeFileHandle? stdinRead = null, stdinWrite = null;
        SafeFileHandle? stdoutRead = null, stdoutWrite = null;
        SafeFileHandle? stderrRead = null, stderrWrite = null;
        IntPtr job = IntPtr.Zero;
        IntPtr procHandle = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;

        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
            if (!CreatePipe(out stdinRead, out stdinWrite, ref sa, 0) ||
                !CreatePipe(out stdoutRead, out stdoutWrite, ref sa, 0) ||
                !CreatePipe(out stderrRead, out stderrWrite, ref sa, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "创建管道失败");

            // Parent ends must NOT be inherited, otherwise EOF is never observed.
            SetHandleInformation(stdinWrite, HandleFlagInherit, 0);
            SetHandleInformation(stdoutRead, HandleFlagInherit, 0);
            SetHandleInformation(stderrRead, HandleFlagInherit, 0);

            var commandLine = BuildCommandLine(fileName, arguments);
            var envChars = BuildEnvironmentBlock(extraEnv);
            var envPin = GCHandle.Alloc(envChars, GCHandleType.Pinned);

            var si = new STARTUPINFOW
            {
                cb = Marshal.SizeOf<STARTUPINFOW>(),
                lpDesktop = desktop,
                dwFlags = StartfUseStdHandles | StartfUseShowWindow,
                wShowWindow = SwHide,
                hStdInput = stdinRead.DangerousGetHandle(),
                hStdOutput = stdoutWrite.DangerousGetHandle(),
                hStdError = stderrWrite.DangerousGetHandle(),
            };

            var flags = CreateNoWindow | CreateUnicodeEnvironment;
            bool ok;
            PROCESS_INFORMATION pi;
            try
            {
                ok = CreateProcessW(
                    null,
                    new StringBuilder(commandLine),
                    IntPtr.Zero,
                    IntPtr.Zero,
                    true,
                    flags,
                    envPin.AddrOfPinnedObject(),
                    null,
                    ref si,
                    out pi);
            }
            finally
            {
                envPin.Free();
            }

            if (!ok)
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"无法在静默桌面启动进程：{fileName} (Win32={err})");
            }

            procHandle = pi.hProcess;
            threadHandle = pi.hThread;

            // Attach a kill-on-close Job Object so cancellation or timeout cleans up the whole tree.
            job = CreateKillOnCloseJob();
            if (job != IntPtr.Zero) AssignProcessToJobObject(job, procHandle);

            // Close child ends in parent immediately.
            stdinRead.Dispose(); stdinRead = null;
            stdoutWrite.Dispose(); stdoutWrite = null;
            stderrWrite.Dispose(); stderrWrite = null;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);

            var utf8 = new UTF8Encoding(false);
            using var outStream = new FileStream(stdoutRead, FileAccess.Read, 4096, isAsync: false);
            using var errStream = new FileStream(stderrRead, FileAccess.Read, 4096, isAsync: false);
            using var outReader = new StreamReader(outStream, utf8);
            using var errReader = new StreamReader(errStream, utf8);

            var stdoutTask = Task.Run(() => outReader.ReadToEnd());
            var stderrTask = Task.Run(() => errReader.ReadToEnd());

            using (var inStream = new FileStream(stdinWrite, FileAccess.Write, 4096, isAsync: false))
            {
                if (!string.IsNullOrEmpty(stdinText))
                {
                    var bytes = utf8.GetBytes(stdinText);
                    await inStream.WriteAsync(bytes, linked.Token);
                    await inStream.FlushAsync(linked.Token);
                }
            }
            stdinWrite = null;

            using var proc = Process.GetProcessById((int)pi.dwProcessId);
            try
            {
                await proc.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                KillTree(job, proc);
                throw new TimeoutException($"本地 CLI 响应超时（>{(int)timeout.TotalSeconds}s）。");
            }
            catch (OperationCanceledException)
            {
                KillTree(job, proc);
                throw;
            }

            GetExitCodeProcess(procHandle, out var exitCode);
            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();
            return new RunResult((int)exitCode, stdout, stderr);
        }
        finally
        {
            stdinRead?.Dispose();
            stdinWrite?.Dispose();
            stdoutRead?.Dispose();
            stdoutWrite?.Dispose();
            stderrRead?.Dispose();
            stderrWrite?.Dispose();
            if (job != IntPtr.Zero) CloseHandle(job);
            if (threadHandle != IntPtr.Zero) CloseHandle(threadHandle);
            if (procHandle != IntPtr.Zero) CloseHandle(procHandle);
        }
    }

    private static void KillTree(IntPtr job, Process proc)
    {
        try
        {
            if (job != IntPtr.Zero) TerminateJobObject(job, 1);
            else if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static async Task<RunResult> RunFallbackAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? stdinText,
        IReadOnlyDictionary<string, string>? extraEnv,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (extraEnv != null)
            foreach (var (k, v) in extraEnv) psi.Environment[k] = v;
        if (IsCmdExe(fileName) && TryBuildCmdCommandLine(fileName, arguments, out var cmdLine))
        {
            var exePrefix = QuoteArg(fileName);
            psi.Arguments = cmdLine.Length > exePrefix.Length ? cmdLine[exePrefix.Length..].TrimStart() : "";
        }
        else
        {
            foreach (var a in arguments) psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动本地 CLI：{fileName}");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);
        if (!string.IsNullOrEmpty(stdinText))
        {
            await process.StandardInput.WriteAsync(stdinText.AsMemory(), linked.Token);
            await process.StandardInput.FlushAsync(linked.Token);
        }
        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"本地 CLI 响应超时（>{(int)timeout.TotalSeconds}s）。");
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return new RunResult(process.ExitCode, (await stdoutTask).Trim(), (await stderrTask).Trim());
    }

    internal static string BuildCommandLine(string fileName, IReadOnlyList<string> arguments)
    {
        // cmd.exe /c strips a single outer quote pair when the inner command contains quoted paths/args.
        // Using `/d /s /c "..."` makes cmd.exe reliably preserve spaces in both the batch path and arguments.
        if (IsCmdExe(fileName) && TryBuildCmdCommandLine(fileName, arguments, out var cmdLine))
            return cmdLine;

        var sb = new StringBuilder();
        sb.Append(QuoteArg(fileName));
        foreach (var arg in arguments)
        {
            sb.Append(' ');
            sb.Append(QuoteArg(arg));
        }
        return sb.ToString();
    }

    private static bool IsCmdExe(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return string.Equals(name, "cmd.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "cmd", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryBuildCmdCommandLine(string fileName, IReadOnlyList<string> arguments, out string commandLine)
    {
        var cIdx = -1;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (string.Equals(arguments[i], "/c", StringComparison.OrdinalIgnoreCase)) { cIdx = i; break; }
        }
        if (cIdx < 0 || cIdx + 1 >= arguments.Count)
        {
            commandLine = "";
            return false;
        }
        var prefix = new StringBuilder();
        prefix.Append(QuoteArg(fileName));
        var hasS = false;
        for (var i = 0; i < cIdx; i++)
        {
            if (string.Equals(arguments[i], "/s", StringComparison.OrdinalIgnoreCase)) hasS = true;
            prefix.Append(' ').Append(arguments[i]);
        }
        if (!hasS) prefix.Append(" /s");
        prefix.Append(" /c \"");
        for (var i = cIdx + 1; i < arguments.Count; i++)
        {
            if (i > cIdx + 1) prefix.Append(' ');
            prefix.Append(QuoteArg(arguments[i]));
        }
        prefix.Append('"');
        commandLine = prefix.ToString();
        return true;
    }

    /// Standard Windows CommandLineToArgvW-compatible argument quoting.
    internal static string QuoteArg(string arg)
    {
        if (arg.Length == 0) return "\"\"";
        var needsQuotes = arg.Any(c => char.IsWhiteSpace(c) || c == '"');
        if (!needsQuotes) return arg;
        var sb = new StringBuilder(arg.Length + 8);
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
            }
            else
            {
                if (backslashes > 0) { sb.Append('\\', backslashes); backslashes = 0; }
                sb.Append(c);
            }
        }
        if (backslashes > 0) sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    private static char[] BuildEnvironmentBlock(IReadOnlyDictionary<string, string>? extraEnv)
    {
        var dict = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string k && entry.Value is string v) dict[k] = v;
        }
        if (extraEnv != null)
            foreach (var (k, v) in extraEnv) dict[k] = v;

        var sb = new StringBuilder();
        foreach (var (k, v) in dict)
            sb.Append(k).Append('=').Append(v).Append('\0');
        sb.Append('\0');
        return sb.ToString().ToCharArray();
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktopW(string lpszDesktop, string? lpszDevice, IntPtr pDevmode, uint dwFlags, uint dwDesiredAccess, IntPtr lpsa);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, uint infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}

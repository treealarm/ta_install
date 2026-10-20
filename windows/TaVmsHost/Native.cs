using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TaVmsHost;

[SupportedOSPlatform("windows")]
internal static class Native
{
  // Puts the host into a job that kills every process in it when the last handle to the job
  // closes. The host holds that handle and never closes it, so it closes when the host dies — by a
  // crash or by taskkill — and every child goes with it instead of sitting on its ports. Children
  // join the job by inheritance, so there is no window between starting one and assigning it.
  public static void KillChildrenWithHost()
  {
    var job = CreateJobObjectW(IntPtr.Zero, null);
    if (job == IntPtr.Zero) throw new Win32Exception();

    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
      BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
    };
    var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
    var ptr = Marshal.AllocHGlobal(size);
    try
    {
      Marshal.StructureToPtr(info, ptr, false);
      if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
        throw new Win32Exception();
    }
    finally
    {
      Marshal.FreeHGlobal(ptr);
    }

    if (!AssignProcessToJobObject(job, GetCurrentProcess()))
      throw new Win32Exception();
  }

  // Runs in a short-lived helper process (`TaVmsHost send-ctrl-c <pid>`), never in the host: it has
  // to leave its own console to join the target's, and the event reaches every process on that
  // console — the helper included, which is why it ignores Ctrl+C first. The target was started
  // with a hidden console of its own (CreateNoWindow), so nobody else is on it.
  public static void SendCtrlC(int pid)
  {
    FreeConsole();
    if (!AttachConsole((uint)pid)) throw new Win32Exception();
    try
    {
      if (!SetConsoleCtrlHandler(IntPtr.Zero, true)) throw new Win32Exception();
      if (!GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0)) throw new Win32Exception();
    }
    finally
    {
      FreeConsole();
    }
  }

  private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
  private const int JobObjectExtendedLimitInformation = 9;
  private const uint CTRL_C_EVENT = 0;

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
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
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

  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfo);

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

  [DllImport("kernel32.dll")]
  private static extern IntPtr GetCurrentProcess();

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern bool AttachConsole(uint dwProcessId);

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern bool FreeConsole();

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);
}

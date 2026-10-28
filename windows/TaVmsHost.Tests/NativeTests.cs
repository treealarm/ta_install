using System.Net;
using System.Net.Sockets;

namespace TaVmsHost.Tests;

public class NativeTests
{
  // The listener table read through iphlpapi, checked against a socket this process opens: the row
  // layouts and the port's byte order are easy to get wrong and would fail silently.
  [Fact]
  public void Finds_the_process_listening_on_a_port_of_either_family()
  {
    if (!OperatingSystem.IsWindows()) return;

    foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
    {
      var listener = new TcpListener(address, 0);
      listener.Start();
      try
      {
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.Contains(Environment.ProcessId, Native.TcpListenerPids(port));
      }
      finally
      {
        listener.Stop();
      }
    }
  }

  // kc.bat's case: the wrapper dies, its child lives on. Closing the wrapper's job takes the child.
  [Fact]
  public void Closing_a_childs_job_kills_what_it_left_behind()
  {
    if (!OperatingSystem.IsWindows()) return;

    var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul")
    {
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    var startedAfter = DateTime.Now.AddSeconds(-1);
    using var wrapper = System.Diagnostics.Process.Start(psi)!;
    var job = Native.ContainProcessTree(wrapper.Handle);
    Thread.Sleep(1000);
    var child = System.Diagnostics.Process.GetProcessesByName("PING").Single(p => p.StartTime > startedAfter);
    wrapper.Kill(entireProcessTree: false);
    wrapper.WaitForExit();
    Assert.False(child.HasExited);

    // The child, and the console host a hidden console comes with.
    Assert.Contains(child.Id, Native.ProcessesIn(job));
    Native.CloseProcessTree(job);
    Assert.True(child.WaitForExit(5000));
  }

  [Fact]
  public void Finds_nobody_on_a_port_nobody_listens_on()
  {
    if (!OperatingSystem.IsWindows()) return;

    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    Assert.Empty(Native.TcpListenerPids(port));
  }
}

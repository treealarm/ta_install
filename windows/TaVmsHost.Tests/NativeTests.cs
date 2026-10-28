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

using System.Net;
using System.Text;
using System.Text.Json;

namespace TaVmsHost.Tests;

// TaVmsHost join --core: what a node asks the core's web (ta_vms web_srv DeploymentController) and
// what it does with each answer.
public sealed class FetchPackageTests : IDisposable
{
  private readonly string _dir = Directory.CreateTempSubdirectory("tavms-fetch").FullName;
  private string Package => Path.Combine(_dir, "node-yard.tar.gz");

  public void Dispose() => Directory.Delete(_dir, recursive: true);

  private sealed class Core(bool distributed, bool open, HttpStatusCode join, string joinBody) : HttpMessageHandler
  {
    public List<(string Method, string Path, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
      Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body));
      return request.RequestUri.AbsolutePath switch
      {
        "/api/deployment" => new HttpResponseMessage(HttpStatusCode.OK)
        {
          Content = new StringContent(
            $$"""{"product":"TreeAlarm VMS","distributed":{{(distributed ? "true" : "false")}},"coreAddress":"192.168.1.10","enrollmentOpen":{{(open ? "true" : "false")}},"machine":"CORE"}""",
            Encoding.UTF8, "application/json"),
        },
        "/api/deployment/join" => new HttpResponseMessage(join) { Content = new StringContent(joinBody) },
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
      };
    }
  }

  [Fact]
  public async Task While_enrollment_is_open_the_node_asks_with_its_name_and_address_and_keeps_the_package()
  {
    var core = new Core(distributed: true, open: true, HttpStatusCode.OK, "PACKAGE");
    await Distributed.FetchPackageAsync("192.168.1.10:5134", "yard", "192.168.1.20", mediaMtx: false, null, null, Package, core);

    Assert.Equal("PACKAGE", File.ReadAllText(Package));
    var (method, path, body) = core.Requests[1];
    Assert.Equal(("POST", "/api/deployment/join"), (method, path));
    var sent = JsonDocument.Parse(body).RootElement;
    Assert.Equal("yard", sent.GetProperty("name").GetString());
    Assert.Equal("192.168.1.20", sent.GetProperty("address").GetString());
    Assert.False(sent.GetProperty("mediaMtx").GetBoolean());
  }

  [Fact]
  public async Task Once_it_is_closed_the_node_needs_an_administrators_password_before_it_asks()
  {
    var core = new Core(distributed: true, open: false, HttpStatusCode.OK, "PACKAGE");
    var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
      Distributed.FetchPackageAsync("192.168.1.10:5134", "yard", "192.168.1.20", true, null, null, Package, core));
    Assert.Contains("closed", error.Message);
    Assert.Single(core.Requests);

    await Distributed.FetchPackageAsync("192.168.1.10:5134", "yard", "192.168.1.20", true, "admin", "admin", Package, core);
    var sent = JsonDocument.Parse(core.Requests[^1].Body).RootElement;
    Assert.Equal("admin", sent.GetProperty("username").GetString());
    Assert.True(File.Exists(Package));
  }

  [Fact]
  public async Task A_refusal_is_reported_in_the_cores_words_and_leaves_no_file()
  {
    var core = new Core(distributed: true, open: false, HttpStatusCode.Unauthorized,
      """{"error":"Enrollment of new nodes is closed: an administrator's user name and password are needed"}""");
    var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
      Distributed.FetchPackageAsync("192.168.1.10:5134", "yard", "192.168.1.20", true, "admin", "wrong", Package, core));
    Assert.Contains("(401): Enrollment of new nodes is closed", error.Message);
    Assert.False(File.Exists(Package));
  }

  [Fact]
  public async Task A_machine_that_is_not_a_core_is_not_asked_to_enroll()
  {
    var core = new Core(distributed: false, open: true, HttpStatusCode.OK, "PACKAGE");
    var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
      Distributed.FetchPackageAsync("192.168.1.10:5134", "yard", "192.168.1.20", true, null, null, Package, core));
    Assert.Contains("not the core", error.Message);
    Assert.Single(core.Requests);
  }
}

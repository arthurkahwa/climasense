using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace ClimaSense.Monitor.Tests.Endpoints;

public class PagesTests(MonitorFactory factory) : IClassFixture<MonitorFactory>
{
    [Theory]
    [InlineData("/")]
    [InlineData("/history")]
    [InlineData("/insights")]
    public async Task Page_returns_200(string url)
        => Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync(url)).StatusCode);
}

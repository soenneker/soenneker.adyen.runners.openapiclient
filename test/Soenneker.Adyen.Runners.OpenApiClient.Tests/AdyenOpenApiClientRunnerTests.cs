using Soenneker.Tests.HostedUnit;

namespace Soenneker.Adyen.Runners.OpenApiClient.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class AdyenOpenApiClientRunnerTests : HostedUnitTest
{
    public AdyenOpenApiClientRunnerTests(Host host) : base(host)
    {
    }

    [Test]
    public void Default()
    {

    }
}

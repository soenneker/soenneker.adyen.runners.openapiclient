using Soenneker.Tests.FixturedUnit;
using Xunit;

namespace Soenneker.Adyen.Runners.OpenApiClient.Tests;

[Collection("Collection")]
public sealed class AdyenOpenApiClientRunnerTests : FixturedUnitTest
{
    public AdyenOpenApiClientRunnerTests(Fixture fixture, ITestOutputHelper output) : base(fixture, output)
    {
    }

    [Fact]
    public void Default()
    {

    }
}

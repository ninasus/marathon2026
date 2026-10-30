using System.Text.Json;

namespace APIHandler.Tests;

public class OwnerGuestSessionEndpointConfigurationTests
{
    private const string RouteTemplate = "/reservations/{reservationId}/guest-session-id";

    [Fact]
    public void DeploymentResource_ExposesRouteThroughCognitoAuthorizedApiHandler()
    {
        string resourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "deployment_resources.json"));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(resourcePath));

        JsonElement getConfiguration = document.RootElement
            .GetProperty("${api_gateway_name}")
            .GetProperty("resources")
            .GetProperty(RouteTemplate)
            .GetProperty("GET");

        Assert.Equal("cognito_authorizer", getConfiguration.GetProperty("authorization_type").GetString());
        Assert.Equal("lambda", getConfiguration.GetProperty("integration_type").GetString());
        Assert.Equal("api-handler", getConfiguration.GetProperty("lambda_name").GetString());
        Assert.True(getConfiguration.GetProperty("enable_proxy").GetBoolean());
    }
}

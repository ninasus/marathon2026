using System.Text.Json;

namespace APIHandler.Tests;

public class SubmitGroupPreOrderEndpointConfigurationTests
{
    private const string RouteTemplate = "/group-reservations/{groupReservationId}/submit";

    [Fact]
    public void DeploymentExposesCognitoAuthorizedPost()
    {
        string resourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "deployment_resources.json"));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(resourcePath));
        JsonElement post = document.RootElement
            .GetProperty("${api_gateway_name}")
            .GetProperty("resources")
            .GetProperty(RouteTemplate)
            .GetProperty("POST");

        Assert.Equal("cognito_authorizer", post.GetProperty("authorization_type").GetString());
        Assert.Equal("lambda", post.GetProperty("integration_type").GetString());
        Assert.Equal("api-handler", post.GetProperty("lambda_name").GetString());
        Assert.True(post.GetProperty("enable_proxy").GetBoolean());
    }
}

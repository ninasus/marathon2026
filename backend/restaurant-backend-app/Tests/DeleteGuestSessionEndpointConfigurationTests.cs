using System.Text.Json;

namespace APIHandler.Tests;

public class DeleteGuestSessionEndpointConfigurationTests
{
    private const string RouteTemplate = "/guest/groups/{groupReservationId}/sessions/{guestSessionId}";

    [Fact]
    public void DeploymentResource_ExposesCognitoAuthorizedDeleteThroughApiHandler()
    {
        string appRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", ".."));
        string resourcePath = Path.Combine(appRoot, "deployment_resources.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(resourcePath));

        JsonElement deleteConfiguration = document.RootElement
            .GetProperty("${api_gateway_name}")
            .GetProperty("resources")
            .GetProperty(RouteTemplate)
            .GetProperty("DELETE");

        Assert.Equal("cognito_authorizer", deleteConfiguration.GetProperty("authorization_type").GetString());
        Assert.Equal("lambda", deleteConfiguration.GetProperty("integration_type").GetString());
        Assert.Equal("api-handler", deleteConfiguration.GetProperty("lambda_name").GetString());
        Assert.True(deleteConfiguration.GetProperty("enable_proxy").GetBoolean());
    }

    [Fact]
    public void RuntimeRegistry_MapsExactDeleteRouteToDeleteGuestSessionHandler()
    {
        string appRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", ".."));
        string registryPath = Path.Combine(
            appRoot, "dnapp", "lambdas", "api-handler", "Routing", "RouteRegistry.cs");
        string registrySource = File.ReadAllText(registryPath);

        Assert.Contains(
            $"new RouteHandler(new Route(HttpMethods.DELETE, \"{RouteTemplate}\"), ActivatorUtilities.CreateInstance<DeleteGuestSessionHandler>(provider))",
            registrySource);
    }
}

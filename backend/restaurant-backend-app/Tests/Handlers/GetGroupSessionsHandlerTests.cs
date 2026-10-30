using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GuestSessions;
using APIHandler.Models.GuestSessions;
using APIHandler.Services.GuestSessions;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers;

public class GetGroupSessionsHandlerTests
{
    private readonly Mock<IGuestSessionService> _service = new();
    private readonly Mock<ILambdaContext> _context = new();
    private readonly Mock<ILambdaLogger> _logger = new();

    public GetGroupSessionsHandlerTests() => _context.Setup(context => context.Logger).Returns(_logger.Object);

    [Fact]
    public async Task HandleRequest_ReturnsExactJsonContractAndForwardsOwnerIdentity()
    {
        _service.Setup(service => service.GetAllSessionsAsync("owner-1", "group-1"))
            .ReturnsAsync(new GetGroupSessionsResponse(
                [new GroupSessionParticipant(
                    "session-1", ParticipantType.Owner, "Andrii",
                    [new GroupSessionItem(new GroupSessionDish("dish-1", "Caesar Salad", 180m), 2)],
                    360m, true)], 360m));

        var response = await Handler().HandleRequest(Request("owner-1"), _context.Object);

        Assert.Equal(200, response.StatusCode);
        using var json = JsonDocument.Parse(response.Body);
        var root = json.RootElement;
        Assert.Equal(2, root.EnumerateObject().Count());
        Assert.Equal(360m, root.GetProperty("total").GetDecimal());
        var participant = Assert.Single(root.GetProperty("participants").EnumerateArray());
        Assert.Equal(6, participant.EnumerateObject().Count());
        Assert.Equal("session-1", participant.GetProperty("id").GetString());
        Assert.Equal("Owner", participant.GetProperty("type").GetString());
        Assert.Equal("Andrii", participant.GetProperty("name").GetString());
        Assert.True(participant.GetProperty("isSubmitted").GetBoolean());
        Assert.Equal(360m, participant.GetProperty("total").GetDecimal());
        var item = Assert.Single(participant.GetProperty("items").EnumerateArray());
        Assert.Equal(2, item.EnumerateObject().Count());
        Assert.Equal(2, item.GetProperty("quantity").GetInt32());
        var dish = item.GetProperty("dish");
        Assert.Equal(3, dish.EnumerateObject().Count());
        Assert.Equal("dish-1", dish.GetProperty("id").GetString());
        Assert.Equal("Caesar Salad", dish.GetProperty("name").GetString());
        Assert.Equal(180m, dish.GetProperty("price").GetDecimal());
        _service.Verify(service => service.GetAllSessionsAsync("owner-1", "group-1"), Times.Once);
    }

    [Fact]
    public async Task HandleRequest_MissingIdentityReturnsUnauthorizedWithoutServiceCall()
    {
        var response = await Handler().HandleRequest(Request(null), _context.Object);

        Assert.Equal(401, response.StatusCode);
        _service.Verify(service => service.GetAllSessionsAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task HandleRequest_DifferentReservationOwnerReturnsForbidden()
    {
        _service.Setup(service => service.GetAllSessionsAsync("other-user", "group-1"))
            .ThrowsAsync(new ActionForbiddenException("Only the reservation owner can perform this action."));

        var response = await Handler().HandleRequest(Request("other-user"), _context.Object);

        Assert.Equal(403, response.StatusCode);
        Assert.DoesNotContain("participants", response.Body);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleRequest_MissingGroupOrReservationReturnsNotFound(bool missingGroup)
    {
        if (missingGroup)
            _service.Setup(service => service.GetAllSessionsAsync("owner-1", "group-1"))
                .ThrowsAsync(new GroupReservationNotFoundException());
        else
            _service.Setup(service => service.GetAllSessionsAsync("owner-1", "group-1"))
                .ThrowsAsync(new ReservationNotFoundException("Parent reservation not found."));

        var response = await Handler().HandleRequest(Request("owner-1"), _context.Object);

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_DeletedCatalogDishReturnsBadRequestWithoutPartialList()
    {
        _service.Setup(service => service.GetAllSessionsAsync("owner-1", "group-1"))
            .ThrowsAsync(new ValidationException("Dish 'deleted-dish' referenced by a guest session was not found."));

        var response = await Handler().HandleRequest(Request("owner-1"), _context.Object);

        Assert.Equal(400, response.StatusCode);
        Assert.DoesNotContain("participants", response.Body);
        Assert.DoesNotContain("total", response.Body);
    }

    [Fact]
    public async Task HandleRequest_MissingGroupPathReturnsBadRequestWithoutServiceCall()
    {
        var request = Request("owner-1");
        request.PathParameters = null;

        var response = await Handler().HandleRequest(request, _context.Object);

        Assert.Equal(400, response.StatusCode);
        _service.Verify(service => service.GetAllSessionsAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private GetGroupSessionsHandler Handler() => new(_service.Object);

    private static APIGatewayProxyRequest Request(string? userId) => new()
    {
        PathParameters = new Dictionary<string, string> { ["groupReservationId"] = "group-1" },
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext
            {
                Claims = userId is null ? null : new Dictionary<string, string> { ["sub"] = userId }
            }
        }
    };
}

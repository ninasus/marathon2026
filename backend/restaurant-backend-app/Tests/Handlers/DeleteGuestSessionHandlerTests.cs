using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GuestSessions;
using APIHandler.Models.GuestSessions;
using APIHandler.Services.GuestSessions;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers;

public class DeleteGuestSessionHandlerTests
{
    private readonly Mock<IGuestSessionService> _service = new();
    private readonly Mock<ILambdaContext> _context = new();
    private readonly Mock<ILambdaLogger> _logger = new();

    public DeleteGuestSessionHandlerTests() => _context.Setup(context => context.Logger).Returns(_logger.Object);

    [Fact]
    public async Task HandleRequest_ReturnsExactCamelCaseSnapshotAndForwardsOwnerAndPathIds()
    {
        _service.Setup(service => service.DeleteGuestSessionAsync("owner-1", "group-1", "guest-1"))
            .ReturnsAsync(new GetGroupSessionsResponse(
                [new GroupSessionParticipant(
                    "owner-session", ParticipantType.Owner, "Andrii",
                    [new GroupSessionItem(new GroupSessionDish("dish-1", "Caesar Salad", 180m), 2)],
                    360m, true)],
                360m));

        APIGatewayProxyResponse response = await Handler().HandleRequest(Request("owner-1"), _context.Object);

        Assert.Equal(200, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        JsonElement root = json.RootElement;
        Assert.Equal(2, root.EnumerateObject().Count());
        Assert.False(root.TryGetProperty("Participants", out _));
        Assert.Equal(360m, root.GetProperty("total").GetDecimal());
        JsonElement participant = Assert.Single(root.GetProperty("participants").EnumerateArray());
        Assert.Equal(6, participant.EnumerateObject().Count());
        Assert.Equal("owner-session", participant.GetProperty("id").GetString());
        Assert.Equal("Owner", participant.GetProperty("type").GetString());
        Assert.Equal("Andrii", participant.GetProperty("name").GetString());
        Assert.Equal(360m, participant.GetProperty("total").GetDecimal());
        Assert.True(participant.GetProperty("isSubmitted").GetBoolean());
        JsonElement item = Assert.Single(participant.GetProperty("items").EnumerateArray());
        Assert.Equal(2, item.GetProperty("quantity").GetInt32());
        JsonElement dish = item.GetProperty("dish");
        Assert.Equal("dish-1", dish.GetProperty("id").GetString());
        Assert.Equal("Caesar Salad", dish.GetProperty("name").GetString());
        Assert.Equal(180m, dish.GetProperty("price").GetDecimal());
        _service.Verify(service => service.DeleteGuestSessionAsync("owner-1", "group-1", "guest-1"), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleRequest_MissingPathParameterReturnsBadRequestWithoutServiceCall(bool omitGroup)
    {
        APIGatewayProxyRequest request = Request("owner-1");
        request.PathParameters = omitGroup
            ? new Dictionary<string, string> { ["guestSessionId"] = "guest-1" }
            : new Dictionary<string, string> { ["groupReservationId"] = "group-1" };

        APIGatewayProxyResponse response = await Handler().HandleRequest(request, _context.Object);

        Assert.Equal(400, response.StatusCode);
        VerifyNoServiceCall();
    }

    [Fact]
    public async Task HandleRequest_MissingIdentityReturnsUnauthorizedWithoutServiceCall()
    {
        APIGatewayProxyResponse response = await Handler().HandleRequest(Request(null), _context.Object);

        Assert.Equal(401, response.StatusCode);
        VerifyNoServiceCall();
    }

    [Fact]
    public async Task HandleRequest_WhitespaceIdentityReturnsUnauthorizedWithoutServiceCall()
    {
        APIGatewayProxyResponse response = await Handler().HandleRequest(Request("   "), _context.Object);

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("{\"error\":\"Unauthorized: User ID is missing.\"}", response.Body);
        VerifyNoServiceCall();
    }

    public static IEnumerable<object[]> ErrorCases()
    {
        yield return [new GroupReservationNotFoundException(), 404];
        yield return [new ReservationNotFoundException("Parent reservation not found."), 404];
        yield return [new GuestSessionNotFoundException("Guest session not found."), 404];
        yield return [new ActionForbiddenException("Only the reservation owner can perform this action."), 403];
        yield return [new ValidationException("Guest session does not belong to this group reservation."), 400];
        yield return [new DishNotFoundException("Dish not found."), 404];
    }

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public async Task HandleRequest_MapsDomainFailures(Exception failure, int expectedStatus)
    {
        _service.Setup(service => service.DeleteGuestSessionAsync("owner-1", "group-1", "guest-1"))
            .ThrowsAsync(failure);

        APIGatewayProxyResponse response = await Handler().HandleRequest(Request("owner-1"), _context.Object);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.DoesNotContain("participants", response.Body);
        _service.Verify(service => service.DeleteGuestSessionAsync("owner-1", "group-1", "guest-1"), Times.Once);
    }

    private DeleteGuestSessionHandler Handler() => new(_service.Object);

    private void VerifyNoServiceCall() => _service.Verify(
        service => service.DeleteGuestSessionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
        Times.Never);

    private static APIGatewayProxyRequest Request(string? userId) => new()
    {
        PathParameters = new Dictionary<string, string>
        {
            ["groupReservationId"] = "group-1",
            ["guestSessionId"] = "guest-1"
        },
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext
            {
                Claims = userId is null ? null : new Dictionary<string, string> { ["sub"] = userId }
            }
        }
    };
}

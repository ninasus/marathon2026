using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GuestSessions;
using APIHandler.Models.GuestSessions;
using APIHandler.Services.GuestSessions;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers.GuestSessions;

public class GetAllGuestSessionsHandlerTests
{
    private readonly Mock<IGuestSessionService> _service = new();
    private readonly Mock<ILambdaContext> _context = new();

    public GetAllGuestSessionsHandlerTests()
    {
        _context.SetupGet(c => c.Logger).Returns(new Mock<ILambdaLogger>().Object);
    }

    private static APIGatewayProxyRequest Request(string? groupId = "group-1", string? userId = "customer-1") => new()
    {
        PathParameters = groupId is null ? null : new Dictionary<string, string> { ["groupReservationId"] = groupId },
        RequestContext = userId is null ? null : new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext
            {
                Claims = new Dictionary<string, string> { ["sub"] = userId }
            }
        }
    };

    [Fact]
    public async Task HandleRequest_SerializesCamelCaseGuestRowsAndMinimalDish()
    {
        _service.Setup(s => s.GetAllGuestSessionsAsync("customer-1", "group-1"))
            .ReturnsAsync(new GetGroupSessionsResponse(
                [new GroupSessionParticipant(null, GroupSessionParticipantType.Guest, "Andrii",
                    [new GroupSessionItem(new GroupSessionDish("dish-1", "Caesar Salad", 180m), 2)], 360m, true)],
                360m));

        var response = await new GetAllGuestSessionsHandler(_service.Object).HandleRequest(Request(), _context.Object);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/json", response.Headers["Content-Type"]);
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal(new[] { "participants", "total" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(360m, json.RootElement.GetProperty("total").GetDecimal());
        var participant = Assert.Single(json.RootElement.GetProperty("participants").EnumerateArray());
        Assert.Equal(new[] { "id", "type", "name", "items", "total", "isSubmitted" },
            participant.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, participant.GetProperty("id").ValueKind);
        Assert.Equal("Guest", participant.GetProperty("type").GetString());
        Assert.Equal("Andrii", participant.GetProperty("name").GetString());
        Assert.True(participant.GetProperty("isSubmitted").GetBoolean());
        Assert.Equal(360m, participant.GetProperty("total").GetDecimal());
        var item = Assert.Single(participant.GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "dish", "quantity" }, item.EnumerateObject().Select(p => p.Name));
        Assert.Equal(2, item.GetProperty("quantity").GetInt32());
        var dish = item.GetProperty("dish");
        Assert.Equal(new[] { "id", "name", "price" }, dish.EnumerateObject().Select(p => p.Name));
        Assert.Equal("dish-1", dish.GetProperty("id").GetString());
        Assert.Equal("Caesar Salad", dish.GetProperty("name").GetString());
        Assert.Equal(180m, dish.GetProperty("price").GetDecimal());
        _service.Verify(s => s.GetAllGuestSessionsAsync("customer-1", "group-1"), Times.Once);
    }

    [Fact]
    public async Task HandleRequest_MissingGroupIdReturns400WithoutServiceCall()
    {
        var response = await new GetAllGuestSessionsHandler(_service.Object).HandleRequest(Request(groupId: null), _context.Object);

        Assert.Equal(400, response.StatusCode);
        _service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task HandleRequest_MissingCognitoSubReturns401WithoutServiceCall(string? userId)
    {
        var response = await new GetAllGuestSessionsHandler(_service.Object).HandleRequest(Request(userId: userId), _context.Object);

        Assert.Equal(401, response.StatusCode);
        _service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(typeof(GroupReservationNotFoundException), 404)]
    [InlineData(typeof(ReservationNotFoundException), 404)]
    [InlineData(typeof(ActionForbiddenException), 403)]
    [InlineData(typeof(ValidationException), 400)]
    public async Task HandleRequest_MapsKnownServiceErrors(Type exceptionType, int expectedStatus)
    {
        Exception exception = exceptionType == typeof(GroupReservationNotFoundException) ? new GroupReservationNotFoundException() :
            exceptionType == typeof(ReservationNotFoundException) ? new ReservationNotFoundException("missing") :
            exceptionType == typeof(ActionForbiddenException) ? new ActionForbiddenException("forbidden") :
            new ValidationException("missing dish");
        _service.Setup(s => s.GetAllGuestSessionsAsync("customer-1", "group-1"))
            .ThrowsAsync(exception);

        var response = await new GetAllGuestSessionsHandler(_service.Object).HandleRequest(Request(), _context.Object);

        Assert.Equal(expectedStatus, response.StatusCode);
        _service.Verify(s => s.GetAllGuestSessionsAsync("customer-1", "group-1"), Times.Once);
    }
}

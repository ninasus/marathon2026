using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GroupReservations;
using APIHandler.Models.GroupReservation;
using APIHandler.Services.GroupReservations;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers;

public class SubmitGroupPreOrderHandlerTests
{
    private readonly Mock<IGroupReservationService> _service = new();
    private readonly Mock<ILambdaContext> _context = new();
    private readonly Mock<ILambdaLogger> _logger = new();

    public SubmitGroupPreOrderHandlerTests() => _context.Setup(c => c.Logger).Returns(_logger.Object);

    [Fact]
    public async Task HandleRequest_ReturnsGroupedDishQuantityArrayAndForwardsIdentity()
    {
        _service.Setup(s => s.SubmitGroupPreOrderAsync("owner-1", "group-1", "reservation-1"))
            .ReturnsAsync([new SubmitGroupPreOrderResponse("dish-1", 3), new SubmitGroupPreOrderResponse("dish-2", 2)]);

        var response = await Handler().HandleRequest(Request(), _context.Object);

        Assert.Equal(200, response.StatusCode);
        using var json = JsonDocument.Parse(response.Body);
        var items = json.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal(2, items[0].EnumerateObject().Count());
        Assert.Equal("dish-1", items[0].GetProperty("dishId").GetString());
        Assert.Equal(3, items[0].GetProperty("quantity").GetInt32());
        Assert.Equal("dish-2", items[1].GetProperty("dishId").GetString());
        Assert.Equal(2, items[1].GetProperty("quantity").GetInt32());
        _service.Verify(s => s.SubmitGroupPreOrderAsync("owner-1", "group-1", "reservation-1"), Times.Once);
    }

    [Fact]
    public async Task HandleRequest_MissingIdentityReturns401WithoutServiceCall()
    {
        var response = await Handler().HandleRequest(Request(userId: null), _context.Object);

        Assert.Equal(401, response.StatusCode);
        VerifyNoServiceCall();
    }

    [Fact]
    public async Task HandleRequest_MissingAuthorizerReturns401WithoutServiceCall()
    {
        var request = Request();
        request.RequestContext.Authorizer = null!;

        var response = await Handler().HandleRequest(request, _context.Object);

        Assert.Equal(401, response.StatusCode);
        VerifyNoServiceCall();
    }

    [Theory]
    [InlineData(null, "{\"reservationId\":\"reservation-1\"}")]
    [InlineData("group-1", null)]
    [InlineData("group-1", "not json")]
    [InlineData("group-1", "{\"reservationId\":\"\"}")]
    public async Task HandleRequest_InvalidPathOrBodyReturns400(string? groupId, string? body)
    {
        var request = Request(body: body);
        request.PathParameters = groupId is null ? null : new Dictionary<string, string> { ["groupReservationId"] = groupId };

        var response = await Handler().HandleRequest(request, _context.Object);

        Assert.Equal(400, response.StatusCode);
        VerifyNoServiceCall();
    }

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public async Task HandleRequest_MapsServiceErrors(Exception error, int status)
    {
        _service.Setup(s => s.SubmitGroupPreOrderAsync("owner-1", "group-1", "reservation-1"))
            .ThrowsAsync(error);

        var response = await Handler().HandleRequest(Request(), _context.Object);

        Assert.Equal(status, response.StatusCode);
        Assert.NotEqual("[]", response.Body);
    }

    public static IEnumerable<object[]> ErrorCases() =>
    [
        [new GroupReservationNotFoundException(), 404],
        [new ReservationNotFoundException("missing"), 404],
        [new ActionForbiddenException("not owner"), 403],
        [new ValidationException("no submitted sessions"), 400],
        [new GroupReservationExpiredException(), 409]
    ];

    private SubmitGroupPreOrderHandler Handler() => new(_service.Object);

    private void VerifyNoServiceCall() => _service.Verify(s =>
        s.SubmitGroupPreOrderAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);

    private static APIGatewayProxyRequest Request(string? userId = "owner-1", string? body = "{\"reservationId\":\"reservation-1\"}") => new()
    {
        Body = body,
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

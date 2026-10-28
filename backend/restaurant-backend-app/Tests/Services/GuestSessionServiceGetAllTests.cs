using Amazon.CognitoIdentityProvider.Model;
using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.GuestSessions;
using Moq;

namespace APIHandler.Tests.Services;

public partial class GuestSessionServiceTests
{
    private const string GroupId = "group-1";
    private const string ReservationId = "reservation-1";
    private const string OwnerId = "customer-1";

    private void ArrangeOwnedGroup()
    {
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(GroupId))
            .ReturnsAsync(new GroupReservation
            {
                GroupReservationId = GroupId,
                ReservationId = ReservationId,
                ReservationToken = "token"
            });
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(new Reservation
            {
                Id = ReservationId,
                TableId = "table-1",
                WaiterId = "waiter-1",
                CustomerId = OwnerId
            });
        _mockUserRepository.Setup(r => r.GetUserByIdAsync(OwnerId))
            .ReturnsAsync(new UserType
            {
                Attributes = [new AttributeType { Name = "given_name", Value = "Andrii" }]
            });
        _mockGuestSessionRepository.Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([]);
        _mockOrderRepository.Setup(r => r.GetOrderByReservationIdAsync(ReservationId))
            .ReturnsAsync((Order?)null);
    }

    private static Order OrderForOwner(OrderStatus status, params OrderItem[] items) => new()
    {
        OrderId = "order-1",
        CustomerId = OwnerId,
        ReservationId = ReservationId,
        ReservationAddress = "address",
        ReservationTime = "time",
        Dishes = [.. items],
        Status = status
    };

    private static GuestSession Session(string id, string name, bool isSubmitted, bool isOwner, params OrderItem[] items) => new()
    {
        Id = id,
        GroupReservationId = GroupId,
        GuestName = name,
        Dishes = [.. items],
        IsSubmitted = isSubmitted,
        IsOwnerSession = isOwner
    };

    private static OrderItem Item(string dishId, int quantity) => new() { DishId = dishId, Quantity = quantity };
    private static Dish Dish(string id, string name, decimal price) => new() { Id = id, Name = name, Price = price };

    [Fact]
    public async Task GetAllGuestSessionsAsync_CombinesOwnerOrderAndJoinedGuestsWithCatalogPrices()
    {
        ArrangeOwnedGroup();
        _mockOrderRepository.Setup(r => r.GetOrderByReservationIdAsync(ReservationId))
            .ReturnsAsync(OrderForOwner(OrderStatus.SubmittedByCustomer, Item("starter", 1)));
        _mockGuestSessionRepository.Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([
                Session("session-1", "Andrii", true, false, Item("salad", 2)),
                Session("session-2", "Olena", false, false, Item("soup", 1))
            ]);
        _mockDishRepository.Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([Dish("starter", "Bread", 100m), Dish("salad", "Caesar Salad", 180m), Dish("soup", "Tom Yum", 220m)]);

        GetGroupSessionsResponse result = await _guestSessionService.GetAllGuestSessionsAsync(OwnerId, GroupId);

        var participants = result.Participants.ToArray();
        Assert.Equal(3, participants.Length);
        Assert.All(participants, p => Assert.Equal(GroupSessionParticipantType.Guest, p.Type));
        Assert.Null(participants[0].Id);
        Assert.Equal("Andrii", participants[0].Name);
        Assert.True(participants[0].IsSubmitted);
        Assert.Equal(100m, participants[0].Total);
        Assert.Equal(("starter", "Bread", 100m, 1), ItemValues(Assert.Single(participants[0].Items)));
        Assert.Equal("session-1", participants[1].Id);
        Assert.Equal("Andrii", participants[1].Name); // Matching names do not identify the creator.
        Assert.True(participants[1].IsSubmitted);
        Assert.Equal(360m, participants[1].Total);
        Assert.Equal(("salad", "Caesar Salad", 180m, 2), ItemValues(Assert.Single(participants[1].Items)));
        Assert.Equal("session-2", participants[2].Id);
        Assert.Equal("Olena", participants[2].Name);
        Assert.False(participants[2].IsSubmitted);
        Assert.Equal(220m, participants[2].Total);
        Assert.Equal(("soup", "Tom Yum", 220m, 1), ItemValues(Assert.Single(participants[2].Items)));
        Assert.Equal(680m, result.Total);
        _mockGuestSessionRepository.Verify(r => r.GetAllByGroupReservationIdAsync(GroupId), Times.Once);
        _mockOrderRepository.Verify(r => r.GetOrderByReservationIdAsync(ReservationId), Times.Once);
        _mockDishRepository.Verify(r => r.GetDishesByIdsAsync(It.Is<IEnumerable<string>>(ids =>
            ids.OrderBy(id => id).SequenceEqual(new[] { "salad", "soup", "starter" }))), Times.Once);
    }

    private static (string Id, string Name, decimal Price, int Quantity) ItemValues(GroupSessionItem item) =>
        (item.Dish.Id, item.Dish.Name, item.Dish.Price, item.Quantity);

    [Fact]
    public async Task GetAllGuestSessionsAsync_EmptyGroupStillReturnsCreatorWithoutDishLookup()
    {
        ArrangeOwnedGroup();

        var result = await _guestSessionService.GetAllGuestSessionsAsync(OwnerId, GroupId);

        var creator = Assert.Single(result.Participants);
        Assert.Null(creator.Id);
        Assert.Equal("Andrii", creator.Name);
        Assert.Equal(GroupSessionParticipantType.Guest, creator.Type);
        Assert.Empty(creator.Items);
        Assert.Equal(0m, creator.Total);
        Assert.False(creator.IsSubmitted);
        Assert.Equal(0m, result.Total);
        _mockDishRepository.Verify(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Fact]
    public async Task GetAllGuestSessionsAsync_UsesStoredOwnerSessionWhenNoActiveOrder()
    {
        ArrangeOwnedGroup();
        _mockGuestSessionRepository.Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([
                Session("owner-session", "Owner", true, true, Item("salad", 2)),
                Session("guest-session", "Andrii", false, false)
            ]);
        _mockDishRepository.Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([Dish("salad", "Caesar Salad", 180m)]);

        var result = await _guestSessionService.GetAllGuestSessionsAsync(OwnerId, GroupId);

        var participants = result.Participants.ToArray();
        Assert.Equal(2, participants.Length);
        var creator = Assert.Single(participants, p => p.Type == GroupSessionParticipantType.Owner);
        Assert.Equal("owner-session", creator.Id);
        Assert.Equal("Andrii", creator.Name);
        Assert.True(creator.IsSubmitted);
        Assert.Equal(("salad", "Caesar Salad", 180m, 2), ItemValues(Assert.Single(creator.Items)));
        Assert.Equal(360m, creator.Total);
        Assert.Equal("guest-session", participants[1].Id);
        Assert.Equal("Andrii", participants[1].Name);
        Assert.Equal(GroupSessionParticipantType.Guest, participants[1].Type);
        Assert.Empty(participants[1].Items);
        Assert.Equal(360m, result.Total);
    }

    [Fact]
    public async Task GetAllGuestSessionsAsync_ActiveOrderReplacesStoredOwnerCartWithoutDuplicate()
    {
        ArrangeOwnedGroup();
        _mockGuestSessionRepository.Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([Session("owner-session", "Owner", true, true, Item("old-dish", 3))]);
        _mockOrderRepository.Setup(r => r.GetOrderByReservationIdAsync(ReservationId))
            .ReturnsAsync(OrderForOwner(OrderStatus.Created, Item("current-dish", 2)));
        _mockDishRepository.Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([Dish("current-dish", "Current", 25m)]);

        var result = await _guestSessionService.GetAllGuestSessionsAsync(OwnerId, GroupId);

        var creator = Assert.Single(result.Participants);
        Assert.Equal("owner-session", creator.Id);
        Assert.Equal(GroupSessionParticipantType.Owner, creator.Type);
        Assert.False(creator.IsSubmitted);
        Assert.Equal(("current-dish", "Current", 25m, 2), ItemValues(Assert.Single(creator.Items)));
        Assert.Equal(50m, result.Total);
        _mockDishRepository.Verify(r => r.GetDishesByIdsAsync(It.Is<IEnumerable<string>>(ids => ids.SequenceEqual(new[] { "current-dish" }))), Times.Once);
    }

    [Fact]
    public async Task GetAllGuestSessionsAsync_CanceledCreatorItemIsNeitherLookedUpNorCounted()
    {
        ArrangeOwnedGroup();
        var canceledItem = Item("deleted-dish", 5);
        canceledItem.State = OrderItemState.Canceled;
        var activeItem = Item("active-dish", 2);
        activeItem.State = OrderItemState.Pending;
        _mockOrderRepository.Setup(r => r.GetOrderByReservationIdAsync(ReservationId))
            .ReturnsAsync(OrderForOwner(OrderStatus.SubmittedByCustomer, canceledItem, activeItem));
        _mockGuestSessionRepository.Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([Session("guest-1", "Olena", false, false, Item("guest-dish", 1))]);
        _mockDishRepository.Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([Dish("active-dish", "Active", 30m), Dish("guest-dish", "Guest dish", 20m)]);

        var result = await _guestSessionService.GetAllGuestSessionsAsync(OwnerId, GroupId);

        var participants = result.Participants.ToArray();
        Assert.Equal(2, participants.Length);
        Assert.Equal(("active-dish", "Active", 30m, 2), ItemValues(Assert.Single(participants[0].Items)));
        Assert.Equal(60m, participants[0].Total);
        Assert.True(participants[0].IsSubmitted);
        Assert.Equal(20m, participants[1].Total);
        Assert.Equal(80m, result.Total);
        _mockDishRepository.Verify(r => r.GetDishesByIdsAsync(It.Is<IEnumerable<string>>(ids =>
            ids.OrderBy(id => id).SequenceEqual(new[] { "active-dish", "guest-dish" }))), Times.Once);
        _mockDishRepository.Verify(r => r.GetDishesByIdsAsync(It.Is<IEnumerable<string>>(ids =>
            ids.Contains("deleted-dish"))), Times.Never);
    }

    [Theory]
    [InlineData(OrderStatus.Created, false)]
    [InlineData(OrderStatus.Submitted, true)]
    [InlineData(OrderStatus.SubmittedByCustomer, true)]
    public async Task GetAllGuestSessionsAsync_MapsActiveOrderSubmissionStatus(OrderStatus status, bool expectedSubmitted)
    {
        ArrangeOwnedGroup();
        _mockOrderRepository.Setup(r => r.GetOrderByReservationIdAsync(ReservationId))
            .ReturnsAsync(OrderForOwner(status));

        var creator = Assert.Single((await _guestSessionService.GetAllGuestSessionsAsync(OwnerId, GroupId)).Participants);

        Assert.Equal(expectedSubmitted, creator.IsSubmitted);
    }

    [Theory]
    [InlineData(OrderStatus.Cancelled, OwnerId)]
    [InlineData(OrderStatus.Submitted, "other-customer")]
    public async Task GetAllGuestSessionsAsync_ExcludesCancelledOrDifferentCustomerOrder(OrderStatus status, string orderCustomerId)
    {
        ArrangeOwnedGroup();
        var order = OrderForOwner(status, Item("unrelated", 4));
        order.CustomerId = orderCustomerId;
        _mockOrderRepository.Setup(r => r.GetOrderByReservationIdAsync(ReservationId)).ReturnsAsync(order);

        var result = await _guestSessionService.GetAllGuestSessionsAsync(OwnerId, GroupId);

        var creator = Assert.Single(result.Participants);
        Assert.Empty(creator.Items);
        Assert.False(creator.IsSubmitted);
        Assert.Equal(0m, result.Total);
        _mockDishRepository.Verify(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Fact]
    public async Task GetAllGuestSessionsAsync_RejectsWrongUserBeforeParticipantDataReads()
    {
        ArrangeOwnedGroup();

        await Assert.ThrowsAsync<ActionForbiddenException>(() =>
            _guestSessionService.GetAllGuestSessionsAsync("different-customer", GroupId));

        _mockGroupReservationRepository.Verify(r => r.GetGroupReservationByIdAsync(GroupId), Times.Once);
        _mockReservationRepository.Verify(r => r.GetReservationByIdAsync(ReservationId), Times.Once);
        _mockGuestSessionRepository.VerifyNoOtherCalls();
        _mockOrderRepository.VerifyNoOtherCalls();
        _mockDishRepository.VerifyNoOtherCalls();
        _mockUserRepository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetAllGuestSessionsAsync_MissingGroupReturnsNotFoundWithoutFurtherReads()
    {
        await Assert.ThrowsAsync<GroupReservationNotFoundException>(() =>
            _guestSessionService.GetAllGuestSessionsAsync(OwnerId, GroupId));

        _mockReservationRepository.VerifyNoOtherCalls();
        _mockGuestSessionRepository.VerifyNoOtherCalls();
        _mockOrderRepository.VerifyNoOtherCalls();
        _mockDishRepository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetAllGuestSessionsAsync_MissingParentReservationReturnsNotFoundWithoutParticipantReads()
    {
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(GroupId))
            .ReturnsAsync(new GroupReservation { GroupReservationId = GroupId, ReservationId = ReservationId, ReservationToken = "token" });

        await Assert.ThrowsAsync<ReservationNotFoundException>(() =>
            _guestSessionService.GetAllGuestSessionsAsync(OwnerId, GroupId));

        _mockGuestSessionRepository.VerifyNoOtherCalls();
        _mockOrderRepository.VerifyNoOtherCalls();
        _mockDishRepository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetAllGuestSessionsAsync_MissingCatalogDishReturnsControlledValidationError()
    {
        ArrangeOwnedGroup();
        _mockGuestSessionRepository.Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([Session("guest", "Olena", false, false, Item("missing", 1))]);
        _mockDishRepository.Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([]);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.GetAllGuestSessionsAsync(OwnerId, GroupId));

        Assert.Contains("missing", error.Message);
        _mockUserRepository.Verify(r => r.GetUserByIdAsync(It.IsAny<string>()), Times.Never);
    }
}

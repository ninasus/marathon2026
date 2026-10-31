using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Orders;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.Tables;
using APIHandler.Services.Auth;
using APIHandler.Services.GroupReservations;
using APIHandler.Services.Locations;
using Moq;

namespace APIHandler.Tests.Services;

public class SubmitGroupPreOrderServiceTests
{
    private const string OwnerId = "owner-1";
    private const string GroupId = "group-1";
    private const string ReservationId = "reservation-1";

    private readonly Mock<IGroupReservationRepository> _groups = new();
    private readonly Mock<IReservationRepository> _reservations = new();
    private readonly Mock<IGuestSessionRepository> _sessions = new();
    private readonly Mock<IOrderRepository> _orders = new();
    private readonly GroupReservationService _service;

    public SubmitGroupPreOrderServiceTests()
    {
        _service = new GroupReservationService(
            _groups.Object,
            Mock.Of<ITableRepository>(),
            _reservations.Object,
            _sessions.Object,
            Mock.Of<IUserService>(),
            Mock.Of<ILocationService>(),
            _orders.Object);
    }

    [Fact]
    public async Task Submit_ConsolidatesOnlySubmittedSessionsAndSavesOrderBeforeGroup()
    {
        var group = ArrangeValidRecords();
        _sessions.Setup(r => r.GetAllByGroupReservationIdAsync(GroupId)).ReturnsAsync([
            Session("owner", true, Item("dish-1", 1), Item("dish-2", 2)),
            Session("guest", true, Item("dish-1", 2)),
            Session("draft", false, Item("dish-1", 99), Item("dish-3", 5))
        ]);
        var order = ExistingOrder();
        _orders.Setup(r => r.GetOrderByReservationIdAsync(ReservationId)).ReturnsAsync(order);
        var calls = new List<string>();
        _orders.Setup(r => r.SaveOrderAsync(It.IsAny<Order>())).Callback<Order>(saved =>
        {
            calls.Add("order");
            Assert.Same(order, saved);
            Assert.Equal(OrderStatus.SubmittedByCustomer, saved.Status);
            Assert.Equal(2, saved.Dishes.Count);
            Assert.Equal(3, saved.Dishes.Single(item => item.DishId == "dish-1").Quantity);
            Assert.Equal(2, saved.Dishes.Single(item => item.DishId == "dish-2").Quantity);
            Assert.All(saved.Dishes, item => Assert.Equal(OrderItemState.Pending, item.State));
        }).Returns(Task.CompletedTask);
        _groups.Setup(r => r.SaveGroupReservationAsync(It.IsAny<GroupReservation>()))
            .Callback<GroupReservation>(saved =>
            {
                calls.Add("group");
                Assert.Same(group, saved);
                Assert.Equal(GroupReservationStatus.Submitted, saved.Status);
            })
            .Returns(Task.CompletedTask);

        var response = await _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId);

        Assert.Equal(new[] { ("dish-1", 3), ("dish-2", 2) }, response.Select(item => (item.DishId, item.Quantity)));
        Assert.Equal(new[] { "order", "group" }, calls);
        _orders.Verify(r => r.SaveOrderAsync(It.IsAny<Order>()), Times.Once);
        _groups.Verify(r => r.SaveGroupReservationAsync(group), Times.Once);
    }

    [Fact]
    public async Task Submit_MissingGroupOrReservationDoesNotWrite()
    {
        await Assert.ThrowsAsync<GroupReservationNotFoundException>(() =>
            _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        _groups.Setup(r => r.GetGroupReservationByIdAsync(GroupId)).ReturnsAsync(OpenGroup());
        await Assert.ThrowsAsync<ReservationNotFoundException>(() =>
            _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        VerifyNoWrites();
    }

    [Fact]
    public async Task Submit_DifferentOwnerDoesNotReadSessionsOrWrite()
    {
        ArrangeValidRecords();

        await Assert.ThrowsAsync<ActionForbiddenException>(() =>
            _service.SubmitGroupPreOrderAsync("other-user", GroupId, ReservationId));

        _sessions.Verify(r => r.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
        VerifyNoWrites();
    }

    [Fact]
    public async Task Submit_MismatchedReservationDoesNotWrite()
    {
        ArrangeValidRecords();
        _groups.Setup(r => r.GetGroupReservationByIdAsync(GroupId))
            .ReturnsAsync(new GroupReservation { GroupReservationId = GroupId, ReservationId = "other-reservation", ReservationToken = "token", Status = GroupReservationStatus.Open });

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        VerifyNoWrites();
    }

    [Theory]
    [InlineData(GroupReservationStatus.Submitted, typeof(GroupReservationNotFoundException))]
    [InlineData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    public async Task Submit_NonOpenGroupDoesNotWrite(GroupReservationStatus status, Type expected)
    {
        ArrangeValidRecords();
        _groups.Setup(r => r.GetGroupReservationByIdAsync(GroupId))
            .ReturnsAsync(new GroupReservation { GroupReservationId = GroupId, ReservationId = ReservationId, ReservationToken = "token", Status = status });

        await Assert.ThrowsAsync(expected, () =>
            _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        VerifyNoWrites();
    }

    [Fact]
    public async Task Submit_NoSubmittedSessionsDoesNotWrite()
    {
        ArrangeValidRecords();
        _sessions.Setup(r => r.GetAllByGroupReservationIdAsync(GroupId)).ReturnsAsync([
            Session("draft", false, Item("dish-1", 2))
        ]);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        VerifyNoWrites();
    }

    [Fact]
    public async Task Submit_SubmittedSessionsWithNoDishesDoesNotWrite()
    {
        ArrangeValidRecords();
        _sessions.Setup(r => r.GetAllByGroupReservationIdAsync(GroupId)).ReturnsAsync([
            Session("empty", true), Session("draft", false, Item("dish-1", 2))
        ]);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        VerifyNoWrites();
    }

    [Fact]
    public async Task Submit_MissingOrderDoesNotChangeGroup()
    {
        ArrangeValidRecords();
        _sessions.Setup(r => r.GetAllByGroupReservationIdAsync(GroupId)).ReturnsAsync([
            Session("guest", true, Item("dish-1", 1))
        ]);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        VerifyNoWrites();
    }

    [Fact]
    public async Task Submit_OrderSaveFailureDoesNotAttemptGroupUpdate()
    {
        ArrangeReadyForWrite();
        _orders.Setup(r => r.SaveOrderAsync(It.IsAny<Order>())).ThrowsAsync(new InvalidOperationException("order save failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        _groups.Verify(r => r.SaveGroupReservationAsync(It.IsAny<GroupReservation>()), Times.Never);
    }

    [Fact]
    public async Task Submit_GroupSaveFailureLeavesSavedOrderAndDoesNotReturnSuccess()
    {
        ArrangeReadyForWrite();
        _groups.Setup(r => r.SaveGroupReservationAsync(It.IsAny<GroupReservation>()))
            .ThrowsAsync(new InvalidOperationException("group save failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));

        _orders.Verify(r => r.SaveOrderAsync(It.Is<Order>(o => o.Status == OrderStatus.SubmittedByCustomer)), Times.Once);
        _groups.Verify(r => r.SaveGroupReservationAsync(It.Is<GroupReservation>(g => g.Status == GroupReservationStatus.Submitted)), Times.Once);
    }

    [Fact]
    public async Task Submit_RetryWhileOpenReplacesOrderSnapshotAndAfterSuccessReturnsNotFound()
    {
        ArrangeReadyForWrite();
        var order = ExistingOrder();
        _orders.Setup(r => r.GetOrderByReservationIdAsync(ReservationId)).ReturnsAsync(order);
        int attempts = 0;
        GroupReservationStatus persistedStatus = GroupReservationStatus.Open;
        _groups.Setup(r => r.GetGroupReservationByIdAsync(GroupId)).ReturnsAsync(() => new GroupReservation
        {
            GroupReservationId = GroupId, ReservationId = ReservationId, ReservationToken = "token", Status = persistedStatus
        });
        _groups.Setup(r => r.SaveGroupReservationAsync(It.IsAny<GroupReservation>()))
            .Returns<GroupReservation>(saved =>
            {
                attempts++;
                if (attempts == 1) throw new InvalidOperationException("group save failed");
                persistedStatus = saved.Status;
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));
        var response = await _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId);

        Assert.Single(response);
        Assert.Single(order.Dishes);
        Assert.Equal(2, order.Dishes[0].Quantity);
        await Assert.ThrowsAsync<GroupReservationNotFoundException>(() =>
            _service.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));
        _orders.Verify(r => r.SaveOrderAsync(It.IsAny<Order>()), Times.Exactly(2));
    }

    private GroupReservation ArrangeValidRecords()
    {
        var group = OpenGroup();
        _groups.Setup(r => r.GetGroupReservationByIdAsync(GroupId)).ReturnsAsync(group);
        _reservations.Setup(r => r.GetReservationByIdAsync(ReservationId)).ReturnsAsync(new Reservation
        {
            Id = ReservationId, TableId = "table-1", WaiterId = "waiter-1", CustomerId = OwnerId
        });
        return group;
    }

    private GroupReservation ArrangeReadyForWrite()
    {
        var group = OpenGroup();
        _groups.Setup(r => r.GetGroupReservationByIdAsync(GroupId)).ReturnsAsync(group);
        _reservations.Setup(r => r.GetReservationByIdAsync(ReservationId)).ReturnsAsync(new Reservation
        {
            Id = ReservationId, TableId = "table-1", WaiterId = "waiter-1", CustomerId = OwnerId
        });
        _sessions.Setup(r => r.GetAllByGroupReservationIdAsync(GroupId)).ReturnsAsync([
            Session("guest", true, Item("dish-1", 2))
        ]);
        _orders.Setup(r => r.GetOrderByReservationIdAsync(ReservationId)).ReturnsAsync(ExistingOrder());
        _orders.Setup(r => r.SaveOrderAsync(It.IsAny<Order>())).Returns(Task.CompletedTask);
        return group;
    }

    private void VerifyNoWrites()
    {
        _orders.Verify(r => r.SaveOrderAsync(It.IsAny<Order>()), Times.Never);
        _groups.Verify(r => r.SaveGroupReservationAsync(It.IsAny<GroupReservation>()), Times.Never);
    }

    private static GroupReservation OpenGroup() => new()
    {
        GroupReservationId = GroupId, ReservationId = ReservationId, ReservationToken = "token", Status = GroupReservationStatus.Open
    };

    private static GuestSession Session(string id, bool submitted, params OrderItem[] dishes) => new()
    {
        Id = id, GuestName = id, GroupReservationId = GroupId, IsSubmitted = submitted, Dishes = dishes.ToList()
    };

    private static OrderItem Item(string dishId, int quantity) => new()
    {
        DishId = dishId, Quantity = quantity, State = OrderItemState.Pending
    };

    private static Order ExistingOrder() => new()
    {
        OrderId = "order-1", CustomerId = OwnerId, ReservationId = ReservationId,
        ReservationAddress = "address", ReservationTime = "time",
        Dishes = [Item("old-dish", 7)], Status = OrderStatus.Created
    };
}

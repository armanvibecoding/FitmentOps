using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class ReturnServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 5, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Request_DeliveredOrderIsIdempotent_AndChangedPayloadConflicts()
    {
        await using var database = await TestDatabase.CreateAsync();
        var order = await database.AddOrderAsync(OrderStatuses.Delivered, quantity: 3);
        var item = Assert.Single(order.OrderItems);
        var service = CreateService(database.Context);

        var first = await service.RequestAsync(
            order.Id,
            " rma-create-1 ",
            [new ReturnItemRequest(item.Id, 2, ReturnReasonCodes.Defective)]);
        var replay = await service.RequestAsync(
            order.Id,
            "rma-create-1",
            [new ReturnItemRequest(item.Id, 2, ReturnReasonCodes.Defective)]);
        var conflict = await service.RequestAsync(
            order.Id,
            "rma-create-1",
            [new ReturnItemRequest(item.Id, 1, ReturnReasonCodes.Defective)]);

        Assert.Equal(ReturnServiceOutcome.Created, first.Outcome);
        Assert.Equal(ReturnServiceOutcome.Replayed, replay.Outcome);
        Assert.Equal(first.ReturnRequest!.Id, replay.ReturnRequest!.Id);
        Assert.Equal(ReturnServiceOutcome.Conflict, conflict.Outcome);
        Assert.Equal(1, await database.Context.Set<ReturnRequest>().CountAsync());
    }

    [Fact]
    public async Task Request_RejectsNonDeliveredOrderAndForeignOrderItem()
    {
        await using var database = await TestDatabase.CreateAsync();
        var pendingOrder = await database.AddOrderAsync(OrderStatuses.Shipped, quantity: 1);
        var deliveredOrder = await database.AddOrderAsync(OrderStatuses.Delivered, quantity: 1);
        var pendingItem = Assert.Single(pendingOrder.OrderItems);
        var deliveredItem = Assert.Single(deliveredOrder.OrderItems);
        var service = CreateService(database.Context);

        var notDelivered = await service.RequestAsync(
            pendingOrder.Id,
            "rma-not-delivered",
            [new ReturnItemRequest(pendingItem.Id, 1, ReturnReasonCodes.Defective)]);
        var foreignItem = await service.RequestAsync(
            deliveredOrder.Id,
            "rma-foreign-item",
            [new ReturnItemRequest(pendingItem.Id, 1, ReturnReasonCodes.Defective)]);

        Assert.Equal(ReturnServiceOutcome.Conflict, notDelivered.Outcome);
        Assert.Equal(ReturnServiceOutcome.InvalidRequest, foreignItem.Outcome);
        Assert.NotEqual(pendingItem.Id, deliveredItem.Id);
        Assert.Empty(await database.Context.Set<ReturnRequest>().ToListAsync());
    }

    [Fact]
    public async Task Request_ActiveAndCompletedReturnsConsumePurchasedQuantity()
    {
        await using var database = await TestDatabase.CreateAsync();
        var order = await database.AddOrderAsync(OrderStatuses.Delivered, quantity: 3);
        var item = Assert.Single(order.OrderItems);
        var service = CreateService(database.Context);

        var first = await service.RequestAsync(
            order.Id,
            "rma-accounted-1",
            [new ReturnItemRequest(item.Id, 2, ReturnReasonCodes.WrongItem)]);
        var id = first.ReturnRequest!.Id;
        Assert.Equal(ReturnServiceOutcome.Updated,
            (await service.TransitionAsync(id, ReturnRequestStatuses.Approved)).Outcome);
        Assert.Equal(ReturnServiceOutcome.Updated,
            (await service.TransitionAsync(id, ReturnRequestStatuses.Received)).Outcome);
        Assert.Equal(ReturnServiceOutcome.Updated,
            (await service.TransitionAsync(id, ReturnRequestStatuses.Inspected)).Outcome);
        var refund = await database.AddRefundAsync(
            order,
            amount: 200m,
            status: RefundStatuses.Requested);
        Assert.Equal(ReturnServiceOutcome.Updated,
            (await service.MarkRefundPendingAsync(id, refund.Id)).Outcome);
        refund.Status = RefundStatuses.Succeeded;
        refund.ProviderRefundId = "refund-confirmation-1";
        refund.CompletedAt = Now.AddMinutes(1).UtcDateTime;
        await database.Context.SaveChangesAsync();
        Assert.Equal(ReturnServiceOutcome.Updated,
            (await service.ReconcileRefundAsync(id)).Outcome);

        var exceeds = await service.RequestAsync(
            order.Id,
            "rma-accounted-2",
            [new ReturnItemRequest(item.Id, 2, ReturnReasonCodes.Defective)]);
        var fits = await service.RequestAsync(
            order.Id,
            "rma-accounted-3",
            [new ReturnItemRequest(item.Id, 1, ReturnReasonCodes.Defective)]);

        Assert.Equal(ReturnServiceOutcome.Conflict, exceeds.Outcome);
        Assert.Equal(ReturnServiceOutcome.Created, fits.Outcome);
    }

    [Fact]
    public async Task Request_CancelledReturnReleasesCapacity()
    {
        await using var database = await TestDatabase.CreateAsync();
        var order = await database.AddOrderAsync(OrderStatuses.Delivered, quantity: 2);
        var item = Assert.Single(order.OrderItems);
        var service = CreateService(database.Context);

        var first = await service.RequestAsync(
            order.Id,
            "rma-cancelled-1",
            [new ReturnItemRequest(item.Id, 2, ReturnReasonCodes.Incompatible)]);
        await service.TransitionAsync(first.ReturnRequest!.Id, ReturnRequestStatuses.Cancelled);
        var replacement = await service.RequestAsync(
            order.Id,
            "rma-cancelled-2",
            [new ReturnItemRequest(item.Id, 2, ReturnReasonCodes.Incompatible)]);

        Assert.Equal(ReturnServiceOutcome.Created, replacement.Outcome);
    }

    [Fact]
    public async Task ConcurrentRequestsCannotOverReserveOneOrderItem()
    {
        await using var database = await SharedTestDatabase.CreateAsync();
        var order = await database.AddOrderAsync(quantity: 2);
        var item = Assert.Single(order.OrderItems);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<ReturnServiceResult> SubmitAsync(string key)
        {
            await start.Task;
            await using var context = database.CreateContext();
            return await CreateService(context).RequestAsync(
                order.Id,
                key,
                [new ReturnItemRequest(item.Id, 2, ReturnReasonCodes.DamagedInTransit)]);
        }

        var firstTask = SubmitAsync("rma-race-1");
        var secondTask = SubmitAsync("rma-race-2");
        start.SetResult();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Single(results, result => result.Outcome == ReturnServiceOutcome.Created);
        Assert.Single(results, result => result.Outcome == ReturnServiceOutcome.Conflict);
        await using var verification = database.CreateContext();
        Assert.Equal(1, await verification.Set<ReturnRequest>().CountAsync());
        Assert.Equal(2, await verification.Set<ReturnItem>().SumAsync(value => value.Quantity));
    }

    [Fact]
    public async Task StateMachineRequiresExplicitExternalRefundCommands_AndNeverRegresses()
    {
        await using var database = await TestDatabase.CreateAsync();
        var order = await database.AddOrderAsync(OrderStatuses.Delivered, quantity: 1);
        var item = Assert.Single(order.OrderItems);
        var service = CreateService(database.Context);
        var created = await service.RequestAsync(
            order.Id,
            "rma-transitions",
            [new ReturnItemRequest(item.Id, 1, ReturnReasonCodes.NotAsDescribed)]);
        var id = created.ReturnRequest!.Id;

        Assert.Equal(ReturnServiceOutcome.Updated,
            (await service.TransitionAsync(id, ReturnRequestStatuses.Approved)).Outcome);
        Assert.Equal(ReturnServiceOutcome.Updated,
            (await service.TransitionAsync(id, ReturnRequestStatuses.Received)).Outcome);
        Assert.Equal(ReturnServiceOutcome.Updated,
            (await service.TransitionAsync(id, ReturnRequestStatuses.Inspected)).Outcome);
        Assert.Equal(ReturnServiceOutcome.InvalidRequest,
            (await service.TransitionAsync(id, ReturnRequestStatuses.RefundPending)).Outcome);
        Assert.Equal(ReturnServiceOutcome.Conflict,
            (await service.TransitionAsync(id, ReturnRequestStatuses.Closed)).Outcome);

        var refund = await database.AddRefundAsync(order, 100m, RefundStatuses.Requested);
        var otherRefund = await database.AddRefundAsync(order, 100m, RefundStatuses.Requested);
        var pending = await service.MarkRefundPendingAsync(id, refund.Id);
        var pendingReplay = await service.MarkRefundPendingAsync(id, refund.Id);
        var pendingConflict = await service.MarkRefundPendingAsync(id, otherRefund.Id);
        refund.Status = RefundStatuses.Succeeded;
        refund.ProviderRefundId = "refund-confirmation-transition";
        refund.CompletedAt = Now.AddMinutes(1).UtcDateTime;
        await database.Context.SaveChangesAsync();
        var refunded = await service.ReconcileRefundAsync(id);
        var refundedReplay = await service.ReconcileRefundAsync(id);
        var closed = await service.TransitionAsync(id, ReturnRequestStatuses.Closed);
        var backward = await service.TransitionAsync(id, ReturnRequestStatuses.Approved);

        Assert.Equal(ReturnServiceOutcome.Updated, pending.Outcome);
        Assert.Equal(ReturnServiceOutcome.Replayed, pendingReplay.Outcome);
        Assert.Equal(ReturnServiceOutcome.Conflict, pendingConflict.Outcome);
        Assert.Equal(ReturnServiceOutcome.Updated, refunded.Outcome);
        Assert.Equal(ReturnServiceOutcome.Replayed, refundedReplay.Outcome);
        Assert.Equal(ReturnServiceOutcome.Updated, closed.Outcome);
        Assert.Equal(ReturnServiceOutcome.Conflict, backward.Outcome);
        Assert.Equal(ReturnRequestStatuses.Closed, closed.ReturnRequest!.Status);
    }

    [Fact]
    public async Task Request_AcceptsOnlyAllowlistedCodes_AndDomainHasNoFreeTextDescription()
    {
        await using var database = await TestDatabase.CreateAsync();
        var order = await database.AddOrderAsync(OrderStatuses.Delivered, quantity: 1);
        var item = Assert.Single(order.OrderItems);
        var service = CreateService(database.Context);

        var result = await service.RequestAsync(
            order.Id,
            "rma-unsafe-reason",
            [new ReturnItemRequest(item.Id, 1, "call-customer-at-05550000000")]);

        Assert.Equal(ReturnServiceOutcome.InvalidRequest, result.Outcome);
        Assert.DoesNotContain(
            typeof(ReturnItem).GetProperties(),
            property => property.Name.Contains("Description", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Comment", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Note", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RefundRecordCannotBeLinkedToTwoReturns()
    {
        await using var database = await TestDatabase.CreateAsync();
        var order = await database.AddOrderAsync(OrderStatuses.Delivered, quantity: 2);
        var service = CreateService(database.Context);
        var first = await service.RequestAsync(
            order.Id,
            "rma-external-reference-1",
            [new ReturnItemRequest(order.OrderItems.Single().Id, 1, ReturnReasonCodes.Defective)]);
        var second = await service.RequestAsync(
            order.Id,
            "rma-external-reference-2",
            [new ReturnItemRequest(order.OrderItems.Single().Id, 1, ReturnReasonCodes.Defective)]);
        foreach (var request in new[] { first.ReturnRequest!, second.ReturnRequest! })
        {
            await service.TransitionAsync(request.Id, ReturnRequestStatuses.Approved);
            await service.TransitionAsync(request.Id, ReturnRequestStatuses.Received);
            await service.TransitionAsync(request.Id, ReturnRequestStatuses.Inspected);
        }
        var refund = await database.AddRefundAsync(order, 100m, RefundStatuses.Requested);

        var linked = await service.MarkRefundPendingAsync(
            first.ReturnRequest!.Id,
            refund.Id);
        var duplicate = await service.MarkRefundPendingAsync(
            second.ReturnRequest!.Id,
            refund.Id);

        Assert.Equal(ReturnServiceOutcome.Updated, linked.Outcome);
        Assert.Equal(ReturnServiceOutcome.Conflict, duplicate.Outcome);
    }

    [Fact]
    public async Task SucceededRefundRequiresImmutableValidProviderCompletion()
    {
        await using var database = await TestDatabase.CreateAsync();
        var order = await database.AddOrderAsync(OrderStatuses.Delivered, quantity: 1);
        var service = CreateService(database.Context);
        var created = await service.RequestAsync(
            order.Id,
            "rma-refund-time",
            [new ReturnItemRequest(order.OrderItems.Single().Id, 1, ReturnReasonCodes.Defective)]);
        await service.TransitionAsync(created.ReturnRequest!.Id, ReturnRequestStatuses.Approved);
        await service.TransitionAsync(created.ReturnRequest.Id, ReturnRequestStatuses.Received);
        await service.TransitionAsync(created.ReturnRequest.Id, ReturnRequestStatuses.Inspected);
        var refund = await database.AddRefundAsync(
            order,
            100m,
            RefundStatuses.Succeeded,
            providerRefundId: "refund-time-confirmation",
            completedAt: Now.AddSeconds(-1).UtcDateTime);

        var invalidTime = await service.MarkRefundPendingAsync(created.ReturnRequest.Id, refund.Id);
        refund.CompletedAt = Now.AddMinutes(1).UtcDateTime;
        refund.ProviderRefundId = null;
        await database.Context.SaveChangesAsync();
        var missingProviderId = await service.MarkRefundPendingAsync(created.ReturnRequest.Id, refund.Id);
        refund.ProviderRefundId = "refund-time-confirmation";
        await database.Context.SaveChangesAsync();
        var reconciled = await service.MarkRefundPendingAsync(created.ReturnRequest.Id, refund.Id);
        var replay = await service.ReconcileRefundAsync(created.ReturnRequest.Id);

        Assert.Equal(ReturnServiceOutcome.Conflict, invalidTime.Outcome);
        Assert.Equal(ReturnServiceOutcome.Conflict, missingProviderId.Outcome);
        Assert.Equal(ReturnServiceOutcome.Updated, reconciled.Outcome);
        Assert.Equal(ReturnServiceOutcome.Replayed, replay.Outcome);
        Assert.Equal(ReturnRequestStatuses.Refunded, created.ReturnRequest.Status);

        refund.ProviderRefundId = "mutated-provider-confirmation";
        await database.Context.SaveChangesAsync();
        var mutatedCompletion = await service.ReconcileRefundAsync(created.ReturnRequest.Id);
        Assert.Equal(ReturnServiceOutcome.Conflict, mutatedCompletion.Outcome);
    }

    [Fact]
    public async Task RefundBindingValidatesOrderCurrencyAmountAndLineSnapshot()
    {
        await using var database = await TestDatabase.CreateAsync();
        var order = await database.AddOrderAsync(OrderStatuses.Delivered, quantity: 1);
        var otherOrder = await database.AddOrderAsync(OrderStatuses.Delivered, quantity: 1);
        var service = CreateService(database.Context);
        var request = await service.RequestAsync(
            order.Id,
            "rma-refund-binding",
            [new ReturnItemRequest(order.OrderItems.Single().Id, 1, ReturnReasonCodes.Defective)]);
        await MoveToInspectedAsync(service, request.ReturnRequest!.Id);

        var foreign = await database.AddRefundAsync(otherOrder, 100m, RefundStatuses.Requested);
        var wrongCurrency = await database.AddRefundAsync(
            order,
            100m,
            RefundStatuses.Requested,
            currency: "USD");
        var zero = await database.AddRefundAsync(order, 0m, RefundStatuses.Requested);
        var excessive = await database.AddRefundAsync(order, 100.01m, RefundStatuses.Requested);
        var valid = await database.AddRefundAsync(order, 100m, RefundStatuses.Requested);

        Assert.Equal(ReturnServiceOutcome.Conflict,
            (await service.MarkRefundPendingAsync(request.ReturnRequest.Id, foreign.Id)).Outcome);
        Assert.Equal(ReturnServiceOutcome.Conflict,
            (await service.MarkRefundPendingAsync(request.ReturnRequest.Id, wrongCurrency.Id)).Outcome);
        Assert.Equal(ReturnServiceOutcome.Conflict,
            (await service.MarkRefundPendingAsync(request.ReturnRequest.Id, zero.Id)).Outcome);
        Assert.Equal(ReturnServiceOutcome.Conflict,
            (await service.MarkRefundPendingAsync(request.ReturnRequest.Id, excessive.Id)).Outcome);
        Assert.Equal(ReturnServiceOutcome.Updated,
            (await service.MarkRefundPendingAsync(request.ReturnRequest.Id, valid.Id)).Outcome);
        Assert.Equal(valid.Id, request.ReturnRequest.RefundId);
    }

    [Fact]
    public async Task RefundReconciliationKeepsUncertainStatesPending_AndReopensFailedRefund()
    {
        await using var database = await TestDatabase.CreateAsync();
        var order = await database.AddOrderAsync(OrderStatuses.Delivered, quantity: 1);
        var service = CreateService(database.Context);
        var request = await service.RequestAsync(
            order.Id,
            "rma-refund-reconciliation",
            [new ReturnItemRequest(order.OrderItems.Single().Id, 1, ReturnReasonCodes.Defective)]);
        await MoveToInspectedAsync(service, request.ReturnRequest!.Id);
        var refund = await database.AddRefundAsync(order, 100m, RefundStatuses.Requested);

        Assert.Equal(ReturnServiceOutcome.Updated,
            (await service.MarkRefundPendingAsync(request.ReturnRequest.Id, refund.Id)).Outcome);
        foreach (var pendingStatus in new[] { RefundStatuses.Processing, RefundStatuses.Unknown })
        {
            refund.Status = pendingStatus;
            await database.Context.SaveChangesAsync();
            var pending = await service.ReconcileRefundAsync(request.ReturnRequest.Id);
            Assert.Equal(ReturnServiceOutcome.Replayed, pending.Outcome);
            Assert.Equal(ReturnRequestStatuses.RefundPending, request.ReturnRequest.Status);
        }

        refund.Status = RefundStatuses.Failed;
        refund.FailureCode = "provider-declined";
        await database.Context.SaveChangesAsync();
        var failed = await service.ReconcileRefundAsync(request.ReturnRequest.Id);
        var failedReplay = await service.ReconcileRefundAsync(request.ReturnRequest.Id);

        Assert.Equal(ReturnServiceOutcome.Updated, failed.Outcome);
        Assert.Equal(ReturnServiceOutcome.Replayed, failedReplay.Outcome);
        Assert.Equal(ReturnRequestStatuses.Inspected, request.ReturnRequest.Status);

        var retryRefund = await database.AddRefundAsync(order, 100m, RefundStatuses.Requested);
        var rebound = await service.MarkRefundPendingAsync(request.ReturnRequest.Id, retryRefund.Id);
        Assert.Equal(ReturnServiceOutcome.Updated, rebound.Outcome);
        Assert.Equal(retryRefund.Id, request.ReturnRequest.RefundId);
        Assert.Equal(ReturnRequestStatuses.RefundPending, request.ReturnRequest.Status);
    }

    [Fact]
    public async Task QuantityCapacityUsesLongArithmeticAtIntegerBoundary()
    {
        await using var database = await TestDatabase.CreateAsync();
        var order = await database.AddOrderAsync(OrderStatuses.Delivered, int.MaxValue);
        var item = Assert.Single(order.OrderItems);
        var service = CreateService(database.Context);

        var first = await service.RequestAsync(
            order.Id,
            "rma-integer-boundary-1",
            [new ReturnItemRequest(item.Id, 1, ReturnReasonCodes.Defective)]);
        var overflowAttempt = await service.RequestAsync(
            order.Id,
            "rma-integer-boundary-2",
            [new ReturnItemRequest(item.Id, int.MaxValue, ReturnReasonCodes.Defective)]);

        Assert.Equal(ReturnServiceOutcome.Created, first.Outcome);
        Assert.Equal(ReturnServiceOutcome.Conflict, overflowAttempt.Outcome);
    }

    [Fact]
    public async Task Request_JoinsAmbientTransaction_AndCallerOwnsRollback()
    {
        await using var database = await TestDatabase.CreateAsync();
        var order = await database.AddOrderAsync(OrderStatuses.Delivered, quantity: 1);
        var orderItemId = order.OrderItems.Single().Id;
        await using var transaction = await database.Context.Database.BeginTransactionAsync();

        var result = await CreateService(database.Context).RequestAsync(
            order.Id,
            "ambient-return",
            [new ReturnItemRequest(orderItemId, 1, ReturnReasonCodes.Defective)]);

        Assert.Equal(ReturnServiceOutcome.Created, result.Outcome);
        Assert.Same(transaction, database.Context.Database.CurrentTransaction);

        await transaction.RollbackAsync();
        database.Context.ChangeTracker.Clear();

        Assert.Empty(await database.Context.ReturnRequests.ToListAsync());
    }

    [Fact]
    public async Task Transition_JoinsAmbientTransaction_AndCallerOwnsRollback()
    {
        await using var database = await TestDatabase.CreateAsync();
        var order = await database.AddOrderAsync(OrderStatuses.Delivered, quantity: 1);
        var service = CreateService(database.Context);
        var created = await service.RequestAsync(
            order.Id,
            "ambient-return-transition",
            [new ReturnItemRequest(
                order.OrderItems.Single().Id,
                1,
                ReturnReasonCodes.Defective)]);
        await using var transaction = await database.Context.Database.BeginTransactionAsync();

        var result = await service.TransitionAsync(
            created.ReturnRequest!.Id,
            ReturnRequestStatuses.Approved);

        Assert.Equal(ReturnServiceOutcome.Updated, result.Outcome);
        Assert.Same(transaction, database.Context.Database.CurrentTransaction);

        await transaction.RollbackAsync();
        database.Context.ChangeTracker.Clear();

        Assert.Equal(
            ReturnRequestStatuses.Requested,
            await database.Context.ReturnRequests
                .Where(request => request.Id == created.ReturnRequest.Id)
                .Select(request => request.Status)
                .SingleAsync());
    }

    private static async Task MoveToInspectedAsync(ReturnService service, long returnRequestId)
    {
        await service.TransitionAsync(returnRequestId, ReturnRequestStatuses.Approved);
        await service.TransitionAsync(returnRequestId, ReturnRequestStatuses.Received);
        await service.TransitionAsync(returnRequestId, ReturnRequestStatuses.Inspected);
    }

    private static ReturnService CreateService(AutoPartsDbContext context) =>
        new(context, new FixedTimeProvider(Now));

    private static DbContextOptions<AutoPartsDbContext> BuildOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AutoPartsDbContext>()
            .UseSqlite(connection)
            .ReplaceService<IModelCustomizer, ReturnTestModelCustomizer>()
            .Options;

    private sealed class ReturnTestModelCustomizer(ModelCustomizerDependencies dependencies)
        : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);

            modelBuilder.Entity<ReturnRequest>()
                .HasOne(request => request.Order)
                .WithMany()
                .HasForeignKey(request => request.OrderId)
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<ReturnRequest>()
                .HasMany(request => request.Items)
                .WithOne(item => item.ReturnRequest)
                .HasForeignKey(item => item.ReturnRequestId)
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<ReturnRequest>()
                .HasOne(request => request.Refund)
                .WithOne()
                .HasForeignKey<ReturnRequest>(request => request.RefundId)
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<ReturnItem>()
                .HasOne(item => item.OrderItem)
                .WithMany()
                .HasForeignKey(item => item.OrderItemId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestDatabase(AutoPartsDbContext context, SqliteConnection connection)
        {
            Context = context;
            _connection = connection;
        }

        public AutoPartsDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new AutoPartsDbContext(BuildOptions(connection));
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(context, connection);
        }

        public async Task<Order> AddOrderAsync(string status, int quantity)
        {
            var order = NewOrder(status, quantity);
            Context.Orders.Add(order);
            await Context.SaveChangesAsync();
            return order;
        }

        public async Task<Refund> AddRefundAsync(
            Order order,
            decimal amount,
            string status,
            string? providerRefundId = null,
            DateTime? completedAt = null,
            string currency = "TRY")
        {
            var refund = new Refund
            {
                PaymentId = order.Payment!.Id,
                Payment = order.Payment,
                Provider = "iyzico",
                IdempotencyKey = $"refund-rma-{Guid.NewGuid():N}",
                Status = status,
                Amount = amount,
                Currency = currency,
                ProviderRefundId = providerRefundId,
                RequestedAt = Now.UtcDateTime,
                UpdatedAt = Now.UtcDateTime,
                CompletedAt = completedAt
            };
            Context.Refunds.Add(refund);
            await Context.SaveChangesAsync();
            return refund;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class SharedTestDatabase : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly SqliteConnection _keeper;

        private SharedTestDatabase(string connectionString, SqliteConnection keeper)
        {
            _connectionString = connectionString;
            _keeper = keeper;
        }

        public static async Task<SharedTestDatabase> CreateAsync()
        {
            var connectionString =
                $"Data Source=file:rma-{Guid.NewGuid():N}?mode=memory&cache=shared;Default Timeout=5;Pooling=False";
            var keeper = new SqliteConnection(connectionString);
            await keeper.OpenAsync();
            await using var context = new AutoPartsDbContext(BuildOptions(keeper));
            await context.Database.EnsureCreatedAsync();
            return new SharedTestDatabase(connectionString, keeper);
        }

        public AutoPartsDbContext CreateContext()
        {
            var connection = new SqliteConnection(_connectionString);
            return new AutoPartsDbContext(BuildOptions(connection));
        }

        public async Task<Order> AddOrderAsync(int quantity)
        {
            await using var context = CreateContext();
            var order = NewOrder(OrderStatuses.Delivered, quantity);
            context.Orders.Add(order);
            await context.SaveChangesAsync();
            return order;
        }

        public async ValueTask DisposeAsync() => await _keeper.DisposeAsync();
    }

    private static Order NewOrder(string status, int quantity)
    {
        var order = new Order
        {
            OrderNumber = $"RMA-{Guid.NewGuid():N}",
            CustomerName = "RMA Test Customer",
            CustomerEmail = "rma@example.com",
            CustomerPhone = "+905551112233",
            ShippingAddress = "Test Mahallesi Test Sokak No 1",
            City = "Istanbul",
            PostalCode = "34000",
            TotalAmount = quantity * 100m,
            Status = status,
            OrderDate = Now.UtcDateTime
        };
        order.Payment = new Payment
        {
            Order = order,
            Provider = "iyzico",
            Method = "Card",
            Status = PaymentStatuses.Paid,
            Amount = quantity * 100m,
            Currency = "TRY",
            IdempotencyKey = $"payment-rma-{Guid.NewGuid():N}",
            ProviderPaymentId = $"payment-provider-{Guid.NewGuid():N}",
            CreatedAt = Now.UtcDateTime,
            UpdatedAt = Now.UtcDateTime,
            PaidAt = Now.UtcDateTime
        };
        order.OrderItems.Add(new OrderItem
        {
            ProductId = 1,
            Quantity = quantity,
            Price = 100m
        });
        return order;
    }
}

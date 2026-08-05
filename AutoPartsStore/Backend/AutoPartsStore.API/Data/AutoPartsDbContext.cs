using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using AutoPartsStore.API.Models;
using System.Text.Json;

namespace AutoPartsStore.API.Data
{
    public class AutoPartsDbContext : DbContext
    {
        public AutoPartsDbContext(DbContextOptions<AutoPartsDbContext> options) : base(options)
        {
        }

        public DbSet<Category> Categories { get; set; }
        public DbSet<Product> Products { get; set; }
        public DbSet<Brand> Brands { get; set; } // Vehicle Brands (Araç Markaları)
        public DbSet<PartBrand> PartBrands { get; set; } // Part Brands (Parça Markaları)
        public DbSet<Order> Orders { get; set; }
        public DbSet<OrderItem> OrderItems { get; set; }
        public DbSet<Payment> Payments { get; set; }
        public DbSet<PaymentEvent> PaymentEvents { get; set; }
        public DbSet<PaymentAttempt> PaymentAttempts { get; set; }
        public DbSet<PaymentTransaction> PaymentTransactions { get; set; }
        public DbSet<Refund> Refunds { get; set; }
        public DbSet<OutboxMessage> OutboxMessages { get; set; }
        public DbSet<Shipment> Shipments { get; set; }
        public DbSet<ShipmentItem> ShipmentItems { get; set; }
        public DbSet<ReturnRequest> ReturnRequests { get; set; }
        public DbSet<ReturnItem> ReturnItems { get; set; }
        public DbSet<InventoryReservation> InventoryReservations { get; set; }
        public DbSet<InventoryReservationItem> InventoryReservationItems { get; set; }
        public DbSet<HostedCheckoutSession> HostedCheckoutSessions { get; set; }
        public DbSet<AdminAuditEvent> AdminAuditEvents { get; set; }
        public DbSet<AdminAuditIntent> AdminAuditIntents { get; set; }
        public DbSet<VehicleMake> VehicleMakes { get; set; }
        public DbSet<VehicleModel> VehicleModels { get; set; }
        public DbSet<VehicleGeneration> VehicleGenerations { get; set; }
        public DbSet<VehicleEngine> VehicleEngines { get; set; }
        public DbSet<Vehicle> Vehicles { get; set; }
        public DbSet<ProductFitment> ProductFitments { get; set; }
        public DbSet<ProductIdentifier> ProductIdentifiers { get; set; }
        public DbSet<Supplier> Suppliers { get; set; }
        public DbSet<SupplierOffer> SupplierOffers { get; set; }
        public DbSet<DealerApplication> DealerApplications { get; set; }
        public DbSet<CustomerGroup> CustomerGroups { get; set; }
        public DbSet<PriceList> PriceLists { get; set; }
        public DbSet<PriceRule> PriceRules { get; set; }
        public DbSet<BulkQuoteRequest> BulkQuoteRequests { get; set; }
        public DbSet<BulkQuoteLine> BulkQuoteLines { get; set; }
        public DbSet<SalesChannel> SalesChannels { get; set; }
        public DbSet<ChannelListing> ChannelListings { get; set; }
        public DbSet<ChannelOrderLink> ChannelOrderLinks { get; set; }
        public DbSet<ChannelInboxEvent> ChannelInboxEvents { get; set; }
        public DbSet<UserVehicle> UserVehicles { get; set; }
        public DbSet<MaintenanceRecord> MaintenanceRecords { get; set; }
        public DbSet<MaintenanceRecordItem> MaintenanceRecordItems { get; set; }
        public DbSet<MaintenanceReminder> MaintenanceReminders { get; set; }
        public DbSet<LegalDocumentVersion> LegalDocumentVersions { get; set; }
        public DbSet<LegalAcceptance> LegalAcceptances { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<Review> Reviews { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ConfigureFitmentModel();
            modelBuilder.ConfigureHostedCheckout();
            modelBuilder.ConfigureSupplierSourcing();
            modelBuilder.ConfigureB2bPricing();
            modelBuilder.ConfigureBulkQuotes();
            modelBuilder.ConfigureSalesChannels();
            modelBuilder.ConfigureMaintenance();
            modelBuilder.ConfigureLegalDocuments();

            // Category configuration
            modelBuilder.Entity<Category>()
                .HasMany(c => c.SubCategories)
                .WithOne(c => c.ParentCategory)
                .HasForeignKey(c => c.ParentCategoryId)
                .OnDelete(DeleteBehavior.Restrict);

            // Product configuration
            modelBuilder.Entity<Product>()
                .Property(p => p.Price)
                .HasPrecision(18, 2);

            modelBuilder.Entity<Product>()
                .Property(p => p.OldPrice)
                .HasPrecision(18, 2);

            var additionalImagesProperty = modelBuilder.Entity<Product>()
                .Property(p => p.AdditionalImages)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions)null!),
                    v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions)null!) ?? new List<string>()
                );

            additionalImagesProperty.Metadata.SetValueComparer(
                new ValueComparer<List<string>>(
                    (left, right) => left != null && right != null && left.SequenceEqual(right),
                    values => values.Aggregate(0, (hash, value) => HashCode.Combine(hash, value.GetHashCode())),
                    values => values.ToList()));

            // Order configuration
            modelBuilder.Entity<Order>()
                .Property(o => o.TotalAmount)
                .HasPrecision(18, 2);

            modelBuilder.Entity<Order>()
                .HasIndex(o => o.OrderNumber)
                .IsUnique();

            modelBuilder.Entity<Order>()
                .HasIndex(o => o.CheckoutIdempotencyKey)
                .IsUnique()
                .HasFilter("[CheckoutIdempotencyKey] IS NOT NULL");

            modelBuilder.Entity<OrderItem>()
                .Property(oi => oi.Price)
                .HasPrecision(18, 2);

            modelBuilder.Entity<OrderItem>()
                .HasOne(orderItem => orderItem.Product)
                .WithMany()
                .HasForeignKey(orderItem => orderItem.ProductId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Payment>()
                .Property(payment => payment.Amount)
                .HasPrecision(18, 2);

            modelBuilder.Entity<Payment>()
                .HasIndex(payment => payment.IdempotencyKey)
                .IsUnique();

            modelBuilder.Entity<Payment>()
                .HasIndex(payment => new { payment.Provider, payment.ProviderPaymentId })
                .IsUnique()
                .HasFilter("[ProviderPaymentId] IS NOT NULL");

            modelBuilder.Entity<PaymentEvent>()
                .HasIndex(paymentEvent => new
                {
                    paymentEvent.Provider,
                    paymentEvent.ProviderEventId
                })
                .IsUnique();

            modelBuilder.Entity<PaymentAttempt>()
                .HasIndex(attempt => attempt.IdempotencyKey)
                .IsUnique();

            modelBuilder.Entity<PaymentAttempt>()
                .HasIndex(attempt => new { attempt.Provider, attempt.ConversationId })
                .IsUnique();

            modelBuilder.Entity<PaymentTransaction>()
                .Property(transaction => transaction.PaidAmount)
                .HasPrecision(18, 2);

            modelBuilder.Entity<PaymentTransaction>()
                .Property(transaction => transaction.RefundedAmount)
                .HasPrecision(18, 2);

            modelBuilder.Entity<PaymentTransaction>()
                .HasIndex(transaction => new
                {
                    transaction.Provider,
                    transaction.ProviderTransactionId
                })
                .IsUnique();

            modelBuilder.Entity<Refund>()
                .Property(refund => refund.Amount)
                .HasPrecision(18, 2);

            modelBuilder.Entity<Refund>()
                .HasIndex(refund => refund.IdempotencyKey)
                .IsUnique();

            modelBuilder.Entity<Refund>()
                .HasIndex(refund => new { refund.Provider, refund.ProviderRefundId })
                .IsUnique()
                .HasFilter("[ProviderRefundId] IS NOT NULL");

            modelBuilder.Entity<OutboxMessage>()
                .HasIndex(message => message.EventId)
                .IsUnique();

            modelBuilder.Entity<Shipment>()
                .HasIndex(shipment => shipment.IdempotencyKey)
                .IsUnique();

            modelBuilder.Entity<Shipment>()
                .HasIndex(shipment => new { shipment.Carrier, shipment.TrackingNumber })
                .IsUnique()
                .HasFilter("[Carrier] IS NOT NULL AND [TrackingNumber] IS NOT NULL");

            modelBuilder.Entity<ReturnRequest>()
                .HasIndex(request => request.IdempotencyKey)
                .IsUnique();

            modelBuilder.Entity<ReturnRequest>()
                .HasIndex(request => request.RefundId)
                .IsUnique()
                .HasFilter("[RefundId] IS NOT NULL");

            modelBuilder.Entity<ReturnRequest>()
                .HasIndex(request => request.ExternalRefundRequestReference)
                .IsUnique()
                .HasFilter("[ExternalRefundRequestReference] IS NOT NULL");

            modelBuilder.Entity<ReturnRequest>()
                .HasIndex(request => request.ExternalRefundConfirmationReference)
                .IsUnique()
                .HasFilter("[ExternalRefundConfirmationReference] IS NOT NULL");

            modelBuilder.Entity<Order>()
                .HasOne(order => order.Payment)
                .WithOne(payment => payment.Order)
                .HasForeignKey<Payment>(payment => payment.OrderId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Payment>()
                .HasMany(payment => payment.Events)
                .WithOne(paymentEvent => paymentEvent.Payment)
                .HasForeignKey(paymentEvent => paymentEvent.PaymentId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<Payment>()
                .HasMany(payment => payment.Attempts)
                .WithOne(attempt => attempt.Payment)
                .HasForeignKey(attempt => attempt.PaymentId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Payment>()
                .HasMany(payment => payment.Transactions)
                .WithOne(transaction => transaction.Payment)
                .HasForeignKey(transaction => transaction.PaymentId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<PaymentTransaction>()
                .HasOne(transaction => transaction.OrderItem)
                .WithMany()
                .HasForeignKey(transaction => transaction.OrderItemId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Payment>()
                .HasMany(payment => payment.Refunds)
                .WithOne(refund => refund.Payment)
                .HasForeignKey(refund => refund.PaymentId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Refund>()
                .HasOne(refund => refund.PaymentTransaction)
                .WithMany()
                .HasForeignKey(refund => refund.PaymentTransactionId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Shipment>()
                .HasOne(shipment => shipment.Order)
                .WithMany()
                .HasForeignKey(shipment => shipment.OrderId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Shipment>()
                .HasMany(shipment => shipment.Items)
                .WithOne(item => item.Shipment)
                .HasForeignKey(item => item.ShipmentId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ShipmentItem>()
                .HasOne(item => item.OrderItem)
                .WithMany()
                .HasForeignKey(item => item.OrderItemId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ReturnRequest>()
                .HasOne(request => request.Order)
                .WithMany()
                .HasForeignKey(request => request.OrderId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ReturnRequest>()
                .HasOne(request => request.Refund)
                .WithOne()
                .HasForeignKey<ReturnRequest>(request => request.RefundId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ReturnRequest>()
                .HasMany(request => request.Items)
                .WithOne(item => item.ReturnRequest)
                .HasForeignKey(item => item.ReturnRequestId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ReturnItem>()
                .HasOne(item => item.OrderItem)
                .WithMany()
                .HasForeignKey(item => item.OrderItemId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<InventoryReservation>()
                .ToTable("InventoryReservations", table =>
                {
                    table.HasCheckConstraint(
                        "CK_InventoryReservations_Status",
                        "[Status] IN ('Active', 'Committed', 'Released', 'Expired')");
                    table.HasCheckConstraint(
                        "CK_InventoryReservations_CommitLink",
                        "([Status] = 'Committed' AND [CommittedOrderId] IS NOT NULL) OR " +
                        "([Status] <> 'Committed' AND [CommittedOrderId] IS NULL)");
                    table.HasCheckConstraint(
                        "CK_InventoryReservations_Timestamps",
                        "[ExpiresAt] > [CreatedAt] AND [UpdatedAt] >= [CreatedAt]");
                });

            modelBuilder.Entity<InventoryReservation>()
                .HasIndex(reservation => reservation.IdempotencyKey)
                .IsUnique();

            modelBuilder.Entity<InventoryReservation>()
                .HasIndex(reservation => reservation.CommittedOrderId)
                .IsUnique()
                .HasFilter("[CommittedOrderId] IS NOT NULL");

            modelBuilder.Entity<InventoryReservation>()
                .HasOne(reservation => reservation.CommittedOrder)
                .WithOne()
                .HasForeignKey<InventoryReservation>(reservation => reservation.CommittedOrderId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<InventoryReservation>()
                .HasMany(reservation => reservation.Items)
                .WithOne(item => item.InventoryReservation)
                .HasForeignKey(item => item.InventoryReservationId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<InventoryReservationItem>()
                .ToTable("InventoryReservationItems", table =>
                    table.HasCheckConstraint(
                        "CK_InventoryReservationItems_Quantity",
                        "[Quantity] > 0"));

            modelBuilder.Entity<InventoryReservationItem>()
                .HasOne(item => item.Product)
                .WithMany()
                .HasForeignKey(item => item.ProductId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<AdminAuditEvent>()
                .ToTable("AdminAuditEvents", table =>
                {
                    table.HasCheckConstraint(
                        "CK_AdminAuditEvents_PositiveIdentity",
                        "[Sequence] > 0 AND [ActorUserId] > 0 AND [AggregateId] > 0");
                    table.HasCheckConstraint(
                        "CK_AdminAuditEvents_Outcome",
                        "[Outcome] IN ('succeeded', 'rejected', 'failed', 'replayed')");
                    table.HasCheckConstraint(
                        "CK_AdminAuditEvents_Role",
                        "[ActorRole] IN ('finance', 'warehouse', 'catalog', 'support', 'superadmin', 'Admin')");
                });

            modelBuilder.Entity<AdminAuditIntent>()
                .ToTable("AdminAuditIntents", table =>
                {
                    table.HasCheckConstraint(
                        "CK_AdminAuditIntents_PositiveIdentity",
                        "[ActorUserId] > 0 AND [AggregateId] > 0 AND [AttemptCount] >= 0");
                    table.HasCheckConstraint(
                        "CK_AdminAuditIntents_Outcome",
                        "[Outcome] IN ('succeeded', 'rejected', 'failed', 'replayed')");
                    table.HasCheckConstraint(
                        "CK_AdminAuditIntents_Status",
                        "[Status] IN ('pending', 'processing', 'succeeded', 'failed')");
                    table.HasCheckConstraint(
                        "CK_AdminAuditIntents_Role",
                        "[ActorRole] IN ('finance', 'warehouse', 'catalog', 'support', 'superadmin', 'Admin')");
                    table.HasCheckConstraint(
                        "CK_AdminAuditIntents_Lease",
                        "([Status] = 'processing' AND [LeaseId] IS NOT NULL AND [LeaseExpiresAtUtc] IS NOT NULL) OR " +
                        "([Status] <> 'processing' AND [LeaseId] IS NULL AND [LeaseExpiresAtUtc] IS NULL)");
                });

            // Seed data
            SeedData(modelBuilder);

            // Keep the legacy database column types aligned with the existing migration snapshot.
            // API validation still uses Data Annotations; schema tightening needs its own audited migration.
            foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                         .Where(entityType =>
                             entityType.ClrType != typeof(Payment) &&
                             entityType.ClrType != typeof(PaymentEvent) &&
                             entityType.ClrType != typeof(PaymentAttempt) &&
                             entityType.ClrType != typeof(PaymentTransaction) &&
                             entityType.ClrType != typeof(Refund) &&
                             entityType.ClrType != typeof(OutboxMessage) &&
                             entityType.ClrType != typeof(Shipment) &&
                             entityType.ClrType != typeof(ShipmentItem) &&
                             entityType.ClrType != typeof(ReturnRequest) &&
                             entityType.ClrType != typeof(ReturnItem) &&
                             entityType.ClrType != typeof(InventoryReservation) &&
                             entityType.ClrType != typeof(InventoryReservationItem) &&
                             entityType.ClrType != typeof(HostedCheckoutSession) &&
                             entityType.ClrType != typeof(AdminAuditEvent) &&
                             entityType.ClrType != typeof(AdminAuditIntent) &&
                             entityType.ClrType != typeof(VehicleMake) &&
                             entityType.ClrType != typeof(VehicleModel) &&
                             entityType.ClrType != typeof(VehicleGeneration) &&
                             entityType.ClrType != typeof(VehicleEngine) &&
                             entityType.ClrType != typeof(Vehicle) &&
                             entityType.ClrType != typeof(ProductFitment) &&
                             entityType.ClrType != typeof(ProductIdentifier) &&
                             entityType.ClrType != typeof(Supplier) &&
                             entityType.ClrType != typeof(SupplierOffer) &&
                             entityType.ClrType != typeof(DealerApplication) &&
                             entityType.ClrType != typeof(CustomerGroup) &&
                             entityType.ClrType != typeof(PriceList) &&
                             entityType.ClrType != typeof(PriceRule) &&
                             entityType.ClrType != typeof(BulkQuoteRequest) &&
                             entityType.ClrType != typeof(BulkQuoteLine) &&
                             entityType.ClrType != typeof(SalesChannel) &&
                             entityType.ClrType != typeof(ChannelListing) &&
                             entityType.ClrType != typeof(ChannelOrderLink) &&
                             entityType.ClrType != typeof(ChannelInboxEvent) &&
                             entityType.ClrType != typeof(UserVehicle) &&
                             entityType.ClrType != typeof(MaintenanceRecord) &&
                             entityType.ClrType != typeof(MaintenanceRecordItem) &&
                             entityType.ClrType != typeof(MaintenanceReminder) &&
                             entityType.ClrType != typeof(LegalDocumentVersion) &&
                             entityType.ClrType != typeof(LegalAcceptance)))
            {
                foreach (var property in entityType.GetProperties()
                             .Where(property => property.ClrType == typeof(string)))
                {
                    property.SetMaxLength(null);
                }
            }

            modelBuilder.Entity<Order>()
                .Property(order => order.OrderNumber)
                .HasMaxLength(50);

            modelBuilder.Entity<Order>()
                .Property(order => order.CheckoutIdempotencyKey)
                .HasMaxLength(100);

            modelBuilder.Entity<User>()
                .Property(user => user.Email)
                .HasMaxLength(200);

            modelBuilder.Entity<User>()
                .HasIndex(user => user.Email)
                .IsUnique();
        }

        private void SeedData(ModelBuilder modelBuilder)
        {
            // Seed Categories
            var categories = new[]
            {
                new Category { Id = 1, Name = "Motor Parçaları", Slug = "motor-parcalari", Description = "Motor yağları ve diğer tüm motor parçaları", ImageUrl = "/images/categories/motor.jpg" },
                new Category { Id = 2, Name = "Fren Sistemi", Slug = "fren-sistemi", Description = "Fren diskleri, balatalar ve hidrolik parçalar", ImageUrl = "/images/categories/fren.jpg" },
                new Category { Id = 3, Name = "Filtreler", Slug = "filtreler", Description = "Yağ, hava, polen ve yakıt filtreleri", ImageUrl = "/images/categories/filtre.jpg" },
                new Category { Id = 4, Name = "Elektrik Aksamı", Slug = "elektrik-aksami", Description = "Akü, alternatör, marş motoru", ImageUrl = "/images/categories/elektrik.jpg" },
                new Category { Id = 5, Name = "Süspansiyon", Slug = "suspansiyon", Description = "Amortisörler, salıncaklar ve rotlar", ImageUrl = "/images/categories/suspansiyon.jpg" },
                new Category { Id = 6, Name = "Şanzıman", Slug = "sanziman", Description = "Şanzıman yağları ve parçaları", ImageUrl = "/images/categories/sanziman.jpg" },
                new Category { Id = 7, Name = "Karoseri", Slug = "karoseri", Description = "Kaput, çamurluk ve tampon parçaları", ImageUrl = "/images/categories/karoseri.jpg" },
            };

            modelBuilder.Entity<Category>().HasData(categories);

            // Seed Brands (Vehicle Brands - Araç Markaları)
            var brands = new[]
            {
                new Brand { Id = 1, Name = "Mercedes-Benz", Slug = "mercedes-benz" },
                new Brand { Id = 2, Name = "BMW", Slug = "bmw" },
                new Brand { Id = 3, Name = "Audi", Slug = "audi" },
                new Brand { Id = 4, Name = "Volkswagen", Slug = "volkswagen" },
                new Brand { Id = 5, Name = "Ford", Slug = "ford" },
                new Brand { Id = 6, Name = "Renault", Slug = "renault" },
                new Brand { Id = 7, Name = "Fiat", Slug = "fiat" },
                new Brand { Id = 8, Name = "Peugeot", Slug = "peugeot" },
                new Brand { Id = 9, Name = "Citroen", Slug = "citroen" },
                new Brand { Id = 10, Name = "Opel", Slug = "opel" },
                new Brand { Id = 11, Name = "Toyota", Slug = "toyota" },
                new Brand { Id = 12, Name = "Honda", Slug = "honda" },
                new Brand { Id = 13, Name = "Hyundai", Slug = "hyundai" },
                new Brand { Id = 14, Name = "Kia", Slug = "kia" },
                new Brand { Id = 15, Name = "Nissan", Slug = "nissan" },
                new Brand { Id = 16, Name = "Mazda", Slug = "mazda" },
                new Brand { Id = 17, Name = "Chevrolet", Slug = "chevrolet" },
                new Brand { Id = 18, Name = "Dacia", Slug = "dacia" },
                new Brand { Id = 19, Name = "Skoda", Slug = "skoda" },
                new Brand { Id = 20, Name = "Seat", Slug = "seat" },
                new Brand { Id = 21, Name = "Volvo", Slug = "volvo" },
                new Brand { Id = 22, Name = "Land Rover", Slug = "land-rover" },
                new Brand { Id = 23, Name = "Jeep", Slug = "jeep" },
                new Brand { Id = 24, Name = "Mitsubishi", Slug = "mitsubishi" },
                new Brand { Id = 25, Name = "Suzuki", Slug = "suzuki" },
            };

            modelBuilder.Entity<Brand>().HasData(brands);

            // Seed Part Brands (Parça Markaları)
            var partBrands = new[]
            {
                new PartBrand { Id = 1, Name = "Bosch", Slug = "bosch" },
                new PartBrand { Id = 2, Name = "Mann-Filter", Slug = "mann-filter" },
                new PartBrand { Id = 3, Name = "Brembo", Slug = "brembo" },
                new PartBrand { Id = 4, Name = "Shell", Slug = "shell" },
                new PartBrand { Id = 5, Name = "Castrol", Slug = "castrol" },
                new PartBrand { Id = 6, Name = "Mobil", Slug = "mobil" },
                new PartBrand { Id = 7, Name = "Varta", Slug = "varta" },
                new PartBrand { Id = 8, Name = "Sachs", Slug = "sachs" },
                new PartBrand { Id = 9, Name = "Total", Slug = "total" },
                new PartBrand { Id = 10, Name = "Petrol Ofisi", Slug = "petrol-ofisi" },
                new PartBrand { Id = 11, Name = "ATE", Slug = "ate" },
                new PartBrand { Id = 12, Name = "TRW", Slug = "trw" },
                new PartBrand { Id = 13, Name = "Mahle", Slug = "mahle" },
                new PartBrand { Id = 14, Name = "Knecht", Slug = "knecht" },
                new PartBrand { Id = 15, Name = "Valeo", Slug = "valeo" },
                new PartBrand { Id = 16, Name = "Monroe", Slug = "monroe" },
                new PartBrand { Id = 17, Name = "Lemförder", Slug = "lemforder" },
            };

            modelBuilder.Entity<PartBrand>().HasData(partBrands);

            // Seed Products with real auto parts data for vehicle brands
            var products = new[]
            {
                // Mercedes-Benz Products (BrandId = 1)
                new Product
                {
                    Id = 1,
                    Name = "Motor Yağ Filtresi - Bosch",
                    Description = "Orijinal kalite yağ filtresi (Bosch üretimi). Mercedes-Benz W205, W213 modelleri için uyumlu. OM651 motor. Premium filtrasyon teknolojisi. 25.000 km kullanım ömrü.",
                    PartNumber = "MB-A6511800009",
                    Price = 245.00m,
                    OldPrice = 315.00m,
                    Stock = 45,
                    ImageUrl = "https://images.unsplash.com/photo-1615906655593-ad0386982a0f?w=500&q=80",
                    Rating = 4.9,
                    ReviewCount = 187,
                    DiscountPercentage = 22,
                    BadgeText = "Orijinal Kalite",
                    IsFeatured = true,
                    IsNew = false,
                    CategoryId = 3,
                    BrandId = 1, // Mercedes-Benz (Araç Markası)
                    PartBrandId = 1 // Bosch (Parça Markası)
                },
                new Product
                {
                    Id = 2,
                    Name = "BMW 3 Serisi Fren Disk Takımı",
                    Description = "Ön fren disk seti (Brembo üretimi). F30, F31, F34 modelleri için. 348mm çap, havalandırmalı tasarım. ECE R90 onaylı. Yüksek performans frenleme.",
                    PartNumber = "BMW-34116792217",
                    Price = 1850.00m,
                    OldPrice = 2200.00m,
                    Stock = 22,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.9,
                    ReviewCount = 164,
                    DiscountPercentage = 16,
                    BadgeText = "Orijinal Kalite",
                    IsFeatured = true,
                    IsNew = false,
                    CategoryId = 2,
                    BrandId = 2, // BMW (Araç Markası)
                    PartBrandId = 3 // Brembo (Parça Markası)
                },
                new Product
                {
                    Id = 3,
                    Name = "Mobil 1 ESP 0W-30",
                    Description = "Düşük küllenme özellikleri ile dizel partikül filtresi koruması. Mercedes-Benz, BMW, VW onaylı. Uzun motor ömrü sağlar.",
                    PartNumber = "MB-0W30-4L",
                    Price = 599.99m,
                    Stock = 28,
                    ImageUrl = "https://images.unsplash.com/photo-1619642751034-765dfdf7c58e?w=500&q=80",
                    Rating = 4.9,
                    ReviewCount = 142,
                    IsFeatured = false,
                    IsNew = true,
                    CategoryId = 1,
                    BrandId = 3, // Audi (Araç Markası)
                    PartBrandId = 6 // Mobil (Parça Markası)
                },
                new Product
                {
                    Id = 4,
                    Name = "Brembo Fren Diski",
                    Description = "Ön fren diski, havalandırmalı tasarım. Volkswagen Golf 7, Audi A3, Skoda Octavia uyumlu. 312mm çap, yüksek performans.",
                    PartNumber = "BR-09.9772.11",
                    Price = 722.50m,
                    OldPrice = 850.00m,
                    Stock = 18,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.6,
                    ReviewCount = 85,
                    DiscountPercentage = 15,
                    BadgeText = "%15 İndirim",
                    IsFeatured = true,
                    IsNew = false,
                    CategoryId = 2,
                    BrandId = 4, // Volkswagen (Araç Markası)
                    PartBrandId = 3 // Brembo (Parça Markası)
                },
                new Product
                {
                    Id = 5,
                    Name = "Bosch Fren Balatası",
                    Description = "Yüksek performanslı ön fren balata takımı. Az toz üreten formül. ECE R90 onaylı. Sessiz ve etkili frenleme.",
                    PartNumber = "BO-0986494396",
                    Price = 385.00m,
                    Stock = 42,
                    ImageUrl = "https://images.unsplash.com/photo-1619642751034-765dfdf7c58e?w=500&q=80",
                    Rating = 4.7,
                    ReviewCount = 103,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 2,
                    BrandId = 5, // Ford (Araç Markası)
                    PartBrandId = 1 // Bosch (Parça Markası)
                },
                new Product
                {
                    Id = 6,
                    Name = "Mann Yağ Filtresi",
                    Description = "Orijinal kalite yağ filtresi. W 712/75 model. Premium multi-grade filtrasyon teknolojisi. 25.000 km kullanım ömrü.",
                    PartNumber = "MN-W712/75",
                    Price = 89.99m,
                    Stock = 120,
                    ImageUrl = "https://images.unsplash.com/photo-1615906655593-ad0386982a0f?w=500&q=80",
                    Rating = 4.8,
                    ReviewCount = 215,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 3,
                    BrandId = 6, // Renault (Araç Markası)
                    PartBrandId = 2 // Mann-Filter (Parça Markası)
                },
                new Product
                {
                    Id = 7,
                    Name = "Bosch Hava Filtresi",
                    Description = "Motor hava filtresi. Yüksek filtrasyon performansı. %99.9 toz tutma kapasitesi. Yakıt tasarrufu sağlar.",
                    PartNumber = "BO-1457433529",
                    Price = 125.00m,
                    Stock = 85,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.6,
                    ReviewCount = 92,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 3,
                    BrandId = 5, // Ford (Araç Markası)
                    PartBrandId = 1 // Bosch (Parça Markası)
                },
                new Product
                {
                    Id = 8,
                    Name = "Mann Polen Filtresi",
                    Description = "Aktif karbonlu kabin hava filtresi. Alerjenleri %99 filtreler. Koku ve bakteri önleyici. Sağlıklı kabin havası.",
                    PartNumber = "MN-CUK2545",
                    Price = 165.00m,
                    Stock = 67,
                    ImageUrl = "https://images.unsplash.com/photo-1619642751034-765dfdf7c58e?w=500&q=80",
                    Rating = 4.7,
                    ReviewCount = 78,
                    IsFeatured = false,
                    IsNew = true,
                    CategoryId = 3,
                    BrandId = 6, // Renault (Araç Markası)
                    PartBrandId = 2 // Mann-Filter (Parça Markası)
                },
                new Product
                {
                    Id = 9,
                    Name = "Varta 60AH Akü",
                    Description = "60AH 540A Blue Dynamic akü. 3 yıl garanti. Start-Stop teknolojisi. Uzun ömürlü ve güvenilir.",
                    PartNumber = "VR-D59",
                    Price = 1299.99m,
                    OldPrice = 1450.00m,
                    Stock = 22,
                    ImageUrl = "https://images.unsplash.com/photo-1593941707882-a5bba14938c7?w=500&q=80",
                    Rating = 4.8,
                    ReviewCount = 246,
                    DiscountPercentage = 10,
                    BadgeText = "Çok Satan",
                    IsFeatured = true,
                    IsNew = false,
                    CategoryId = 4,
                    BrandId = 7, // Fiat (Araç Markası)
                    PartBrandId = 7 // Varta (Parça Markası)
                },
                new Product
                {
                    Id = 10,
                    Name = "Bosch S5 77AH Akü",
                    Description = "77AH 780A Silver Plus akü. Üstün performans. Soğuk havada güçlü çalıştırma. 4 yıl garanti.",
                    PartNumber = "BO-S5008",
                    Price = 1849.99m,
                    Stock = 15,
                    ImageUrl = "https://images.unsplash.com/photo-1605559424843-9e4c228bf1c2?w=500&q=80",
                    Rating = 4.9,
                    ReviewCount = 187,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 4,
                    BrandId = 5, // Ford (Araç Markası)
                    PartBrandId = 1 // Bosch (Parça Markası)
                },
                new Product
                {
                    Id = 11,
                    Name = "Sachs Amortisör",
                    Description = "Ön amortisör. OE kalitesinde üretim. Gaz basınçlı teknoloji. Konfor ve güvenlik için tasarlandı.",
                    PartNumber = "SA-311867",
                    Price = 875.00m,
                    Stock = 12,
                    ImageUrl = "https://images.unsplash.com/photo-1625047509248-ec889cbff17f?w=500&q=80",
                    Rating = 4.7,
                    ReviewCount = 64,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 5,
                    BrandId = 8, // Peugeot (Araç Markası)
                    PartBrandId = 8 // Sachs (Parça Markası)
                },
                new Product
                {
                    Id = 12,
                    Name = "Bosch Fren Balatası Takımı",
                    Description = "Arka fren balata seti, 4 adet. Seramik formül. Düşük gürültü, uzun ömür. ECE R90 onaylı.",
                    PartNumber = "BO-0986494662",
                    Price = 337.50m,
                    OldPrice = 450.00m,
                    Stock = 38,
                    ImageUrl = "https://images.unsplash.com/photo-1619642751034-765dfdf7c58e?w=500&q=80",
                    Rating = 4.5,
                    ReviewCount = 167,
                    DiscountPercentage = 25,
                    BadgeText = "%25 İndirim",
                    IsFeatured = true,
                    IsNew = false,
                    CategoryId = 2,
                    BrandId = 5, // Ford (Araç Markası)
                    PartBrandId = 1 // Bosch (Parça Markası)
                },
                new Product
                {
                    Id = 13,
                    Name = "Total Quartz 9000 5W-40",
                    Description = "Tam sentetik motor yağı. Aşırı koşullarda üstün koruma. VW 502.00/505.00, MB 229.5 onaylı.",
                    PartNumber = "TT-9000-5W40",
                    Price = 479.99m,
                    Stock = 44,
                    ImageUrl = "https://images.unsplash.com/photo-1615906655593-ad0386982a0f?w=500&q=80",
                    Rating = 4.7,
                    ReviewCount = 89,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 1,
                    BrandId = 9, // Citroen (Araç Markası)
                    PartBrandId = 9 // Total (Parça Markası)
                },
                new Product
                {
                    Id = 14,
                    Name = "Petrol Ofisi Maxima 5W-30",
                    Description = "Yerli üretim tam sentetik motor yağı. API SN Plus, ACEA C3. Yakıt ekonomisi sağlar.",
                    PartNumber = "PO-MAX-5W30",
                    Price = 419.99m,
                    OldPrice = 499.99m,
                    Stock = 62,
                    ImageUrl = "https://images.unsplash.com/photo-1615906655593-ad0386982a0f?w=500&q=80",
                    Rating = 4.6,
                    ReviewCount = 134,
                    DiscountPercentage = 16,
                    BadgeText = "Yerli Üretim",
                    IsFeatured = true,
                    IsNew = false,
                    CategoryId = 1,
                    BrandId = 10, // Opel (Araç Markası)
                    PartBrandId = 10 // Petrol Ofisi (Parça Markası)
                },
                new Product
                {
                    Id = 15,
                    Name = "ATE Fren Hidroliği",
                    Description = "DOT 4 fren hidrolik sıvısı. 1 litre. Yüksek kaynama noktası. ABS/ESP uyumlu.",
                    PartNumber = "AT-706202",
                    Price = 125.00m,
                    Stock = 95,
                    ImageUrl = "https://images.unsplash.com/photo-1615906655593-ad0386982a0f?w=500&q=80",
                    Rating = 4.8,
                    ReviewCount = 156,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 2,
                    BrandId = 4, // Volkswagen (Araç Markası)
                    PartBrandId = 11 // ATE (Parça Markası)
                },
                new Product
                {
                    Id = 16,
                    Name = "TRW Fren Diski Seti",
                    Description = "Arka fren disk seti, 2 adet. Ford Focus, Mondeo uyumlu. 278mm çap.",
                    PartNumber = "TRW-DF6363S",
                    Price = 645.00m,
                    OldPrice = 780.00m,
                    Stock = 24,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.6,
                    ReviewCount = 71,
                    DiscountPercentage = 17,
                    BadgeText = "%17 İndirim",
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 2,
                    BrandId = 5, // Ford (Araç Markası)
                    PartBrandId = 12 // TRW (Parça Markası)
                },
                new Product
                {
                    Id = 17,
                    Name = "Mahle Yakıt Filtresi",
                    Description = "Dizel yakıt filtresi. Su tutucu özellikli. Enjektör koruması sağlar.",
                    PartNumber = "MH-KL440",
                    Price = 145.00m,
                    Stock = 78,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.7,
                    ReviewCount = 98,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 3,
                    BrandId = 6, // Renault (Araç Markası)
                    PartBrandId = 13 // Mahle (Parça Markası)
                },
                new Product
                {
                    Id = 18,
                    Name = "Knecht Filtre Seti",
                    Description = "Komple filtre bakım seti: yağ, hava, polen filtresi. Volkswagen, Audi, Seat, Skoda için.",
                    PartNumber = "KN-FSK001",
                    Price = 325.00m,
                    OldPrice = 425.00m,
                    Stock = 31,
                    ImageUrl = "https://images.unsplash.com/photo-1619642751034-765dfdf7c58e?w=500&q=80",
                    Rating = 4.9,
                    ReviewCount = 187,
                    DiscountPercentage = 24,
                    BadgeText = "Set İndirimi",
                    IsFeatured = true,
                    IsNew = true,
                    CategoryId = 3,
                    BrandId = 4, // Volkswagen (Araç Markası)
                    PartBrandId = 14 // Knecht (Parça Markası)
                },
                new Product
                {
                    Id = 19,
                    Name = "Valeo Alternatör",
                    Description = "120A alternatör. BMW 3 Serisi, 5 Serisi uyumlu. Yüksek verimlilik.",
                    PartNumber = "VL-437576",
                    Price = 2499.99m,
                    Stock = 8,
                    ImageUrl = "https://images.unsplash.com/photo-1605559424843-9e4c228bf1c2?w=500&q=80",
                    Rating = 4.7,
                    ReviewCount = 42,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 4,
                    BrandId = 2, // BMW (Araç Markası)
                    PartBrandId = 15 // Valeo (Parça Markası)
                },
                new Product
                {
                    Id = 20,
                    Name = "Bosch Marş Motoru",
                    Description = "Güçlü başlatma performansı. Mercedes C-Class, E-Class uyumlu. 2 yıl garanti.",
                    PartNumber = "BO-0001109400",
                    Price = 1899.99m,
                    Stock = 11,
                    ImageUrl = "https://images.unsplash.com/photo-1593941707882-a5bba14938c7?w=500&q=80",
                    Rating = 4.8,
                    ReviewCount = 65,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 4,
                    BrandId = 1, // Mercedes-Benz (Araç Markası)
                    PartBrandId = 1 // Bosch (Parça Markası)
                },
                new Product
                {
                    Id = 21,
                    Name = "Bosch Buji Seti",
                    Description = "4 adet platin buji. Uzun ömürlü, yakıt ekonomisi. 60.000 km kullanım garantisi.",
                    PartNumber = "BO-FR7DPP332S",
                    Price = 285.00m,
                    OldPrice = 350.00m,
                    Stock = 55,
                    ImageUrl = "https://images.unsplash.com/photo-1615906655593-ad0386982a0f?w=500&q=80",
                    Rating = 4.8,
                    ReviewCount = 203,
                    DiscountPercentage = 19,
                    BadgeText = "Set",
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 4,
                    BrandId = 11, // Toyota (Araç Markası)
                    PartBrandId = 1 // Bosch (Parça Markası)
                },
                new Product
                {
                    Id = 22,
                    Name = "Monroe Amortisör Seti",
                    Description = "4 adet amortisör seti. Ön ve arka komple. Toyota Corolla uyumlu. Reflex teknolojisi.",
                    PartNumber = "MN-5803SET",
                    Price = 2850.00m,
                    OldPrice = 3400.00m,
                    Stock = 6,
                    ImageUrl = "https://images.unsplash.com/photo-1625047509248-ec889cbff17f?w=500&q=80",
                    Rating = 4.6,
                    ReviewCount = 38,
                    DiscountPercentage = 16,
                    BadgeText = "Komple Set",
                    IsFeatured = true,
                    IsNew = false,
                    CategoryId = 5,
                    BrandId = 11, // Toyota (Araç Markası)
                    PartBrandId = 16 // Monroe (Parça Markası)
                },
                new Product
                {
                    Id = 23,
                    Name = "TRW Rotil",
                    Description = "Direksiyon rotil başı. Sağ ve sol uyumlu. Ford, Opel modelleri için.",
                    PartNumber = "TRW-JTE1095",
                    Price = 285.00m,
                    Stock = 28,
                    ImageUrl = "https://images.unsplash.com/photo-1615906655593-ad0386982a0f?w=500&q=80",
                    Rating = 4.5,
                    ReviewCount = 52,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 5,
                    BrandId = 10, // Opel (Araç Markası)
                    PartBrandId = 12 // TRW (Parça Markası)
                },
                new Product
                {
                    Id = 24,
                    Name = "Lemförder Salıncak Burcu",
                    Description = "Alt salıncak burç takımı. VW, Audi için. Orijinal ekipman kalitesi.",
                    PartNumber = "LM-10160",
                    Price = 165.00m,
                    Stock = 47,
                    ImageUrl = "https://images.unsplash.com/photo-1605559424843-9e4c228bf1c2?w=500&q=80",
                    Rating = 4.7,
                    ReviewCount = 76,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 5,
                    BrandId = 4, // Volkswagen (Araç Markası)
                    PartBrandId = 17 // Lemförder (Parça Markası)
                },
                new Product
                {
                    Id = 25,
                    Name = "Castrol Transmax Dexron VI",
                    Description = "Otomatik şanzıman yağı. 1 litre. GM Dexron VI onaylı. Uzun ömürlü koruma.",
                    PartNumber = "CA-DEXVI-1L",
                    Price = 185.00m,
                    Stock = 72,
                    ImageUrl = "https://images.unsplash.com/photo-1615906655593-ad0386982a0f?w=500&q=80",
                    Rating = 4.8,
                    ReviewCount = 142,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 6,
                    BrandId = 17, // Chevrolet (Araç Markası)
                    PartBrandId = 5 // Castrol (Parça Markası)
                },
                new Product
                {
                    Id = 26,
                    Name = "Total Transmission Gear 8 75W-80",
                    Description = "Manuel şanzıman yağı. API GL-4. Peugeot, Renault onaylı. 1 litre.",
                    PartNumber = "TT-TG8-75W80",
                    Price = 145.00m,
                    Stock = 58,
                    ImageUrl = "https://images.unsplash.com/photo-1615906655593-ad0386982a0f?w=500&q=80",
                    Rating = 4.6,
                    ReviewCount = 87,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 6,
                    BrandId = 8, // Peugeot (Araç Markası)
                    PartBrandId = 9 // Total (Parça Markası)
                },
                new Product
                {
                    Id = 27,
                    Name = "Valeo Debriyaj Seti",
                    Description = "Komple debriyaj takımı: baskı, disk, bilya. Fiat, Opel uyumlu. OE kalitesi.",
                    PartNumber = "VL-826552",
                    Price = 1650.00m,
                    OldPrice = 1950.00m,
                    Stock = 14,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.7,
                    ReviewCount = 94,
                    DiscountPercentage = 15,
                    BadgeText = "Komple Set",
                    IsFeatured = true,
                    IsNew = false,
                    CategoryId = 6,
                    BrandId = 7, // Fiat (Araç Markası)
                    PartBrandId = 15 // Valeo (Parça Markası)
                },
                new Product
                {
                    Id = 28,
                    Name = "Far Camı Sol",
                    Description = "Ön far camı, sol taraf. VW Golf 7 uyumlu. Orijinal ölçülerde.",
                    PartNumber = "DP-441-1156L",
                    Price = 425.00m,
                    Stock = 16,
                    ImageUrl = "https://images.unsplash.com/photo-1625047509248-ec889cbff17f?w=500&q=80",
                    Rating = 4.4,
                    ReviewCount = 31,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 7,
                    BrandId = 4, // Volkswagen (Araç Markası)
                    PartBrandId = 1 // Bosch (Parça Markası - Default)
                },
                new Product
                {
                    Id = 29,
                    Name = "Dış Ayna Camı Sağ",
                    Description = "Isıtmalı dış ayna camı, sağ. Ford Focus 3 uyumlu. Kolay montaj.",
                    PartNumber = "VW-1859832",
                    Price = 185.00m,
                    Stock = 33,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.5,
                    ReviewCount = 45,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 7,
                    BrandId = 5, // Ford (Araç Markası)
                    PartBrandId = 1 // Bosch (Parça Markası - Default)
                },
                new Product
                {
                    Id = 30,
                    Name = "Hyundai i20 Ön Tampon Panjuru",
                    Description = "Alt ön tampon panjuru/ızgarası. i20 modelleri için. Siyah renk. Orijinal kalite plastik.",
                    PartNumber = "HY-865601J000",
                    Price = 295.00m,
                    OldPrice = 385.00m,
                    Stock = 19,
                    ImageUrl = "https://images.unsplash.com/photo-1619642751034-765dfdf7c58e?w=500&q=80",
                    Rating = 4.3,
                    ReviewCount = 28,
                    DiscountPercentage = 23,
                    BadgeText = "Fırsat",
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 7,
                    BrandId = 13, // Hyundai (Araç Markası)
                    PartBrandId = 1 // Bosch (Parça Markası - Default)
                },
                new Product
                {
                    Id = 31,
                    Name = "Mahle Hava Filtresi",
                    Description = "Yüksek performanslı hava filtresi. BMW, Mercedes uyumlu. Uzun ömürlü.",
                    PartNumber = "MH-LX1780",
                    Price = 195.00m,
                    Stock = 45,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.7,
                    ReviewCount = 112,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 1,
                    BrandId = 1, // Mercedes-Benz
                    PartBrandId = 14 // Mahle
                },
                new Product
                {
                    Id = 32,
                    Name = "Knecht Polen Filtresi",
                    Description = "Aktif karbonlu polen filtresi. Audi, VW uyumlu. Anti-alerjik.",
                    PartNumber = "KN-LA230",
                    Price = 165.00m,
                    OldPrice = 210.00m,
                    Stock = 38,
                    ImageUrl = "https://images.unsplash.com/photo-1625047509248-ec889cbff17f?w=500&q=80",
                    Rating = 4.5,
                    ReviewCount = 89,
                    DiscountPercentage = 21,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 1,
                    BrandId = 3, // Audi
                    PartBrandId = 15 // Knecht
                },
                new Product
                {
                    Id = 33,
                    Name = "TRW Fren Diski Ön",
                    Description = "Havalandırmalı fren diski, ön aks. Toyota, Honda uyumlu. Yüksek karbon içerik.",
                    PartNumber = "TRW-DF4823",
                    Price = 850.00m,
                    Stock = 22,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.8,
                    ReviewCount = 156,
                    IsFeatured = true,
                    IsNew = false,
                    CategoryId = 2,
                    BrandId = 11, // Toyota
                    PartBrandId = 12 // TRW
                },
                new Product
                {
                    Id = 34,
                    Name = "ATE Fren Kaliperi Ön Sağ",
                    Description = "Komple fren kaliperi, ön sağ. Renault, Dacia uyumlu. Orijinal kalite.",
                    PartNumber = "ATE-342019",
                    Price = 1450.00m,
                    OldPrice = 1750.00m,
                    Stock = 11,
                    ImageUrl = "https://images.unsplash.com/photo-1625047509248-ec889cbff17f?w=500&q=80",
                    Rating = 4.6,
                    ReviewCount = 67,
                    DiscountPercentage = 17,
                    BadgeText = "Kaliteli",
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 2,
                    BrandId = 6, // Renault
                    PartBrandId = 11 // ATE
                },
                new Product
                {
                    Id = 35,
                    Name = "Varta Blue Dynamic 60Ah Akü",
                    Description = "60Ah 540A akü. Volkswagen, Seat uyumlu. 2 yıl garanti.",
                    PartNumber = "VRT-560408054",
                    Price = 2150.00m,
                    Stock = 28,
                    ImageUrl = "https://images.unsplash.com/photo-1593941707882-a5bba14938c7?w=500&q=80",
                    Rating = 4.7,
                    ReviewCount = 203,
                    IsFeatured = true,
                    IsNew = false,
                    CategoryId = 3,
                    BrandId = 4, // Volkswagen
                    PartBrandId = 7 // Varta
                },
                new Product
                {
                    Id = 36,
                    Name = "Varta Silver Dynamic 75Ah Akü",
                    Description = "75Ah 750A akü. BMW, Mercedes uyumlu. AGM teknoloji.",
                    PartNumber = "VRT-575301078",
                    Price = 3250.00m,
                    OldPrice = 3650.00m,
                    Stock = 15,
                    ImageUrl = "https://images.unsplash.com/photo-1593941707882-a5bba14938c7?w=500&q=80",
                    Rating = 4.9,
                    ReviewCount = 187,
                    DiscountPercentage = 11,
                    BadgeText = "Premium",
                    IsFeatured = true,
                    IsNew = false,
                    CategoryId = 3,
                    BrandId = 2, // BMW
                    PartBrandId = 7 // Varta
                },
                new Product
                {
                    Id = 37,
                    Name = "Shell Helix Ultra 5W-30",
                    Description = "Tam sentetik motor yağı 5W-30. 4 litre. VW 504.00/507.00 onaylı.",
                    PartNumber = "SHL-550040758",
                    Price = 485.00m,
                    Stock = 67,
                    ImageUrl = "https://images.unsplash.com/photo-1615906655593-ad0386982a0f?w=500&q=80",
                    Rating = 4.8,
                    ReviewCount = 341,
                    IsFeatured = true,
                    IsNew = false,
                    CategoryId = 4,
                    BrandId = 4, // Volkswagen
                    PartBrandId = 4 // Shell
                },
                new Product
                {
                    Id = 38,
                    Name = "Mobil 1 ESP Formula 5W-30",
                    Description = "Sentetik dizel motor yağı. 5 litre. Mercedes onaylı. DPF uyumlu.",
                    PartNumber = "MOB-153767",
                    Price = 695.00m,
                    OldPrice = 795.00m,
                    Stock = 42,
                    ImageUrl = "https://images.unsplash.com/photo-1615906655593-ad0386982a0f?w=500&q=80",
                    Rating = 4.9,
                    ReviewCount = 278,
                    DiscountPercentage = 13,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 4,
                    BrandId = 1, // Mercedes-Benz
                    PartBrandId = 6 // Mobil
                },
                new Product
                {
                    Id = 39,
                    Name = "Castrol Edge 0W-40",
                    Description = "Tam sentetik motor yağı 0W-40. 1 litre. BMW Longlife onaylı.",
                    PartNumber = "CST-15669A",
                    Price = 185.00m,
                    Stock = 95,
                    ImageUrl = "https://images.unsplash.com/photo-1615906655593-ad0386982a0f?w=500&q=80",
                    Rating = 4.7,
                    ReviewCount = 167,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 4,
                    BrandId = 2, // BMW
                    PartBrandId = 5 // Castrol
                },
                new Product
                {
                    Id = 40,
                    Name = "Total Quartz 9000 Energy 5W-40",
                    Description = "Sentetik benzinli motor yağı. 4 litre. Renault onaylı.",
                    PartNumber = "TOT-183199",
                    Price = 395.00m,
                    Stock = 53,
                    ImageUrl = "https://images.unsplash.com/photo-1615906655593-ad0386982a0f?w=500&q=80",
                    Rating = 4.6,
                    ReviewCount = 143,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 4,
                    BrandId = 6, // Renault
                    PartBrandId = 9 // Total
                },
                new Product
                {
                    Id = 41,
                    Name = "Petrol Ofisi Maxima 10W-40",
                    Description = "Yarı sentetik motor yağı. 4 litre. Fiat, Ford uyumlu.",
                    PartNumber = "PO-8697406080111",
                    Price = 295.00m,
                    OldPrice = 350.00m,
                    Stock = 78,
                    ImageUrl = "https://images.unsplash.com/photo-1615906655593-ad0386982a0f?w=500&q=80",
                    Rating = 4.5,
                    ReviewCount = 198,
                    DiscountPercentage = 16,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 4,
                    BrandId = 5, // Ford
                    PartBrandId = 10 // Petrol Ofisi
                },
                new Product
                {
                    Id = 42,
                    Name = "Monroe Amortisör Ön Sol",
                    Description = "Gaz yağ amortisör, ön sol. VW, Skoda uyumlu. OE kalitesi.",
                    PartNumber = "MNR-G8223",
                    Price = 875.00m,
                    Stock = 26,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.7,
                    ReviewCount = 124,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 5,
                    BrandId = 4, // Volkswagen
                    PartBrandId = 16 // Monroe
                },
                new Product
                {
                    Id = 43,
                    Name = "Monroe Amortisör Arka Takım",
                    Description = "Gaz yağ amortisör takımı, arka. Ford, Mazda uyumlu. 2'li set.",
                    PartNumber = "MNR-E1130",
                    Price = 1650.00m,
                    OldPrice = 1950.00m,
                    Stock = 18,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.8,
                    ReviewCount = 97,
                    DiscountPercentage = 15,
                    BadgeText = "Takım",
                    IsFeatured = true,
                    IsNew = false,
                    CategoryId = 5,
                    BrandId = 5, // Ford
                    PartBrandId = 16 // Monroe
                },
                new Product
                {
                    Id = 44,
                    Name = "Sachs Amortisör Ön Sağ",
                    Description = "Orijinal kalite amortisör. Mercedes, Audi uyumlu. Uzun ömürlü.",
                    PartNumber = "SCH-313933",
                    Price = 1250.00m,
                    Stock = 21,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.8,
                    ReviewCount = 156,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 5,
                    BrandId = 1, // Mercedes-Benz
                    PartBrandId = 8 // Sachs
                },
                new Product
                {
                    Id = 45,
                    Name = "Lemförder Rotbaşı",
                    Description = "Ön rotbaşı. BMW, Opel uyumlu. Alman kalitesi.",
                    PartNumber = "LEM-10745",
                    Price = 425.00m,
                    Stock = 34,
                    ImageUrl = "https://images.unsplash.com/photo-1625047509248-ec889cbff17f?w=500&q=80",
                    Rating = 4.6,
                    ReviewCount = 82,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 5,
                    BrandId = 2, // BMW
                    PartBrandId = 17 // Lemförder
                },
                new Product
                {
                    Id = 46,
                    Name = "Lemförder Salıncak Burcu",
                    Description = "Alt salıncak burcu. Volkswagen, Seat uyumlu. Polyüretan.",
                    PartNumber = "LEM-33918",
                    Price = 185.00m,
                    OldPrice = 235.00m,
                    Stock = 56,
                    ImageUrl = "https://images.unsplash.com/photo-1625047509248-ec889cbff17f?w=500&q=80",
                    Rating = 4.5,
                    ReviewCount = 94,
                    DiscountPercentage = 21,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 5,
                    BrandId = 4, // Volkswagen
                    PartBrandId = 17 // Lemförder
                },
                new Product
                {
                    Id = 47,
                    Name = "Valeo Debriyaj Balatası",
                    Description = "Debriyaj disk balatası. Renault, Dacia uyumlu. Yüksek sürtünme.",
                    PartNumber = "VL-801629",
                    Price = 695.00m,
                    Stock = 29,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.7,
                    ReviewCount = 118,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 6,
                    BrandId = 6, // Renault
                    PartBrandId = 15 // Valeo
                },
                new Product
                {
                    Id = 48,
                    Name = "Sachs Debriyaj Baskısı",
                    Description = "Debriyaj baskı tabağı. Volkswagen, Audi uyumlu. Hidrolik sistem.",
                    PartNumber = "SCH-883089",
                    Price = 1150.00m,
                    OldPrice = 1350.00m,
                    Stock = 17,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.8,
                    ReviewCount = 89,
                    DiscountPercentage = 15,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 6,
                    BrandId = 3, // Audi
                    PartBrandId = 8 // Sachs
                },
                new Product
                {
                    Id = 49,
                    Name = "Bosch Buji Takımı",
                    Description = "4'lü platin buji takımı. Ford, Mazda uyumlu. 60.000 km ömür.",
                    PartNumber = "BSH-0242236564",
                    Price = 485.00m,
                    Stock = 43,
                    ImageUrl = "https://images.unsplash.com/photo-1625047509248-ec889cbff17f?w=500&q=80",
                    Rating = 4.9,
                    ReviewCount = 267,
                    IsFeatured = true,
                    IsNew = false,
                    CategoryId = 6,
                    BrandId = 5, // Ford
                    PartBrandId = 1 // Bosch
                },
                new Product
                {
                    Id = 50,
                    Name = "Bosch Ateşleme Bobini",
                    Description = "Elektronik ateşleme bobini. BMW, Mercedes uyumlu. Yüksek voltaj.",
                    PartNumber = "BSH-0221604103",
                    Price = 825.00m,
                    OldPrice = 975.00m,
                    Stock = 31,
                    ImageUrl = "https://images.unsplash.com/photo-1625047509248-ec889cbff17f?w=500&q=80",
                    Rating = 4.7,
                    ReviewCount = 143,
                    DiscountPercentage = 15,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 6,
                    BrandId = 2, // BMW
                    PartBrandId = 1 // Bosch
                },
                new Product
                {
                    Id = 51,
                    Name = "Valeo Far Komple Sağ",
                    Description = "Komple far ünitesi sağ. Peugeot 308 uyumlu. H7 ampul girişi.",
                    PartNumber = "VL-043378",
                    Price = 1850.00m,
                    Stock = 12,
                    ImageUrl = "https://images.unsplash.com/photo-1625047509248-ec889cbff17f?w=500&q=80",
                    Rating = 4.6,
                    ReviewCount = 54,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 7,
                    BrandId = 8, // Peugeot
                    PartBrandId = 15 // Valeo
                },
                new Product
                {
                    Id = 52,
                    Name = "Bosch Xenon Far Ampulü",
                    Description = "D2S Xenon ampul. BMW, Audi uyumlu. 4300K beyaz ışık.",
                    PartNumber = "BSH-1987302904",
                    Price = 995.00m,
                    OldPrice = 1195.00m,
                    Stock = 23,
                    ImageUrl = "https://images.unsplash.com/photo-1625047509248-ec889cbff17f?w=500&q=80",
                    Rating = 4.8,
                    ReviewCount = 186,
                    DiscountPercentage = 17,
                    BadgeText = "Xenon",
                    IsFeatured = true,
                    IsNew = false,
                    CategoryId = 7,
                    BrandId = 3, // Audi
                    PartBrandId = 1 // Bosch
                },
                new Product
                {
                    Id = 53,
                    Name = "Stop Lambası Komple Sol",
                    Description = "Arka stop lambası sol. Toyota Corolla uyumlu. LED teknoloji.",
                    PartNumber = "TY-8156002640",
                    Price = 645.00m,
                    Stock = 18,
                    ImageUrl = "https://images.unsplash.com/photo-1625047509248-ec889cbff17f?w=500&q=80",
                    Rating = 4.5,
                    ReviewCount = 72,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 7,
                    BrandId = 11, // Toyota
                    PartBrandId = 1 // Bosch
                },
                new Product
                {
                    Id = 54,
                    Name = "Ön Kaput",
                    Description = "Komple ön kaput. Hyundai i20 uyumlu. Boya hazır primer kaplı.",
                    PartNumber = "HY-664001J000",
                    Price = 2850.00m,
                    OldPrice = 3250.00m,
                    Stock = 7,
                    ImageUrl = "https://images.unsplash.com/photo-1619642751034-765dfdf7c58e?w=500&q=80",
                    Rating = 4.4,
                    ReviewCount = 28,
                    DiscountPercentage = 12,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 7,
                    BrandId = 13, // Hyundai
                    PartBrandId = 1 // Bosch
                },
                new Product
                {
                    Id = 55,
                    Name = "Mann-Filter Yakıt Filtresi",
                    Description = "Dizel yakıt filtresi. Mercedes, VW uyumlu. Su tutucu özellik.",
                    PartNumber = "MF-WK820/17",
                    Price = 285.00m,
                    Stock = 51,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.8,
                    ReviewCount = 214,
                    IsFeatured = false,
                    IsNew = true,
                    CategoryId = 1,
                    BrandId = 1, // Mercedes-Benz
                    PartBrandId = 2 // Mann-Filter
                },
                new Product
                {
                    Id = 56,
                    Name = "Brembo Fren Balatası Arka",
                    Description = "Seramik fren balatası, arka. Audi, VW uyumlu. Az toz bırakır.",
                    PartNumber = "BRM-P85073",
                    Price = 595.00m,
                    OldPrice = 695.00m,
                    Stock = 36,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.9,
                    ReviewCount = 289,
                    DiscountPercentage = 14,
                    IsFeatured = true,
                    IsNew = false,
                    CategoryId = 2,
                    BrandId = 3, // Audi
                    PartBrandId = 3 // Brembo
                },
                new Product
                {
                    Id = 57,
                    Name = "TRW Fren Hidrolik Hortumu",
                    Description = "Fren hidrolik hortumu. Renault, Nissan uyumlu. Paslanmaz uçlar.",
                    PartNumber = "TRW-PHD1062",
                    Price = 165.00m,
                    Stock = 44,
                    ImageUrl = "https://images.unsplash.com/photo-1625047509248-ec889cbff17f?w=500&q=80",
                    Rating = 4.6,
                    ReviewCount = 91,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 2,
                    BrandId = 6, // Renault
                    PartBrandId = 12 // TRW
                },
                new Product
                {
                    Id = 58,
                    Name = "Mahle Termostat",
                    Description = "Motor termostat. BMW, Opel uyumlu. 87°C açılma sıcaklığı.",
                    PartNumber = "MH-TX10987D",
                    Price = 245.00m,
                    OldPrice = 295.00m,
                    Stock = 39,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.7,
                    ReviewCount = 127,
                    DiscountPercentage = 17,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 6,
                    BrandId = 2, // BMW
                    PartBrandId = 14 // Mahle
                },
                new Product
                {
                    Id = 59,
                    Name = "Shell Spirax S5 ATE 75W-90",
                    Description = "Sentetik şanzıman yağı. 1 litre. Manuel şanzıman için.",
                    PartNumber = "SHL-550028154",
                    Price = 185.00m,
                    Stock = 62,
                    ImageUrl = "https://images.unsplash.com/photo-1615906655593-ad0386982a0f?w=500&q=80",
                    Rating = 4.6,
                    ReviewCount = 148,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 4,
                    BrandId = 4, // Volkswagen
                    PartBrandId = 4 // Shell
                },
                new Product
                {
                    Id = 60,
                    Name = "Castrol ATF Dex II Multivehicle",
                    Description = "Otomatik şanzıman yağı. 1 litre. Çok araç uyumlu.",
                    PartNumber = "CST-4008177072130",
                    Price = 145.00m,
                    OldPrice = 175.00m,
                    Stock = 73,
                    ImageUrl = "https://images.unsplash.com/photo-1615906655593-ad0386982a0f?w=500&q=80",
                    Rating = 4.5,
                    ReviewCount = 192,
                    DiscountPercentage = 17,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 4,
                    BrandId = 12, // Honda
                    PartBrandId = 5 // Castrol
                },
                // Motor Parçaları - Pistonlar
                new Product
                {
                    Id = 61,
                    Name = "Mahle Piston Seti STD",
                    Description = "4 silindirli motorlar için standart ölçü piston seti. Yüksek dayanıklı alüminyum alaşım. Mercedes, BMW uyumlu.",
                    PartNumber = "MH-PST-5001",
                    Price = 2850.00m,
                    Stock = 12,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.8,
                    ReviewCount = 45,
                    IsFeatured = true,
                    IsNew = false,
                    CategoryId = 1, // Motor Parçaları
                    BrandId = 1, // Mercedes-Benz
                    PartBrandId = 14 // Mahle
                },
                new Product
                {
                    Id = 62,
                    Name = "Kolbenschmidt Piston Takımı 0.50",
                    Description = "Tamir ölçü piston takımı (+0.50mm). 4 silindirli dizel motorlar için. BMW, Audi uyumlu.",
                    PartNumber = "KS-PST-8450",
                    Price = 3200.00m,
                    OldPrice = 3600.00m,
                    Stock = 8,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.9,
                    ReviewCount = 32,
                    DiscountPercentage = 11,
                    IsFeatured = false,
                    IsNew = true,
                    CategoryId = 1, // Motor Parçaları
                    BrandId = 2, // BMW
                    PartBrandId = 12 // Kolbenschmidt
                },
                // Krank Milleri
                new Product
                {
                    Id = 63,
                    Name = "Bosch Krank Mili Sensörü",
                    Description = "Motor devir sensörü. Hassas ölçüm teknolojisi. Volkswagen, Audi, Seat uyumlu.",
                    PartNumber = "BSH-0261210318",
                    Price = 450.00m,
                    Stock = 35,
                    ImageUrl = "https://images.unsplash.com/photo-1492144534655-ae79c964c9d7?w=500&q=80",
                    Rating = 4.7,
                    ReviewCount = 89,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 1, // Motor Parçaları
                    BrandId = 4, // Volkswagen
                    PartBrandId = 1 // Bosch
                },
                new Product
                {
                    Id = 64,
                    Name = "SKF Krank Kasnağı",
                    Description = "Ön krank kasnağı. Titreşim damperi dahil. Mercedes, BMW dizel motorlar uyumlu.",
                    PartNumber = "SKF-VKM-35012",
                    Price = 1850.00m,
                    OldPrice = 2100.00m,
                    Stock = 15,
                    ImageUrl = "https://images.unsplash.com/photo-1492144534655-ae79c964c9d7?w=500&q=80",
                    Rating = 4.6,
                    ReviewCount = 56,
                    DiscountPercentage = 12,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 1, // Motor Parçaları
                    BrandId = 1, // Mercedes-Benz
                    PartBrandId = 16 // SKF
                },
                // Devirdaim (Su Pompası)
                new Product
                {
                    Id = 65,
                    Name = "Bosch Devirdaim Su Pompası",
                    Description = "Yüksek performanslı su pompası. Metal gövde, uzun ömürlü rulman. Mercedes, BMW uyumlu.",
                    PartNumber = "BSH-1987949454",
                    Price = 875.00m,
                    Stock = 28,
                    ImageUrl = "https://images.unsplash.com/photo-1581092918056-0c4c3acd3789?w=500&q=80",
                    Rating = 4.8,
                    ReviewCount = 124,
                    IsFeatured = true,
                    IsNew = false,
                    CategoryId = 1, // Motor Parçaları
                    BrandId = 1, // Mercedes-Benz
                    PartBrandId = 1 // Bosch
                },
                new Product
                {
                    Id = 66,
                    Name = "Hepu Devirdaim + Conta",
                    Description = "Su pompası conta seti dahil. Alüminyum gövde. Volkswagen, Audi, Skoda uyumlu.",
                    PartNumber = "HPU-P514",
                    Price = 695.00m,
                    OldPrice = 850.00m,
                    Stock = 32,
                    ImageUrl = "https://images.unsplash.com/photo-1581092918056-0c4c3acd3789?w=500&q=80",
                    Rating = 4.5,
                    ReviewCount = 98,
                    DiscountPercentage = 18,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 1, // Motor Parçaları
                    BrandId = 4, // Volkswagen
                    PartBrandId = 13 // Hepu
                },
                new Product
                {
                    Id = 67,
                    Name = "Graf Devirdaim Elektrikli",
                    Description = "Elektrikli su pompası. Verimli soğutma sistemi. BMW, Audi hibrit modeller uyumlu.",
                    PartNumber = "GRF-PA1234",
                    Price = 1650.00m,
                    Stock = 9,
                    ImageUrl = "https://images.unsplash.com/photo-1581092918056-0c4c3acd3789?w=500&q=80",
                    Rating = 4.9,
                    ReviewCount = 67,
                    IsFeatured = false,
                    IsNew = true,
                    CategoryId = 1, // Motor Parçaları
                    BrandId = 2, // BMW
                    PartBrandId = 15 // Graf
                },
                // Supaplar
                new Product
                {
                    Id = 68,
                    Name = "Mahle Emme Supabı Seti",
                    Description = "4 silindirli motorlar için emme supap takımı. Isıya dayanıklı özel alaşım. Mercedes, Audi uyumlu.",
                    PartNumber = "MH-VS-50001",
                    Price = 1250.00m,
                    Stock = 14,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.7,
                    ReviewCount = 41,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 1, // Motor Parçaları
                    BrandId = 1, // Mercedes-Benz
                    PartBrandId = 14 // Mahle
                },
                new Product
                {
                    Id = 69,
                    Name = "TRW Egzoz Supabı Takımı",
                    Description = "Egzoz supap seti 8 adet. Yüksek sıcaklık dayanımlı. BMW, Volkswagen uyumlu.",
                    PartNumber = "TRW-EX-8800",
                    Price = 1450.00m,
                    OldPrice = 1650.00m,
                    Stock = 11,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.6,
                    ReviewCount = 38,
                    DiscountPercentage = 12,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 1, // Motor Parçaları
                    BrandId = 2, // BMW
                    PartBrandId = 11 // TRW
                },
                // Silindir Kapak Contası
                new Product
                {
                    Id = 70,
                    Name = "Elring Silindir Kapak Contası",
                    Description = "Metal takviyeli silindir kapak contası. 4 silindirli motorlar. Mercedes, BMW uyumlu.",
                    PartNumber = "ELR-026.980",
                    Price = 950.00m,
                    Stock = 22,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.8,
                    ReviewCount = 156,
                    IsFeatured = true,
                    IsNew = false,
                    CategoryId = 1, // Motor Parçaları
                    BrandId = 1, // Mercedes-Benz
                    PartBrandId = 8 // Elring
                },
                new Product
                {
                    Id = 71,
                    Name = "Victor Reinz Üst Takım Contas",
                    Description = "Motor üst takım conta seti. Tüm contalar dahil. Volkswagen, Audi, Seat uyumlu.",
                    PartNumber = "VR-02-37805-01",
                    Price = 1550.00m,
                    OldPrice = 1800.00m,
                    Stock = 17,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.7,
                    ReviewCount = 92,
                    DiscountPercentage = 14,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 1, // Motor Parçaları
                    BrandId = 4, // Volkswagen
                    PartBrandId = 9 // Victor Reinz
                },
                // Segmanlar
                new Product
                {
                    Id = 72,
                    Name = "Goetze Piston Segmanı STD",
                    Description = "Standart ölçü piston segman takımı. 4 silindir. Mercedes, BMW, Audi uyumlu.",
                    PartNumber = "GTZ-08-109800-00",
                    Price = 850.00m,
                    Stock = 19,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.6,
                    ReviewCount = 73,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 1, // Motor Parçaları
                    BrandId = 1, // Mercedes-Benz
                    PartBrandId = 10 // Goetze
                },
                new Product
                {
                    Id = 73,
                    Name = "Mahle Segman Takımı 0.25",
                    Description = "Tamir ölçü segman seti (+0.25mm). Dizel ve benzinli motorlar. Volkswagen uyumlu.",
                    PartNumber = "MH-028-RS-00114-0N0",
                    Price = 920.00m,
                    Stock = 16,
                    ImageUrl = "https://images.unsplash.com/photo-1486262715619-67b85e0b08d3?w=500&q=80",
                    Rating = 4.8,
                    ReviewCount = 55,
                    IsFeatured = false,
                    IsNew = true,
                    CategoryId = 1, // Motor Parçaları
                    BrandId = 4, // Volkswagen
                    PartBrandId = 14 // Mahle
                },
                // Termostat
                new Product
                {
                    Id = 74,
                    Name = "Wahler Termostat 87°C",
                    Description = "Motor termostatı 87 derece. Hızlı açılma özelliği. Mercedes, BMW uyumlu.",
                    PartNumber = "WAH-4471.87D",
                    Price = 385.00m,
                    Stock = 45,
                    ImageUrl = "https://images.unsplash.com/photo-1581092918056-0c4c3acd3789?w=500&q=80",
                    Rating = 4.5,
                    ReviewCount = 112,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 1, // Motor Parçaları
                    BrandId = 1, // Mercedes-Benz
                    PartBrandId = 13 // Wahler
                },
                new Product
                {
                    Id = 75,
                    Name = "Gates Termostat + Conta",
                    Description = "Termostat conta dahil 92°C. Volkswagen, Audi, Skoda, Seat uyumlu.",
                    PartNumber = "GTS-TH35492G1",
                    Price = 425.00m,
                    OldPrice = 500.00m,
                    Stock = 38,
                    ImageUrl = "https://images.unsplash.com/photo-1581092918056-0c4c3acd3789?w=500&q=80",
                    Rating = 4.7,
                    ReviewCount = 145,
                    DiscountPercentage = 15,
                    IsFeatured = false,
                    IsNew = false,
                    CategoryId = 1, // Motor Parçaları
                    BrandId = 4, // Volkswagen
                    PartBrandId = 7 // Gates
                },
            };

            var productSeedTimestamp = new DateTime(2025, 11, 3, 12, 0, 0, DateTimeKind.Utc);
            foreach (var product in products)
            {
                product.CreatedAt = productSeedTimestamp;
                product.UpdatedAt = productSeedTimestamp;
            }

            modelBuilder.Entity<Product>().HasData(products);

            // User seeding removed - will be handled in Program.cs after migration
        }
    }
}

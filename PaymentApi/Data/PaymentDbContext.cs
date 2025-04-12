using Microsoft.EntityFrameworkCore;

namespace PaymentApi.Data
{
    public class PaymentDbContext : DbContext
    {
        public PaymentDbContext(DbContextOptions<PaymentDbContext> options) : base(options)
        {
        }

        // Define your DbSets here
        public DbSet<Payment> Payments { get; set; }
        public DbSet<OutboxMessage> OutboxMessages { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Payment>(b =>
            {
                b.HasIndex(p => p.OrderId).IsUnique();
            });
            modelBuilder.Entity<OutboxMessage>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.EventType).HasMaxLength(100);
            });

            modelBuilder.Entity<Payment>().ToTable("Payments");
            modelBuilder.Entity<OutboxMessage>().ToTable("OutboxMessages");
        }
    }

    public enum PaymentStatus
    {
        Failed = -1,
        Pending,
        Processing,
        Processed,
        Refunded
    }
    public class Payment
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid OrderId { get; set; }
        public decimal Amount { get; set; }
        public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public string? Notes;
    }

    public class OutboxMessage
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string EventType { get; set; }
        public string EventData { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool Processed { get; set; }
        public int RetryCount { get; set; }
        public DateTime? ProcessedAt { get; set; }
    }
}

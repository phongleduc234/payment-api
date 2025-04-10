using MassTransit;
using Microsoft.EntityFrameworkCore;
using PaymentApi.Data;

namespace PaymentApi.Shared
{
    public class PaymentConsumer :
       IConsumer<ProcessPayment>,
       IConsumer<CompensatePayment>
    {
        private readonly PaymentDbContext _context;
        private readonly ILogger<PaymentConsumer> _logger;
        private readonly IBus _bus;

        public PaymentConsumer(
            PaymentDbContext context,
            ILogger<PaymentConsumer> logger,
            IBus bus)
        {
            _context = context;
            _logger = logger;
            _bus = bus;
        }

        // Xử lý thanh toán
        public async Task Consume(ConsumeContext<ProcessPayment> context)
        {
            _logger.LogInformation($"Processing payment for Order {context.Message.OrderId}");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // Kiểm tra trùng lặp
                var existingPayment = await _context.Payments
                    .FirstOrDefaultAsync(p => p.OrderId == context.Message.OrderId);

                if (existingPayment != null)
                {
                    _logger.LogWarning("Duplicate payment detected. Skipping...");
                    await context.Publish(new PaymentProcessed(context.Message.CorrelationId, true));
                    return;
                }

                // Tạo payment
                var payment = new Payment
                {
                    OrderId = context.Message.OrderId,
                    Amount = 100, // Giả định số tiền
                    Status = PaymentStatus.Processed
                };

                _context.Payments.Add(payment);
                await _context.SaveChangesAsync();

                // Publish event thành công
                await context.Publish(new PaymentProcessed(context.Message.CorrelationId, true));
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Payment processing failed");

                // Publish event thất bại
                await context.Publish(new PaymentProcessed(context.Message.CorrelationId, false));
            }
        }

        // Xử lý hoàn tiền (Compensate)
        public async Task Consume(ConsumeContext<CompensatePayment> context)
        {
            _logger.LogInformation($"Compensating payment for Order {context.Message.OrderId}");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var payment = await _context.Payments
                    .FirstOrDefaultAsync(p => p.OrderId == context.Message.OrderId);

                if (payment != null)
                {
                    payment.Status = PaymentStatus.Refunded;
                    await _context.SaveChangesAsync();
                }

                // Thông báo hoàn tiền thành công
                await _bus.Publish(new PaymentCompensated(context.Message.CorrelationId, true));
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Payment compensation failed");
                await _bus.Publish(new PaymentCompensated(context.Message.CorrelationId, false));
            }
        }
    }
}

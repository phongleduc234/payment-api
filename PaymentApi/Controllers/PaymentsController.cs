using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaymentApi.Data;
using PaymentApi.Shared;

namespace PaymentApi.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class PaymentsController : ControllerBase
    {
        private readonly PaymentDbContext _context;
        private readonly IBus _bus;
        private readonly ILogger<PaymentsController> _logger;

        public PaymentsController(
            PaymentDbContext context,
            IBus bus,
            ILogger<PaymentsController> logger)
        {
            _context = context;
            _bus = bus;
            _logger = logger;
        }

        [HttpPost("process")]
        public async Task<IActionResult> ProcessPayment([FromBody] ProcessPaymentRequest request)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var payment = new Payment
                {
                    OrderId = request.OrderId,
                    Amount = request.Amount,
                    Status = PaymentStatus.Processed
                };

                _context.Payments.Add(payment);
                await _context.SaveChangesAsync();

                // Publish event thành công
                await _bus.Publish(new PaymentProcessed(request.CorrelationId, true));
                await transaction.CommitAsync();

                return Ok(payment);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Payment processing failed");

                // Publish event thất bại
                await _bus.Publish(new PaymentProcessed(request.CorrelationId, false));
                return StatusCode(500, "Payment processing failed");
            }
        }

        [HttpPost("refund")]
        public async Task<IActionResult> RefundPayment([FromBody] RefundPaymentRequest request)
        {
            var payment = await _context.Payments
                .FirstOrDefaultAsync(p => p.OrderId == request.OrderId);

            if (payment == null) return NotFound();

            payment.Status = PaymentStatus.Refunded;
            await _context.SaveChangesAsync();

            return Ok(payment);
        }
    }
}

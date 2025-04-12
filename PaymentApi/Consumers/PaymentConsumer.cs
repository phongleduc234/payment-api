using MassTransit;
using Microsoft.EntityFrameworkCore;
using PaymentApi.Data;
using PaymentApi.Extensions;
using SharedContracts.Events;

namespace PaymentApi.Consumers
{
    /// <summary>
    /// Handles payment processing and compensation events within the Payment service.
    /// Implements both initial payment processing and refund operations as part of the distributed saga.
    /// </summary>
    public class PaymentConsumer :
       IConsumer<ProcessPaymentRequest>, // Handles payment processing requests from Saga
       IConsumer<CompensatePayment>      // Handles payment compensation (refund) requests from Saga
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

        /// <summary>
        /// Processes a payment request from the Saga Orchestrator.
        /// Creates a payment record, processes the payment, and publishes the result.
        /// Uses the Outbox pattern to ensure reliable messaging.
        /// </summary>
        public async Task Consume(ConsumeContext<ProcessPaymentRequest> context)
        {
            var correlationId = context.Message.CorrelationId;
            var orderId = context.Message.OrderId;
            var amount = context.Message.Amount;

            _logger.LogInformation($"Processing payment for Order {orderId}, CorrelationId: {correlationId}, Amount: {amount}");

            // Use transaction to ensure atomicity of database operations
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // Check for duplicate payment requests (idempotency)
                var existingPayment = await _context.Payments
                    .FirstOrDefaultAsync(p => p.OrderId == orderId);

                if (existingPayment != null)
                {
                    _logger.LogWarning($"Duplicate payment detected for Order {orderId}. Skipping...");

                    // Even for duplicates, we ensure the success event is published via outbox
                    await _context.SaveEventToOutboxAsync(new PaymentProcessed(correlationId, orderId, true));
                    await transaction.CommitAsync();
                    return;
                }

                // Create a new payment record
                var payment = new Payment
                {
                    Id = Guid.NewGuid(),
                    OrderId = orderId,
                    Amount = amount,
                    Status = PaymentStatus.Processing,
                    CreatedAt = DateTime.UtcNow
                };

                _context.Payments.Add(payment);

                // Process the payment (in reality, this would call a payment gateway)
                bool paymentSuccess = await ProcessPaymentTransaction(payment);

                if (paymentSuccess)
                {
                    // Update payment status to processed
                    payment.Status = PaymentStatus.Processed;
                    await _context.SaveChangesAsync();

                    // Save success event to outbox for reliable publishing
                    await _context.SaveEventToOutboxAsync(new PaymentProcessed(correlationId, orderId, true));
                    _logger.LogInformation($"Payment processed successfully for Order {orderId}");
                }
                else
                {
                    // Update payment status to failed
                    payment.Status = PaymentStatus.Failed;
                    await _context.SaveChangesAsync();

                    // Save failure event to outbox
                    await _context.SaveEventToOutboxAsync(new PaymentProcessed(correlationId, orderId, false));
                    _logger.LogWarning($"Payment processing failed for Order {orderId}");
                }

                // Commit the transaction
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                // Rollback the transaction on error
                await transaction.RollbackAsync();
                _logger.LogError(ex, $"Exception during payment processing for Order {orderId}");

                // Ensure we still publish failure event even if the main transaction failed
                try
                {
                    using var newTransaction = await _context.Database.BeginTransactionAsync();
                    await _context.SaveEventToOutboxAsync(new PaymentProcessed(correlationId, orderId, false));
                    await newTransaction.CommitAsync();
                }
                catch (Exception outboxEx)
                {
                    _logger.LogError(outboxEx, $"Failed to save payment failure event to outbox for Order {orderId}");
                }
            }
        }

        /// <summary>
        /// Handles compensation requests when a payment needs to be refunded as part of saga rollback.
        /// Updates the payment status to refunded and publishes a compensation completed event.
        /// </summary>
        public async Task Consume(ConsumeContext<CompensatePayment> context)
        {
            var correlationId = context.Message.CorrelationId;
            var orderId = context.Message.OrderId;

            _logger.LogInformation($"Compensating payment for Order {orderId}, CorrelationId: {correlationId}");

            // Use transaction to ensure atomicity
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // Find the payment to compensate
                var payment = await _context.Payments
                    .FirstOrDefaultAsync(p => p.OrderId == orderId);

                if (payment == null)
                {
                    _logger.LogWarning($"No payment found for Order {orderId} to compensate");
                    // Still report successful compensation to allow saga to continue
                    await _context.SaveEventToOutboxAsync(new PaymentCompensated(correlationId, orderId, true));
                    await transaction.CommitAsync();
                    return;
                }

                // Update payment to refunded state
                payment.Status = PaymentStatus.Refunded;
                payment.UpdatedAt = DateTime.UtcNow;
                payment.Notes = $"Payment refunded as part of compensation at {DateTime.UtcNow}";
                await _context.SaveChangesAsync();

                // Save compensation success event to outbox
                await _context.SaveEventToOutboxAsync(new PaymentCompensated(correlationId, orderId, true));
                _logger.LogInformation($"Payment successfully compensated for Order {orderId}");

                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                // Rollback on error
                await transaction.RollbackAsync();
                _logger.LogError(ex, $"Payment compensation failed for Order {orderId}");

                // Ensure we publish compensation failure event
                try
                {
                    using var newTransaction = await _context.Database.BeginTransactionAsync();
                    await _context.SaveEventToOutboxAsync(new PaymentCompensated(correlationId, orderId, false));
                    await newTransaction.CommitAsync();
                }
                catch (Exception outboxEx)
                {
                    _logger.LogError(outboxEx, $"Failed to save payment compensation failure event to outbox for Order {orderId}");
                }
            }
        }

        /// <summary>
        /// Simulates payment processing with an external payment gateway.
        /// In a real application, this would integrate with a payment provider.
        /// </summary>
        /// <param name="payment">The payment record to process</param>
        /// <returns>True if payment is successful, false otherwise</returns>
        private async Task<bool> ProcessPaymentTransaction(Payment payment)
        {
            // Simulate API call to payment gateway
            await Task.Delay(200);

            // Simulate 90% success rate
            return new Random().Next(100) < 90;
        }
    }
}
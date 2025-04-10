namespace PaymentApi.Shared
{
    // CompensateEvents.cs
    public record CompensateOrder(Guid CorrelationId, Guid OrderId);
    public record CompensatePayment(Guid CorrelationId, Guid OrderId);
    public record CompensateInventory(Guid CorrelationId, Guid OrderId);
    public record ProcessPayment(Guid CorrelationId, Guid OrderId);
    public record PaymentProcessed(Guid CorrelationId, bool Success);
    public record PaymentCompensated(Guid CorrelationId, bool Success);
    public record ProcessPaymentRequest(Guid CorrelationId, Guid OrderId, decimal Amount);
    public record RefundPaymentRequest(Guid OrderId);
}

// PaymentApi/Extensions/OutboxExtensions.cs
using Newtonsoft.Json;
using PaymentApi.Data;

namespace PaymentApi.Extensions
{
    public static class OutboxExtensions
    {
        public static async Task SaveEventToOutboxAsync<TEvent>(
            this PaymentDbContext dbContext, 
            TEvent eventData)
        {
            var outboxMessage = new OutboxMessage
            {
                Id = Guid.NewGuid(),
                EventType = typeof(TEvent).AssemblyQualifiedName,
                EventData = JsonConvert.SerializeObject(eventData),
                CreatedAt = DateTime.UtcNow,
                Processed = false,
                RetryCount = 0
            };

            dbContext.OutboxMessages.Add(outboxMessage);
            await dbContext.SaveChangesAsync();
        }
    }
}

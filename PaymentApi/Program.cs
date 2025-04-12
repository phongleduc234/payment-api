using MassTransit;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using PaymentApi.Consumers;
using PaymentApi.Data;

var builder = WebApplication.CreateBuilder(args);

// Configure Entity Framework and PostgreSQL
builder.Services.AddDbContext<PaymentDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Register IHttpClientFactory for making HTTP requests
builder.Services.AddHttpClient();

// Register ASP.NET Core services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure MassTransit for message handling
builder.Services.AddMassTransit(x =>
{
    // Register the PaymentConsumer that handles payment processing and compensation
    x.AddConsumer<PaymentConsumer>();

    // Configure RabbitMQ as the message broker
    x.UsingRabbitMq((context, cfg) =>
    {
        // Get RabbitMQ configuration from appsettings
        var rabbitConfig = builder.Configuration.GetSection("RabbitMq");
        var host = rabbitConfig["Host"] ?? "localhost";
        var port = rabbitConfig.GetValue<int>("Port", 5672);
        var username = rabbitConfig["UserName"] ?? "guest";
        var password = rabbitConfig["Password"] ?? "guest";

        // Configure the RabbitMQ host
        cfg.Host(new Uri($"rabbitmq://{host}:{port}"), h =>
        {
            h.Username(username);
            h.Password(password);
        });

        // Configure global retry policies
        // This handles temporary failures by retrying with increasing delays
        cfg.UseDelayedRedelivery(r => r.Intervals(
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15)
        ));

        // Immediate retry for quick recoverable errors
        cfg.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));

        // Endpoint for handling payment processing commands from Saga
        cfg.ReceiveEndpoint("process-payment", e =>
        {
            // Configure the endpoint to use PaymentConsumer for ProcessPaymentRequest
            e.ConfigureConsumer<PaymentConsumer>(context);

            // Endpoint-specific retry policy
            e.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));

            // Set up Dead Letter Queue for failed messages
            e.BindDeadLetterQueue("process-payment-dlq", "process-payment-dlx",
                dlq => dlq.Durable = true);
        });

        // Endpoint for handling payment compensation commands from Saga
        cfg.ReceiveEndpoint("compensate-payment", e =>
        {
            // Configure the endpoint to use PaymentConsumer for CompensatePayment
            e.ConfigureConsumer<PaymentConsumer>(context);

            // Endpoint-specific retry policy
            e.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));

            // Set up Dead Letter Queue for failed compensation messages
            e.BindDeadLetterQueue("compensate-payment-dlq", "compensate-payment-dlx",
                dlq => dlq.Durable = true);
        });

        // Dead Letter handling endpoint - monitors messages that failed processing
        cfg.ReceiveEndpoint("payment-dead-letter-queue", e =>
        {
            // Simple handler to log dead-lettered messages
            e.Handler<object>(async context =>
            {
                var logger = context.GetPayload<ILogger<object>>();
                logger.LogError("Dead-lettered payment message received: {MessageId}", context.MessageId);
                // Additional handling logic can be added here (alerts, manual intervention, etc.)
            });

            // Bind to all dead letter queues in this service
            e.Bind("process-payment-dlq");
            e.Bind("compensate-payment-dlq");
        });
    });
});

var app = builder.Build();

// Apply database migrations automatically on startup
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
    dbContext.Database.Migrate();
}

// Configure Swagger for API documentation
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Payment API V1");
    c.RoutePrefix = "swagger";
});

// Configure forwarded headers for proper IP resolution behind proxies
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// Health checks endpoint
app.MapHealthChecks("/health");

// Configure routing and endpoints
app.UseRouting();
app.UseAuthorization();
app.MapControllers();

app.Run();
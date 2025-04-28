using MassTransit;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using PaymentApi.Consumers;
using PaymentApi.Data;
using SharedContracts.Events;
using StackExchange.Redis;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables();

// Configure Entity Framework and PostgreSQL
builder.Services.AddDbContext<PaymentDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Register IHttpClientFactory for making HTTP requests
builder.Services.AddHttpClient();

// Register ASP.NET Core services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Payment API",
        Version = "v1",
        Description = "API for payment management with Outbox Pattern and DLQ support",
        Contact = new OpenApiContact
        {
            Name = "Development Team",
            Email = "jun8124@gmail.com"
        }
    });

    // Add XML comments support
    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }

    // Group endpoints by controller
    options.TagActionsBy(api => new[] { api.GroupName ?? api.ActionDescriptor.RouteValues["controller"] });
});

// Add Redis connection
builder.Services.AddSingleton(sp =>
{
    var redisConfig = builder.Configuration.GetSection("Redis");
    var host = redisConfig["Host"] ?? "localhost";
    var port = redisConfig["Port"] ?? "6379";
    var password = redisConfig["Password"] ?? "";

    var configOptions = new ConfigurationOptions
    {
        AbortOnConnectFail = false,
        ConnectRetry = 3, // Tăng số lần thử lại khi kết nối thất bại
        ConnectTimeout = 10000, // Tăng timeout kết nối lên 10 giây
        SyncTimeout = 10000, // Tăng timeout cho các lệnh đồng bộ lên 10 giây
        AsyncTimeout = 10000 // Tăng timeout cho các lệnh bất đồng bộ lên 10 giây
    };

    configOptions.EndPoints.Add($"{host}:{port}");

    if (!string.IsNullOrEmpty(password))
    {
        configOptions.Password = password;
    }
    return ConnectionMultiplexer.Connect(configOptions);
});

// Configure MassTransit for message handling
builder.Services.AddMassTransit(x =>
{
    // Register the PaymentConsumer that handles payment processing and compensation
    x.AddConsumer<PaymentConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        var rabbitConfig = builder.Configuration.GetSection("RabbitMq");
        var host = rabbitConfig["Host"] ?? "localhost";
        var port = rabbitConfig.GetValue<int>("Port", 5672);
        var username = rabbitConfig["UserName"] ?? "guest";
        var password = rabbitConfig["Password"] ?? "guest";

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

        cfg.ReceiveEndpoint("compensate-payment", e =>
        {
            e.ConfigureConsumer<PaymentConsumer>(context);

            // Ensure the correct message type is being handled
            e.Consumer<PaymentConsumer>(context, c =>
            {
                c.Message<CompensatePayment>(m => { });
            });

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
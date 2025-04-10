using MassTransit;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using PaymentApi.Data;
using PaymentApi.Shared;

var builder = WebApplication.CreateBuilder(args);

// Configure Entity Framework and PostgreSQL
builder.Services.AddDbContext<PaymentDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Register IHttpClientFactory
builder.Services.AddHttpClient();

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Bind RabbitMQ configuration
var rabbitMqOptions = builder.Configuration.GetSection("RabbitMq").Get<RabbitMqOptions>();
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<PaymentConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(new Uri($"rabbitmq://{rabbitMqOptions.Host}:{rabbitMqOptions.Port}/"), h =>
        {
            h.Username(rabbitMqOptions.UserName ?? "admin");
            h.Password(rabbitMqOptions.Password ?? "123456");
        });

        // Cấu hình queue cho Compensate
        cfg.ReceiveEndpoint("compensate-payment", e =>
        {
            e.ConfigureConsumer<PaymentConsumer>(context);
            e.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));
            e.BindDeadLetterQueue("compensate-payment-dlq", "compensate-payment-dlx");
        });
    });
});

var app = builder.Build();

// Apply database migrations automatically
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
    dbContext.Database.Migrate();
}

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Order API V1");
    c.RoutePrefix = "swagger";
});

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.MapHealthChecks("/health");
app.UseRouting();

app.UseAuthorization();

app.MapControllers();

app.Run();

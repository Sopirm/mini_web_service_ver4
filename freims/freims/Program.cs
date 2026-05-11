using System.Diagnostics;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(options =>
{
    options.IncludeScopes = true;
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddSingleton<BookingProcessStore>();
builder.Services.AddSingleton<MetricsStore>();
builder.Services.AddSingleton<ReadinessState>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "meeting-room-booking-training-service",
    endpoints = new[]
    {
        "POST /events",
        "GET /processes/{processKey}",
        "GET /health/live",
        "GET /health/ready",
        "POST /degradation/critical",
        "GET /metrics"
    }
}));

app.MapPost("/events", (
    BookingEvent request,
    BookingProcessStore store,
    MetricsStore metrics,
    ILogger<Program> logger) =>
{
    var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId)
        ? Guid.NewGuid().ToString("N")
        : request.CorrelationId.Trim();

    using var scope = logger.BeginScope(new Dictionary<string, object>
    {
        ["CorrelationId"] = correlationId,
        ["ProcessKey"] = request.ProcessKey ?? string.Empty,
        ["IdempotencyKey"] = request.IdempotencyKey ?? string.Empty
    });

    var validationError = request.Validate();
    if (validationError is not null)
    {
        metrics.RegisterFailedTransition("validation", 0);
        logger.LogWarning(
            "Invalid event. CorrelationId={CorrelationId}, Reason={Reason}",
            correlationId,
            validationError);
        return Results.BadRequest(new EventResponse(
            correlationId,
            request.ProcessKey ?? string.Empty,
            null,
            false,
            false,
            false,
            validationError));
    }

    var stopwatch = Stopwatch.StartNew();
    var result = store.Apply(request, correlationId, logger);
    stopwatch.Stop();

    if (result.Duplicate)
    {
        metrics.RegisterDuplicateDelivery(request.EventName.ToString());
    }
    else if (result.Success)
    {
        metrics.RegisterSuccessfulTransition(request.EventName.ToString(), stopwatch.Elapsed.TotalMilliseconds);
    }
    else
    {
        metrics.RegisterFailedTransition(request.EventName.ToString(), stopwatch.Elapsed.TotalMilliseconds);
    }

    if (result.CompensationExecuted)
    {
        metrics.RegisterCompensation();
    }

    return result.HttpStatus switch
    {
        StatusCodes.Status200OK => Results.Ok(result.Response),
        StatusCodes.Status202Accepted => Results.Accepted($"/processes/{request.ProcessKey}", result.Response),
        StatusCodes.Status409Conflict => Results.Conflict(result.Response),
        _ => Results.Json(result.Response, statusCode: result.HttpStatus)
    };
});

app.MapGet("/processes/{processKey}", (string processKey, BookingProcessStore store) =>
{
    var process = store.Get(processKey);
    return process is null ? Results.NotFound() : Results.Ok(process.ToSnapshot());
});

app.MapGet("/health/live", () => Results.Ok(new
{
    status = "live",
    checkedAtUtc = DateTimeOffset.UtcNow
}));

app.MapGet("/health/ready", (ReadinessState readiness) =>
{
    if (readiness.CriticalDegradation)
    {
        return Results.Json(new
        {
            status = "not_ready",
            reason = "critical_degradation",
            checkedAtUtc = DateTimeOffset.UtcNow
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new
    {
        status = "ready",
        checkedAtUtc = DateTimeOffset.UtcNow
    });
});

app.MapPost("/degradation/critical", (CriticalDegradationRequest request, ReadinessState readiness) =>
{
    readiness.CriticalDegradation = request.Enabled;
    return Results.Ok(new
    {
        criticalDegradation = readiness.CriticalDegradation,
        readiness = readiness.CriticalDegradation ? "not_ready" : "ready"
    });
});

app.MapGet("/metrics", (MetricsStore metrics) =>
{
    return Results.Text(metrics.ToPrometheusText(), "text/plain; version=0.0.4; charset=utf-8");
});

app.Run();

public sealed record BookingEvent(
    string? ProcessKey,
    string? IdempotencyKey,
    BookingEventName EventName,
    string? CorrelationId = null,
    bool SimulateFailure = false)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(ProcessKey))
        {
            return "processKey is required";
        }

        if (string.IsNullOrWhiteSpace(IdempotencyKey))
        {
            return "idempotencyKey is required";
        }

        return null;
    }
}

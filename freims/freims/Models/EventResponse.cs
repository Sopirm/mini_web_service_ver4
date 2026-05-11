public sealed record EventResponse(
    string CorrelationId,
    string ProcessKey,
    BookingState? State,
    bool Success,
    bool Duplicate,
    bool CompensationExecuted,
    string? Message);

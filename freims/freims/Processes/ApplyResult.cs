public sealed record ApplyResult(
    int HttpStatus,
    EventResponse Response,
    bool Duplicate,
    bool Success,
    bool CompensationExecuted);

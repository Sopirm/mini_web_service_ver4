public sealed record ProcessSnapshot(
    string ProcessKey,
    BookingState State,
    bool ReservationActive,
    bool AccessIssued,
    string? LastError,
    IReadOnlyCollection<string> ProcessedIdempotencyKeys);

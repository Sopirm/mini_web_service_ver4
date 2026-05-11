public sealed class BookingProcess(string processKey)
{
    public object SyncRoot { get; } = new();
    public string ProcessKey { get; } = processKey;
    public BookingState State { get; set; } = BookingState.New;
    public bool ReservationActive { get; set; }
    public bool AccessIssued { get; set; }
    public string? LastError { get; set; }
    public Dictionary<string, EventResponse> ProcessedEvents { get; } = new();

    public ProcessSnapshot ToSnapshot()
    {
        lock (SyncRoot)
        {
            return new ProcessSnapshot(
                ProcessKey,
                State,
                ReservationActive,
                AccessIssued,
                LastError,
                ProcessedEvents.Keys.ToArray());
        }
    }
}

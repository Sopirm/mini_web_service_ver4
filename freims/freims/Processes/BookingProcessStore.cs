using System.Collections.Concurrent;

public sealed class BookingProcessStore
{
    private readonly ConcurrentDictionary<string, BookingProcess> _processes = new();

    public BookingProcess? Get(string processKey)
    {
        return _processes.TryGetValue(processKey, out var process) ? process : null;
    }

    public ApplyResult Apply(BookingEvent request, string correlationId, ILogger logger)
    {
        var processKey = request.ProcessKey!.Trim();
        var idempotencyKey = request.IdempotencyKey!.Trim();
        var process = _processes.GetOrAdd(processKey, key => new BookingProcess(key));

        lock (process.SyncRoot)
        {
            if (process.ProcessedEvents.TryGetValue(idempotencyKey, out var cachedResponse))
            {
                logger.LogInformation(
                    "Duplicate delivery ignored. CorrelationId={CorrelationId}, Event={EventName}, State={State}",
                    correlationId,
                    request.EventName,
                    process.State);

                return new ApplyResult(
                    StatusCodes.Status200OK,
                    cachedResponse with { CorrelationId = correlationId, Duplicate = true },
                    true,
                    cachedResponse.Success,
                    cachedResponse.CompensationExecuted);
            }

            var previousState = process.State;
            var transition = ExecuteTransition(process, request);

            var response = new EventResponse(
                correlationId,
                processKey,
                process.State,
                transition.Success,
                false,
                transition.CompensationExecuted,
                transition.Message);

            process.ProcessedEvents[idempotencyKey] = response;

            if (transition.CompensationExecuted)
            {
                logger.LogWarning(
                    "Compensation executed. CorrelationId={CorrelationId}, Event={EventName}, From={PreviousState}, To={NewState}, Reason={Reason}",
                    correlationId,
                    request.EventName,
                    previousState,
                    process.State,
                    transition.Message);
            }
            else if (transition.Success)
            {
                logger.LogInformation(
                    "State transition completed. CorrelationId={CorrelationId}, Event={EventName}, From={PreviousState}, To={NewState}",
                    correlationId,
                    request.EventName,
                    previousState,
                    process.State);
            }
            else
            {
                logger.LogError(
                    "State transition failed. CorrelationId={CorrelationId}, Event={EventName}, From={PreviousState}, Current={CurrentState}, Reason={Reason}",
                    correlationId,
                    request.EventName,
                    previousState,
                    process.State,
                    transition.Message);
            }

            return new ApplyResult(
                transition.HttpStatus,
                response,
                false,
                transition.Success,
                transition.CompensationExecuted);
        }
    }

    private static TransitionResult ExecuteTransition(BookingProcess process, BookingEvent request)
    {
        if (request.EventName == BookingEventName.Fail || request.SimulateFailure)
        {
            return FailCurrentStep(process, request.EventName);
        }

        return (process.State, request.EventName) switch
        {
            (BookingState.New, BookingEventName.AcceptApplication) => Move(
                process,
                BookingState.ApplicationAccepted,
                "application accepted"),

            (BookingState.ApplicationAccepted, BookingEventName.BookResource) => BookResource(process),

            (BookingState.ResourceBooked, BookingEventName.GrantAccess) => MoveWithAccess(
                process,
                BookingState.AccessGranted,
                "access issued"),

            (BookingState.AccessGranted, BookingEventName.Complete) => Move(
                process,
                BookingState.Completed,
                "booking process completed"),

            _ => InvalidTransition(process, request.EventName)
        };
    }

    private static TransitionResult BookResource(BookingProcess process)
    {
        process.State = BookingState.ResourceBooked;
        process.ReservationActive = true;
        process.LastError = null;
        return TransitionResult.Accepted("resource booked");
    }

    private static TransitionResult MoveWithAccess(BookingProcess process, BookingState state, string message)
    {
        process.State = state;
        process.AccessIssued = true;
        process.LastError = null;
        return TransitionResult.Accepted(message);
    }

    private static TransitionResult Move(BookingProcess process, BookingState state, string message)
    {
        process.State = state;
        process.LastError = null;
        return TransitionResult.Accepted(message);
    }

    private static TransitionResult FailCurrentStep(BookingProcess process, BookingEventName eventName)
    {
        if (process.State == BookingState.ResourceBooked
            && eventName is BookingEventName.GrantAccess or BookingEventName.Fail)
        {
            process.ReservationActive = false;
            process.AccessIssued = false;
            process.State = BookingState.CompensationCompleted;
            process.LastError = "grant access failed; reservation was cancelled by compensation";
            return TransitionResult.FailedWithCompensation(process.LastError);
        }

        var failedState = process.State;
        process.State = BookingState.Error;
        process.LastError = $"step failed while process was in {failedState}";
        return TransitionResult.Failed(process.LastError);
    }

    private static TransitionResult InvalidTransition(BookingProcess process, BookingEventName eventName)
    {
        process.LastError = $"event {eventName} is not allowed from state {process.State}";
        return TransitionResult.Conflict(process.LastError);
    }
}

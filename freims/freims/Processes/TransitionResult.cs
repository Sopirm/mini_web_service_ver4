public sealed record TransitionResult(
    int HttpStatus,
    bool Success,
    bool CompensationExecuted,
    string Message)
{
    public static TransitionResult Accepted(string message)
    {
        return new TransitionResult(StatusCodes.Status202Accepted, true, false, message);
    }

    public static TransitionResult Conflict(string message)
    {
        return new TransitionResult(StatusCodes.Status409Conflict, false, false, message);
    }

    public static TransitionResult Failed(string message)
    {
        return new TransitionResult(StatusCodes.Status500InternalServerError, false, false, message);
    }

    public static TransitionResult FailedWithCompensation(string message)
    {
        return new TransitionResult(StatusCodes.Status500InternalServerError, false, true, message);
    }
}

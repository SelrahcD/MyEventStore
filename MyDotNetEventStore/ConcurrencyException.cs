namespace MyDotNetEventStore;

public class ConcurrencyException : Exception
{
    private ConcurrencyException(string message) : base(message)
    {
    }

    public static ConcurrencyException StreamDoesntExist(string streamId)
    {
        return new ConcurrencyException($"Stream '{streamId}' doesn't exists.");
    }

    public static ConcurrencyException StreamAlreadyExists(string streamId)
    {
        return new ConcurrencyException($"Stream '{streamId}' already exists.");
    }

    public static Exception StreamIsNotAtExpectedRevision(object expectedRevision, long lastRevision)
    {
        return new ConcurrencyException($"Stream 'stream-id' is at revision {lastRevision}. You tried appending events at revision {expectedRevision}.");
    }
}
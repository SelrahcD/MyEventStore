namespace MyDotNetEventStore.Tests;

public class CannotAppendAnEmptyListException : Exception
{
    private CannotAppendAnEmptyListException(string message) : base(message)
    {
    }

    public static CannotAppendAnEmptyListException ToStream(string streamId)
    {
        return new CannotAppendAnEmptyListException($"Cannot append an empty list to stream '{streamId}'.");
    }
}
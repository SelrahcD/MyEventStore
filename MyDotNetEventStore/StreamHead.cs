namespace MyDotNetEventStore;

public record StreamHead(long Revision, long Position)
{
}
using System.Diagnostics;

namespace MyDotNetEventStore;

public static class Tracing
{
    public static readonly ActivitySource ActivitySource = new("MyDotNetEventStore");
}
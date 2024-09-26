namespace MyDotNetEventStore.Tests;

public static class AsyncEnumerableExtensions
{
    public static async Task<int> CountAsync<T>(this IAsyncEnumerable<T> asyncEnumerable)
    {
        var count = 0;

        await foreach (var item in asyncEnumerable)
        {
            count++;
        }

        return count;
    }

    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> asyncEnumerable)
    {
        var list = new List<T>();

        await foreach (var item in asyncEnumerable)
        {
            list.Add(item);
        }

        return list;
    }
}
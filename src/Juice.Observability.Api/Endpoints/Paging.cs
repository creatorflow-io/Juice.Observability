namespace Juice.Observability.Api.Endpoints
{
    // Page<T> is Juice.Observability.Extensibility.Page<T> (global using in this project): one type for the platform and its modules, the same JSON.

    /// <summary>
    /// Opaque cursor pagination (requirements §6.1). Results are ordered by id, so paging never repeats a row or skips
    /// one that existed when paging started. Ids are UUIDv7: roughly creation order, random within a millisecond. The
    /// cursor carries a phase for lists that span two tables.
    /// </summary>
    public sealed record Cursor(string Phase, Guid After)
    {
        public const int DefaultLimit = Extensibility.Paging.DefaultLimit;
        public const int MaxLimit = Extensibility.Paging.MaxLimit;

        // One parser for the platform and its modules (Juice.Observability.Extensibility): the encoding and the 400 problems are the same.
        public string Encode() => new Extensibility.PageCursor(Phase, After).Encode();

        public static bool TryDecode(string? value, out Cursor? cursor)
        {
            var ok = Extensibility.PageCursor.TryDecode(value, out var decoded);
            cursor = decoded is null ? null : new Cursor(decoded.Phase, decoded.After);
            return ok;
        }

        /// <summary>Validates <c>limit</c> and <c>cursor</c>; returns a 400 problem when either is invalid.</summary>
        public static IResult? Validate(int? limit, string? cursor, out int take, out Cursor? decoded)
        {
            var problem = Extensibility.Paging.Validate(limit, cursor, out take, out var after);
            decoded = after is null ? null : new Cursor(after.Phase, after.After);
            return problem;
        }
    }
}

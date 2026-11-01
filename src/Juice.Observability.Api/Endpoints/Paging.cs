using System.Buffers.Text;
using System.Text;

namespace Juice.Observability.Api.Endpoints
{
    /// <summary>A page of results. <see cref="NextCursor"/> is null on the last page.</summary>
    public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

    /// <summary>
    /// Opaque cursor pagination (requirements §6.1). Results are ordered by id, so paging never repeats a row or skips
    /// one that existed when paging started. Ids are UUIDv7: roughly creation order, random within a millisecond. The
    /// cursor carries a phase for lists that span two tables.
    /// </summary>
    public sealed record Cursor(string Phase, Guid After)
    {
        public const int DefaultLimit = 50;
        public const int MaxLimit = 200;

        public string Encode() => Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{Phase}:{After:N}"));

        public static bool TryDecode(string? value, out Cursor? cursor)
        {
            cursor = null;
            if (string.IsNullOrEmpty(value))
            {
                return true;
            }
            if (value.Length > 128)
            {
                return false;
            }
            try
            {
                var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value));
                var parts = text.Split(':');
                if (parts.Length == 2 && parts[0].Length is > 0 and <= 8 && Guid.TryParseExact(parts[1], "N", out var after))
                {
                    cursor = new Cursor(parts[0], after);
                    return true;
                }
            }
            catch (FormatException)
            {
            }
            return false;
        }

        /// <summary>Validates <c>limit</c> and <c>cursor</c>; returns a 400 problem when either is invalid.</summary>
        public static IResult? Validate(int? limit, string? cursor, out int take, out Cursor? decoded)
        {
            take = limit ?? DefaultLimit;
            decoded = null;
            if (take is < 1 or > MaxLimit)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid limit",
                    detail: $"limit must be between 1 and {MaxLimit}.");
            }
            if (!TryDecode(cursor, out decoded))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid cursor",
                    detail: "cursor must be a value returned as nextCursor.");
            }
            return null;
        }
    }
}

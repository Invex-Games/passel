using Marten.Exceptions;
using Npgsql;

namespace Invex.Passel.WebApi.Domain;

/// <summary>
///     What a save that lost a race to another request looks like, for the store services that read again and decide
///     afresh when it happens.
/// </summary>
internal static class MartenRaces
{
    /// <summary>
    ///     Whether <paramref name="exception" /> is a lost race: an update of a document that is gone or has changed
    ///     since it was read, or a write of a unique key another request has just written.
    /// </summary>
    internal static bool IsLostRace(Exception exception)
    {
        // Marten may report several failed operations of one batch together
        if (exception is AggregateException aggregate)
            return aggregate.InnerExceptions.All(IsLostRace);

        for (var inner = exception; inner is not null; inner = inner.InnerException)
            if (inner is NonExistentDocumentException or ConcurrencyException
                or PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                return true;

        return false;
    }
}

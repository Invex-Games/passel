using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Options;
using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.Tests;

/// <summary>
///     Which parcels the sweep takes, by its own predicates compiled and run over parcels in memory, at a fixed time
///     with the default lifetimes: an hour for an unsent file, fifteen minutes for an upload to finish.
/// </summary>
public sealed class ParcelSweepSelectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static readonly ParcelOptions Options = new();

    private static Parcel NewParcel(ParcelStatus status, TimeSpan age, Guid? attachedPacketId = null) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = "alice",
            CreatedAt = Now - age,
            Status = status,
            Size = 1,
            ContentType = "application/octet-stream",
            FileName = null,
            AttachedPacketId = attachedPacketId,
            DeletedAt = status == ParcelStatus.Deleted
                ? Now - age
                : null,
        };

    private static bool Expirable(Parcel parcel) =>
        ParcelSweep.Expirable(Now, Options).Compile()(parcel);

    private static bool Erasable(Parcel parcel) =>
        ParcelSweep.Erasable().Compile()(parcel);

    [Fact]
    public void An_unsent_file_expires_once_it_is_older_than_the_unattached_lifetime()
    {
        Assert.False(Expirable(NewParcel(ParcelStatus.Uploaded, TimeSpan.FromMinutes(59))));
        Assert.False(Expirable(NewParcel(ParcelStatus.Uploaded, Options.UnattachedLifetime)));
        Assert.True(Expirable(NewParcel(ParcelStatus.Uploaded, Options.UnattachedLifetime + TimeSpan.FromTicks(1))));
        Assert.True(Expirable(NewParcel(ParcelStatus.Uploaded, TimeSpan.FromDays(3))));
    }

    [Fact]
    public void A_sent_file_never_expires()
    {
        Assert.False(Expirable(NewParcel(ParcelStatus.Uploaded, TimeSpan.FromDays(30), Guid.CreateVersion7())));
    }

    [Fact]
    public void An_upload_expires_once_it_is_older_than_the_grace_to_finish()
    {
        Assert.False(Expirable(NewParcel(ParcelStatus.Uploading, TimeSpan.FromMinutes(14))));
        Assert.False(Expirable(NewParcel(ParcelStatus.Uploading, Options.UploadingGrace)));
        Assert.True(Expirable(NewParcel(ParcelStatus.Uploading, Options.UploadingGrace + TimeSpan.FromTicks(1))));

        // Long before an unsent file of the same age would
        Assert.False(Expirable(NewParcel(ParcelStatus.Uploaded, Options.UploadingGrace + TimeSpan.FromTicks(1))));
    }

    [Fact]
    public void A_deleted_file_is_erased_and_never_expired_again()
    {
        foreach (var age in new[] { TimeSpan.Zero, TimeSpan.FromDays(3) })
        {
            var deleted = NewParcel(ParcelStatus.Deleted, age);

            Assert.False(Expirable(deleted));
            Assert.True(Erasable(deleted));
        }

        Assert.False(Erasable(NewParcel(ParcelStatus.Uploaded, TimeSpan.FromDays(3))));
        Assert.False(Erasable(NewParcel(ParcelStatus.Uploading, TimeSpan.FromDays(3))));
    }

    [Fact]
    public void The_lifetimes_come_from_the_options()
    {
        var shorter = new ParcelOptions
        {
            UnattachedLifetime = TimeSpan.FromMinutes(10),
            UploadingGrace = TimeSpan.FromMinutes(1),
        };

        var expirable = ParcelSweep.Expirable(Now, shorter).Compile();

        Assert.True(expirable(NewParcel(ParcelStatus.Uploaded, TimeSpan.FromMinutes(11))));
        Assert.True(expirable(NewParcel(ParcelStatus.Uploading, TimeSpan.FromMinutes(2))));
        Assert.False(expirable(NewParcel(ParcelStatus.Uploaded, TimeSpan.FromMinutes(9))));
    }
}

using Invex.Passel.WebApi.Endpoints.PacketEndpoints;

namespace Invex.Passel.WebApi.Tests;

public sealed class PostPacketTests
{
    [Theory]
    [InlineData("https://example.com/path?q=1")]
    [InlineData("http://example.com")]
    public void Validate_accepts_http_links(string url)
    {
        var errors = PacketEndpoints.Validate(new PacketEndpoints.PostPacketRequestBody.Link
        {
            Name = "Example",
            Url = url,
        });

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("intent://scan/#Intent;scheme=zxing;end")]
    public void Validate_rejects_non_http_links(string url)
    {
        var errors = PacketEndpoints.Validate(new PacketEndpoints.PostPacketRequestBody.Link
        {
            Name = null,
            Url = url,
        });

        Assert.Contains("url", errors.Keys);
    }

    [Fact]
    public void Validate_rejects_overlong_link_name()
    {
        var errors = PacketEndpoints.Validate(new PacketEndpoints.PostPacketRequestBody.Link
        {
            Name = new('a', PacketEndpoints.MaxTitleLength + 1),
            Url = "https://example.com",
        });

        Assert.Contains("name", errors.Keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_rejects_blank_text_body(string body)
    {
        var errors = PacketEndpoints.Validate(new PacketEndpoints.PostPacketRequestBody.Text
        {
            Title = null,
            Body = body,
        });

        Assert.Contains("body", errors.Keys);
    }

    [Fact]
    public void Validate_rejects_overlong_text_body()
    {
        var errors = PacketEndpoints.Validate(new PacketEndpoints.PostPacketRequestBody.Text
        {
            Title = "Title",
            Body = new('a', PacketEndpoints.MaxBodyLength + 1),
        });

        Assert.Contains("body", errors.Keys);
    }

    [Fact]
    public void Validate_accepts_text()
    {
        var errors = PacketEndpoints.Validate(new PacketEndpoints.PostPacketRequestBody.Text
        {
            Title = "Title",
            Body = "Body",
        });

        Assert.Empty(errors);
    }

    [Fact]
    public void ContentBytes_counts_the_title_and_body_as_utf8()
    {
        var bytes = PacketEndpoints.ContentBytes(new PacketEndpoints.PostPacketRequestBody.Text
        {
            Title = "é",
            Body = "abc",
        });

        Assert.Equal(5, bytes);
    }

    [Fact]
    public void ContentBytes_counts_the_name_and_url_and_skips_a_missing_name()
    {
        var bytes = PacketEndpoints.ContentBytes(new PacketEndpoints.PostPacketRequestBody.Link
        {
            Name = null,
            Url = "https://example.com",
        });

        Assert.Equal(19, bytes);
    }
}

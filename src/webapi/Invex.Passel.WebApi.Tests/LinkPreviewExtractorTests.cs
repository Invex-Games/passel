using Invex.Passel.WebApi.Domain;
using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.Tests;

public sealed class LinkPreviewExtractorTests
{
    private static readonly Uri PageUrl = new("https://example.com/articles/1?ref=feed");

    private static readonly DateTimeOffset FetchedAt = new(2026, 10, 22, 11, 3, 10, TimeSpan.Zero);

    private static LinkPreview? Extract(string html) =>
        LinkPreviewExtractor.Extract(html, PageUrl, FetchedAt);

    private static string Page(string head, string body = "<p>Body</p>") =>
        $"<!DOCTYPE html><html><head>{head}</head><body>{body}</body></html>";

    [Fact]
    public void Open_graph_tags_win_over_the_plain_title_and_description()
    {
        var preview = Extract(Page("""
                                   <title>Doc title</title>
                                   <meta name="description" content="Meta description">
                                   <meta property="og:title" content="OG title">
                                   <meta property="og:description" content="OG description">
                                   <meta property="og:image" content="https://cdn.example.com/og.png">
                                   <meta name="twitter:title" content="Twitter title">
                                   """));

        Assert.NotNull(preview);
        Assert.Equal("OG title", preview.Title);
        Assert.Equal("OG description", preview.Description);
        Assert.Equal("https://cdn.example.com/og.png", preview.ImageUrl);
        Assert.Equal(FetchedAt, preview.FetchedAt);
    }

    [Fact]
    public void Twitter_card_tags_come_next()
    {
        var preview = Extract(Page("""
                                   <title>Doc title</title>
                                   <meta name="twitter:title" content="Twitter title">
                                   <meta name="twitter:description" content="Twitter description">
                                   <meta name="twitter:image" content="https://cdn.example.com/card.png">
                                   """));

        Assert.NotNull(preview);
        Assert.Equal("Twitter title", preview.Title);
        Assert.Equal("Twitter description", preview.Description);
        Assert.Equal("https://cdn.example.com/card.png", preview.ImageUrl);
    }

    [Fact]
    public void The_title_tag_and_meta_description_are_the_last_resort()
    {
        var preview = Extract(Page("""
                                   <title>Doc title</title>
                                   <meta name="description" content="Meta description">
                                   """));

        Assert.NotNull(preview);
        Assert.Equal("Doc title", preview.Title);
        Assert.Equal("Meta description", preview.Description);
        Assert.Null(preview.ImageUrl);
    }

    [Fact]
    public void Each_field_falls_back_on_its_own()
    {
        // An Open Graph title with nothing else: the description comes from the meta tag, the image from Twitter
        var preview = Extract(Page("""
                                   <meta property="og:title" content="OG title">
                                   <meta name="description" content="Meta description">
                                   <meta name="twitter:image:src" content="https://cdn.example.com/card.png">
                                   """));

        Assert.NotNull(preview);
        Assert.Equal("OG title", preview.Title);
        Assert.Equal("Meta description", preview.Description);
        Assert.Equal("https://cdn.example.com/card.png", preview.ImageUrl);
    }

    [Fact]
    public void A_blank_tag_falls_through_to_the_next()
    {
        var preview = Extract(Page("""
                                   <meta property="og:title" content="   ">
                                   <title>Doc title</title>
                                   """));

        Assert.NotNull(preview);
        Assert.Equal("Doc title", preview.Title);
    }

    [Fact]
    public void The_first_tag_of_a_kind_wins()
    {
        var preview = Extract(Page("""
                                   <meta property="og:image" content="https://cdn.example.com/first.png">
                                   <meta property="og:image" content="https://cdn.example.com/second.png">
                                   <meta property="og:title" content="First">
                                   <meta property="og:title" content="Second">
                                   """));

        Assert.NotNull(preview);
        Assert.Equal("First", preview.Title);
        Assert.Equal("https://cdn.example.com/first.png", preview.ImageUrl);
    }

    [Theory]
    [InlineData("/images/cover.png", "https://example.com/images/cover.png")]
    [InlineData("cover.png", "https://example.com/articles/cover.png")]
    [InlineData("//cdn.example.com/cover.png", "https://cdn.example.com/cover.png")]
    [InlineData("http://cdn.example.com/cover.png", "http://cdn.example.com/cover.png")]
    [InlineData("  https://cdn.example.com/a%20b.png ", "https://cdn.example.com/a%20b.png")]
    public void A_relative_image_is_resolved_against_the_page(string content, string expected)
    {
        var preview = Extract(Page($"""<meta property="og:image" content="{content}">"""));

        Assert.NotNull(preview);
        Assert.Equal(expected, preview.ImageUrl);
    }

    [Theory]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://cdn.example.com/cover.png")]
    [InlineData("")]
    public void An_image_that_is_not_an_http_url_is_dropped(string content)
    {
        var preview = Extract(Page($"""
                                    <meta property="og:title" content="Title">
                                    <meta property="og:image" content="{content}">
                                    """));

        Assert.NotNull(preview);
        Assert.Equal("Title", preview.Title);
        Assert.Null(preview.ImageUrl);
    }

    [Fact]
    public void An_image_url_too_long_to_store_is_dropped()
    {
        var url = "https://cdn.example.com/" + new string('a', LinkPreview.MaxImageUrlLength);

        var preview = Extract(Page($"""
                                    <meta property="og:title" content="Title">
                                    <meta property="og:image" content="{url}">
                                    """));

        Assert.NotNull(preview);
        Assert.Null(preview.ImageUrl);
    }

    [Fact]
    public void Entities_are_decoded_and_whitespace_collapsed()
    {
        var preview = Extract(Page("""
                                   <meta property="og:title" content="  Tom &amp; Jerry&#39;s
                                        big   day  ">
                                   <title>Ignored</title>
                                   <meta name="description" content="Caf&eacute;&nbsp;&quot;tabs&quot;	here">
                                   """));

        Assert.NotNull(preview);
        Assert.Equal("Tom & Jerry's big day", preview.Title);
        Assert.Equal("Café \"tabs\" here", preview.Description);
    }

    [Fact]
    public void Long_text_is_cut_to_what_is_stored()
    {
        var title = new string('t', LinkPreview.MaxTitleLength + 50);
        var description = new string('d', LinkPreview.MaxDescriptionLength + 50);

        var preview = Extract(Page($"""
                                    <meta property="og:title" content="{title}">
                                    <meta property="og:description" content="{description}">
                                    """));

        Assert.NotNull(preview);
        Assert.Equal(LinkPreview.MaxTitleLength, preview.Title!.Length);
        Assert.Equal(LinkPreview.MaxDescriptionLength, preview.Description!.Length);
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        var cleaned = LinkPreviewExtractor.Clean(new string('a', 9) + "\U0001F600" + "tail", 10);

        Assert.Equal(new string('a', 9), cleaned);
    }

    [Fact]
    public void Attributes_may_come_in_any_order_quoting_and_case()
    {
        var preview = Extract(Page("""
                                   <META CONTENT='Single quoted' PROPERTY="OG:TITLE">
                                   <meta content=Bare name=description />
                                   <meta property="og:image" content="https://cdn.example.com/a.png"/>
                                   """));

        Assert.NotNull(preview);
        Assert.Equal("Single quoted", preview.Title);
        Assert.Equal("Bare", preview.Description);
        Assert.Equal("https://cdn.example.com/a.png", preview.ImageUrl);
    }

    [Fact]
    public void A_quoted_value_may_contain_a_closing_bracket()
    {
        var preview = Extract(Page("""<meta property="og:title" content="a > b">"""));

        Assert.NotNull(preview);
        Assert.Equal("a > b", preview.Title);
    }

    [Fact]
    public void Tags_inside_comments_scripts_and_styles_do_not_count()
    {
        var preview = Extract(Page("""
                                   <!-- <meta property="og:title" content="Commented out"> -->
                                   <script type="text/javascript">
                                       var html = '<meta property="og:title" content="Scripted">';
                                   </script>
                                   <style>/* <meta property="og:description" content="Styled"> */</style>
                                   <meta property="og:title" content="Real">
                                   """));

        Assert.NotNull(preview);
        Assert.Equal("Real", preview.Title);
        Assert.Null(preview.Description);
    }

    [Fact]
    public void Tags_in_the_body_do_not_count_once_the_head_has_ended()
    {
        var preview = Extract(Page(string.Empty, """<meta property="og:title" content="In the body">"""));

        Assert.Null(preview);
    }

    [Fact]
    public void A_page_without_a_head_is_read_in_full()
    {
        var preview = Extract("""<meta property="og:title" content="No head at all">""");

        Assert.NotNull(preview);
        Assert.Equal("No head at all", preview.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<title></title>")]
    [InlineData("<title>   </title>")]
    [InlineData("""<meta property="og:title" content="">""")]
    [InlineData("""<meta property="og:title">""")]
    [InlineData("""<meta name="keywords" content="a, b">""")]
    [InlineData("""<meta property="og:image" content="data:image/png;base64,AAAA">""")]
    public void A_page_with_nothing_to_show_gives_no_preview(string head)
    {
        Assert.Null(Extract(Page(head)));
    }

    [Fact]
    public void Control_characters_are_dropped()
    {
        var preview = Extract(Page("<meta property=\"og:title\" content=\"Tit\u0000le\u0007\">"));

        Assert.NotNull(preview);
        Assert.Equal("Title", preview.Title);
    }
}
